using AzureBank.Api.Observability;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Observability;

/// <summary>
/// Unit tests for <see cref="EmailMaskingRedactor"/> — the masking contract that keeps
/// raw emails out of exported logs (OTLP/Loki). Exercised through the base class's
/// string overload, which is the exact path production call sites use
/// (<c>Redactor.Redact(string)</c> sizes the buffer via GetRedactedLength, then calls
/// the span-based Redact — so these tests cover both overrides and their agreement).
/// </summary>
public class EmailMaskingRedactorTests
{
    private readonly EmailMaskingRedactor _sut = new();

    [Theory]
    // Well-formed emails: first char of the local part + "***" + full domain.
    [InlineData("john@example.com", "j***@example.com")]
    [InlineData("j@x.io", "j***@x.io")]
    [InlineData("first.last+tag@sub.domain.co.uk", "f***@sub.domain.co.uk")]
    // The tail is echoed VERBATIM, so anything not provably ONE well-formed address
    // collapses to the bare mask: empty local part or domain, a second '@' (could embed
    // another address), or malformed input generally.
    [InlineData("@example.com", "***")]
    [InlineData("john@", "***")]
    [InlineData("a@b@c.com", "***")]
    [InlineData("not-an-email", "***")]
    [InlineData("   ", "***")]
    // Empty short-circuits in the BASE string overload (before our override runs) and
    // stays empty — nothing to leak, so that framework behaviour is fine; pinned here.
    [InlineData("", "")]
    public void Redact_MasksEmailToExpectedShape(string input, string expected)
    {
        _sut.Redact(input).Should().Be(expected);
    }

    [Theory]
    // A redactor is a trust boundary: a crafted "email" whose kept tail smuggles
    // control/format/whitespace characters would hand log forging a path straight
    // through the PII defence. Any such character anywhere -> the bare mask.
    [InlineData("a@b\r\n[WARN] forged")]
    [InlineData("user name@example.com")]
    [InlineData("user@exam\tple.com")]
    [InlineData("user@example.com\n[ERROR] fake")]
    public void Redact_UnsafeCharacters_CollapseToFullMask(string input)
    {
        var result = _sut.Redact(input);

        result.Should().Be("***", "the verbatim tail must never carry forgeable characters");
    }

    [Fact]
    public void Redact_NeverEchoesTheLocalPart()
    {
        // The property that matters for PII: the identifying local part must not survive.
        _sut.Redact("sensitive.person@example.com").Should().NotContain("sensitive");
    }

    [Fact]
    public void GetRedactedLength_MatchesActualOutputLength()
    {
        // The base class sizes destination buffers off GetRedactedLength before calling
        // the span-based Redact — a mismatch would corrupt or truncate the masked value.
        // Compared at span level so the base string overload's empty short-circuit
        // doesn't hide a disagreement between the two overrides.
        foreach (var input in new[] { "john@example.com", "@x.io", "garbage", "" })
        {
            var destination = new char[64];
            var written = _sut.Redact(input.AsSpan(), destination);
            _sut.GetRedactedLength(input).Should().Be(written, $"input was '{input}'");
        }
    }

    [Fact]
    public void Redact_DestinationTooSmall_ThrowsInsteadOfTruncating()
    {
        // A truncated mask could silently drop the domain an operator relies on; failing
        // loudly is the safe behaviour for a direct span-based caller.
        var act = () =>
        {
            var destination = new char[3]; // "j***@example.com" needs 16
            return _sut.Redact("john@example.com".AsSpan(), destination);
        };

        act.Should().Throw<ArgumentException>();
    }
}
