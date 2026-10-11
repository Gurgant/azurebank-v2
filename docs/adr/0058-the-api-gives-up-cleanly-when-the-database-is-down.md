# ADR-0058: The API gives up cleanly when the database is down

**Status:** Accepted · **Date:** 2026-09-29 · **Amended:** 2026-10-01 (ADR-0059, ADR-0060),
2026-10-05 (ADR-0064), 2026-10-11 (D6, D7, the table of values, residual risks 6 and 8) ·
**Decision Makers:** Vladislav Aleshaev

## Context

Before this decision a database outage answered a 500 with no `errorCode` and no `Retry-After`,
after 15 to 37 s, and a paused API left the BFF waiting 100 s for an empty 504. EF's retries were
logged nowhere, and locally SqlClient's pool kept handing out a cached error after the database was
back. A token cannot interrupt a commit, so a late cancel can lose the answer to money that moved.

## Preconditions

1. **One replica of the app, plus a second only while a revision replaces it.** Held on Azure by a
   Deny policy (ADR-0061), not by the app's code. The migration (ADR-0060) and a pool run (ADR-0064)
   are jobs at 5: 12 + 5 + 5 beside one API process, never both beside two (arithmetic only).
2. **A migration run has the same limits: met (ADR-0060).** Not covered: `dotnet ef … --connection`.

## Decision

- **D1: The connection limits are code defaults, and a value the connection string sets wins.**
  `SqlConnectionDefaults` adds NeverBlock pool blocking, which `Auto` already gives Azure SQL,
  because with it an outage ends when the database is back (validation on the compose stack).
- **D2: EF is the only retry layer: 4 retries, a back-off capped at 10 s, each logged at Warning**,
  because SqlClient's connect retry, set to 0, ran under each of EF's. −2 is not retried (ADR-0034).
- **D3: Every request but three has a deadline, 40 s**: `RequestDeadlineMiddleware` gives it one
  token that the deadline or a hang-up cancels. `RequestDeadlineUserManager` hands it to Identity,
  because without it a sign-in ran to 64.06 and 82.74 s, past the BFF's 503 at 55.05 and 55.02 s.
- **D4: Nothing cancels a commit that has started, and nothing lets one start after the deadline**,
  because a commit checks its token only before it starts. `CommitGateInterceptor` refuses it after
  the deadline or turns the deadline off; a failed commit turns it back on at its original instant.
- **D5: A result that exists is sent**: `DeadlineResultFilter` turns the deadline off before a
  result is written, because MVC's JSON formatter turns a cancelled write into an empty success.
  Once a commit has started nothing cancels the write, so the answer stored for replay is whole.
- **D6: An outage is one 503, and a client that hung up gets nothing**: `SERVICE_UNAVAILABLE` with
  `Retry-After` and `no-store` (measured: `no-cache,no-store`, `Pragma: no-cache`, `Expires: -1`)
  for a fired deadline, EF's spent retries, a pool-wait or bare timeout, and a `SqlException` EF
  calls transient, or numbered −2, 35, 11001, 18401, 17197 or 17142, or of class 20 or more.
  Anything else stays a 500; a gone client is answered 499 with no body. A deadline that cuts EF
  short leaves the SQL numbers in EF's retry Warnings for the same request. **A busy account is
  the same 503, and not an outage**: a write that lost an account's row to another write on each
  of its 8 attempts names a wait of 1 s, because a 500 tells a client to stop. A deposit, a
  withdrawal and both transfers answer so only when their idempotency record, read then, is still
  their own claim and `Processing` and no commit started: the conflict names the account alone
  even when the save lost the record too. A claim taken over stays a 500; a commit that landed is
  a 500 for a deposit and the 409 with `applied: true` for the other three. A closure and a change
  of the primary account hold no key: they answer so when no commit started, since each of their
  saves lands at a gated commit, and say nothing of `applied`. A rename's one statement passes no
  gate, so past its attempts it stays a 500. An account that closed meanwhile is not a busy one:
  after that proof it answers what it answers on any other attempt. A step-up authorisation is
  unspent after a busy 503: the same request, sent again, is made. The SPA words a busy account
  as it words an outage, and leaves sending the request again to the visitor.
- **D7: `applied: false` only when the answer knows it**: on a money 503, for a request that owns
  the idempotency claim it made, has let no commit start and is younger than `ProcessingStaleAfter`
  less 10 s, because money moves only in a gated commit. A busy account is under this rule and has
  none of its own, so a closure or a change of the primary account, which holds no claim, never
  says it. **A client never drops the key on `applied: false`**, because one told so wrongly would
  pay again under a new key.
- **D8: The idempotency bookkeeping has budgets of its own**: 3 s each to store an answer and to
  release a claim, because each writes one row. An empty 2xx is never stored: it is a lost answer.
- **D9: A stuck claim is stale after 2 minutes, not 10** (ADR-0009), because a claim whose release
  failed holds its key at 409 `IDEMPOTENCY_IN_FLIGHT` until then, and no request runs that long.
- **D10: An audited save asks whether its commit landed before it retries**, by looking for its own
  audit row, because a transient fault at the commit ended in a deposit's 201 with no ledger row. It
  asks once more under 3 s of its own when its token was cancelled after the commit was attempted.
- **D11: The BFF waits 55 s on both roads, and answers the same 503 when it gives up**, without
  `applied`: only the API knows. **The BFF never retries a visitor's request**; the SPA decides.
- **D12: What is exempt**: refresh, revoke and logout carry `[NoRequestDeadline]` (ADR-0057 §4.3),
  because a refused refresh's audit row must not be taken back by a hang-up and the other two are
  short and idempotent. A fresh scope has no deadline either: a refusal's audit row (ADR-0044 D1).

| The numbers, innermost first | Value | Why |
|---|---|---|
| Connect timeout | 10 s | SqlClient bounds each login by it, and each BEGIN, COMMIT and ROLLBACK too: it sends a transaction request with the connect timeout ([its source](https://github.com/dotnet/SqlClient/blob/v6.1.1/src/Microsoft.Data.SqlClient/netcore/src/Microsoft/Data/SqlClient/SqlInternalConnectionTds.cs)). A COMMIT slower than it fails, its outcome unknown. Not an open to a name that does not resolve: 11.81 to 23.75 s on the compose stack |
| EF retries, back-off cap | 4, 10 s | 12.1 s of waiting, and 32.1 s on Azure's throttling numbers, 40613 (a failover's error) among them: a reconfiguration [generally finishes within 30 seconds](https://learn.microsoft.com/en-us/azure/azure-sql/database/planned-maintenance). The budget ADR-0021, ADR-0034 and ADR-0036 quote |
| Request deadline | 40 s | Above the 35 s at which a hung command answers −2 (30 s, plus up to 5 s for SQL Server to acknowledge the cancel). 40 + 5 + 3 (the claim's release) + 5 = 53 < 55: every answer before a commit, `applied: false` included, reaches the visitor from the API. At 45 the sum is 58. `ProcessingStaleAfter` is at least a minute above it, checked at start |
| Max pool size | API 12, seeder 5 | 2 × 12 + 5 = 29 of the [30 logins of the Basic tier](https://learn.microsoft.com/en-us/azure/azure-sql/database/resource-limits-dtu-single-databases): two API processes while a revision replaces one, and one job at 5 (precondition 1). A second steady replica makes it 41 |
| `retryAfterSeconds`, `Retry-After` | 10; 1 for a busy account | EF's cap: a client that comes back after it meets a database EF would have tried again by then. A busy account names 1: the header counts whole seconds and 0 would send the caller straight back into the writes it lost to, so 1 is the least wait it can name. In ten runs, 24 parallel deposits were all answered within 0.9 to 2.3 s, and a caller that comes back to an account still busy is answered the same way. For machines: a visitor is told no time |
| A write's attempts on its account | 8 | `ConcurrencyRetry.MaxAttempts`, shared by every account write. 24 parallel deposits to one account on 2026-10-11: at 8, 13 to 15 answered busy over ten runs and 11 to 14 over five earlier ones; at 12, 8 to 10 over five. Half as many attempts again does not halve them, and each attempt is a turn at the audit chain's lock. At 8, over ten runs each, 7 to 10 of 24 parallel withdrawals answered busy, and 9 to 12 of 24 parallel internal transfers |
| BFF client and proxy timeout | 55 s | `BackendApi:TimeoutSeconds`, above the 53 (the wait ADR-0039 and ADR-0057 §8 quote). A proxied request takes 5 + 55 = 60 s at worst, since the session's renewal can spend 5 s first, under the [240 s of Azure Container Apps' ingress](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview) |
| The BFF's shorter waits | 30, 5 and 3 s | The renewal (30 s), revoke, the session-stamp poll and `/me`'s read-through (5 s each) and the health probe (3 s) stay as ADR-0057 and ADR-0039 set them: none writes anything a late answer could lose |

## Rejected

- Rejected: ASP.NET Core's `RequestTimeouts`, because the gate needs one atomic refuse-or-disarm.
- Rejected: cancelling everything at the deadline, because a committed transfer could answer 503.
- Rejected: never cancelling a write, because a hung database then holds its connections longest.
- Rejected: the limits in every connection string, because it misses user-secrets and the Azure one.
- Rejected: a 45 s deadline with the BFF at 60 s, because every visitor then waits 5 s longer.
- Rejected: a BFF at 70 s, because the tails after a commit answer "unknown" either way.
- Rejected: a 503 for a result that exists, because the result is true (D5).
- Rejected: a release budget sized by the BFF's remaining wait, because an exhausted pool beats it.
- Rejected: required tokens on the service interfaces, because CA2016 as an error already holds it.
- Rejected: rethrowing cancellations in registration, because a created account would answer 503.
- Rejected: retrying −2, because the work was blocked, and a retry holds a connection 30 s more.
- Rejected: YARP's own route `Timeout`, because YARP 2.3 reports it as the client cancelling.
- Rejected: one answer for the lock's 500 and the audit tail's 503, because it is its own decision.

## Consequences

- Every operation declares the 503. The audit tail's bounded read and a tripwire's audit row that
  cannot reach the database answer it too (ADR-0044, ADR-0057 §4.3).
- Costs: the SPA retries a read's 503 once (ADR-0059), about 45 s each; a pool of 12 is a ceiling.

**Residual risks**, each one not covered:

1. **A key held after a failed release**: it answers 409 `IN_FLIGHT` until its claim is stale (D9).
2. **A single statement outside a transaction can commit while a cancel races it**: a counted PIN or
   sign-in attempt, a claim, a step-up authorisation, or a new account, which carries no key.
3. **After a commit, the rest of the request is bounded only by EF's budget and the 3 s store**: up
   to 71 s on a hung database, past the BFF's own 503 at 55 s.
4. **A frozen API cannot enforce its deadline**; frozen after a commit started it is not measured.
5. **NeverBlock means every retry tries the server**: no run counted the logins the database got.
6. **An audited save's check can miss a commit that has not landed yet**, and the save runs again:
   for a deposit the claim's fence stops the second commit, a 500 with one ledger row. On the last
   attempt no save follows: the request's commit mark keeps it from a busy 503, and the record,
   `Executed` by then, would too.
7. **A commit whose acknowledgement is lost at or after the deadline**: a deposit can answer 503
   without `applied`; a registration that landed can answer 503, then the neutral 409 (ADR-0013).
8. **A withdrawal or a transfer compares its claim with the record as its last attempt read it**:
   one taken over before that read, by a request with the same bytes, would be answered busy
   without `applied`. Not measured: the request is past its deadline long before a claim is stale.

## Revisit when

- More than one replica of the app, or a larger database tier: the pool of 12 is re-sized.
- `Database:MaxRetryCount` above 4: at 5 the throttled waits, 47.1 s, no longer fit in 40 s.
- SqlClient gains an asynchronous commit that honours its token, or a COMMIT on Azure nears 10 s.
- The API is reached over a network (ADR-0057): the BFF's 55 s is derived again.

## Verified by

`TimeoutChainTests`, `CancellationFlowTests`, `CommitGateInterceptorTests`, `BackendTimeoutTests`,
`ServiceUnavailableExceptionHandlerTests`, `DatabaseUnavailableSqlServerTests`,
`RequestDeadlineSqlServerTests`, `DepositCommitFaultSqlServerTests`, `ConnectionDefaultsTests`,
`BalanceConcurrencySqlServerTests`, `DepositIntoClosedAccountSqlServerTests`,
`BusyMoneyWriteSqlServerTests`, `BusyAccountWriteSqlServerTests`,
`ConcurrencyRetryAccountRaceTests`, `ConcurrencyRetryBusyAnswerTests`.

## Related

ADR-0009, ADR-0013, ADR-0017, ADR-0022, ADR-0034, ADR-0044, ADR-0057, ADR-0059, ADR-0060, ADR-0064.
