# ADR-0044: Security events go to an append-only, hash-chained table

**Status:** Accepted · **Date:** 2026-08-19 · Gives the event vocabulary in
`AzureBank.Shared/Constants/SecurityEvents.cs` a durable destination — it had none, and no ADR of its
own either. Prerequisite for B1 (recording the acting user on every transaction) and B3 (the PSD2
Art. 72 evidence pack). Supersedes nothing.

## Context

Seventeen security events are logged, and that is all that happens to them. A log is a stream: it
rotates, it is writable by whoever can reach the host, and nothing about it is evidence. PCI DSS v4
10.3.2 asks that audit logs be protected from modification; NIST SP 800-53 AU-9 asks the same;
PSD2 Art. 72 requires an institution to be able to reconstruct what happened. None of that is
satisfied by `ILogger`.

The gap was found while doing something else — checking whether an account closure was audited at
all — and the answer was that there was no audit store to be audited into.

## Decision

A single `AuditEvents` table, written in the same transaction as the action it describes, with each
row hash-chained to the one before it.

### D1 — a failed audit write fails the business action

`IAuditService.Record` only calls `Add`. The row rides the caller's existing `SaveChangesAsync`, so
either both land or neither does. This is the contract `IdempotencyService.MarkExecutedPending`
already established in this codebase, so no new transaction machinery was introduced.

The cost is stated plainly: if the audit table is unwritable, audited operations stop. That is the
intended trade — better a blocked bank than an emptied one. Ratified by Vlad.

Refusals are the exception and need the opposite treatment: the refused operation's own rollback
would take the record of the refusal with it. `RecordRefusalAsync` therefore writes on its own scope
and commits immediately. The two names are deliberately hard to confuse, and `Record` **throws** if
handed a `Refused` or `MitigationFailed` outcome rather than silently doing the wrong thing.

Measured, not assumed —
`AuditChainSqlServerTests.WhenTheAuditRowCannotBeWritten_TheBusinessChangeIsRolledBackToo`: an audit
row too long for its column is refused by SQL Server, and the account rename that shared the save is
gone when re-read from a fresh connection.

### D2 — the chain now, the SQL Server ledger later

Each row carries an HMAC-SHA256 over its own fields **and its predecessor's hash**, so removing or
altering a row breaks every link after it. Keyed rather than a bare digest, for the reason
`StepUpOptions.BindingKey` gives: every field of an audit row is enumerable, so an unkeyed hash could
be recomputed by anyone holding the table.

What this does **not** defend against, stated so the record does not overclaim: an attacker holding
both the database and the application's secrets can rewrite a row and recompute the chain. Closing
that needs a digest anchored outside the system — SQL Server's ledger feature — which is deferred,
not rejected.

**A withdrawn argument, left visible.** The first version of this decision claimed the ledger was
impractical because its digests require an Azure storage destination. That is false, and measuring it
is what showed so: `sp_generate_database_ledger_digest` takes no destination at all — automatic
upload is what needs Azure. The ledger is deferred because `APPEND_ONLY` is a one-way door on a
schema still moving (measured: `DROP TABLE` leaves undroppable residue, Msg 37427; only nullable
columns may be added, Msg 37387), not because it cannot be done here. A local emulator is genuinely
out — Azurite is rejected with error 12136 over both http and https, measured directly rather than
inferred, after two research agents contradicted each other on the point.

### D3 — the chain is applied in the `SaveChanges` funnel

Not in the writer, and not in a `SaveChangesInterceptor`. The writer cannot hold a lock across the
insert; an interceptor is silently absent under test, because `CustomWebApplicationFactory` rebuilds
the `DbContext` registration and would drop it — every audit row would then be written with an empty
hash, and the guarantee would exist only where nobody looks.

`Sequence` is assigned by the chain rather than by an IDENTITY column. An IDENTITY exists only on a
relational provider, and roughly 585 of this project's tests run on EF InMemory, where `Sequence`
would stay 0 and the chain would have no order to be verified against. Ordering by `Id` instead was
tried first and was wrong: `Guid` ordering in .NET is not creation order even for a UUIDv7, and SQL
Server collates `uniqueidentifier` on a different byte order again.

### D4 — the six BFF events stay log-only, and this says so

`RateLimitExceeded`, `CrossSiteRequestBlocked`, `StepUpRequired`, `StepUpWithoutSession`,
`RawRefreshBlocked` and `RefreshRejected` are raised in the BFF, which has no database. Giving it one
means a second writer against the audit table, which is a larger decision than this one. They are
listed here so the next reader knows they were considered rather than missed.

### D5 — no personal data in the table

Actor and subject are stored as ids. `Detail` is JSON, capped at 1024 characters rather than MAX
precisely so nobody puts a stack trace — and the PII inside it — into a table designed never to be
purged. The AzureTag rename event records **neither** the old handle nor the new one, only that a
rename happened.

That cap is also what makes the GDPR position coherent: every regime binding this system states a
**minimum** retention — PCI DSS v4 10.5.1 twelve months, AMLD Art. 40 and PSD2 Art. 21 five years —
and a minimum cannot be violated by keeping more. Erasure is answered upstream: there is nothing
personal in the table to erase. (NIST SP 800-53 AU-11 requires an organisation-defined period and
names no number; quoting one from it would be an invention.)

## What is wired, and what is not

Seven of the seventeen API events write a row today: `AccountDeleted`, `AccountNumberRevealed`,
`AzureTagRenamed`, `PinEnrolled`, `RefreshTokenUnknown`, `RefreshTokenReuse` and
`RefreshTokenReuseRevokeFailed`. The log line is kept alongside the row — two destinations, two jobs.

The remaining ten are deliberately log-only, with reasons that were measured rather than assumed:

- **Registration refusals** (`DuplicateRegistration`, `RegistrationRejected`) — `/api/auth/register`
  is unauthenticated, and the API carries **no rate limiter of its own** (checked: zero
  `RequireRateLimiting` in `AzureBank.Api`; the limit lives in the BFF). An audit row per attempt is
  therefore an unauthenticated, unbounded write into the one table that is never pruned. Revisit
  together with #231, which puts these endpoints behind the BFF.
- **Retry collisions** (`TransactionNumberCollision` ×2, `AccountNumberCollision`) — these are health
  signals about a random-id generator, not acts by a principal, and they are raised inside a `catch`
  that `continue`s a retry loop. An enlisted row would die with the attempt that failed; a
  self-committing one would write one row per attempt. Both wrong, and the log is right.

Two further gaps, named rather than left implicit: **nothing reads this table yet** — there is no
endpoint, no access control and no operator view, which is B3's work — and **`VerifyAsync` is called
only by tests**, so nothing verifies the chain on a schedule.

## Consequences

- Audited operations now depend on the audit table being writable. That is D1 working, not a defect.
- Saves that carry an audit row open an explicit transaction. Saves that do not are untouched.
- Two secrets exist where one did: `Audit:ChainKey` joins `StepUp:BindingKey`, `Idempotency:HashKey`
  and `Security:PinPepper`. All four are `ValidateOnStart`, and none is ever stored in the database.

## Three defects this work found, none of them visible to a green suite

Recorded here because each was live in a version whose whole suite was green, and each needed a
different oracle to find: two needed real SQL Server, the third needed the running API.

**The hash did not survive a round trip.** `OccurredAt` was hashed with `ToString("O")`, which emits
a trailing `Z` when `DateTime.Kind` is `Utc` and omits it when the kind is `Unspecified`.
`datetime2` stores no kind, so a row written from `DateTime.UtcNow` hashed one way and, read back
from the database, hashed another: every audit row would have failed verification in production,
always. Confirmed by recomputing a stored row's HMAC outside .NET —

```
payload with "2026-08-19T10:39:24.7403300Z" -> 6a7028…143903  == the stored RowHash
payload with "2026-08-19T10:39:24.7403300"  -> a98d42…c3dbd9  != the stored RowHash
```

`Ticks` is hashed instead: a plain integer, exact through `datetime2(7)`, with nothing kind-dependent
to lose.

**The lock had no transaction to be held by.** `UPDLOCK, HOLDLOCK` on the tail read is meaningless
outside a transaction, and EF opens its implicit one *inside* `SaveChanges` — after the chain had
already run and released it. Twenty-four concurrent writers: `Cannot insert duplicate key row in
object 'dbo.AuditEvents' with unique index 'IX_AuditEvents_Sequence'. The duplicate key value is
(2)`. The unique index on `Sequence` is why this was loud instead of a silent fork, which is an
argument for keeping it. After the fix: 24 rows, sequences 1..24, no fork, no deadlock.

**And the transaction was refused by production's own configuration.** `EnableRetryOnFailure` is on
in `ServiceCollectionExtensions`, and EF refuses a user-initiated transaction under a retrying
strategy. So every audited request on the real API answered **500** —

```
POST https://localhost:7215/api/auth/refresh  ->  500
System.InvalidOperationException: The configured execution strategy
'SqlServerRetryingExecutionStrategy' does not support user-initiated transactions.
```

— while all 766 tests passed, because `CustomWebApplicationFactory` leaves the retrying strategy
**off** unless a test opts into it. The default SQL test path is therefore not the production path.
The fix is the idiom `AuthService.RegisterAsync` already uses,
`Database.CreateExecutionStrategy()`; `AnAuditedSave_WorksUnderTheRetryingStrategyProductionActuallyUses`
now opts in, so one test finally runs the production configuration.

Verified afterwards on the running API rather than only in tests: the same call answers **401**, and
four rows sit in `AzureBankDev` — three `RefreshTokenUnknown` refusals written on their own
connections, then an `AzureTagRenamed` success that rode a business transaction, each linked to the
hash before it, `Detail` empty on all four (D5).

## References

- PCI DSS v4.0 §10.2.2, §10.3.2, §10.5.1
- NIST SP 800-53 Rev. 5 AU-3, AU-9, AU-11
- PSD2 (EU 2015/2366) Art. 21, Art. 72; AMLD (EU 2015/849) Art. 40
- `backend/src/AzureBank.Shared/Constants/SecurityEvents.cs` — the event names this table stores
- [ADR-0009](0009-idempotency-monetary-operations.md) — the enlisting-writer contract D1 copies
- [ADR-0008](0008-step-up-authentication.md) — the PIN events among them
- `azurebank-work/plans/audit-trail/` — the measurements behind every number above
