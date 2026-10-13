using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureBank.Api.Converters;

/// <summary>
/// JSON converter that reads an enum from one of its names and from nothing else.
///
/// Purpose:
/// - Rejects integer values for enums (e.g., "type": 0)
/// - Accepts a string only when it is a name of the enum, in any letter case
///   (e.g., "type": "Checking", "type": "checking")
/// - Rejects every other string with the answer an unknown name gets: a number written as text
///   (e.g., "type": "1"), a list of names, a name with white space around it
/// - Rejects the empty string with a sentence of its own, which does not list the names
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
/// Generic strict enum converter that takes a name of the enum and rejects everything else.
/// </summary>
public sealed class StrictJsonStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly string[] Names = Enum.GetNames<TEnum>();

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

        // The text is compared with the names and never handed to Enum.TryParse, which reads
        // more than names: a number written as text ("1", "+1", "99"), a comma-separated list of
        // names even when the enum is not flags, and any of them with white space around it. A
        // number that is no member would bind as well, to a value the enum does not define.
        // The exact spelling is looked for first, so that two names that differ only by case
        // each stay reachable.
        var name = NameEqualTo(stringValue, StringComparison.Ordinal)
            ?? NameEqualTo(stringValue, StringComparison.OrdinalIgnoreCase);

        if (name is not null)
        {
            return Enum.Parse<TEnum>(name);
        }

        throw new JsonException(
            $"'{stringValue}' is not a valid value for enum '{typeof(TEnum).Name}'. " +
            $"Valid values: {string.Join(", ", Enum.GetNames(typeof(TEnum)))}");
    }

    private static string? NameEqualTo(string text, StringComparison comparison)
    {
        foreach (var name in Names)
        {
            if (string.Equals(name, text, comparison))
            {
                return name;
            }
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
