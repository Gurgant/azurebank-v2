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
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

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
