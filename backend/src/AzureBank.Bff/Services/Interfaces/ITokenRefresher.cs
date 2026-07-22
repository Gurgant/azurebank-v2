namespace AzureBank.Bff.Services.Interfaces;

/// <summary>
/// Silently re-mints a session's short-lived access token via the API's refresh-token
/// rotation (ADR-0021, PR-2). The browser never sees any token; the BFF holds them
/// server-side and refreshes them inline, on-demand, before proxying an API call.
/// </summary>
public interface ITokenRefresher
{
    /// <summary>
    /// Returns a usable access token for the session, re-minting it first when it is within
    /// the refresh skew window. Single-flight per session (concurrent callers share one
    /// refresh). Returns:
    /// - a fresh (or still-valid) access token on success;
    /// - <c>null</c> when the session is gone, when the refresh token is dead (a 401 from the
    ///   API — the session is then revoked), or when a tokenless session's access token has
    ///   expired;
    /// - the current (possibly stale) token on a transient failure (network/5xx) — the session
    ///   is NOT revoked, so a blip can't log the user out.
    /// </summary>
    Task<string?> GetFreshAccessTokenAsync(string sessionId, CancellationToken cancellationToken = default);
}
