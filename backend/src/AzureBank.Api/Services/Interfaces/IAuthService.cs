using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.User;

namespace AzureBank.Api.Services.Interfaces;

/// <summary>
/// Service interface for authentication operations.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Authenticates a user and returns login response with token.
    /// </summary>
    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new user with initial account and returns registration response.
    /// </summary>
    Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Presents a grant for a fresh access token, capped at the grant's expiry. The grant is read,
    /// not rotated: the same one renews again (ADR-0057 §4.3).
    /// </summary>
    /// <param name="request">The grant.</param>
    /// <param name="receivedAt">When the API received the request (the tripwire compares it).</param>
    Task<RefreshResponse> RefreshAsync(RefreshRequest request, DateTime receivedAt);

    /// <summary>
    /// Revokes the grants of sessions the BFF has ended (ADR-0057 §4.4), stamped with
    /// <paramref name="receivedAt"/>. Unknown and already-revoked grants are not an error.
    /// </summary>
    Task RevokeAsync(RevokeRequest request, DateTime receivedAt);

    /// <summary>
    /// Signs the user out of EVERY session: revokes all of their active grants as
    /// <c>SignOutEverywhere</c>, stamped with <paramref name="receivedAt"/>, and raises the user's
    /// session stamp in the same transaction (ADR-0057 §5.3).
    /// </summary>
    Task LogoutAsync(Guid userId, DateTime receivedAt);

    /// <summary>
    /// The current session stamp of each listed user the database knows; an unknown id has no entry.
    /// Read by the BFF's watcher for the users who hold sessions (ADR-0057 §5.3).
    /// </summary>
    Task<IReadOnlyList<UserSessionStamp>> GetSessionStampsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current authenticated user's information.
    /// </summary>
    Task<UserResponse> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a user's PIN for step-up authentication.
    /// </summary>
    Task<bool> VerifyPinAsync(Guid userId, string pin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or updates a user's PIN.
    /// </summary>
    Task SetPinAsync(Guid userId, SetPinRequest request, CancellationToken cancellationToken = default);
}
