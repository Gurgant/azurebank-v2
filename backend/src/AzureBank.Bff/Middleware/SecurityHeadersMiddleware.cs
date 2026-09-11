namespace AzureBank.Bff.Middleware;

/// <summary>
/// Middleware that adds security headers to all responses.
/// Implements OWASP recommended security headers.
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Prevent MIME type sniffing
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");

        // Prevent clickjacking
        context.Response.Headers.Append("X-Frame-Options", "DENY");

        // "0", not "1; mode=block", as the OWASP HTTP Headers cheat sheet recommends: it warns that
        // the filter this header turns on "can create XSS vulnerabilities in otherwise safe
        // websites". The protection is the CSP below, which allows no inline script (ADR-0054).
        context.Response.Headers.Append("X-XSS-Protection", "0");

        // Referrer policy
        context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

        // Permissions policy (disable sensitive features)
        context.Response.Headers.Append("Permissions-Policy",
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()");

        // Content Security Policy — ONE policy for every response, the SPA's pages included, and it
        // was measured against the built bundle served from here rather than written for it
        // (ADR-0054). No 'unsafe-inline' anywhere and no 'unsafe-eval'.
        //
        // The hash in style-src is SHA-256 of the EMPTY string. Griffel, Fluent's styling engine,
        // creates empty <style> elements and fills them through CSSOM insertRule, which CSP does not
        // inspect; the empty element itself is what 'self' alone refuses. Measured on the served
        // build: with style-src 'self' only, 99 style-src-elem violations in a six-page walk and a
        // Fluent button's border-radius fell from 10px to 0px; with this hash, none, and 10px. A
        // <style> with any content still does not match it, which is what 'unsafe-inline' allowed.
        context.Response.Headers.Append("Content-Security-Policy",
            "default-src 'self'; " +
            "script-src 'self'; " +
            "style-src 'self' 'sha256-47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU='; " +
            "img-src 'self' data:; " +
            "font-src 'self'; " +
            "connect-src 'self'; " +
            "object-src 'none'; " +
            "base-uri 'none'; " +
            "form-action 'self'; " +
            "frame-ancestors 'none';");

        await _next(context);
    }
}

/// <summary>
/// Extension methods for SecurityHeadersMiddleware.
/// </summary>
public static class SecurityHeadersMiddlewareExtensions
{
    /// <summary>
    /// Adds the security headers middleware to the pipeline.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
