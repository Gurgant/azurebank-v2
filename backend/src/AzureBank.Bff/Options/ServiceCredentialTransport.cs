namespace AzureBank.Bff.Options;

/// <summary>
/// Where the service credential (ADR-0055) may be sent: over TLS, or to this machine.
/// </summary>
/// <remarks>
/// The key is a bearer secret, so a destination spelt <c>http://api.internal</c> would put it on
/// the network in clear for anyone on the path. Loopback is the one exception, because it is the
/// development and CI topology (the API on <c>http://localhost:5068</c>, no dev certificate on a
/// runner) and a loopback connection crosses no network.
/// </remarks>
public static class ServiceCredentialTransport
{
    /// <summary>True when the key may travel to <paramref name="destination"/>.</summary>
    public static bool IsSafe(Uri? destination) =>
        destination is { IsAbsoluteUri: true }
        && (destination.Scheme == Uri.UriSchemeHttps
            || (destination.Scheme == Uri.UriSchemeHttp && destination.IsLoopback));

    /// <summary>
    /// The primary handler of the BFF's own client to the API. It follows no redirect, and
    /// accepts a self-signed certificate only where asked to (development).
    /// </summary>
    public static HttpClientHandler CreateHandler(bool acceptAnyServerCertificate)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (acceptAnyServerCertificate)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    /// <summary>
    /// Every configured road to the API the key must not travel: the named client's
    /// <c>BackendApi:BaseUrl</c> and each YARP destination's <c>Address</c>. Empty means start.
    /// </summary>
    public static IReadOnlyList<string> UnsafeDestinations(IConfiguration configuration)
    {
        var roads = new List<(string Key, string? Value)>
        {
            ("BackendApi:BaseUrl", configuration["BackendApi:BaseUrl"]),
        };
        foreach (var cluster in configuration.GetSection("ReverseProxy:Clusters").GetChildren())
        {
            foreach (var destination in cluster.GetSection("Destinations").GetChildren())
            {
                roads.Add(($"{destination.Path}:Address", destination["Address"]));
            }
        }

        return roads
            .Where(road => !IsSafe(Uri.TryCreate(road.Value, UriKind.Absolute, out var uri) ? uri : null))
            .Select(road => $"{road.Key} = {road.Value ?? "(unset)"}")
            .ToList();
    }
}
