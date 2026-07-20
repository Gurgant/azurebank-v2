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
    /// unparseable, or that uses cleartext http to a non-loopback host. A null/empty endpoint
    /// is fine (export is opt-in and simply off). Call at startup, before wiring exporters.
    /// </summary>
    public static void EnsureSecureExportEndpoint(string? otlpEndpoint, bool isDevelopment, string environmentName)
    {
        if (string.IsNullOrWhiteSpace(otlpEndpoint) || isDevelopment)
        {
            return;
        }

        if (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException(
                $"OTEL_EXPORTER_OTLP_ENDPOINT '{otlpEndpoint}' is not a valid absolute URI in the " +
                $"'{environmentName}' environment — refusing to export telemetry to an unverifiable destination.");
        }

        // Uri.IsLoopback never resolves DNS, so a hostname that merely *resolves* to loopback
        // does not pass — only literal loopback forms do. Fail-closed by construction.
        if (endpointUri.Scheme == Uri.UriSchemeHttp && !endpointUri.IsLoopback)
        {
            throw new InvalidOperationException(
                $"OTLP endpoint '{otlpEndpoint}' uses cleartext http to a non-loopback host in the " +
                $"'{environmentName}' environment. Use https, or an OTLP collector on loopback.");
        }
    }
}
