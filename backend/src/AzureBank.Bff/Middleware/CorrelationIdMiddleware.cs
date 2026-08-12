using System.Text.RegularExpressions;
using Serilog.Context;

namespace AzureBank.Bff.Middleware;

/// <summary>
/// Gives every request a correlation id and makes sure the SAME one reaches the API.
///
/// <para>
/// The API has had this since the start; the BFF had nothing, so the two services could only be
/// joined through W3C <c>traceparent</c>, and a correlation id supplied by the browser never
/// survived the proxy hop. That gap has a name in the rulebook: <b>SCA-RTS Art. 29(2)(a)</b> requires
/// "a unique identifier of the session", and 29(2)(b) detailed logging of the transaction "in all
/// the various stages" — which is not satisfiable if the edge and the API label the same request
/// differently.
/// </para>
///
/// <para>
/// Writing it back onto <c>Request.Headers</c> is the part that does the work: YARP forwards request
/// headers as it proxies, so the API's own <c>CorrelationIdMiddleware</c> finds one already there
/// and adopts it instead of minting a second.
/// </para>
/// </summary>
public partial class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-ID";

    /// <summary>Bounded so a hostile value cannot bloat every log line for the request.</summary>
    private const int MaxLength = 64;

    /// <summary>
    /// An ALLOW-list, not a sanitizer. This value is attacker-controlled, is pushed into logs that
    /// are exported, and is echoed back in a response header — the two places CWE-117 log forging
    /// and header injection live. Stripping bad characters would keep an arbitrary caller-chosen
    /// string; refusing anything that is not id-shaped keeps the guarantee simple: what reaches a
    /// sink here is always <c>[A-Za-z0-9._-]{1,64}</c>, which needs no escaping anywhere downstream.
    /// A rejected value is not an error — the request simply gets a fresh id, like one with no header.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex AcceptableId();

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[CorrelationIdHeader].FirstOrDefault();

        var correlationId = supplied is { Length: <= MaxLength } && AcceptableId().IsMatch(supplied)
            ? supplied
            : Guid.NewGuid().ToString();

        // Overwrite rather than append: a request carrying two of these must not leave the BFF
        // ambiguous, and the value below is the one every downstream log line will show.
        context.Request.Headers[CorrelationIdHeader] = correlationId;
        context.Items["CorrelationId"] = correlationId;

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationIdHeader] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}

/// <summary>Extension methods for <see cref="CorrelationIdMiddleware"/>.</summary>
public static class CorrelationIdMiddlewareExtensions
{
    /// <summary>
    /// Adds the correlation-id middleware. Register it FIRST: everything after it — request
    /// logging, the security-header, Fetch-Metadata and step-up refusals, the proxy — should be
    /// able to name the request it is talking about.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder builder)
        => builder.UseMiddleware<CorrelationIdMiddleware>();
}
