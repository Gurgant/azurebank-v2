using AzureBank.Shared.Utilities;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Utilities;

/// <summary>
/// The one helper a secret may reach a log through, pinned on its own: eight characters, never
/// more, and a shorter secret unchanged.
/// </summary>
/// <remarks>
/// <c>LogPlaceholderClassTests</c> requires the helper by NAME at every <c>{SessionId}</c> site,
/// which proves the sites call it and nothing about what it returns. A review round on 2026-09-15
/// read the length as sixteen — it is not, <c>Math.Min(Length, …)</c> with <c>Length = 8</c> — but
/// the only thing that said so was the source, and a guard that names a helper should also hold
/// the helper. ADR-0017's rule says "the first eight characters"; <see cref="SecretPrefix.Length"/>
/// is that eight, and this is where the two are tied.
/// </remarks>
public class SecretPrefixTests
{
    [Fact]
    public void TheRuleAndTheConstantAgree()
    {
        SecretPrefix.Length.Should().Be(8, "ADR-0017's log-identifier rule: a secret appears only as its first eight characters");
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", "01234567")]
    [InlineData("0123456789", "01234567")]
    [InlineData("01234567", "01234567")]
    [InlineData("0123", "0123")]
    [InlineData("", "")]
    public void Of_KeepsAtMostEightCharacters(string secret, string expected)
    {
        var prefix = SecretPrefix.Of(secret);

        prefix.Should().Be(expected);
        prefix.Length.Should().BeLessThanOrEqualTo(SecretPrefix.Length);
    }
}
