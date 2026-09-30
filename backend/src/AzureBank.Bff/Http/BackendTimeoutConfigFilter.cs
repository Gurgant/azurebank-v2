using AzureBank.Bff.Options;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace AzureBank.Bff.Http;

/// <summary>
/// Gives every proxy cluster <c>BackendApi:TimeoutSeconds</c> as its activity timeout, so the BFF
/// waits on the API for one length of time whichever road a call takes (ADR-0058): its own
/// <c>BackendApi</c> client for the routes it answers itself, and YARP for everything under
/// <c>/api</c>.
/// </summary>
/// <remarks>
/// <para>
/// Without it the proxy waited YARP's own default, 100 s, whatever the option said, and a call the
/// API never answered came back as an empty 504 after it. The timeout is idle time, not total time:
/// YARP restarts it once the request transforms are done (the session's renewal can spend up to 5 s
/// there) and once the response headers arrive, so it bounds the wait for the API's answer.
/// </para>
/// <para>
/// A cluster that sets its own <c>HttpRequest.ActivityTimeout</c> keeps it: an operator who wrote a
/// value for one cluster meant it. No route gets a <c>Timeout</c>: YARP 2.3 reports that one as the
/// client cancelling (<c>ForwarderError.RequestCanceled</c>, a 400 or a 502), which the response
/// transform could not tell from a browser that hung up.
/// </para>
/// <para>
/// Read from <see cref="IOptions{TOptions}"/>, as the <c>BackendApi</c> client reads it, so a
/// configuration reload that re-runs this filter cannot give the two roads different values.
/// </para>
/// </remarks>
public sealed class BackendTimeoutConfigFilter : IProxyConfigFilter
{
    private readonly TimeSpan _timeout;

    public BackendTimeoutConfigFilter(IOptions<BackendApiOptions> options)
    {
        _timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds);
    }

    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        if (cluster.HttpRequest?.ActivityTimeout is not null)
        {
            return ValueTask.FromResult(cluster);
        }

        var request = (cluster.HttpRequest ?? ForwarderRequestConfig.Empty) with { ActivityTimeout = _timeout };
        return ValueTask.FromResult(cluster with { HttpRequest = request });
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel) =>
        ValueTask.FromResult(route);
}
