using System.Buffers.Text;
using System.Text.Json;

namespace AzureBank.Bff.Services;

/// <summary>
/// Reads when an access token was issued, so the BFF can take its renewal thresholds from the
/// token's own lifetime, L = exp - iat (ADR-0057 §4.5, F6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the token's own lifetime and not a constant.</b> The thresholds used to be a fixed 60 s
/// before expiry. That is a quarter of nothing on the 2-minute tokens the outage harness runs with,
/// and it gave a renewal no room to fail and try again before the token died. Half the lifetime for
/// the background renewal and min(60 s, L/4) for the foreground one scale with whatever the API
/// issues: 7.5 min and 60 s for 15-minute tokens, 60 s and 30 s for 2-minute ones. A token the API
/// clamped to its grant's expiry is short, and its thresholds shrink with it.
/// </para>
/// <para>
/// <b>Read, never verified.</b> The BFF does not validate the token — the API does, on every call —
/// so this only decodes the payload to find <c>iat</c>. A token it cannot read, or whose <c>iat</c>
/// is not before its expiry, falls back to the moment the BFF received it: later than the true
/// issue by the network's latency, which makes L slightly shorter and a renewal slightly later.
/// </para>
/// </remarks>
public static class AccessTokenIssuedAt
{
    /// <summary>
    /// The token's <c>iat</c>, or <paramref name="receivedAt"/> when the token carries no usable one.
    /// </summary>
    public static DateTime Of(string accessToken, DateTime expiresAt, DateTime receivedAt)
    {
        var issuedAt = ReadIat(accessToken);
        return issuedAt is { } iat && iat < expiresAt ? iat : receivedAt;
    }

    private static DateTime? ReadIat(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            if (payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty("iat", out var iat)
                && iat.ValueKind == JsonValueKind.Number
                && iat.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            // Not a JWT this can read: the caller falls back to the receipt time.
        }

        return null;
    }
}
