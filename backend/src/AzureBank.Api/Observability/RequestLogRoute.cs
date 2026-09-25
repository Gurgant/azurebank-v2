using Microsoft.AspNetCore.Diagnostics;
using Serilog.Events;

namespace AzureBank.Api.Observability;

/// <summary>
/// What the request log names in place of the path: the route PATTERN that matched, which is a
/// constant of the code, where the path is a value the client chose.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-09-14 the request line was <c>HTTP {RequestMethod} {RequestPath} responded ...</c>,
/// and <c>GET /api/users/{azureTag}</c> put a handle in every one of its lines — measured,
/// <c>HTTP GET /api/users/janesmith responded 200</c> — which ADR-0017's log-identifier rule
/// forbids and <c>LogPlaceholderClassTests</c> could not see, since a path is a template's VALUE.
/// The line now reads <c>HTTP GET /api/users/{azureTag} responded 200</c>.
/// </para>
/// <para>
/// The PROPERTY SET is replaced, not only the sentence. Serilog's middleware attaches its default
/// properties — <c>RequestPath</c> among them — to the event whether or not the template names
/// them, and the OpenTelemetry sink exports every property as an attribute; so a template that
/// merely stopped mentioning the path would still have shipped it. <see cref="MessageTemplateProperties"/>
/// is the whole set, and <c>RequestPath</c> is not in it.
/// </para>
/// <para>
/// A request nothing routes has no pattern and logs <see cref="Unmatched"/>: its path is whatever
/// the client sent, which is the one thing this line must not carry.
/// </para>
/// </remarks>
public static class RequestLogRoute
{
    /// <summary>Printed for a request that matched no endpoint.</summary>
    public const string Unmatched = "(unmatched)";

    /// <summary>
    /// The matched endpoint's route pattern, with a leading slash, or <see cref="Unmatched"/>. The
    /// exception handler NULLS the endpoint before it runs its handlers, so a line written from one
    /// -- a domain refusal's warning, the request line of a refused request -- would read
    /// <see cref="Unmatched"/>; the feature the handler leaves behind still holds the endpoint.
    /// </summary>
    public static string Of(HttpContext httpContext) =>
        (httpContext.GetEndpoint() ?? httpContext.Features.Get<IExceptionHandlerFeature>()?.Endpoint)
            is RouteEndpoint { RoutePattern.RawText: { } pattern }
            ? "/" + pattern.TrimStart('/')
            : Unmatched;

    /// <summary>
    /// The request line's properties, for <c>RequestLoggingOptions.GetMessageTemplateProperties</c>:
    /// method, route pattern, status, elapsed. The path argument is deliberately unused.
    /// </summary>
    public static IEnumerable<LogEventProperty> MessageTemplateProperties(
        HttpContext httpContext, string requestPath, double elapsedMilliseconds, int statusCode) =>
    [
        new("RequestMethod", new ScalarValue(httpContext.Request.Method)),
        new("RoutePattern", new ScalarValue(Of(httpContext))),
        new("StatusCode", new ScalarValue(statusCode)),
        new("Elapsed", new ScalarValue(elapsedMilliseconds)),
    ];

    /// <summary>
    /// The request line's level, for <c>RequestLoggingOptions.GetLevel</c>: Serilog's own rule
    /// (Error for an exception or a status above 499, Information otherwise), except that a
    /// <c>/health</c> probe that passed is Verbose, below every configured floor, so its line is
    /// never written. A failing probe keeps Error.
    /// </summary>
    /// <remarks>
    /// Measured on the two containers running as Production (2026-09-25), before this: ten
    /// <c>/health/live</c> and ten <c>/health/ready</c> through the BFF wrote 20 request lines in
    /// the BFF's log and 10 in the API's, since the BFF's readiness asks the API's liveness. The
    /// traces already leave <c>/health</c> out (ADR-0016); this is the same rule for the log.
    /// </remarks>
    public static LogEventLevel LevelFor(HttpContext httpContext, double elapsedMilliseconds, Exception? exception) =>
        exception is not null || httpContext.Response.StatusCode > 499
            ? LogEventLevel.Error
            : httpContext.Request.Path.StartsWithSegments("/health")
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;
}
