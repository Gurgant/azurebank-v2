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
and commits immediately. The two names are deliberately hard to confuse, and each **throws** on the
outcomes that belong to the other: `Record` on `Refused` and `MitigationFailed`, `RecordRefusalAsync`
on `Succeeded` and `RetryCollision`. The second guard was missing until the first review round —
which meant a success could be committed out-of-band, D1 inverted.

Measured, not assumed —
`AuditChainSqlServerTests.WhenTheAuditRowCannotBeWritten_TheBusinessChangeIsRolledBackToo`: an audit
row too long for its column is refused by SQL Server, and the account rename that shared the save is
gone when re-read from a fresh connection.

**D1 HAS EXACTLY ONE EXCEPTION, and it is the refresh-token reuse path.** Everywhere else "no
evidence, no action" is the right trade. There, the action is CONTAINMENT of a token already proven
stolen, and refusing to contain it because the logging failed hands the attacker the family. The
first wiring awaited the refusal write *before* the try that guards `RevokeAllForUserAsync`, so an
unwritable audit table meant the revoke never ran, no `MitigationFailed` row was written either, and
the caller got the 500 that the comment inside that very catch calls out as inviting a retry of the
stolen token. Containment now runs first and the row is written after it. The row can still fail
loudly — the 5xx just no longer buys the attacker anything, because the family is already dead.
Pinned by `WhenTheReuseAuditRowCannotBeWritten_TheStolenFamilyIsStillRevoked`, which was verified to
go red when the order is put back.

### D2 — the chain now, the SQL Server ledger later

Each row carries an HMAC-SHA256 over its own fields **and its predecessor's hash**. Keyed rather than
a bare digest, for the reason `StepUpOptions.BindingKey` gives: every field of an audit row is
enumerable, so an unkeyed hash could be recomputed by anyone holding the table. `Sequence` — the
column verification orders by — is inside the payload, which is why the marker reads `v2`.

**A CORRECTION, because the first version of this section was too strong.** It said removing a row
"breaks every link after it". That is true of an INTERIOR row and false of the TAIL. Deleting the
last row, or the last thousand, leaves every surviving row hashing correctly and linking correctly,
because `VerifyAsync` only ever looks backwards and has nothing to compare the end of the chain
against. Truncation is therefore undetected — and, unlike the rewrite case below, it needs **no key
at all**, only write access to the table. Confirmed by walking the loop, and stated here rather than
left for a reader to discover.

What this also does not defend against: an attacker holding both the database and the application's
secrets can rewrite a row and recompute the chain. Both gaps close the same way — a digest anchored
outside the system, which is SQL Server's ledger, deferred rather than rejected. Until then the
honest claim is narrow: **this chain detects tampering by someone who holds the database but not the
key, except at the end of the table.**

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

### D4 — the eight BFF events stay log-only, and this says so

`RateLimitExceeded`, `CrossSiteRequestBlocked`, `StepUpRequired`, `StepUpWithoutSession`,
`RawRefreshBlocked`, `RawAuthEntryBlocked`, `SessionRequired` and `RefreshRejected` are raised in
the BFF, which has no database. The list is the count: naming them is what makes this checkable, and
`SecurityEventConstantTests.TheEventInventoryThisAdrStatesIsStillTheOneInTheSource` fails when a
new one appears without this section moving with it — which is how `RawAuthEntryBlocked` came to
be missing from it for one commit. Giving it one
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

**Eleven events write a row today.** Seven are administrative: `AccountDeleted`,
`AccountNumberRevealed`, `AzureTagRenamed`, `PinEnrolled`, `RefreshTokenUnknown`,
`RefreshTokenReuse` and `RefreshTokenReuseRevokeFailed`. For those the log line is kept alongside the
row — two destinations, two jobs.

**Four are money movements, added by B1** (2026-08-20): `MoneyDeposited`, `MoneyWithdrawn`,
`MoneyTransferred` and `MoneyTransferredInternally`. Until then this table recorded a renamed handle
and not one movement of money, which is the single thing a bank is audited for.

Three things about those four are decisions rather than details.

**They emit NO `SecurityEvent` log line, and that is the first event class to do so.** The
administrative seven are worth waking an operator for; a deposit is not, and the money paths already
carry their own operational log lines. So a movement gets durable evidence without flooding the
alert stream. It also means the two inventories — logged sites, and rows written — moved
independently for the first time, which is why
`SecurityEventConstantTests.TheEventInventoryThisAdrStatesIsStillTheOneInTheSource` now counts
`_audit.Record` and `_audit.RecordRefusalAsync` sites as well as log templates. A guard that only
counted templates would have let this very paragraph go stale in silence, exactly as D4 did.

**`Detail` is null on all four.** The amount, the counterparty, the description and the account are
already on the ledger row that `SubjectId` reaches, and copying them here would break D5 — an amount
tied to an actor id is financial data about an identifiable person, in a table designed never to be
purged. The audit row answers *who did what to which movement*; the ledger row answers *what moved*.

**A transfer writes ONE row, and its subject is the OUTGOING ledger row.** Two ledger rows are the
bookkeeping of a single act, and the outgoing leg is the act itself — money leaving, under the
authorisation the actor minted and which is consumed against that very row.

For an EXTERNAL transfer there is a second, sharper reason, and it is worth stating separately
because it does NOT apply to the internal case: the incoming leg lands on the payee's account, whose
`Account.UserId` is provably not the actor. The payee is resolved by handle with no ownership check,
and the self-transfer guard plus the unique `AzureTag` index make the two ids differ by construction.
Subjecting the row to that leg would name someone who authorised nothing.

An INTERNAL transfer moves between two accounts the actor already owns — `InternalTransferAsync`
ownership-checks BOTH against the same user — so there the incoming leg's owner *is* the actor and
nothing would be misattributed. The subject is still the outgoing leg, for the first reason alone.
Saying so is not pedantry: an earlier draft of this paragraph gave the external reason for both, and
a reader checking it against `InternalTransferAsync` would have found the ADR asserting something
the code contradicts.

**And one thing the retry loop forced.** A money row must be written INSIDE the concurrency-retry
loop, because the transaction id it takes as its subject is minted inside that loop. That left a
failed attempt's row tracked as Added while the next attempt added another —
`ConcurrencyRetry.ResetToStoreAsync` detached only `Transaction`, and its own comment records that
the `IdempotencyRecord` is deliberately left attached, so nothing had ever been asked this question.
Measured with an injected collision: one deposit, **two** rows. It now detaches `AuditEvent` too,
because a row written about an attempt must die with the attempt — the opposite of the idempotency
flip, which is about the request and survives every attempt. An audit trail that overcounts
movements is worse than one that misses them: it manufactures evidence of transfers that never
happened. Pinned by `ADepositThatRetries_WritesExactlyOneAuditRow`.

**Money REFUSALS are not wired here**, and the reason is measured rather than assumed. There are 19
throw sites across `TransactionService` and `TransferService`, and most are business validation —
insufficient funds, a self-transfer, a same-account transfer. Those are routine user outcomes, and a
row per attempt is the same unbounded write into a never-purged table that keeps registration
refusals out. The ones that ARE security signals — a transfer presented without a step-up
authorisation, a wrong PIN at the mint — belong with the step-up path, where ADR-0010's lockout
already lives, and that is its own change with its own tests.

The remaining ten logged events are deliberately log-only, with reasons that were measured rather
than assumed:

- **Registration refusals** (`DuplicateRegistration`, `RegistrationRejected`) — `/api/auth/register`
  is unauthenticated, and the API carries **no rate limiter of its own** (checked: zero
  `RequireRateLimiting` in `AzureBank.Api`; the limit lives in the BFF). An audit row per attempt is
  therefore an unauthenticated, unbounded write into the one table that is never pruned. ~~Revisit
  together with #231, which puts these endpoints behind the BFF.~~ *(that revisit has happened —
  see below; the instruction is struck rather than deleted so the original entry stays whole)*

  **Revisited 2026-08-19, as this ADR said to.** #231 has landed: the proxied `/api/auth/register` is
  now answered 404, so the only route to the API's registration is the BFF's own
  `/bff/auth/register`, which carries the `auth` rate-limiter policy. The unbounded half of the
  argument is therefore weaker than when it was written. It is NOT gone — a rate limit is per IP, and
  the events stay log-only for now — but the reason has changed and is recorded rather than left
  standing on a premise that no longer holds. Wiring them is a change with its own tests; it belongs
  to B1/B3, not to a footnote here.
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

## Six defects this work found, none of them visible to a green suite

Recorded here because each was live in a version whose whole suite was green, and each needed a
different oracle: two needed real SQL Server, one needed the running API, two came from a review that
read the code, and one from an adversarial sweep of the write path. The pattern is the point — "779
tests pass" said nothing about any of them.

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

### The three the review round added

**`PinEnrolled` was never written at all.** `Record` only calls `Add`; the caller's save persists it.
The call sat AFTER `UserManager.UpdateAsync`, which had already saved, and nothing saved again — so
the row was discarded when the scope disposed. Measured on the running API: `POST /api/auth/pin`
answered **200**, the security log line was emitted once, and `AuditEvents` held **zero** rows. The
unit test was green throughout, because it held a `Mock<IAuditService>` and asserted that `Record` was
CALLED. That is the lesson worth keeping: *the writer was invoked* and *the evidence exists* are
different claims, and only the second one is the feature. `AuditTrailPersistenceTests` now reads the
table for every wired success path.

**`AzureTagRenamed` rode a second save.** The rename committed, then the row was added and saved
separately, so the two could part company — D1 broken on the path this ADR is the reason for. Every
row-exists assertion still passed, because the row WAS written; only an injected failure separates
the shapes, which is what `WhenTheAuditRowCannotBeWritten_TheHandleRenameIsRolledBackToo` does.

Fixing it moved a second thing: with the audit row now sharing that save, `IX_AuditEvents_Sequence`
can raise the same 2601/2627 a duplicate handle does, so `UserService`'s number-only match would have
reported a hash-chain collision to the caller as "that handle is already taken". The narrowing moved
to `ConcurrencyRetry.IsAzureTagCollision`, matching by INDEX NAME like its three siblings.

### And one the sweep added, which no bot and no test had seen

**The reuse path was made fail-open by its own audit row** — see D1's exception above. Found by
fanning out over the audit write path with instructions to classify every call site and then refute
each finding, rather than by re-reading the diff.

## References

- PCI DSS v4.0 §10.2.2, §10.3.2, §10.5.1
- NIST SP 800-53 Rev. 5 AU-3, AU-9, AU-11
- PSD2 (EU 2015/2366) Art. 21, Art. 72; AMLD (EU 2015/849) Art. 40
- `backend/src/AzureBank.Shared/Constants/SecurityEvents.cs` — the event names this table stores
- [ADR-0009](0009-idempotency-monetary-operations.md) — the enlisting-writer contract D1 copies
- [ADR-0008](0008-step-up-authentication.md) — the PIN events among them
- `azurebank-work/plans/audit-trail/` — the measurements behind every number above
