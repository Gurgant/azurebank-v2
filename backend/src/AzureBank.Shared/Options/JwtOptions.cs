using System.Text;

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

    /// <summary>Fewest bytes of <see cref="Secret"/> the API starts with: HMAC-SHA256's 256 bits.</summary>
    public const int MinimumSecretBytes = 32;

    /// <summary>
    /// Secret key for signing JWT tokens: at least <see cref="MinimumSecretBytes"/> bytes as UTF-8,
    /// the encoding both signing sites use, so the rule counts bytes, not characters. The API
    /// refuses to start without it (<c>ValidateOnStart</c>). <i>(Until 2026-09-25 this said "must be
    /// at least 32 characters", and nothing enforced it: a 31-byte key started, and the first
    /// sign-in answered 500.)</i>
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

    /// <summary>Shortest <see cref="RefreshTokenCleanupInterval"/> the API starts with.</summary>
    public static readonly TimeSpan ShortestCleanupInterval = TimeSpan.FromMinutes(1);

    /// <summary>Longest <see cref="RefreshTokenCleanupInterval"/> the API starts with.</summary>
    public static readonly TimeSpan LongestCleanupInterval = TimeSpan.FromDays(7);

    /// <summary>
    /// How often expired refresh tokens are deleted, the first time one interval after start. Six
    /// hours, the value the sweep has always used; the sweep is hygiene, since every read already
    /// refuses an expired token, so the interval bounds only how many dead rows the table holds.
    /// </summary>
    public TimeSpan RefreshTokenCleanupInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>True when <paramref name="secret"/> is long enough to sign with.</summary>
    public static bool IsUsableSecret(string? secret) =>
        !string.IsNullOrWhiteSpace(secret) && Encoding.UTF8.GetByteCount(secret) >= MinimumSecretBytes;
}
