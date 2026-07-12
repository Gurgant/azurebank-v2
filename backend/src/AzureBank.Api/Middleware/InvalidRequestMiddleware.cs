using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;

namespace AzureBank.Api.Middleware;

/// <summary>
/// Middleware that catches framework-level request validation errors and returns
/// consistent JSON error responses.
///
/// Purpose:
/// - Handles BadHttpRequestException thrown by Kestrel for invalid requests
/// - Catches invalid UTF-8 encoding in path parameters
/// - Returns structured JSON error instead of empty 400 response
/// - Fixes Schemathesis "Missing Content-Type header" and "JSON deserialization error"
///
/// This middleware MUST be registered early in the pipeline, before routing,
/// to catch exceptions that occur during request parsing.
/// </summary>
public class InvalidRequestMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<InvalidRequestMiddleware> _logger;

    public InvalidRequestMiddleware(RequestDelegate next, ILogger<InvalidRequestMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (BadHttpRequestException ex)
        {
            _logger.LogWarning(ex, "Bad HTTP request: {Message}", ex.Message);
            await HandleBadRequestAsync(context, ex);
        }
    }

    private static async Task HandleBadRequestAsync(HttpContext context, BadHttpRequestException ex)
    {
        // Ensure response hasn't started
        if (context.Response.HasStarted)
            return;

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";

        // Get trace ID for debugging
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        // Build RFC 7807 Problem Details response
        var problemDetails = new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            title = "Bad Request",
            status = 400,
            detail = GetUserFriendlyMessage(ex),
            instance = context.Request.Path.Value,
            traceId
        };

        await context.Response.WriteAsJsonAsync(problemDetails);
    }

    private static string GetUserFriendlyMessage(BadHttpRequestException ex)
    {
        // Provide user-friendly messages for common issues
        var message = ex.Message;

        if (message.Contains("invalid", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("utf", StringComparison.OrdinalIgnoreCase))
        {
            return "The request contains invalid UTF-8 encoding in the URL or headers.";
        }

        if (message.Contains("request", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("body", StringComparison.OrdinalIgnoreCase))
        {
            return "The request body is malformed or could not be parsed.";
        }

        // Default message
        return "The request is malformed or contains invalid characters.";
    }
}

/// <summary>
/// Extension methods for InvalidRequestMiddleware.
/// </summary>
public static class InvalidRequestMiddlewareExtensions
{
    /// <summary>
    /// Adds the invalid request handling middleware to the pipeline.
    /// IMPORTANT: Call this early in the pipeline, before UseRouting().
    /// </summary>
    public static IApplicationBuilder UseInvalidRequestHandling(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<InvalidRequestMiddleware>();
    }
}
