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
    /// the encoding every site that uses the key applies, so the rule counts bytes, not characters.
    /// The API refuses to start without it (<c>ValidateOnStart</c>). <i>(Until 2026-09-25 this said
    /// "must be at least 32 characters", and nothing enforced it: a 31-byte key started, and the
    /// first sign-in answered 500.)</i>
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

    /// <summary>Shortest <see cref="RefreshTokenLifetimeMinutes"/> the API starts with.</summary>
    public const int ShortestRefreshTokenLifetimeMinutes = 15;

    /// <summary>Longest <see cref="RefreshTokenLifetimeMinutes"/> the API starts with: one day.</summary>
    public const int LongestRefreshTokenLifetimeMinutes = 1440;

    /// <summary>
    /// How long a grant (the BFF session's refresh token) lives, in minutes from issue: fixed then and
    /// never extended, since a renewal writes nothing (ADR-0057 §4.1). Sixty by default, the BFF's
    /// absolute session cap, so nothing from one sign-in outlives it. Checked at start: between
    /// <see cref="ShortestRefreshTokenLifetimeMinutes"/> and <see cref="LongestRefreshTokenLifetimeMinutes"/>,
    /// and never below <see cref="ExpirationMinutes"/>, because the sign-in's access token is minted
    /// just before its grant and is not capped by it (see <see cref="IsUsableRefreshTokenLifetime"/>).
    /// <i>(Until PR-1 this was <c>RefreshTokenExpirationDays</c>, 7, and every rotation added another
    /// seven days.)</i>
    /// </summary>
    public int RefreshTokenLifetimeMinutes { get; set; } = 60;

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

    /// <summary>
    /// True when <see cref="RefreshTokenLifetimeMinutes"/> is in range and not below
    /// <see cref="ExpirationMinutes"/> (ADR-0057 F11). The second half is what keeps
    /// "nothing from one sign-in lasts past the grant" true for the sign-in's own access token: a
    /// renewal clamps its token to the grant, but the sign-in mints its token BEFORE the grant
    /// exists, so only this rule stops a 30-minute access token outliving a 15-minute grant.
    /// </summary>
    public static bool IsUsableRefreshTokenLifetime(int lifetimeMinutes, int accessTokenMinutes) =>
        lifetimeMinutes is >= ShortestRefreshTokenLifetimeMinutes and <= LongestRefreshTokenLifetimeMinutes
        && lifetimeMinutes >= accessTokenMinutes;
}
