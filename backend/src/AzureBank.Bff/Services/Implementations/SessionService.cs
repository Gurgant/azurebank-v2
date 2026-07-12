using System.Security.Cryptography;
using AzureBank.Bff.Models;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Services.Implementations;

/// <summary>
/// Session service for managing user sessions in the BFF layer.
/// Creates cryptographically secure session IDs and manages token storage.
/// </summary>
public class SessionService : ISessionService
{
    private readonly ITokenStoreService _tokenStore;
    private readonly SecurityOptions _securityOptions;
    private readonly ILogger<SessionService> _logger;

    public SessionService(
        ITokenStoreService tokenStore,
        IOptions<SecurityOptions> securityOptions,
        ILogger<SessionService> logger)
    {
        _tokenStore = tokenStore;
        _securityOptions = securityOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string CreateSession(string accessToken, DateTime tokenExpiry, UserLoginInfo userInfo)
    {
        var sessionId = GenerateSecureSessionId();
        var now = DateTime.UtcNow;

        var session = new UserSession
        {
            SessionId = sessionId,
            UserId = userInfo.Id,
            AccessToken = accessToken,
            TokenExpiry = tokenExpiry,
            SessionCreated = now,
            LastActivity = now,
            AuthLevel = 1, // Level 1 = authenticated via email/password
            PinVerifiedAt = null,
            UserInfo = new UserSessionInfo
            {
                Id = userInfo.Id,
                Email = userInfo.Email,
                FirstName = userInfo.FirstName,
                LastName = userInfo.LastName,
                AzureTag = userInfo.AzureTag,
                HasPin = userInfo.HasPin
            }
        };

        _tokenStore.StoreSessionAsync(session).GetAwaiter().GetResult();

        _logger.LogInformation("Session created for user {UserId}", userInfo.Id);
        return sessionId;
    }

    /// <inheritdoc />
    public UserSession? GetSession(string sessionId)
    {
        return _tokenStore.GetSessionAsync(sessionId).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public bool TryGetToken(string sessionId, out string? token)
    {
        var session = GetSession(sessionId);
        token = session?.AccessToken;
        return token != null;
    }

    /// <inheritdoc />
    public void UpdateActivity(string sessionId)
    {
        var session = GetSession(sessionId);
        if (session != null)
        {
            session.LastActivity = DateTime.UtcNow;
            _tokenStore.UpdateSessionAsync(session).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public bool ValidateSession(string sessionId)
    {
        return GetSession(sessionId) != null;
    }

    /// <inheritdoc />
    public void RevokeSession(string sessionId)
    {
        _tokenStore.RemoveSessionAsync(sessionId).GetAwaiter().GetResult();
        _logger.LogInformation("Session revoked: {SessionId}", sessionId[..Math.Min(8, sessionId.Length)]);
    }

    /// <inheritdoc />
    public void SetPinVerified(string sessionId)
    {
        var session = GetSession(sessionId);
        if (session != null)
        {
            session.AuthLevel = 2;
            session.PinVerifiedAt = DateTime.UtcNow;
            _tokenStore.UpdateSessionAsync(session).GetAwaiter().GetResult();
            _logger.LogInformation("PIN verified for session: {SessionId}", sessionId[..Math.Min(8, sessionId.Length)]);
        }
    }

    /// <inheritdoc />
    public int GetAuthLevel(string sessionId)
    {
        var session = GetSession(sessionId);
        if (session == null) return 0;

        // If PIN verification has expired, downgrade to level 1
        if (session.AuthLevel == 2 && !IsPinVerificationValidInternal(session))
        {
            session.AuthLevel = 1;
            session.PinVerifiedAt = null;
            _tokenStore.UpdateSessionAsync(session).GetAwaiter().GetResult();
            _logger.LogDebug("PIN verification expired, downgrading to AuthLevel 1");
        }

        return session.AuthLevel;
    }

    /// <inheritdoc />
    public bool IsPinVerificationValid(string sessionId)
    {
        var session = GetSession(sessionId);
        return session != null && IsPinVerificationValidInternal(session);
    }

    /// <inheritdoc />
    public void UpdateUserInfo(string sessionId, Action<UserSessionInfo> update)
    {
        var session = GetSession(sessionId);
        if (session != null)
        {
            update(session.UserInfo);
            _tokenStore.UpdateSessionAsync(session).GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc />
    public void RefreshSession(string sessionId, string newToken, DateTime expiresAt)
    {
        var session = GetSession(sessionId);
        if (session != null)
        {
            session.AccessToken = newToken;
            session.TokenExpiry = expiresAt;
            _tokenStore.UpdateSessionAsync(session).GetAwaiter().GetResult();
            _logger.LogDebug("Session refreshed: {SessionId}", sessionId[..Math.Min(8, sessionId.Length)]);
        }
    }

    /// <summary>
    /// Internal helper to check PIN verification validity.
    /// </summary>
    private bool IsPinVerificationValidInternal(UserSession session)
    {
        return session.IsPinVerificationValid(_securityOptions.PinValidityMinutes);
    }

    /// <summary>
    /// Generates a cryptographically secure session ID.
    /// Uses 32 bytes of random data encoded as URL-safe Base64.
    /// </summary>
    private static string GenerateSecureSessionId()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }
}
