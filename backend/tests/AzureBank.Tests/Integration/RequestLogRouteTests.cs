using System.Net;
using System.Net.Http.Json;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Serilog.Events;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The request line names the route PATTERN, never the path, and carries no property that does
/// (ADR-0017's log-identifier rule, closed on the request log 2026-09-14).
/// </summary>
/// <remarks>
/// Its own factory per test, capturing from Information: the shared fixture captures Warning and
/// above, where the request line does not live. Read through the real host, so what is asserted is
/// what Serilog's middleware emitted with <c>RequestLogRoute.MessageTemplateProperties</c> in
/// place, not what the helper would return if asked.
/// </remarks>
public sealed class RequestLogRouteTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory = new();

    public RequestLogRouteTests()
    {
        _factory.CaptureLog(LogEventLevel.Information);
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>The one request line the host wrote for <paramref name="path"/>.</summary>
    private async Task<(LogEvent Line, HttpStatusCode Status)> RequestLineOf(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);
        var lines = _factory.CapturedEvents
            .Where(e => e.MessageTemplate.Text.StartsWith("HTTP {RequestMethod} {RoutePattern} responded", StringComparison.Ordinal))
            .ToList();
        lines.Should().ContainSingle("one request, one request line, written before the response completes");
        return (lines[0], response.StatusCode);
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
    /// Not one event of the request — the request line or any other — carries the path or the value.
    /// </summary>
    /// <remarks>
    /// Measured before <c>RequestPathEnricher</c> existed, with the request line already routed
    /// through the host logger: its properties read <c>RequestId="0HNOIG7UQS63L" |
    /// RequestPath="/api/users/janesmith"</c>, from the hosting scope, not from the template. That
    /// is the positive control this assertion stands on.
    /// </remarks>
    private void NothingNamesTheValue(string value)
    {
        var events = _factory.CapturedEvents.ToList();
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
    public async Task ARoutedRequest_LogsTheRoutePattern_NotThePath()
    {
        // Unauthenticated on purpose: the line is written for every response, and the question is
        // what it names, not whether the lookup was allowed. Before 2026-09-14 this line read
        // "HTTP GET /api/users/janesmith responded ..." on the running stack (ADR-0017).
        var (line, status) = await RequestLineOf("/api/users/janesmith");

        // Observed through the host: 401 (no bearer token), and the pattern is the controller's.
        IsTheRequestLine(line, "/api/users/{azureTag}", status);
        NothingNamesTheValue("janesmith");
    }

    [Fact]
    public async Task ARefusedRequest_LogsTheStatusTheHandlerWrote_AndTheHandlerLineNamesTheRoute()
    {
        /*
          TWO THINGS THE EXCEPTION HANDLER DID TO THE ROUTE LINES, both found by an adversarial pass
          on 2026-09-14 and both measured through the host before the fix. (1) With the request
          logging middleware INSIDE UseExceptionHandler, a domain refusal unwound through it as an
          exception and the request line read "HTTP POST /api/auth/login responded 500" at Error,
          exception attached, for a request the client saw answered 401 -- on the console for as
          long as the ordering stood, and exported the moment the line joined the host pipeline.
          (2) The handler middleware nulls the endpoint before its handlers run, so the handler's
          own warning carried RoutePattern="(unmatched)". The middleware sits outside the handler
          now, and RequestLogRoute reads the endpoint from the feature the handler preserves.
        */
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { email = "nobody@example.com", password = "Wrong-Password-1!" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an unknown account is a domain refusal, 401");

        var line = _factory.CapturedEvents.Single(e =>
            e.MessageTemplate.Text.StartsWith("HTTP {RequestMethod} {RoutePattern} responded", StringComparison.Ordinal));
        line.Properties["StatusCode"].Should().Be(new ScalarValue(401), "the status the handler wrote, not the 500 the exception implied");
        line.Level.Should().Be(LogEventLevel.Information, "a refusal is not an error");
        line.Exception.Should().BeNull();
        line.Properties["RoutePattern"].Should().Be(new ScalarValue("/api/auth/login"));

        var handled = _factory.CapturedEvents.Single(e =>
            e.MessageTemplate.Text.StartsWith("Domain exception: {ErrorCode}", StringComparison.Ordinal));
        handled.Properties["RoutePattern"].Should().Be(
            new ScalarValue("/api/auth/login"), "the handler runs after the endpoint was nulled; the feature still names it");
        // A domain refusal's message names what it refused -- a recipient lookup's, the typed handle
        // -- so the handler logs the code and the type, and attaches nothing.
        handled.Properties.Should().NotContainKey("Message");
        handled.Exception.Should().BeNull();
        NothingNamesTheValue("nobody@example.com");
    }

    [Fact]
    public async Task AnUnroutedRequest_LogsUnmatched_BecauseItsPathIsWhateverTheClientSent()
    {
        var (line, status) = await RequestLineOf("/api/nowhere/janesmith");

        status.Should().Be(HttpStatusCode.NotFound);
        IsTheRequestLine(line, "(unmatched)", HttpStatusCode.NotFound);
        NothingNamesTheValue("janesmith");
    }
}
