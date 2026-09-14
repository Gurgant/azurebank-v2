using Serilog.Core;
using Serilog.Events;

namespace AzureBank.Api.Observability;

/// <summary>
/// Strips ASP.NET Core's <c>RequestPath</c> scope property from EVERY event the host writes during
/// a request, and names the route pattern in its place.
/// </summary>
/// <remarks>
/// <para>
/// Found while closing the request line (<see cref="RequestLogRoute"/>), by measurement: with the
/// line routed through the host logger its properties read
/// <c>RequestId="0HNOIG7UQS63L" | RequestPath="/api/users/janesmith"</c> — neither of them from the
/// template. They are the hosting scope <c>HostingApplication</c> opens around each request, which
/// the Serilog logging provider attaches to every event written inside it. So a warning logged
/// while serving <c>GET /api/users/{azureTag}</c> carried the handle as a property whatever its
/// template said, and the OpenTelemetry sink exports every property as an attribute. ADR-0017's
/// log-identifier rule is about the event, not the sentence; this enricher applies it to the event.
/// </para>
/// <para>
/// It runs in the pipeline AFTER <c>FromLogContext</c>: the logging provider pushes the hosting
/// scope into Serilog's <c>LogContext</c>, and <c>FromLogContext</c> (appsettings, read by
/// <c>ReadFrom.Configuration</c>) is what carries it onto every event, including the ones written
/// straight to <c>Serilog.ILogger</c>; <c>ReadFrom.Services</c> adds this enricher after it, which
/// is what makes the removal stick. Program.cs says so where the two calls sit. <c>RequestId</c> stays: an opaque per-request id (on Kestrel, the connection id plus a
/// counter), operational.
/// <c>RequestLogRouteTests</c> reads every event of a request and finds no path in any of them.
/// </para>
/// </remarks>
public sealed class RequestPathEnricher(IHttpContextAccessor httpContextAccessor) : ILogEventEnricher
{
    /// <summary>The hosting scope's property that never reaches a sink.</summary>
    public const string Removed = "RequestPath";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        logEvent.RemovePropertyIfPresent(Removed);
        if (httpContextAccessor.HttpContext is { } httpContext)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("RoutePattern", RequestLogRoute.Of(httpContext)));
        }
    }
}
