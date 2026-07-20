# ADR-0016: Observability — OpenTelemetry three pillars with a local Grafana LGTM stack

**Status**: Accepted

**Date**: 2026-07-20

**Decision Makers**: Vladislav Aleshaev

---

## Context

The backend was functionally complete (auth, monetary operations, idempotency, rate limiting)
but blind: no traces, no metrics, logs to console only. For a banking-grade showcase the bar is
the "three pillars" — traces, metrics and logs, correlated, so an operator can pivot from a
metric spike to the exact trace to the exact log lines. The stack had to be local and free
(portfolio), honest (no mocked dashboards — real telemetry from the real services), and quiet
by default (tests and collector-less dev runs must not spray connection errors).

## Decision

1. **OpenTelemetry SDK on both services** (`azurebank-api`, `azurebank-bff`): traces
   (AspNetCore + HttpClient + SqlClient on the API; AspNetCore + HttpClient + the
   `Yarp.ReverseProxy` ActivitySource on the BFF) and metrics (AspNetCore + HttpClient +
   Runtime; plus `Microsoft.AspNetCore.RateLimiting` on the BFF — the edge limiter is the
   flagship security control and must be alertable, not just a Serilog warning).
2. **Logs are the third pillar, not an afterthought**: `Serilog.Sinks.OpenTelemetry` exports
   logs over OTLP with `trace_id`/`span_id` stamped from `Activity.Current`, so the Grafana
   log↔trace pivot works with zero manual wiring. The sink's resource mirrors the SDK resource
   (name, version, namespace `azurebank`, stable `service.instance.id`, environment), so all
   three signals join per-instance.
3. **One distributed trace across the BFF→API hop.** YARP propagates the W3C trace context by
   default; `AddSource("Yarp.ReverseProxy")` makes the forwarder span visible. No
   `AddMeter` for YARP — v2.x ships no `System.Diagnostics.Metrics` Meter; proxy-traffic
   metrics come from the ASP.NET Core / `System.Net.Http` meters. Verified live: one proxied
   registration = one 16-span trace (BFF server → forwarder → HttpClient → API server → SQL).
4. **Export is opt-in and env-var-driven — a deliberate contract.** Telemetry leaves the
   process only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set as a **process environment
   variable**; the endpoint is never set programmatically (a programmatic endpoint disables the
   SDK's per-signal path append, so batches POST to the bare URL and 404 silently). The gate
   deliberately does not read `IConfiguration`: the Serilog sink reads only real env vars, so
   an appsettings-driven gate would enable traces/metrics while logs stay dark — split pillars.
   The protocol is pinned to `http/protobuf` in code so a missing `OTEL_EXPORTER_OTLP_PROTOCOL`
   cannot silently ship gRPC to the HTTP port.
5. **Local backend: `grafana/otel-lgtm`** (Loki + Grafana + Tempo + Prometheus in one
   container), digest-pinned, published on loopback only (Grafana runs anonymous-admin for dev
   convenience and must never be LAN-reachable), memory-capped, no restart policy, gRPC port
   not published (unused). **On Windows + Docker Desktop the endpoint must be
   `http://127.0.0.1:4318`, not `localhost`**: .NET resolves `localhost` to `::1` first and the
   IPv6 port-forward drops OTLP POSTs with no error (collector shows 0 accepted / 0 refused).
6. **Health probes**: `/health/live` (process up) and `/health/ready` (API: DB reachable;
   BFF: backend reachable through the same named client). The BFF readiness reports
   **Degraded, not Unhealthy**, when the API blips — a hard readiness failure on a shared
   downstream would evict every BFF instance at once (cascading failure). Probes are excluded
   from the rate limiter, and probe spans (server and client side) are filtered out of tracing:
   at 100% sampling they would flood Tempo with zero signal.
7. **Domain metrics with strict cardinality discipline**: one application meter
   (`AzureBank.Api`) with `azurebank.logins` / `azurebank.transfers` /
   `azurebank.idempotency.replays`, tagged only with namespaced low-cardinality outcomes
   (`azurebank.outcome`, `azurebank.kind`) — never an account id, user id, amount, or free
   text. Exemplars (`TraceBased`) link metric buckets to traces. Sampling stays the default
   `ParentBased(AlwaysOn)` — the honest choice at demo volume, overridable with the standard
   `OTEL_TRACES_SAMPLER` env vars with no code change.
8. **Errors are visible in traces**: `RecordException` on the instrumentation plus an explicit
   `Activity.AddException` in the global exception handler (which marks exceptions handled, so
   the instrumentation alone would never see them). Every ProblemDetails carries the bare
   32-hex `traceId` that pastes directly into Tempo search.

## Residuals (accepted, documented)

- `/health/ready` performs a live dependency check per request (unauthenticated); acceptable
  for a loopback dev stack, and the right fix (publisher/TTL memoization) is a small follow-up.
- The prod TLS guard covers `OTEL_EXPORTER_OTLP_ENDPOINT`; the per-signal variants
  (`…_TRACES_ENDPOINT` etc.) are not individually guarded — operator footgun, not an attacker
  boundary; follow-up.
- `service.version` reports the assembly version (1.0.0 until CI stamps a real version).
- Static meter (`ApiMetrics`) rather than `IMeterFactory` injection; conversion is a
  test-ergonomics follow-up, de-risked by the namespaced tag keys.

## Alternatives considered

- **.NET Aspire dashboard** as the local backend: lighter, but no Loki/Tempo/Prometheus story
  and no Grafana pivots — the LGTM container demonstrates the real operator workflow.
- **Always-on export with error suppression**: rejected; quiet tests and collector-less runs
  are worth the explicit opt-in contract.
- **gRPC (4317)**: rejected for local dev; silent-failure modes on Windows made `http/protobuf`
  the robust default (and the reason the protocol is pinned in code).

## Consequences

- A reviewer can run `docker compose -f observability/docker-compose.yml up -d`, start both
  services with two env vars, and see real traces, metrics and correlated logs in Grafana.
- Everything observability-related is opt-in: the full test suite runs with zero telemetry.
- The verbose "gotcha" comments (path-append, IPv6/localhost) are deliberate: they document
  silent-failure modes that cost real debugging time and would otherwise be re-learned.

## References

- Verification evidence: one cross-service trace (16 spans), Loki lines with `trace_id`
  resolving in Tempo, `aspnetcore_rate_limiting_requests_total` series, exemplars on
  `http_server_request_duration` — all reproduced live against the compose stack.
- ADR-0017 (PII redaction in telemetry + CodeQL log-forging barrier) — the compliance side of
  this chapter.
