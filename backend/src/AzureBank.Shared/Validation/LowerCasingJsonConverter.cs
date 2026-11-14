using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureBank.Shared.Validation;

/// <summary>
/// Reads a JSON string with its capital letters lower-cased (<see cref="HandleCase"/>), before any
/// validation sees it. It exists for a payee's handle (<see cref="PayeeAzureTagAttribute"/>), which
/// the API reads in any letter case but stores, compares and logs in lower case.
///
/// Everything but the letter case is the framework's own: a null stays null for [Required] to
/// refuse, and a value that is not a string is refused with the framework's own message, because
/// the framework's string converter does the reading. Writing is left alone, so a client that
/// serialises the DTO sends what it was given.
/// </summary>
public sealed class LowerCasingJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var framework = (JsonConverter<string>)options.GetConverter(typeof(string));
        var text = framework.Read(ref reader, typeToConvert, options);
        return text is null ? null : HandleCase.Lower(text);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
