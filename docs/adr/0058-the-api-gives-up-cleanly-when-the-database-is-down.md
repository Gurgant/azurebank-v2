# ADR-0058: The API gives up cleanly when the database is down

**Status:** Accepted · **Date:** 2026-09-29 · **Decision Makers:** Vladislav Aleshaev ·
**Amends** [ADR-0009](0009-idempotency-monetary-operations.md) (the claim's release and the stored
answer get 3 s each, an empty success is never stored, a money 503 may say `applied: false`, and
`ProcessingStaleAfter` is 2 minutes), [ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md)
(the tail bound's refusal is a 503, and an audited save asks whether its commit landed before it
retries), [ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) §4.3 and §8
(a tripwire row that cannot reach the database is a 503; the BFF waits 55 s on both of its roads),
the retry budget quoted in [ADR-0021](0021-refresh-token-rotation-bff-remint.md),
[ADR-0034](0034-failed-family-revoke-recovery.md) and
[ADR-0036](0036-account-number-collision-recovery.md), and the BFF client's wait quoted in
[ADR-0039](0039-bff-session-cache-is-a-fallback.md)

## Preconditions

Two things this decision rests on that no code in the repository enforces. Break either one and
the sums under "The numbers" stop adding up.

1. **One replica of the app, plus a second only while a revision replaces it.** The API's pool of
   12 is sized so that two API processes and one job at 5 open at most 2 × 12 + 5 = 29 connections,
   within the 30 concurrent logins and 30 workers of Azure SQL's Basic tier
   (<https://learn.microsoft.com/en-us/azure/azure-sql/database/resource-limits-dtu-single-databases>).
   A second steady replica makes it 41. The BFF keeps its sessions in memory, so it runs as one
   replica anyway (ADR-0057); scaling out is a shared session store first, and this pool with it.
   Any other process that opens the database through `AddInfrastructure` (the notice-relay
   Function of ADR-0051, not deployed, and `backend/tools/AzureBank.AuditVerifier`) gets a pool of
   up to 12 unless its settings say otherwise; one that runs beside the app sets
   `Database:MaxPoolSize` so the sum stays within 30.
2. **A migration run gets the same connection limits in its own change.** `dotnet ef` builds its
   context from `DesignTimeDbContextFactory`, which sets its own options and applies no
   `SqlConnectionDefaults`, so a migration run that way still opens with SqlClient's defaults
   (15 s to connect, one connect retry, a pool of 100, pool blocking `Auto`). The seeder's
   `reset`, which migrates through `AddInfrastructure`, has them. A deployment that migrates
   through `dotnet ef`, or a bundle built from it, applies `SqlConnectionDefaults` in that factory
   first.

## Context

Measured on 2026-09-29 on the local compose stack, before this decision, with outage scripts kept
outside this repository that stop the SQL Server container ("refused": its name stops resolving,
SQL error 11001), pause it (a hang) or pause the API. One run per row unless the row says
otherwise.

| Outage | What the visitor got |
|---|---|
| Database refused, 60 s and 120 s, sign-in, signed-in read, transfer (6 runs) | **500** after 15.0 to 36.9 s, the generic body with no `errorCode` and no `Retry-After` |
| Database hung, 60 s, sign-in and read | **500** at 35.0 s: the command's own timeout, SQL error −2 |
| API paused 120 s, a proxied read | an **empty 504** at 100.0 s, YARP's own timeout, no `Content-Type` |
| API paused 120 s, a sign-in | an **empty 500** at 100.0 s; the queued sign-in then ran when the API resumed and wrote a 60-minute grant 19.3 s after the visitor's 500 |

Four things in those runs decided the shape of this record:

- **Every outage was a 500,** which reads as a bug, and nothing told a client that trying again
  was the right answer. One transfer failed on a pool-wait timeout while reading its idempotency
  key: an `InvalidOperationException` that nothing classified.
- **EF's retries were invisible.** EF raises its "retrying" event at Information and the hosts log
  EF at Warning, so a request could spend its whole budget retrying and leave no line saying so.
- **The API went on failing after the database was back:** the last 500 came 4.1 to 18.6 s after
  the database answered again, and a request still retrying failed for longer. In the sign-in run
  refused for 120 s the database answered again at 16:32:04.3 UTC, every open still failed within
  about a millisecond with error 11001 until 16:32:27.0, and the first 200 came at 16:32:30.4,
  26.1 s after. That matches SqlClient's pool blocking period as documented: after a failed open
  the pool hands the cached error to every caller for 5 s, doubling up to a minute, without trying
  the server. `Auto` turns it off for Azure SQL and on for everything else
  (<https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlclient.poolblockingperiod>,
  <https://learn.microsoft.com/en-us/sql/connect/ado-net/sql-server-connection-pooling>). If that
  is the cause, locally EF's retries were replays that never reached the server, and no local run
  said anything about Azure; the runs in the pull request, with NeverBlock, test it.
- **The BFF waited 100 s** on both roads: its own client had `HttpClient`'s default, and the proxy
  YARP's. The API had no bound of its own, and a frozen API cannot enforce one anyway.

The tests this decision adds were run first on the commit before it, and three of them measured
defects that were already there: a deposit whose commit failed transiently answered **201 with no
ledger row**, a balance of 0 and its idempotency record still `Processing`
(`DepositCommitFaultSqlServerTests`); a client that hung up after a deposit committed left an
**empty body stored for replay**, and its retry was answered an empty 201
(`RequestDeadlineSqlServerTests`); and a proxied call the API could not reach came back as YARP's
**empty 502** (`BackendTimeoutTests`).

## Decision

**D1 — The connection limits are code defaults, applied only where the connection string leaves
them unset.** `SqlConnectionDefaults.Apply` writes `Connect Timeout=10`, `ConnectRetryCount=0`,
`Max Pool Size=12` (the seeder: 5, in its own `appsettings.json`) and `Pool Blocking
Period=NeverBlock` into the string every host opens through `AddInfrastructure`. A value the string
sets wins, under any of its names (`Connection Timeout` is `Connect Timeout`). The three numbers are
`Database:ConnectTimeoutSeconds`, `Database:ConnectRetryCount` and `Database:MaxPoolSize`,
validated at the API's start. NeverBlock is what `Auto` already gives Azure, so Azure behaves as
before and a local or CI outage now behaves like one on Azure. The API logs the limits it opened
with once it has started, read back from the context it builds.

**D2 — EF is the only retry layer: 4 retries, the back-off capped at 10 s, each retry logged at
Warning.** SqlClient's own connect retry is off (D1): it retried under each of EF's attempts, and
one open of an unroutable address took 36.8 s (`AddObservability` records the measurement). A
command timeout, −2, is still not retried, for ADR-0034's reasons.

**D3 — Every request but three has a deadline, 40 s.** `RequestDeadlineMiddleware` runs inside
`UseExceptionHandler`, before the service credential, authentication and the idempotency claim. It
replaces `RequestAborted` with one token that either the deadline or the client hanging up cancels,
and every action, service method and EF call takes it: `CancellationFlowTests` holds the actions,
the service interfaces and every EF call in the API and Infrastructure to it, and CA2016, "forward
the token", is an error in both projects. Identity's calls take it too. `AddIdentity` registers the
base `UserManager`, whose token is always `None`, so a sign-in's lookup, its password check and
every create or update through Identity ran with none; the API registers
`RequestDeadlineUserManager`, which reads the deadline from the request's scope at each call, where
a fresh scope and the three exempt endpoints find none, and `CancellationFlowTests` holds that
registration. `RequestDeadline:Seconds`, 1 to 600, validated at start.

**D4 — Nothing cancels a commit that has started, and nothing lets one start after the deadline.**
A token can stop a commit from starting and can never interrupt one: SqlClient has no asynchronous
commit, and `DbTransaction.CommitAsync` checks its token only before it starts
(<https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.Data.Common/src/System/Data/Common/DbTransaction.cs>).
Cancelling a commit in flight can only lose its answer: the money moved, and the visitor is told to
try again. So `CommitGateInterceptor`, at each commit of the request's own context, either refuses
to let the commit start, because the deadline has already fired, or turns the deadline off; one
lock decides which of the two comes first. A commit that fails turns the deadline back on at its
original instant, and the request stays marked as one that started a commit. One hook covers the
explicit commits (transfers, the withdrawal, set-primary, account closure), the audited saves and
registration's, and the transaction EF opens around a save by itself, which raises the same events
(no test here exercises that last one). Not gated: a context from a fresh scope (a refusal's audit
row, the release of a claim, the PIN counters), the three exempt endpoints, and single statements
outside a transaction. A money request moves money in exactly one gated commit, which
`RequestDeadlineSqlServerTests` counts on the metric `azurebank.commit_gate.entered`: one per
success, on each of the four endpoints.

**D5 — A result that exists is sent.** `DeadlineResultFilter` turns the deadline off before any
MVC result is written, because MVC's JSON formatter swallows a cancellation of `RequestAborted` and
writes nothing under a status that already says success
(<https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Mvc/Mvc.Core/src/Formatters/SystemTextJsonOutputFormatter.cs>).
`RequestAborted` becomes the client's token again for the write, except once a commit has started:
then it stays the request's, which nothing can cancel any more, so a keyed answer is captured
whole even for a client that has gone, and its retry is replayed the whole answer. A commit that
failed turns the deadline back on (D4), and when the save then finds that commit landed after all
(D10), the deadline may already have fired: that answer is written under no token.

**D6 — An outage is one 503, and a client that hung up gets nothing.**
`ServiceUnavailableExceptionHandler` answers **503** `SERVICE_UNAVAILABLE` with
`retryAfterSeconds: 10`, `Retry-After: 10` and `Cache-Control: no-store`, and logs a Warning with
the route pattern (never the path, ADR-0017), the elapsed time, every SQL error number in the
exception chain and the chain's types. It walks the whole inner-exception chain, since EF wraps
what failed. A 503 for:

| What failed | Why it is an outage |
|---|---|
| The request's deadline fired, whatever it threw | a cancelled command surfaces as whatever SqlClient and EF make of it |
| EF's retries were spent (`RetryLimitExceededException`) | the database failed longer than the budget |
| A `SqlException` EF's own detector calls transient, or numbered −2, 35, 11001, 18401, 17197 or 17142, or of class 20 or more | the database could not be reached or did not answer in time; EF's detector is asked directly, so the list cannot drift from what EF retries |
| SqlClient's pool-wait timeout, matched by its English message: SqlClient ships translations, so the API sets its UI culture to the invariant one as it starts | its type, `InvalidOperationException`, is otherwise a bug |
| A bare `TimeoutException` | |

Everything else stays the 500 it was: a unique violation, a concurrency conflict, the application
lock's own `THROW 50000`. That makes the audit tail's bounded read, a −2, a 503 (ADR-0044's note).
`ClientAbortedExceptionHandler` runs first: if the client's own token is cancelled it sets 499 and
writes nothing, because with the token reaching every database call a hang-up also surfaces as a
`SqlException`, a `DbUpdateException` or a retry limit, and those were answered 500 at Error to a
client that was gone. It logs at Debug only when the hang-up caused the failure (the deadline
records the client as what cancelled the request before any commit started, or the chain holds the
cancellation); anything else, such as an exempt endpoint's failure, whose calls never see that
token, or a commit that failed after the client left, is a Warning with the SQL error numbers and
the chain the 503 would have named.

**D7 — `applied: false` only when the answer knows it.** On the four money endpoints, and only for
a request that owns the idempotency claim it made (`OwnedIdempotencyClaim`, set after a claim that
was not a replay) and has let no commit start, the 503 adds `applied: false` and says "nothing was
changed". A money request moves money only inside a gated commit (D4), so no commit started means
no money moved. Anywhere else the key is left out, never set to a guess: a commit that started may
have landed even if it failed, and a request that failed reading its key, claiming it or writing a
replay cannot know what an earlier request with the same key did. **A client never drops the key on
`applied: false`**: the flag changes what the visitor is told, never which key the retry uses. A
client that keeps its key on every 503 is safe either way; one told "nothing was applied" when
something was would pay again under a new key. The document declares `applied` on the four money
503s only, through the `MoneyServiceUnavailable` component, and the 503 on every operation
(`ServiceUnavailableResponseTransformer`, `PublishedErrorContractTests`).

**D8 — The idempotency bookkeeping has budgets of its own.** Storing an answer for replay and
releasing a claim on the error path get 3 s each, not the request's token: each writes one row, and
when that takes longer the database is failing. An answer not stored leaves the record `Executed`
and the 2xx is still sent; a claim not released stays `Processing`. An empty 2xx is never stored:
no monetary success is empty, so an empty one is an answer lost while it was written, and a retry
of its key gets `IN_FLIGHT`, then `RESULT_UNKNOWN`, never an empty replay. A replay and a stored
answer are written under the client's own token (ADR-0009's notes).

**D9 — A stuck claim is stale after 2 minutes, not 10.** A claim whose release failed while the
database was down holds its key at 409 `IDEMPOTENCY_IN_FLIGHT` until `ProcessingStaleAfter`. A
request now gives up at its deadline, lets no commit start after it and gives its release 3 s, so a
claim two minutes old belongs to no request still running; a paused process is covered by the
fence, since its commit carries the old `ClaimId` and aborts. The API refuses to start with a value
less than a minute longer than `RequestDeadline:Seconds` (ADR-0009's note).

**D10 — An audited save asks whether its commit landed before it retries.** It used to accept its
changes before the commit, so a transient fault as the commit started rolled the work back and the
execution strategy's re-run found nothing to save, committed an empty transaction and returned
success: the deposit's 201 with no ledger row above. The save now keeps its changes pending until
the strategy returns and runs through `ExecuteInTransaction`, which asks the database whether the
save's own audit row, a client-side id, is there before it decides (ADR-0044's note). The strategy
asks under the save's token, which the deadline cancels: a commit that fails turns the deadline
back on (D4), and past its instant the deadline fires at once, so the question is refused or cut
short. So when that token is cancelled after a commit was attempted, the save asks once more under
3 s of its own, as the bookkeeping does (D8), and a row found is a success. Measured with a PIN
change's acknowledgement lost after a 3 s deadline: 503 before, with the new PIN in place, and 200
now (`DepositCommitFaultSqlServerTests`).

**D11 — The BFF waits 55 s, on both roads, and answers the same 503 when it gives up.**
`BackendApi:TimeoutSeconds` is its own client's `HttpClient.Timeout` and, through
`BackendTimeoutConfigFilter`, every proxy cluster's activity timeout unless the cluster sets its
own. The BFF's own timeout, an API it cannot reach, a connection that breaks while a proxied body
is sent and a success it cannot read all answer the API's outage 503 (`SERVICE_UNAVAILABLE`,
`retryAfterSeconds` 10, `Retry-After`, `no-store`), never with `applied`, since only the API can
know, and the session is kept. Its own calls (sign-in, registration, re-authentication, the two
PIN calls and rename) forward the API's 503 with its `Retry-After` and `Cache-Control`, which used
to stop at the BFF. **The BFF never retries a visitor's request**: whether to send again is the
SPA's decision. No route gets YARP's `Timeout`: YARP 2.3 reports that one as the client cancelling
(<https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/HttpForwarder.cs>), a 400
or a 502 the response transform cannot tell from a browser that hung up.

**D12 — What is exempt, and why.** `POST /api/auth/refresh`, `/api/auth/revoke` and
`/api/auth/logout` carry `[NoRequestDeadline]` (ADR-0057 §4.3, §4.4): they run to completion once
started, and their commits are not gated. Refresh only reads for an active grant, and the audit row
of a refusal must not be taken back by a caller that hangs up; revoke and logout are short and
idempotent, and finishing one after the caller has gone only ends a session sooner. A refusal's
audit row everywhere else is written on its own scope with no token (`RecordRefusalAsync`, ADR-0044
D1). Health checks keep their own budget (`Audit:TailTimeoutSeconds`), and background services
have no request. At the BFF, five calls are bounded more tightly than 55 s on purpose and stay as
they were set: by ADR-0057 the renewal (30 s, detached), `/api/auth/revoke` (5 s, retried) and the
session-stamp poll (5 s); `/me`'s read-through (5 s, then the cached copy), which came with
ADR-0039; and the health probe (3 s, `BackendApiHealthCheck`). None writes anything a late answer
could lose.

## The numbers

The waits nest, innermost first: a command inside the request, the request inside the BFF's wait,
the BFF inside the ingress in front of it. `TimeoutChainTests` holds the chain against the settings
each host ships and the code defaults behind them.

EF's wait before retry *k*, for *k* = 0 to 3, is min((2^*k* − 1) × U[1, 1.1] s, cap), so the four
waits add up to **12.1 s** at most. On Azure's throttling numbers — 40613, "database not currently
available", the error a failover answers with, is one of them — EF adds 5 s to every wait: **32.1
s** (<https://github.com/dotnet/efcore/blob/v10.0.1/src/EFCore/Storage/ExecutionStrategy.cs>,
<https://github.com/dotnet/efcore/blob/v10.0.1/src/EFCore.SqlServer/SqlServerRetryingExecutionStrategy.cs>,
the version this repository ships).
The measured waits before this decision, 0, 1,013 and 3,257 ms, fit the formula.

| Layer | Value | Why |
|---|---|---|
| Connect timeout | 10 s | SqlClient bounds each login by it, and each BEGIN, COMMIT and ROLLBACK it sends as well (<https://github.com/dotnet/SqlClient/blob/v6.1.1/src/Microsoft.Data.SqlClient/netcore/src/Microsoft/Data/SqlClient/SqlInternalConnectionTds.cs>). 15 was SqlClient's |
| Connect retries, pool blocking | 0, NeverBlock | One retry layer (D2); and an outage that ends when the database is back, as on Azure (D1) |
| EF retries, back-off cap | 4, 10 s | 12.1 s of waiting, 32.1 s on the throttling numbers, which covers a failover: "Reconfigurations generally finish within 30 seconds" (<https://learn.microsoft.com/en-us/azure/azure-sql/database/planned-maintenance>). A longer one answers 503 |
| Command timeout | 30 s, unchanged | Plus up to 5 s for SQL Server to acknowledge the cancel, the attention (`AttentionTimeoutSeconds` in SqlClient's `TdsParserStateObject`): 35 s, which is where the measured −2s landed, at 35.0 s |
| **Request deadline** | **40 s** | Above 35, so a lone hung command answers with its own −2. Long enough for EF's 32.1 s of throttled waits and the attempts between them, so a failover can still succeed. And **40 + 5 (the attention) + 3 (the claim's release) + 5 (that release's attention) = 53 < 55**: every answer the API gives before a commit, `applied: false` included, reaches the visitor from the API. At 45 the sum is 58, and the BFF would turn a certain "nothing was changed" into "unknown" |
| Release and store budgets | 3 s each | One row on a key; waiting on a failing database helps no one (D8) |
| Max pool size | API 12, seeder 5 | 2 × 12 + 5 = 29 of Basic's 30 logins, under the first precondition |
| `retryAfterSeconds`, `Retry-After` | 10 | EF's cap: a client that comes back after it meets a database EF would have tried again by then. For machines: a visitor is told no time |
| BFF client and proxy activity timeout | 55 s | Above the 53 |
| A proxied request, worst case | 5 + 55 = 60 s | The session's renewal can spend 5 s before the request is forwarded; YARP restarts its activity timer after the request transforms. Under Azure Container Apps' ingress, "Request time out is 240 seconds" (<https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview>) |

When the API answers, by mode — reasoned from the numbers above; the runs against the real stack
that measure it are in the pull request:

| Outage | A read | A money write, before its commit | After its commit started |
|---|---|---|---|
| Azure refusal or failover (fails fast) | 503 once EF's retries are spent, 13 to 37 s, or at the 40 s deadline | within 40 s and a fast release | the commit fails fast and the deadline is back on: about 43 s |
| A hung database, new login | −2 in about 10 s (the login's connect timeout) | about 10 s and the release | n/a |
| A hung database, pooled connection | −2 at about 35 s, or cut at 40 + 5 | **53 s at most** | up to 40 + 10 (the commit, bounded by the connect timeout) + 5 (its attention, not measured) + 3 + 5 (the save's second question, D10, and its attention) + 3 + 5 (the release and its attention) = 71 s: the BFF's own 503 comes first, at 55 s, and says the same thing, "unknown" |

## What the deadline does to writes

The claim is its own committed INSERT; `Executed` is saved in the same commit as the money; a
started commit cannot be interrupted; and the work after a commit (writing the answer, storing it,
sending it) observes a token. Three ways to put a deadline on that were weighed:

- **Cancel everything:** rejected. A committed transfer could answer 503, "try again".
- **Never cancel a write:** rejected. It loses the bound exactly where a hung database holds
  connections longest.
- **Chosen:** reads and every wait before a commit are cancelled; the gate decides whether each
  commit may start; after that nothing is cancelled. ADR-0009's fence and its `Executed` flip are
  untouched, a refused commit rolls back through the catch it already had, and an unknown commit is
  ADR-0009's existing case, which the deadline cannot cause.

| What happened | The first answer | The same key, sent again |
|---|---|---|
| Failed before any commit, the request owning its claim | 503 `applied: false` | Released: it executes (an expired step-up authorisation, after its 2 minutes, asks for the PIN again first). Not released: 409 `IN_FLIGHT` until the claim is stale, then it executes. Never two debits |
| Failed before the request owned a claim (reading the key, claiming it, writing a replay) | 503 without `applied` | the replayed 201, 409 `IN_FLIGHT`, or it executes |
| A commit started and its outcome is unknown | 503 without `applied` | the replayed 201, `IN_FLIGHT` or `RESULT_UNKNOWN` |
| The BFF stopped waiting | the BFF's 503, without `applied` | as above |
| The client or the BFF hung up after the commit started | nothing: the client is gone | the whole 201, replayed |

### Residual risks

1. **A key held after a failed release.** The release's 3 s also loses to a 10 s pool wait when
   the pool is exhausted, and the key then answers 409 `IN_FLIGHT` until its claim is stale: two
   minutes (D9).
2. **A single statement outside a transaction can commit while a cancel races it:** a counted PIN
   or sign-in attempt, which fails closed; a claim, which is released or goes stale; an unused
   step-up authorisation, which expires in its 2 minutes; a new account, whose request still
   answers 503, so a visitor who tries again opens a second, empty one (opening an account carries
   no idempotency key). Money never moves outside a gated commit (D4).
3. **After a commit, the rest of the request is bounded only by EF's budget and the 3 s store.** If
   the database dies at that moment, the BFF's "unknown" arrives first (the last row of the table
   above).
4. **A frozen API cannot enforce its deadline.** Before this decision a queued sign-in wrote its
   grant 19.3 s after the visitor had been answered. Whether the client's token, which now reaches
   every call, stops such a request when the API resumes is not measured here.
5. **NeverBlock means every retry tries the server.** Microsoft's guidance is to keep `Auto` unless
   a measured retry design says otherwise, because turning the blocking period off can turn an
   outage into a storm of logins. This is meant to be that design, and the runs in the pull
   request measure it: on Azure, `Auto` already is NeverBlock, and the attempts are bounded by 4
   retries on a back-off, a pool of 12 and a 10 s connect timeout.
6. **An audited save's check can miss a commit that has not landed yet.** If the connection that
   sent the commit dropped and the commit is still being applied when the re-run asks for its audit
   row, the check reads "not there" and the save runs again. For a deposit the claim's fence stops
   the second commit: with the check forced to answer "not there" after a commit that landed,
   `DepositCommitFaultSqlServerTests`' lost-acknowledgement case answered 500 with one ledger row,
   never two.

## Rejected

- **ASP.NET Core's `RequestTimeouts` middleware.** It answers only when an
  `OperationCanceledException` reaches it, and a cancelled command surfaces as a `SqlException` or
  whatever wraps one; it clears its feature on the way out, so the handlers could not tell a
  deadline from a hang-up; and placed outside `UseExceptionHandler`, the handler's own
  short-circuit for a cancelled request swallows it
  (<https://github.com/dotnet/aspnetcore/blob/release/10.0/src/Middleware/Diagnostics/src/ExceptionHandler/ExceptionHandlerMiddlewareImpl.cs>).
  The gate also needs one atomic "refuse, or turn the deadline off", which a linked token cannot
  give.
- **Writing the limits into every connection string** (17 strings in about 14 files). It misses
  user-secrets and the Azure secret, and a string that carried them would silently override any
  later code default.
- **A 45 s deadline with the BFF at 60 s.** Every visitor waits 5 s longer, for more margin on a
  failover that 40 s already covers.
- **A BFF at 70 s,** to outlast the tails after a commit. It adds 15 s for everyone, for tails
  whose honest answer is "unknown" either way.
- **Answering 503 for a result that exists** because the deadline fired after the action returned.
  The result is true, and it is sent (D5).
- **A release budget sized by the time left before the BFF gives up.** It couples the API to the
  BFF's number, and it cannot beat an exhausted pool anyway.
- **Required tokens, with no default, on the service interfaces.** CA2016 as an error and
  `CancellationFlowTests` already make a missing token fail the build or the suite.
- **Rethrowing cancellations in registration.** After its commit the token cannot fire, and a
  rethrow would turn a created account into a 503.
- **Retrying −2.** Unchanged, for ADR-0034's reasons: a timeout means the work was blocked, and
  retrying it holds a connection for another 30 s.
- **Aligning the application lock's timeout with the tail's.** The lock's `THROW 50000` stays a
  500 while the tail's −2 is a 503; making them one answer is a separate decision.

## Consequences

**Positive**

- An outage answers a JSON 503 a client can act on, inside the BFF's wait, and every 503 is logged
  with the SQL error numbers that caused it. A retry is logged at Warning.
- A money request that did nothing can say so, and a request that cannot know never guesses.
- A request that is cancelled commits nothing afterwards, and a commit that started is answered.
- A deposit, a rename, a reveal or a PIN set whose commit fails transiently no longer answers
  success for work that never landed.
- A local or CI outage behaves like one on Azure, so a local measurement means something.

**Negative**

- A read during a long outage waits longer before the SPA gives up: the SPA retries a read's 503
  up to 3 attempts in all (ADR-0057 §8), and each can take up to about 45 s (the deadline plus the
  cancelled command's acknowledgement). The SPA does not read `applied` or `Retry-After` yet.
- A pool of 12 is a real ceiling. Twelve requests stuck on a hung database take every connection,
  and the thirteenth waits up to the connect timeout for one; refresh, revoke and logout, which are
  exempt, hold theirs to the end.
- A COMMIT slower than the 10 s connect timeout fails where it used to wait 15, and its outcome is
  unknown: a 503 without `applied`, then the replayed 201, `IN_FLIGHT` or `RESULT_UNKNOWN` on the
  same key. Commit latency measured on the deployed tier would change the 10.
- Two preconditions live outside the code, in this record.

**Neutral**

- `CustomWebApplicationFactory` applies the same connection defaults and the commit gate to every
  SQL Server test, so the suite runs on the pool of 12. Its own retry budget, when a test opts in,
  stays 3 under 5 s.
- The OpenAPI document, `schema.d.ts` and `apiSchemas.ts` declare the 503 on every operation, and
  the Schemathesis run waits 60 s per request instead of 30, so a stalled call shows the API's 503
  rather than the client's own timeout (`tests/contract/README.md`).

## Validation

- `ConnectionDefaultsTests` and `RetryBudgetTests` build `AddInfrastructure` itself: the four
  limits, a value the string sets winning under any of its names, the seeder's pool of 5, 4 retries
  under 10 s, and a retry logged at Warning.
- `RequestDeadlineTests` and `CommitGateInterceptorTests` drive the state machine on a fake clock:
  the deadline and a commit in either order, a failed commit turning the deadline back on, a
  client's hang-up after the gate cancelling nothing, a timer that fires after the request ended.
- `DatabaseUnavailableSqlServerTests` and `RequestDeadlineSqlServerTests` run on SQL Server: a
  missing database, a refused connection, an exhausted pool, a key lookup that fails transiently (a
  503 without `applied`), a read, a sign-in and a transfer held past the deadline (the transfer's
  503 says `applied: false`, balances unchanged, then the same key moves the money once), one gate
  entry per money success, a stuck response store, a client hanging up before and after the
  commit, and the exempt endpoints committing past the deadline.
- `ServiceUnavailableExceptionHandlerTests` pins the mapping, including 4060, 40613, 11001 and 1205
  to 503 and 2627 to 500; `CancellationFlowTests` the token flow; `DepositCommitFaultSqlServerTests`
  the audited save; `BackendTimeoutTests` the BFF; `TimeoutChainTests` the chain.

## What would change this

- **More than one replica of the app, or a larger database tier:** the pool of 12 is re-sized with
  it (the first precondition).
- **`Database:MaxRetryCount` above 4:** at 5 the unthrottled waits (22.1 s) still fit in 40 s, and
  the throttled ones (47.1 s) do not. Raise the deadline with it, and the BFF above the new sum.
- **SqlClient gaining an asynchronous commit that honours its token:** the gate still decides
  whether a commit starts; what a started commit does with a cancel is then to be measured again.
- **The API reached over a network** (ADR-0057's first precondition): the BFF's wait then includes
  a network, and 55 s is re-derived.

## Related

- [ADR-0009](0009-idempotency-monetary-operations.md) — the fence and the `Executed` flip the gate
  leaves untouched
- [ADR-0017](0017-pii-redaction-codeql-barrier.md) — why the 503's log line names the route
  pattern, not the path
- [ADR-0022](0022-client-money-mutation-protocol.md) — the client keeps its key on every 5xx
- [ADR-0034](0034-failed-family-revoke-recovery.md) — which SQL errors EF retries, and why −2 is
  not one of them
- [ADR-0044](0044-the-audit-trail-is-append-only-and-chained.md) — D1, the refusal rows the
  deadline never cancels, and the tail bound
- [ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) — the three exempt
  token endpoints and three of the BFF's five shorter waits
