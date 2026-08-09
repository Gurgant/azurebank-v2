# ADR-0036: Recovering from an account-number collision

**Status:** Accepted · **Date:** 2026-08-09 · **Supersedes nothing.** Closes the headroom question
left open by PR #88/#89, and completes the pattern [ADR-0035](0035-transaction-number-check-symbol.md)
established for the other generated identifier.

## Context

`AccountNumber` is generated as `AB-{1000..9999}-{1000..9999}-{10..99}` and lands in a unique index.
The open question was whether its entropy needed widening. Investigating it turned up a defect that
has nothing to do with entropy.

PR #90 gave `TransactionNumber` a recovery path and narrowed it **by index name**, so it
deliberately does not cover `IX_Accounts_AccountNumber`. Neither account-creation path caught
`DbUpdateException`: `AuthService` registration and `AccountService.CreateAccountAsync` were both a
bare `Add` followed by `SaveChangesAsync`.

## What was measured first

A collision was injected on the registration path with a `DbCommandInterceptor`, against real SQL
Server. The result was not simply a 500:

```text
first attempt   -> 500
user committed? -> YES
accounts owned  -> 0
roles assigned  -> 1
retry same user -> 409  "Registration could not be completed."
```

`UserManager.CreateAsync` commits the `ApplicationUser` in its own unit of work, so by the time the
account INSERT runs the user is already durable. When that INSERT failed, the caller was left
holding the email and the AzureTag with **no account at all** — and could never register again,
because the pre-checks then find their own row and return the enumeration-neutral 409 (ADR-0013).

**An unrecoverable, account-less user.** That is strictly worse than the transaction-number case PR
#90 fixed, which left the caller free to retry the deposit.

## The arithmetic, corrected in both directions

The space is **7,290,000,000**, confirmed empirically rather than read off the bounds: 400,000 draws
of `GetInt32(1000, 10000)` produced exactly 9000 distinct values spanning 1000–9999.

Two claims that were in circulation are both wrong:

- **"The space is shared by every account ever opened."** It is not. Deletion is soft and
  `IX_Accounts_AccountNumber` is **filtered** on `[IsDeleted] = 0`, so a closed account's number
  leaves the index and can be re-issued. The constrained population is *live* accounts.
- **"So a birthday bound over the live count is the answer."** That understates it. Because numbers
  are recycled, the right quantity is `inserts × live / N`, not `live² / 2N`. At a million inserts
  against a steady ten thousand live accounts that is **1.37 expected collisions** — about 200× the
  birthday figure. A long-lived system with churn can accumulate collisions without its live count
  ever approaching 100,000.

Under monotonic growth, `P(collision)` passes **1% at 12,106** accounts and **50% at 100,530**. Per
INSERT — the number that governs any single registration — it is `live/N`: 1.37e-7 at a thousand
accounts, 1.37e-5 at a hundred thousand.

## Decision

**Retry with a fresh number. Do not widen the format.**

`ConcurrencyRetry.IsAccountNumberCollision` mirrors the transaction predicate and is narrowed by
`IX_Accounts_AccountNumber` as well as by 2601/2627. Both creation paths go through
`ConcurrencyRetry.SaveNewAccountAsync`, which mints a new number and re-saves, up to `MaxAttempts`.

**The narrowing is load-bearing, and more so than it was for transactions.** The registration
request can legitimately lose the AzureTag or NormalizedEmail race, and that is the deliberate
enumeration-neutral 409. A predicate matching the error number alone would retry it — spinning on a
genuine duplicate and converting a security response into a loop. `IdempotencyRecord` carries the
same hazard the transaction case documented. The narrowing has its own test, and deleting it turns
that test red — verified by mutation, because the equivalent narrowing in #90 shipped with all 24
SQL proofs passing and no coverage at all.

The index name is **declared** rather than inherited from EF's convention
(`.HasDatabaseName("IX_Accounts_AccountNumber")`, the same string the convention already emitted, so
no migration). Without that the predicate would be keyed on a name EF is free to change, which would
silently turn this recovery back into a 500 and an account-less user. `TransactionConfiguration`
carries the identical declaration for the identical reason; this one was missing.

Widening the format was rejected: the column is exactly full at 15 characters so it needs a
migration, it touches masking (ADR-0020), the reveal endpoint, the spec, generated frontend types
and every fixture — and it would buy a smaller reduction in user-visible failure than three lines of
retry, because at 1.37e-7 per insert the collision was never the likely cause of the stranding.

Widening also has a **silent** failure mode the retry does not.
`AccountMapper.MaskAccountNumber` guards on `Length < 14` and then hard-codes
`$"{n[..3]}****-****-{n[^2..]}"`. A longer number would return a mask that quietly drops the added
group — no exception, no failing test. A format change would have to fix the mask first.

## Consequences

- **The retry fixes the rarest cause of stranding, not all of them.** Any failure at that INSERT
  strands the user the same way. `EnableRetryOnFailure(maxRetryCount: 3)` already retries deadlocks
  (1205) and the throttling numbers, but ADR-0034 deliberately does **not** retry a command timeout
  (`SqlException` −2), so a timeout at that line still leaves an account-less user. Recorded rather
  than fixed here: making registration atomic across two units of work is a larger design change
  than this PR, and is filed separately.
  > **Closed by [ADR-0037](0037-atomic-registration.md).** Registration now commits the user, the
  > role and the account in one transaction, so *every* cause of that INSERT failing rolls the whole
  > registration back rather than only the duplicate-number one. The retry described above is kept:
  > it turns a recoverable clash into a success instead of a rollback the caller has to redo.
- **Entropy is now genuinely a non-issue rather than an unexamined one.** A collision is recovered
  in-process; the 1%-at-12,106 figure matters only for how often the `AccountNumberCollision`
  warning appears, not for whether anything breaks.
- **If that warning is ever seen in a real log, re-examine the format, not the retry.** At these
  probabilities it should be unobservable, so an actual occurrence means the entropy assumption is
  wrong.
- **A side effect worth naming: it removes a Development-only disclosure.** SQL Server's duplicate-key
  message carries the offending value — `The duplicate key value is (AB-1234-5678-90)` — and
  `GlobalExceptionHandler` puts `exception.Message` into `Detail` when
  `_environment.IsDevelopment()`. A raw 500 from this INSERT on the `[AllowAnonymous]` registration
  endpoint therefore echoed **another customer's full account number**: exactly the value ADR-0020
  masks and PIN-gates. Recovering before the exception escapes closes that path for the common
  trigger. It does not make the handler safe in general — any other Development 500 still returns
  its message — but that is a separate question about the handler, not about account numbers.
