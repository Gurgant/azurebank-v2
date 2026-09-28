using System.Diagnostics;
using System.Globalization;
using AzureBank.Shared.Constants;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Bff.Http;

/// <summary>
/// The 503 the BFF answers when the service, not the session, is failing (ADR-0057 §4.5, §4.7): a
/// renewal that could not be had while the held token has 5 s or less left, or the API refusing
/// this host's service key. The SPA stays signed in: it retries a read's 503, and shows a write's
/// as an error without retrying it. Without it, either case would reach the browser as a 401, which
/// the SPA reads as a sign-out.
/// </summary>
/// <remarks>
/// The API's own shape for a 503 (<c>AppExceptionHandler</c> over <c>ServiceUnavailableException</c>):
/// <c>SERVICE_UNAVAILABLE</c>, <c>retryAfterSeconds</c> and a <c>Retry-After</c> header, so the SPA
/// meets nothing new.
/// </remarks>
public static class ServiceUnavailable
{
    /// <summary>
    /// The <c>Retry-After</c> for a refused key: long enough not to hammer, short enough that a retry
    /// sees the fix. The SPA retries a read's 503 on its own schedule whatever this says
    /// (<c>problemBaseQuery.ts</c>).
    /// </summary>
    public const int KeyRefusalRetryAfterSeconds = 5;

    /// <summary>
    /// Sets <c>Retry-After</c> on <paramref name="context"/>'s response and returns the body.
    /// </summary>
    public static ProblemDetails Problem(HttpContext context, string detail, int retryAfterSeconds)
    {
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Service Unavailable",
            Detail = detail,
            Type = "https://httpstatuses.com/503",
            Instance = context.Request.Path,
        };
        problem.Extensions["errorCode"] = ErrorCodes.ServiceUnavailable;
        problem.Extensions["retryAfterSeconds"] = retryAfterSeconds;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        return problem;
    }

    /// <summary>
    /// Writes <see cref="Problem"/> as the whole response, for the proxy's transforms, which have no
    /// action result to return.
    /// </summary>
    public static Task WriteAsync(HttpContext context, string detail, int retryAfterSeconds)
    {
        var problem = Problem(context, detail, retryAfterSeconds);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        // Whatever the API's response declared no longer describes this body.
        context.Response.ContentLength = null;
        return context.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", context.RequestAborted);
    }
}
