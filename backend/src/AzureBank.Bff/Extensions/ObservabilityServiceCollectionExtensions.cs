using AzureBank.Bff.Health;
using OpenTelemetry.Exporter;
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
///  - Microsoft.AspNetCore.RateLimiting → the edge rate limiter (ADR-0013/0014) as first-class
///    metrics: a brute-force burst is now an alertable series, not just a Serilog warning.
///
/// Telemetry is exported over OTLP to a collector (Grafana LGTM) ONLY when
/// OTEL_EXPORTER_OTLP_ENDPOINT is set, so local dev / tests don't emit noisy connection failures.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>Single source of truth for the service name (also used by the Serilog OTLP sink's resource).</summary>
    internal const string ServiceName = "azurebank-bff";

    /// <summary>
    /// The ActivitySource name YARP emits its forwarder span under. NOT a Meter — YARP 2.x
    /// ships no System.Diagnostics.Metrics instrument, so there is nothing to AddMeter().
    /// </summary>
    private const string YarpActivitySourceName = "Yarp.ReverseProxy";

    /// <summary>The meter that emits aspnetcore.rate_limiting.* (lease acquired/rejected, queue).</summary>
    private const string RateLimitingMeterName = "Microsoft.AspNetCore.RateLimiting";

    public static IServiceCollection AddObservability(
        this IServiceCollection services, IHostEnvironment environment)
    {
        // Gate on the PROCESS environment variable — a deliberate contract (see the API twin
        // for the full rationale): the Serilog OTLP sink reads only real env vars, so an
        // appsettings-driven gate would enable traces/metrics while logs stay dark.
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        // Prod TLS guard: telemetry must not leave the host as cleartext over a network outside
        // Development. Loopback http (an OTLP sidecar) is fine; a remote http endpoint fails fast.
        if (exportOtlp && !environment.IsDevelopment()
            && Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpointUri)
            && endpointUri.Scheme == Uri.UriSchemeHttp && !endpointUri.IsLoopback)
        {
            throw new InvalidOperationException(
                $"OTLP endpoint '{otlpEndpoint}' uses cleartext http to a non-loopback host in the " +
                $"'{environment.EnvironmentName}' environment. Use https, or an OTLP collector on loopback.");
        }

        // Endpoint comes from OTEL_EXPORTER_OTLP_* env vars. Setting it in code DISABLES the
        // SDK's per-signal path append (/v1/traces, /v1/metrics), so batches POST to the bare
        // URL and 404 silently. Pin only the protocol so a missing OTEL_EXPORTER_OTLP_PROTOCOL
        // doesn't silently default to gRPC:4317 and ship to the http/4318 port.
        static void ConfigureOtlp(OtlpExporterOptions o) => o.Protocol = OtlpExportProtocol.HttpProtobuf;

        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(
                    serviceName: ServiceName,
                    serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    serviceInstanceId: Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("service.namespace", "azurebank"),
                    new("deployment.environment.name", environment.EnvironmentName),
                }))
            .WithTracing(tracing =>
            {
                // AddHttpClientInstrumentation is REQUIRED alongside AddSource: it emits the
                // outbound forwarder span AND injects the W3C traceparent that stitches the API
                // span into this trace. YARP propagates context by default — do not disable it.
                // RecordException attaches exception.* to error spans; /health probes are filtered.
                tracing
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.RecordException = true;
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation(o =>
                    {
                        o.RecordException = true;
                        // The readiness probe pings the API's /health/live through the named
                        // client. Its SERVER span is already filtered above; filter the CLIENT
                        // span too, or every probe cycle ships an orphan root span to Tempo.
                        o.FilterHttpRequestMessage = req =>
                            req.RequestUri is not { } uri
                            || !uri.AbsolutePath.StartsWith("/health", StringComparison.OrdinalIgnoreCase);
                    })
                    .AddSource(YarpActivitySourceName);
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter(ConfigureOtlp);
                }
            })
            .WithMetrics(metrics =>
            {
                // No AddMeter for YARP — v2.3.0 ships no Meter. The rate-limiting meter IS added:
                // the edge limiter is the flagship security control and must be observable.
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(RateLimitingMeterName)
                    .SetExemplarFilter(ExemplarFilterType.TraceBased);
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter(ConfigureOtlp);
                }
            });

        // Liveness = process up (no dependency probe); readiness = the backend API is reachable
        // (tagged "ready"). Probe spans (server AND client side) are filtered out of tracing
        // above — they would flood Tempo with zero signal at 100% sampling.
        services.AddHealthChecks()
            .AddCheck<BackendApiHealthCheck>(name: "backend-api", tags: ["ready"]);

        return services;
    }
}
