using System.Diagnostics.CodeAnalysis;

namespace AzureBank.Tests.Fixtures;

/// <summary>A text with parentheses where it has braces, so two of them can be compared.</summary>
/// <remarks>
/// With parentheses where the body has braces. The assertion library builds a failure's message
/// with <c>string.Format</c>, and a brace in either of two long texts that differ makes it throw
/// <see cref="FormatException"/> in place of the message that shows where they differ; inside
/// an assertion scope that exception is then lost behind the scope's own, with every assertion
/// after it (FluentAssertions 8.8.0, measured on a sign-in's answer).
/// </remarks>
internal static class ComparableText
{
    /// <summary>The same text, with <c>(</c> for <c>{</c> and <c>)</c> for <c>}</c>. Null stays null.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Of(string? text) => text?.Replace('{', '(').Replace('}', ')');
}
