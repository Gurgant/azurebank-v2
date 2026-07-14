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

        _logger.LogWarning(
            exception,
            "Domain exception: {ErrorCode} - {Message}",
            appException.ErrorCode,
            appException.Message);

        var problemDetails = new ProblemDetails
        {
            Status = appException.StatusCode,
            Title = GetTitleForStatusCode(appException.StatusCode),
            Detail = appException.Message,
            Type = $"https://httpstatuses.com/{appException.StatusCode}",
            Instance = httpContext.Request.Path
        };

        // Add error code extension
        problemDetails.Extensions["errorCode"] = appException.ErrorCode;

        // Add correlation ID
        problemDetails.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        // Add details if present (e.g., InsufficientFundsException details)
        if (appException.Details is { Count: > 0 })
        {
            foreach (var detail in appException.Details)
            {
                problemDetails.Extensions[detail.Key] = detail.Value;
            }
        }

        // Emit a standard Retry-After header when the exception advertises a
        // back-off (e.g. PIN lockout, HTTP 429) so generic clients and proxies
        // honor it (RFC 9110 §10.2.3), in addition to the ProblemDetails fields.
        if (appException.Details is not null
            && appException.Details.TryGetValue("retryAfterSeconds", out var retryAfter))
        {
            httpContext.Response.Headers.RetryAfter = Convert.ToInt32(retryAfter).ToString();
        }

        httpContext.Response.StatusCode = appException.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
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
        _ => "Error"
    };
}
