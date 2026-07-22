namespace AzureBank.Shared.DTOs.Auth;

public class LoginResponse
{
    public required string Token { get; set; }
    public required DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Refresh token (plaintext, shown once) for rotating the short-lived access token via
    /// POST /api/auth/refresh. In the BFF deployment this is captured server-side and never
    /// reaches the browser. Deliberately NOT `required`: a consumer deserializing this response
    /// across the service boundary (the BFF) must degrade gracefully on its absence rather than
    /// hard-fail — the API always populates it, so it is non-null in practice.
    /// </summary>
    public string? RefreshToken { get; set; }

    public required UserLoginInfo User { get; set; }
}
