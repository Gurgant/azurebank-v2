using AzureBank.Api.Observability;
using AzureBank.Infrastructure.Data;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AzureBank.Api.Extensions;

/// <summary>
/// OpenTelemetry (traces + metrics) and health checks — the observability layer.
/// Instrumentation is collected from the framework APIs (ASP.NET Core, HttpClient, SqlClient,
/// runtime) plus the application's own <see cref="ApiMetrics"/> domain meter. Telemetry is
/// exported over OTLP to a collector (Grafana LGTM / .NET Aspire) ONLY when
/// OTEL_EXPORTER_OTLP_ENDPOINT is set, so local dev and tests don't emit noisy connection
/// failures to a collector that isn't running.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    private const string ServiceName = "azurebank-api";

    public static IServiceCollection AddObservability(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Gate on the PROCESS environment variable — the same source the OTLP exporter itself
        // reads. Reading IConfiguration could open the gate off an appsettings value the exporter
        // never sees, then export nowhere.
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        // Prod TLS guard (consistent with the app's ValidateOnStart posture): telemetry must not
        // leave the host as cleartext over a network outside Development. Loopback http (an OTLP
        // sidecar/agent on 127.0.0.1) is fine; a remote http endpoint in Production fails fast.
        if (exportOtlp && !environment.IsDevelopment()
            && Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpointUri)
            && endpointUri.Scheme == Uri.UriSchemeHttp && !endpointUri.IsLoopback)
        {
            throw new InvalidOperationException(
                $"OTLP endpoint '{otlpEndpoint}' uses cleartext http to a non-loopback host in the " +
                $"'{environment.EnvironmentName}' environment. Use https, or an OTLP collector on loopback.");
        }

        // The exporter's ENDPOINT comes entirely from OTEL_EXPORTER_OTLP_* env vars — setting it
        // programmatically double-appends the signal path (…/v1/traces/v1/traces → 404, dropped).
        // We DO pin the protocol in code: the SDK default is gRPC:4317, so a missing
        // OTEL_EXPORTER_OTLP_PROTOCOL would otherwise silently ship gRPC to the http/4318 port.
        static void ConfigureOtlp(OtlpExporterOptions o) => o.Protocol = OtlpExportProtocol.HttpProtobuf;

        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(
                    serviceName: ServiceName,
                    serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    // Stable per-instance id (HOSTNAME in a container, else machine name) so metric
                    // series don't churn on restart and multi-replica dashboards stay correct.
                    serviceInstanceId: Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("service.namespace", "azurebank"),
                    new("deployment.environment.name", environment.EnvironmentName),
                }))
            .WithTracing(tracing =>
            {
                // SqlClient text capture is OFF by default — a banking SQL statement can carry PII.
                // RecordException attaches exception.* events to error spans (the global handler
                // also records explicitly, since it marks the exception handled). /health probes
                // are filtered out — they'd flood Tempo at 100% sampling with zero signal.
                tracing
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        o.RecordException = true;
                        o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation(o => o.RecordException = true)
                    .AddSqlClientInstrumentation();
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter(ConfigureOtlp);
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(ApiMetrics.MeterName)                      // domain RED (transfers, logins, replays)
                    .SetExemplarFilter(ExemplarFilterType.TraceBased);  // metric bucket -> exemplar -> trace pivot
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter(ConfigureOtlp);
                }
            });

        // Liveness = process up (no dependency probe); readiness = DB reachable (tagged "ready").
        services.AddHealthChecks()
            .AddDbContextCheck<AzureBankDbContext>(name: "database", tags: ["ready"]);

        return services;
    }
}
