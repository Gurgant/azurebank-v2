using System.Text.RegularExpressions;
using AzureBank.Tests.Fixtures;

namespace AzureBank.Tests.Unit;

/// <summary>
/// The helper the comparisons of brace-holding texts go through, held on its own: two texts
/// that differ still differ after it, whatever stands where the brace stood.
/// </summary>
/// <remarks>
/// Every comparison here is xUnit's, whose message shows a brace as it is: the helper exists
/// because the other library's message does not. The pairs and the read-back are examples of
/// the one-to-one claim, not a proof of it.
/// </remarks>
public class ComparableTextTests
{
    [Theory]
    [InlineData("{", "%7B")]
    [InlineData("}", "%7D")]
    [InlineData("%", "%%")]
    [InlineData("{\"ok\":true}", "%7B\"ok\":true%7D")]
    [InlineData("%7B", "%%7B")]
    public void Of_WritesABraceAndAPercentSignAsTheirTokens(string text, string expected)
    {
        Assert.Equal(expected, ComparableText.Of(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("(\"ok\":true) at snippet.cs:4")]
    public void Of_LeavesATextWithNoneOfTheThreeAsItIs(string text)
    {
        Assert.Equal(text, ComparableText.Of(text));
    }

    [Fact]
    public void Of_KeepsNullNull()
    {
        Assert.Null(ComparableText.Of(null));
    }

    /// <summary>
    /// The first four pairs are a brace against a parenthesis in the same place. The last three
    /// are the pairs a helper writes alike when it does not double the percent sign, or doubles
    /// it after the braces: both were tried, and each failed these three.
    /// </summary>
    [Theory]
    [InlineData("{", "(")]
    [InlineData("}", ")")]
    [InlineData("{\"ok\":true}", "(\"ok\":true)")]
    [InlineData("in {Elapsed:0.0000}ms", "in (Elapsed:0.0000)ms")]
    [InlineData("{", "%7B")]
    [InlineData("}", "%7D")]
    [InlineData("%{", "%%7B")]
    public void Of_KeepsTwoDifferentTextsDifferent(string one, string other)
    {
        Assert.NotEqual(ComparableText.Of(one), ComparableText.Of(other));
    }

    [Fact]
    public void Of_KeepsTwoEqualTextsEqual()
    {
        const string text = "100% of {\"ok\":true}";
        var sameTextBuiltApart = new string(text.AsSpan());

        Assert.Equal(ComparableText.Of(text), ComparableText.Of(sameTextBuiltApart));
    }

    [Theory]
    [InlineData("{\"ok\":true}")]
    [InlineData("in {Elapsed:0.0000}ms")]
    [InlineData("%7B")]
    [InlineData("%%7D}")]
    public void Of_CanBeReadBackToTheTextItCameFrom(string text)
    {
        Assert.Equal(text, ReadBack(ComparableText.Of(text)));
    }

    /// <summary>An encoded text read left to right, each token back to the character it stands for.</summary>
    private static string ReadBack(string encoded) =>
        Regex.Replace(encoded, "%(%|7B|7D)", token => token.Value switch
        {
            "%%" => "%",
            "%7B" => "{",
            _ => "}",
        });
}
