# ADR-0034: Recovery for a family revoke that fails

**Status:** Accepted · **Date:** 2026-08-08 · **Supersedes nothing.** Closes the residual left open
by the [ADR-0021](0021-refresh-token-rotation-bff-remint.md) amendment of 2026-08-04.

## Context

Reuse-detection revokes the user's entire active refresh-token set. PR #74 stopped a failed revoke
from changing the status code — the 401 is the contract, the revoke is a mitigation — but added no
recovery, and recorded the gap honestly:

> The bad case is attacker-first: the attacker rotates the stolen token, the legitimate client later
> replays the old one after the grace window, reuse is detected, and the revoke fails. The 401 goes
> to the _legitimate_ client, so there may be no further replay to re-run the mitigation, and the
> attacker's successor stays active until logout or the 7-day expiry.

That amendment deliberately deferred three options: **retry inline**, **a durable work item**, or
**accept with detection**. This ADR decides between them.

## What was measured first

The amendment named the suspected triggers as "a deadlock victim or a command timeout". Both
assumed the failure reaches our `catch` at all — but the context is registered with
`EnableRetryOnFailure(maxRetryCount: 3)` (`ServiceCollectionExtensions.cs`), and EF runs every
`ExecuteUpdateAsync` through that strategy. So the real question is which of those triggers EF
already handles.

Probed against the shipped `Microsoft.EntityFrameworkCore.SqlServer` 10.0.1 assembly, asking its own
`SqlServerTransientExceptionDetector` (2026-08-08):

| SQL error                              | retried by EF? |
| -------------------------------------- | -------------- |
| 1205 — deadlock victim                 | **yes**        |
| −2 — command timeout                   | **no**         |
| −2 with the real inner `Win32(258)`     | **no**         |
| bare `TimeoutException`                 | yes            |
| 40613 — database unavailable            | yes            |
| 10928 — resource limit                  | yes            |
| 233 — connection init                   | yes            |
| 50000 — user `RAISERROR` (control)      | no             |

Two things follow, and neither was obvious from reading:

1. **A deadlock on the revoke is already retried three times** before the exception could ever reach
   the reuse branch. Any inline retry we add would be a _fourth_ layer for the case it is usually
   written for.
2. **A command timeout is not retried at all.** EF treats a bare `TimeoutException` as transient,
   but SqlClient does not throw one for a command timeout — it throws `SqlException` with
   `Number == -2`, which is not in the list, and `errorNumbersToAdd` is `null`. The comment at that
   registration ("Retry on transient failures (network issues, deadlocks)") is right about deadlocks
   and silent about timeouts; it has been corrected in place.

## Decision

**Accept the residual, with detection. Add neither an inline retry nor a durable work item.**

### Why not retry inline

The measurement removes the usual argument for it (deadlocks are covered) and turns the remaining
case into an argument _against_ it. The one failure EF does not retry is a **command timeout** —
which means the write sat blocked for the full `CommandTimeout(30)`. Retrying that inline does not
make the write more likely to succeed; it holds the request, and a database connection, for another
30 seconds per attempt.

That cost lands on a path **an attacker can trigger at will**: replaying a stolen token is what
_causes_ the reuse branch to run. A bounded retry there converts a cheap 401 into a request that can
be made to occupy a connection for a minute or more, on demand. Retrying the one failure mode EF
declines to retry would be the reflex, not the fix.

### Why not a durable work item

An outbox row (or any "revoke pending" marker swept later by
`RefreshTokenCleanupService`) is **another write, through the same `DbContext`, the same execution
strategy and the same database**, issued at the moment that database has just refused a write. Under
the failure it is meant to survive, it is subject to the same failure.

It is not _identically_ likely to fail — the marker is a single insert on an uncontended table,
where the revoke is a set update on the index concurrent rotations are writing, so it would probably
survive a blocking timeout. That narrow advantage is the honest case for it. It does not carry a
table, a migration, sweep scheduling, idempotency on replay, and a convergence window bounded by the
sweep interval (currently 6 h), for a failure that **has never been observed** — not in CI, and not
locally under deliberate contention (12 rounds × 17 concurrent requests, `READ_COMMITTED_SNAPSHOT`
off).

### What "with detection" has to mean

Accepting a residual on the strength of observability is only honest if the observability is real.
It was previously asserted by a comment and by nothing else — the existing fault-injection test
passes a `Mock<ILogger>` and never verifies it, so deleting the log statement would not have failed
a single test. Two tests now pin it:

- the failed revoke logs at `Error` under `SecurityEvent RefreshTokenReuseRevokeFailed`, carrying
  the user id;
- `OperationCanceledException` still propagates and is **not** logged as a failed mitigation — a
  caller who hung up is not a security event, and burying it in the same counter would poison the
  signal this decision depends on.

A third test makes the residual itself falsifiable rather than prose: after a failed revoke the
family is asserted to be **still active**. If a future change makes the revoke converge anyway, that
test fails and this ADR is what needs revisiting.

## Revisit when

This decision is a function of "never observed". It flips the moment that stops being true:

- **`SecurityEvent RefreshTokenReuseRevokeFailed` appears in a real sink even once** → implement the
  durable work item. The design is pre-decided above: a marker row written on the failure path, swept
  by `RefreshTokenCleanupService`, sweep interval dropped to minutes. The reason not to build it is
  the absence of evidence, not a defect in the design.
- **The exposure ceiling matters more than it does today** — a shorter refresh lifetime than 7 days,
  or a requirement to prove revocation — → prefer a per-user `RefreshTokensRevokedAfter` stamp
  checked on validation. That dominates both options here, because it converts an N-row write on a
  contended index into a single-row write on a cold one _and_ makes revocation total rather than
  partial: the family dies from one timestamp, whether or not the row updates landed. It is not
  proposed now because it changes the validation path and costs a migration for an unobserved
  failure.

## Consequences

- The attacker-first window is unchanged: bounded by logout or the 7-day expiry. Stated, not fixed.
- No new table, no migration, no scheduled work, no added latency on the reuse path.
- The claim the decision rests on is now enforced by tests rather than by comments.
- One inaccurate comment about retry coverage is corrected, which was itself a small instance of the
  rule this repo keeps relearning: the behaviour of a dependency is measured, not remembered.
