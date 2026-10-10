# ADR-0036: Recovering from an account-number collision

**Status:** Accepted · **Date:** 2026-08-09 · **Amended:** 2026-08-09 (ADR-0037), 2026-09-30
(ADR-0058). Supersedes nothing. Completes ADR-0035's pattern for the other generated identifier.

## Context

`AccountNumber` is generated as `AB-{1000..9999}-{1000..9999}-{10..99}` and lands in a unique index.
The transaction number's recovery (ADR-0035) is narrowed by index name, so it does not cover
`IX_Accounts_AccountNumber`, and neither account-creation path caught `DbUpdateException`:
registration in `AuthService` and `AccountService.CreateAccountAsync` were a bare `Add` followed by
`SaveChangesAsync`. On registration a collision was worse than a 500. `UserManager.CreateAsync`
committed the user in its own unit of work, so a failed account INSERT left a user holding the email
and the AzureTag with no account, who could never register again: the pre-checks find their own row
and answer the enumeration-neutral 409 (ADR-0013).

## Decision

**Retry with a fresh number. Do not widen the format.**

1. **Both creation paths go through `ConcurrencyRetry.SaveNewAccountAsync`**, which mints a new
   number and saves again, up to `MaxAttempts` (8), because a clash otherwise answers 500.
2. **The predicate is narrowed by index name.** `ConcurrencyRetry.IsAccountNumberCollision` mirrors
   the transaction predicate: errors 2601 and 2627, and `IX_Accounts_AccountNumber`. Registration
   can legitimately lose the AzureTag or NormalizedEmail race, and that is the deliberate neutral
   409: a predicate on the error number alone would retry it, spin on a genuine duplicate and turn a
   security response into a loop. `IdempotencyRecord` carries the same hazard.
3. **The index name is declared**, `.HasDatabaseName("IX_Accounts_AccountNumber")`, the string EF's
   convention already emitted, so there is no migration. Otherwise the predicate is keyed on a name
   EF is free to change, which silently turns the recovery back into a 500.
   `TransactionConfiguration` carries the same declaration for the same reason.
4. **The format is not widened**, on this arithmetic. The space is **7,290,000,000** values.
   Deletion is soft and the index is filtered on `[IsDeleted] = 0`, so a closed account's number can
   be issued again: the constrained population is the live accounts. Because numbers are recycled,
   the expected collisions are `inserts × live / N`, not the birthday bound `live² / 2N`: a million
   inserts against a steady ten thousand live accounts give **1.37**, about 200 times the birthday
   figure. Under monotonic growth `P(collision)` passes **1% at 12,106** accounts and **50% at
   100,530**. Per INSERT it is `live / N`: 1.37e-7 at a thousand accounts, 1.37e-5 at a hundred
   thousand.

## Rejected

- Rejected: widening the format, because the column is exactly full at 15 characters, so it needs a
  migration and touches masking (ADR-0020), the reveal endpoint, the spec, the generated frontend
  types and every fixture, and it buys less than the retry: at 1.37e-7 per insert the collision was
  never the likely cause of the stranding.
- Widening also fails silently: `AccountMapper.MaskAccountNumber` guards on `Length < 14` and then
  hard-codes `$"{n[..3]}****-****-{n[^2..]}"`, so a longer number gets a mask that drops the added
  group, with no exception and no failing test. A format change has to fix the mask first.

## Consequences

- A collision is recovered in-process. The 1%-at-12,106 figure matters only for how often the
  `AccountNumberCollision` warning appears, not for whether anything breaks.
- The retry fixes the rarest cause of a failed account INSERT, not all of them. The execution
  strategy (`Database:MaxRetryCount`, 4, ADR-0058) retries deadlocks (1205) and the throttling
  numbers, and ADR-0034 deliberately does not retry a command timeout (`SqlException` -2). ADR-0037
  closes the rest: registration commits the user, the role and the account in one transaction, so
  every cause rolls the whole registration back. The retry is kept, because it turns a recoverable
  clash into a success instead of a rollback the caller has to redo.
- It removes a Development-only disclosure. SQL Server's duplicate-key message carries the offending
  value, and `GlobalExceptionHandler` puts `exception.Message` into `Detail` when
  `_environment.IsDevelopment()`, so a raw 500 from this INSERT on the `[AllowAnonymous]`
  registration endpoint echoed another customer's full account number (ADR-0020).
- Not covered: the handler in general. Any other Development 500 still returns its message.

## Revisit when

- The `AccountNumberCollision` warning appears in a real log: at these probabilities it should be
  unobservable, so the entropy assumption is wrong. Re-examine the format, not the retry.

## Verified by

- `AccountNumberCollisionSqlServerTests`: registration and account creation survive an injected
  collision on SQL Server, and a unique violation on another index is not treated as one.

## Related

ADR-0013, ADR-0020, ADR-0034, ADR-0035, ADR-0037, ADR-0058.
