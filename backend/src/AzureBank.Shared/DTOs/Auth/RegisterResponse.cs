using AzureBank.Shared.DTOs.Account;

namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// Response returned after successful user registration.
/// Includes user info, initial account, and authentication token.
/// </summary>
public class RegisterResponse
{
    /// <summary>
    /// Registered user information
    /// </summary>
    public required UserLoginInfo User { get; set; }

    /// <summary>
    /// Initial primary account created for the user
    /// </summary>
    public required AccountResponse Account { get; set; }

    /// <summary>
    /// Authentication token for immediate login
    /// </summary>
    public required TokenResponse Token { get; set; }
}
