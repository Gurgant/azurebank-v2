# Engineering practices

How this project is built and kept correct. It is deliberately **not** called `CONTRIBUTING.md`:
this is a solo portfolio project with no outside contributors, and a document that opens with "fork
and clone" is describing an audience that does not exist. The real readers are the person who wrote
this and whoever picks it up in six months.

Decisions live in [`adr/`](adr/README.md). Sharp edges that fail silently live in
[`engineering-traps.md`](engineering-traps.md). Frontend rules live in
[`../frontend/CONVENTIONS.md`](../frontend/CONVENTIONS.md).

---

## Local setup

The one copy of these instructions; the root README links here. *(Until 2026-09-24 the root README
carried its own copy, and this one had fallen behind it: it never set `ServiceCredential:BffKey`,
without which neither the API nor the BFF starts.)*

Configuration comes from **user-secrets**, never from a committed settings file. The API fails at
startup without them, by design — `ValidateOnStart` refuses to run a bank with a missing pepper.

**The API, the BFF and the seeder have separate secret stores** (a `UserSecretsId` each), so a value
two of them share is set once per project — `--project` is not optional. Three values are shared
and must match: the connection string and the PIN pepper, API and seeder, and the service
credential, API and BFF.

```bash
API=backend/src/AzureBank.Api
BFF=backend/src/AzureBank.Bff
SEEDER=backend/tools/AzureBank.Seeder
CONN='Server=(localdb)\MSSQLLocalDB;Database=AzureBankDev;Trusted_Connection=True;TrustServerCertificate=True'
PEPPER="$(openssl rand -base64 48)"
SERVICE_KEY="$(openssl rand -base64 48)"

dotnet user-secrets --project $API    set "Jwt:Secret" "$(openssl rand -base64 64)"
dotnet user-secrets --project $API    set "Idempotency:HashKey" "$(openssl rand -base64 32)"
dotnet user-secrets --project $API    set "StepUp:BindingKey" "$(openssl rand -base64 32)"
dotnet user-secrets --project $API    set "Audit:ChainKey" "$(openssl rand -base64 32)"
dotnet user-secrets --project $API    set "Audit:AnchorKey" "$(openssl rand -base64 32)"
dotnet user-secrets --project $API    set "Security:PinPepper" "$PEPPER"
dotnet user-secrets --project $API    set "ConnectionStrings:DefaultConnection" "$CONN"
dotnet user-secrets --project $API    set "ServiceCredential:BffKey" "$SERVICE_KEY"
dotnet user-secrets --project $BFF    set "ServiceCredential:BffKey" "$SERVICE_KEY"
dotnet user-secrets --project $SEEDER set "Security:PinPepper" "$PEPPER"
dotnet user-secrets --project $SEEDER set "ConnectionStrings:DefaultConnection" "$CONN"
```

If the two peppers differ, seeding succeeds and every seeded PIN then fails verification — the
failure surfaces at login, far from its cause.

`Security:PinPepper` is mixed into the Argon2id PIN hash so a stolen database cannot brute-force
the six-digit PIN space offline. It supports zero-downtime rotation through a keyring
(`Security:PreviousPinPeppers`) — when rotating, keep the whole ring in one secret provider.

`ServiceCredential:BffKey` is how the API knows a request comes from the BFF: the BFF sends it in
`X-AzureBank-Service-Key` on every call, and the API refuses anything without it before it looks at
a token, so the API serves one client (ADR-0055). Neither host starts without it, and the running
API answers 401 to every request without the header but its health probes and, in Development, its
own API documentation:
`/health/*` is exempt in every environment, `/openapi` and `/scalar` in Development, so a browser
can still open the documentation. Calling an API OPERATION by hand — curl, Bruno, the Scalar page's
"Try it" — needs that header. Bruno reads it from `serviceKey`, which ships empty in the tracked
`local.bru` and is passed per run instead: `cd tests/api-collection && bru run . -r --env local
--env-var serviceKey="$SERVICE_KEY" --insecure`. The `-r` is not optional — without it bru sends no
requests at all and still reports PASS. In production the API also has no public address; the key
is the second line behind that.

`Audit:ChainKey` keys the audit trail's hash chain and `Audit:AnchorKey` authenticates the anchor
records that say what the chain looked like at an instant (ADR-0044). Both are 32+ characters and
deliberately separate: the anchor constrains whoever holds the database, so it must not be
forgeable with the key the row chain uses.

**Database — one command, not `dotnet ef`:**

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project backend/tools/AzureBank.Seeder -- reset --confirm
```

That drops, migrates and seeds in one step. The environment variable is required and the `DOTNET_`
prefix is not interchangeable with `ASPNETCORE_` — the seeder is a console Generic Host and reads
the other prefix; see the traps document. Seeding also creates the Identity roles, without which
registration returns a 500. The seeded demo user is `john@example.com` / `Test123!`, PIN `123456`,
with two months of history on two accounts.

**Running it** — the three processes each run in their own terminal and stay running. Start them
sequentially the first time, or two parallel first builds race on `AzureBank.Shared.dll` and fail
with a file lock that looks like a corrupted build:

```bash
dotnet run --project backend/src/AzureBank.Api --launch-profile https   # https://localhost:7215
dotnet run --project backend/src/AzureBank.Bff --launch-profile http    # http://localhost:5000
cd frontend && npm ci && npm run dev                                     # http://localhost:5173
```

The API must run the **https** profile: the BFF's proxy cluster points at 7215, so the http profile
produces a BFF that starts and then fails every proxied call.

The BFF's proxy reaches the API over https with the SDK's development certificate. The committed
`appsettings.Development.json.example` tells it to accept any certificate on that hop, in
Development only; the real file is git-ignored, so copy it once:

```bash
cp backend/src/AzureBank.Bff/appsettings.Development.json.example backend/src/AzureBank.Bff/appsettings.Development.json
```

**Running the tests** — stop the API and the BFF first: the solution build rewrites their
executables, which Windows keeps locked while they run, so after an edit the build `dotnet test`
starts fails with `MSB3027: Could not copy … apphost.exe` (measured 2026-09-25):

```bash
dotnet test backend/AzureBank.slnx    # name the solution — see below
cd frontend && npm run build && npx vitest run
```

Two commands that look like they work and do not: a filtered `dotnet test` silently drops the
entire BFF suite while reporting success, and `tsc --noEmit` skips project references under this
solution-style tsconfig. Both are explained in [engineering traps](engineering-traps.md). The
concurrency proofs need a real SQL Server and skip without one:

```bash
AZUREBANK_TEST_SQLSERVER="Server=(localdb)\\MSSQLLocalDB;Database=AzureBankProofs;Trusted_Connection=True;TrustServerCertificate=True" \
  dotnet test backend/AzureBank.slnx --filter "Category=SqlServer"
```

**Traces, metrics and logs, locally.** Both services emit them over OpenTelemetry, correlated end to
end: a request through the BFF is **one trace** — BFF span, YARP forwarder, HttpClient, API, SQL —
and every ProblemDetails carries the bare 32-hex `traceId` that pastes straight into Tempo search.

```bash
docker compose -f observability/docker-compose.yml up -d   # Grafana LGTM on 127.0.0.1:3000

# Export in the terminal that starts each service — a bare assignment stays shell-local
# and the child process never sees it. Use 127.0.0.1, not localhost: on Windows the name
# resolves to ::1 first and the collector is listening on IPv4.
export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

In PowerShell: `$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://127.0.0.1:4318"`. Export is opt-in:
without the variable, tests and dev runs emit nothing. Telemetry is PII-safe by design — emails are
masked through the .NET compliance stack, amounts never appear in log lines, and user-controlled
values pass a central sanitizer whose contract is pinned by tests.

**Running the images as Production.** `compose.yaml` at the root builds the API and BFF images and
runs them as Production against SQL Server in a container: the `__Host-` session cookie, the SPA
served under its CSP, and an API with no address of its own, reached by the BFF over loopback as a
Container Apps sidecar would be. Its header lists the eight secrets it requires — none has a
default — and the one seed command. The database is published on 127.0.0.1:14330, not 1433: a
SQL Server installed on the host usually holds 1433, publishing over it does not fail, and the
seed then reaches the host's instance instead. Measured on 2026-09-25: the e2e suite, 24 of 24,
against the two containers. Sign in from a Chromium browser, as that run does: the `__Host-`
cookie is Secure, Chromium keeps it on `http://localhost`, and Safari keeps no Secure cookie over
http even there, which is why the development profile's cookie is neither (the BFF's `Program.cs`).

```bash
docker compose up --build -d   # after exporting the eight variables compose.yaml names
```

## Quality gates

Run all of these before opening a pull request.

```bash
dotnet build
dotnet test AzureBank.slnx
dotnet format --verify-no-changes
```

**Name the solution.** A bare `dotnet test` or a filtered run both under-report: see the traps
document for the two distinct ways `--filter` silently drops the BFF suite.

Frontend changes additionally need, from `frontend/`:

```bash
npm run format:check && npm run lint && npm run build && npx vitest run
```

The type gate is `npm run build` (`tsc -b`), **not** `tsc --noEmit` — the root tsconfig is
solution-style, so `--noEmit` skips project references and misses errors the build catches.

**A gate is a delta against `main`, never an absolute count.** "This change introduces no new
alert" survives an untriaged backlog; "zero open alerts" dies at the first one and then gets
ignored — which is how eight security alerts once accumulated while a note still recorded zero.

**Never dismiss a finding to make a number look good.** A dismissal needs a reason that still
convinces on re-reading in six months. If you cannot write one, it is not a false positive, and
weakening the check to pass it defeats the only thing the check was for.

**Never blind-apply an automated fix.** Review-bot suggestions are frequently right and
occasionally confidently wrong. Verify each against the current code. The same applies in reverse:
when a bot challenges something you wrote, check the primary source, then concede or push back on
evidence rather than on conviction.

**No green without runnable proof.** "Should work" is not a test result. If a change is observable
in a browser, verify it in a browser and keep the evidence.

## Merging

**Merging is a human act.** The repository requires a pull request, permits squash only, and has no
bypass. Automated contributors open pull requests and stop there.

**Branches are not deleted after merge.** Several are cited as evidence in decision records — one
holds the only pinned reproduction of a library incompatibility — and deleting one turns that
citation into a dead end.

**Contract changes ship as two pull requests.** The endpoint, the regenerated OpenAPI spec and the
generated types land together in the backend change; the UI that consumes them follows. One pull
request spanning both makes the contract diff unreviewable.

**The spec is regenerated by the test that checks it, not by hand.** `CommittedOpenApiDocumentTests`
fails whenever `docs/api/openapiv1.json` is not byte-for-byte what the API generates — apart from
the line endings and the newlines inside strings, which belong to the machine rather than the
contract (ADR-0053 D3) — and names the first JSON paths that differ. To regenerate, from `backend/`:

    AZUREBANK_REGENERATE_OPENAPI=1 dotnet test --filter "FullyQualifiedName~CommittedOpenApiDocumentTests"

It writes the file and then **fails on purpose**, so the flag can only ever make a build red; re-run
without it to confirm, review the diff, then regenerate the frontend artefacts with
`npm run generate:api` and `npm run generate:zod` from `frontend/`. No running API and no real
secrets are needed — the test host generates the document from the same composition the API uses.
The other route — `node scripts/openapi-spec.mjs regen` against an API started in Development, as
`docs/api/README.md` describes — writes the same bytes, but it needs every validated secret
configured before the API will even start.

**Integration is verified against the real stack before a release.** A green mock-mode run proves
the client's model of the protocol, never that the server agrees with it.

## Commit and pull-request titles

This repository squash-merges, so **only the pull-request title lands on `main`**. Individual
commits on the branch are discarded — commit however you like while working; the title is the one
thing that has to be right.

Plain, imperative, at most 72 characters, naming the concrete outcome rather than a category:

```text
Reject negative balances on concurrent withdrawals
Restrict workflow token permissions and sanitize the logged HTTP method
```

**Delete the `(#NN)` that GitHub pre-fills into the squash subject.** The convention here is a bare
title; the log shows one commit carrying a number and it is an anomaly, not the pattern.

**No `type(scope):` prefix.** Conventional Commits is deliberately not used: its payoff is an
automated changelog and version bump, this repository runs neither, and a consistent plain log reads
better than a half-applied taxonomy.

## Correcting a document

A decision record is never rewritten to look as if it had always been right: what changed is
recorded beside what it changes. This rule lived in the ADR index until 2026-09-15, written down
because it had never been stated and a review round went on it; it applies to every document here,
so it moved.

**The note goes immediately against what it corrects.** That is the load-bearing half: a note in a
section further down is one that the reader of the wrong sentence never reaches, and the reader of
the note has to go hunting for what it refers to. **And nothing is deleted — a superseded clause
is struck in place with `~~…~~` and the note follows it inline**, which is what ADR-0044 already
does at its #231 revisit and ADR-0007 at its envelope note. Struck rather than merely annotated,
because a wrong sentence left looking current is read as current: that is not a hypothetical here,
it is why `docs/runbooks/audit-chain-unavailable.md` went on repeating a claim ADR-0044 had already
withdrawn.

**The first exception is text an operator reads under pressure** — runbooks, printed verdicts,
error strings. There the wrong wording is removed rather than struck, because nobody scrolls past a
struck line during an incident and a `~~` renders as noise in a terminal. **The second is the root
README** (since 2026-09-24): it is the first page a visitor reads, often not an engineer, and a line
about what it used to claim reads there as noise, or as doubt about everything around it. It is
corrected in place without that line; git keeps what it said. Everything else that describes the
system AS IT IS rather than as it was decided — `docs/deferred/`, code comments, XML docs — is
simply corrected in place, with a line saying what it used to claim.

**What this rule does not ask anybody to decide.** An earlier draft of it split corrections by kind
— a decision that was right when made versus a statement of fact that was never true — and that is a
real distinction, but it puts a judgement in the middle of a rule, and a rule with a judgement in it
is one that gets re-argued every time somebody new reads the file. The ADR directory has already
learnt that about MD040, four times across #94, #96, #127 and #129. Adjacency needs no
classification. The two corrections ADR-0044 made before this rule existed (`Corrected 2026-08-25`,
`CORRECTED 2026-08-20`) are already adjacent and quote the superseded wording in full, so nothing is
lost by leaving them as they are; from here the wording is struck in place instead, which costs less
and reads better.

## Code style

Follow the [Microsoft C# conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions).
Beyond those:

| Element | Convention | Example |
|---|---|---|
| Classes, methods | PascalCase | `AccountService`, `GetAccountsAsync` |
| Interfaces | `I` + PascalCase | `IAccountService` |
| Locals, parameters | camelCase | `accountBalance` |
| Private fields | `_camelCase` | `_repository` |

Async methods carry the `Async` suffix. `async void` appears only in event handlers. Nullable
reference types are on; prefer returning `null` explicitly over an empty sentinel, and handle it at
the call site. Throw the typed exceptions from `AzureBank.Shared.Exceptions` with a message that
names the thing that was missing — never a bare `Exception`.

Comments explain constraints the code cannot show. A comment that restates the next line, records
where the code came from, or argues that the change is correct is noise the moment the pull request
merges.

Feature code is organised by role: `Controllers/`, `Services/{Interfaces,Implementations}/`,
`Validators/`, `Mappers/`.

## Tests

```text
tests/AzureBank.Tests/
├── Unit/           # validators, services, utilities
├── Integration/    # API endpoints against a real host
└── Architecture/   # layer dependency and naming rules
```

Arrange–act–assert, one behaviour per test, and a name that states the behaviour rather than the
method under test. New public API surface gets tests; anything touching money gets integration
coverage, because the unit level cannot observe a transaction boundary.

**Tests that pin a decision are load-bearing.** Several ADRs name the tests that hold them, and
deleting one is a reversal of that decision rather than a cleanup. If a pinned test is in your way,
the decision is what needs revisiting.
