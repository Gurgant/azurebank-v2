# ADR-0016: Observability — OpenTelemetry three pillars with a local Grafana LGTM stack

**Status:** Accepted · **Date:** 2026-07-20 · **Amended:** 2026-09-25 (decision 6, probe log lines)
· **Decision Makers:** Vladislav Aleshaev

## Context

Without this decision the backend is blind: no traces, no metrics, logs to the console only. The bar
is the three pillars, correlated, so an operator can pivot from a metric spike to the exact trace to
the exact log lines. The stack must be local and free, honest (real telemetry from the real
services) and quiet by default: tests and runs without a collector must not spray connection errors.

## Decision

1. **The OpenTelemetry SDK runs on both services** (`azurebank-api`, `azurebank-bff`). Traces:
   AspNetCore, HttpClient and SqlClient on the API; AspNetCore, HttpClient and the
   `Yarp.ReverseProxy` ActivitySource on the BFF. Metrics: AspNetCore, HttpClient and Runtime, plus
   `Microsoft.AspNetCore.RateLimiting` on the BFF, because the edge limiter is the flagship security
   control and has to be alertable.
2. **Logs are the third pillar.** `Serilog.Sinks.OpenTelemetry` exports logs over OTLP with
   `trace_id` and `span_id` stamped from `Activity.Current`, so Grafana's log-to-trace pivot needs
   no manual wiring. The sink's resource mirrors the SDK's (name, version, namespace `azurebank`, a
   stable `service.instance.id`, environment), so all three signals join per instance.
3. **One distributed trace crosses the BFF-to-API hop.** YARP propagates the W3C trace context by
   default, and `AddSource("Yarp.ReverseProxy")` makes the forwarder span visible. There is no
   `AddMeter` for YARP: v2.x ships no `System.Diagnostics.Metrics` meter, so proxy-traffic metrics
   come from the ASP.NET Core and `System.Net.Http` meters.
4. **Export is opt-in: telemetry leaves the process only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set
   in its environment.** The endpoint is never set in code, because that disables the SDK's
   per-signal path append: batches POST to the bare URL and 404 silently. The gate does not read
   `IConfiguration`, because the Serilog sink reads only the environment: an appsettings gate would
   enable traces and metrics while logs stay dark. The protocol is pinned to `http/protobuf` in
   code, so a missing `OTEL_EXPORTER_OTLP_PROTOCOL` cannot ship gRPC to the HTTP port.
5. **The local backend is `grafana/otel-lgtm`** (Loki, Grafana, Tempo and Prometheus in one
   container): digest-pinned, memory-capped, no restart policy, the unused gRPC port not published,
   and on loopback only, because Grafana runs anonymous-admin and must never be reachable from the
   LAN. On Windows with Docker Desktop the endpoint is `http://127.0.0.1:4318`, not `localhost`:
   .NET resolves `localhost` to `::1` first, and the IPv6 port-forward silently drops OTLP POSTs.
6. **Health probes.** `/health/live` says the process is up; `/health/ready` checks the dependency
   (API: the database; BFF: the API, through the same named client). The BFF reports Degraded, not
   Unhealthy, when the API blips, because a hard readiness failure on a shared downstream would
   evict every BFF instance at once. Probes are excluded from the rate limiter, and their spans,
   server and client side, are filtered out of tracing: at 100% sampling they would flood Tempo.
   Their request log lines are dropped too, unless the probe failed: a status above 499 or an
   exception keeps Serilog's Error line.
7. **Domain metrics with strict cardinality discipline.** One application meter (`AzureBank.Api`)
   carries `azurebank.logins`, `azurebank.transfers` and `azurebank.idempotency.replays`, tagged
   only with namespaced low-cardinality outcomes (`azurebank.outcome`, `azurebank.kind`): never an
   account id, a user id, an amount or free text. Exemplars (`TraceBased`) link metric buckets to
   traces. Sampling stays the default `ParentBased(AlwaysOn)`, honest at demo volume and overridable
   with the standard `OTEL_TRACES_SAMPLER` variables.
8. **Errors are visible in traces.** `RecordException` is set on the instrumentation, and the global
   exception handler calls `Activity.AddException`, because it marks exceptions handled and the
   instrumentation alone would never see them. Every ProblemDetails carries the bare 32-hex
   `traceId`, which pastes directly into Tempo's search.

## Rejected

- Rejected: the lighter .NET Aspire dashboard, because it has no Loki, Tempo or Prometheus story.
- Rejected: always-on export with errors suppressed, because an explicit opt-in keeps runs quiet.
- Rejected: gRPC (4317) for local development, because of its silent-failure modes on Windows.

## Consequences

- `docker compose -f observability/docker-compose.yml up -d` and two environment variables per
  service show real traces, metrics and correlated logs in Grafana.
- Everything is opt-in: the full test suite runs with no telemetry.
- Not covered: `/health/ready` runs a live dependency check per request, unauthenticated. Accepted
  for a loopback stack; the fix is publisher or TTL memoization.
- Not covered: the production TLS guard covers `OTEL_EXPORTER_OTLP_ENDPOINT` and not the per-signal
  variants (`…_TRACES_ENDPOINT` and the others): a misconfiguration risk, not an attacker boundary.
- Not covered: `service.version` reports the assembly version, 1.0.0 until CI stamps a real one.
- Not covered: the meter (`ApiMetrics`) is static, not injected through `IMeterFactory`; the
  namespaced tag keys keep a later conversion cheap.

## Verified by

- `HealthEndpointTests` (BFF): live is healthy, and ready reports Degraded but stays available.
- `RequestLogRouteTests` and `RequestLogRouteLevelTests`: a probe's request line is dropped unless
  the probe failed. `OtlpEndpointGuardTests`: the TLS guard on the endpoint.

## Related

ADR-0017.
