using AzureBank.Shared.Entities;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Owns the refresh-token lifecycle (RFC 9700 / OWASP): issue, rotate-on-use, and
/// reuse-detection. Tokens are stored ONLY as a SHA-256 hash; the plaintext is returned
/// to the caller exactly once (at issue/rotate) and never persisted.
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>
    /// Issues a fresh refresh token for the user, persists its hash, and returns the
    /// plaintext (shown once). Called on login and registration.
    /// </summary>
    Task<string> IssueAsync(ApplicationUser user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a presented refresh token and rotates it: the presented token is revoked
    /// and a successor is issued (chained via ReplacedByTokenId). Throws
    /// <see cref="Shared.Exceptions.AuthenticationException"/> (401) on an unknown, expired,
    /// or already-revoked token. Replaying an already-revoked token is treated as theft —
    /// the user's ENTIRE active token set is revoked before throwing (RFC 9700 §4.14.2).
    /// </summary>
    Task<RefreshRotationResult> RotateAsync(string presentedToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes every currently-active refresh token for the user (logout / theft response).
    /// </summary>
    Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
