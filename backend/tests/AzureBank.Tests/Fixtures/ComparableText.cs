using System.Diagnostics.CodeAnalysis;

namespace AzureBank.Tests.Fixtures;

/// <summary>A text with parentheses where it has braces, for a comparison whose failure can be read.</summary>
/// <remarks>
/// The assertion library builds a failure's message with <c>string.Format</c>, and a brace in
/// either of two texts that differ spoils that message (FluentAssertions 8.8.0, measured on the
/// tests that compare through this helper). Two long answers throw <see cref="FormatException"/>
/// in place of the message that shows where they differ. A short JSON text fails inside a
/// warning that the message could not be formatted. A text with a <c>{Name}</c> in it is shown
/// without that name, so two that differ only there are shown alike. The price: a brace and a
/// parenthesis in the same place compare equal.
/// </remarks>
internal static class ComparableText
{
    /// <summary>The same text, with <c>(</c> for <c>{</c> and <c>)</c> for <c>}</c>. Null stays null.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Of(string? text) => text?.Replace('{', '(').Replace('}', ')');
}
