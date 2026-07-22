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
    /// NOT `required` so a cross-boundary consumer (the BFF) degrades gracefully on its absence;
    /// the API always populates it on success.
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
