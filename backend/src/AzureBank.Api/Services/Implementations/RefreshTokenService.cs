using System.Security.Cryptography;
using System.Text;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Refresh-token rotation with reuse-detection (RFC 9700 §4.14.2 / OWASP OAuth2 Cheat Sheet).
///
/// - Tokens are 256 bits of CSPRNG entropy, stored ONLY as a SHA-256 hash — a database leak
///   yields useless hashes, never a usable token.
/// - Rotate-on-use: every refresh revokes the presented token and issues a chained successor
///   (ReplacedByTokenId), so a stolen token can be used at most once before divergence.
/// - Reuse-detection: replaying an already-revoked token is the signature of theft (attacker
///   and client both hold a copy); the response is to revoke the user's ENTIRE active token
///   set and force a fresh login. Matches the entity's documented "revoke ALL user tokens".
///
/// Concurrency: rotation is guarded by an optimistic-concurrency rowversion on the presented
/// token, so two concurrent rotations of the SAME token cannot both commit (the loser gets a
/// benign 401) — the chain is un-forkable regardless of caller, so reuse-detection cannot be
/// silently bypassed. A just-rotated token replayed within a short grace window is treated as
/// a benign lost-response retry, not theft.
/// </summary>
public class RefreshTokenService : IRefreshTokenService
{
    // A just-rotated token replayed within this window is a benign lost-response retry, not a
    // theft signal. RFC 9700 endorses the grace-window concept; the 10-second duration is our
    // application policy (kept short to bound the theft-tolerance window). A client that loses
    // the rotation response and receives a 401 recovers by re-authenticating.
    private static readonly TimeSpan RotationGraceWindow = TimeSpan.FromSeconds(10);

    private readonly AzureBankDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(
        AzureBankDbContext context,
        IHttpContextAccessor httpContextAccessor,
        IOptions<JwtOptions> jwtOptions,
        ILogger<RefreshTokenService> logger)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> IssueAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        var plaintext = GenerateToken();
        var token = BuildToken(user.Id, plaintext);

        _context.RefreshTokens.Add(token);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Issued refresh token {TokenId} for user {UserId}", token.Id, user.Id);
        return plaintext;
    }

    /// <inheritdoc />
    public async Task<RefreshRotationResult> RotateAsync(
        string presentedToken, CancellationToken cancellationToken = default)
    {
        var hash = ComputeHash(presentedToken);

        // The User is needed to mint a fresh access token AND to build the successor row.
        var existing = await _context.RefreshTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (existing is null)
        {
            // Never existed, or already reaped by cleanup. Uniform 401 (no oracle).
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: refresh presented an unknown token", "RefreshTokenUnknown");
            throw InvalidRefreshToken();
        }

        if (existing.IsRevoked)
        {
            // A token that was already ROTATED (has a successor) and revoked within the grace
            // window is a benign lost-response retry — the client re-sent before it saw the new
            // pair — NOT theft, so reject it WITHOUT revoking the family. Outside the window, or
            // a token revoked WITHOUT a successor (explicit logout / theft response), is genuine
            // REUSE: an already-invalidated token replayed is the hallmark of a stolen token
            // used alongside the legitimate client → revoke the user's ENTIRE active set so
            // neither party can continue (RFC 9700 §4.14.2).
            var rotatedWithinGrace = existing.ReplacedByTokenId is not null
                && existing.RevokedAt is { } revokedAt
                && DateTime.UtcNow - revokedAt <= RotationGraceWindow;

            if (rotatedWithinGrace)
            {
                _logger.LogInformation(
                    "Refresh token {TokenId} (user {UserId}) replayed within the rotation grace window; benign retry",
                    existing.Id, existing.UserId);
            }
            else
            {
                _logger.LogWarning(
                    "SecurityEvent {SecurityEvent}: reuse of revoked refresh token {TokenId} (user {UserId}); revoking all active tokens",
                    "RefreshTokenReuse", existing.Id, existing.UserId);
                await RevokeAllForUserAsync(existing.UserId, cancellationToken);
            }
            throw InvalidRefreshToken();
        }

        if (existing.IsExpired)
        {
            _logger.LogInformation(
                "Refresh token {TokenId} (user {UserId}) is expired", existing.Id, existing.UserId);
            throw InvalidRefreshToken();
        }

        // Happy path: rotate. Mint a successor, revoke the presented token, and chain them so a
        // later replay of THIS token is caught by the reuse branch above.
        var newPlaintext = GenerateToken();
        var successor = BuildToken(existing.UserId, newPlaintext);
        _context.RefreshTokens.Add(successor);

        existing.RevokedAt = DateTime.UtcNow;
        existing.ReplacedByTokenId = successor.Id;

        try
        {
            // The presented row carries a rowversion, so a concurrent rotation of the SAME
            // token makes this UPDATE match zero rows. EF then rolls back the whole unit
            // (successor INSERT + this UPDATE) — no fork, no orphan.
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // EF does not auto-revert entity states after a concurrency failure: the successor
            // (Added) and the presented row (Modified) stay tracked. Detach both so a later
            // SaveChanges in this request scope can't retry the losing INSERT/UPDATE (matches the
            // detach-after-write pattern in AuthService.ResetLoginLockoutAsync).
            _context.Entry(successor).State = EntityState.Detached;
            _context.Entry(existing).State = EntityState.Detached;

            // Lost a concurrent rotation of this exact token (another request rotated it first).
            // Benign race, NOT reuse of an already-revoked token — uniform 401, no family revoke.
            _logger.LogInformation(
                "Refresh token {TokenId} (user {UserId}) lost a concurrent rotation race; rejecting without family revocation",
                existing.Id, existing.UserId);
            throw InvalidRefreshToken();
        }

        _logger.LogInformation(
            "Rotated refresh token {OldId} -> {NewId} for user {UserId}",
            existing.Id, successor.Id, existing.UserId);

        // Non-null: loaded via Include, and the non-nullable UserId FK (Cascade) admits no orphan.
        return new RefreshRotationResult(existing.User!, newPlaintext);
    }

    /// <inheritdoc />
    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        if (_context.Database.IsRelational())
        {
            // Set-based revoke over IX_RefreshTokens_UserId_Active: one round-trip, nothing tracked.
            await _context.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
        }
        else
        {
            // ExecuteUpdate is relational-only; the EF InMemory test host loads + mutates + saves.
            var active = await _context.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
                .ToListAsync(cancellationToken);
            foreach (var token in active)
            {
                token.RevokedAt = now;
            }
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────

    private RefreshToken BuildToken(Guid userId, string plaintext)
    {
        var (ip, userAgent) = ReadClientContext();
        var now = DateTime.UtcNow;

        return new RefreshToken
        {
            // Set the key explicitly (UUIDv7, matching the value generator) so the rotation
            // chain can reference it before SaveChanges assigns database values.
            Id = Guid.CreateVersion7(),
            UserId = userId,
            // Deliberately NOT setting the User navigation: DbContext.Add cascades an INSERT to
            // any non-null reachable principal, and a caller may hand us a DETACHED user (the
            // login path detaches it after the atomic lockout reset). Setting only the FK makes
            // issuance safe regardless of the principal's tracking state.
            TokenHash = ComputeHash(plaintext),
            CreatedAt = now,                                       // not a BaseEntity → set here
            ExpiresAt = now.AddDays(_jwtOptions.RefreshTokenExpirationDays),
            IpAddress = ip,
            UserAgent = userAgent
        };
    }

    /// <summary>
    /// 256 bits of CSPRNG entropy, URL-safe Base64 (same scheme as the BFF session id). This
    /// is the ONLY moment the plaintext exists in the system; only its hash is persisted.
    /// </summary>
    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");

    /// <summary>SHA-256 → Base64 (44 chars, matching ValidationRules.TokenHashLength).</summary>
    private static string ComputeHash(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Best-effort caller fingerprint for theft forensics. Never a security boundary (a NAT
    /// or proxy hop changes the IP legitimately) — recorded, not enforced. Truncated to the
    /// column widths so an oversized User-Agent can never overflow the write.
    /// </summary>
    private (string Ip, string UserAgent) ReadClientContext()
    {
        var ctx = _httpContextAccessor.HttpContext;

        var ip = ctx?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (ip.Length > ValidationRules.IpAddressMaxLength)
        {
            ip = ip[..ValidationRules.IpAddressMaxLength];
        }

        var userAgent = ctx?.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(userAgent))
        {
            userAgent = "unknown";
        }
        else if (userAgent.Length > ValidationRules.UserAgentMaxLength)
        {
            userAgent = userAgent[..ValidationRules.UserAgentMaxLength];
        }

        return (ip, userAgent);
    }

    private static AuthenticationException InvalidRefreshToken() =>
        new("Invalid refresh token.", ErrorCodes.RefreshTokenInvalid);
}
