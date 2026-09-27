using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AzureBank.Bff.Observability;
using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Serilog.Events;
using Yarp.ReverseProxy.Forwarder;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The BFF's request line names YARP's route pattern, never the proxied path, and carries no
/// property that does (ADR-0017's log-identifier rule, closed on the request log 2026-09-14).
/// </summary>
/// <remarks>
/// The sink is registered in DI, which the host's <c>ReadFrom.Services</c> picks up — the same
/// route the API's test factory uses — so the event read here is the one the real middleware wrote.
/// </remarks>
public class RequestLogRouteTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RequestLogRouteTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private sealed class QueueSink(ConcurrentQueue<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }

    private async Task<(LogEvent Line, HttpStatusCode Status, List<LogEvent> Events)> RequestLineOf(string path)
    {
        var events = new ConcurrentQueue<LogEvent>();
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(new QueueSink(events))));

        var response = await host.CreateClient().GetAsync(path);
        var lines = events
            .Where(e => e.MessageTemplate.Text.StartsWith("HTTP {RequestMethod} {RoutePattern} responded", StringComparison.Ordinal))
            .ToList();
        lines.Should().ContainSingle("one request, one request line");
        return (lines[0], response.StatusCode, events.ToList());
    }

    /// <summary>The line's template and its three named properties, read as values, not as a rendering.</summary>
    private static void IsTheRequestLine(LogEvent line, string routePattern, HttpStatusCode status)
    {
        line.MessageTemplate.Text.Should().Be(
            "HTTP {RequestMethod} {RoutePattern} responded {StatusCode} in {Elapsed:0.0000}ms");
        line.Properties["RequestMethod"].Should().Be(new ScalarValue("GET"));
        line.Properties["RoutePattern"].Should().Be(new ScalarValue(routePattern));
        line.Properties["StatusCode"].Should().Be(new ScalarValue((int)status));
    }

    /// <summary>
    /// Not one event of the request — the request line or any other — carries the path or the value:
    /// the hosting scope's RequestPath is stripped by <c>RequestPathEnricher</c> (measured on the
    /// API before it existed: <c>RequestPath="/api/users/janesmith"</c> on the request line).
    /// </summary>
    private static void NothingNamesTheValue(List<LogEvent> events, string value)
    {
        events.Should().NotBeEmpty();
        foreach (var e in events)
        {
            e.Properties.Should().NotContainKey(
                "RequestPath", $"the hosting scope's path is stripped from every event, and {e.MessageTemplate.Text} carries it");
            e.Properties.Select(p => $"{p.Key}={p.Value}").Should()
                .NotContain(p => p.Contains(value, StringComparison.Ordinal), "no property of any event carries the handle");
            e.RenderMessage().Should().NotContain(value);
            // The attached exception is a slot of its own: the OpenTelemetry sink exports it as
            // exception.message and exception.stacktrace, inner exceptions included.
            (e.Exception?.ToString() ?? string.Empty).Should().NotContain(value);
        }
    }

    [Fact]
    public async Task AProxiedRequest_LogsYarpsRoutePattern_NotThePath()
    {
        // No session, so the BFF answers before any upstream call; the line is written all the
        // same, and what matters is what it names. The status is whatever the host answered, read
        // back from the line rather than assumed here.
        var (line, status, events) = await RequestLineOf("/api/users/janesmith");

        // Observed through the host: 401 (no session), and the pattern is YARP's users route.
        IsTheRequestLine(line, "/api/users/{**catch-all}", status);
        NothingNamesTheValue(events, "janesmith");
        // The security line beside it names the resource class as a constant, so a probe on the
        // users lookup and one on the ledger no longer render identically under the catch-all.
        var refused = events.Single(e => e.MessageTemplate.Text.Contains("no session on proxied", StringComparison.Ordinal));
        refused.Properties["RoutePattern"].Should().Be(new ScalarValue("/api/users/{**catch-all}"));
        refused.Properties["Resource"].Should().Be(new ScalarValue("users"));
    }

    /// <summary>A stub upstream behind the REAL forwarder, so YARP's own log lines are written.</summary>
    private sealed class StubUpstream : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new FakeBackendApiHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            }));
    }

    private (WebApplicationFactory<Program> Host, ConcurrentQueue<LogEvent> Events) ProxyingHost(
        Action<IWebHostBuilder>? configure = null)
    {
        var events = new ConcurrentQueue<LogEvent>();
        var host = _factory.WithWebHostBuilder(builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILogEventSink>(new QueueSink(events));
                services.Replace(ServiceDescriptor.Singleton<IForwarderHttpClientFactory>(new StubUpstream()));
            });
        });
        return (host, events);
    }

    private static HttpRequestMessage WithSession(WebApplicationFactory<Program> host, string path)
    {
        var sessions = host.Services.GetRequiredService<ISessionService>();
        var sessionId = sessions.CreateSession(
            "fake-jwt",
            DateTime.UtcNow.AddHours(1),
            "fake-refresh",
            new UserLoginInfo
            {
                Id = Guid.NewGuid(),
                AzureTag = "johnsmith",
                Email = "john@example.com",
                FirstName = "John",
                LastName = "Smith",
                HasPin = true,
            });
        var cookie = host.Services.GetRequiredService<IOptions<BffSessionOptions>>().Value.CookieName;
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{cookie}={sessionId}");
        return request;
    }

    [Fact]
    public async Task AProxiedRequestWithASession_LeavesNoEventThatNamesTheHandle_YarpsForwarderIncluded()
    {
        /*
          THE CHANNEL NO TEMPLATE GUARD CAN SEE. Measured on the running stack, 2026-09-14, a
          lookup through the proxy with a session: YARP's own forwarder wrote
          "Proxying to https://localhost:7215/api/users/janesmith HTTP/2 RequestVersionOrLower" at
          Information, twice, and the BFF's console held the handle twice while every template of
          ours named the route pattern. appsettings.json now overrides Yarp.ReverseProxy.Forwarder
          (and System.Net.Http.HttpClient, which prints every upstream URL the BFF's own client
          calls) to Warning; the same lookup then left "HTTP GET /api/users/{**catch-all} responded
          200" and nothing else. This runs the REAL forwarder over a stub upstream, so that line is
          written here too — or would be. The control below puts the forwarder back and finds it.
        */
        var (host, events) = ProxyingHost();

        var response = await host.CreateClient().SendAsync(WithSession(host, "/api/users/janesmith"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the stub upstream answers 200, so the request went through the forwarder");
        var all = events.ToList();
        all.Should().Contain(
            e => e.MessageTemplate.Text.StartsWith("HTTP {RequestMethod} {RoutePattern} responded", StringComparison.Ordinal),
            "the request line is written");
        NothingNamesTheValue(all, "janesmith");
    }

    [Fact]
    public async Task TheControl_WithTheForwarderBackAtInformation_ItsOwnLineNamesTheHandle()
    {
        // A silence is a pass only when the event was due: same host, the override undone, and the
        // forwarder's line is there with the handle in it. If this goes red, the sink no longer sees
        // YARP's logger and the test above proves nothing.
        var (host, events) = ProxyingHost(builder =>
            builder.UseSetting("Serilog:MinimumLevel:Override:Yarp.ReverseProxy.Forwarder", "Information"));

        await host.CreateClient().SendAsync(WithSession(host, "/api/users/janesmith"));

        events.Should().Contain(
            e => e.RenderMessage().Contains("janesmith", StringComparison.Ordinal)
                && e.Properties.ContainsKey("SourceContext")
                && e.Properties["SourceContext"].ToString().Contains("Yarp.ReverseProxy.Forwarder", StringComparison.Ordinal),
            "the forwarder logs the destination URL at Information; that is the line the override silences");
    }

    [Fact]
    public async Task AnUnroutedRequest_LogsUnmatched()
    {
        var (line, status, events) = await RequestLineOf("/nowhere/janesmith");

        status.Should().Be(HttpStatusCode.NotFound);
        IsTheRequestLine(line, "(unmatched)", HttpStatusCode.NotFound);
        NothingNamesTheValue(events, "janesmith");
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")] // Degraded with no API behind it, and still 200
    public async Task AProbeThatPasses_WritesNoRequestLine_WhileTheNextRequestStillDoes(string probe)
    {
        // Measured on the two containers as Production before GetLevel was set: ten probes of each
        // wrote 20 request lines in the BFF's log.
        var events = new ConcurrentQueue<LogEvent>();
        using var host = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILogEventSink>(new QueueSink(events))));
        var client = host.CreateClient();

        (await client.GetAsync(probe)).StatusCode.Should().Be(HttpStatusCode.OK);
        // The control makes a line due in the same host.
        var control = await client.GetAsync("/bff/auth/me");

        var lines = events
            .Where(e => e.MessageTemplate.Text.StartsWith("HTTP {RequestMethod} {RoutePattern} responded", StringComparison.Ordinal))
            .ToList();
        lines.Should().ContainSingle("the probe wrote no line, and the control one");
        IsTheRequestLine(lines[0], "/bff/auth/me", control.StatusCode);
    }

    [Theory]
    [InlineData("/health/live", 200, false, LogEventLevel.Verbose)] // below every floor: not written
    [InlineData("/health/ready", 200, false, LogEventLevel.Verbose)]
    [InlineData("/health/ready", 503, false, LogEventLevel.Error)] // a failing probe still speaks
    [InlineData("/health/live", 200, true, LogEventLevel.Error)]
    [InlineData("/healthz", 200, false, LogEventLevel.Information)] // a path segment, not a prefix
    [InlineData("/health/typo", 404, false, LogEventLevel.Information)] // not a probe that passed
    [InlineData("/api/accounts", 401, false, LogEventLevel.Information)]
    [InlineData("/api/accounts", 500, false, LogEventLevel.Error)]
    public void LevelFor_IsSerilogsRule_ButAPassingProbeIsVerbose(string path, int status, bool threw, LogEventLevel level)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.StatusCode = status;

        RequestLogRoute.LevelFor(context, 1.0, threw ? new InvalidOperationException() : null).Should().Be(level);
    }
}
