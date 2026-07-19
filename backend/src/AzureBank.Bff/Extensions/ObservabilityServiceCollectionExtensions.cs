using AzureBank.Bff.Health;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AzureBank.Bff.Extensions;

/// <summary>
/// OpenTelemetry (traces + metrics) and health checks for the BFF gateway — the observability
/// layer. Mirrors the API's setup so a request entering the BFF and proxied through YARP to the
/// API is ONE distributed trace (shared trace-id): the API's server span is a child of the BFF's.
///
/// Instrumentation:
///  - AspNetCore  → server span for every incoming request, including YARP /api/* endpoints.
///  - HttpClient  → client span for the controller's calls to the API (named client "BackendApi").
///  - Yarp.ReverseProxy → the forwarder span for proxied /api/* traffic (AddSource). YARP 2.x
///    ships no System.Diagnostics.Metrics Meter, so proxy metrics come from the ASP.NET Core /
///    System.Net.Http meters, not a YARP-specific one.
/// YARP propagates the W3C trace context to the destination, so the downstream API continues the
/// same trace with no extra wiring.
///
/// Telemetry is exported over OTLP to a collector (Grafana LGTM) ONLY when
/// OTEL_EXPORTER_OTLP_ENDPOINT is set, so local dev / tests don't emit noisy connection failures.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    private const string ServiceName = "azurebank-bff";

    /// <summary>
    /// The ActivitySource name YARP emits its forwarder span under. NOT a Meter — YARP 2.x
    /// ships no System.Diagnostics.Metrics instrument, so there is nothing to AddMeter().
    /// </summary>
    private const string YarpActivitySourceName = "Yarp.ReverseProxy";

    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration configuration)
    {
        // Export OTLP only when an endpoint is configured, so local dev / tests don't emit
        // noisy connection failures to a collector that isn't running.
        //
        // The exporter is driven ENTIRELY by the standard OTEL_EXPORTER_OTLP_* environment
        // variables (endpoint, protocol, headers). The SDK resolves the per-signal path
        // (/v1/traces, /v1/metrics) from them per the OTLP spec. We deliberately do NOT set the
        // endpoint programmatically: doing so conflicts with the env-var path resolution and
        // silently double-appends the signal path (…/v1/traces/v1/traces → 404, dropped).
        // For a local Grafana LGTM collector, set:
        //   OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318   (127.0.0.1, NOT localhost — on
        //     Windows, "localhost" resolves to ::1 first and Docker Desktop's IPv6 port-forward
        //     drops the OTLP POSTs silently; forcing IPv4 makes them land)
        //   OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
        var exportOtlp = !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: ServiceName,
                serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"))
            .WithTracing(tracing =>
            {
                // AddHttpClientInstrumentation is REQUIRED alongside AddSource: it emits the
                // outbound forwarder span AND injects the W3C traceparent that stitches the API
                // span into this trace. YARP propagates context by default — do not disable it.
                tracing.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource(YarpActivitySourceName);
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                // No AddMeter for YARP — v2.3.0 ships no System.Diagnostics.Metrics Meter. Proxy
                // traffic metrics come from the ASP.NET Core (Hosting/Kestrel/RateLimiting) and
                // System.Net.Http meters, which AspNetCore + HttpClient instrumentation cover.
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            });

        // Liveness = process up (no dependency probe); readiness = the backend API is reachable
        // (tagged "ready"). The readiness probe is itself a traced BFF→API hop.
        services.AddHealthChecks()
            .AddCheck<BackendApiHealthCheck>(name: "backend-api", tags: ["ready"]);

        return services;
    }
}
