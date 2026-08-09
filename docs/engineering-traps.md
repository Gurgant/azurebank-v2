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
