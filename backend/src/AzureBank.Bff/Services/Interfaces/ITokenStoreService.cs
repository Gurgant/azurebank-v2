using AzureBank.Bff.Models;

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
    /// Updates an existing session (e.g., LastActivity, AuthLevel).
    /// </summary>
    Task UpdateSessionAsync(UserSession session);

    /// <summary>
    /// Removes a session from storage.
    /// </summary>
    Task RemoveSessionAsync(string sessionId);

    /// <summary>
    /// Cleans up expired sessions (called periodically by background service).
    /// </summary>
    Task CleanupExpiredSessionsAsync();
}
