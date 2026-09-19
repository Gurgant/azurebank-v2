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
    /// The primary handler of the BFF's own client to the API: it follows no redirect, and it
    /// trusts an unverifiable certificate only on the LOCAL development one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No redirects</b>, because .NET drops <c>Authorization</c> when a redirect leaves the
    /// authority and keeps every other header: a 302 from the API's address would carry
    /// <c>X-AzureBank-Service-Key</c> wherever it pointed. Nothing the BFF calls redirects; a 3xx
    /// is handed back as the answer it is.
    /// </para>
    /// <para>
    /// <b>The certificate exception is loopback's alone.</b> It exists for ASP.NET's development
    /// certificate on <c>https://localhost:7215</c>, which no chain validates. Granted to the whole
    /// Development environment it would also cover a remote <c>https://</c> destination — which
    /// <see cref="IsSafe"/> allows — and then whoever answered that address, certificate or not,
    /// would be handed the key. Outside loopback the certificate is validated in every environment.
    /// </para>
    /// </remarks>
    public static HttpClientHandler CreateHandler(bool isDevelopment, Uri? destination)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (isDevelopment && destination is { IsLoopback: true })
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
