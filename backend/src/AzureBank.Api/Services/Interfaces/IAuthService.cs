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
    Task<LoginResponse> LoginAsync(LoginRequest request);

    /// <summary>
    /// Registers a new user with initial account and returns registration response.
    /// </summary>
    Task<RegisterResponse> RegisterAsync(RegisterRequest request);

    /// <summary>
    /// Exchanges a valid refresh token for a fresh access + refresh token pair (rotation).
    /// </summary>
    Task<RefreshResponse> RefreshAsync(RefreshRequest request);

    /// <summary>
    /// Logs out a user, revoking all of their active refresh tokens.
    /// </summary>
    Task LogoutAsync(Guid userId);

    /// <summary>
    /// Gets the current authenticated user's information.
    /// </summary>
    Task<UserResponse> GetCurrentUserAsync(Guid userId);

    /// <summary>
    /// Verifies a user's PIN for step-up authentication.
    /// </summary>
    Task<bool> VerifyPinAsync(Guid userId, string pin);

    /// <summary>
    /// Sets or updates a user's PIN.
    /// </summary>
    Task SetPinAsync(Guid userId, SetPinRequest request);
}
