# Engineering practices

How this project is set up locally, checked and merged. It is deliberately not a
`CONTRIBUTING.md`: this is a solo portfolio project with no outside contributors.

Decisions are in [`adr/`](adr/README.md), the sharp edges that fail silently in
[`engineering-traps.md`](engineering-traps.md), and the frontend's rules in
[`../frontend/CONVENTIONS.md`](../frontend/CONVENTIONS.md).

---

## Local setup

The one copy of these instructions. Commands are bash, from the repository root.

Configuration comes from **user-secrets**, never from a committed settings file, and the API
refuses to start without them. The API, the BFF and the seeder each have a secret store of their
own, so `--project` is not optional and a shared value is set in each. Three values are shared
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

- If the two peppers differ, seeding succeeds and every seeded PIN then fails verification.
  When rotating the pepper, keep the whole ring in one secret provider (ADR-0011, decision 4).
- `Demo:ClientKeySecret` is not set above: the public demo is off unless `Demo:Enabled` is true,
  and only then does the API refuse to start without 32 characters of it (ADR-0063).
- What each setting is for is in [the API's README](../backend/src/AzureBank.Api/README.md).

**Calling the API by hand.** The API serves one client, the BFF (ADR-0055): it answers 401 to a
request without the header `X-AzureBank-Service-Key`, whose value is `$SERVICE_KEY`, except on
`/health/*` and, in Development, on `/openapi` and `/scalar`. So curl, Bruno and the Scalar
page's "Try it" need that header. The six token endpoints and the session-stamp feed also answer
404 unless the call comes over loopback with exactly one `X-AzureBank-Token-Road: bff` header
(ADR-0057 §4.2). The Bruno command line is in
[`tests/api-collection/README.md`](../tests/api-collection/README.md).

**Database: one command, not `dotnet ef`.** It drops, migrates and seeds.

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project backend/tools/AzureBank.Seeder -- reset --confirm
```

The variable is required, and `ASPNETCORE_ENVIRONMENT` does not replace it. Seeding also creates
the Identity roles, without which registration answers 500. The seeded demo user is
`john@example.com` / `Test123!`, PIN `123456`, with two months of history on two accounts.

**Running it.** Three processes, each in a terminal of its own. Start them one after the other
the first time: two parallel first builds race on `AzureBank.Shared.dll`.

```bash
dotnet run --project backend/src/AzureBank.Api --launch-profile https   # https://localhost:7215
dotnet run --project backend/src/AzureBank.Bff --launch-profile http    # http://localhost:5000
cd frontend && npm ci && npm run dev                                     # http://localhost:5173
```

The API must run the **https** profile: the BFF's proxy points at 7215, and with the http profile
the BFF starts and then fails every proxied call. A settings file tells the BFF to accept any
certificate on that hop, in Development only. It is git-ignored: copy it once from its example.

```bash
cp backend/src/AzureBank.Bff/appsettings.Development.json.example backend/src/AzureBank.Bff/appsettings.Development.json
```

**Running the tests.** Stop the API and the BFF first: Windows keeps their executables locked
while they run, and the build that `dotnet test` starts fails with
`MSB3027: Could not copy … apphost.exe`.

```bash
dotnet test backend/AzureBank.slnx    # name the solution
cd frontend && npm run build && npx vitest run
```

A `dotnet test` filtered on `AzureBank.Tests` drops the BFF's tests and still reports success,
and `tsc --noEmit` skips this tsconfig's project references
([engineering traps](engineering-traps.md#frontend-test-infrastructure)). The concurrency proofs
need a real SQL Server and skip without one:

```bash
AZUREBANK_TEST_SQLSERVER="Server=(localdb)\\MSSQLLocalDB;Database=AzureBankProofs;Trusted_Connection=True;TrustServerCertificate=True" \
  dotnet test backend/AzureBank.slnx --filter "Category=SqlServer"
```

**Traces, metrics and logs, locally.** Both services emit them over OpenTelemetry (ADR-0016). A
request through the BFF is one trace, and every ProblemDetails carries the bare 32-hex `traceId`
that pastes into Tempo's search. Export is opt-in: without the variable nothing is emitted.

```bash
docker compose -f observability/docker-compose.yml up -d   # Grafana LGTM on 127.0.0.1:3000

# Export in the terminal that starts each service — a bare assignment stays shell-local
# and the child process never sees it. Use 127.0.0.1, not localhost: on Windows the name
# resolves to ::1 first and the collector is listening on IPv4.
export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318
export OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

In PowerShell: `$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://127.0.0.1:4318"`. Telemetry is PII-safe
by design: emails are masked and amounts never appear in a log line (ADR-0017).

**Running the images as Production.** `compose.yaml` at the root runs the API and BFF images as
Production against SQL Server in a container, with the API on loopback behind the BFF. Its
header names the eight variables it requires and says how the database is prepared (ADR-0060).

```bash
docker compose up --build -d   # after exporting the eight variables compose.yaml names
```

- The database is published on 127.0.0.1:14330, not 1433: a SQL Server installed on the host
  usually holds 1433, publishing over it does not fail, and a tool aimed at it then reaches the
  host's instance.
- Sign in from a Chromium browser. The session cookie there is `__Host-` and Secure: Chromium
  keeps it on `http://localhost`, and Safari keeps no Secure cookie over http even there.
  Measured on 2026-09-25: the e2e suite, 24 of 24, against the two containers.
- `compose.demo.yaml`, an override of that file, turns the public demo on (ADR-0063). Its header
  says how to run it. It asks for a ninth variable, `DEMO_CLIENT_KEY_SECRET`, and every command
  that loads the file needs it, `down` included.

**The public demo on that stack, and in the dev loop.** `npm run test:e2e:demo`, from
`frontend/`, drives the demo in Chromium on `http://localhost:5000`. CI runs it on every pull
request, in the job `demo-stack`, on a stack the job builds from the two compose files and takes
down. By hand, run it on a stack nobody else is using: it **restarts the BFF's container and
then the API's**. [`frontend/playwright.demo.config.ts`](../frontend/playwright.demo.config.ts)
has how to run it, the caps a repeated run meets (five runs fit in a day on one volume) and what
a run leaves on disk to delete. In the dev loop the demo's screens need `AZUREBANK_DEMO=true` in
the dev server's environment ([`frontend/README.md`](../frontend/README.md#run-it)). That sets
the page and not the backend: a BFF with the demo off answers the claim 404.

The default suite, `npm run test:e2e`, expects the demo off. On the compose stack one of its
tests, `e2e/pinLockExpiry.spec.ts`, failed in two of four whole runs on 2026-10-05 with its own
expectations met: the browser's context did not close within the test's 30 s. Why is not known.

## Quality gates

Run these before opening a pull request, for every part the change touches. From `backend/`:

```bash
dotnet build AzureBank.slnx --configuration Release
dotnet test AzureBank.slnx --configuration Release --no-build
dotnet format AzureBank.slnx --verify-no-changes
```

From `frontend/`:

```bash
npm run format:check && npm run lint && npm run build && npm test && npm run test:contract:mock
```

For the deployment scripts, the templates and the workflows, from the root:

```bash
python -m unittest discover -s infra -p 'test_*.py'
actionlint        # for a change to a workflow; with ShellCheck installed, as in CI
```

- **Build in Release.** Warnings are errors there, as in CI: a Debug build passes with a warning
  that CI refuses.
- **Name the solution**, and take `npm run build` as the type gate, not `tsc --noEmit`
  ([traps](engineering-traps.md#frontend-test-infrastructure)).
- **A gate is a delta against `main`, never an absolute count.** "This change introduces no new
  alert" survives an untriaged backlog; "zero open alerts" dies at the first one.
- **Never dismiss a finding to make a number look good.** A dismissal needs a reason that still
  convinces in six months; without one it is not a false positive.
- **Never blind-apply an automated fix.** A review bot's suggestion is often right and sometimes
  confidently wrong: verify each against the current code, and answer a bot's challenge from the
  primary source.
- **No green without runnable proof.** "Should work" is not a test result. A change that can be
  seen in a browser is verified in a browser.

**CI also counts what the tests run.** On every pull request the job `coverage` runs the
backend's two test projects with a collector, the API's whole and against a SQL Server so that
the concurrency proofs run too, and then the frontend's unit and component tests
(`npm run test:coverage`). Its summary gives the share of the lines of hand-written code that
ran, for each backend assembly and in total, and the job fails under a floor set a little under
the last figure measured: a tripwire, not a target.
[`infra/coverage_summary.py`](../infra/coverage_summary.py) does the counting. Two test
projects run the same assemblies, so their files are joined line by line: a line ran when
either saw it run.

- **Left out of the backend's figure:** what the build writes under `obj/`, which is the output
  of the source generators, and the EF migrations. Nobody writes either by hand, and together
  they are larger than the code they sit beside: counted, they move an assembly's figure by tens
  of points. They are printed under the table, apart.
- **Left out of the frontend's figure:** the tests and the helpers under `src/test`, the mock
  backend under `src/mocks`, the contract and integration suites, and the generated types and
  validators ([`vitest.config.ts`](../frontend/vitest.config.ts)). A source file that no test
  loads counts with all its lines unrun.
- **Not seen at all:** a backend assembly that no test project loads is in no coverage file, and
  what only the suites against the running stack reach counts as not run.
- **A line that ran is not a line that was checked.** The figure says where no test goes. It
  says nothing about what the tests assert.

## Merging

- **Every pull request is merged by hand after review.** `main` requires a pull request,
  permits squash only, and has no bypass list.
- **Branches are not deleted after merge.** A squash leaves one commit on `main`, and the branch
  is where the commits behind it stay.
- **Contract changes ship as two pull requests.** The endpoint, the regenerated OpenAPI document
  and the generated types land in the backend change; the UI that consumes them follows.
- **Integration is verified against the real stack before a release.** A green mock-mode run
  proves the client's model of the protocol, never that the server agrees with it.

**The OpenAPI document is regenerated by the test that checks it, not by hand.**
`CommittedOpenApiDocumentTests` fails whenever `docs/api/openapiv1.json` is not what the API
generates (ADR-0053). To regenerate, from `backend/`:

    AZUREBANK_REGENERATE_OPENAPI=1 dotnet test --filter "FullyQualifiedName~CommittedOpenApiDocumentTests"

It writes the file and then **fails on purpose**, so the flag can only make a build red. It
needs no running API and no secrets. [`docs/api/README.md`](api/README.md) has what follows it,
the frontend's artefacts to regenerate, and the other route, against a running API.

## Commit and pull-request titles

This repository squash-merges, so one commit lands on `main`: **its title is the pull request's
and its text is a shorter one written for the commit**, both put there by hand at the merge.
The commits of the branch do not land, but they stay public, on the pull request's page and on
the branch. They follow the same rules as any text here: a subject that says what the commit
does, no line that credits a second author, no at-sign before a name, no secret. CI reads the
commits of a pull request and fails on two things: a line that begins with a key ending in
`-by:`, the form in which git names a signer, a reviewer or a second author, and an at-sign
before a name outside backticks. Any other closing line, such as `Gate: 12 passed`, passes. A
pushed commit is not rewritten, so a branch with a message that fails is replaced by a new one.

The title is plain, imperative, at most 72 characters, and names the concrete outcome, not a
category:

```text
Reject negative balances on concurrent withdrawals
Restrict workflow token permissions and sanitize the logged HTTP method
```

Delete the `(#NN)` that GitHub pre-fills into the squash subject: the convention is a bare
title. No `type(scope):` prefix: Conventional Commits pays off through an automated changelog and
version bump, and this repository runs neither.

## Correcting a document

Every document here says what is true now. A sentence that turns out wrong, or that the code has
moved away from, is corrected in place, with no line about what it used to claim: git keeps what
it said. A date stays where it is a fact about the system, such as the day something was measured.

Prose is in British spelling. `python infra/check_british_spelling.py`, run from the root, reads
every page for the American forms that `infra/british_spelling_words.txt` lists. On a pull
request the job "Prose spelling (British)" runs it and reports: a finding is a red mark on the
pull request, and the job is not among the required checks, so a word does not block a merge.
When it is red, correct the word; a name that keeps its own spelling goes in backticks, or in
`infra/british_spelling_allowed.txt` with its reason. The job also runs
`python -m unittest discover -s infra -p 'prose_test_*.py'`, which fails when a page writes a
word in -ise or -yse whose American twin the list does not hold yet, and says where to add it.

## Code style

Follow the [Microsoft C# conventions][csharp]: PascalCase for classes and methods, `I` +
PascalCase for interfaces, camelCase for locals and parameters, `_camelCase` for private fields.
Beyond those:

- Async methods carry the `Async` suffix, and `async void` appears only in event handlers.
- Nullable reference types are on: return `null` explicitly, not an empty sentinel.
- Throw the typed exceptions of `AzureBank.Shared.Exceptions`, with a message that names what was
  missing, never a bare `Exception`.
- A comment explains a constraint the code cannot show, never what the next line does or where
  the code came from.
- Feature code is organised by role: `Controllers/`, `Services/{Interfaces,Implementations}/`,
  `Validators/`, `Mappers/`.

[csharp]: https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions

## Tests

`backend/tests/AzureBank.Tests/` has `Unit/` (validators, services, utilities), `Integration/`
(the API's endpoints against a real host) and `Architecture/` (layer, naming and source rules).
Arrange, act, assert; one behaviour per test; a name that states the behaviour, not the method
under test. New public API surface gets tests, and anything touching money gets integration
coverage, because the unit level cannot observe a transaction boundary.

**Tests that pin a decision are load-bearing.** Several decision records name the tests that hold
them, and deleting one reverses that decision. If a pinned test is in the way, the decision is
what needs revisiting.
