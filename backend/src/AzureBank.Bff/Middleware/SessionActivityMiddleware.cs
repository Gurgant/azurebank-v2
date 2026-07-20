using AzureBank.Bff.Options;
using AzureBank.Bff.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Middleware;

/// <summary>
/// Middleware that tracks session activity by updating LastActivity timestamp.
/// Must run early in the pipeline to accurately track all requests.
/// </summary>
public class SessionActivityMiddleware
{
    private readonly RequestDelegate _next;

    public SessionActivityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ISessionService sessionService,
        IOptions<BffSessionOptions> sessionOptions)
    {
        var cookieName = sessionOptions.Value.CookieName;

        if (context.Request.Cookies.TryGetValue(cookieName, out var sessionId)
            && !IsSessionStatusProbe(context.Request))
        {
            // Update activity timestamp for every authenticated request
            sessionService.UpdateActivity(sessionId);
        }

        await _next(context);
    }

    /// <summary>
    /// GET /bff/auth/session-status is the frontend's "check WITHOUT keeping alive"
    /// probe: if it refreshed LastActivity, any status poll would silently neutralize
    /// the inactivity timeout (ADR-0018). Every other cookie-bearing request — including
    /// /bff/auth/me, which is the deliberate "Stay signed in" refresher — still counts
    /// as activity.
    /// </summary>
    private static bool IsSessionStatusProbe(HttpRequest request)
    {
        return HttpMethods.IsGet(request.Method)
            && request.Path.Equals("/bff/auth/session-status", StringComparison.OrdinalIgnoreCase);
    }
}

public static class SessionActivityMiddlewareExtensions
{
    public static IApplicationBuilder UseSessionActivity(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SessionActivityMiddleware>();
    }
}
