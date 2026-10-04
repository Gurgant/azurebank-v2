using System.Text;
using AzureBank.Bff.Options;
using AzureBank.Shared.Options;
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
    /// The tag the page carries on the public demo (<c>Demo:Enabled</c>): how the application,
    /// which is one build for every deployment, learns before its first request that this one is
    /// the demo. With the demo off the page is the file, and has no such tag.
    /// </summary>
    internal const string DemoTag = "<meta name=\"azurebank-demo\" content=\"true\">";

    private static ReadOnlySpan<byte> HeadEnd => "</head>"u8;

    /// <summary>
    /// Adds the static files and the shell fallback. Call it after the security headers, so both
    /// carry them; it does nothing when <c>Spa:RootPath</c> is unset.
    /// </summary>
    /// <remarks>
    /// On the public demo the page is the shell with <see cref="DemoTag"/> in its head, wherever
    /// the shell is served: for a navigation, and for <c>/index.html</c> by its name. The file is
    /// read once, here, and a shell the tag cannot be put in stops the host
    /// (<see cref="WithDemoTag"/>).
    /// </remarks>
    public static WebApplication UseSpaHosting(this WebApplication app)
    {
        var root = app.Services.GetRequiredService<IOptions<SpaOptions>>().Value
            .ResolveRoot(app.Environment.ContentRootPath);
        if (root is null)
        {
            return app;
        }

        var files = new PhysicalFileProvider(root);

        // Reading SpaOptions above ran its validator, so the file is there.
        var indexPath = Path.Combine(root, "index.html");
        var demoPage = app.Services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled
            ? WithDemoTag(File.ReadAllBytes(indexPath), indexPath)
            : null;

        if (demoPage is not null)
        {
            /*
              /index.html BY ITS NAME, before the static files: they would answer it with the
              file's own bytes, without the tag, and the fallback below never sees it, since a
              path with an extension is not a navigation. Any spelling of the name: on a file
              system that ignores case the static files would answer "/INDEX.HTML" too.
            */
            app.Use(async (context, next) =>
            {
                if (IsTheShellByName(context.Request))
                {
                    await WriteDemoPageAsync(context, demoPage);
                    return;
                }

                await next();
            });
        }

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
                if (demoPage is not null)
                {
                    await WriteDemoPageAsync(context, demoPage);
                    return;
                }

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
    /// The shell's bytes with <see cref="DemoTag"/> put right before its first
    /// <c>&lt;/head&gt;</c>, in upper or lower case, and no other byte changed.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The shell has no <c>&lt;/head&gt;</c>. A page served without the tag would look, to the
    /// application and to whoever reads the log, like a deployment with the demo off, so the host
    /// stops instead, as it does for a <c>Spa:RootPath</c> with no <c>index.html</c>.
    /// </exception>
    internal static byte[] WithDemoTag(byte[] shell, string indexPath)
    {
        var at = IndexOfHeadEnd(shell);
        if (at < 0)
        {
            throw new InvalidOperationException(
                $"Demo:Enabled is true and '{indexPath}' has no </head> to put the demo's tag before. " +
                "Build the SPA (npm run build), whose index.html has a head, or set Demo:Enabled to false.");
        }

        var tag = Encoding.UTF8.GetBytes(DemoTag);
        var tagged = new byte[shell.Length + tag.Length];
        shell.AsSpan(0, at).CopyTo(tagged);
        tag.CopyTo(tagged.AsSpan(at));
        shell.AsSpan(at).CopyTo(tagged.AsSpan(at + tag.Length));
        return tagged;
    }

    private static int IndexOfHeadEnd(ReadOnlySpan<byte> shell)
    {
        for (var at = 0; at + HeadEnd.Length <= shell.Length; at++)
        {
            if (Ascii.EqualsIgnoreCase(shell.Slice(at, HeadEnd.Length), HeadEnd))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>
    /// Writes the tagged shell with the headers the shell has: its content type, and
    /// <c>no-cache</c>. Its length too, and no body for a HEAD.
    /// </summary>
    private static async Task WriteDemoPageAsync(HttpContext context, byte[] page)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        // The shell names the fingerprinted bundle, so it must never be cached past a deploy.
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentLength = page.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(page, context.RequestAborted);
        }
    }

    /// <summary>A GET or a HEAD of <c>/index.html</c>, the shell asked for by its file name.</summary>
    private static bool IsTheShellByName(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A page navigation: GET or HEAD, outside the server's prefixes, and not a file name — a missing
    /// <c>/assets/x.js</c> must stay a 404 rather than come back as HTML the browser tries to run.
    /// </summary>
    private static bool ServesShell(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && !ServerPrefixes.Any(prefix => request.Path.StartsWithSegments(prefix))
        && !Path.HasExtension(request.Path.Value);
}
