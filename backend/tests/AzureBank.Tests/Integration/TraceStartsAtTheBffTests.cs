extern alias bff;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;
using BffSessionOptions = bff::AzureBank.Bff.Options.BffSessionOptions;
using BffSessions = bff::AzureBank.Bff.Services.Interfaces.ISessionService;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The trace of a request starts at the BFF: the id a browser names in <c>traceparent</c> is on no
/// audit row, in no answer and on no log line, and the id the BFF gives the request is the one
/// the API continues.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both hosts on loopback sockets.</b> The trace context reaches the API in a header that the
/// BFF's two real clients write as they send: the proxy's and the <c>BackendApi</c> one. A test
/// server's handler writes none, so <see cref="BffOverApiFactory"/> cannot show which id the API
/// is handed. Here the real BFF calls the real API over HTTP, as the deployment does.
/// </para>
/// <para>
/// <b>The browser is a client that writes no trace header of its own.</b> Once a host in this
/// process listens for client spans, <c>HttpClient</c> adds a <c>traceparent</c> to every request
/// it sends, and the control with no header would carry one. The handler below has that turned
/// off, so a request carries the trace headers a test gives it and no other.
/// </para>
/// </remarks>
public sealed class TraceStartsAtTheBffTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>How long <see cref="TraceIdsOnceAsync"/> waits for a request's log line.</summary>
    private static readonly TimeSpan LineWait = TimeSpan.FromSeconds(10);

    /// <summary>How both hosts' request line begins (<c>UseSerilogRequestLogging</c> in each Program.cs).</summary>
    private const string RequestLine = "HTTP {RequestMethod} {RoutePattern} responded";

    private const string Password = "SecurePass123!";
    private const string Pin = "824613";
    private const string DepositPath = "/api/transactions/deposit";
    private const string SetPinPath = "/bff/auth/set-pin";

    /// <summary>The trace id the browser names, and the span it says it comes from.</summary>
    private const string BrowsersTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string BrowsersSpanId = "b7ad6b7169203331";

    private readonly ApiOnASocket _api = new();
    private readonly BffOnASocket _bff;
    private readonly HttpClient _browser;

    public TraceStartsAtTheBffTests()
    {
        _api.CaptureLog(LogEventLevel.Information);
        _api.UseKestrel();
        _api.StartServer();

        _bff = new BffOnASocket(_api.Address);
        _bff.UseKestrel();
        _bff.StartServer();

        _browser = new HttpClient(new SocketsHttpHandler { ActivityHeadersPropagator = null, UseCookies = false })
        {
            BaseAddress = new Uri(_bff.Address),
        };
    }

    public void Dispose()
    {
        _browser.Dispose();
        _bff.Dispose();
        _api.Dispose();
    }

    /// <summary>Every trace header a caller can write: W3C's two, the baggage, and the two older names.</summary>
    private static void AddTraceHeadersOfTheBrowsersChoosing(HttpRequestMessage request, string flags = "01")
    {
        request.Headers.Add("traceparent", $"00-{BrowsersTraceId}-{BrowsersSpanId}-{flags}");
        request.Headers.Add("tracestate", "vendor=browser");
        request.Headers.Add("baggage", "who=browser");
        request.Headers.Add("Request-Id", $"|{BrowsersTraceId}.1.");
        request.Headers.Add("Correlation-Context", "who=browser");
    }

    [Theory]
    [InlineData("01")]
    [InlineData("00")]
    public async Task ADeposit_SentWithTraceHeadersOfTheBrowsersChoosing_IsAuditedUnderTheIdTheBffGaveIt(string flags)
    {
        var visitor = await SignInAsync();
        var accountId = await FirstAccountAsync(visitor);

        using var deposit = Deposit(visitor, accountId);
        AddTraceHeadersOfTheBrowsersChoosing(deposit, flags);
        using var answer = await _browser.SendAsync(deposit);
        answer.StatusCode.Should().Be(HttpStatusCode.Created, await answer.Content.ReadAsStringAsync());

        var atTheBff = _bff.Requests.Single(DepositPath);
        var atTheApi = _api.Requests.Single(DepositPath);
        var audited = await AuditedTraceIdAsync(visitor.UserId, SecurityEvents.MoneyDeposited);
        var apiLines = await TraceIdsOnceAsync(_api.CapturedEvents, atTheBff.TraceId);
        var bffLines = await TraceIdsOnceAsync(_bff.CapturedEvents, atTheBff.TraceId);

        using var all = new AssertionScope();
        atTheBff.TraceId.Should().NotBe(BrowsersTraceId, "the BFF starts the trace, whatever the browser names");
        audited.Should().NotBe(BrowsersTraceId, "a caller does not choose the id on its own audit rows");
        audited.Should().Be(atTheBff.TraceId, "the row carries the id the BFF gave the request");
        atTheApi.TraceId.Should().Be(atTheBff.TraceId, "one request has one id on both hosts");
        atTheApi.Traceparent.Should().ContainSingle()
            .Which.Should().MatchRegex($"^00-{atTheBff.TraceId}-[0-9a-f]{{16}}-01$",
                "the API is handed the BFF's trace, sampled by the BFF's own rule, whatever flag the browser wrote");
        atTheApi.Tracestate.Should().BeEmpty("the browser's trace state stops at the BFF");
        atTheApi.Baggage.Should().BeEmpty("the browser's baggage stops at the BFF");
        atTheApi.ActivityBaggage.Should().BeEmpty();
        apiLines.Should().Contain(atTheBff.TraceId, "the API's log lines carry the same id")
            .And.NotContain(BrowsersTraceId);
        bffLines.Should().Contain(atTheBff.TraceId, "the BFF's log lines carry the same id")
            .And.NotContain(BrowsersTraceId);
    }

    [Fact]
    public async Task APinSetOnTheBffsOwnRoad_WithTraceHeadersOfTheBrowsersChoosing_IsAuditedUnderTheIdTheBffGaveIt()
    {
        var visitor = await SignInAsync();

        using var setPin = WithSession(visitor, HttpMethod.Post, SetPinPath, new { pin = Pin, password = Password });
        AddTraceHeadersOfTheBrowsersChoosing(setPin);
        using var answer = await _browser.SendAsync(setPin);
        answer.StatusCode.Should().Be(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());

        var atTheBff = _bff.Requests.Single(SetPinPath);
        var atTheApi = _api.Requests.Single("/api/auth/pin");
        var audited = await AuditedTraceIdAsync(visitor.UserId, SecurityEvents.PinEnrolled);

        using var all = new AssertionScope();
        atTheBff.TraceId.Should().NotBe(BrowsersTraceId);
        audited.Should().NotBe(BrowsersTraceId, "a caller does not choose the id on its own audit rows");
        audited.Should().Be(atTheBff.TraceId, "the row carries the id the BFF gave the request");
        atTheApi.TraceId.Should().Be(atTheBff.TraceId, "one request has one id on both hosts");
        atTheApi.Traceparent.Should().ContainSingle()
            .Which.Should().MatchRegex($"^00-{atTheBff.TraceId}-[0-9a-f]{{16}}-01$");
        atTheApi.Tracestate.Should().BeEmpty("the browser's trace state stops at the BFF");
        atTheApi.Baggage.Should().BeEmpty("the browser's baggage stops at the BFF");
        atTheApi.ActivityBaggage.Should().BeEmpty();
    }

    [Fact]
    public async Task AnErrorAnswer_ToARequestWithTraceHeadersOfTheBrowsersChoosing_CarriesTheIdOfBothHostsLogLines()
    {
        var visitor = await SignInAsync();

        // The API's own answer: an account nobody holds.
        var missing = $"/api/accounts/{Guid.NewGuid()}";
        using var askForIt = WithSession(visitor, HttpMethod.Get, missing);
        AddTraceHeadersOfTheBrowsersChoosing(askForIt);
        using var notFound = await _browser.SendAsync(askForIt);
        var fromTheApi = await notFound.Content.ReadFromJsonAsync<JsonElement>();

        // The BFF's own answer: no session.
        using var withoutASession = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        AddTraceHeadersOfTheBrowsersChoosing(withoutASession);
        using var unauthorised = await _browser.SendAsync(withoutASession);
        var fromTheBff = await unauthorised.Content.ReadFromJsonAsync<JsonElement>();

        var atTheBff = _bff.Requests.Single(missing);
        var atTheApi = _api.Requests.Single(missing);
        var refusedAtTheBff = _bff.Requests.Single("/api/accounts");
        var apiLines = await TraceIdsOnceAsync(_api.CapturedEvents, atTheBff.TraceId);
        var bffLines = await TraceIdsOnceAsync(_bff.CapturedEvents, atTheBff.TraceId, refusedAtTheBff.TraceId);

        using var all = new AssertionScope();
        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fromTheApi.GetProperty("traceId").GetString().Should().NotBe(BrowsersTraceId)
            .And.Be(atTheBff.TraceId, "the id in the API's answer is the one the BFF gave the request")
            .And.Be(atTheApi.TraceId);
        apiLines.Should().Contain(atTheBff.TraceId, "the answer's id finds the API's log lines")
            .And.NotContain(BrowsersTraceId);

        unauthorised.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        fromTheBff.GetProperty("traceId").GetString().Should().NotBe(BrowsersTraceId)
            .And.Be(refusedAtTheBff.TraceId, "the id in the BFF's answer is the one it gave the request");
        _api.Requests.All("/api/accounts").Should().BeEmpty("the BFF refuses a request with no session itself");
        bffLines.Should().Contain([atTheBff.TraceId, refusedAtTheBff.TraceId])
            .And.NotContain(BrowsersTraceId);
    }

    [Fact]
    public async Task ARequestWithNoTraceHeader_HasOneIdOnBothHosts_OnTheAuditRowAndInTheAnswer()
    {
        var visitor = await SignInAsync();
        var accountId = await FirstAccountAsync(visitor);

        using var deposit = Deposit(visitor, accountId);
        using var created = await _browser.SendAsync(deposit);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        var missing = $"/api/accounts/{Guid.NewGuid()}";
        using var notFound = await _browser.SendAsync(WithSession(visitor, HttpMethod.Get, missing));
        var problem = await notFound.Content.ReadFromJsonAsync<JsonElement>();

        var depositAtTheBff = _bff.Requests.Single(DepositPath);
        var depositAtTheApi = _api.Requests.Single(DepositPath);
        var audited = await AuditedTraceIdAsync(visitor.UserId, SecurityEvents.MoneyDeposited);
        var apiLines = await TraceIdsOnceAsync(_api.CapturedEvents, depositAtTheBff.TraceId);
        var bffLines = await TraceIdsOnceAsync(_bff.CapturedEvents, depositAtTheBff.TraceId);

        using var all = new AssertionScope();
        depositAtTheBff.Traceparent.Should().BeEmpty("ARRANGE: the browser sent no trace header");
        depositAtTheBff.TraceId.Should().MatchRegex("^[0-9a-f]{32}$");
        depositAtTheApi.TraceId.Should().Be(depositAtTheBff.TraceId, "one request has one id on both hosts");
        depositAtTheApi.Traceparent.Should().ContainSingle()
            .Which.Should().MatchRegex($"^00-{depositAtTheBff.TraceId}-[0-9a-f]{{16}}-01$");
        audited.Should().Be(depositAtTheBff.TraceId);

        notFound.StatusCode.Should().Be(HttpStatusCode.NotFound);
        problem.GetProperty("traceId").GetString().Should().Be(_bff.Requests.Single(missing).TraceId)
            .And.Be(_api.Requests.Single(missing).TraceId)
            .And.NotBe(depositAtTheBff.TraceId, "each request has an id of its own");
        apiLines.Should().Contain(depositAtTheBff.TraceId);
        bffLines.Should().Contain(depositAtTheBff.TraceId);
    }

    private sealed record Visitor(string Cookie, Guid UserId);

    private async Task<Visitor> SignInAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"trace{unique}@example.com";
        using var apiClient = _api.CreateClient();
        using var registration = await apiClient.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            AzureTag = $"trace_{unique}",
            Email = email,
            Password = Password,
            FirstName = "Trace",
            LastName = "Holder",
        }, Json);
        registration.StatusCode.Should().Be(HttpStatusCode.Created,
            $"ARRANGE: the registration must work (body: {await registration.Content.ReadAsStringAsync()})");

        using var signIn = await _browser.PostAsJsonAsync("/bff/auth/login",
            new LoginRequest { Email = email, Password = Password }, Json);
        signIn.StatusCode.Should().Be(HttpStatusCode.OK,
            $"ARRANGE: the sign-in through the BFF must work (body: {await signIn.Content.ReadAsStringAsync()})");

        var cookieName = _bff.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var prefix = cookieName + "=";
        var sessionId = signIn.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..].Split(';')[0];
        var userId = _bff.Services.GetRequiredService<BffSessions>().GetSession(sessionId)!.UserId;
        return new Visitor($"{cookieName}={sessionId}", userId);
    }

    private static HttpRequestMessage WithSession(Visitor visitor, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", visitor.Cookie);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return request;
    }

    private static HttpRequestMessage Deposit(Visitor visitor, Guid accountId)
    {
        var request = WithSession(visitor, HttpMethod.Post, DepositPath,
            new { accountId, amount = 10.00m, description = "trace" });
        request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString());
        return request;
    }

    private async Task<Guid> FirstAccountAsync(Visitor visitor)
    {
        using var answer = await _browser.SendAsync(WithSession(visitor, HttpMethod.Get, "/api/accounts"));
        answer.StatusCode.Should().Be(HttpStatusCode.OK, "ARRANGE: registration opens an account");
        var body = await answer.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data")[0].GetProperty("id").GetGuid();
    }

    private async Task<string?> AuditedTraceIdAsync(Guid userId, string securityEvent)
    {
        using var scope = _api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var row = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.ActorUserId == userId && e.Event == securityEvent);
        return row.TraceId;
    }

    private static string[] TraceIdsOn(IEnumerable<LogEvent> events) =>
        [.. events.Where(e => e.TraceId is not null).Select(e => e.TraceId.ToString()!).Distinct()];

    /// <summary>
    /// <see cref="TraceIdsOn"/>, read once the host has written the request line of every id asked
    /// for: the last line a request writes, so every other line of that request is in by then. Both
    /// hosts write it as the request ends (<c>UseSerilogRequestLogging</c>), and over a socket the
    /// answer can reach this client first: read at once, the last request's line was missing on CI on
    /// 2026-10-14. A request line that has not come after <see cref="LineWait"/> fails here.
    /// </summary>
    private static async Task<string[]> TraceIdsOnceAsync(IEnumerable<LogEvent> events, params string?[] traceIds)
    {
        var waited = Stopwatch.StartNew();
        while (!traceIds.All(id => id is not null && RequestLineIdsOn(events).Contains(id))
            && waited.Elapsed < LineWait)
        {
            await Task.Delay(20);
        }

        RequestLineIdsOn(events).Should().Contain(
            traceIds.Select(id => id ?? "(no trace id)"), "a host writes a request's line when the request ends");
        return TraceIdsOn(events);
    }

    private static string[] RequestLineIdsOn(IEnumerable<LogEvent> events) =>
        TraceIdsOn(events.Where(e => e.MessageTemplate.Text.StartsWith(RequestLine, StringComparison.Ordinal)));

    /// <summary>What a host held for one request: the id of its trace, and the trace headers it was sent.</summary>
    private sealed record Seen(
        string Path, string? TraceId, string[] Traceparent, string[] Tracestate, string[] Baggage, string[] ActivityBaggage);

    /// <summary>Records each request as the host's own pipeline first sees it.</summary>
    private sealed class Recorder : IStartupFilter
    {
        private readonly ConcurrentQueue<Seen> _seen = new();

        public Seen Single(string path) => All(path).Should().ContainSingle($"one request for {path}").Subject;

        public Seen[] All(string path) => [.. _seen.Where(s => s.Path == path)];

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, following) =>
            {
                var activity = Activity.Current;
                _seen.Enqueue(new Seen(
                    context.Request.Path.Value!,
                    activity?.TraceId.ToString(),
                    [.. context.Request.Headers["traceparent"]!],
                    [.. context.Request.Headers["tracestate"]!],
                    [.. context.Request.Headers["baggage"]!],
                    [.. (activity?.Baggage ?? []).Select(item => $"{item.Key}={item.Value}")]));
                await following(context);
            });
            next(app);
        };
    }

    private static string AddressOf(IServiceProvider services) =>
        services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    /// <summary>The API on a loopback socket.</summary>
    private sealed class ApiOnASocket : CustomWebApplicationFactory
    {
        public Recorder Requests { get; } = new();

        public string Address => AddressOf(Services);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // The address is configuration, in place before the host is built: see
            // KestrelRequestSizeLimitTests for what a port assigned afterwards costs.
            builder.UseUrls("http://127.0.0.1:0");
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(Requests));
        }
    }

    /// <summary>The BFF on a loopback socket, both of its roads pointed at the API's socket.</summary>
    private sealed class BffOnASocket(string apiAddress) : WebApplicationFactory<bff::Program>
    {
        public Recorder Requests { get; } = new();

        public ConcurrentQueue<LogEvent> CapturedEvents { get; } = new();

        public string Address => AddressOf(Services);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing, not Development: see BffOverApiFactory.
            builder.UseEnvironment("Testing");
            builder.UseUrls("http://127.0.0.1:0");
            builder.UseSetting("ServiceCredential:BffKey", CustomWebApplicationFactory.ServiceCredentialKey);
            builder.UseSetting("BackendApi:BaseUrl", apiAddress);
            builder.UseSetting("ReverseProxy:Clusters:backend-api:Destinations:primary:Address", apiAddress);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(Requests);
                services.AddSingleton<ILogEventSink>(new QueueSink(CapturedEvents));
            });
        }

        private sealed class QueueSink(ConcurrentQueue<LogEvent> events) : ILogEventSink
        {
            public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
        }
    }
}
