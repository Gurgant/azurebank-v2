using AzureBank.Bff.Options;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace AzureBank.Bff.Extensions;

/// <summary>
/// Serves the built SPA from this origin when <c>Spa:RootPath</c> is set (ADR-0054).
/// </summary>
public static class SpaHostingExtensions
{
    /// <summary>
    /// Path prefixes that belong to the server. A request under one that matched no endpoint is a
    /// 404, never the app shell: answering an unknown API route with 200 and a page of HTML would
    /// turn every client's typo into a parse error far from its cause.
    /// </summary>
    private static readonly PathString[] ServerPrefixes = ["/api", "/bff", "/health"];

    /// <summary>
    /// Adds the static files and the shell fallback. Call it after the security headers, so both
    /// carry them; it does nothing when <c>Spa:RootPath</c> is unset.
    /// </summary>
    public static WebApplication UseSpaHosting(this WebApplication app)
    {
        var root = app.Services.GetRequiredService<IOptions<SpaOptions>>().Value
            .ResolveRoot(app.Environment.ContentRootPath);
        if (root is null)
        {
            return app;
        }

        var files = new PhysicalFileProvider(root);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
                // Vite fingerprints everything under /assets, so a changed file is a new name and
                // the old one can be cached for good. Everything else keeps its name across builds
                // (theme-init.js, the icons), so it is revalidated rather than trusted.
                context.Context.Request.Path.StartsWithSegments("/assets")
                    ? "public, max-age=31536000, immutable"
                    : "no-cache",
        });

        var shell = files.GetFileInfo("index.html");

        /*
          THE SHELL FALLBACK IS A MIDDLEWARE, NOT MapFallbackToFile, and the difference is measured.
          With MapFallbackToFile in this place, GET /bff/auth/login — a POST-only route, 405 on main —
          answered 200 with this page, and so did GET /bff/nope and GET /health/nope: the fallback
          endpoint accepts a GET whenever no other endpoint does, method mismatch included. Here the
          shell is served only when routing selected NO endpoint and the path is not the server's,
          and the three answer 405, 404 and 404, as they do on main.
        */
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint() is null && ServesShell(context.Request))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                // The shell names the fingerprinted bundle, so it must never be cached past a deploy.
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.SendFileAsync(shell);
                return;
            }

            await next();
        });

        return app;
    }

    /// <summary>
    /// A page navigation: GET or HEAD, outside the server's prefixes, and not a file name — a missing
    /// <c>/assets/x.js</c> must stay a 404 rather than come back as HTML the browser tries to run.
    /// </summary>
    private static bool ServesShell(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && !ServerPrefixes.Any(prefix => request.Path.StartsWithSegments(prefix))
        && !Path.HasExtension(request.Path.Value);
}
