using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Middleware;

/// <summary>
/// Middleware that checks AuthLevel for routes requiring step-up authentication.
/// Returns 403 with X-Auth-Level-Required header if PIN verification is needed.
/// </summary>
public class AuthLevelMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AuthLevelMiddleware> _logger;

    // Routes that require PIN (AuthLevel 2) - POST operations
    private static readonly HashSet<string> PinRequiredPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/transfers",
        "/api/transfers/internal"
    };

    // Path patterns that require PIN
    private static readonly string[] PinRequiredPrefixes =
    {
        "/api/accounts/" // For endpoints like /api/accounts/{id}/full-number
    };

    // Path suffixes that require PIN
    private static readonly string[] PinRequiredSuffixes =
    {
        "/full-number"
    };

    public AuthLevelMiddleware(RequestDelegate next, ILogger<AuthLevelMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ISessionService sessionService,
        IOptions<BffSessionOptions> sessionOptions)
    {
        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;

        // Check if this route requires AuthLevel 2 (PIN)
        if (RequiresPinVerification(path, method))
        {
            var cookieName = sessionOptions.Value.CookieName;

            if (context.Request.Cookies.TryGetValue(cookieName, out var sessionId))
            {
                var authLevel = sessionService.GetAuthLevel(sessionId);

                if (authLevel < 2)
                {
                    _logger.LogWarning(
                        "Access denied: AuthLevel {CurrentLevel} < 2 required for {Method} {Path}",
                        authLevel, method, path);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.Headers.Append("X-Auth-Level-Required", "2");
                    context.Response.Headers.Append("X-Auth-Level-Current", authLevel.ToString());

                    await context.Response.WriteAsJsonAsync(new
                    {
                        type = "STEP_UP_REQUIRED",
                        title = "PIN Verification Required",
                        detail = "This operation requires PIN verification",
                        requiredLevel = 2,
                        currentLevel = authLevel,
                        status = 403
                    });

                    return;
                }
            }
            else
            {
                // No session cookie - let the API handle 401
                _logger.LogDebug("No session cookie found for PIN-protected route {Path}", path);
            }
        }

        await _next(context);
    }

    private static bool RequiresPinVerification(string path, string method)
    {
        // POST to transfer endpoints requires PIN
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            if (PinRequiredPaths.Contains(path))
            {
                return true;
            }
        }

        // Check path suffixes that require PIN (any method)
        foreach (var suffix in PinRequiredSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                // Only if it's under a PIN-required prefix
                foreach (var prefix in PinRequiredPrefixes)
                {
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}

public static class AuthLevelMiddlewareExtensions
{
    public static IApplicationBuilder UseAuthLevelEnforcement(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<AuthLevelMiddleware>();
    }
}
