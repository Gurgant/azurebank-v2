using AzureBank.Bff.Models;
using AzureBank.Shared.DTOs.Auth;

namespace AzureBank.Bff.Services.Interfaces;

/// <summary>
/// Service for managing user sessions in the BFF layer.
/// Sessions store JWT tokens server-side, providing security by keeping tokens out of the browser.
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Creates a new session and stores the JWT token with user info.
    /// </summary>
    /// <param name="accessToken">The JWT access token to store</param>
    /// <param name="tokenExpiry">Token expiration time</param>
    /// <param name="refreshToken">The session's grant, for renewals (null if none was issued)</param>
    /// <param name="refreshTokenExpiresAt">
    /// When the grant expires, as the API answered it; the session ends no later (06 §4.1, F5).
    /// Null when there is no grant.
    /// </param>
    /// <param name="userInfo">User information to cache in session</param>
    /// <returns>A secure session ID to be stored in a cookie</returns>
    string CreateSession(
        string accessToken, DateTime tokenExpiry, string? refreshToken, DateTime? refreshTokenExpiresAt,
        UserLoginInfo userInfo);

    /// <summary>
    /// Gets the full session data for a session ID.
    /// Returns null if session is invalid or expired.
    /// </summary>
    UserSession? GetSession(string sessionId);

    /// <summary>
    /// Attempts to retrieve just the JWT token for a session.
    /// Used by YARP transform to add Authorization header.
    /// </summary>
    bool TryGetToken(string sessionId, out string? token);

    /// <summary>
    /// Updates the last activity timestamp for a session.
    /// Called on every authenticated request.
    /// </summary>
    void UpdateActivity(string sessionId);

    /// <summary>
    /// Validates if a session is still active.
    /// </summary>
    bool ValidateSession(string sessionId);

    /// <summary>
    /// Ends a session: marks it ended and removes it, then queues its grant for revocation at the API,
    /// after any renewal in flight (06 §4.6). Only this session; the user's others are untouched.
    /// Ending an unknown or already-ended session does nothing.
    /// </summary>
    void EndSession(string sessionId);

    /// <summary>
    /// Upgrades session to PIN-verified level (AuthLevel 2).
    /// </summary>
    void SetPinVerified(string sessionId);

    /// <summary>
    /// Gets the current auth level for a session.
    /// Automatically downgrades from level 2 if PIN verification expired.
    /// </summary>
    int GetAuthLevel(string sessionId);

    /// <summary>
    /// Checks if PIN verification is still valid for a session.
    /// </summary>
    bool IsPinVerificationValid(string sessionId);

    /// <summary>
    /// Updates cached user info (e.g., when HasPin changes after setting PIN).
    /// </summary>
    void UpdateUserInfo(string sessionId, Action<UserSessionInfo> update);

    /// <summary>
    /// Stores a renewed access token on <paramref name="session"/>, and only if it expires later than
    /// the one held. The grant is never touched: it does not rotate (06 §4.5). Call it holding the
    /// session's <see cref="UserSession.SyncRoot"/>, as the renewal does.
    /// </summary>
    /// <returns>
    /// False when nothing was stored: the session has ended, or the new token expires no later.
    /// </returns>
    bool RefreshSession(UserSession session, string newToken, DateTime expiresAt);
}
