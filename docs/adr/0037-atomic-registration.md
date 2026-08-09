# ADR-0037: Registration is all-or-nothing

**Status:** Accepted · **Date:** 2026-08-09 · **Supersedes nothing.** Closes the residual recorded in
[ADR-0036](0036-account-number-collision-recovery.md) under Consequences.

## Context

Registration writes three things: the Identity user, its default role, and the starter account.
`UserManager.CreateAsync` commits the user in **its own unit of work**, so by the time the account
INSERT ran, the user was already durable. ADR-0036 measured what that cost when the INSERT failed:

```text
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
