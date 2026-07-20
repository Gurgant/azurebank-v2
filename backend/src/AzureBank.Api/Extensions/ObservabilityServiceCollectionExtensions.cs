using AzureBank.Api.Observability;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Observability;
using Microsoft.Extensions.Compliance.Classification;
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
    /// <summary>Single source of truth for the service name (also used by the Serilog OTLP sink's resource).</summary>
    internal const string ServiceName = "azurebank-api";

    public static IServiceCollection AddObservability(
        this IServiceCollection services, IHostEnvironment environment)
    {
        // Gate on the PROCESS environment variable. This is a deliberate CONTRACT, not an
        // oversight: the process env var is the only supported way to enable export. The Serilog
        // OTLP sink reads only real env vars, so honouring an appsettings-supplied endpoint here
        // would enable traces/metrics while logs stay dark — a split-pillar failure worse than
        // the inert alternative (appsettings value + closed gate = exporter never registered,
        // nothing binds, nothing half-works).
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        // Prod TLS guard (consistent with the app's ValidateOnStart posture) — the policy lives
        // in ONE place shared with the BFF (AzureBank.Shared.Observability.OtlpEndpointGuard):
        // outside Development it fails fast on cleartext-to-remote AND on an unparseable
        // endpoint (fail-closed). Loopback http (an OTLP sidecar) stays fine.
        OtlpEndpointGuard.EnsureSecureExportEndpoint(
            otlpEndpoint, environment.IsDevelopment(), environment.EnvironmentName);

        // The exporter's ENDPOINT comes entirely from OTEL_EXPORTER_OTLP_* env vars. Setting it
        // programmatically DISABLES the SDK's per-signal path append (/v1/traces, /v1/metrics),
        // so batches POST to the bare URL and 404 silently. We DO pin the protocol in code: the
        // SDK default is gRPC:4317, so a missing OTEL_EXPORTER_OTLP_PROTOCOL would otherwise
        // silently ship gRPC to the http/4318 port.
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

        // PII redaction — the compliance side of observability. Logs leave the process over
        // OTLP (Loki), so values classified AzureBank/PII must be masked BEFORE they reach the
        // logging pipeline. Registered here (not in AddApplicationServices) because the reason
        // redaction exists at all is the log-export path configured above. Call sites resolve a
        // Redactor by CLASSIFICATION via IRedactorProvider, never a concrete masker — swapping
        // the strategy (e.g. HMAC hashing for correlation) is a one-line change here.
        services.AddRedaction(redaction =>
            redaction.SetRedactor<EmailMaskingRedactor>(new DataClassificationSet(DataClassifications.Pii)));

        // Liveness = process up (no dependency probe); readiness = DB reachable (tagged "ready").
        services.AddHealthChecks()
            .AddDbContextCheck<AzureBankDbContext>(name: "database", tags: ["ready"]);

        return services;
    }
}
