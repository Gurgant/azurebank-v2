using AzureBank.Bff.Models;
using AzureBank.Bff.Services;

namespace AzureBank.Bff.Services.Interfaces;

/// <summary>
/// Low-level session storage abstraction.
/// MVP: In-memory storage using ConcurrentDictionary.
/// Production: Replace with Redis or distributed cache.
/// </summary>
public interface ITokenStoreService
{
    /// <summary>
    /// Stores a complete user session.
    /// </summary>
    Task StoreSessionAsync(UserSession session);

    /// <summary>
    /// Retrieves a session by session ID.
    /// Returns null if not found or expired.
    /// </summary>
    Task<UserSession?> GetSessionAsync(string sessionId);

    /// <summary>
    /// Writes back a session that is still stored (e.g., LastActivity, AuthLevel). A session that was
    /// removed meanwhile stays removed: a write-back never brings an ended session back (06 §4.6, F2).
    /// </summary>
    Task UpdateSessionAsync(UserSession session);

    /// <summary>
    /// Ends a session: under its lock, marks it ended, captures the renewal in flight and removes it,
    /// then queues its grant for revocation at the API (06 §4.6). The one path every ending takes.
    /// True when this call ended it; false for an unknown id or a session that had already ended.
    /// </summary>
    Task<bool> EndSessionAsync(string sessionId);

    /// <summary>
    /// Ends every stored session for a graceful stop, and hands back their grants for one revoke
    /// call instead of queueing each (06 §4.6, F7).
    /// </summary>
    IReadOnlyList<GrantRevocation> EndAllSessions();

    /// <summary>
    /// Cleans up expired sessions (called periodically by background service).
    /// </summary>
    Task CleanupExpiredSessionsAsync();
}
