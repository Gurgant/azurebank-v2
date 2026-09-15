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
}
