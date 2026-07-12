using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureBank.Api.Converters;

/// <summary>
/// JSON converter that serializes DateTime values in RFC 3339 format with UTC designator.
///
/// Purpose:
/// - Ensures DateTime output matches OpenAPI date-time format specification
/// - Fixes Schemathesis "Response violates schema" errors for date-time fields
/// - Converts local times to UTC and appends 'Z' suffix
///
/// Input:  DateTime (local or unspecified kind)
/// Output: "2026-01-12T10:48:26.5907775Z" (always UTC with Z suffix)
/// </summary>
public sealed class Rfc3339DateTimeConverter : JsonConverter<DateTime>
{
    // RFC 3339 format with 7 fractional digits (same precision as .NET)
    private const string Rfc3339Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dateString = reader.GetString();
        if (string.IsNullOrEmpty(dateString))
        {
            return default;
        }

        // Parse ISO 8601/RFC 3339 format
        return DateTime.Parse(dateString, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        // Convert to UTC if not already, then format with Z suffix
        var utcValue = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc) // Assume unspecified is UTC
        };

        writer.WriteStringValue(utcValue.ToString(Rfc3339Format));
    }
}
