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
/// The grant: one reusable refresh token per BFF session, which does not rotate (ADR-0057 §3, §4).
///
/// - Grants are 256 bits of CSPRNG entropy, stored ONLY as a SHA-256 hash — a database leak
///   yields useless hashes, never a usable grant.
/// - A grant lives <c>Jwt:RefreshTokenLifetimeMinutes</c> from issue (60 by default), fixed then
///   and never extended, and every access token minted from it is capped at that instant.
/// - A renewal READS the grant and writes nothing. That is the whole fix for ADR-0057 §1: a renewal
///   whose answer is lost, whose commit EF retries as if it had rolled back, or which a hung
///   database holds, has nothing to lose, so the BFF can simply send it again with the same grant.
/// - Revocation is per session (<see cref="RevokeAsync"/>, reason SessionEnded) or per user
///   (<see cref="RevokeAllForUserAsync"/>, which also raises the user's session stamp in the same
///   transaction, ADR-0057 §5.3). A grant whose session ENDED, presented in a request the
///   API received after that revoke, is the tripwire: it is logged and audited, and it revokes
///   nothing (ADR-0057 F3).
/// - Every accepted renewal is counted in memory by <see cref="RefreshRenewalRateDetector"/>, which
///   logs <c>RefreshRenewalRateHigh</c> when one grant renews more than three times in one
///   access-token lifetime (ADR-0057 §6, anomaly 2). It refuses nothing and writes nothing.
///
/// <i>Until PR-1 this rotated the token on every renewal and treated a revoked token presented again
/// as theft, revoking every token of the user. Measured (ADR-0057 §1): a renewal that met a 10 s
/// database hang signed someone out in 8 runs of 8, and in 4 of them signed out every session of the
/// user and wrote a false reuse event. FAPI 2.0 §5.3.2.1 item 9 reads "shall not use refresh token
/// rotation except in extraordinary
/// circumstances"; ADR-0057 §3 argues why that holds here without FAPI's client authentication:
/// presenting a grant takes the grant, the service key and a socket on the API's loopback
/// interface.</i>
/// </summary>
public class RefreshTokenService : IRefreshTokenService
{
    /// <summary>The <c>Retry-After</c> a failed revoke answers with.</summary>
    private const int RevokeRetryAfterSeconds = 5;

    /// <summary>
    /// Less life than this left on a grant and a renewal refuses it as expired: an access token
    /// capped at the grant, in whole seconds, would already be dead.
    /// </summary>
    private static readonly TimeSpan MinimumRemainingLife = TimeSpan.FromSeconds(1);

    private readonly AzureBankDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<RefreshTokenService> _logger;
    private readonly IAuditService _audit;
    private readonly RefreshRenewalRateDetector _renewalRate;

    public RefreshTokenService(
        AzureBankDbContext context,
        IHttpContextAccessor httpContextAccessor,
        IOptions<JwtOptions> jwtOptions,
        ILogger<RefreshTokenService> logger,
        IAuditService audit,
        RefreshRenewalRateDetector renewalRate)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
        _audit = audit;
        _renewalRate = renewalRate;
    }

    /// <inheritdoc />
    public async Task<IssuedGrant> IssueAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        var plaintext = GenerateToken();
        var token = BuildToken(user.Id, plaintext);

        _context.RefreshTokens.Add(token);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Issued refresh token {TokenId} for user {UserId}, expires at {ExpiresAt}",
            token.Id, user.Id, token.ExpiresAt);
        return new IssuedGrant(plaintext, token.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<RenewResult> RenewAsync(
        string presentedToken, DateTime receivedAt, CancellationToken cancellationToken = default)
    {
        var hash = ComputeHash(presentedToken);

        // The User is needed to mint the access token. AsNoTracking: nothing on this path saves,
        // and a tracked entity would only invite a later SaveChanges in this scope to write it.
        var grant = await _context.RefreshTokens
            .AsNoTracking()
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (grant is null)
        {
            // Never existed, or already reaped by cleanup. Uniform 401 (no oracle).
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: refresh presented an unknown token", SecurityEvents.RefreshTokenUnknown);

            /*
              RecordRefusalAsync, not Record: this path throws, so anything enlisted in the caller's
              unit of work would be rolled back with the 401 — the refusal would erase its own
              record. No actor: the whole point is that nobody could be identified (ADR-0044).
              CancellationToken.None, as for the tripwire below: the row is the evidence, and a
              caller that hangs up must not be able to take it back.
            */
            await _audit.RecordRefusalAsync(
                SecurityEvents.RefreshTokenUnknown, AuditOutcome.Refused, cancellationToken: CancellationToken.None);
            throw InvalidRefreshToken();
        }

        // Revoked BEFORE expired, as it always was: an ended session's grant presented after its
        // expiry is still the tripwire, which an expiry check first would hide as a routine refusal.
        if (grant.RevokedAt is { } revokedAt)
        {
            await RefuseRevokedAsync(grant, revokedAt, receivedAt);
            throw InvalidRefreshToken();
        }

        /*
          Expired, or ending within the second. The access token minted from it is capped at the
          grant's expiry, and its exp claim is whole seconds, truncated: with half a second left, exp
          would fall on the second already begun, and the answer would be a 200 carrying a token that
          had expired before it was sent. With at least a second left, the truncated exp is still
          after now. The grant is at its end either way, so the refusal costs its session at most
          that second.
        */
        if (grant.ExpiresAt - DateTime.UtcNow < MinimumRemainingLife)
        {
            _logger.LogInformation(
                "Refresh token {TokenId} (user {UserId}) is expired", grant.Id, grant.UserId);
            throw InvalidRefreshToken();
        }

        // ADR-0057 §6, anomaly 2: count this accepted renewal in memory. It refuses nothing and
        // writes nothing; more than three in one access-token lifetime raises
        // RefreshRenewalRateHigh.
        _renewalRate.Record(grant.Id, grant.UserId, receivedAt);

        // Non-null: loaded via Include, and the non-nullable UserId FK (Cascade) admits no orphan.
        return new RenewResult(grant.User!, grant.ExpiresAt);
    }

    /// <summary>
    /// The revoked rows of ADR-0057 §4.3's table. Every one ends in the same 401; they differ only
    /// in what is written about it.
    /// </summary>
    private async Task RefuseRevokedAsync(RefreshToken grant, DateTime revokedAt, DateTime receivedAt)
    {
        switch (grant.RevokedReason)
        {
            // THE TRIPWIRE. The BFF revoked this grant because it ended the session, and it held the
            // grant nowhere else, so a request that ARRIVED after that revoke did not come from a
            // live session. Both instants are stamps from ReceivedAtClock, so a wall-clock step
            // between them cannot reorder them (F10).
            case RefreshTokenRevokedReason.SessionEnded when receivedAt > revokedAt:
                // The log line BEFORE the audit write, as it always was, so it is written even when
                // the audit write then fails.
                _logger.LogWarning(
                    "SecurityEvent {SecurityEvent}: refresh token {TokenId} (user {UserId}) presented after its session ended; refused, nothing revoked",
                    SecurityEvents.RefreshTokenReuse, grant.Id, grant.UserId);

                /*
                  RECORD, DO NOT REVOKE (ADR-0057 F3). Until PR-1 this branch revoked every token of
                  the user. Only code inside the replica can present a grant (ADR-0057 §3), and
                  revoking one user's tokens does not contain that code; the incident runbook does.
                  What the containment did do was sign out every session of a user whenever an
                  innocent path got here — a lost answer, a stall, "Esci" on another device.

                  CancellationToken.None: the row is the evidence, and a caller that hangs up must
                  not be able to take it back. If the write FAILS, the exception surfaces, as the
                  unknown-grant refusal's does (ADR-0044's loud failure), and the exception handlers
                  answer it: when the database could not be reached or did not answer in time (EF's
                  retries spent, a connection-level error, a command timeout), with the outage 503
                  SERVICE_UNAVAILABLE and Retry-After (ServiceUnavailableExceptionHandler,
                  ADR-0058); when the database refused the write, with the 500. The BFF keeps the
                  session on either (ADR-0057 §4.5). Nothing is revoked or issued either way, and
                  the grant is already revoked, so a retry can only try the audit write again.
                */
                await _audit.RecordRefusalAsync(
                    SecurityEvents.RefreshTokenReuse, AuditOutcome.Refused,
                    actorUserId: grant.UserId, subjectType: "RefreshToken", subjectId: grant.Id,
                    cancellationToken: CancellationToken.None);
                return;

            // Received before the revoke and read after it: the renewal was in flight when the
            // session ended. The BFF drops its answer anyway.
            case RefreshTokenRevokedReason.SessionEnded:
                _logger.LogInformation(
                    "Refresh token {TokenId} (user {UserId}) was revoked while its renewal was in flight; refused",
                    grant.Id, grant.UserId);
                return;

            // The two reasons an operator writes during an incident. A Warning, so a session that
            // keeps knocking afterwards is visible, but no event: a session that was running when
            // the lever was pulled learns of it here, innocently.
            case RefreshTokenRevokedReason.Incident or RefreshTokenRevokedReason.ReuseContainment:
                _logger.LogWarning(
                    "Refresh token {TokenId} (user {UserId}) is revoked ({Reason}); refused",
                    grant.Id, grant.UserId, grant.RevokedReason.ToString());
                return;

            // SignOutEverywhere, Deployment, or a legacy row with no reason (rotated away or signed
            // out before the column existed).
            default:
                _logger.LogInformation(
                    "Refresh token {TokenId} (user {UserId}) is revoked ({Reason}); refused",
                    grant.Id, grant.UserId, grant.RevokedReason?.ToString() ?? "legacy");
                return;
        }
    }

    /// <inheritdoc />
    public async Task<int> RevokeAsync(
        IReadOnlyCollection<string> presentedTokens, DateTime receivedAt, CancellationToken cancellationToken = default)
    {
        // A null or empty entry can name no grant; skip it rather than hash it. Distinct, because a
        // drain may list one grant twice and the IN list needs it once.
        var hashes = presentedTokens
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(ComputeHash)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hashes.Count == 0)
        {
            return 0;
        }

        int revoked;
        try
        {
            revoked = await RevokeWhereAsync(
                _context.RefreshTokens.Where(t => hashes.Contains(t.TokenHash) && t.RevokedAt == null),
                RefreshTokenRevokedReason.SessionEnded,
                receivedAt,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /*
              503, NOT 500 (ADR-0057 §4.4, F12; RFC 7009 §2.2.1). The only work here is one
              idempotent UPDATE, so a failure is the database's, and the answer that helps is
              "try again": the BFF keeps the grant queued and retries with backoff until the grant
              expires. A 500 would read as a verdict on the request. Error, with the exception, so a
              failure that is not the database's is still loud here.
            */
            _logger.LogError(ex, "Revoking {Count} grants failed; answering 503", hashes.Count);
            throw new ServiceUnavailableException(
                "The revocation could not be recorded now. Retry it.", RevokeRetryAfterSeconds);
        }

        _logger.LogInformation(
            "Revoked {Count} of {Attempted} presented grants (SessionEnded)", revoked, hashes.Count);
        return revoked;
    }

    /// <inheritdoc />
    public async Task<int> RevokeAllForUserAsync(
        Guid userId,
        RefreshTokenRevokedReason reason,
        DateTime revokedAt,
        CancellationToken cancellationToken = default)
    {
        // One pass. While renewal rotated, a rotation racing this revoke could commit a successor
        // the UPDATE missed, so this looped until a pass revoked nothing. A renewal writes nothing
        // now, so nothing can appear behind the UPDATE except a NEW sign-in's grant — a session the
        // user opened after asking to be signed out, which is theirs to keep.
        var now = DateTime.UtcNow;
        var grants = _context.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now);

        int revoked;
        if (_context.Database.IsRelational())
        {
            /*
              THE REVOKE AND THE STAMP COMMIT TOGETHER (ADR-0057 §5.3). The stamp is what ends the
              user's BFF sessions within one 15-second poll; the revoke is what ends them at their
              next renewal if the BFF never reads the stamp. One without the other would leave a
              lever that looks pulled and is half pulled.

              Through the execution strategy, because production retries (EnableRetryOnFailure) and
              EF refuses a user-initiated transaction under a retrying strategy otherwise. A retry
              after a commit whose outcome was lost revokes nothing more (RevokedAt IS NULL) and adds
              1 again. The stamp only has to rise: one higher still ends every session the user held
              before the sign-out, and at worst also one that signed in between the two commits.

              THE RAISE ALSO ROTATES ConcurrencyStamp, or a whole-row write can lower the stamp
              again. Identity's UpdateAsync writes every column of a user it loaded earlier, and
              checks only ConcurrencyStamp: SetPinAsync loads the user, spends the hash time, then
              writes the row back with the stamp it read. Measured 2026-09-28 on LocalDB: a raise
              between that load and that write went from 1 back to 0, and the write succeeded.
              With the rotation the write fails as ConcurrencyFailure and the stamp stays 1.
            */
            revoked = await _context.Database.CreateExecutionStrategy().ExecuteAsync(
                async token =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(token);
                    var count = await RevokeWhereAsync(grants, reason, revokedAt, token);
                    await _context.Users
                        .Where(u => u.Id == userId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(u => u.SessionStamp, u => u.SessionStamp + 1)
                            .SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid().ToString()), token);
                    await transaction.CommitAsync(token);
                    return count;
                },
                cancellationToken);
        }
        else
        {
            // The EF InMemory test host has neither ExecuteUpdate nor transactions: both changes go
            // in one SaveChanges.
            var active = await grants.ToListAsync(cancellationToken);
            foreach (var token in active)
            {
                token.RevokedAt = revokedAt;
                token.RevokedReason = reason;
            }

            if (await _context.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken) is { } user)
            {
                user.SessionStamp++;
                user.ConcurrencyStamp = Guid.NewGuid().ToString();
            }

            await _context.SaveChangesAsync(cancellationToken);
            revoked = active.Count;
        }

        _logger.LogInformation(
            "Revoked {Count} grants of user {UserId} ({Reason}) and raised the user's session stamp",
            revoked, userId, reason.ToString());
        return revoked;
    }

    /// <summary>
    /// Sets <c>RevokedAt</c> and <c>RevokedReason</c> on every row of <paramref name="rows"/>, in one
    /// set-based UPDATE on a relational provider.
    /// </summary>
    private async Task<int> RevokeWhereAsync(
        IQueryable<RefreshToken> rows,
        RefreshTokenRevokedReason reason,
        DateTime revokedAt,
        CancellationToken cancellationToken)
    {
        if (_context.Database.IsRelational())
        {
            // One round-trip, nothing tracked. Idempotent, so the retrying strategy may re-run it.
            return await rows.ExecuteUpdateAsync(
                s => s
                    .SetProperty(t => t.RevokedAt, (DateTime?)revokedAt)
                    .SetProperty(t => t.RevokedReason, (RefreshTokenRevokedReason?)reason),
                cancellationToken);
        }

        // ExecuteUpdate is relational-only; the EF InMemory test host loads + mutates + saves.
        var active = await rows.ToListAsync(cancellationToken);
        foreach (var token in active)
        {
            token.RevokedAt = revokedAt;
            token.RevokedReason = reason;
        }
        await _context.SaveChangesAsync(cancellationToken);
        return active.Count;
    }

    // ─────────────────────────────────────────────────────────────────────────────

    private RefreshToken BuildToken(Guid userId, string plaintext)
    {
        var (ip, userAgent) = ReadClientContext();
        var now = DateTime.UtcNow;

        return new RefreshToken
        {
            // Set the key explicitly (UUIDv7, matching the value generator) so the issue log line
            // can name the row before SaveChanges assigns database values.
            Id = Guid.CreateVersion7(),
            UserId = userId,
            // Deliberately NOT setting the User navigation: DbContext.Add cascades an INSERT to
            // any non-null reachable principal, and a caller may hand us a DETACHED user (the
            // login path detaches it after the atomic lockout reset). Setting only the FK makes
            // issuance safe regardless of the principal's tracking state.
            TokenHash = ComputeHash(plaintext),
            CreatedAt = now,                                       // not a BaseEntity → set here
            // Fixed here and never extended: nothing from one sign-in outlives this
            // (ADR-0057 §4.1).
            ExpiresAt = now.AddMinutes(_jwtOptions.RefreshTokenLifetimeMinutes),
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
    /// Best-effort caller fingerprint for forensics. Never a security boundary (a NAT or proxy hop
    /// changes the IP legitimately) — recorded, not enforced. Truncated to the column widths so an
    /// oversized User-Agent can never overflow the write.
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
