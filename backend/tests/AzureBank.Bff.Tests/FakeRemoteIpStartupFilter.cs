using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace AzureBank.Bff.Tests;

/// <summary>
/// TestServer does NOT populate <c>Connection.RemoteIpAddress</c> — it is null — so every
/// request would collapse into the rate limiter's single "unknown" partition and any per-IP
/// assertion would be vacuous (a limiter keyed on a constant would pass). This injects the
/// connection IP from an <c>X-Test-Client-Ip</c> header so the partitioning can actually be
/// tested.
///
/// It must be an <see cref="IStartupFilter"/>, not app.Use(...): that PREPENDS it ahead of
/// the app's own pipeline, so the IP is set before UseForwardedHeaders and the rate limiter
/// read it.
/// </summary>
public sealed class FakeRemoteIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Client-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var raw)
                && IPAddress.TryParse(raw.ToString(), out var ip))
            {
                context.Connection.RemoteIpAddress = ip;
            }

            await nextMiddleware();
        });

        next(app);
    };
}
