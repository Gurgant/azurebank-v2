# Engineering traps

Things that **fail silently or ship green**, and whose fix is not obvious from the failure. They
are not decisions: there was nothing to weigh. Each entry says what goes wrong, why nothing
reports it, and what to do.

Decisions are in [`docs/adr/`](adr/README.md). Frontend conventions, with the traps of testing
Fluent under jsdom, are in [`frontend/CONVENTIONS.md`](../frontend/CONVENTIONS.md).

---

## Database and EF Core

**An explicit transaction must run inside the execution strategy.** `EnableRetryOnFailure` is on,
and a bare `BeginTransactionAsync` throws at run time, with a message about execution strategies
that reads as a configuration problem. Wrap it:

```csharp
await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () => { /* transaction here */ });
```

- **Migrations follow expand → migrate → contract.** Never add a column as non-nullable in one
  step against a populated table.
- **A uniqueness migration fails on pre-existing duplicates, and never deduplicates an identity
  table.** A clean-up inside a migration destroys user rows before anyone sees that it ran. A
  failed migration makes a person decide which row survives.
- **`WITH (ONLINE = ON)` is unavailable here.** LocalDB runs the Express engine, which has no
  online index operations: index migrations run offline.
- **SQL Server only.** Provider-specific migration SQL is deliberate: portability would be
  untested, and the code relies on what the specific SQL guarantees.

## Validation and DTOs

- **Normalise and trim in the DTO setter, not in the service.** FluentValidation runs against the
  DTO as bound: with a trim in the service, the validator inspects one string and another is
  stored.
- **Do not add a `required` member to a DTO that crosses the BFF boundary.** The BFF deserializes
  API responses, and a newly `required` member fails that hop as soon as the two hosts are a
  version apart: it surfaces as a broken BFF, not as a contract change.

## Transactions and money

- **Transactions are immutable and `CreatedAt` is server-stamped.** A test that sets `CreatedAt`
  gets the server's value and asserts against a fiction: to place a row in time, move the
  window, not the data.
- **Money aggregates are computed in SQL, count `Completed` only, and use unsigned amounts**, with
  the direction carried by `Type`. Summing signed amounts on the client gives a wrong answer.
- **The client sends `fromDate` only.** The server defaults `toDate` to now at each request, so a
  refetch after a mutation includes that mutation. A `toDate` sent by the client freezes the
  window at render time, and the new transaction is missing from the summary until a reload.

## Local development

- **The seeder needs `DOTNET_ENVIRONMENT=Development`.** It is a console Generic Host: without
  the variable it runs as Production, its user-secrets do not load, and it exits 2 with `reset
  refused: there is no connection string. Set ConnectionStrings__DefaultConnection. Nothing was
  opened.` `ASPNETCORE_ENVIRONMENT` does not work: the Generic Host reads the `DOTNET_` prefix.
- **Run the API on the `https` profile (7215).** The BFF's proxy cluster points there: with the
  API on the `http` profile, 5068 only, the BFF starts and fails every proxied call.
- **Start the API and the BFF one after the other the first time.** Two parallel first builds
  race on `AzureBank.Shared.dll` and fail with a file lock (CS2012).
- **A running BFF locks `AzureBank.Bff.exe`.** A build of the solution then fails with
  MSB3027/MSB3021, "could not copy … the file is locked", which reads like a corrupted output
  directory and invites a `clean`. Stop the BFF first.
- **`sqlcmd` writes against `AspNetUsers` and `RefreshTokens` need `-I`; reads do not.** Without
  the flag every `INSERT`, `UPDATE` and `DELETE` fails with Msg 1934, "SET options have incorrect
  settings: 'QUOTED_IDENTIFIER'". The cause is the filtered index each table carries. A `SELECT`
  succeeds in the same session, which makes it look like a permissions problem.
- **`DangerousAcceptAnyServerCertificate` belongs only in `appsettings.Development.json`**, never
  in the base file. A stale `bin/Release` output can still carry it: check the source.
- **The `Access-Control-Allow-Origin` header seen in development comes from Vite.** A response
  from the BFF on `:5000` or from the API on `:7215` has none, and the same call through the Vite
  proxy on `:5173` has one. The application has no CORS: the topology is same-origin, and
  cross-site state-changing requests are rejected by Fetch-Metadata on top of `SameSite=Strict`.

## Frontend test infrastructure

**The frontend type gate is `npm run build` (`tsc -b`), not `tsc --noEmit`.** The root tsconfig
is solution-style, so `--noEmit` skips the project references and misses errors the build
catches.

**Run the whole solution's tests: `dotnet test AzureBank.slnx`**, from `backend/`. A filter that
reads as "the AzureBank test projects" fails in two ways:

- **`--filter ~AzureBank.Tests` is malformed.** The condition needs a property name. The
  discoverer throws `Invalid Condition`, and dotnet warns that "the incorrect format can lead to
  no test getting executed". A run that executes nothing has not passed.
- **`--filter FullyQualifiedName~AzureBank.Tests` is well formed and still wrong.** The match is
  a substring of the fully-qualified name, the BFF's tests are namespaced `AzureBank.Bff.Tests`,
  and the run leaves all of them out and reports success.

**Vitest's default reporter prints console output only for failing tests**, so a passing test can
write to `console.error` and no gate shows it: React's `act(...)` warnings, invalid props,
anything an error boundary catches. `src/test/setup.ts` fails the test that wrote to it, and a
test that provokes a logged error on purpose stubs it:
`vi.spyOn(console, 'error').mockImplementation(() => {})`. That assertion's `afterEach` is
registered first (`src/test/hook-order.test.ts`): Vitest runs `afterEach` hooks in reverse
registration order, and a throwing hook skips the ones still to run, the teardown among them.

**`vi.advanceTimersByTimeAsync` is not act-aware.** A component ticking on `setInterval` gets one
un-acted `setState` for each tick advanced. Wrap the advance, not the assertion:
`await act(async () => { await vi.advanceTimersByTimeAsync(ms) })`. `waitFor` and `userEvent`
need no wrapper.

## Tooling

**A browser tab that is hidden cannot be used to judge this application.** In a hidden tab
`requestAnimationFrame` never fires and React 19 freezes partway through a passive update: the
response arrives with a 200 and the spinner spins for ever. Drive a browser whose tab is visible,
and when anything looks hung, check the network tab and the DOM before calling it a bug.

## MSW mocks

- **A block comment cannot quote the glob `*` + `/api/*`.** The `*` followed by `/` closes the
  comment, and what follows becomes code. It compiled, built and passed every test: only eslint's
  `no-unused-expressions` noticed. Describe the glob in prose, or put it in a `//` line comment.
- **A catch-all that returns `undefined` disables `onUnhandledRequest`.** `sessionActivity` is
  registered over every `/api` path and returns `undefined` to fall through to the real handler.
  MSW counts that as a match, so a route with no handler is "handled": `onUnhandledRequest:
  'error'` never fires, and the request escapes to the network as a bare `fetch failed`. A
  sentinel handler registered last answers `501 MOCK_HANDLER_MISSING` and names the route.
- **A measurement quoted in a mock's comment is dated evidence, not a contract.** A mock that a
  test holds to a table measured on one day stays green after the real gate moves. Only a case
  that runs against the real target (`npm run test:contract:real`) proves that the mock still
  matches. When a decision moves a gate, grep the mock for the old table.

## jsdom

**jsdom's missing layout is a wrong answer, not a missing API, and Fluent's focus trap reads it.**
Every element reports `offsetParent === null` and a 0x0 `getBoundingClientRect()`, as a browser
does for an element that is not rendered. tabster concludes that an open Fluent dialog holds
nothing focusable, and 250 ms later puts `aria-hidden="true"` on the dialog's own surface. The
symptom is a `findByRole(role, { name })` that cannot see a control while `getByText` still can,
and it looks like a flake. `{ hidden: true }` or a text query silences it and asserts something
false. `src/test/layout.ts` supplies the two answers tabster reads, and its comment has the
whole chain; `src/test/layout.test.tsx` fails if it is removed.

**With that fixed, a second race is real.** tabster hides the page behind an open dialog and
un-hides it through the same 250 ms debounce after the dialog closes. A bare `getByRole` on the
page straight after a dialog closes fails: use `findBy*`.

## An XML `<summary>` on a DTO property IS the published API contract

The OpenAPI generator lifts the XML doc comment of a request DTO's property into that property's
`description` in `docs/api/openapiv1.json`, and from there into the generated frontend types,
**unless a validation attribute already supplies one**: `[Pin]` on `SetPinRequest.Pin` does. A
property with no such attribute publishes its whole summary: a first draft of
`SetPinRequest.Password` put 1391 characters of internal history and an attack recipe into the
public contract. Code comments call this "the T8 trap".

**Rule:** the `<summary>` of a DTO property is contract prose for a consumer: one or two lines
that say when the field is required and what it means. Everything else goes in a plain `/* … */`
comment, which the generator ignores. In a summary, consecutive `<para>` blocks are joined with
no separator, and `<see cref="CurrentPin"/>` renders as the C# member, not as the JSON field:
use `<c>currentPin</c>`. After touching a DTO, regenerate the document
([`docs/api/README.md`](api/README.md)) and read the property's `description` back.

## A `<param>` on a renamed `[FromHeader]` argument lands on the REQUEST BODY

`[FromHeader(Name = …)]` renames the OpenAPI parameter, here to `Step-Up-Authorization`, so a
`<param name="stepUpAuthorizationId">` tag matches nothing. The generator does not drop the tag:
it applies it to the `requestBody`, in place of that argument's own description, and leaves the
header parameter with none. The compiler pushes towards it: documenting only `request` raises
CS1573 ("has no matching param tag").

**Rule:** describe every argument whose OpenAPI name differs from its C# name (a renamed
`[FromHeader]`, `[FromQuery]` or `[FromRoute]`) with `[Description("…")]`
(`System.ComponentModel`), and the `[FromBody]` argument of the same signature too. Then
regenerate and read the description back out of the JSON, at the `requestBody` and at the
`parameters` entry.

## A new `ValidateOnStart` option must be taught to five places, and the test suite is not one of them

`.Validate(…).ValidateOnStart()` turns a missing value into a crash at startup, which is right
for a secret such as `StepUp:BindingKey` (ADR-0042). The test suite cannot see a place that was
missed, because `CustomWebApplicationFactory` injects the value with `UseSetting`: with every
test green, CI's real-stack job failed with `OptionsValidationException: StepUp:BindingKey must
be configured`. When adding a required option:

1. `appsettings.json`: the parts that are not secret.
2. `appsettings.Development.json.example`: the section, and the `user-secrets` commands in its
   header comment.
3. The recipe in [`engineering-practices.md`](engineering-practices.md#local-setup) and the table
   of variables in `backend/src/AzureBank.Api/README.md`.
4. `.github/workflows/*.yml`: the variable, and every "Start API" step. `ci.yml` has two (the
   `real-stack` and the `conformance` jobs), `contract-tests.yml` one.
5. `CustomWebApplicationFactory`: `UseSetting`, the one that makes the tests pass while the rest
   is still missing.

A secret also goes into `compose.yaml` and into the Azure deployment (`infra/main.bicep`,
`infra/secrets.ps1`). Grep for an existing required secret in both spellings
(`Idempotency__HashKey` and `Idempotency:HashKey`) and mirror every hit.

## The dev database goes stale and EVERY money endpoint answers 500

Nothing after a merge touches a development database, so it falls behind the migrations. With
`AzureBankDev` two migrations behind, deposit, withdraw and both transfers answered 500:

```text
String or binary data would be truncated in table 'AzureBankDev.dbo.Transactions',
column 'TransactionNumber'. Truncated value: 'TXN-20260817-AJKNG5F'.
```

SQL Server prints the value already truncated to the column's width, so it looks as if it should
have fitted: count what `IdGenerator.GenerateTransactionNumber` produces, 24 characters. A
missing table shows as `500 Invalid object name`. No suite catches either: the tests and CI
build their schema from the migrations.

```bash
cd backend/src/AzureBank.Api
dotnet ef database update --no-build --connection "Server=(localdb)\MSSQLLocalDB;Database=AzureBankDev;Trusted_Connection=True;TrustServerCertificate=True"
```

- `--connection` is not optional. The plain form fails with "The ConnectionString property has
  not been initialized": `DesignTimeDbContextFactory` reads `appsettings.json`, where
  `DefaultConnection` is `""`, and the git-ignored `appsettings.Development.json`, never
  user-secrets or the environment (ADR-0060, decision 8).
- The `cd` is part of the recipe: the factory resolves `../AzureBank.Api` from the current
  directory, and where that folder is missing it builds a context with no connection string.
- `dotnet ef migrations list` takes the same `--connection` and marks each migration `(Pending)`
  against that database: the diagnosis in one command.

## A tool that writes source can inject a control character the compiler accepts

A regex in `MoneyFormattingTests` began with a literal `U+0008` BACKSPACE where a backslash-b was
meant: the escape was expanded on its way to disk, and a C# verbatim string does not process
escapes. The guard could match nothing and reported clean. The compiler accepted it, `grep`
rendered it as nothing, and only `od -c` showed it, as one token where a backslash and a `b` are
two. `SourceHygieneTests` fails the build on any control character outside tab, CR and LF in
hand-written source. When a script writes C#, prefer a form with no backslash, `(char)0x08` and
not `'\b'`.

## A guard that has never been watched refusing anything is a wish

A rule that reports clean looks the same as a rule that cannot report anything else: a corrupted
pattern, or a path filter that eats its own input (an unnormalised root containing `\bin\` skips
every file while `Directory.Exists` still answers true). So a source-scanning guard in
`tests/AzureBank.Tests/Architecture/` carries both halves: **liveness**, an assertion that the
scan read a plausible number of files, and **coverage**, a `[Theory]` that drives the detector
against shapes that are not in the tree, so that the rule is seen refusing on every run.

## A `$(PkgSomePackage)` path property is EMPTY unless the PackageReference asks for it

NuGet defines `Pkg<PackageId>` only when the `PackageReference` sets
`GeneratePathProperty="true"`. Without it the property is the empty string, an
`<Analyzer Remove="$(Pkg…)/analyzers/…" />` matches nothing, and the build does not warn. A
target written that way in `AzureBank.Api.csproj` was meant to let `[EndpointSummary]` attributes
set the published titles, and none of them ever reached the document (ADR-0053). Check the
property, as in `dotnet msbuild -getProperty:PkgMicrosoft_AspNetCore_OpenApi`, and check the
artefact the target is meant to change, here the committed document, not the build log.

## An OpenAPI transformer that ASSIGNS silently discards what the controller declared

A transformer that sets `operation.Responses["401"] = …` replaces what a controller declared with
`[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]`: the published
contract is then worse than the source, and the source looks right. Regenerating cannot reveal
it: the document and the generated artefacts both derive from the transformer, so the drift gate
compares two copies of one wrong answer. Only an HTTP call to the running API disagrees.

Fill gaps and never assign: `TryAdd`, or a helper such as `ProblemDetailsResponses.Declare`. When
a transformer's comment rests on the framework's default ("ASP.NET Core returns empty 401s"),
check that this application still has it: here `OnChallenge` calls `context.HandleResponse()`,
which replaces it.

## A `{id:guid}` route constraint 404s; it never produces a binding 400

A route constraint takes part in route matching. A segment that is not a GUID matches no route,
so MVC is never entered and nothing binds or validates. The answer is a 404 from the framework,
not from `GlobalExceptionHandler`: `application/problem+json` with `type`, `title`, `status` and
a W3C trace-context `traceId`. An unconstrained string parameter cannot fail either. A
documented 400 for "invalid path parameter format" is a response nobody can produce.

---

## `dotnet ef` reads the compiled assembly, not your source files

`dotnet ef migrations add … --no-build` scaffolds from the dll. Scaffolding after adding an entity
without a rebuild gives a migration with an empty `Up()`, and no error or warning. Deleting it
and generating it again under the same name then fails with "the name is used by an existing
migration", because the deleted `.cs` is still in the assembly. Rebuild between EF operations.

- `dotnet ef migrations remove` fails with "The ConnectionString property has not been
  initialized", and has no `--connection`. Delete the migration's files by hand and rebuild.
- `--connection` exists on `database update` and `migrations list`, not on `migrations add` or
  `migrations remove`.

## `datetime2` stores no `DateTimeKind`, so a hash over a formatted timestamp changes on read

`DateTime.ToString("O")` ends in `Z` when `Kind` is `Utc` and not when it is `Unspecified`.
`datetime2` has no kind, so a value written from `DateTime.UtcNow` comes back `Unspecified` and
formats differently. A hash, a signature or a cache key derived from that string does not survive
a round trip. The EF InMemory provider hides it: its identity map hands back the object that was
written. Hash `Ticks`, an integer that is exact through `datetime2(7)`, or store a
`DateTimeOffset`.

## `UPDLOCK, HOLDLOCK` outside a transaction is decoration

EF opens its implicit transaction inside `SaveChanges`. Code in a `SaveChanges` override that
runs before `base.SaveChanges` is not in it: a locking read there auto-commits and drops its lock
at once, and the write it was meant to protect happens afterwards, alone. Under concurrent
writers it shows as a duplicate key on the unique index `IX_AuditEvents_Sequence`; without that
index, two rows would silently claim the same predecessor. Open the transaction explicitly around
the read and the write, and skip it when the caller already has one
(`Database.CurrentTransaction is not null`) or the provider is not relational.

## The test host is not the production host: the retrying strategy is opt-in

`AddInfrastructure` configures the API with `EnableRetryOnFailure`, and EF refuses a
user-initiated transaction under a retrying strategy. `CustomWebApplicationFactory` rebuilds the
`DbContext` registration and leaves that strategy off unless a test calls
`EnableSqlRetryOnFailure()`, so that an injected transient fault surfaces. So
`Database.BeginTransaction()` in shared code passes every test and answers 500 on the real API:
"The configured execution strategy 'SqlServerRetryingExecutionStrategy' does not support
user-initiated transactions."

- Code that opens its own transaction goes through `Database.CreateExecutionStrategy()`:
  `AuthService.RegisterAsync` is the worked example.
- When such code is added, one SQL Server test opts into `EnableSqlRetryOnFailure()`, or nothing
  exercises the production configuration.

Anything attached to the production registration is absent under test unless the factory adds it
back, which is why the audit chain is in the `DbContext` class and not in a
`SaveChangesInterceptor`. On SQL Server the factory adds back the connection defaults and the
host's interceptors, the commit gate among them (ADR-0058). It leaves out the warning level of an
EF retry and production's retry budget: an opted-in test has 3 retries with the back-off capped
at 5 s, production 4 capped at 10 s (ADR-0058 D2).

## "The writer was called" is not evidence that a row exists

`IAuditService.Record` only calls `Add`: the caller's `SaveChanges` persists the row (ADR-0044
D1). So a unit test that asserts `_auditMock.Verify(a => a.Record(…), Times.Once)` on a
`Mock<IAuditService>` passes whether or not anything is written. With `Record` called after
`UserManager.UpdateAsync` had saved, and nothing saving again, `POST /api/auth/pin` answered 200
and `AuditEvents` held no row, with the whole suite green.

- A writer whose contract is "add, never save" needs one test that reads the store after driving
  the real endpoint, not one that watches the writer.
- When placing such a writer, check what performs the save: `UserManager.UpdateAsync`,
  `SignInManager`, a repository and `ExecuteUpdate` are saves that a search for
  `_context.SaveChangesAsync` misses, and `ExecuteUpdate` commits without flushing tracked
  entities.

## Only a grant whose session ended trips the tripwire

`RefreshTokenReuse` is raised in one case: a grant revoked with the reason `SessionEnded`,
presented in a request the API received after that revoke (ADR-0057 §4.3). A grant revoked
through `/api/auth/logout`, by the migration or by a runbook's SQL is refused with a log line and
no event. A test that wants the tripwire revokes through `/api/auth/revoke`, as the BFF does when
a session ends, and presents the grant afterwards, as `AuditChainSqlServerTests` does.

**A test whose setup already satisfies its postcondition proves nothing.** "The user's other
session is still active" is worth asserting only when there is another session (sign in twice
first), and only once breaking the code on purpose has turned the assertion red.

## Three binding kinds, two parsers — and a `Guid` does not mean the same thing in each

A `Guid` in a route or a header is parsed by MVC's `TryParse`: it accepts the `D`, `N`, `B`, `P`
and `X` forms, in any case, and trims surrounding whitespace. A `Guid` that is a member of a JSON
body is parsed by `System.Text.Json`: the `D` form only, in any case, and untrimmed. The same
value is accepted on one surface and refused on the other, so the mock
(`frontend/src/mocks/handlers.ts`) has a helper for each: `parseGuid` for a route or a header,
`parseBodyGuid` for a body member. There, `fingerprint(raw)` stays above `bindAccountIds`, over
the bytes on the wire: an idempotency key is keyed on what the caller sent, not on a normalised
value.

## `Guid.CreateVersion7()` is not monotonic within a millisecond, and SQL Server disagrees about order

Version-7 GUIDs are time-ordered to the millisecond only: two created in the same millisecond
have no defined order. Measured in a burst, 44,712 of 89,508 adjacent pairs were out of order.
And SQL Server sorts `uniqueidentifier` on a different byte order from .NET's `Guid.CompareTo`,
so a sort that looks right in memory is not the one the database performs.

**Never order by `Id` and call it creation order.** Order by the column that means what is wanted
(`Sequence`, `OccurredAt`, `CreatedAt`); if there is none, that is the finding. When the code and
its test share the wrong assumption, the test confirms the bug.

## A withdrawn argument is a guard, so it has to be framed as one

A rejected argument is kept where it sounds better than the rule that replaced it: otherwise
somebody derives it again and reopens the hole. Its reader is the person about to loosen the
condition, and that person skips history. So in code and in runbooks it is an imperative, the
constraint first ("DO NOT WIDEN THIS TO THE COUNT ALONE. The wide version is the one that helps
an attacker. The argument for it reads well: …"), and not a label such as "A withdrawn argument,
left visible". In a decision record it is a line under Rejected, with its reason. When the gate,
flag or branch it protects is removed, the text goes in the same commit.

## A notice that reaches the session holder is not a notification

NIST SP 800-63B-4 §4.1.2.1 asks that the subscriber be notified of a new authenticator "via a
mechanism independent of the transaction binding" it. The cheap reading is a line on the success
screen, a toast, an inbox item — and every one of them reaches whoever holds the session, which in
the threat model is the attacker who just enrolled the PIN. §4.1.2.2 says where in-session text
belongs: *in addition to* the notice, as instructions for a mishap, never instead of it. The same
goes for a log line and the audit row, which reach the system's operator and not the subscriber.
ADR-0045 records what does count, and where that stops.

The test for a channel is one question: **could the attacker who caused the event see or prevent
the notice?** If yes, it is a receipt, not a notification.

## A send inside the request inverts D1 — write the row, deliver later

Sending a notice inside the request that caused it is wrong in both places. After the save, a
crash between the commit and the call loses the notice with no record that it was owed. Inside
the save, the request holds the audit tail lock (`UPDLOCK, HOLDLOCK`) for as long as the I/O
takes, and a rollback after the send leaves a message about an enrolment that never happened.
Record the obligation in the same transaction as the action, as the audit row is (ADR-0044 D1),
and deliver later, from the row (ADR-0045 D1 and D3, ADR-0048).

## Restoring a file with `mv` from a backup can leave the build running the MUTATED binary

To watch a test go red, a source file is changed, built and put back. `cp f f.bak` before and
`mv f.bak f` after restores the content and the old timestamp. The backup is older than the dll
the changed build produced, MSBuild's incremental check sees a source older than its output, and
the next `dotnet test` does not recompile: every later run still executes the changed code, and
a red result can be red for the wrong reason. A plain rebuild skips the project too.

**Restore by writing, not by moving**: `cp -f f.bak f` gives the file a new mtime, and so does
`touch f` after an `mv`. After any restore, run the baseline and watch it pass before the next
change: a baseline that still fails is the stale binary.

## An early 413 that leaves the body unread can cost the NEXT request a 502

The idempotent endpoints refuse a body over 32 KB from its `Content-Length`, before buffering or
hashing it (ADR-0009, decision 7). When that 413 went out keep-alive with the body unread,
Kestrel drained the body, hit the endpoint's 32 KB limit and aborted the connection. Through the
BFF that was a 502, or a reset of the client's connection (`ECONNRESET`), or, once YARP had
pooled the connection, a 502 for the next request on it, anyone's. The in-memory test host cannot
show it: it copies the whole request body itself.

**Read the body before an early refusal, or close the connection, and give the read a
deadline.** `OversizedBodyDrain` reads and discards up to 1 MiB, for five seconds at most, before
the 413. Above that size, or once the time is up, the 413 carries `Connection: close`. The four
money endpoints and the four authorisation mints go through it (`OversizedBodyDrainTests`,
`MintOversizedBodyDrainTests`, `KestrelRequestSizeLimitTests`). A chunked body has no length to
check first: at a mint the server's refusal during the read becomes the same 413, with
`Connection: close`. Not covered: on Kestrel no test sends a mint more than 1 MiB or stalls a
body, and through the BFF a sender that slow gets a 502 at 13 s, not the 413.

- **Stop a body read with `PipeReader.CancelPendingRead`, not with its cancellation token.** A
  read cancelled by its token leaves Kestrel's body reader mid-read, and Kestrel's own drain then
  logs an error for every such request: `automatic draining of the request body failed because
  the body reader is in an invalid state`.
- **`WebApplicationFactory`'s default client does not send a body that never ends.** Its redirect
  handler copies the whole request body before sending any of it, so a test body that stalls on
  purpose never leaves the client. Create that client with `AllowAutoRedirect = false`.

## A token appended to `ExecuteSqlRawAsync(sql, a, b)` becomes a SQL parameter

`ExecuteSqlRawAsync(string, params object[])` takes every argument after the SQL as a parameter
value, and a `CancellationToken` is an `object`. So `ExecuteSqlRawAsync(sql, resource, timeout,
ct)` compiles, forwards no token and fails at run time, on SQL Server only: "The current provider
doesn't have a store type mapping for properties of type 'CancellationToken'." The in-memory
provider never runs raw SQL. `FindAsync(id, ct)` has the same shape, with the token taken as a
second key value. The token goes after a collection:
`ExecuteSqlRawAsync(sql, new object[] { resource, timeout }, ct)` and `FindAsync([id], ct)`.
`CancellationFlowTests` refuses the other shape.

## MVC's JSON formatter swallows a cancelled `RequestAborted` and sends an empty success

`SystemTextJsonOutputFormatter` catches the `OperationCanceledException` of its write whenever
`RequestAborted` is cancelled, and returns. The status, already 200 or 201, goes out with an
empty or cut body, and nothing above it sees an exception. Behind `IdempotencyMiddleware` that
empty body was stored as the answer and replayed to every retry of the key. So
`DeadlineResultFilter` gives the write a token that cannot be cancelled once a commit has
started, and an empty 2xx is never stored for replay (ADR-0058 D5 and D8). Anything else that
lets something other than the client cancel `RequestAborted` meets the same formatter.

## A local outage can outlive the database: the pool's blocking period

After a failed open, SqlClient's pool hands the cached error to every caller for 5 s, doubling up
to a minute, without trying the server again: `Pool Blocking Period=Auto`, the default, does that
for every server except Azure SQL. Measured on the compose stack on 2026-09-29: the first 200
came 26.1 s after the database was answering again. So a local outage with blocking on says
little about how the retry budget behaves on Azure. Every host opens with `NeverBlock` unless its
connection string says otherwise (ADR-0058 D1), which is what `Auto` already gives Azure.
