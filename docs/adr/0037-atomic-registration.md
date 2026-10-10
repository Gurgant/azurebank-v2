# ADR-0037: Registration is all-or-nothing

**Status:** Accepted · **Date:** 2026-08-09 · **Amended:** 2026-09-17 (ADR-0003, the password hash
is PBKDF2). Supersedes nothing. Closes the residual of ADR-0036 (Consequences).

## Context

Registration writes three things: the Identity user, its default role and the starter account.
`UserManager.CreateAsync` commits the user in its own unit of work, so without this decision the
user is durable before the account INSERT runs. If that INSERT fails, the result is a user with no
account who can never register again: the pre-checks find their own row and answer the
enumeration-neutral 409 of ADR-0013 forever. ADR-0036 retries one cause, a duplicate account number.
The others stay open: the execution strategy retries deadlocks, and ADR-0034 deliberately does not
retry a command timeout (`SqlException` -2), which is far likelier than a collision at 1.37e-7 per
insert.

## Decision

**All three writes are wrapped in one transaction.**

1. **`UserManager` enlists in the transaction `AuthService` begins**, because Identity is registered
   with `AddEntityFrameworkStores<AzureBankDbContext>()` and writes through the same scoped context.
   `UserStore.AutoSaveChanges` is not touched.
2. **The transaction runs through `CreateExecutionStrategy()`**, because EF refuses a user-initiated
   transaction under a retrying strategy otherwise. The change tracker is cleared and the entities
   are rebuilt inside the delegate: a rolled-back attempt leaves them tracked, and a retry that
   reused them would insert the previous attempt's rows beside the new ones.
3. **Exceptions escape the delegate.** `ConflictException` from the duplicate paths is not
   transient, so the strategy does not retry it: the transaction disposes uncommitted and the caller
   gets the neutral 409.
4. **Only a confirmed duplicate becomes that 409.** `ConcurrencyRetry.IsRegistrationDuplicate`
   narrows to `IX_AspNetUsers_AzureTag` and `EmailIndex`, because `DbUpdateException` is EF's
   generic wrapper: an unnarrowed catch reports deadlocks, lock and command timeouts, dropped
   connections and unrelated constraint failures as "these details are taken". It also suppresses
   their retry: the delegate is the only retry point, and the strategy decides by walking an inner
   exception chain that `ConflictException` does not have.
5. **The commit is verified, not assumed.** `ExecuteInTransactionAsync` carries a `verifySucceeded`
   keyed on the UUIDv7 minted for that attempt. Without it, a transient raised by the commit itself
   re-runs the delegate against a database that already holds the registration, and a caller who
   succeeded gets the neutral 409 with no token and no account. The key is never the email or the
   AzureTag: a concurrent registration could satisfy either, and this caller would get a 201 and a
   JWT for somebody else's user.
6. **The role result is checked**, because a discarded `IdentityResult` from `AddToRoleAsync` is the
   one remaining way to commit two of the three writes and still answer 201.
7. **The account-number retry of ADR-0036 is kept**, because atomicity alone turns a recoverable
   clash into a rollback the caller has to redo. The retry handles what it can, and the transaction
   undoes what it cannot.
8. **The refresh token stays outside the transaction**, issued after the commit and best-effort, so
   that its failure cannot roll back a registration that otherwise succeeded.

## Rejected

- Rejected: a compensating delete (remove the just-created user if the account fails), because the
  compensation can fail too, and it puts a "delete a user" primitive into the auth service.
- Rejected: idempotent-resumable registration ("the user exists but owns no account, so finish the
  job"), because any behaviour that treats one existing email differently from another risks
  becoming the existence oracle ADR-0013 denies.
- Rejected: a reconciliation job, because it is asynchronous and leaves the caller with the 500.

## Consequences

- Every cause of a failed write rolls the whole registration back, and the caller can try again.
- The unit tests suppress `TransactionIgnoredWarning` and run the real path with a no-op
  transaction, where a provider branch in production code would give them a path that does not ship.
  They prove the neutral-409 logic and say nothing about rollback.
- Registration holds a transaction for the duration of password hashing: Identity's PBKDF2 at
  100,000 iterations (ADR-0003), which `UserManager.CreateAsync` runs inside it. A connection and
  its locks are held longer. Accepted at this scale: registration is rare and the rows are new, so
  nothing contends on them. If registration becomes hot, the hash moves outside the transaction.

## Verified by

- `RegistrationAtomicitySqlServerTests`: on SQL Server a truncation error on the account INSERT
  (`OverlongAccountNameInterceptor`), which nothing retries, leaves no user behind.
- `RegistrationDuplicateSqlServerTests`: both index names. `AuthServiceTests`: the neutral 409.

## Related

ADR-0003, ADR-0013, ADR-0034, ADR-0036.
