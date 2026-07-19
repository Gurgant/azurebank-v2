using AzureBank.Shared.Utilities;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Utilities;

/// <summary>
/// Contract guard for LogSanitizer. The CodeQL model pack
/// (.github/codeql/extensions/azurebank-csharp-models) tells CodeQL that Sanitize's return
/// value is safe for log sinks - CodeQL trusts that claim unconditionally and forever. THIS
/// suite is what keeps the claim true: it must be impossible to weaken Sanitize without
/// these tests failing.
/// </summary>
public class LogSanitizerTests
{
    [Fact]
    public void Sanitize_RemovesEveryC0ControlCharacterAndDel()
    {
        for (var c = '\u0000'; c <= '\u001F'; c++)
        {
            LogSanitizer.Sanitize($"a{c}b").Should().Be("ab", $"U+{(int)c:X4} must be stripped");
        }

        LogSanitizer.Sanitize("a\u007Fb").Should().Be("ab");
    }

    [Fact]
    public void Sanitize_RemovesC1ControlsAndUnicodeLineSeparators()
    {
        // U+0085 NEL, U+2028 LINE SEPARATOR, U+2029 PARAGRAPH SEPARATOR render as newlines
        // in some log viewers - the classic forging bypass beyond plain CRLF.
        LogSanitizer.Sanitize("a\u0085b\u2028c\u2029d").Should().Be("abcd");
    }

    [Fact]
    public void Sanitize_NeutralizesLogForgingPayload()
    {
        var forged = "user123\r\n[ERROR] forged admin login\u001B[31m";

        var result = LogSanitizer.Sanitize(forged);

        result.Should().NotContain("\r").And.NotContain("\n").And.NotContain("\u001B");
        result.Should().Be("user123[ERROR] forged admin login[31m");
    }

    [Fact]
    public void Sanitize_IsIdempotent()
    {
        var once = LogSanitizer.Sanitize("a\r\nb\tc");

        LogSanitizer.Sanitize(once).Should().Be(once);
    }

    [Fact]
    public void Sanitize_PreservesOrdinaryText()
    {
        const string text = "Main Checking Account (EUR) - n.1 citta'";

        LogSanitizer.Sanitize(text).Should().Be(text);
    }

    [Fact]
    public void Sanitize_NullOrEmpty_YieldsEmpty_NeverThrows()
    {
        LogSanitizer.Sanitize(null).Should().Be(string.Empty);
        LogSanitizer.Sanitize(string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void Sanitize_RemovesEveryC1ControlCharacter()
    {
        // U+0080-U+009F: the full C1 range (incl. U+009B CSI, exploitable like ESC).
        for (var c = '\u0080'; c <= '\u009F'; c++)
        {
            LogSanitizer.Sanitize($"a{c}b").Should().Be("ab", $"U+{(int)c:X4} must be stripped");
        }
    }

    [Fact]
    public void Sanitize_RemovesEveryUnicodeCategoryTheCodeQlBarrierTrusts()
    {
        // The CodeQL model pack suppresses log-forging findings for Sanitize's return value
        // on the strength of the FULL \p{C} claim - so every category must stay pinned:
        // a regression to a narrower pattern (e.g. only C0/C1) must fail here, not ship.
        LogSanitizer.Sanitize("a\u202Eb").Should().Be("ab", "Cf: U+202E RTL override reorders rendered text");
        LogSanitizer.Sanitize("a\u200Bb").Should().Be("ab", "Cf: U+200B zero-width space hides content");
        LogSanitizer.Sanitize("a\uE000b").Should().Be("ab", "Co: private-use rendering is viewer-defined");
        LogSanitizer.Sanitize("a\uFDD0b").Should().Be("ab", "Cn: noncharacter");
        LogSanitizer.Sanitize("a" + '\uD800' + "b").Should().Be("ab", "Cs: isolated surrogate breaks downstream encoders");
    }
}
