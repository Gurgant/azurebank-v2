using System.Diagnostics.CodeAnalysis;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// A text written without braces, one-to-one, for a comparison that is exact and whose failure
/// can be read.
/// </summary>
/// <remarks>
/// The assertion library builds a failure's message with <c>string.Format</c>, and a brace in
/// either of two texts that differ spoils that message, unless both are eight characters or
/// fewer (FluentAssertions 8.8.0; the three outcomes that follow were measured on the tests
/// that compare through this helper). Two long answers throw <see cref="FormatException"/>
/// in place of the message that shows where they differ. A short JSON text fails inside a
/// warning that the message could not be formatted. A text with a <c>{Name}</c> in it is shown
/// without that name, so two that differ only there are shown alike.
/// <para>
/// The encoding is one-to-one. A percent sign is doubled first, then each brace becomes a token
/// that starts with one, so the result, read left to right, splits in one way only into
/// <c>%%</c>, <c>%7B</c>, <c>%7D</c> and characters that are not a percent sign, and reading it
/// so gives back the one text it came from. Two encoded texts are equal exactly when the two
/// texts are: the comparison is exact, and a parenthesis where a brace belongs fails it
/// (<c>ComparableTextTests</c> holds the helper to this on a few pairs). A failure shows both
/// texts with <c>%7B</c> and <c>%7D</c> where their braces are. The index the message names
/// counts the encoded text, and the stretch it prints can begin inside a token (<c>…7B</c> for
/// a brace).
/// </para>
/// </remarks>
internal static class ComparableText
{
    /// <summary>
    /// The same text, with <c>%%</c> for <c>%</c>, <c>%7B</c> for <c>{</c> and <c>%7D</c> for
    /// <c>}</c>. Null stays null.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Of(string? text) => text?.Replace("%", "%%").Replace("{", "%7B").Replace("}", "%7D");
}
