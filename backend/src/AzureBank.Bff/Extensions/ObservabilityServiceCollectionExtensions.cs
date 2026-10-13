using System.Diagnostics;
using AzureBank.Bff.Health;
using AzureBank.Bff.Observability;
using AzureBank.Shared.Observability;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
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

        // Prod TLS guard — the policy lives in ONE place shared with the API
        // (AzureBank.Shared.Observability.OtlpEndpointGuard): outside Development it fails fast
        // on cleartext-to-remote AND on an unparseable endpoint (fail-closed).
        OtlpEndpointGuard.EnsureSecureExportEndpoint(
            otlpEndpoint, environment.IsDevelopment(), environment.EnvironmentName);

        // Endpoint comes from OTEL_EXPORTER_OTLP_* env vars. Setting it in code DISABLES the
        // SDK's per-signal path append (/v1/traces, /v1/metrics), so batches POST to the bare
        // URL and 404 silently. Pin only the protocol so a missing OTEL_EXPORTER_OTLP_PROTOCOL
        // doesn't silently default to gRPC:4317 and ship to the http/4318 port.
        static void ConfigureOtlp(OtlpExporterOptions o) => o.Protocol = OtlpExportProtocol.HttpProtobuf;

        // The trace of a request starts here, whatever trace headers its caller sends: the trace id
        // is written on the API's audit rows and in every error answer, and a caller must not
        // choose it. Two readers have to be told, and with either one alone the caller's trace is
        // still adopted. The host reads the headers through the propagator in the container: this
        // one reads nothing. The ASP.NET Core instrumentation then reads them again by itself,
        // through the SDK's default propagator, unless that one is the W3C trace context alone,
        // which it takes to mean the host has done the reading. The SDK's default is one value for
        // the process. With both in place neither is what the two clients write with: they inject
        // through DistributedContextPropagator.Current, so the API is still handed this host's
        // trace.
        services.AddSingleton<DistributedContextPropagator>(
            new TraceStartsHerePropagator(DistributedContextPropagator.Current));
        Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());

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
                // AddHttpClientInstrumentation is REQUIRED alongside AddSource: it records the
                // client span of each outbound call, the parent of the API's server span. It does
                // not write the traceparent: the runtime's HTTP handler does, through
                // DistributedContextPropagator.Current. YARP passes it on by default — keep it so.
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
                        // AbsolutePath THROWS on a relative Uri (possible in test/custom-handler
                        // setups where HttpClient hasn't resolved BaseAddress) — check first.
                        o.FilterHttpRequestMessage = req =>
                            req.RequestUri is not { } uri
                            || !uri.IsAbsoluteUri
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
