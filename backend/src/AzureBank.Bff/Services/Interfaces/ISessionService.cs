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
    /// <param name="refreshToken">The refresh token for silent re-mint (null if none was issued)</param>
    /// <param name="userInfo">User information to cache in session</param>
    /// <returns>A secure session ID to be stored in a cookie</returns>
    string CreateSession(string accessToken, DateTime tokenExpiry, string? refreshToken, UserLoginInfo userInfo);

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
    /// Revokes a session, removing all stored data.
    /// </summary>
    void RevokeSession(string sessionId);

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
    /// Refreshes a session with a re-minted access token AND its rotated refresh token
    /// (rotation issues a new refresh token on every use — store the successor).
    /// </summary>
    void RefreshSession(string sessionId, string newToken, DateTime expiresAt, string? newRefreshToken);
}
