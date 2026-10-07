using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Primitives;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The list <see cref="StrictForwardedFor"/> makes of <c>X-Forwarded-For</c>: every comma ends an
/// entry, and an entry is an address as it is written or the word no parser takes for one. What
/// the host then believes of that list is held on its pipeline, in
/// <see cref="TrustedProxyNetworkTests"/> and <see cref="ForwardedForOnARealConnectionTests"/>.
/// </summary>
public class StrictForwardedForTests
{
    private const string Caller = "203.0.113.9";
    private const string Claimed = "198.51.100.200";

    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("203.0.113.9:51234")]
    [InlineData("2001:db8:1:2::9")]
    [InlineData("2001:DB8:1:2::9")]
    [InlineData("[2001:db8:1:2::9]:443")]
    [InlineData("::ffff:203.0.113.9")]
    public void AnEntryWrittenAsAnAddress_IsKeptAsItIsWritten(string entry)
    {
        StrictForwardedFor.Of(entry).Should().Be(entry);
        StrictForwardedFor.Of($"{Claimed}, {entry}").Should().Be($"{Claimed}, {entry}");
    }

    [Theory]
    // What a proxy writes when it will not say, and what is no address at all.
    [InlineData("unknown")]
    [InlineData("_hidden")]
    [InlineData("proxy.example")]
    [InlineData("203.0.113.9 x")]
    [InlineData("203.0.113.9;")]
    // A zone: the parser drops whatever follows the percent sign, so it is not an address here.
    [InlineData("fe80::1%3")]
    [InlineData("fe80::1%eth0")]
    [InlineData("::ffff:198.51.100.200%")]
    // A quotation mark, alone, around an address, or after a zone.
    [InlineData("\"")]
    [InlineData("\"203.0.113.9\"")]
    [InlineData("::ffff:198.51.100.200%\"")]
    // Digits a lenient parser takes for an address written another way.
    [InlineData("0xcb.0.113.9")]
    [InlineData("٢٠٣.0.113.9")]
    [InlineData("２０３.0.113.9")]
    // Characters no header line should hold; a blank that is not a blank or a tab.
    [InlineData("203.0.113.9\0")]
    [InlineData("203.0.113.9 ")]
    [InlineData("203.0.113.9\u0001")]
    public void AnEntryHoldingAnyOtherCharacter_IsNotAnAddress(string entry)
    {
        StrictForwardedFor.Of(entry).Should().Be(StrictForwardedFor.NotAnAddress);
        // And it changes nothing of the entry after it, which is the proxy's.
        StrictForwardedFor.Of($"{entry}, {Caller}").Should().Be($"{StrictForwardedFor.NotAnAddress}, {Caller}");
        StrictForwardedFor.Of($"{entry},{Caller}").Should().Be($"{StrictForwardedFor.NotAnAddress}, {Caller}");
    }

    [Fact]
    public void AQuotationMark_DoesNotHideTheCommaAfterIt()
    {
        // The header a caller behind the proxy was believed with: its own entry, then the proxy's.
        StrictForwardedFor.Of($"::ffff:{Claimed}%\", {Caller}")
            .Should().Be($"{StrictForwardedFor.NotAnAddress}, {Caller}");
        StrictForwardedFor.Of($"\"{Claimed}, {Caller}\"")
            .Should().Be($"{StrictForwardedFor.NotAnAddress}, {StrictForwardedFor.NotAnAddress}");
    }

    [Theory]
    [InlineData("198.51.100.200,203.0.113.9")]
    [InlineData(" 198.51.100.200 ,\t203.0.113.9\t")]
    [InlineData("198.51.100.200, , ,203.0.113.9,")]
    [InlineData(",198.51.100.200,,203.0.113.9")]
    public void BlanksTabsAndEmptyEntries_AreDropped(string line)
    {
        StrictForwardedFor.Of(line).Should().Be($"{Claimed}, {Caller}");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" , ,\t,")]
    public void ALineWithNoEntry_IsNoList(string line)
    {
        StrictForwardedFor.Of(line).Should().BeEmpty();
    }

    [Fact]
    public void SeveralLines_AreOneList_InTheirOrder()
    {
        StrictForwardedFor.Of(new StringValues(["192.0.2.1, " + Claimed, "", "\"", Caller]))
            .Should().Be($"192.0.2.1, {Claimed}, {StrictForwardedFor.NotAnAddress}, {Caller}");
        StrictForwardedFor.Of(StringValues.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Rewrite_PutsTheListInPlaceOfTheLines()
    {
        var headers = new HeaderDictionary
        {
            [StrictForwardedFor.HeaderName] = new StringValues([$"::ffff:{Claimed}%\"", Caller]),
            ["X-Real-IP"] = Claimed,
        };

        StrictForwardedFor.Rewrite(headers);

        headers[StrictForwardedFor.HeaderName].Should().Equal($"{StrictForwardedFor.NotAnAddress}, {Caller}");
        headers["X-Real-IP"].Should().Equal(Claimed);
    }

    [Fact]
    public void Rewrite_RemovesAHeaderWithNoEntry_AndAddsNoneWhereThereWasNone()
    {
        var empty = new HeaderDictionary { [StrictForwardedFor.HeaderName] = " , ," };
        var none = new HeaderDictionary { ["X-Real-IP"] = Claimed };

        StrictForwardedFor.Rewrite(empty);
        StrictForwardedFor.Rewrite(none);

        empty.Should().BeEmpty();
        none.Keys.Should().Equal("X-Real-IP");
    }

    [Fact]
    public void TheHeaderIsTheOneTheFrameworksMiddlewareReads()
    {
        // The framework's name is a property, so the constant cannot be it: held equal here, to
        // the default and to what a middleware's options start with. Program.cs sets no other.
        StrictForwardedFor.HeaderName.Should().Be(ForwardedHeadersDefaults.XForwardedForHeaderName)
            .And.Be(new ForwardedHeadersOptions().ForwardedForHeaderName);
    }
}
