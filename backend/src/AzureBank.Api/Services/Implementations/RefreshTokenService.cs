using System.Security.Cryptography;
using System.Text;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Enums;
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
    // theft signal. The 10-second duration is purely LOCAL APPLICATION POLICY — an
    // availability/security trade-off that bounds the theft-tolerance window. (RFC 9700 defines
    // rotation + reuse-detection but does NOT define a grace window.) A client that loses the
    // rotation response and receives a 401 recovers by re-authenticating.
    private static readonly TimeSpan RotationGraceWindow = TimeSpan.FromSeconds(10);

    private readonly AzureBankDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<RefreshTokenService> _logger;
    private readonly IAuditService _audit;

    public RefreshTokenService(
        AzureBankDbContext context,
        IHttpContextAccessor httpContextAccessor,
        IOptions<JwtOptions> jwtOptions,
        ILogger<RefreshTokenService> logger,
        IAuditService audit)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
        _audit = audit;
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
                "SecurityEvent {SecurityEvent}: refresh presented an unknown token", SecurityEvents.RefreshTokenUnknown);

            /*
              RecordRefusalAsync, not Record: this path throws, so anything enlisted in the caller's
              unit of work would be rolled back with the 401 — the refusal would erase its own
              record. No actor: the whole point is that nobody could be identified (ADR-0044).
            */
            await _audit.RecordRefusalAsync(
                SecurityEvents.RefreshTokenUnknown, AuditOutcome.Refused, cancellationToken: cancellationToken);
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
                    SecurityEvents.RefreshTokenReuse, existing.Id, existing.UserId);

                /*
                  CONTAIN FIRST, RECORD SECOND, and the order is not cosmetic — it was the other way
                  round when the audit row was first wired in, which quietly made this path
                  fail-open. RecordRefusalAsync is deliberately allowed to throw (ADR-0044: a
                  swallowed audit failure is the silent gap this work exists to close) and it runs on
                  its own connection, so a command timeout or an unwritable audit table raises from
                  it. Placed ahead of the try below, that exception escaped RotateAsync BEFORE the
                  family revoke ran: a token confirmed STOLEN kept its whole family alive, the catch
                  never ran so no MitigationFailed row was written either, and the caller got a 500 —
                  the exact outcome the comment inside that catch calls out as inviting a retry of
                  the stolen token.

                  Containment is the urgent half and must not be reachable only when logging works.
                  Recording afterwards keeps the loud-failure posture: if THAT write fails the
                  exception still surfaces, but by then the family is already dead, so the 5xx no
                  longer hands the attacker a usable retry.
                */
                try
                {
                    await RevokeAllForUserAsync(existing.UserId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The 401 is the CONTRACT; the family revoke is a MITIGATION. Letting the
                    // mitigation's failure decide the status code broke both halves at once: the
                    // caller got a 500 instead of the uniform rejection this endpoint promises
                    // everywhere else (see the concurrency-loss branch below, which returns 401
                    // precisely so a race cannot be told apart from a rejection), and a 5xx invites
                    // the caller to RETRY the very token that was just detected as stolen.
                    //
                    // The set-based revoke is the one unguarded database write on this path, and it
                    // runs while concurrent rotations are touching the same index, so a deadlock
                    // victim or a command timeout lands exactly here. Found by reading the path,
                    // not by catching it in the act: it has never been observed failing in CI, and
                    // it did not reproduce locally under deliberate contention. The guard is
                    // therefore about the reachable failure mode, not about a logged incident.
                    //
                    // Swallowed rather than surfaced because surfacing it never helped: the 500 did
                    // not revoke anything either, so the exposure of a failed revoke is IDENTICAL
                    // before and after this guard. Only the status code changes.
                    //
                    // Do not read the swallow as "harmless". A failed revoke leaves the stolen
                    // family active, and it is NOT always self-healing: if the attacker rotated
                    // first, the legitimate client is the one that gets the 401, so there may be no
                    // further replay to re-run the revoke, and the attacker's successor survives
                    // until logout or the 7-day expiry. Bounded, but real — tracked as a residual
                    // in ADR-0021. That is why this logs at Error with a SecurityEvent marker: a
                    // failed revoke must be loud in the sink even though it is quiet on the wire.
                    //
                    // Cancellation still propagates — a disconnected caller is not a failed
                    // mitigation.
                    _logger.LogError(
                        ex,
                        "SecurityEvent {SecurityEvent}: family revoke FAILED after reuse detection for user {UserId}; "
                            + "the 401 stands and the next replay re-runs the revoke",
                        SecurityEvents.RefreshTokenReuseRevokeFailed, existing.UserId);

                    /*
                      MitigationFailed, the only outcome of its kind: a compromise was detected and
                      NOT contained. Written on its own connection precisely because everything
                      around it is failing — if this one is lost, the single case where a human has
                      to act leaves no trace at all.
                    */
                    await _audit.RecordRefusalAsync(
                        SecurityEvents.RefreshTokenReuseRevokeFailed, AuditOutcome.MitigationFailed,
                        actorUserId: existing.UserId, subjectType: "RefreshToken", subjectId: existing.Id,
                        cancellationToken: cancellationToken);
                }

                // The theft signal of ADR-0021, and the one event here an operator is most likely
                // to be woken by. Out-of-band because this path ends in a 401 whose rollback would
                // otherwise take the record with it; AFTER the containment above, for the reason
                // spelled out where that try begins.
                await _audit.RecordRefusalAsync(
                    SecurityEvents.RefreshTokenReuse, AuditOutcome.Refused,
                    actorUserId: existing.UserId, subjectType: "RefreshToken", subjectId: existing.Id,
                    cancellationToken: cancellationToken);
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
        // Loop until a pass revokes nothing. A rotation racing this revoke can commit a successor
        // that a single bulk UPDATE misses (a phantom row under READ COMMITTED); the next pass
        // catches it. This TERMINATES: a successor can only be minted by rotating an ACTIVE parent,
        // and once every active row is revoked the parent's rowversion-guarded rotation UPDATE
        // fails — so no new successor can appear. Capped as a safety net against a pathological
        // sustained race (a straggler then falls to the next reuse-replay / logout / expiry).
        const int maxPasses = 5;
        var now = DateTime.UtcNow;

        for (var pass = 0; pass < maxPasses; pass++)
        {
            int revoked;
            if (_context.Database.IsRelational())
            {
                // Set-based revoke over IX_RefreshTokens_UserId_Active: one round-trip, nothing tracked.
                revoked = await _context.RefreshTokens
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
                revoked = active.Count;
            }

            if (revoked == 0)
            {
                return;
            }
        }

        _logger.LogWarning(
            "RevokeAllForUserAsync hit the {MaxPasses}-pass cap for user {UserId}; a straggler token may survive until expiry",
            maxPasses, userId);
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
