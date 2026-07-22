namespace AzureBank.Shared.DTOs.Auth;

public class LoginResponse
{
    public required string Token { get; set; }
    public required DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Refresh token (plaintext, shown once) for rotating the short-lived access token via
    /// POST /api/auth/refresh. In the BFF deployment this is captured server-side and never
    /// reaches the browser.
    /// </summary>
    public required string RefreshToken { get; set; }

    public required UserLoginInfo User { get; set; }
}
