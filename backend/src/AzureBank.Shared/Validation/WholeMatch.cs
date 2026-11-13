using System.Text.RegularExpressions;

namespace AzureBank.Shared.Validation;

internal static class WholeMatch
{
    /// <summary>
    /// True only when <paramref name="pattern"/> matches from the first character to the last.
    /// Without <see cref="RegexOptions.Multiline"/>, <c>$</c> also matches just before a final line
    /// feed, so <c>IsMatch</c> on a <c>^...$</c> pattern lets "alice\n" through. The pattern stays as
    /// published (the OpenAPI document and the generated zod schema carry the same ECMA text);
    /// requiring the match to cover the whole value is what
    /// <see cref="System.ComponentModel.DataAnnotations.RegularExpressionAttribute"/> already does.
    /// </summary>
    public static bool IsWholeMatch(this Regex pattern, string value)
    {
        var match = pattern.Match(value);
        return match.Success && match.Index == 0 && match.Length == value.Length;
    }
}
