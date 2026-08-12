using System.Text.RegularExpressions;
using Serilog.Context;

namespace AzureBank.Api.Middleware;

/// <summary>
/// Middleware that ensures every request has a correlation ID for tracing.
/// Reads from X-Correlation-ID header or generates a new one.
/// Adds the correlation ID to the response headers and Serilog context.
///
/// <para>
/// Normally the BFF has already put one here and YARP forwarded it, so this adopts it and the two
/// services label the request identically (SCA-RTS Art. 29(2)(a)). The API is directly reachable
/// though, so it validates for itself rather than trusting the edge.
/// </para>
/// </summary>
public partial class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private const int MaxLength = 64;

    /// <summary>
    /// An ALLOW-list, matching the BFF's. The header is caller-controlled and ends up in exported
    /// logs and in a response header — CWE-117 log forging and header injection. Accepting only an
    /// id-shaped value means nothing downstream has to escape it; anything else is replaced with a
    /// fresh id rather than rejected, since a malformed trace hint is not worth failing a request over.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex AcceptableId();

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Adopt the caller's id when it is id-shaped; otherwise mint one.
        var supplied = context.Request.Headers[CorrelationIdHeader].FirstOrDefault();
        var correlationId = supplied is { Length: <= MaxLength } && AcceptableId().IsMatch(supplied)
            ? supplied
            : Guid.NewGuid().ToString();

        // Store in HttpContext items for access throughout request
        context.Items["CorrelationId"] = correlationId;

        // Add to response headers
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationIdHeader] = correlationId;
            return Task.CompletedTask;
        });

        // Push to Serilog context for structured logging
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}

/// <summary>
/// Extension methods for CorrelationIdMiddleware.
/// </summary>
public static class CorrelationIdMiddlewareExtensions
{
    /// <summary>
    /// Adds the correlation ID middleware to the pipeline.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<CorrelationIdMiddleware>();
    }
}
