# ADR-0034: Recovery for a family revoke that fails

**Status:** Accepted; decision 1 is superseded by ADR-0057, F3. Decisions 2 and 3, the record of
which SQL errors EF retries and why −2 is not among them, stay · **Date:** 2026-08-08 ·
**Amended:** 2026-09-25, 2026-09-28 (ADR-0057), 2026-09-30 (ADR-0058)

## Context

Before ADR-0057 the reuse detection of ADR-0021 revoked the user's whole active set of refresh
tokens when one was presented twice, and a revoke that failed did not change the answer: the 401
was the contract, the revoke a mitigation. The bad case was attacker-first. The attacker rotates
the stolen token, the legitimate client replays the old one after the grace window, reuse is
detected and the revoke fails: the 401 goes to the legitimate client, so no further replay may run
the mitigation again, and the attacker's successor stays active until logout or the 7-day expiry.
The options were an inline retry, a durable work item, or accepting the residual with detection,
and the choice turned on which failures EF's execution strategy already retries.

## Decision

1. **Superseded by ADR-0057, F3.** The decision was to accept the residual, with detection, and to
   add neither an inline retry nor a durable work item, because the failure had never been
   observed. ADR-0057's tripwire records a grant presented after its session ended and revokes
   nothing, so no family revoke is left to fail and there is nothing to recover.
2. **What EF retries is its own detector's list, with `errorNumbersToAdd: null`**, because the list
   is measured and not remembered: `SqlServerTransientExceptionDetector` of
   `Microsoft.EntityFrameworkCore.SqlServer` 10.0.1 answers as below, and every `ExecuteUpdateAsync`
   runs through that strategy.

   | SQL error                           | retried by EF? |
   | ----------------------------------- | -------------- |
   | 1205, deadlock victim               | **yes**        |
   | −2, command timeout                 | **no**         |
   | −2 with the real inner `Win32(258)` | **no**         |
   | bare `TimeoutException`             | yes            |
   | 40613, database unavailable         | yes            |
   | 10928, resource limit               | yes            |
   | 233, connection init                | yes            |
   | 50000, user `RAISERROR` (control)   | no             |

3. **A command timeout (−2) is not retried**, because it means the write sat blocked for the full
   `CommandTimeout(30)`: a retry does not make it likelier to succeed and holds the request, and a
   database connection, for another 30 seconds per attempt. SqlClient throws `SqlException` with
   `Number == -2` for it and never the bare `TimeoutException` that EF treats as transient.

## Rejected

- Rejected: an inline retry of the revoke, because EF already retries a deadlock, and the one
  failure it does not retry, the timeout of decision 3, sits on a path an attacker triggers at will
  by replaying a stolen token: a bounded retry turns a cheap 401 into a request that occupies a
  connection for a minute or more, on demand.
- Rejected: a durable work item (a "revoke pending" row swept by `RefreshTokenCleanupService`),
  because it is another write through the same `DbContext`, execution strategy and database at the
  moment that database has refused one, and it costs a table, a migration, a sweep and idempotency
  on replay for a failure seen neither in CI nor under deliberate contention (12 rounds of 17
  concurrent requests, `READ_COMMITTED_SNAPSHOT` off).

## Consequences

- The retry budget is a setting and does not change the list: `Database:MaxRetryCount` and
  `Database:MaxRetryDelay`, 4 and 10 s unless a deployment sets them (ADR-0058). Raising either
  lengthens how long a retried deadlock holds a connection.
- A deadlock is retried by EF before any `catch` of the application sees it.
- Since ADR-0057 no attacker-first window of this kind is left: a copy of a live grant works until
  its session ends, at most 60 minutes (ADR-0057, §3).
- `RefreshTokenReuseRevokeFailed` is no longer raised; the name stays for the rows already written.
- A caller that hangs up cannot stop the tripwire's audit row.
- The per-user revocation stamp this record preferred, should the exposure matter more, is
  ADR-0057's (§5.3), checked by the BFF and not on the API's validation.

## Verified by

- `RetryBudgetTests`: the budget EF retries with, observed on a bare `TimeoutException`.
- `RefreshTokenServiceTests.RenewAsync_TheTripwiresRow_IsWrittenEvenWhenTheCallerHangsUp`.
- Decisions 2 and 3 are the registration itself: `AddInfrastructure`, `errorNumbersToAdd: null`.

## Related

ADR-0021, ADR-0057, ADR-0058.
