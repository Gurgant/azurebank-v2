using AzureBank.Api.Observability;
using AzureBank.Api.HealthChecks;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Observability;
using AzureBank.Shared.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
        this IServiceCollection services, IHostEnvironment environment, IConfiguration configuration)
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
            .AddDbContextCheck<AzureBankDbContext>(name: "database", tags: ["ready"])

            // Tagged "ready" alongside the database, because since ADR-0044 D1 an unreachable audit
            // store does not degrade this instance — it stops it moving money, which is the thing it
            // exists to do. Readiness is exactly the right signal for "do not send me traffic".
            .AddCheck<AuditChainHealthCheck>(name: "audit-chain", tags: ["ready"]);

        /*
          NO READINESS CHECK MAY RUN LONGER THAN THE BANK'S OWN WAIT TOLERANCE — and until this,
          neither of them had any bound at all. AddCheck and AddDbContextCheck both leave
          HealthCheckRegistration.Timeout at Timeout.InfiniteTimeSpan.

          MEASURED, because "unbounded" sounds theoretical until it has a number: the audit probe
          pointed at an unroutable address (RFC 5737 TEST-NET-1) took 36,800 ms to answer — SqlClient's
          15s connect timeout, then a retry after ConnectRetryInterval, then 15s again. Anything asking
          gave up long before; Kubernetes' default probe timeout is ONE second. A probe that does not
          answer is strictly worse than one that answers "unhealthy", because the first tells an
          orchestrator nothing while still consuming a connection.

          THE FRAMEWORK DOES ENFORCE THIS VALUE, which was worth measuring rather than assuming — the
          finding that prompted this cited a source claiming DefaultHealthCheckService ignores it. It
          does not: a five-second check registered with a 300 ms timeout came back in 307 ms as
          Unhealthy, "A timeout occurred while running check."

          APPLIED HERE RATHER THAN PER-REGISTRATION for two reasons. AddDbContextCheck takes no timeout
          parameter at all, so the database check could not be bounded at its call site — and leaving it
          unbounded would leave /health/ready able to hang for 37 seconds anyway, which is the thing
          being fixed. Doing it by tag also means a readiness check added later inherits the bound
          instead of quietly reintroducing the hang.

          POSTConfigure, NOT Configure, AND THAT WORD IS THE WHOLE SENTENCE ABOVE. AddCheck appends
          its registration through a Configure callback of its own, and callbacks run in registration
          order — so with Configure, a check added after this method had already been called was
          configured AFTER the loop ran, and kept Timeout.InfiniteTimeSpan. The claim "added later
          inherits the bound" was simply false, and measured: the regression test read -1ms, which is
          InfiniteTimeSpan. PostConfigure runs after every Configure, which makes it true.
          Pinned by ReadinessAnswersWithinBudgetTests.AReadinessCheckRegisteredAFTERAddObservability_
          StillInheritsTheBudget.

          BOUND TO Audit:TailTimeoutSeconds rather than to a constant of its own, so the two cannot
          drift apart. A fixed 5s probe under a 20s configured tail bound would pull this instance out
          of rotation while money movements were still succeeding — a false alarm that takes the bank
          offline for a wait it had been told to tolerate.
        */
        var readinessBudget = TimeSpan.FromSeconds(
            configuration.GetValue<int?>("Audit:TailTimeoutSeconds")
            ?? new AuditOptions().TailTimeoutSeconds);

        services.PostConfigure<HealthCheckServiceOptions>(options =>
        {
            foreach (var registration in options.Registrations.Where(r => r.Tags.Contains("ready")))
            {
                registration.Timeout = readinessBudget;
            }
        });

        return services;
    }
}
