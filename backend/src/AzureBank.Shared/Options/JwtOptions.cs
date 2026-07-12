namespace AzureBank.Shared.Options;

/// <summary>
/// Configuration options for JWT authentication.
/// Binds to "Jwt" section in appsettings.json.
/// </summary>
public class JwtOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "Jwt";

    /// <summary>
    /// Secret key for signing JWT tokens.
    /// Must be at least 32 characters for HMAC-SHA256.
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// JWT token issuer (iss claim)
    /// </summary>
    public string Issuer { get; set; } = "AzureBank";

    /// <summary>
    /// JWT token audience (aud claim)
    /// </summary>
    public string Audience { get; set; } = "AzureBank.Api";

    /// <summary>
    /// Access token expiration in minutes.
    /// Default: 15 minutes (banking security standard)
    /// </summary>
    public int ExpirationMinutes { get; set; } = 15;

    /// <summary>
    /// Refresh token expiration in days.
    /// Default: 7 days
    /// </summary>
    public int RefreshTokenExpirationDays { get; set; } = 7;
}
