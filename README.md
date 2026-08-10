# AzureBank

A personal banking application — accounts, deposits, withdrawals, transfers — built as a portfolio
and study project. .NET 10 API behind a BFF, React 19 SPA, SQL Server. Both halves are real and
wired to each other.

The interesting part is not the CRUD. It is everything that has to be true **because it moves
money**: an operation that executes exactly once across retries, crashes and concurrent duplicates;
a browser that never holds a token; a second factor that survives a replayed request byte for byte.

## Read this first

**→ [How AzureBank works](docs/architecture/overview.md)** — one document, complete on its own.
Ten minutes, and you will know how the money guarantee works and why the browser has no token.

If you want more after that:

| | |
|---|---|
| [ADR-0009](docs/adr/0009-idempotency-monetary-operations.md) + [ADR-0022](docs/adr/0022-client-money-mutation-protocol.md) | The money protocol, server and client halves. The five-outcome table is the core of the project. |
| [`docs/adr/`](docs/adr/README.md) | 25 decisions with their alternatives and residuals. The index names four to start with. |
| [`docs/engineering-traps.md`](docs/engineering-traps.md) | The things that fail silently — each one cost a debugging session. |
| [`SECURITY.md`](SECURITY.md) | The security posture in one place. |

## Running it

Configuration comes from user-secrets, never a committed file — the API refuses to start without
them. **The API and the seeder have separate secret stores**, so the pepper has to be set twice and
must match, or seeded PINs fail verification at login rather than at seeding.

```bash
API=backend/src/AzureBank.Api
SEEDER=backend/tools/AzureBank.Seeder
CONN='Server=(localdb)\MSSQLLocalDB;Database=AzureBankDev;Trusted_Connection=True;TrustServerCertificate=True'
PEPPER="$(openssl rand -base64 48)"

dotnet user-secrets --project $API    set "Jwt:Secret" "$(openssl rand -base64 64)"
dotnet user-secrets --project $API    set "Idempotency:HashKey" "$(openssl rand -base64 32)"
dotnet user-secrets --project $API    set "Security:PinPepper" "$PEPPER"
dotnet user-secrets --project $API    set "ConnectionStrings:DefaultConnection" "$CONN"
dotnet user-secrets --project $SEEDER set "Security:PinPepper" "$PEPPER"
dotnet user-secrets --project $SEEDER set "ConnectionStrings:DefaultConnection" "$CONN"
```

`Security:PinPepper` is mixed into the Argon2id PIN hash so a stolen database cannot brute-force
the six-digit PIN space offline. It supports zero-downtime rotation through a keyring
(`Security:PreviousPinPeppers`) — when rotating, keep the whole ring in one secret provider.

Then create and seed the database in one command. The three processes each run in **their own
terminal and stay running** — but on a cold clone, let the API finish building before starting the
BFF: two first builds in parallel race on a shared assembly and fail with a file lock that looks
like a corrupted build.

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project backend/tools/AzureBank.Seeder -- reset --confirm

dotnet run --project backend/src/AzureBank.Api --launch-profile https   # https://localhost:7215
dotnet run --project backend/src/AzureBank.Bff --launch-profile http    # http://localhost:5000
cd frontend && npm ci && npm run dev                                     # http://localhost:5173
```

The `DOTNET_ENVIRONMENT` variable is required, and `ASPNETCORE_ENVIRONMENT` will not do — the
seeder is a console Generic Host and reads the other prefix. The API must run the **https** profile
because the BFF proxies to 7215.

Seeded demo login: `john@example.com` / `Test123!`, PIN `123456`.

## Running the tests

```bash
dotnet test backend/AzureBank.slnx    # name the solution — see below
cd frontend && npm run build && npx vitest run
```

Two commands that look like they work and do not: a filtered `dotnet test` silently drops the
entire BFF suite while reporting success, and `tsc --noEmit` skips project references under this
solution-style tsconfig. Both are explained in
[engineering traps](docs/engineering-traps.md).

The concurrency proofs are gated behind a real SQL Server and skip locally:

```bash
AZUREBANK_TEST_SQLSERVER="Server=(localdb)\\MSSQLLocalDB;Database=AzureBankProofs;Trusted_Connection=True;TrustServerCertificate=True" \
  dotnet test backend/AzureBank.slnx --filter "Category=SqlServer"
```

## Observability

Both services emit traces, metrics and logs over OpenTelemetry, correlated end-to-end. A request
through the BFF is **one trace** — BFF span, YARP forwarder, HttpClient, API, SQL — and every
ProblemDetails carries the bare 32-hex `traceId` that pastes straight into Tempo search.

```bash
docker compose -f observability/docker-compose.yml up -d   # Grafana LGTM on 127.0.0.1:3000

# Export in the terminal that starts each service — a bare assignment stays shell-local
# and the child process never sees it. Use 127.0.0.1, not localhost: on Windows the name
# resolves to ::1 first and the collector is listening on IPv4.
export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

In PowerShell: `$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://127.0.0.1:4318"`.

Export is opt-in: without the variable, tests and dev runs emit nothing. Telemetry is PII-safe by
design — emails are masked through the .NET compliance stack, amounts never appear in log lines,
and user-controlled values pass a central sanitizer whose contract is pinned by tests.

## Status

**Backend** — builds with zero warnings. 674 tests passing, plus 36 held behind the SQL Server flag
that run in CI against a real database — 710 in total once `AZUREBANK_TEST_SQLSERVER` is set, with
nothing skipped. Both auth modes verified live. Monetary operations are idempotent and
concurrency-safe under 24 parallel duplicates.

**Frontend** — fully wired to the real API through the BFF: authentication, accounts, transaction
history, the four money flows with idempotency keys and step-up PIN, and the dashboard on real
aggregates. 586 tests across 57 files, and a test that writes to `console.error` fails. Verified
end-to-end against the running stack, not only against mocks.

**Known gaps**, tracked rather than hidden: the accessibility sweep is a dedicated phase not yet
run; `ConfirmDialog` — the confirm step on delete and on both transfer flows — restores focus and
closes on Escape but does not contain Tab, so focus can leave it; the production CSP is designed but
unverifiable until the BFF serves the built SPA, which it does not yet do. A UI/UX overhaul is in
progress.

The end-to-end browser suite that used to be listed here **exists**: Playwright drives a real
Chromium against the real BFF, API and SQL Server, and CI runs it in the same job as the contract
and integration layers.

## How this repository is built

Squash-merge: the **pull-request title** is the commit that lands on `main`, so it carries the
meaning — plain, imperative, naming the concrete outcome, never a `type(scope):` prefix.
Conventional Commits is deliberately unused; its payoff needs release automation this project does
not run. Every change ships behind a reviewed pull request with CodeQL and an AI reviewer, and a
human merges. Full rules in [engineering practices](docs/engineering-practices.md).

## Provenance

A curated fresh-history consolidation of work done in three phases: design-first documentation
(Dec 2025 – Jan 2026), backend implementation over 27 iterations (Jan 2026), then recovery from
backups, repair to green, and ongoing evolution as a monorepo (Jul 2026). The full original
history, including intermediate generations and experiments, lives in the original private
repositories and backup archives rather than here.
