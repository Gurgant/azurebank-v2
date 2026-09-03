# Engineering traps

Things that **fail silently or ship green** and whose fix is not obvious from the failure. These
are not decisions — there is nothing to weigh, and no alternative was considered. They are the
sharp edges this codebase actually has, written down because each one cost real time to find and
none of them announces itself.

Decisions live in [`docs/adr/`](adr/README.md). Frontend conventions live in
[`frontend/CONVENTIONS.md`](../frontend/CONVENTIONS.md). This file is for the traps.

---

## Database and EF Core

**An explicit transaction must run inside the execution strategy.** `EnableRetryOnFailure` is on,
and a bare `BeginTransactionAsync` throws at runtime because a retrying strategy cannot own a
transaction it did not open. Wrap it:

```csharp
await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () => { /* transaction here */ });
```

The failure is a runtime exception with a message about execution strategies that reads as a
configuration problem rather than a code-shape problem.

**Migrations follow expand → migrate → contract.** Never add a column as non-nullable in one step
against a populated table.

**A uniqueness migration fails fast on pre-existing duplicates, and must never auto-deduplicate an
identity table.** "Cleaning up" duplicates during a migration destroys user rows silently, and the
rows are gone before anyone notices the migration succeeded. Failing the migration is the correct
outcome: it forces a human to decide which row survives.

**`WITH (ONLINE = ON)` is unavailable here.** Online index operations are edition- and
version-dependent — newer SQL Server releases widened where they are supported — but the target
that matters for local development is **LocalDB**, which runs the Express engine and does not
support them at all. Index migrations run offline.

**SQL Server only.** Provider-specific migration SQL is deliberate. Do not "make it portable": the
portability would be untested, and the specificity is buying correctness we rely on.

## Validation and DTOs

**Normalise and trim in the DTO setter, not in the service.** FluentValidation runs against the DTO
as bound, so a service-side trim means the validator inspects the raw value and the stored value is
a different string from the validated one.

**Do not add `required` members to DTOs that cross the BFF boundary.** The BFF deserializes API
responses, and a newly-`required` member hard-fails that hop the moment the API is one version
ahead — an error that surfaces as a broken BFF, not as a contract change.

## Transactions and money

**Transactions are immutable and `CreatedAt` is server-stamped.** A test that wants a row at a
particular time cannot write one: it must **move the window**, not the data. Tests that try to set
`CreatedAt` silently get the server's value and then assert against a fiction.

**Money aggregates are computed in SQL, count `Completed` only, and use unsigned amounts** with the
direction carried by `Type`. Summing signed amounts client-side gives a different — wrong — answer.

**The client sends `fromDate` only.** The server defaults `toDate` to _now_ per request, which is
what lets a tag-invalidated refetch include the very mutation that triggered it. Sending an explicit
`toDate` from the client freezes the window at render time and the new transaction disappears from
the summary until the next reload.

## Local development

**The seeder needs `DOTNET_ENVIRONMENT=Development`.** It is a console Generic Host, so it defaults
to Production, its user-secrets do not load, and it dies with `PinPepper must be ≥32 chars` — a
message that points at configuration rather than at the missing environment variable. Note that
`ASPNETCORE_ENVIRONMENT` does **not** work here; the Generic Host reads the `DOTNET_` prefix.

**Run the API on the `https` profile (7215).** The BFF's proxy cluster points there, so starting the
API on `http`/5068 produces a BFF that builds, starts, and fails every proxied call.

**Start the API and the BFF sequentially the first time.** Two parallel first builds race on
`AzureBank.Shared.dll` and fail with a file lock (CS2012) that looks like a corrupted build.

**A running BFF locks `AzureBank.Bff.exe`, and the build failure blames the wrong thing.** Building
the solution while the BFF is up fails with MSB3027/MSB3021 — "could not copy … the file is locked"
— which reads like a corrupted output directory and invites a `clean`. Stop the BFF first. This bites
hardest mid-session, when the stack is up for a live measurement and the next step is a rebuild.

**`sqlcmd` WRITES against `AspNetUsers` need `-I`; reads do not.** Without it, every write fails
with *"SET options have incorrect settings: 'QUOTED_IDENTIFIER'"* and a long list of possible
causes. The actual cause is the filtered index on the table, and the fix is the one flag. Reads
succeed in the same session, which is what makes it confusing — it looks like a permissions or
connection problem rather than a session option. Measured on a scratch table carrying one filtered
index, so the split is observed rather than assumed:

| without `-I` | result |
| ------------ | ------ |
| `SELECT` | succeeds |
| `INSERT` / `UPDATE` / `DELETE` | all fail, Msg 1934 |

**`DangerousAcceptAnyServerCertificate` belongs only in `appsettings.Development.json`.** It must
never appear in the base file. Be aware that stale `bin/Release` artifacts can still carry it and be
mistaken for evidence that it was configured in the base file — check the source, not the output.

**The `Access-Control-Allow-Origin` header you see in dev comes from Vite, not from this
application.** Neither service emits it — measured directly, with the Origin header set:

| Response from                                   | `Access-Control-Allow-Origin` |
| ----------------------------------------------- | ----------------------------- |
| BFF on `:5000`                                  | absent                        |
| API on `:7215`                                  | absent                        |
| the same call through the Vite proxy on `:5173` | **present**                   |

This matters because "no CORS anywhere" is a load-bearing claim: the topology is same-origin, so
there is no cross-origin grant to misconfigure, and cross-site state-changing requests are rejected
by Fetch-Metadata on top of `SameSite=Strict`. Reading the dev network tab and concluding the app
has CORS configured is the wrong conclusion from real evidence — check against `:5000` or `:7215`
directly before acting on it.

## Frontend test infrastructure

**The frontend type gate is `npm run build` (`tsc -b`), not `tsc --noEmit`.** The root tsconfig is
solution-style, so `--noEmit` skips project references and misses errors the build catches. This has
already shipped a red CI once: a stale barrel re-export passed locally and failed on push.

**Run the whole solution's tests: `dotnet test AzureBank.slnx`.** Narrowing it with a filter has
two distinct failure modes, both verified on this solution:

- **`--filter ~AzureBank.Tests` is malformed.** The condition needs a property name; without one
  the discoverer throws `Invalid Condition` and dotnet itself warns that _"the incorrect format can
  lead to no test getting executed"_. A run that executes nothing is not a run that passed.
- **`--filter FullyQualifiedName~AzureBank.Tests` is well-formed and still wrong.** It selects
  **590 of 651** tests — the match is a substring of the fully-qualified name, and BFF tests are
  namespaced `AzureBank.Bff.Tests.*`, which does not contain `AzureBank.Tests`. All 61 BFF tests
  are excluded and the run reports success.

The trap in both cases is that the filter reads as "the AzureBank test projects" and means
"fully-qualified names containing this substring". Name the solution instead.

**Vitest's default reporter prints console output only for FAILING tests, so a passing test can
write to `console.error` forever and no gate will ever show it.** This is the trap that hid every
other one in this section: measured on `d6ea72e` with `--reporter=verbose`, the suite emitted
**3,074** React `act(...)` warnings across 21 tests while `npm test` and CI both reported a clean
584-passing run. React uses that same channel for invalid props, bad keys, and anything an error
boundary catches — all of it equally invisible.

Reading the logs harder is not the fix, because there were no logs to read. `src/test/setup.ts` now
records `console.error` per test and **fails** the test that wrote to it. A test that provokes a
logged error on purpose stubs it — `vi.spyOn(console, 'error').mockImplementation(() => {})` — which
is what `AppErrorBoundary.test.tsx` and `RouteError.test.tsx` already did.

Two ordering rules make that gate safe, and both were learned by getting them wrong:

- **A throwing `afterEach` must be registered FIRST, because vitest's default `sequence.hooks` is
  `stack` — `afterEach` runs in REVERSE registration order.** Registering last runs it _first_,
  which is the opposite of how it reads. Measured, after getting it backwards: instrumenting both
  of `setup.ts`'s hooks printed `file-hook → setup:assertion → setup:teardown` while the assertion
  was registered last. It matters because a throwing hook skips the ones still to run, so the wrong
  order lets a failing test skip the whole teardown — MSW handlers, mock state, the session and
  step-up mirrors, the viewport — and hand all of it to the next test.
  `src/test/hook-order.test.ts` pins the ordering so setting `sequence.hooks: 'list'` cannot flip
  it silently.
- **Unmount explicitly, first.** Every reset in `setup.ts` depends on nothing being mounted:
  `resetMediaEnvironment()` notifies live `MediaQueryList`s, `useMediaQuery` subscribes through
  `useSyncExternalStore`, and a re-render from teardown lands outside `act(...)`. Testing Library's
  auto-cleanup happens to run first under `stack`, but that is a property of a default the file does
  not control, so `setup.ts` calls `cleanup()` itself. It is idempotent; the later auto-cleanup
  becomes a no-op.

**`vi.advanceTimersByTimeAsync` is not act-aware.** A component ticking on `setInterval` gets one
un-acted `setState` per tick advanced — a 61-second advance against a 1-second tick is 61 of them,
times every component that re-renders. Wrap the advance, not the assertion:
`await act(async () => { await vi.advanceTimersByTimeAsync(ms) })`. `waitFor` and `userEvent` need
no wrapper; Testing Library's async wrapper already suspends the act environment for their duration.

The remaining frontend testing traps — Fluent and jsdom behaviour — live in
[`frontend/CONVENTIONS.md`](../frontend/CONVENTIONS.md), next to the conventions they constrain.

## Tooling

**The in-app browser pane cannot be used to judge this application.** Its tabs are permanently
hidden, so `requestAnimationFrame` never fires and React 19 freezes partway through a passive
update: the network response arrives with a 200 and the spinner spins forever. This is a harness
defect and must never be written up as an application bug. Drive the real browser instead, and when
anything looks hung, check the network tab and the DOM before believing it.

## MSW mocks

**A block comment cannot quote the glob `*` + `/api/*`.** The `*` followed by `/` in the middle of
it closes the comment, and everything after becomes code. Documenting `handlers.ts`'s catch-all did
exactly that: it left a live `api;` expression statement in the file and rewrote the docblock to
quote a pattern that does not exist. It compiled, the build passed and all tests passed — only
eslint's `no-unused-expressions` noticed. Describe the glob in prose, or put it in a `//` line
comment.

**A catch-all that returns `undefined` disables `onUnhandledRequest`.** `sessionActivity` is
registered over every `/api` path so it can expire the session, and it returns `undefined` to fall
through to the real handler. MSW counts that as a MATCH, so a route with NO handler is "handled" —
`onUnhandledRequest: 'error'` never fires and the request escapes to the network. Measured: an
unmocked `/api` path throws a bare `fetch failed` in vitest, indistinguishable from an outage, and
in `dev:mock` it reaches Vite and dies on an HTML parse error. Two real API routes stayed unmocked
for months behind exported hooks because of it. A sentinel handler registered LAST answers
`501 MOCK_HANDLER_MISSING` and names the route.

## jsdom

**jsdom's missing layout is a wrong answer, not a missing API — and Fluent's focus trap reads it.**
Every element reports `offsetParent === null` and a 0x0 `getBoundingClientRect()`, which is exactly
what a real browser reports for an element that is not rendered. tabster's `isDisplayNone()` starts
with that check, so it concludes an open Fluent dialog contains nothing focusable; Fluent's
`useFocusFirstElement` falls through to `resetFocus(surface)`, which focuses without the programmatic
flag, so `ModalizerAPI` never marks the dialog active. Its debounced `hiddenUpdate()`
(`setTimeout(…, 250)`) then runs, decides this modalizer is not the active one, and puts
`aria-hidden="true"` on the OPEN dialog's own surface. Nothing removes it.

The visible symptom is a `findByRole(role, { name })` that cannot see a control while `getByText`
and `getByLabelText` still can — those never consult the accessibility tree. It presents as a flake
because it is a race against 250ms: a query inside the window passes, one after it can never pass,
and CPU contention pushes nearly everything past it. Four tests in three files failed this way,
each of them the only in-dialog role query in its file.

`{ hidden: true }` or a text query silences it and asserts something false. `src/test/layout.ts`
supplies the two answers tabster actually reads; `src/test/layout.test.tsx` fails if it is removed.

**Fixing it moves a second race into view, and that one is real.** With the modalizer activating
properly, tabster does what a modal should: it aria-hides the page behind the dialog. Un-hiding on
close goes through the same 250ms debounce, so for up to a quarter second after a dialog closes the
page underneath is still outside the accessibility tree. A bare `getByRole` on the page immediately
after closing a dialog therefore fails — measured 6 of 6 runs under load. Use `findBy*`: the state
does settle, and waiting for it is what a user experiences.

## An XML `<summary>` on a DTO property IS the published API contract

`Swashbuckle`/OpenAPI lifts the XML doc comment of a request-DTO property into that property's
`description` in `openapiv1.json` — **unless a validation attribute already supplies one**, in which
case the attribute's message wins and the summary never reaches the wire.

That exception is why the trap stayed invisible. `Pin` and `CurrentPin` on `SetPinRequest` both
carry `[Pin]`, whose message ("PIN must be exactly 6 digits.") becomes their description, so the
long forensic histories in their summaries were masked by accident rather than by design. Add a
property with **no** attribute and the whole summary ships.

Measured on the T8 branch: a first draft of `SetPinRequest.Password` published **1391 characters**
into `docs/api/openapiv1.json` and into the generated frontend types — internal engineering history,
a commit SHA, and a step-by-step attack recipe against the product, in the public contract of a
public repository. The other two descriptions over 200 characters in the whole spec are 557 and 453,
both legitimate contract prose about refresh tokens.

Two further wrinkles seen in the same output: consecutive `<para>` blocks concatenate with **no
separator** (`…to ask for.Without it…`), and `<see cref="CurrentPin"/>` renders as the C# member
(`string? SetPinRequest.CurrentPin`) rather than the JSON field a consumer sees — use `<c>currentPin</c>`.

**Rule:** the `<summary>` of a DTO property is consumer-facing contract prose — one or two lines
saying when the field is required and what it means. Everything else (why it exists, what it
prevents, what was measured) goes in a plain `/* … */` comment, which the generator ignores. After
touching a DTO, `node scripts/openapi-spec.mjs regen` and read the property's `description` back.

## A `<param>` on a renamed `[FromHeader]` argument lands on the REQUEST BODY

Same family as the DTO-summary trap above, and found the same way — by reading the regenerated
document instead of trusting the source.

`TransferController.Transfer` documented its new header argument the obvious way:

```csharp
/// <param name="request">Transfer details</param>
/// <param name="stepUpAuthorizationId">Authorisation reference from the Step-Up-Authorization header</param>
public async Task<…> Transfer(
    [FromBody] TransferRequest request,
    [FromHeader(Name = StepUpConstants.HeaderName)] Guid? stepUpAuthorizationId = null)
```

`[FromHeader(Name = …)]` renames the OpenAPI parameter to `Step-Up-Authorization`, so the
`<param name="stepUpAuthorizationId">` tag matches **nothing**. Measured on #113: the generator did
not drop it — it applied it to the **`requestBody`**, replacing `"Transfer details"` on both
money-moving endpoints, and left the header parameter with no description at all. The published
contract then described the body of a transfer as an authorisation reference.

The compiler pushes you into this: documenting only `request` raises **CS1573** ("has no matching
param tag"), so the natural fix is to add the second `<param>` — the one that breaks it.

**Rule:** for any argument whose OpenAPI name differs from its C# name — every renamed
`[FromHeader]`, `[FromQuery]` or `[FromRoute]` — describe it with
`[Description("…")]` (`System.ComponentModel`) on the parameter, not with `<param>`. Do the same for
the `[FromBody]` argument in the same signature, so no `<param>` tags remain to be mismatched. Then
regenerate and **read the description back out of the JSON**, at both the `requestBody` and the
`parameters` entry.

## A new `ValidateOnStart` option must be taught to five places, and the test suite is not one of them

Adding `StepUp:BindingKey` with `.Validate(…).ValidateOnStart()` (ADR-0042) is correct — a missing
HMAC key should stop startup, not surface as a 500 on the first transfer. But `ValidateOnStart`
turns a missing value into a **startup crash**, and the whole test suite is blind to it because
`CustomWebApplicationFactory` injects the value with `UseSetting`. #113 shipped 799 green tests and
still broke two things nothing could catch:

- **`Real-stack layers` in CI went red** with
  `OptionsValidationException: StepUp:BindingKey must be configured` → `API never became ready at
  http://localhost:5068/health/ready`. The workflows set `Jwt__Secret`, `Idempotency__HashKey` and
  `Security__PinPepper`; nobody had told them about the fourth.
- **The dev database had no table**, because a migration is only exercised against a real database
  by a human running the stack — the suites build their schema from the model.

**Checklist when adding a required option.** Miss any one and the failure appears somewhere the
tests are silent:

1. `appsettings.json` — the non-secret parts (the window, the TTL).
2. `appsettings.Development.json.example` — the section **and** the `user-secrets` command list in
   its header comment; a developer who reads only the list gets a crash.
3. `README.md` and `docs/engineering-practices.md` — both carry the same setup recipe.
4. `.github/workflows/*.yml` — an env var plus **every** "Start API" step. `ci.yml` has one,
   `contract-tests.yml` has two.
5. `CustomWebApplicationFactory` — `UseSetting`, which is the one that makes the tests pass while
   everything above is still missing.

Grep for an existing required secret (`Idempotency__HashKey`) and mirror every hit. That grep is the
cheapest form of this checklist, and it finds all five.

## The dev database goes stale and EVERY money endpoint answers 500

Hit twice. On 2026-08-15 the first live PIN-mint answered `500 Invalid object name`; on 2026-08-17
deposit, withdraw and both transfers all answered

```text
String or binary data would be truncated in table 'AzureBankDev.dbo.Transactions',
column 'TransactionNumber'. Truncated value: 'TXN-20260817-AJKNG5F'.
```

`AzureBankDev` was two migrations behind (`WidenTransactionNumberForCheckSymbol`,
`AddStepUpAuthorizations`), so the column was still 20 characters while `IdGenerator` produces 24:
`TXN-` + 8 date digits + `-` + a 10-character suffix + a check symbol.

**The part that wastes the hour.** SQL Server prints the value ALREADY TRUNCATED to the column
width, so `TXN-20260817-AJKNG5F` looks like exactly 20 characters and therefore looks like it should
have fitted. Count what `IdGenerator.GenerateTransactionNumber` produces, not what the error prints.

No suite catches this. The SQL CI job and the integration tests build their schema from migrations,
so they stay green while the hand-maintained dev database rots. Diagnose by comparing
`dbo.__EFMigrationsHistory` against `dotnet ef migrations list`.

```bash
cd backend/src/AzureBank.Api
dotnet ef database update --no-build --connection "Server=(localdb)\MSSQLLocalDB;Database=AzureBankDev;Trusted_Connection=True;TrustServerCertificate=True"
```

The `--connection` is not optional: the plain form fails with *"The ConnectionString property has
not been initialized"*, because user-secrets are not picked up in that host build even with
`ASPNETCORE_ENVIRONMENT=Development`.

## A tool that writes source can inject a control character the compiler accepts

`MoneyFormattingTests` shipped a regex whose pattern began with a literal `U+0008` BACKSPACE, where
a backslash-b was intended. The writer expanded the escape on its way to disk; C# verbatim strings
do not process escapes, so the byte went into the pattern as a character to match. The guard could
never match anything and reported clean for a full session while the defect it existed to catch sat
in the tree.

Every ordinary instrument was blind to it. The compiler accepted it — a backspace in a regex is
legal and means "match a backspace". `grep` rendered it as nothing. Reading the file showed the
intended text. **Only `od -c` revealed it**, because there the byte prints as ONE token where real
backslashes print as two:

```text
new  R e g e x ( @ "  \b  C u r r e n c y  \ s * ...
                     ^^ one token: this is 0x08, not backslash-b
```

`SourceHygieneTests` now fails the build on any control character outside tab/CR/LF in hand-written
source. When writing C# through a script, prefer a form with no backslash at all — `(char)0x08`
rather than `'\b'` — because the escape is what the intermediate tool rewrites.

## A guard that has never been watched refusing anything is a wish

Both incidents above have the same shape: a rule that reports clean, and no way to tell that from a
rule that cannot report anything else. Two independent mechanisms produce that identical green — a
corrupted pattern, and a path filter that silently eats its own input (an unnormalised root
containing `\bin\` skips every file while `Directory.Exists` still answers true).

So every source-scanning guard in `tests/AzureBank.Tests/Architecture/` now carries both halves:

- **liveness** — assert the scan read a plausible number of files, not merely that the folder exists;
- **coverage** — a `[Theory]` driving the detector against shapes that are NOT in the tree, so the
  rule is observed refusing something on every run.

Add both when adding a scanner. The coverage theory is the one that pays: writing it for the
currency rule immediately exposed a second hole the scan could never have shown.

## An OpenAPI transformer that ASSIGNS silently discards what the controller declared

`AuthorizationResponseTransformer` set `operation.Responses["401"]` outright, with the comment
"Always set 401/403 with no content (even if already defined)". `TransferController` had declared

```csharp
// AUTHORIZATION_REQUIRED, AUTHORIZATION_EXPIRED, AUTHORIZATION_INVALID (ADR-0042)
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
```

and the assignment replaced it with an empty body. **The published contract was therefore worse than
what the source said, and the source looked right.** `NotFoundResponseTransformer` did the same for
404. Two consequences worth recognising by sight:

- **Fifteen responses elsewhere carried hand-written INLINE error schemas.** That duplication is what
  an unusable shared route looks like from the outside — people work around it one endpoint at a
  time rather than reporting it.
- **Regenerating cannot reveal it.** The document and the generated artefacts both derive from the
  transformer, so the drift gate compares two copies of the same wrong answer. Only an HTTP call to
  the running API disagrees.

`TryAdd`, or a helper that fills gaps, instead of `[...] =`. And when a transformer's comment
justifies itself with framework behaviour ("ASP.NET Core returns empty 401s"), check whether THIS
application still uses that default — here `OnChallenge` calls `context.HandleResponse()`, whose
whole purpose is to replace it.

## A `{id:guid}` route constraint 404s; it never produces a binding 400

Four endpoints published a 400 for "invalid path parameter format". Measured, all four answer **404**
with `application/problem+json` and a W3C trace-context `traceId` — a third envelope, from the
framework rather than from `GlobalExceptionHandler`:

```json
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,
 "traceId":"00-c3755f5dd06635c9f80bd38f90d54f52-6e8251d2e9f65eb0-01"}
```

A route constraint participates in route MATCHING. A non-GUID segment matches no route, so MVC is
never entered, so there is no model binding and nothing to produce a 400. An unconstrained path
parameter cannot fail either, for the opposite reason: every byte sequence is a valid string. So
"path parameters can fail validation" is wrong both ways round, and a documented response nobody can
produce is as false as an undocumented body.

---

## `dotnet ef` reads the compiled assembly, not your source files

Found in B2 (ADR-0044), and it cost two rounds of confusion in a row.

`dotnet ef migrations add … --no-build` scaffolds from the **dll**. Adding an entity and scaffolding
without rebuilding first produces a migration with an **empty `Up()`** — no error, no warning, just a
migration that does nothing. Deleting that migration and regenerating it under the same name then
fails with *"the name is used by an existing migration"*, because the deleted `.cs` is still inside
the assembly. Rebuild between every EF operation and both symptoms disappear.

Two neighbours of the same trap:

- `dotnet ef migrations remove` dies with *"The ConnectionString property has not been initialized"*
  — user-secrets are not picked up there, the same failure this file already records for
  `database update`. Deleting the migration files by hand and rebuilding is the way out.
- `--connection` is **not** a valid option for `migrations add`; it exists only on `database update`.

## `datetime2` stores no `DateTimeKind`, so a hash over a formatted timestamp changes on read

Also from B2, and the more dangerous of the two because everything was green while it was wrong.

`DateTime.ToString("O")` emits a trailing `Z` when `Kind` is `Utc` and omits it when `Kind` is
`Unspecified`. SQL Server's `datetime2` has no kind column, so a value written from `DateTime.UtcNow`
comes back as `Unspecified` and formats differently than it did on the way in. Anything derived from
that string — a hash, a signature, a cache key — therefore does not survive a round trip:

```
"2026-08-19T10:39:24.7403300Z" -> HMAC 6a7028…143903   (as written)
"2026-08-19T10:39:24.7403300"  -> HMAC a98d42…c3dbd9   (as read back)
```

The EF InMemory provider hides this completely: its identity map hands back the object that was
written, so nothing is ever re-materialised and the test passes. Hash `Ticks` instead — an integer,
exact through `datetime2(7)`, with nothing kind-dependent in it — or store a `DateTimeOffset`.

## `UPDLOCK, HOLDLOCK` outside a transaction is decoration

EF opens its implicit transaction **inside** `SaveChanges`. Code that runs in a `SaveChanges`
override, before `base.SaveChanges`, is therefore **not** in that transaction: a locking read issued
there auto-commits and drops its lock immediately, and the write that was meant to be protected
happens afterwards on its own.

Symptom, from 24 concurrent writers against a table whose sequence column carries a unique index:
`Cannot insert duplicate key row in object 'dbo.AuditEvents' with unique index
'IX_AuditEvents_Sequence'. The duplicate key value is (2)`. Without that unique index it would not
have raised at all — it would have silently produced two rows claiming the same predecessor.

Open the transaction explicitly around the read and the write, and skip it when the caller already
has one (`Database.CurrentTransaction is not null`) or the provider is not relational.

## The test host is not the production host: the retrying strategy is opt-in

The one in this batch that no test could have found, because the tests were the blind spot.

`ServiceCollectionExtensions` configures the API with `EnableRetryOnFailure`, and EF **refuses a
user-initiated transaction** under a retrying strategy. `CustomWebApplicationFactory` rebuilds the
`DbContext` registration and leaves that strategy **off** unless a test calls
`EnableSqlRetryOnFailure()` — deliberately, so an injected transient fault surfaces instead of being
retried. The consequence is that `Database.BeginTransaction()` in shared infrastructure passes every
test and throws on the real API:

```
System.InvalidOperationException: The configured execution strategy
'SqlServerRetryingExecutionStrategy' does not support user-initiated transactions. Use the execution
strategy returned by 'DbContext.Database.CreateExecutionStrategy()' …
```

Measured as a **500** on `POST /api/auth/refresh` with the whole 766-test suite green.

Two rules follow. Any code that opens its own transaction goes through
`Database.CreateExecutionStrategy()` — `AuthService.RegisterAsync` is the worked example. And when
such code is added, one SQL Server test must opt into `EnableSqlRetryOnFailure()`, or the production
configuration is exercised by nothing at all.

More generally: the factory removes `DbContextOptions` and `IDbContextOptionsConfiguration` and
rebuilds them, so **anything attached to the production registration is absent under test** — which
is also why the audit chain lives in the `DbContext` class rather than in a `SaveChangesInterceptor`.

## "The writer was called" is not evidence that a row exists

The most expensive one in this batch, because every layer of the suite agreed it was fine.

`IAuditService.Record` deliberately only calls `Add` — the caller's `SaveChanges` is what persists
the row (ADR-0044 D1). A unit test holding a `Mock<IAuditService>` and asserting

```csharp
_auditMock.Verify(a => a.Record(SecurityEvents.PinEnrolled, ...), Times.Once);
```

therefore passes whether or not anything is ever written. On this branch `AuthService.SetPinAsync`
called `Record` *after* `UserManager.UpdateAsync` had already saved and nothing saved again:
`POST /api/auth/pin` answered **200**, the security log line was emitted, and `AuditEvents` held
**zero** rows — with the mock assertion green and the whole suite green.

Two rules. Any writer whose contract is "add, never save" needs at least one test that reads the
STORE after driving the real endpoint, not one that watches the writer. And when such a writer is
placed, check what actually performs the save — `UserManager.UpdateAsync`, `SignInManager`, a
repository and `ExecuteUpdate` are all saves that a reader scanning for `_context.SaveChangesAsync`
will miss (`ExecuteUpdate` is worse: it commits without flushing tracked entities at all).

## A rotated-then-replayed refresh token is a benign retry, not reuse

Cost two rewrites of a test that looked obviously right.

`RefreshTokenService` treats a token that HAS a successor and was revoked inside
`RotationGraceWindow` (10 s) as a lost-response retry: rejected, but with no security event at all.
So "rotate, then replay the old token" never reaches the reuse branch, and a fault injected there
never fires. Genuine reuse needs a token revoked WITHOUT a successor — log out, which calls
`RevokeAllForUserAsync`.

Then the second trap: logging out revokes *everything*, so an assertion that the family ends up with
zero active tokens holds whether or not containment ran. Log back in first, so the mitigation has a
victim — and confirm by breaking the code on purpose and watching the test go red. A test whose
setup already satisfies its postcondition proves nothing, and it will not tell you so.

## Three binding kinds, two parsers — and a `Guid` does not mean the same thing in each

A `Guid` arriving in a ROUTE or a HEADER is parsed by MVC's `TryParse`, which accepts the `D`, `N`,
`B`, `P` and `X` forms, in any case, and trims surrounding whitespace. A `Guid` that is a MEMBER of a
JSON body is parsed by `System.Text.Json`, which accepts the **`D` form only**, is case-insensitive,
and does **not** trim. So the same value is accepted on one surface and rejected on another, and a
helper written for one is wrong on the other: `parseGuid` for a route or a header, `parseBodyGuid`
for a body member.

**Measured**, on the two code paths themselves — the `TypeConverter` MVC uses for a simple type, and
`System.Text.Json` with the Web defaults ASP.NET Core applies to a body:

```
form              route / header (TypeConverter)   JSON body member (System.Text.Json)
D  canonical      ACCEPTED                         ACCEPTED
N  no dashes      ACCEPTED                         REJECTED
B  braces         ACCEPTED                         REJECTED
P  parens         ACCEPTED                         REJECTED
X  hex            ACCEPTED                         REJECTED
D uppercase       ACCEPTED                         ACCEPTED
D with spaces     ACCEPTED                         REJECTED
```

Related and load-bearing: `fingerprint(raw)` must stay ABOVE `bindAccountIds`, computed over the WIRE
bytes. Fingerprinting after binding fingerprints a normalised value, which is not what the caller
sent and therefore not what an idempotency key should key on.

## `Guid.CreateVersion7()` is not monotonic within a millisecond, and SQL Server disagrees about order

Version-7 GUIDs are time-ordered only to millisecond resolution; two created in the same millisecond
have no defined order between them. Worse, SQL Server collates `uniqueidentifier` on a **different
byte order** than .NET's `Guid.CompareTo`, so a sort that looks right in memory is not the sort the
database performs.

**Measured twice.** Within a burst, `Guid.CreateVersion7()` produced **44,712 out-of-order adjacent
pairs out of 89,508** — essentially a coin flip, so there is no ordering inside a millisecond at all.
And ten of them, created in sequence, sort three different ways:

```
creation order : 0,1,2,3,4,5,6,7,8,9
.NET sort order: 1,7,8,0,2,6,3,9,5,4
SQL sort order : 4,8,7,0,3,5,1,2,6,9
```

**Never order by `Id` and call it creation order.** This cost two separate bugs in one PR — one in
production code, one in a test that agreed with it — which is the shape to watch for: when the code
and its test share a wrong assumption, the test confirms the bug instead of catching it. Order by the
column that means what you want (`Sequence`, `OccurredAt`, `CreatedAt`), and if there isn't one, that
is the finding.


## A withdrawn argument is a guard, so it has to be framed as one

This repository keeps the reasoning that was rejected, not only the decision that won. The reason is
narrow and it is not sentiment: several of those arguments sound *better* than the rule that
replaced them, so somebody eventually re-derives one and re-opens the hole. The text exists to stop
that, and its whole value is being read at the moment of the edit.

Which means the framing decides whether it works. **`A withdrawn argument, left visible.` is an
archival label** — it tells a reader "here is what we used to think", which is an invitation to
skip. The person it needs to reach is the one about to loosen the condition, and that person skips
history by definition. Lead with the constraint and demote the history to evidence:

```
A withdrawn argument, left visible. This note used to say the tell was the count alone ...
```

```
DO NOT WIDEN THIS TO THE COUNT ALONE. It was written that way once, and the wide version is the
one that helps an attacker. The argument for it reads well: ... That case is unreachable, and the
tool was gated on it anyway.
```

Same words, same length, different reader.

**The rule is about WHERE the text lives, not about which framing is nicer.**

- **In code and in runbooks: imperative.** A code comment is read by somebody with the file open and
  a change in mind. A runbook is read under pressure. Neither reader is browsing.
- **In an ADR: archival is correct, and should stay.** An ADR is read deliberately, by somebody
  asking what was decided and why — there the history *is* the content, and an imperative aimed at
  an editor would be the thing out of place. `docs/adr/0044-...md` keeps its
  `A withdrawn argument, left visible.` for exactly this reason.

**And the mechanical test for keeping one at all:** a withdrawn argument earns its place only while
it constrains something that still exists. When the gate, flag or branch it protects is removed, the
text goes in the same commit. Kept past that point it stops being a guard and becomes what it merely
looked like all along.

## A notice that reaches the session holder is not a notification

NIST SP 800-63B-4 §4.1.2.1 asks that the subscriber be notified of a new authenticator "via a
mechanism independent of the transaction binding" it. The cheap reading is a line on the success
screen, a toast, an inbox item — and every one of them reaches whoever holds the session, which in
the threat model is the attacker who just enrolled the PIN. §4.1.2.2 says where in-session text
belongs: *in addition to* the notice, as instructions for a mishap, never instead of it. The same
goes for the operator's log line and the audit row: durable, chained, and read by the wrong person.
ADR-0045 records what does count — a message addressed to the email held on the account, on a path
the session cannot read, redirect or suppress — and, honestly, where that stops.

The test for any candidate channel is one question: **could the attacker who caused the event see or
prevent the notice?** If yes, it is a receipt, not a notification.

## A send inside the request inverts D1 — write the row, deliver later

The obvious place to send a notice is the request that caused it, and both places to put it there
are wrong. AFTER the save: a crash, a kill or a lost connection between the commit and the call
loses the notice with no record that it was ever owed — the enrolment stands, the owner is never
told, and nothing can tell later. INSIDE the save: the request holds the audit tail lock
(`UPDLOCK, HOLDLOCK`) for as long as the I/O takes, and a slow or failing relay stalls every audited
write behind it, while a rollback after the send leaves a message about an enrolment that never
happened.

The shape that works is the one ADR-0044 D1 already uses for the audit row: record the OBLIGATION in
the same transaction as the action, and deliver from somewhere else, later, from the row. What
"later" means is a decision about runners (ADR-0045 D3), not about the request.
