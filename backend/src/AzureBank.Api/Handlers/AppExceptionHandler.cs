using System.Diagnostics;
using AzureBank.Shared.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Api.Handlers;

/// <summary>
/// Handles all AppException-derived exceptions and converts them to ProblemDetails.
/// Registered first in the exception handler chain (highest priority for domain exceptions).
/// </summary>
public class AppExceptionHandler : IExceptionHandler
{
    private readonly ILogger<AppExceptionHandler> _logger;

    public AppExceptionHandler(ILogger<AppExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not AppException appException)
            return false; // Let next handler deal with it

        // The code and the type, not the message and not the exception object: a domain
        // refusal's message names what it refused, and for a recipient lookup that is the typed
        // handle -- "Recipient with identifier 'janesmith' was not found." reached every sink as
        // {Message} and again as exception.message, the two slots a template guard cannot see
        // (ADR-0017's log-identifier rule; found by an adversarial pass, 2026-09-14). The client
        // still receives the message in the ProblemDetails below, which is where it belongs.
        _logger.LogWarning(
            "Domain exception: {ErrorCode} ({ExceptionType})",
            appException.ErrorCode,
            exception.GetType().Name);

        await WriteProblemAsync(
            httpContext, appException.StatusCode, appException.ErrorCode, appException.Message,
            appException.Details, cancellationToken);

        return true;
    }

    /// <summary>
    /// Writes a refusal as ProblemDetails with an <c>errorCode</c>: the one writer for every answer
    /// that names a reason, shared with <see cref="ServiceUnavailableExceptionHandler"/> so the
    /// outage 503 and a domain refusal cannot drift apart in shape.
    /// </summary>
    /// <remarks>
    /// A <c>retryAfterSeconds</c> detail is also written as the <c>Retry-After</c> header, and a 503
    /// is never cached (<c>Cache-Control: no-store</c>): it describes a moment, and a cache that kept
    /// it would answer "unavailable" after the service is back.
    /// </remarks>
    internal static Task WriteProblemAsync(
        HttpContext httpContext,
        int statusCode,
        string errorCode,
        string detail,
        IReadOnlyDictionary<string, object>? details,
        CancellationToken cancellationToken)
    {
        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = GetTitleForStatusCode(statusCode),
            Detail = detail,
            Type = $"https://httpstatuses.com/{statusCode}",
            Instance = httpContext.Request.Path
        };

        // Add error code extension
        problemDetails.Extensions["errorCode"] = errorCode;

        // Correlation id: bare 32-hex trace id (not the full W3C "00-…-01") so it pastes
        // straight into Tempo/Grafana search.
        problemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        // Add details if present (e.g., InsufficientFundsException details)
        if (details is { Count: > 0 })
        {
            foreach (var entry in details)
            {
                problemDetails.Extensions[entry.Key] = entry.Value;
            }
        }

        // Emit a standard Retry-After header when the exception advertises a
        // back-off (e.g. PIN lockout, HTTP 429) so generic clients and proxies
        // honor it (RFC 9110 §10.2.3), in addition to the ProblemDetails fields.
        if (details is not null && details.TryGetValue("retryAfterSeconds", out var retryAfter))
        {
            httpContext.Response.Headers.RetryAfter = Convert.ToInt32(retryAfter).ToString();
        }

        if (statusCode == StatusCodes.Status503ServiceUnavailable)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
        }

        httpContext.Response.StatusCode = statusCode;
        return httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
    }

    private static string GetTitleForStatusCode(int statusCode) => statusCode switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        413 => "Payload Too Large",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        503 => "Service Unavailable",
        _ => "Error"
    };
}
