using System.Collections.Concurrent;
using AzureBank.Bff.Models;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Services.Implementations;

/// <summary>
/// In-memory session storage for MVP.
/// Uses ConcurrentDictionary for thread-safe operations.
///
/// Limitations:
/// - Sessions are lost on application restart
/// - Not suitable for multi-instance deployments
///
/// Production Alternative: Replace with Redis-backed implementation.
/// </summary>
public class InMemoryTokenStore : ITokenStoreService
{
    private readonly ConcurrentDictionary<string, UserSession> _sessions = new();
    private readonly BffSessionOptions _sessionOptions;
    private readonly ILogger<InMemoryTokenStore> _logger;

    public InMemoryTokenStore(
        IOptions<BffSessionOptions> sessionOptions,
        ILogger<InMemoryTokenStore> logger)
    {
        _sessionOptions = sessionOptions.Value;
        _logger = logger;
    }

    public Task StoreSessionAsync(UserSession session)
    {
        _sessions[session.SessionId] = session;
        _logger.LogDebug("Session stored for user {UserId}", session.UserId);
        return Task.CompletedTask;
    }

    public Task<UserSession?> GetSessionAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            // Check if session is still valid
            if (IsSessionValid(session))
            {
                return Task.FromResult<UserSession?>(session);
            }

            // Session expired, remove it
            _sessions.TryRemove(sessionId, out _);
            _logger.LogDebug("Expired session removed: {SessionId}", sessionId[..Math.Min(8, sessionId.Length)]);
        }

        return Task.FromResult<UserSession?>(null);
    }

    public Task UpdateSessionAsync(UserSession session)
    {
        _sessions[session.SessionId] = session;
        return Task.CompletedTask;
    }

    public Task RemoveSessionAsync(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _logger.LogDebug("Session removed: {SessionId}", sessionId[..Math.Min(8, sessionId.Length)]);
        return Task.CompletedTask;
    }

    public Task CleanupExpiredSessionsAsync()
    {
        var expiredCount = 0;
        foreach (var kvp in _sessions)
        {
            if (!IsSessionValid(kvp.Value))
            {
                if (_sessions.TryRemove(kvp.Key, out _))
                {
                    expiredCount++;
                }
            }
        }

        if (expiredCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired sessions", expiredCount);
        }

        return Task.CompletedTask;
    }

    private bool IsSessionValid(UserSession session)
    {
        var now = DateTime.UtcNow;

        // Check absolute timeout
        if (now >= session.SessionCreated.AddMinutes(_sessionOptions.AbsoluteTimeoutMinutes))
        {
            return false;
        }

        // Check inactivity timeout
        if (now >= session.LastActivity.AddMinutes(_sessionOptions.InactivityTimeoutMinutes))
        {
            return false;
        }

        // Check token expiry
        if (session.IsTokenExpired)
        {
            return false;
        }

        return true;
    }
}
