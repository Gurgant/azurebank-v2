namespace AzureBank.Shared.DTOs.Auth;

/// <summary>
/// Token information returned in authentication responses.
/// </summary>
public class TokenResponse
{
    /// <summary>
    /// JWT access token
    /// </summary>
    public required string AccessToken { get; set; }

    /// <summary>
    /// Refresh token (plaintext, shown once) for rotating the access token via
    /// POST /api/auth/refresh. In the BFF deployment it is captured server-side. Deliberately
    /// NOT `required`: registration issues it BEST-EFFORT — the user + account are already
    /// committed, so a post-registration token-write failure must not fail the request. It is
    /// therefore genuinely optional here (null when that write failed); the user obtains a
    /// refresh token on their next login.
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Token expiration time in seconds
    /// </summary>
    public int ExpiresIn { get; set; }

    /// <summary>
    /// Token type (always "Bearer")
    /// </summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>
    /// Absolute expiration timestamp
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}
