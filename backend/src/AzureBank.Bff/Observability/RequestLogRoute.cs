using Microsoft.AspNetCore.Diagnostics;
using Serilog.Events;

namespace AzureBank.Bff.Observability;

/// <summary>
/// What the request log names in place of the path: the route PATTERN that matched, which is a
/// constant of the code, where the path is a value the client chose. The BFF's copy of the API's
/// helper, as <c>CorrelationIdMiddleware</c> is; the two hosts share no web project.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-09-14 the request line was <c>HTTP {RequestMethod} {RequestPath} responded ...</c>,
/// and a proxied <c>GET /api/users/{azureTag}</c> put a handle in every one of its lines, which
/// ADR-0017's log-identifier rule forbids and <c>LogPlaceholderClassTests</c> could not see, since
/// a path is a template's VALUE. Here the pattern is YARP's route match — the line reads
/// <c>HTTP GET /api/users/{**catch-all} responded 200</c>; the API's own line names the endpoint.
/// </para>
/// <para>
/// The PROPERTY SET is replaced, not only the sentence: Serilog's middleware attaches
/// <c>RequestPath</c> to the event whether or not the template names it, and the OpenTelemetry
/// sink exports every property. <see cref="MessageTemplateProperties"/> is the whole set.
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
    /// The resource class a request addresses, as one of five constants: the first segment after
    /// <c>/api/</c> when it names a controller the API has, else <c>other</c>. YARP's catch-all
    /// pattern is the same for the ledger and the reveal endpoint, so a security line naming only
    /// the pattern lost what a probe hit; this gives it back without a value that could be a
    /// person's -- the set is fixed here, not read from the path.
    /// </summary>
    public static string ResourceOf(HttpContext httpContext)
    {
        var path = httpContext.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return "other";
        }

        var segment = path[5..].Split('/', 2)[0].ToLowerInvariant();
        return Resources.Contains(segment) ? segment : "other";
    }

    private static readonly HashSet<string> Resources = new(StringComparer.Ordinal)
    {
        "accounts", "auth", "transfers", "users",
    };

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
