namespace AzureBank.Shared.DTOs.Auth;

// Login used to send the access token as a bare string, with ExpiresAt and RefreshToken beside
// it (measured 2026-09-23), while registration nested all three in `token`: two answers to the
// same question from one API. The history lives here and not in the summary below, because the
// summary is published in the OpenAPI document and a client reads it as the contract.

/// <summary>
/// Result of a successful login: the same <see cref="TokenResponse"/> registration returns, so
/// the two sign-in endpoints answer one shape.
/// </summary>
public class LoginResponse
{
    public required TokenResponse Token { get; set; }

    public required UserLoginInfo User { get; set; }
}
