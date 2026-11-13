using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AzureBank.Bff.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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

    /*
      The other road. The calls the BFF makes to the API itself (a sign-in, a PIN check, the read of
      /me, a renewal) are new requests on the "BackendApi" client, so nothing the middleware wrote
      on the browser's request reaches them: CorrelationIdHandler gives each the id. A refused
      sign-in is enough to see it, because what is under test is what the call carried, not what
      the API answered; and it opens no session, so nothing else of the BFF calls the API after it.
    */

    /// <summary>Stands in for the API on the BFF's own road and keeps the ids each call carried.</summary>
    private sealed class OwnRoad
    {
        private readonly ConcurrentQueue<(string Path, string[] Ids)> _calls = new();

        public List<string[]> IdsSentTo(string path) =>
            _calls.Where(call => call.Path == path).Select(call => call.Ids).ToList();

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            string[] ids = request.Headers.TryGetValues(Header, out var values) ? [.. values] : [];
            _calls.Enqueue((request.RequestUri!.AbsolutePath, ids));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"status":401,"title":"Unauthorized","errorCode":"INVALID_CREDENTIALS"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private WebApplicationFactory<Program> HostOver(OwnRoad api) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddHttpClient("BackendApi").ConfigurePrimaryHttpMessageHandler(
                    () => new FakeBackendApiHandler(api.Respond))));

    private static HttpRequestMessage SignIn(string? correlationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/bff/auth/login")
        {
            Content = JsonContent.Create(new { email = "someone@example.com", password = "Password123!" }),
        };
        if (correlationId is not null)
        {
            request.Headers.TryAddWithoutValidation(Header, correlationId);
        }
        return request;
    }

    [Fact]
    public async Task TheBffsOwnCall_CarriesTheIdTheBffAdopted()
    {
        var api = new OwnRoad();
        using var host = HostOver(api);
        using var client = host.CreateClient();
        using var signIn = SignIn("bff-own-call-1");

        using var response = await client.SendAsync(signIn);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized, "the control: the sign-in reached the API and was answered");
        api.IdsSentTo("/api/auth/login").Should().ContainSingle()
            .Which.Should().Equal("bff-own-call-1");
    }

    [Fact]
    public async Task TheBffsOwnCall_CarriesTheIdTheBffMinted()
    {
        var api = new OwnRoad();
        using var host = HostOver(api);
        using var client = host.CreateClient();
        using var signIn = SignIn(null);

        using var response = await client.SendAsync(signIn);

        var minted = CorrelationOf(response);
        minted.Should().NotBeNullOrWhiteSpace("the control: the BFF names every request it answers");
        api.IdsSentTo("/api/auth/login").Should().ContainSingle()
            .Which.Should().Equal(minted!);
    }

    /*
      The handler alone, with no host. The grant revoker's workers and the session-stamp watcher
      start with the host and call the API outside any request: there is no id for them to carry,
      and the handler must not invent one. Run inside the middleware's next, the same handler sends
      that request's id, which is the control, and once the middleware has returned it sends none
      again: the id ends with its request.
    */
    [Fact]
    public async Task TheHandler_SendsNoIdOutsideARequest_AndTheRequestsIdInsideOne()
    {
        var sent = new List<string[]>();
        using var invoker = HandlerRecordingInto(sent);

        Task CallTheApi() => SendRevoke(invoker);

        await CallTheApi();

        var context = new DefaultHttpContext();
        context.Request.Headers[Header] = "bff-own-call-2";
        await new AzureBank.Bff.Middleware.CorrelationIdMiddleware(_ => CallTheApi()).InvokeAsync(context);

        await CallTheApi();

        sent.Should().HaveCount(3);
        sent[0].Should().BeEmpty("outside any request there is no id, and the handler invents none");
        sent[1].Should().Equal("bff-own-call-2");
        sent[2].Should().BeEmpty("the id ends with the request that settled on it");
    }

    /*
      The reason the id sits in an AsyncLocal and not on the request. A renewal that TokenRefresher
      starts with Task.Run takes the id with it and can still be sending when its request has
      ended. This drives a Task.Run of its own, not TokenRefresher: it holds the handler's reading
      of the ambient id, and the runtime carrying it into detached work. A renewal that stopped
      flowing the context (SuppressFlow, UnsafeQueueUserWorkItem) would need a test on the real
      renewal path to be seen. The gate keeps this call waiting until the middleware has returned,
      which is the control, and it must still go out under the id of the request that started it.
    */
    [Fact]
    public async Task ACallStartedWithTaskRun_GoesOutUnderItsRequestsId_AfterTheRequestHasEnded()
    {
        var sent = new List<string[]>();
        using var invoker = HandlerRecordingInto(sent);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = Task.CompletedTask;

        var context = new DefaultHttpContext();
        context.Request.Headers[Header] = "bff-own-call-3";
        await new AzureBank.Bff.Middleware.CorrelationIdMiddleware(_ =>
        {
            renewal = Task.Run(async () =>
            {
                await gate.Task;
                await SendRevoke(invoker);
            });
            return Task.CompletedTask;
        }).InvokeAsync(context);

        sent.Should().BeEmpty("the control: the request has ended and the renewal has not yet sent");

        gate.SetResult();
        await renewal;

        sent.Should().ContainSingle().Which.Should().Equal("bff-own-call-3");
    }

    /// <summary>The handler under test, in front of a stand-in API that keeps each call's ids.</summary>
    private static HttpMessageInvoker HandlerRecordingInto(List<string[]> sent) =>
        new(new CorrelationIdHandler
        {
            InnerHandler = new FakeBackendApiHandler(request =>
            {
                sent.Add(request.Headers.TryGetValues(Header, out var values) ? [.. values] : []);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
        });

    private static async Task SendRevoke(HttpMessageInvoker invoker)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://localhost:7215/api/auth/revoke");
        using var response = await invoker.SendAsync(request, CancellationToken.None);
    }
}
