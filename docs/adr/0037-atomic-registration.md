# ADR-0037: Registration is all-or-nothing

**Status:** Accepted · **Date:** 2026-08-09 · **Supersedes nothing.** Closes the residual recorded in
[ADR-0036](0036-account-number-collision-recovery.md) under Consequences.

## Context

Registration writes three things: the Identity user, its default role, and the starter account.
`UserManager.CreateAsync` commits the user in **its own unit of work**, so by the time the account
INSERT ran, the user was already durable. ADR-0036 measured what that cost when the INSERT failed:

```
first attempt   -> 500
user committed? -> YES
accounts owned  -> 0
roles assigned  -> 1
retry same user -> 409  "Registration could not be completed."
```

An unrecoverable, account-less user — because the pre-checks then find their own row and return the
enumeration-neutral 409 of [ADR-0013](0013-registration-user-enumeration.md) forever.

ADR-0036 fixed **one** cause: a duplicate account number, now retried. It recorded the rest as open,
and named the sharpest one — `EnableRetryOnFailure` retries deadlocks but
[ADR-0034](0034-failed-family-revoke-recovery.md) deliberately does **not** retry a command timeout
(`SqlException` −2), which is far likelier than a 1.37e-7-per-insert collision.

## What was measured first

The stranding was reproduced with a fault that is **not** a collision: an interceptor overflows the
100-character account `Name`, so SQL Server raises a genuine truncation error — non-transient,
nothing retries it, nothing recovers it. A real server error rather than an exception thrown in .NET,
because the question is what the database and EF do *together*; an exception raised before the
command runs would never reach the transaction and would prove nothing about rollback.

Against `main`, both assertions failed exactly as ADR-0036 predicted: the user was found
(`019fe6c9-…`), and the second registration with the same details returned **409**.

## Decision

**Wrap all three writes in one transaction.**

Identity is registered with `AddEntityFrameworkStores<AzureBankDbContext>()`, so `UserManager` writes
through the same scoped context `AuthService` holds and enlists in a transaction begun on it. That is
what makes this work without touching `UserStore.AutoSaveChanges`.

Run through `CreateExecutionStrategy()`, because `EnableRetryOnFailure` is on and EF refuses a
user-initiated transaction under a retrying strategy otherwise. The change tracker is cleared and the
entities rebuilt **inside** the delegate: a rolled-back attempt leaves them tracked, and a retry
reusing them would insert the previous attempt's rows alongside the new ones — the same trap
PR #93 hit in the seeder.

Exceptions are allowed to escape the delegate. `ConflictException` from the duplicate paths is not
transient, so the strategy does not retry it; the transaction disposes uncommitted and the caller
still gets the neutral 409.

**Only a confirmed duplicate becomes that 409.** `DbUpdateException` is EF's generic wrapper for
anything failing in the update pipeline, so an unnarrowed catch reported deadlocks, lock and command
timeouts, dropped connections and unrelated constraint failures to the caller as "these details are
taken". Wrapping the writes made that worse rather than better: `CreateAsync`'s `SaveChanges` is no
longer the outermost strategy execution and so no longer retries internally, leaving the delegate as
the only retry point — and the strategy decides by walking the inner exception chain, which
`ConflictException` does not have. So swallowing a transient there did not merely mislabel it, it
suppressed the retry. `ConcurrencyRetry.IsRegistrationDuplicate` narrows to
`IX_AspNetUsers_AzureTag` and `EmailIndex`; both names are pinned against real SQL Server violations
by `RegistrationDuplicateSqlServerTests`, because a typo would silently turn a race loser into a 500
and nothing else would notice.

**The commit is verified rather than assumed.** `ExecuteInTransactionAsync` carries a
`verifySucceeded` keyed on the UUIDv7 minted for that attempt — never on the email or the AzureTag,
either of which a *concurrent* registration could satisfy, which would hand this caller a 201 and a
JWT for somebody else's user. Without it, a transient raised by the commit itself re-runs the
delegate against a database that already holds the registration, and a caller who succeeded gets the
neutral 409 with no token and no account.

**The role result is checked.** A discarded `IdentityResult` from `AddToRoleAsync` was the one
remaining way to commit two of the three writes and still answer 201, which would have made this
record's central claim false.

**The account-number retry from ADR-0036 is kept.** Atomicity alone would turn a recoverable clash
into a rollback the caller has to redo; the retry turns it into a success. They compose: the retry
handles what it can, the transaction undoes what it cannot.

## What this rejects

- **Compensating delete** (remove the just-created user if the account fails). Leaves a second
  failure mode — the compensation itself can fail — and puts a "delete a user" primitive into the
  auth service, which is a dangerous thing to have lying around for a bug this narrow.
- **Idempotent-resumable registration** ("user exists but owns no account, so finish the job"). It
  touches ADR-0013 directly: any behaviour that treats one existing email differently from another
  risks becoming the existence oracle that ADR is built to deny.
- **A reconciliation job.** Asynchronous, and does nothing for the caller holding the 500.

## Consequences

- **The unit tests suppress `TransactionIgnoredWarning` rather than the production code branching on
  the provider.** `AuthService` already keys on `IsRelational()` twice, so a guard would have matched
  local precedent — but it would mean the InMemory tests exercise a *different* path from the one
  that ships. Suppressed, they run the real path with a no-op transaction. They therefore prove the
  neutral-409 logic and say **nothing** about rollback; atomicity is proved only by
  `RegistrationAtomicitySqlServerTests` on real SQL Server.
- **Registration now holds a transaction for the duration of password hashing.** Argon2id
  ([ADR-0003](0003-argon2id-password-hashing.md)) is deliberately expensive, so this widens the
  window a connection and its locks are held. Acceptable at this scale — registration is rare and
  the rows are new, so nothing contends on them — but it is the cost, and if registration ever
  becomes hot the hash should move outside the transaction.
- **The refresh token stays outside**, unchanged and still best-effort: it is issued after the commit
  precisely so its failure cannot roll back a registration that otherwise succeeded.
