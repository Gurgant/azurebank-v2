using System.Collections.Concurrent;
using AzureBank.Bff.Models;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using Microsoft.Extensions.Options;
using AzureBank.Shared.Utilities;

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
/// <remarks>
/// <para>
/// <b>Every ending goes through <see cref="End"/></b>: "Esci", idle expiry and the cap found on a
/// read or by the sweep, re-authentication, a new sign-in over an old cookie, a dead grant, and a
/// graceful stop (06 §4.6). It marks the session ended and removes it under the session's own lock,
/// then hands its grant to <see cref="GrantRevoker"/> with the renewal it caught in flight. Before
/// PR-1 an expired session was only dropped from this dictionary, and its refresh token lived on at
/// the API for days with no holder.
/// </para>
/// </remarks>
public class InMemoryTokenStore : ITokenStoreService
{
    private readonly ConcurrentDictionary<string, UserSession> _sessions = new();
    private readonly BffSessionOptions _sessionOptions;
    private readonly GrantRevoker _grantRevoker;
    private readonly ILogger<InMemoryTokenStore> _logger;

    public InMemoryTokenStore(
        IOptions<BffSessionOptions> sessionOptions,
        GrantRevoker grantRevoker,
        ILogger<InMemoryTokenStore> logger)
    {
        _sessionOptions = sessionOptions.Value;
        _grantRevoker = grantRevoker;
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

            // Expired: end it the way every session ends, so its grant is revoked too.
            End(session);
            _logger.LogDebug("Expired session ended: {SessionId}", SecretPrefix.Of(sessionId));
        }

        return Task.FromResult<UserSession?>(null);
    }

    public Task UpdateSessionAsync(UserSession session)
    {
        /*
          TryUpdate, NEVER the indexer (06 §4.6, F2). Every write in SessionService reads the session,
          changes it, then writes it back. The indexer re-added a session that "Esci" removed between
          the read and the write, and the signed-out cookie worked again: measured on main, the next
          request with it answered 200 (06 §10 O0-2 item 7). TryUpdate replaces only an entry that is
          still there, and only with the same object, so a removed session stays removed.
        */
        _sessions.TryUpdate(session.SessionId, session, session);
        return Task.CompletedTask;
    }

    public Task<bool> EndSessionAsync(string sessionId) =>
        Task.FromResult(_sessions.TryGetValue(sessionId, out var session) && End(session));

    public IReadOnlyList<GrantRevocation> EndAllSessions()
    {
        var grants = new List<GrantRevocation>();
        foreach (var kvp in _sessions)
        {
            if (EndUnderLock(kvp.Value).Grant is { } grant)
            {
                grants.Add(grant);
            }
        }

        return grants;
    }

    public Task CleanupExpiredSessionsAsync()
    {
        var expiredCount = 0;
        foreach (var kvp in _sessions)
        {
            if (!IsSessionValid(kvp.Value) && End(kvp.Value))
            {
                expiredCount++;
            }
        }

        if (expiredCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired sessions", expiredCount);
        }

        return Task.CompletedTask;
    }

    /// <summary>Ends <paramref name="session"/> and queues its grant. False if it had already ended.</summary>
    private bool End(UserSession session)
    {
        var (endedNow, grant) = EndUnderLock(session);
        if (grant is not null)
        {
            _grantRevoker.Queue(grant);
        }

        return endedNow;
    }

    /// <summary>
    /// Under the session's lock: marks it ended, captures the renewal in flight and removes it
    /// (06 §4.6 step 1). Returns whether this call ended it, and the grant to revoke — null when the
    /// session had already ended or never held one. The removal names the object as well as the id,
    /// so it can never take out a different session stored under the same id.
    /// </summary>
    private (bool EndedNow, GrantRevocation? Grant) EndUnderLock(UserSession session)
    {
        lock (session.SyncRoot)
        {
            var endedNow = !session.Ended;
            session.Ended = true;
            var inFlight = session.InFlightRenewal;
            _sessions.TryRemove(KeyValuePair.Create(session.SessionId, session));

            if (!endedNow || session.RefreshToken is null)
            {
                return (endedNow, null);
            }

            // Retried until the grant itself expires. A registration's grant with no reported
            // expiry is bounded by the session's own cap, which the grant cannot outlive by more
            // than the sign-in's duration.
            return (true, new GrantRevocation(
                session.SessionId,
                session.RefreshToken,
                session.GrantExpiresAt ?? session.AbsoluteExpiresAt,
                inFlight));
        }
    }

    private bool IsSessionValid(UserSession session)
    {
        var now = DateTime.UtcNow;

        if (session.Ended)
        {
            return false;
        }

        // Absolute timeout: the configured cap or the grant's expiry, whichever comes first (06 §4.1).
        if (now >= session.AbsoluteExpiresAt)
        {
            return false;
        }

        // Check inactivity timeout
        if (now >= session.LastActivity.AddMinutes(_sessionOptions.InactivityTimeoutMinutes))
        {
            return false;
        }

        // Access-token expiry no longer kills a session that can still renew (ADR-0021, PR-2): it
        // slides within the inactivity/absolute budgets above, and its JWT is renewed before the next
        // proxied call. A session that can NEVER renew again keeps the old hard stop instead of
        // becoming a zombie that 401s on every request: the tokenless one (registration's best-effort
        // issuance failed, 06 F15), and one whose token already reaches its grant's expiry.
        if (session.IsTokenExpired && !session.CanStillRenew)
        {
            return false;
        }

        return true;
    }
}
