using System.Diagnostics;
using AzureBank.Shared.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace AzureBank.Bff.Middleware;

/// <summary>
/// Fetch-Metadata resource isolation (W3C Fetch Metadata Request Headers): browsers stamp
/// every request with <c>Sec-Fetch-Site</c>, so a state-changing request initiated from
/// another site is rejected here — before it can touch session activity, the rate
/// limiter, the auth controller, or the proxied API. This is the origin-level CSRF
/// backstop behind SameSite=Strict. An absent header allows the request: non-browser
/// clients (curl, server-to-server, tests) and pre-Fetch-Metadata browsers do not send
/// it, and for them the cookie-based session is not an ambient-authority risk.
/// </summary>
public class FetchMetadataMiddleware
{
    private const string SecFetchSiteHeader = "Sec-Fetch-Site";

    private readonly RequestDelegate _next;
    private readonly ILogger<FetchMetadataMiddleware> _logger;

    public FetchMetadataMiddleware(RequestDelegate next, ILogger<FetchMetadataMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsCrossSiteStateChange(context.Request))
        {
            // Header value, method and path are attacker-controlled — everything goes
            // through the one audited sanitizer (log-forging barrier, ADR-0017), even
            // though Kestrel already rejects non-token method names.
            _logger.LogWarning(
                "SecurityEvent {SecurityEvent}: cross-site {Method} to {Path} blocked (Sec-Fetch-Site: {Site})",
                "CrossSiteRequestBlocked",
                LogSanitizer.Sanitize(context.Request.Method),
                LogSanitizer.Sanitize(context.Request.Path.Value ?? string.Empty),
                LogSanitizer.Sanitize(context.Request.Headers[SecFetchSiteHeader].ToString()));

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = "Cross-site request blocked by Fetch-Metadata policy.",
                Type = "https://httpstatuses.com/403",
                Instance = context.Request.Path,
            };
            problem.Extensions["errorCode"] = "CROSS_SITE_REQUEST_BLOCKED";
            // Bare 32-hex trace id — pastes straight into Tempo/Grafana search.
            problem.Extensions["traceId"] =
                Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            await context.Response.WriteAsJsonAsync(problem);
            return;
        }

        await _next(context);
    }

    private static bool IsCrossSiteStateChange(HttpRequest request)
    {
        // Safe methods stay open: they change no state, and reads are already gated by
        // the session cookie's SameSite=Strict plus the API's own auth. HEAD is GET's
        // metadata twin (RFC 9110 §9.3.2).
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var site = request.Headers[SecFetchSiteHeader];
        if (StringValues.IsNullOrEmpty(site))
        {
            return false;
        }

        var value = site.ToString();
        // "same-origin" = our SPA; "none" = user-initiated (address bar, bookmark).
        // Everything else — "cross-site" AND "same-site" (a sibling subdomain is not
        // this host; the cookie is host-locked via __Host- outside Development) — is
        // rejected.
        return !string.Equals(value, "same-origin", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);
    }
}

public static class FetchMetadataMiddlewareExtensions
{
    public static IApplicationBuilder UseFetchMetadata(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<FetchMetadataMiddleware>();
    }
}
