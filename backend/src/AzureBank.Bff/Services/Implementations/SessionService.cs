using System.Security.Cryptography;
using AzureBank.Bff.Models;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using Microsoft.Extensions.Options;
using AzureBank.Shared.Utilities;

namespace AzureBank.Bff.Services.Implementations;

/// <summary>
/// Session service for managing user sessions in the BFF layer.
/// Creates cryptographically secure session IDs and manages token storage.
/// </summary>
public class SessionService : ISessionService
{
    private readonly ITokenStoreService _tokenStore;
    private readonly SecurityOptions _securityOptions;
    private readonly BffSessionOptions _sessionOptions;
    private readonly ILogger<SessionService> _logger;

    public SessionService(
        ITokenStoreService tokenStore,
        IOptions<SecurityOptions> securityOptions,
        IOptions<BffSessionOptions> sessionOptions,
        ILogger<SessionService> logger)
    {
        _tokenStore = tokenStore;
        _securityOptions = securityOptions.Value;
        _sessionOptions = sessionOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string CreateSession(
        string accessToken, DateTime tokenExpiry, string? refreshToken, DateTime? refreshTokenExpiresAt,
        UserLoginInfo userInfo, int sessionStamp = 0)
    {
        var sessionId = GenerateSecureSessionId();
        var now = DateTime.UtcNow;

        // The earlier of the configured cap and the grant's own expiry (ADR-0057 §4.1, F5). A grant
        // that expired first would leave a session that looks alive and can no longer renew.
        // Without a grant (registration's best-effort issuance failed) the configured cap stands
        // alone, and the store keeps the hard stop at the access token's expiry (F15).
        var cap = now.AddMinutes(_sessionOptions.AbsoluteTimeoutMinutes);
        var grantExpiresAt = refreshToken is null ? null : refreshTokenExpiresAt;

        var session = new UserSession
        {
            SessionId = sessionId,
            UserId = userInfo.Id,
            AccessToken = accessToken,
            TokenExpiry = tokenExpiry,
            TokenIssuedAt = AccessTokenIssuedAt.Of(accessToken, tokenExpiry, now),
            RefreshToken = refreshToken,
            GrantExpiresAt = grantExpiresAt,
            SessionCreated = now,
            AbsoluteExpiresAt = grantExpiresAt is { } grantEnd && grantEnd < cap ? grantEnd : cap,
            SessionStamp = sessionStamp,
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
    public void EndSession(string sessionId)
    {
        // Logged only when this call ended it. A sign-in that carries a stale cookie
        // (ADR-0057 §4.6, F13) asks to end a session that is long gone, and saying "ended" there
        // would be a false line.
        if (_tokenStore.EndSessionAsync(sessionId).GetAwaiter().GetResult())
        {
            _logger.LogInformation("Session ended: {SessionId}", SecretPrefix.Of(sessionId));
        }
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
            _logger.LogInformation("PIN verified for session: {SessionId}", SecretPrefix.Of(sessionId));
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
    public bool RefreshSession(UserSession session, string newToken, DateTime expiresAt)
    {
        // The object, not a lookup by id: a lookup runs the store's validity check, which can END the
        // session, and ending takes the session's lock the caller already holds. Only a later expiry
        // is stored (ADR-0057 §4.5), so a late answer can never swap a fresher token for an older
        // one.
        if (session.Ended || expiresAt <= session.TokenExpiry)
        {
            return false;
        }

        session.AccessToken = newToken;
        session.TokenExpiry = expiresAt;
        session.TokenIssuedAt = AccessTokenIssuedAt.Of(newToken, expiresAt, DateTime.UtcNow);
        _tokenStore.UpdateSessionAsync(session).GetAwaiter().GetResult();
        _logger.LogDebug("Session refreshed: {SessionId}", SecretPrefix.Of(session.SessionId));
        return true;
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
