using AzureBank.Infrastructure.Data;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AzureBank.Api.Extensions;

/// <summary>
/// OpenTelemetry (traces + metrics) and health checks — the observability layer.
/// Instrumentation is collected from the framework APIs (ASP.NET Core, HttpClient, SqlClient,
/// runtime). Telemetry is exported over OTLP to a collector (Grafana LGTM / .NET Aspire
/// dashboard) ONLY when OTEL_EXPORTER_OTLP_ENDPOINT is set, so local dev and tests don't emit
/// noisy connection failures to a collector that isn't running.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    private const string ServiceName = "azurebank-api";

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
                // SqlClient text capture is OFF by default — a banking SQL statement can carry PII.
                tracing.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation();
                if (exportOtlp)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (exportOtlp)
                {
                    metrics.AddOtlpExporter();
                }
            });

        // Liveness = process up (no dependency probe); readiness = DB reachable (tagged "ready").
        services.AddHealthChecks()
            .AddDbContextCheck<AzureBankDbContext>(name: "database", tags: ["ready"]);

        return services;
    }
}
