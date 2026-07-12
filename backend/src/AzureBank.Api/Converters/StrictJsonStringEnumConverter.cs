using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureBank.Api.Converters;

/// <summary>
/// JSON converter that enforces string-only enum deserialization.
///
/// Purpose:
/// - Rejects integer values for enums (e.g., "type": 0)
/// - Only accepts string values (e.g., "type": "Checking")
/// - Fixes Schemathesis "API accepted schema-violating request" when sending integers
///
/// The standard JsonStringEnumConverter accepts both strings and integers,
/// but our OpenAPI schema documents enums as string-only.
/// </summary>
public sealed class StrictJsonStringEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsEnum;
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(StrictJsonStringEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>
/// Generic strict enum converter that rejects integer values.
/// </summary>
public sealed class StrictJsonStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            throw new JsonException(
                $"Integer values are not allowed for enum '{typeof(TEnum).Name}'. " +
                $"Use string values: {string.Join(", ", Enum.GetNames(typeof(TEnum)))}");
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected string value for enum '{typeof(TEnum).Name}', " +
                $"but got {reader.TokenType}.");
        }

        var stringValue = reader.GetString();

        if (string.IsNullOrEmpty(stringValue))
        {
            throw new JsonException($"Empty string is not a valid value for enum '{typeof(TEnum).Name}'.");
        }

        // Try case-insensitive parse
        if (Enum.TryParse<TEnum>(stringValue, ignoreCase: true, out var result))
        {
            return result;
        }

        throw new JsonException(
            $"'{stringValue}' is not a valid value for enum '{typeof(TEnum).Name}'. " +
            $"Valid values: {string.Join(", ", Enum.GetNames(typeof(TEnum)))}");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
