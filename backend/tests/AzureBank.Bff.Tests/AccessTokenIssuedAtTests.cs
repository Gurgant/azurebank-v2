using System.Buffers.Text;
using System.Text;
using AzureBank.Bff.Services;
using FluentAssertions;

namespace AzureBank.Bff.Tests;

/// <summary>
/// When an access token was issued, as the BFF reads it for its renewal thresholds
/// (ADR-0057 §4.5): the token's own <c>iat</c>, and the moment of receipt for every token whose
/// <c>iat</c> cannot be used.
/// </summary>
/// <remarks>
/// The BFF renews a token at a share of its lifetime, expiry minus issue. A token it cannot read
/// must not stop a sign-in or a renewal, and must not give a lifetime of nonsense either: the
/// fallback is the moment the BFF received it. The suites that renew tokens hand in well-formed
/// ones, so the fallbacks for a payload with no <c>iat</c>, a payload that is not JSON and a
/// string that is not a token ran in no test.
/// </remarks>
public sealed class AccessTokenIssuedAtTests
{
    private static readonly DateTime ReceivedAt = new(2026, 10, 12, 9, 0, 30, DateTimeKind.Utc);
    private static readonly DateTime ExpiresAt = new(2026, 10, 12, 9, 15, 0, DateTimeKind.Utc);

    private static string Part(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    private static string Token(string payload) => Part("""{"alg":"none"}""") + "." + payload + ".signature";

    [Fact]
    public void ATokenWithAnIat_IsIssuedThen_AndNotWhenItWasReceived()
    {
        var issuedAt = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var iat = new DateTimeOffset(issuedAt).ToUnixTimeSeconds();

        var read = AccessTokenIssuedAt.Of(Token(Part($$"""{"sub":"u","iat":{{iat}}}""")), ExpiresAt, ReceivedAt);

        read.Should().Be(issuedAt);
    }

    [Theory]
    [InlineData("""{"sub":"u"}""", "no iat at all")]
    [InlineData("""{"sub":"u","iat":"1791795600"}""", "an iat that is a string")]
    [InlineData("""{"sub":"u","iat":1791795600.5}""", "an iat that is not a whole number")]
    [InlineData("""["iat",1791795600]""", "a payload that is not an object")]
    public void APayloadWithNoUsableIat_FallsBackToTheMomentOfReceipt(string payload, string because)
    {
        var read = AccessTokenIssuedAt.Of(Token(Part(payload)), ExpiresAt, ReceivedAt);

        read.Should().Be(ReceivedAt, because);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void AStringThatIsNotThreeParts_FallsBackToTheMomentOfReceipt_EvenWithAReadableIatInIt(int parts)
    {
        // The second part is a payload with a usable iat, so what refuses it is the count of parts.
        var issuedAt = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var payload = Part($$"""{"iat":{{new DateTimeOffset(issuedAt).ToUnixTimeSeconds()}}}""");
        var token = string.Join('.', Enumerable.Repeat(payload, parts));

        var read = AccessTokenIssuedAt.Of(token, ExpiresAt, ReceivedAt);

        read.Should().Be(ReceivedAt, $"a token has three parts and this has {parts}");
    }

    [Theory]
    [InlineData("!!!not base64url!!!", "a payload that is not base64url")]
    [InlineData("bm90IGpzb24", "a payload that decodes to text that is not JSON")]
    public void APayloadThatCannotBeRead_FallsBackToTheMomentOfReceipt_AndDoesNotThrow(string payload, string because)
    {
        var read = AccessTokenIssuedAt.Of(Token(payload), ExpiresAt, ReceivedAt);

        read.Should().Be(ReceivedAt, because);
    }

    [Fact]
    public void AnIatThatIsNotBeforeTheExpiry_IsNotBelieved()
    {
        // A lifetime of zero or less would put every threshold at the expiry itself.
        var iat = new DateTimeOffset(ExpiresAt).ToUnixTimeSeconds();

        var read = AccessTokenIssuedAt.Of(Token(Part($$"""{"iat":{{iat}}}""")), ExpiresAt, ReceivedAt);

        read.Should().Be(ReceivedAt);
    }
}
