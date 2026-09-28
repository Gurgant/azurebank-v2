using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Owns the grant: one reusable refresh token per BFF session (06 §3). Issue, renew (a read), and
/// revoke. Grants are stored ONLY as a SHA-256 hash; the plaintext is returned to the caller exactly
/// once, at issue, and never persisted.
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>
    /// Issues a grant for the user, persists its hash with an expiry fixed now
    /// (<c>Jwt:RefreshTokenLifetimeMinutes</c>), and returns the plaintext (shown once) with that
    /// expiry. Called on login and registration.
    /// </summary>
    Task<IssuedGrant> IssueAsync(ApplicationUser user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a presented grant and, when it is active, returns its user and expiry. WRITES NOTHING
    /// for an active grant; the only writes are the audit rows of two refusals, the unknown grant and
    /// the tripwire (06 §4.3). Throws
    /// <see cref="Shared.Exceptions.AuthenticationException"/> (the uniform 401) for an unknown,
    /// revoked or expired grant. A grant whose session ended, presented in a request received after
    /// that revoke, is the tripwire: logged, audited, and refused — and nothing is revoked. If that
    /// audit write fails, its exception surfaces instead of the 401.
    /// </summary>
    /// <param name="presentedToken">The grant's plaintext.</param>
    /// <param name="receivedAt">When the API received the request (<c>ReceivedAtClock</c>).</param>
    /// <param name="cancellationToken">Cancels the read only; the tripwire's audit write ignores it.</param>
    Task<RenewResult> RenewAsync(
        string presentedToken, DateTime receivedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the presented grants as <see cref="RefreshTokenRevokedReason.SessionEnded"/>, stamping
    /// <paramref name="receivedAt"/> as their <c>RevokedAt</c>: <c>UPDATE … WHERE TokenHash IN (…) AND
    /// RevokedAt IS NULL</c>. Unknown and already-revoked grants are left alone, so repeating the call
    /// changes nothing. Returns how many rows it revoked. A failed write surfaces as
    /// <see cref="Shared.Exceptions.ServiceUnavailableException"/>, so the caller retries (06 §4.4).
    /// </summary>
    Task<int> RevokeAsync(
        IReadOnlyCollection<string> presentedTokens, DateTime receivedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes every active grant of the user with <paramref name="reason"/>, stamping
    /// <paramref name="revokedAt"/>. Returns how many rows it revoked.
    /// </summary>
    Task<int> RevokeAllForUserAsync(
        Guid userId,
        RefreshTokenRevokedReason reason,
        DateTime revokedAt,
        CancellationToken cancellationToken = default);
}
