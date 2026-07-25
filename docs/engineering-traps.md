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

**`WITH (ONLINE = ON)` is unavailable here** — it is an Enterprise feature and it breaks LocalDB
outright. Index migrations run offline.

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

**The client sends `fromDate` only.** The server defaults `toDate` to *now* per request, which is
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

## Frontend test infrastructure

**The frontend type gate is `npm run build` (`tsc -b`), not `tsc --noEmit`.** The root tsconfig is
solution-style, so `--noEmit` skips project references and misses errors the build catches. This has
already shipped a red CI once: a stale barrel re-export passed locally and failed on push.

**Run the whole solution's tests: `dotnet test AzureBank.slnx`.** Never narrow it with
`--filter ~AzureBank.Tests`. That filter is a *substring* match on the fully-qualified test name,
and BFF tests are namespaced `AzureBank.Bff.Tests.*` — which does not contain the string
`AzureBank.Tests`. So the whole BFF suite is excluded, and the run reports success for the tests it
did execute. The trap is that the filter looks like it means "the AzureBank test projects" and
actually means "names containing this substring".

The remaining frontend testing traps — Fluent and jsdom behaviour — live in
[`frontend/CONVENTIONS.md`](../frontend/CONVENTIONS.md), next to the conventions they constrain.

## Tooling

**The in-app browser pane cannot be used to judge this application.** Its tabs are permanently
hidden, so `requestAnimationFrame` never fires and React 19 freezes partway through a passive
update: the network response arrives with a 200 and the spinner spins forever. This is a harness
defect and must never be written up as an application bug. Drive the real browser instead, and when
anything looks hung, check the network tab and the DOM before believing it.
