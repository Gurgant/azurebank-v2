namespace AzureBank.Shared.Observability;

/// <summary>
/// The single security policy for OTLP export endpoints, shared by the API and the BFF so the
/// rule cannot drift between services: outside Development, telemetry must not leave the host
/// as cleartext over a network — and an endpoint we cannot even parse is a config error, not a
/// pass (fail-closed: silently exporting "somewhere" is exactly what this guard exists to stop).
/// Loopback http (an OTLP sidecar/agent on 127.0.0.1) is a legitimate production pattern and
/// stays allowed. Takes primitives, not IHostEnvironment, so Shared gains no hosting dependency.
/// </summary>
public static class OtlpEndpointGuard
{
    /// <summary>
    /// Throws when a non-Development environment configures an OTLP endpoint that is
    /// unparseable, or that is anything other than https or loopback http (scheme ALLOWLIST —
    /// an exotic scheme is a config error, not a pass). A null/empty endpoint is fine (export
    /// is opt-in and simply off). Exception messages never echo the raw endpoint: the
    /// configured value can carry credentials, and startup errors land in (exported) logs —
    /// only a scheme://host:port form is quoted, and nothing at all for unparseable input.
    /// Call at startup, before wiring exporters.
    /// </summary>
    public static void EnsureSecureExportEndpoint(string? otlpEndpoint, bool isDevelopment, string environmentName)
    {
        if (string.IsNullOrWhiteSpace(otlpEndpoint) || isDevelopment)
        {
            return;
        }

        if (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpointUri))
        {
            // Deliberately no echo of the raw value — an unparseable string can still
            // contain a pasted secret, and we cannot redact what we cannot parse.
            throw new InvalidOperationException(
                $"The configured OTEL_EXPORTER_OTLP_ENDPOINT is not a valid absolute URI in the " +
                $"'{environmentName}' environment — refusing to export telemetry to an unverifiable destination.");
        }

        // ALLOWLIST, not blocklist: https anywhere, or cleartext http strictly on loopback.
        // Uri.IsLoopback never resolves DNS, so a hostname that merely *resolves* to loopback
        // does not pass — only literal loopback forms do. Fail-closed by construction.
        var isHttps = endpointUri.Scheme == Uri.UriSchemeHttps;
        var isLoopbackHttp = endpointUri.Scheme == Uri.UriSchemeHttp && endpointUri.IsLoopback;
        if (!isHttps && !isLoopbackHttp)
        {
            var reason = endpointUri.Scheme == Uri.UriSchemeHttp
                ? "uses cleartext http to a non-loopback host"
                : $"uses the unsupported scheme '{endpointUri.Scheme}'";
            throw new InvalidOperationException(
                $"OTLP endpoint '{Redacted(endpointUri)}' {reason} in the " +
                $"'{environmentName}' environment. Use https, or an OTLP collector on loopback http.");
        }
    }

    /// <summary>
    /// scheme://host:port only — no user-info, path or query, any of which can carry secrets
    /// that must not reach startup error text (which is itself logged and exported).
    /// </summary>
    private static string Redacted(Uri endpoint) => $"{endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}";
}
