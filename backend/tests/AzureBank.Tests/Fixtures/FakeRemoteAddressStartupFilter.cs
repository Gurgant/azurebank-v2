using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Sets <c>Connection.RemoteIpAddress</c> from an <see cref="HeaderName"/> header, so a test can say
/// which address a request came from. <c>TestServer</c> has no socket and leaves it null.
/// </summary>
/// <remarks>
/// <para>
/// Written for the token road (06 §4.2, O2d): "from loopback" and "from anywhere else" are the two
/// cases the API must tell apart, and with a null address the test host can show neither. The BFF's
/// suite has the same seam for its rate limiter (<c>FakeRemoteIpStartupFilter</c>).
/// </para>
/// <para>
/// An <see cref="IStartupFilter"/>, so it runs ahead of the application's whole pipeline, the
/// ReceivedAt stamp included. A request without the header keeps the null address, which is what
/// every other test in this project sends.
/// </para>
/// </remarks>
public sealed class FakeRemoteAddressStartupFilter : IStartupFilter
{
    /// <summary>The header carrying the address the request should appear to come from.</summary>
    public const string HeaderName = "X-Test-Remote-Address";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var raw)
                && IPAddress.TryParse(raw.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            await nextMiddleware();
        });

        next(app);
    };
}
