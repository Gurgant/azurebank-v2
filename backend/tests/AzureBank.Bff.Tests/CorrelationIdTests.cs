using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The BFF gives every request a correlation id and forwards it, so the edge and the API label the
/// same request identically — <b>SCA-RTS Art. 29(2)(a)</b>, "a unique identifier of the session".
/// Before this the BFF had none at all: the two services could only be joined through W3C
/// <c>traceparent</c>, and a browser-supplied id never survived the proxy hop.
///
/// <para>
/// The backend API is not running here. That does not matter: the middleware is first in the
/// pipeline and sets the response header via <c>OnStarting</c>, so the header is present whatever
/// the request goes on to do — 400, 403, or a 502 from the dead upstream.
/// </para>
/// </summary>
public class CorrelationIdTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Header = "X-Correlation-ID";

    private readonly WebApplicationFactory<Program> _factory;

    public CorrelationIdTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static HttpRequestMessage Probe(string? correlationId)
    {
        // /health is unauthenticated and needs no upstream, so the response is about the header
        // and nothing else.
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        if (correlationId is not null)
        {
            request.Headers.TryAddWithoutValidation(Header, correlationId);
        }
        return request;
    }

    private static string? CorrelationOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues(Header, out var values) ? values.Single() : null;

    [Fact]
    public async Task WithNoHeader_OneIsGeneratedAndEchoed()
    {
        var response = await _factory.CreateClient().SendAsync(Probe(null));

        CorrelationOf(response).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task AnAcceptableSuppliedId_IsAdoptedUnchanged()
    {
        // The whole point: a caller that already has an id keeps it, so one request has one name
        // from the browser through the BFF to the API.
        const string supplied = "req-0123456789abcdef";

        var response = await _factory.CreateClient().SendAsync(Probe(supplied));

        CorrelationOf(response).Should().Be(supplied);
    }

    [Fact]
    public async Task TwoRequestsWithoutAnId_GetDifferentOnes()
    {
        var client = _factory.CreateClient();

        var first = CorrelationOf(await client.SendAsync(Probe(null)));
        var second = CorrelationOf(await client.SendAsync(Probe(null)));

        first.Should().NotBe(second, "a correlation id identifies ONE request");
    }

    [Theory]
    // CWE-117 log forging: the id is pushed into exported logs and echoed in a response header.
    [InlineData("bad value with spaces")]
    [InlineData("semi;colon")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("")]
    public async Task AnUnacceptableSuppliedId_IsReplaced_NotSanitised(string hostile)
    {
        var response = await _factory.CreateClient().SendAsync(Probe(hostile));

        var echoed = CorrelationOf(response);
        echoed.Should().NotBe(hostile, "a value that is not id-shaped is discarded, not cleaned up");
        echoed.Should().NotBeNullOrWhiteSpace("the request still gets an id of its own");
        // The guarantee the allow-list buys: whatever reaches a log or a header is id-shaped, so
        // nothing downstream has to escape it.
        echoed.Should().MatchRegex("^[A-Za-z0-9._-]{1,64}$");
    }

    [Fact]
    public async Task AUnicodeLineSeparator_IsReplaced()
    {
        // U+2028 is the interesting one for CWE-117: Kestrel rejects CR/LF in a header value, so a
        // log-forging attempt has to reach for a separator that passes header validation and still
        // starts a new line in plenty of log viewers. Built at runtime — C# treats this codepoint as
        // a line terminator in SOURCE, so it cannot be written in a literal, escape or not.
        var hostile = "inject" + (char)0x2028 + "newline";

        var response = await _factory.CreateClient().SendAsync(Probe(hostile));

        var echoed = CorrelationOf(response);
        echoed.Should().NotBe(hostile);
        echoed.Should().MatchRegex("^[A-Za-z0-9._-]{1,64}$");
    }

    [Fact]
    public async Task AnOverlongSuppliedId_IsReplaced()
    {
        // Shape-valid but unbounded: it would otherwise bloat every log line for the request.
        var tooLong = new string('a', 65);

        var response = await _factory.CreateClient().SendAsync(Probe(tooLong));

        CorrelationOf(response).Should().NotBe(tooLong);
    }

    /*
      The half that makes the two services agree — and the half a response-header assertion cannot
      see. The middleware writes the id back onto Request.Headers; YARP proxies request headers as
      they stand, so the API's own CorrelationIdMiddleware finds one already there and adopts it
      instead of minting a second.

      Driven directly rather than through the factory: what needs proving is a mutation of the
      REQUEST, which is consumed by the proxy and never appears in the response. Going through the
      pipeline would only let us assert the response header again — the thing we already assert
      above — and a test that cannot observe its own claim is worse than no test.
    */
    private static async Task<string?> RequestHeaderSeenByNext(string? supplied)
    {
        var context = new DefaultHttpContext();
        if (supplied is not null)
        {
            context.Request.Headers[Header] = supplied;
        }

        string? seen = null;
        var sut = new AzureBank.Bff.Middleware.CorrelationIdMiddleware(ctx =>
        {
            seen = ctx.Request.Headers[Header].FirstOrDefault();
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(context);
        return seen;
    }

    [Fact]
    public async Task TheGeneratedId_IsPlacedOnTheRequest_soYarpForwardsIt()
    {
        var seen = await RequestHeaderSeenByNext(null);

        seen.Should().NotBeNullOrWhiteSpace(
            "the API adopts an id it finds on the request; absent one, it would mint a second");
        seen.Should().MatchRegex("^[A-Za-z0-9._-]{1,64}$");
    }

    [Fact]
    public async Task AnAdoptedId_ReachesTheRequestUnchanged()
    {
        const string supplied = "req-abc123";

        (await RequestHeaderSeenByNext(supplied)).Should().Be(supplied);
    }

    [Fact]
    public async Task AHostileId_IsReplacedOnTheRequestToo_notJustInTheResponse()
    {
        // Otherwise the BFF would clean its own logs while handing the raw value to the API.
        var seen = await RequestHeaderSeenByNext("hostile value; with junk");

        seen.Should().NotBe("hostile value; with junk");
        seen.Should().MatchRegex("^[A-Za-z0-9._-]{1,64}$");
    }
}
