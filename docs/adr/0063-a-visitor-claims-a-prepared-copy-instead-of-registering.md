# ADR-0063: A visitor claims a prepared copy instead of registering

**Status:** Accepted · **Date:** 2026-10-04 · **Amends**
[ADR-0062](0062-demo-visitors-get-private-copies-from-a-prepared-pool.md) (decision 2: what
`ClaimId`, `ClientKey` and `Writes` are for is decided here; decision 3: the API and the BFF bind
the demo's settings too; decision 13: `compose.demo.yaml` runs both with the demo on; and its
"not measured": a copy used through the front door was deleted by a run of the command),
[ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md) §4.2 (a sixth token
endpoint, and one more blocked proxied path),
[ADR-0055](0055-the-api-serves-one-client-the-bff.md) D4's note (the same list),
[ADR-0014](0014-recipient-lookup-enumeration.md) (on the demo the account a lookup's limit counts
by is a claimed copy) and
[ADR-0053](0053-the-committed-contract-is-what-the-api-generates.md) D6's note (the conformance
floor is 31 operations)

**Where the code is.** In the API: `AuthController.ClaimDemoCopy`; `DemoClaimService`;
`Security/DemoPasswordGenerator` and `Security/DemoClientKey`; `DemoEndpointMiddleware` and
`DemoWriteBudgetMiddleware`, with the three markers they read (`DemoOnly`, `ClosedInDemo`,
`CountedAsDemoWrite`, in `Attributes/`); the gate in `AuthService.LoginAsync`
(`DemoGateRefusalAsync`); `DemoEndpointResponsesTransformer`; `AddDemoOptions`. In the BFF:
`BffAuthController.ClaimDemoCopy`; `DemoModeMiddleware` and its two markers; `ClientAddress`;
`SpaHostingExtensions`; the blocked path in `AuthLevelMiddleware`. Shared: `DemoRefusalException`,
`RegistrationClosedException`, four codes in `ErrorCodes`, `DemoClaimRequest`,
`DemoClaimResponse`. `compose.demo.yaml` turns it on, on one machine. The comments this record made
false cite it as ADR-0063; the code's own remarks name the test that holds each rule.

## Context

ADR-0062 prepared the pool: private copies of the fixed demo, each with a demo user who has no
password, so nobody can sign in to a free copy. It left three columns for "a later record"
(`ClaimId`, `ClientKey`, `Writes`) and one question open: how a visitor gets a copy.

Until this change the app's two doors were sign-in and registration, and neither fits the demo.
Sign-in needs a password, and no copy has one. Registration creates a user outside every copy:
that user sees none of the demo's history, and every run of `recycle` exits 13 while one exists
(ADR-0062, decision 12). And it lets anybody create as many users as they care to.

What the demo needs: one request that hands a visitor a copy nobody else has, with a password
only that visitor is told; no way in to a copy that is free or over; no new users; and a bound on
what one visitor, one address and one copy can cost a database of 2 GB that never gives an audit
row back (ADR-0062, Consequences).

**What was read at the start of this work, and decided the shape:**

- `AuthController` is an `[ApiController]`, and so is the BFF's: the binder answers a malformed
  body 400 and a body that is not JSON 415 before an action's first line. A refusal written
  inside an action or a service therefore comes after the body was judged.
- `GET /api/accounts/{id}/full-number` writes an audit row on every call
  (`AccountService`, `AccountNumberRevealed`). Of the nine `_audit.Record(` sites in the API it is
  the only one on a safe method (`git grep -n "_audit.Record(" -- backend/src/AzureBank.Api`).
- An access token carries the user's id and no copy (`JwtService`: sub, email, azure_tag, jti,
  iat).
- The BFF's limiters live in the process's memory, and the replica that holds them stops about
  five minutes after its last request (ADR-0057 §4.6).
- The SPA words every 503 as the service being slow or down, and retries a read's once
  (ADR-0059).

## Decision

**1. On the demo a visitor claims a copy, and nobody registers.** `POST /bff/auth/demo/claim`
takes a free copy for the caller, gives its owner a password, opens a session as that owner and
answers with what signs in to the copy again. `POST /bff/auth/register` and the API's own
registration answer 403 `REGISTRATION_CLOSED`. Sign-in lets in the owner of a claimed copy whose
time is not over, and nobody else. All of it is behind `Demo:Enabled`, which is false unless a
deployment sets it (decision 14): with the flag off the claim's endpoints answer 404,
registration and sign-in are as they were, and the page is the built file byte for byte.

The claim writes no audit row and no `SecurityEvent` line, as sign-in and registration write
none: a plain log line and one counter record it (decision 15).

**2. One conditional statement decides who has a copy.** The API's claim,
`POST /api/auth/demo/claim`, marks a copy with
`UPDATE DemoCopies SET ClaimedAt, ClaimId, ClientKey … WHERE Id = @id AND ClaimedAt IS NULL`.
"One row changed" is the only proof that the copy is this request's. The read that came before
only proposes candidates. Why no copy is given twice:

1. Of any number of claims that reach the same free copy, the database lets one change the row;
   the others are told they changed none and try their next candidate.
2. A claim that fails after that statement rolls it back with everything else (decision 3): the
   copy is free again and its owner has no password.
3. Everything a claim makes up is made once, before the transaction, its `ClaimId` among it.
   When the answer to the commit is lost, the execution strategy asks whether the work landed,
   and the question is "does a row carry this claim's id". `ClaimId` is unique in the table, so
   no other request can have written it, and a claim that landed is answered, never run again on
   a second copy.
4. The password is written only where there is none
   (`… WHERE Id = @owner AND PasswordHash IS NULL`). A free copy whose owner already has one is
   not handed out: the claim logs the copy's id at Error, rolls back and answers 500, and the
   copy stays free until `recycle` removes it as too old.
5. `recycle` takes a free copy that is too old with the same conditional statement (ADR-0062,
   decision 8), so of a visitor and a run only one can have it.

That two updates of one row cannot both see it free is SQL Server's behaviour, and it was
measured, not assumed: eight claims at once on a pool of five, and twelve on a pool of twenty,
each with `READ_COMMITTED_SNAPSHOT` on and with it off (Validation).

**3. A claim is one transaction, and its statements run in an order that was measured.** Made
once, outside the transaction: the claim's id, the password and its hash (the slow part, spent
while no row is locked), the two stamps Identity keeps beside a password, the client's key, and
the instant, read from the host's `TimeProvider`. Then, through the execution strategy, eight
statements (recorded on LocalDB from one claim):

1. the client's claims in the last 24 hours (decision 7);
2. the candidates: the newest twenty free copies, by `Id` and `CreatedAt` only (decision 4);
3. the conditional `UPDATE` of decision 2, one candidate after another until one row changes;
4. the copy's `OwnerUserId`, by the copy's key;
5. the handles of the copy's two other users, by `DemoCopyId` and never by a handle;
6. the password: `PasswordHash`, `SecurityStamp` and `ConcurrencyStamp` in one statement;
7. the owner, by key, to mint the access token and read the session stamp;
8. the grant's `INSERT`.

- **The contacts are read before the password is written.** It is the one read that can touch
  another copy's users, since the database is free to answer it by reading every user. Run after
  the password's write, as it was first built, two claims at once each held their own
  owner's row and waited to read the other's. Measured on LocalDB with `READ_COMMITTED_SNAPSHOT`
  off, on a host that ran nothing again, with eight claims at once on a pool of five: three runs
  of three ended with claims answered 503 for error 1205, and 17 of the 18 deadlock reports those
  runs left in the server's `system_health` session show that statement on every side, each
  waiting for a key of `PK_AspNetUsers`. From statement 5 on a claim reads no user but its own
  owner, by key.
- **The candidates' read asks for `Id` and `CreatedAt` only,** which is all the index of free
  copies holds (`IX_DemoCopies_Free`), and the owner's id is read by key once the copy is this
  claim's. That costs one statement, and does not keep the read in the index (decision 5).
- **The password's write sets three columns, not two.** Identity writes back every column of a
  user it read earlier and checks only `ConcurrencyStamp`. Without the new stamp, a copy of the
  owner read while the copy was free and saved after the claim put the password hash back to
  null. No code path of the API does that to a free copy's owner today (read, not measured); the
  column's rule is Identity's, and a set-based write to a user changes it elsewhere in this code
  too (`RefreshTokenService`).
- **The grant is inside the transaction, as at sign-in and not as at registration,** where it is
  best effort. A claim with no grant would leave the visitor signed in for the life of one access
  token, fifteen minutes by default, on a copy that is spent.
  `IssueAsync` saves through the request's own context, so the grant commits with the claim or
  not at all. The grant's expiry is read from the wall clock and the claim's instant from the
  host's `TimeProvider`: under a test's clock the two differ, and on a deployment they are one
  clock.
- **Each attempt starts from nothing.** The strategy can run the delegate again (decision 5), so
  it clears what the context tracked first; what was made outside is written again unchanged.
- The contacts are sorted in memory, by code point, so the answer's order is the same on every
  database and collation.

**4. A visitor is given a fresh copy while one is left, and an old one before none.** A round
reads the newest twenty free copies, splits them at `Demo:Pool:MaxFreeAgeHours` (44 by default)
and tries the younger ones first; each group is shuffled with the cryptographic generator, so
claims that arrive together do not all try the same copy first and nobody predicts the order.
When every candidate of a round was taken by another claim, the claim reads again: three rounds.

- **429 `DEMO_POOL_EMPTY` means no free copy of any age,** which is what `recycle`'s exit 11
  means (ADR-0062, decision 12). A copy too old to count towards the pool's target is still
  handed out when nothing younger is free: an old history is better than none.
- A claim that lost every candidate of three rounds is answered the same 429, logged at Warning
  and counted apart (`lost_races`): the pool is being emptied faster than it is claimed from, not
  empty.

**5. Two claims that arrive together can deadlock while `READ_COMMITTED_SNAPSHOT` is off, and
the host's retry is the answer.** The read of free copies is answered from `IX_DemoCopies_Free`
and then looks each row up, because the filtered index does not hold `ClaimedAt`: it can hold a
shared lock on an index entry while it waits for a row that another claim's conditional `UPDATE`
holds, and that claim waits for the index entry. SQL Server ends the read with error 1205. On
LocalDB 17, with the setting turned off, three parallel loads run 50 times each met it three
times on a host with the retrying strategy (all 150 runs ended as they should). Run 50 times
each once more on a host without it, they met it twice, and each time one claim was answered
503.

The answer is the one the API already has: its execution strategy (`EnableRetryOnFailure`,
`Database:MaxRetryCount` 4 unless set) runs the claim again, 1205 is on the list EF retries, and
the claim is built to be run again. With `Database:MaxRetryCount` 0 the visitor would be answered
503 (read from `DatabaseOptions`; no host was run at 0).

With `READ_COMMITTED_SNAPSHOT` on the read waits for no row, and no run failed. That is the
setting of every database made for this application: Azure SQL's default, and what EF sets when
`migrate` creates a database on any other SQL Server (EF 10.0.1's generator of
`CREATE DATABASE`; measured on LocalDB, and on SQL Server 2022 in a container under compose:
`is_read_committed_snapshot_on` 1). It is off only in a database made some other way, or turned
off by hand.

**6. The password and the client's key come from the cryptographic generator and a keyed hash.**

- **The password** is sixteen characters in four groups joined by hyphens, in the shape of
  `Kp7m-Xw2R-hd9G-tQ4n`: a person reads it and may type it on another device. Its alphabet is 56
  characters, without the six that pass for one another (I, O, l, o, 0, 1). Each character is one
  `RandomNumberGenerator.GetInt32` over the alphabet, which is uniform; a generator whose next
  value follows from its last would hand one visitor the passwords of the next. Sixteen of 56 are
  92 bits (`python -c "from math import log2; print(16*log2(56))"` prints 92.9…). Sign-in holds a
  password to `ValidationRules.PasswordPattern`, so a draw without an upper-case letter, a
  lower-case letter or a digit is thrown away and drawn again, about one draw in twelve
  (`python -c "print((48/56)**16)"` prints 0.0848…, the share with no digit); that costs a
  fraction of one bit (computed; no test counts the redraws). The hyphens are the pattern's
  fourth kind of character.
- **The password exists in the claim's answer ~~and nowhere else~~.** *(Struck 2026-10-05:
  nowhere else on the server. Since the application's change the browser that made the claim
  keeps it too, under one `localStorage` key, and the dashboard shows it to the copy's owner
  when asked: "What the browser keeps in demo mode", points 1, 3 and 4.)* The database holds
  Identity's hash; no log line carries it (decision 15).
- **The client's key** is `HMAC-SHA256(Demo:ClientKeySecret, "demo-claim:" + address)`, 32 bytes,
  in `DemoCopies.ClientKey`. An address is personal data, and all the row needs of it is to tell
  one client from another. A plain hash would not do: there are few enough IPv4 addresses to hash
  every one, and a secret in the hash is what keeps that list from being made. The label says
  what the hash is for, so a second use of the secret would not give the same value. The key
  leaves with the copy (ADR-0062, decision 10). `DemoClientKey.Of` refuses a blank secret and a
  blank address in its own code: HMAC accepts an empty key, and what it gives then is a hash
  anybody can compute.

**7. Four caps, each with its number.**

| Cap | Number | Where it lives | What it does not stop |
|---|---|---|---|
| Claims of one client in a minute | 10 in a sliding 60 s, shared with sign-in, registration, re-authentication and the rename | The BFF's `auth` policy, on the claim's action | Anything spread over a day |
| Claims of one client in a day | `Demo:Claim:MaxPerClientPerDay`, 10 | SQL, inside the claim: the rows that carry this client's key and were claimed after the instant 24 hours ago | Several addresses. One address shared by many (an office, a proxy the BFF is not told to trust) is one client |
| Changes one copy can make | `Demo:Copy:MaxWrites`, 200 | `DemoCopies.Writes`, an API middleware (decision 11) | Reads, which the BFF's global limit of 300 a minute bounds |
| Copies handed out in a day | `Demo:Pool:MaxClaimsPerDay`, 150, with `Demo:Pool:TargetFree` (50) free at a time | `recycle`'s top-up (ADR-0062, decision 8); the claim reads neither | It is not a protection for visitors: reaching it is "all copies are in use" |

- **Why SQL for the daily cap.** A limiter in the BFF's memory is lost each time the replica
  stops, and the count has to span a day.
- **The same count as the pool's job.** `ClaimedAt > now - 24 h` and "refused at `cap` claims or
  more", the comparison `PoolCounts` makes for `clientsAtCap`, so the 429 a client reads and the
  job's line agree.
- **The cap can be overshot, by at most the minute's cap.** The count and the claim are two
  statements, so claims of one client that arrive together can each pass the count. Through the
  BFF no more than ten requests of one client pass the `auth` policy in a minute, so a client
  with nine counted claims can end with nineteen (arithmetic: 9 + 10; not run).
- **`retryAfterSeconds` is the time until the client is back under its cap,** not until its
  oldest claim leaves the 24 hours: after an overshoot those differ, and the earlier instant
  would send the visitor back to a second 429. It is never under 1, and it is in the `Retry-After`
  header too.
- **What the numbers multiply to** (arithmetic, not a measurement): one client at its cap, 10
  copies of 200 changes, is 2,000 counted requests a day; the pool's ceiling, 150 copies of 200,
  is 30,000. Without the cap on a copy the bound on one address was the global limit alone, 300
  requests a minute, which is 432,000 a day (300 x 60 x 24).
- **No cap on starting over.** A visitor who claims again spends one of the client's ten.

**8. A refusal of the demo is 429 with a code of its own, never 503.** `DEMO_POOL_EMPTY`,
`DEMO_DAILY_LIMIT` and `DEMO_COPY_LIMIT`, each with one sentence a visitor can read. A 503 is the
outage's answer (ADR-0058), which the SPA words as the service being slow or down and retries on
a read (ADR-0059); here the service is up, and asking again at once changes nothing. Only the
daily limit ends at an instant that can be computed, so only it carries `retryAfterSeconds`.
`REGISTRATION_CLOSED` is 403: it is not a matter of waiting.

**9. The two answers that depend on the flag come before the request's body is looked at.** The
claim is 404 while the demo is off, and registration is 403 while it is on, whatever the request
carries: a malformed body is not answered 400, nor a form post 415, each of which would tell a
caller what a request should look like on a deployment that takes none. So neither is decided in
the action or the service. Each host has a middleware that reads a marker from the endpoint's
metadata, the mechanism the token road already uses (`TokenEndpointAttribute`):

- **In the API,** `UseDemoEndpoints()` runs after `UseTokenRoad()` and before authentication. A
  caller off the token road was answered 404 before it, for the claim and for registration
  alike. The claim's 404 is left empty for the status-code pages to fill, so it is the answer a
  path with no route gets, and the document declares it as `application/problem+json` (a small
  operation transformer, read from the same marker). Registration's 403 is thrown as
  `RegistrationClosedException` and written by the exception handler, with the members every
  refusal has.
- **In the BFF,** `UseDemoMode()` runs after the rate limiter: a closed door is still a door
  somebody can hammer, so a request to it spends the `auth` policy's allowance like any other.
  The BFF's 403 has the API's code and sentence and is written without calling the API;
  `instance` is the path the browser asked for.
- **`DemoClaimService` refuses in its own code too** while the demo is off
  (`InvalidOperationException`), as the pool's builder and recycler do (ADR-0062, decision 3).
  The pipeline never reaches it then.
- **The two flags are two settings,** one per container. BFF on and API off: the API answers the
  claim 404 and the browser gets that 404 with the API's problem body. BFF off: the BFF's own
  404, with no body, as for a path it does not have.

**10. Sign-in is gated on the demo.** In `AuthService.LoginAsync`, after the user is found and
before the password is looked at, a user is answered exactly as an email nobody has, for one of
four reasons:

| Reason | Who |
|---|---|
| `OutsideEveryCopy` | A user that belongs to no copy |
| `NoPassword` | A user of a copy that has no password: a copy's two contacts, a free copy's owner |
| `CopyNotClaimed` | A user with a password whose copy nobody has claimed |
| `CopyEnded` | A user whose copy was claimed `Demo:CopyLifetimeHours` ago or more |

- "Exactly" is: the same cost spent (`SpendVerifyCost`), the same 401 `INVALID_CREDENTIALS`, no
  password check, and no failed attempt counted, so nothing a visitor cannot sign in as can be
  locked. The reason is logged at Information with the user's id, never the address, and the
  sign-in is counted as failed.
- **The gate costs one read more than an unknown email,** the pool's row by its key, and only for
  a user who has a copy and a password. So a refusal for `CopyNotClaimed` or `CopyEnded` takes
  that read longer; how much was not measured. It stands beside the residual ADR-0012 already
  records for a known account.
- **A copy's end is not a column.** It is `ClaimedAt` plus the lifetime, on the clock the claim
  was stamped with, and the copy is over from that instant on. The gate is what keeps a new
  session from being opened in a copy whose time is over, which `recycle`'s backstop only
  reports (ADR-0062, decision 9: a `hardStop` above 0).
- **Renewal is not gated.** A session opened before a copy's end runs to its grant's end:
  `Jwt:RefreshTokenLifetimeMinutes`, 60 minutes by default and 24 hours at most. `recycle`
  leaves the copy alone until then (ADR-0062, decision 9).
- Re-authentication goes through sign-in, so it is gated too (read: the BFF's
  re-authentication calls the API's sign-in; no test of the demo re-authenticates).
- With the demo off the gate reads nothing and refuses nobody.

**11. A copy can make `Demo:Copy:MaxWrites` changes, and the request past that is 429
`DEMO_COPY_LIMIT`.** A middleware after authentication and authorisation, before idempotency.

- **What is counted:** a request of a signed-in user that reached an endpoint and whose method is
  not GET, HEAD, OPTIONS or TRACE; and the reveal of an account number, a GET that writes an audit
  row (`[CountedAsDemoWrite]`). Audit rows are what `recycle` never gives back, so a cap that left
  the reveal out would not bound a copy.
- **It counts requests, not changes.** A request is counted before it is looked at, so one the API
  then refuses has spent one all the same: a wrong PIN, a deposit with no idempotency key, a
  method the path does not take (405). So has a retry answered from the idempotency store. Each
  errs toward the cap; none lets a change through uncounted.
- **What is not:** a request from nobody; one that matched no endpoint; every request while the
  demo is off; and the token endpoints. The one a signed-in user makes is
  `POST /api/auth/logout`, which signs the user out of every session, ADR-0057's lever for one
  user, and no cap of the demo's may refuse that.
- **One statement, with its condition inside:** "add one to the caller's copy if it is under its
  limit and not a record". Nothing reads the count and writes it back, so of changes that arrive
  together exactly as many go on as the copy has left. The statement finds the copy through the
  caller, since a token carries no copy.
- **Its own commit.** It runs outside any transaction, before the idempotency claim, and is not
  given back when the request fails. The retrying strategy can send it twice after a lost
  answer (read; no test loses that answer): that too errs toward the cap.
- **No copy, no budget.** A caller who belongs to no copy, or to the record of a deleted one, is
  refused as a copy at its limit is.
- `DEMO_COPY_LIMIT` is named on no operation of the contract. Of the sixteen that can answer
  it (the fifteen of a signed-in user that are not a GET, logout apart, and the reveal; counted
  from `docs/api/openapiv1.json`), seven declare a 429 for another refusal and nine declare
  none. No test of the document asks an operation to declare a 429 it can answer: the one
  that names a 429 pins the claim's own list of answers, and the conformance run, which
  reports a status an operation answers and does not declare, starts the API with the demo
  off, where the budget answers nothing. Declaring the budget's 429 on the sixteen would say
  of every deployment what is true of the demo.

**12. The BFF's door.** `POST /bff/auth/demo/claim`, `[EnableRateLimiting(Auth)]`, `[DemoOnly]`.

- **The body is an empty JSON object.** The claim needs nothing from the browser, and a member
  would be a place to name an address. It is still a body and has to be JSON, because a page of
  another site can make a browser post a form or plain text without asking first and cannot make
  it send JSON: the binder answers 415 to a form, to plain text and to no body, and 400 to JSON
  that is not an object, before the action runs. From a browser that sends `Sec-Fetch-Site`, the
  Fetch-Metadata rule refuses a cross-site or same-site claim with 403 before the body is looked
  at (ADR-0018).
- **The BFF names the client, never the browser.** It sends the API
  `{ "clientAddress": ClientAddress.Of(connection) }`, the key its own limiters count the request
  under: an IPv4 address in full, an IPv6 address as its /64, `unknown` for a connection with no
  address, and behind a proxy the proxy's address unless `ForwardedHeaders:KnownProxies` names
  it. One method for both, so a limiter and the daily cap cannot count one visitor as two.
- **The answer is the house envelope,** as at every door of the BFF, and the application's next
  change is written against it:

  | Member | What it is |
  |---|---|
  | `data.user` | `id`, `email`, `firstName`, `lastName`, `azureTag`, `hasPin`: the copy's owner, as sign-in answers a user |
  | `data.expiresAt` | When the access token expires, as sign-in's. Not the copy's end |
  | `data.copy.email`, `data.copy.password` | What signs in to the copy again. The password is in this answer only |
  | `data.copy.pin` | The copy's PIN, `123456` for every copy (ADR-0062, decision 4) |
  | `data.copy.contacts` | The handles of the copy's two other users, sorted: whom the owner can pay |
  | `data.copy.expiresAt` | The copy's end: the claim's instant plus `Demo:CopyLifetimeHours` |
  | `message` | `Demo copy claimed` |

  With it: the session cookie, `Cache-Control: no-store` and `Pragma: no-cache`. No token is in
  the body.
- **The session is opened as sign-in opens it:** the token, the grant, the grant's expiry as the
  session's cap, and the user's session stamp, as the API answered them, at auth level 1. The new
  cookie is set first, and only then is the session the request's cookie named ended and its
  grant queued for revocation (ADR-0026's order). A claim the API refuses never gets that far, so
  it leaves the visitor in the session they had.
- **Starting over is the same request with a session cookie.** It gives another copy and ends the
  session on the first. The first copy stays claimed, and ends at its time.
- **Every refusal of the API is forwarded as the API wrote it,** with its `Retry-After` when it
  named a wait, and its own `instance`. An API that does not answer is the outage 503 of
  ADR-0058, and opens nothing.
- **`/api/auth/demo/claim` is not proxied.** It joins the BFF's blocked proxied paths, for
  sign-in's reason (its answer carries the tokens the BFF exists to withhold) and one of its own:
  the API counts a client by the address its caller names, and the caller is meant to be the BFF.
  Blocked with the demo off as with it on.

**13. The page says it is the demo with one tag, put in when the host starts.** With the demo on,
the BFF reads the built `index.html` once and serves it with
`<meta name="azurebank-demo" content="true">` right before its first `</head>`, as
`text/html; charset=utf-8` with `no-cache`: for a navigation, and for `index.html` asked for by
its name. The application is one build for every deployment, and the tag lets it learn before
its first request that this one is the demo, with no round trip and no state in which it does
not know.

- **The tag goes into the file's bytes, not its text:** nothing is decoded, so the page is the
  file's bytes and the tag.
- **A shell with no `</head>` stops the host,** as a `Spa:RootPath` with no `index.html` does. A
  page served without the tag would look, to the application and to whoever reads the log, like
  a deployment with the demo off.
- **With the demo off the page is the file,** and has no such tag.
- **`index.html` by its name** is answered with the tagged page in any case and behind any
  number of slashes and backslashes, the spellings the static files would answer with the file's
  own bytes. The comparison is of texts and asks the file system nothing ("What a visitor can
  still do" has what that leaves).
- **In the Vite development loop the BFF serves no page,** ~~so nothing carries the tag there. How
  the demo's screens are seen in that loop belongs to the change that builds them.~~ *(Struck
  2026-10-05: that change is in. The dev server adds the same tag itself when its environment
  holds `AZUREBANK_DEMO=true`, and only then, and a build never carries it
  (`frontend/vite.config.ts`); measured that day, `docs/engineering-practices.md` has the
  commands. "What the browser keeps in demo mode", below, is that change's record.)*

**14. The settings, and where the demo is on.**

| Host | Reads | At start |
|---|---|---|
| API | `Demo:Enabled`, `Demo:CopyLifetimeHours`, `Demo:Claim:MaxPerClientPerDay`, `Demo:Copy:MaxWrites`, `Demo:Pool:MaxFreeAgeHours`, `Demo:ClientKeySecret` | `DemoOptionsValidator`'s ranges; and with the demo on, a `Demo:ClientKeySecret` of 32 characters or more |
| BFF | `Demo:Enabled` | `DemoOptionsValidator`'s ranges. It holds no key of the demo's |
| Seeder (`seed-pool`, `recycle`) | As ADR-0062 has it; of the claim's numbers, `Demo:Claim:MaxPerClientPerDay`, for `clientsAtCap` | As ADR-0062 has it |

- **Either host refuses to start on any `Demo:*` value out of range,** one it never reads and
  with the demo off included: both register the shared validator, which checks every range
  whatever the flag (ADR-0062, decision 3).
- **The secret's rule is the API's own,** not the shared validator's: the Seeder runs that
  validator and holds no secret, so the rule there would stop `seed-pool` and `recycle`.
- **A lifetime under 24 hours is allowed.** The range, 1 to 168, is ADR-0062's, and a short
  lifetime is what a trial on one machine uses. What it costs the daily cap is under "What a
  visitor can still do".
- **A number of the demo is given to every service that reads it.** `Demo:CopyLifetimeHours` to
  the API, which ends a copy by it, and to `recycle`, which deletes by it;
  `Demo:Claim:MaxPerClientPerDay` to the API, which refuses a client by it, and to the pool's two
  commands, whose line counts the clients at their cap by it. `Demo:Pool:MaxFreeAgeHours` only
  orders the claim's candidates in the API; a different value there changes which free copy is
  preferred and nothing else.
- **`compose.demo.yaml`** runs the API and the BFF with the flag on and asks for a ninth
  variable, `DEMO_CLIENT_KEY_SECRET`, given to the API alone. Compose asks for it whichever
  service is named, so the pool's two commands and `down` need it too.
- **On Azure the demo is still off.** This change touches no file under `infra/`
  (`git grep -n "Demo__" -- infra` prints nothing): after it the deployment of ADR-0061 answers
  the claim 404, its registration is open and its page is the built file. The change that adds
  the pool's job (ADR-0062, decision 13) is the one that turns the demo on, and it has to set:
  `Demo__Enabled=true` on the `api` and the `bff` containers as on the job; `Demo__ClientKeySecret`
  on `api`, an eighth application secret where ADR-0061's decision 9 counts seven; and, until what
  the BFF sees as a visitor's address behind the ingress has been measured,
  `Demo__Claim__MaxPerClientPerDay=1000` on `api` and on the job. No file under `infra/` sets
  `ForwardedHeaders:KnownProxies` (`git grep -n "KnownProxies" -- infra` prints nothing), so the
  BFF may see the ingress as every visitor's address, and the default of 10 would then be ten
  copies a day for everybody. 1,000 is the range's maximum. A number near the pool's size would
  not do: the count is a rolling 24 hours and is asked before the pool is.

**15. What is logged and counted.** Plain log lines, no `SecurityEvent` line and no audit row, so
ADR-0044's inventory and its pinned counts do not move:

- `Demo copy {CopyId} claimed for user {UserId}`, Information, written after the commit;
- `A demo claim lost every candidate in {Attempts} rounds`, Warning;
- `Demo copy {CopyId} is free and its owner has a password; the claim was rolled back`, Error;
- `Sign-in refused by the demo gate for user {UserId} ({Reason})`, Information;
- in the BFF, `User {UserId} claimed a demo copy via BFF`, Information;
- the counter `azurebank.demo.claims`, tagged `azurebank.outcome`: `claimed`, `pool_empty`,
  `daily_limit`, `lost_races`.

No line carries a copy's password, its email, an access token or a grant. A test asserts it
on each host's log, reading every event whole (its message, its properties, its exception):
`AClaim_WritesNoLogEventThatHoldsThePasswordTheEmailTheAccessTokenOrTheGrant` on the API's,
the host that mints the token and the grant, and
`AClaim_WritesNoLogLineThatHoldsThePasswordTheEmailOrAToken` on the BFF's, against the values
its stand-in API answers. The logs of the compose runs were searched for every email the runs
sent or were answered, every password, every whole cookie value and the stack's nine secret
values (Validation), and not for a token or a grant: through the BFF a caller is given
neither to search for. Counted afterwards in the logs kept from those runs: no text of a
JWT's shape, and no run of 40 or more base64url characters but two event names (a grant is 43
such characters). The limiter's own rejection line names the client's address as its
partition, as it did before; that no other line carries the address is not asserted.

**16. The claim is the first token endpoint that opens a session from no credential, and it
rests on the API being on loopback.** Sign-in asks for a password, renewal for a grant; the claim
asks for nothing but an address, and takes that address on its caller's word. Both are safe only
because the API answers its token endpoints to the BFF's own client alone: over loopback, with
the service key and exactly one token-road marker (ADR-0057 §4.2, ADR-0055). A caller that
reaches the API itself with the key and the marker can claim copies without the BFF's limit and
name any address, so the daily cap does not bind it. That is ADR-0057's first precondition, and
this record adds nothing beside it: **if the API is ever reached over a network, the claim's
caps are worth nothing until that is answered.**

## What a visitor can still do

- **Hold several copies.** Each claim gives another copy; the earlier ones stay claimed until
  their time is over. The bound is the client's daily cap.
- **Stay in a copy past its end,** in a session opened before it: until the session's grant ends,
  60 minutes after sign-in by default and 24 hours at most
  (`Jwt:RefreshTokenLifetimeMinutes`), and no longer than the BFF's own caps allow.
- **Be given an old copy.** There is no bound on how old a free copy is when it is claimed: one
  older than `Demo:Pool:MaxFreeAgeHours` is handed out when nothing younger is free, and it opens
  on a history that ends that long ago (ADR-0062, Consequences).
- **Claim past the daily cap,** by the overshoot of decision 7; by coming from several addresses;
  and where `Demo:CopyLifetimeHours` is under 24, because a copy deleted inside the 24 hours
  takes its client key with it and stops counting (ADR-0062, Consequences).
- **Share a cap with strangers.** Visitors behind one address are one client, to the daily cap as
  to the `auth` limit. Under `compose.demo.yaml` every browser on the machine was one client when
  it was measured, on Docker Desktop for Windows: the BFF saw the compose network's gateway
  address for every request through the published port (Validation).
- **Learn that this build has the claim, though not whether the demo is on.** Three answers of
  the BFF tell a caller with no key and no session that the endpoint is there, with the demo
  off as with it on, and each is pinned by a test and left as it is:
  1. another method on the claim's path, or on registration's, is 405 with `Allow: POST`, as on
     sign-in's path, where a path with no route is 404. The marker hides the endpoint, not its
     path. The API answers the same 405, but only to a caller that holds the service key: one
     without it is answered 401 on these paths under any method, as on every operation;
  2. with the demo off, the BFF's claim path still spends the `auth` limit, so past it the answer
     is 429 `RATE_LIMIT_EXCEEDED` where a path the BFF does not have is still 404;
  3. with no session, the proxied `/api/auth/demo/claim` is 404 with no body, as sign-in's
     proxied path is, where a path under `/api` that names nothing is 401.

  The repository and its published contract are public, so none tells anybody what the document
  does not. What says whether the demo is on is a POST to the claim: 404 or not.
- **Spend a copy's budget on requests that change nothing:** a refused request is counted, and so
  is a method a path does not take when it is not a safe one (decision 11).
- **Read "limit of changes" where the copy is gone.** A caller whose copy was deleted, or who
  belongs to none, is refused by the budget with the sentence of a copy at its limit. On the demo
  a session is opened only for a claimed copy's owner, so this takes a copy deleted under a live
  token, which `recycle` does only at its backstop, or a session opened before the API was given
  the flag.
- **On Windows, read the page without its tag under four spellings of its file's name.** With
  the demo on, `/assets/..%5Cindex.html`, `/assets%5C..%5Cindex.html`, `/.%5Cindex.html` and
  `/INDEX~1.HTM` answer the file's own bytes: names only the file system resolves (a step back
  written with a backslash; the 8.3 short name), which a comparison of texts cannot list. On the
  Linux image each answered 404. A page without the tag reads as "the demo is off" to the
  application, and gives a visitor nothing they could not ask the BFF for directly.

## Rejected

- **The two flag-dependent answers inside the action or the service.** The binder answers a
  malformed body first, so neither "404 whatever is sent" nor "403 whatever is sent" would be
  true.
- **404 for every method on a demo-only path while the demo is off.** It would need the
  middleware to read route patterns as well as endpoints, a second mechanism, to hide what the
  public contract states. The 405 does not tell whether the demo is on.
- **A `SecurityEvent` line or an audit row for the claim, the gate or the closed registration.**
  Sign-in and registration write neither, and an audit row for every claim is a row the demo's
  database never gives back. A new `SecurityEvent` line would also move ADR-0044's inventory and
  the counts `SecurityEventConstantTests` pins.
- **A cap on starting over, per session.** Parallel requests and a sign-out both pass it; the
  daily cap is the control.
- **A plain hash of the address.** Undone by hashing every IPv4 address.
- **The address read from the browser's request.** A browser could name any.
- **A bare `{ user, expiresAt, copy }` answer from the BFF.** Every other door answers the
  envelope, and the application reads one shape everywhere.
- **Serving a shell with no `</head>` untagged.** It looks like a deployment with the demo off,
  with nothing in the log.
- **A `GET /bff/config` for the flag.** A round trip before the first screen, and a state in
  which the application does not know yet.
- **Refusing a `Demo:CopyLifetimeHours` under 24 in the API,** and **lowering the floor of
  `Demo:Copy:MaxWrites` for the tests.** Both ranges are ADR-0062's; the tests use 10.
- **503 for an empty pool or a cap.** Decision 8.
- **Asking the file system what a path resolves to,** to hold the four Windows spellings: one
  file-system call on every GET of the demo, a test whose expectation depends on the system, and
  the short name would still pass.

## Consequences

**Positive**

- A visitor gets a private demo with one request and no form, and the next visitor gets another.
- No password of any copy is in the repository, a log or a second answer.
- With the demo on, nothing creates a user outside the pool, so `recycle`'s exit 13 stops being
  something a visitor can cause.
- What one client, one copy and one day can write is bounded by numbers a deployment sets.
- With the demo off a visitor meets what they met before: registration and sign-in answer as
  they did, the page is the same bytes, and a sign-in sends no statement about the pool. What a
  deployment with the demo off does gain is the three answers under "What a visitor can still
  do", and a start that refuses a `Demo:*` value out of range.

**Negative**

- **A claim whose answer is lost holds a copy nobody has.** If the commit lands and the answer
  does not reach the browser (the BFF gave up waiting, the visitor left), the copy is claimed and
  its password was told to nobody. It counts towards the client's cap, and stays claimed until
  `recycle` deletes it at its time. Read from the code; no run lost that answer.
- **A free copy whose owner has a password is never handed out,** and every claim that picks it
  is answered 500, until `recycle` removes it as too old. No command leaves such a copy; a write
  by hand would.
- **The daily cap is per address as the BFF sees it.** Behind an ingress the BFF is not told to
  trust it is one cap for everybody (decision 14).
- **The budget counts requests that changed nothing** (decision 11), and the contract names
  `DEMO_COPY_LIMIT` on no operation: nine of the sixteen that can answer it declare no 429.
- **A deployment that turns the demo on needs the two flags and the secret together.** A flag on
  one of the app's containers only fails closed: on the BFF alone the claim is the API's 404 and
  registration is closed; on the API alone the page has no tag, the BFF's claim is 404 and the
  API refuses the registration the BFF forwards (that case read, not run through both hosts).
  With the flag on the job and on neither container, as
  `compose.demo.yaml` ran until this change and as Azure would after a job that sets it for
  itself alone, visitors register beside the pool and every `recycle` exits 13.
- ~~**`compose.demo.yaml` starts a stack whose page has no screen for the claim yet.** Until the
  application's change lands, a copy is claimed there by sending the request by hand.~~ *(Struck
  2026-10-05: the application's change is in. The sign-in page of that stack leads with "Try the
  demo", which sends the claim; measured that day in a browser, under "What the browser keeps in
  demo mode".)*
- **Under compose, and on any deployment where the BFF sees one address,** ten claims in a
  rolling 24 hours are all the stack hands out (decision 7's overshoot apart), unless the cap is
  raised on every service that reads it.
- **The claim's retry is not free.** A claim that loses a deadlock waits for the server's search
  for the cycle before it is run again: in the runs that staged one at the read (Validation),
  the claim was answered 3.2 to 3.8 s after it was sent; in the runs that staged one at the
  conditional `UPDATE`, with `READ_COMMITTED_SNAPSHOT` on, 1.7 to 5.3 s.
- The conformance run in CI starts the API with the demo off, so the claim answers its declared
  404 to every generated request and registration never answers the 403 it declares: neither is
  exercised there.

**Neutral**

- `ClientAddress.Of` is the limiters' old local function, moved so that the claim can call it.
- `AuthService.ToTokenResponse` is `internal static`: sign-in, registration and the claim build
  the token object in one place.
- The contract has 31 operations on 28 paths. The claim added four schemas; registration gained
  a 403.
- Seen while this was measured, older than this change and left as it is: a doubled slash in
  front of a server path (`//api/accounts`, `//bff/nope`) answers the page shell, with the demo
  off as with it on; and the BFF's log names the first 8 characters of an ended session's id.

## Validation

The tests are in `backend/tests/AzureBank.Tests` unless a line says `AzureBank.Bff.Tests`.

**The claim** (`DemoClaimSqlServerTests`, on SQL Server; `DemoModeEndpointTests` on the in-memory
host):

- `OnAPoolOfOne_TheFirstClaimIs200_AndTheSecond429PoolEmpty`;
  `TheAnsweredPassword_SignsIn_TheStampIsTheOwners_AndTheGrantEndsSixtyMinutesAfterTheClaim`;
  `ClaimedAt_AndTheCopysEnd_ComeFromTheHostsClock_InUtc`;
  `ACopysEnd_FollowsTheLifetimeTheHostIsGiven`.
- **No copy twice:**
  `EightParallelClaims_OnAPoolOfFive_GiveFiveDifferentCopies_AndThree429s` and
  `TwelveParallelClaims_OnAPoolOfTwenty_AllGetDifferentCopies`, each under both settings of
  `READ_COMMITTED_SNAPSHOT`, set by the test and read back before it claims;
  `AClaimWhoseOnlyCandidateIsTakenFirst_Is429_AndChangesNothingElse`;
  `AClaimWhoseCandidateIsTakenFirst_TakesAnotherCopy`;
  `AClaimThatLosesEveryCandidateOfThreeRounds_Is429PoolEmpty_AndStopsThere`.
- **All or nothing:** `AGrantThatCannotBeWritten_LeavesTheCopyFree_AndWithoutAPassword`;
  `AFreeCopyWhoseOwnerAlreadyHasAPassword_Is500_AndStaysFree`;
  `ACommitWhoseAnswerIsLost_ClaimsOneCopy_AndAnswersForIt` (answered 200 in every run);
  `AClaimRunAgainAfterATransientFault_ClaimsOneCopy_AndWritesOneGrant`;
  `AWholeRowWriteOfAnOwnerReadBeforeTheClaim_IsRefused_AndThePasswordStillSignsIn` (the third
  column of decision 3).
- **The order of decision 3:** `OnceAClaimHasWrittenAUser_ItReadsNoUserButItsOwnerByKey` and
  `TheCandidatesRead_AsksOnlyForWhatTheIndexOfFreeCopiesHolds`, on one claim's recorded
  statements. The theories of claims at once do not hold the order: without row versioning they
  run on a host that runs a refused claim again, and with the contacts' read moved back after
  the grant they ended as they should all the same (three runs of three).
- **The retry of decision 5:**
  `AClaimWhoseReadOfCandidatesIsRefusedOnce_IsRunAgain_AndClaimsOneCopy` and its control
  `OnAHostThatRunsNothingAgain_TheSameRefusalIs503_AndNothingIsClaimed`. The read is refused with
  a `SqlException` carrying error 1205, class 13, built by the test and thrown before the
  statement is sent: it is the error a deadlock's victim is given, not a deadlock. A real one
  arrives while the results are read. It was met and run again in the measurements below, and no
  committed test makes one happen.
- **Which copy:** `AFreshCopyIsHandedOutBeforeOneTooOldToCount_AndAnOldOneBeforeNone`;
  `HowOldAFreeCopyStillGoesFirst_IsWhatTheHostIsGiven`;
  `WithMoreFreeCopiesThanARoundReads_TheFreshOneIsStillHandedOutFirst`;
  `TheFreshCandidatesAreTriedFirst_AndEachGroupInAnOrderThatIsNotTheReads`.
- **The daily cap:**
  `PastItsCap_AClientIs429DailyLimit_WithRetryAfter_AndAnotherAddressStillClaims`;
  `TheDailyLimitsRetryAfter_IsWhenTheClientIsBackUnderItsCap`;
  `AClaimOlderThan24Hours_IsNotCounted_AndAnotherClientsNeverIs`;
  `WithTheCapAt1000_FiftyEarlierClaimsOfOneClient_DoNotStopTheNext`. No test overshoots the cap
  with claims at once.
- **The flag off:** `WithTheDemoOff_TheClaimLooksLikeAPathThatDoesNotExist`,
  `WithTheDemoOff_AMalformedClaim_IsStill404`,
  `WithTheDemoOff_TheClaimsOwnCodeRefusesToo_AndTakesNoCopy`; and off the road,
  `WithTheDemoOn_AClaimFromOffTheTokenRoad_Is404_AndTakesNothing`.
- **The log:** `AClaim_WritesNoLogEventThatHoldsThePasswordTheEmailTheAccessTokenOrTheGrant`,
  on the in-memory host with the log captured from the lowest level: no event holds any of
  the four in its message, its properties or its exception, and the claim's own event is
  among those read. On SQL Server, `OnAPoolOfOne_TheFirstClaimIs200_AndTheSecond429PoolEmpty`
  asserts the claim's line and that no line at Information or above holds the copy's email or
  its password.
- **The password and the key** (`DemoClaimPartsTests`): a thousand passwords match the pattern,
  pass sign-in's check and Identity's validators, differ, and use every one of the 56 characters
  and no other; each of the three redraw conditions with a chosen draw; the known answer of the
  key (`TheKey_IsHmacSha256_OfTheLabelledAddress_UnderTheSecret`, the same value from Python's
  `hmac` and from `openssl dgst`). `DemoCopyBoundaryTests`: the claim's code never uses
  `System.Random`, and the generator is handed `RandomNumberGenerator.GetInt32` itself. No test
  measures the distribution of what the generator answers.

**Two claims at once, measured on LocalDB 17.0.4025 with `READ_COMMITTED_SNAPSHOT` turned off**
(deadlocks counted from `xml_deadlock_report` in the server's `system_health` session):

| Host | Runs | Results | Deadlocks | Outcome |
|---|---|---|---|---|
| retrying strategy on; three loads (eight claims on a pool of five, twelve on twenty, six claims beside fourteen other requests on eight) | 50 of each | 150 | 3: none among the eight, one among the twelve, two in the third | all 150 as they should |
| strategy off; the same three loads | 50 of each | 150 | 2: one among the twelve, one in the third | 148; each deadlock left one claim answered 503 |
| the two theories as committed | 40 | 160 (80 with the setting on, on a host that runs nothing again) | 3, each run again | 160 passed |
| a deadlock staged at the read, strategy on | 6 | 6 | 6 | 200, six of six; one claimed row, one grant |
| the same, strategy off | 5 | 5 | 5 | 503, five of five; nothing claimed |

Each deadlock is the shape of decision 5: the read holds a shared lock on a key of
`IX_DemoCopies_Free` and wants one on `PK_DemoCopies`; the conditional `UPDATE` holds that key
and wants the index key. The read's plan, from the server's cache: an index scan of
`IX_DemoCopies_Free` and a key lookup in `PK_DemoCopies` whose predicate is `ClaimedAt IS NULL`.
With the setting off the read, sent beside a session that held one free row, ended with error
1222 after its 1.5 s lock timeout (three runs of three); with it on it answered in 1 to 3 ms.
The staging of the table's last two rows, run with the setting on (six runs with the
strategy and five without), deadlocks at the conditional `UPDATE`, since the read does not
wait: 200 six of six, and 503 five of five.

**The gate and registration:**

- `AuthServiceTests`: `InDemoMode_AUserOutsideEveryCopy_WithTheCorrectPassword_Is401_LikeAnUnknownEmail`,
  `InDemoMode_AFreeCopysOwner_Is401`,
  `InDemoMode_ACopyAtItsLifetimesEnd_Is401_AndOneSecondBeforeItSignsIn` (24 and 5 hours),
  `InDemoMode_AUserWithNoPassword_InALiveCopy_Is401_AndCountsNoFailedAttempt`, and the control
  `WithTheDemoOff_TheSameUsersSignIn`. Each asserts the cost spent, the password never checked,
  nothing counted towards a lock, no token and no grant, the reason on the log line, and one
  `failed` on the logins counter.
- `DemoModeEndpointTests.WithTheDemoOn_OnlyTheOwnerOfAClaimedCopySignsIn`;
  `WithTheDemoOn_PastACopysEnd_ItsPasswordSignsNobodyIn_AndASessionOpenedBeforeStillRenews`.
- On SQL Server: `ACopyPastItsLifetime_CannotSignIn_AndRecycleThenDeletesIt_WithoutTheBackstop`;
  `AUserOutsideEveryCopy_AndAFreeCopysOwner_CannotSignInOnTheDemoHost`;
  `FiveWrongPasswords_OnAGatedUser_LockNothing`, four gated users, with the living copy's owner
  locked by the same five as its control;
  `WithTheDemoOff_ASignInSendsNoStatementAboutThePool_AndWithItOn_OneReadOfTheCopyByItsKey`:
  with the demo on exactly one statement of a sign-in names `DemoCopies`,
  `SELECT TOP(1) ClaimedAt FROM DemoCopies WHERE Id = @copyId`, and with it off none. Recorded
  once on LocalDB and not asserted: two statements with the demo off (the user by its email,
  the grant's insert) and three with it on, the read between them.
- Registration: `WithTheDemoOn_RegistrationIs403RegistrationClosed_WhateverTheBody`, five bodies,
  each first shown to be answered 201, 400 or 415 with the demo off;
  `WithTheDemoOn_RegistrationOffTheTokenRoad_IsStill404`;
  `PublishedErrorContractTests.Every_action_the_demo_closes_declares_the_403_it_answers_there`,
  which ties the document's 403 to the marker.
- The time a gated refusal takes was not measured.

**The budget** (`DemoWriteBudgetSqlServerTests`, `DemoWriteBudgetEndpointTests`,
`DemoWriteBudgetMiddlewareTests`):

- `TheEleventhChange_Is429CopyLimit_AndAReadStillAnswers`; `AnotherCopy_IsNotAffected`;
  `TheReveal_IsCounted_AndRefusedAtTheLimit`; `SignOutEverywhere_IsNeverRefused`;
  `WithTheDemoOff_TheSameUser_IsNeverCounted`.
- `ARequestTheApiRefuses_IsCountedToo`: a wrong PIN at verification (answered 200), a deposit
  with no key (400), a deposit of a negative amount (400), a mint with a wrong PIN (401), each
  counted. `AChangeSentAgainUnderItsKey_IsAnsweredFromWhatWasStored_AndCountedAgain`.
- `AChange_IsCountedByOneStatementThatCarriesItsOwnCondition_OutsideAnyTransaction_AndBeforeAnythingElseIsWritten`.
  The statement's plan on LocalDB: two clustered index seeks, on `PK_AspNetUsers` and
  `PK_DemoCopies`, and no scan.
- `ChangesThatArriveTogether_AreEachCountedOnce_AndStopAtTheLimit`: 24 requests at once on a copy
  with 10 left, ten let through and fourteen refused, under both isolation settings. With the
  statement replaced by a read, a check and a write, all 24 were let through, 9 runs of 9.
- `WithTheDemoOn_ADepositWithNoKey_IsCountedBeforeItsKeyIsAskedFor` holds the middleware's place
  before idempotency without a database.
- `WithTheDemoOn_AnotherMethodOnTheRevealsPath_Is405_AndCountedOnlyIfItIsOneThatChanges`.
- Not measured: a count sent twice after a lost answer; the statement on SQL Server 2022 or
  Azure SQL other than through the compose run below.

**The BFF** (`AzureBank.Bff.Tests`):

- `DemoClaimTests`: `AClaim_SendsTheLimitersOwnKeyUpstream` (the body's address is the partition
  the limiter's own rejection names, and an address a request names goes nowhere);
  `AClaim_OpensASession_WithTheAnsweredStampAndGrant_CappedAtTheGrantsEnd_AndSetsTheCookie`;
  `AClaim_EndsTheSessionItsCookieNamed_AndRevokesOnlyItsGrant`;
  `AClaimArrivingWithAPinVerifiedSession_OpensOneAtAuthLevelOne`;
  `TheAnswer_IsTheEnvelope_WithTheUserTheCopyAndNoToken_AndIsNeverStored`;
  `AClaim_WritesNoLogLineThatHoldsThePasswordTheEmailOrAToken`;
  `HostileBodies_AreRefusedBeforeTheAction_AndReachNoApi`, eight bodies;
  `AClaimFromAnotherSite_Is403_AndReachesNoApi`;
  `AnApiRefusal_IsForwardedWithItsRetryAfter_AndTheOldSessionLives`;
  `AnApiWithTheDemoOff_MakesTheClaim404`;
  `AnApiThatDoesNotAnswer_Is503_AndOpensNoSession`;
  `PastTheAuthLimit_TheClaimIs429RateLimitExceeded`;
  `InDemoMode_RegistrationIs403_WhateverTheBody_AndTheApiIsNeverCalled`;
  `InDemoMode_AClosedRegistration_StillSpendsTheAuthLimit`.
- The three answers of "What a visitor can still do":
  `AnotherMethodOnTheClaimsPath_Is405WhateverTheFlag_AsOnSignInsPath` and
  `AnotherMethodOnRegistrationsPath_Is405WhateverTheFlag_AsOnSignInsPath` (and, in
  `AzureBank.Tests`, their two siblings in `DemoModeEndpointTests` for the API, with
  `WithoutTheServiceKey_TheClaimsPathAndRegistrations_Are401_WhateverTheMethodAndTheFlag`:
  eight requests without the service key, each 401 where the same request with the key is
  404, 405, 429, 400 or 403);
  `WithTheFlagOff_TheClaimsPath_StillSpendsTheAuthLimit`;
  `AuthLevelMiddlewareTests.WithNoSession_TheProxiedDemoClaimIs404_WhereAPathThatNamesNothingIs401_WhateverTheFlag`.
- `ClientAddressTests`: an IPv4 address in full, an IPv6 address as its /64, `unknown` for none;
  the longest key fits the 64 characters the API takes.
- `SpaHostingTests` and `SpaDemoTagTests`: the tag exactly once and right before the head's end,
  for `/`, `/index.html`, another case and extra separators; the content type, `no-cache` and the
  security headers; a HEAD; what is not the page never gets the tagged shell (14 requests, each
  compared with a host with the demo off); a shell with no head stops the host; with the demo off
  the page is the file's own bytes.
- Through both hosts, on SQL Server (`DemoClaimSqlServerTests`, in `AzureBank.Tests`):
  `ThroughTheBff_AClaim_OpensASession_AndTheProxyReadsTheCopysTwoAccounts` and
  `ThroughTheBff_StartingOver_GivesAnotherCopy_AndTheOldCookieIs401`.
- **The built BFF as a process** (Testing environment, `Demo__Enabled=true`): a shell with no
  `</head>` ended it with exit code 1 and one Fatal line naming the file and the setting; with
  the real build it served `/`, `/index.html` and a navigation as 3,063 bytes with one tag, the
  file's 3,020 and the tag's 43.
- Not held: the literal order of the two lines "new cookie, then end the old session" (no test
  can see it; what is held is that the old session ends only after the API answered a copy).

**The settings:** `DemoApiOptionsTests` (the API refuses to start with the demo on and no
usable secret: null, empty, 32 spaces, 31 characters; starts with 32; a value out of range stops
it with the demo off); `DemoOptionsPipelineTests` in `AzureBank.Bff.Tests`; `DemoHostTests`.
With `Demo__Enabled=true` in the environment of a test run, `DemoHostTests` fails 2 of 5: the
default API host does not start (the flag on and no secret), and the BFF nobody asked has the
flag on. The whole suites were not run that way.

**Each guard was broken once,** by a script that copies the file, changes one thing, runs the
tests, writes the copy back and compares SHA-256; every file was restored byte for byte. Of the
changes made on the committed bytes, by part:

| Part | Changes | Failed a named test | Failed none |
|---|---|---|---|
| The vocabulary and the settings | 9 | 9 | |
| The password and the key | 17 | 17 | |
| The claim | 37, then 11 after review | 36, then 11 | `[AllowAnonymous]` off the action fails no host test, since the API has no fallback policy; the committed document's test fails for it |
| The gate and registration | 40 runs, then 9 after review | 38, then 9 | the gate asked after the password check and before the count, which the SQL Server host tests cannot see and the unit tests do; the marker off registration, against the contract's tests alone, until the test above tied the two |
| The budget | 28, then 1 after review | 27, then 1 | the middleware between authentication and authorisation: nothing fails and nothing can today, since no policy of the API refuses a signed-in user |
| Two claims at once | 13 | 13 | |
| The BFF | 14, then 14 after review | 13, then 14 | the two lines "new cookie" and "end the old session" swapped |
| The API's log, and the service key on the claim's path (two tests added when this record was corrected) | 9 | 9 | |

Two statements of the claim are held by nothing and cannot be: the reset of the answer and of
the copy's id at the start of an attempt, which nothing a caller sees depends on.

**Schemathesis 4.27.1 against the claim,** run by hand as CI's conformance job runs it, on a
LocalDB database just migrated and seeded, the demo off, twice: exit 0, `Selected: 31/31`,
`Tested: 31`, `8880 generated, 8880 passed, 2879 skipped`, 32 test cases in the report. The claim
answered its declared 404 to each of its 152 requests, which is two warnings and no failure
(`Missing test data` for the claim; `Schema validation mismatch` on four operations). CI's own
job had not run on these commits when this was written.

**Measured on 2026-10-04 on the compose stack** (`compose.yaml` and `compose.demo.yaml`,
Production, the three images built from commit `f028b6b7` of this change's branch, SQL Server 2022
CU27 in a container, engine edition 3; the BFF on a published loopback port). Two runs of the
whole list on that commit, 380 and 409 values compared with what was expected, none different.
A value is the same in both runs unless the row says otherwise.

| What was run | What was observed | Not run |
|---|---|---|
| `migrate`, then `seed-pool` for a pool of 6 | Exit 0 both. 6 pool rows, 18 users, none with a password, none outside every copy, 24 accounts, 156 ledger rows. `is_read_committed_snapshot_on` 1 on the database `migrate` created | Azure SQL |
| `GET /`, `GET /index.html`, `HEAD /`, demo on | 200, `text/html; charset=utf-8`, `no-cache`, the seven security headers; the tag once, right before `</head>`; 3,063 bytes, the image's file (3,020) and 43; without the tag the bytes are that file's (SHA-256) | A browser |
| `/INDEX.HTML`, `//index.html`; the four spellings that answer the untagged file on Windows | The tagged page; 404 for each of the four | Another base image |
| Two claims, from two cookie jars | 200 and 200; two emails `demo-…@azurebank.example`; the envelope of decision 12; a password of 19 characters matching the pattern; `Cache-Control: no-store`, `Pragma: no-cache`; the cookie `__Host-AzureBank.Session`, `path=/; secure; samesite=strict; httponly`; no token in either body. `data.expiresAt` 899 to 900 s after the request, `data.copy.expiresAt` 24 hours after it. By SQL: two claimed rows, a 32-byte key on each, **one distinct key** | Two claims at once; a second address |
| Each owner: `/bff/auth/me`, `/api/accounts` | 200; two accounts each, 12,450.00 and 2,300.00 | |
| A mints a transfer to its own contact with the answered PIN | 201 | |
| What one copy's owner does, replayed against a second copy: A uses up its day, changes its PIN, renames its handle, drains an account, opens and renames another, is locked out by five wrong passwords (429 `ACCOUNT_LOCKED`, `Retry-After` 899) and locks its PIN with three wrong ones | B, each time: its transfers 201, its mint with the seeded PIN 201, its sign-in 200, its two accounts, its balances 12,430.00 and 2,300.00 after its own two transfers of 10.00. Both branches of A's day were run: the seeded 25.00 dated the day before (the mint of 5,000.00 is 201) and dated the same day (422 `DAILY_LIMIT_EXCEEDED` with `used` 25, then 4,975.00 is 201) | |
| A looks up, and mints to, the handles of B's three users, and a handle nobody holds | Lookup 200 `exists: false`; mint 404 `ACCOUNT_NOT_FOUND`; each body equal to the unheld handle's but for the handle and `traceId` | |
| Registration: through the BFF, a valid body and `{}`; the API's own, from inside the BFF's network namespace | BFF: 403 `REGISTRATION_CLOSED`, `Registration is closed on this demo.`, `instance` `/bff/auth/register`, twice. API: 403 with the key and the marker, 404 without the marker, 401 without the key. By SQL: 18 users, 0 outside every copy | The claim sent to the API's own address |
| A claims again, with its cookie | 200, another copy, a new cookie, the starting balances; the old cookie 401; the first copy still claimed; its grant revoked, reason `SessionEnded`, 0.04 s after the claim by the API's own log | |
| The gate: a third copy's owner taken out of its copy by SQL, and put back; its `ClaimedAt` moved 24 h 6 min back, then its password, a wrong one, and an email nobody holds; in the second run, a free copy's owner | 401 `INVALID_CREDENTIALS` each, and 200 for the controls. Each 401 equal to a wrong password on a living copy in status, headers but `Date` and the correlation id, and body but `traceId` (5 of 5 in the second run; the locked account's answer differs under the same comparison). `AccessFailedCount` 0 and no lock. The API's log names `OutsideEveryCopy`, `CopyEnded` twice, `NoPassword` | `CopyNotClaimed`: no command makes a user with a password on a copy nobody claimed. Timing |
| `POST /api/auth/demo/claim` through the proxy with a session; a form post to the BFF's claim with no `Sec-Fetch-Site`; the same marked `cross-site` | 404 with no body; 415; 403 `CROSS_SITE_REQUEST_BLOCKED`. Free and claimed rows the same before and after the three | |
| `recycle`, after the used copy's `ClaimedAt` was moved 24 h 6 min back by SQL | Exit 0, as the container's code too: `free=6 was=2 claimed=3 claims24h=2 clientsAtCap=0 seeded=4 deleted(expired=1 hardStop=0 staleFree=0 failed=0) swept(idempotency=0 grants=0) tombstones=1 foreignUsers=0 ceiling=no result=PoolOk`. Of the used copy, before: 3 users, 3 role rows, 5 accounts, 30 ledger rows, 4 authorisations, 2 idempotency records, 1 grant, 1 notice; after: 0 of each, in the eleven tables `DemoCopyRecycler.TablesOfACopy` names (the second run counted all eleven, with one row of its own in each of the three Identity tables the product never writes). Its record has `DeletedAt` and no client key; its owner's 9 audit rows are still 9. A copy past its lifetime with a live grant was left | `recycle` stopped while it deletes |
| `AzureBank.AuditVerifier verify` against that database | Exit 0, `CHAIN INTACT: 11 rows verified.`; SQL counts 11 | |
| The API's and the BFF's logs, searched | No email answered or sent, no password, no whole cookie value and none of the stack's nine secret values in either; four lines `Demo copy <id> claimed for user <id>` whose ids are SQL's; four `User <id> claimed a demo copy via BFF`; no line at Error or Fatal. The BFF's log names the first 8 of an ended session id's 43 characters (three lines), by a rule older than this change | |
| Caps run, a pool of 1, the daily cap at 2 and the budget at 10: eleven sign-ins in two seconds | Ten 401, then 429 `RATE_LIMIT_EXCEEDED`, `Retry-After: 60`. The limiter's line names the partition `172.22.0.1`, the compose network's gateway | A proxy in front; Azure |
| A claim; a second on the empty pool; after `seed-pool 3`, a second and a third | 200; 429 `DEMO_POOL_EMPTY` with no `retryAfterSeconds` and no `Retry-After`; 200; 429 `DEMO_DAILY_LIMIT` with `retryAfterSeconds` 86394 (86395 in the second run), equal to `Retry-After`. Two free copies left; one distinct client key | The overshoot |
| Ten deposits of 1.00 in one copy, an eleventh, a read; a deposit in another copy | 201 ten times; 429 `DEMO_COPY_LIMIT`, `This demo copy has reached its limit of changes. Start over to get a fresh copy.`, no wait named; 200; 201. By SQL `Writes` 10 and 1 | |
| `recycle` | `clientsAtCap=1 … result=PoolOk`, exit 0 | `seed-pool` given the cap the API has |
| The same images with `compose.yaml` alone | `seed` exits 0 with the four fixed users; no tag in the page, and its SHA-256 is the image's `index.html`; the claim 404 with no body, as a path the BFF does not have; registration 201; the seeded user signs in | |

**The compose files, without a stack** (`docker compose config` and dry runs, the values never
printed): with the nine variables, `api` gets `Demo__Enabled` and `Demo__ClientKeySecret`, `bff`,
`seed` and `recycle` get `Demo__Enabled` alone, and no variable of another service holds the key;
`compose.yaml` alone gives the same output as before this change. With eight of the nine,
`config`, `ps` and dry runs of `up`, `down`, `run --rm recycle` and `run --rm seed seed-pool 5`
each exit 1 with one line naming `DEMO_CLIENT_KEY_SECRET`. The secret's rule on the API's test
host: 31 characters do not start it, 32 do.

**Not measured here:** anything on Azure, what the BFF sees as a visitor's address behind the
ingress among it; another client address than the one a published port gives; two claims at
once on the compose stack; ~~a claim from a browser, since no screen calls it yet;~~ *(struck
2026-10-05: measured that day, from the sign-in page's "Try the demo" and from the dashboard's
"Start over", under "What the browser keeps in demo mode")* the gate's
`CopyNotClaimed` on a running stack; the time any of this takes (single readings: the first
claim of a fresh stack about 1.5 s, later ones about 0.1 to 0.2 s); a host with
`Database:MaxRetryCount` 0; a claim that loses a deadlock more often than the strategy retries;
a parallel load on an index that holds `ClaimedAt`.

## What would change this

- **An index of free copies that holds `ClaimedAt`** (as an included column) lets the read of
  decision 5 stay in the index: measured on a scratch database, the plan loses its key lookup
  and the read no longer waits for a locked row. It needs a migration, which this change does
  not add. A parallel load on that index was not measured.
- **The API reached over a network.** ADR-0057's precondition fails, and with it everything
  decision 16 rests on.
- **A measured address behind the ingress.** Once the BFF sees a visitor's own address there
  (`ForwardedHeaders:KnownProxies`), the daily cap means "per visitor" and the 1,000 of decision
  14 goes back to the default.
- **Visitors turned away by the daily cap behind one shared address:** a higher
  `Demo:Claim:MaxPerClientPerDay`, on every service that reads it.
- **A need to hide that a build has the claim:** the three answers of "What a visitor can still
  do" would then have to change, the first in both hosts and the other two in the BFF.
- **A demo deployed on Windows,** where four spellings of the page's file answer it untagged.
- **A claim whose lost answer strands copies often enough to see** on `recycle`'s line as claimed
  copies nobody used: a way to hand the same copy to the same request again.
- **A new endpoint that writes on a safe method:** it carries `[CountedAsDemoWrite]`, or the cap
  on a copy does not bound it.
- **A second action the demo closes, or one that exists only on the demo:** the marker, and for
  a closed one a declared 403, which `PublishedErrorContractTests` asks for.
- **A `Demo:*` value a host should not have to carry:** today either host refuses to start on
  any of them out of range.

## What the browser keeps in demo mode (added 2026-10-05)

Decision 13 left "how the demo's screens are seen" to the change that builds them, and the
Consequences said the stack's page had no screen for the claim. That change is the application's,
and with it the browser keeps something it never kept: what signs in to the copy a visitor
claimed. This section is its record, written after the screens ran on the compose stack. The
code is under `frontend/`: `src/features/demo/` (`demoMode.ts`, `demoCopyStorage.ts`,
`demoWords.ts`, `demoPin.ts` and four components), the claim's two effects in
`src/features/auth/sessionMiddleware.ts`, the sign-in page, the dashboard's panel, the session
dialog and the PIN boxes. The run is `playwright.demo.config.ts`, with its specs in `e2e-demo/`.

**1. One key, one shape.** `localStorage["azurebank.demoCopy"]` holds
`{ "v": 1, "email", "password", "pin", "contacts": [..], "expiresAt" }`: the five members of the
claim's `data.copy` (decision 12) and the shape's number. `expiresAt` is the copy's end, never
the access token's `data.expiresAt` beside it. The key is written in one place, where a claim's
200 arrives, so every caller of the claim leaves the copy there and none has to remember to. The
same dispatch then drops the application's whole cache of answers, the claim's own entry and
its password with it: what was fetched for whoever was signed in before is not drawn for the
copy's owner. In demo mode a sign-in that succeeds drops the cache too. With the demo off a
sign-in drops nothing, as before. The demo keeps nothing else in web storage. The application
writes one other key there, with the demo on or off: `azurebank.theme`, the theme preference
(`frontend/src/theme/themePreference.ts`).

**2. What comes back from the key is input.** Anything on the page's origin can write to it. It
is parsed as JSON and checked with the definition the claim's answer is checked by, with `v`
exactly 1, and a string that fails either is removed, not repaired. `expiresAt` has to name its
zone, a `Z` or an offset: a string that names none is refused, since a browser would read it as
its own local time. Every kept value reaches the page as text, never as markup. The `pin` is
kept and never rendered: the digits a page prints are one constant of the application,
`123456`, the pool's (ADR-0062, decision 4).

**3. When the key goes, and when it does not.**

| What happens | The key |
|---|---|
| A claim succeeds: "Try the demo", "Get a new copy", "Start over" | Written, in place of whatever copy it held |
| "Forget this copy", on the sign-in page | Removed. The page says "This browser no longer remembers the copy." and offers "Try the demo" |
| "Continue with my copy" is answered 401 `INVALID_CREDENTIALS` | Removed. Nobody typed that pair, so the answer a wrong password gets (decision 10) means the copy is over or gone, and the page says that in place of "Invalid email or password." |
| The sign-in page opens, and the copy's end is not after the browser's clock | Removed, and the copy is not offered (5 below) |
| The string does not parse, or is not a kept copy | Removed |
| The browser refuses to store a new copy | Removed, so that a reload does not bring back the copy the new one replaced. The new copy is held in memory while the page lives |
| A sign-out | **Kept.** That is what the key is for: the visitor comes back to the copy |
| The session dialog's "Stay signed in" is answered 401 | **Kept.** The dialog says "This demo copy has ended. Sign out to get a new one."; the sign-in page's own 401 is what removes it |
| The page is not the demo | Never read, and no screen sends the claim that would write it |

Nothing else removes it, and the key has no life of its own: no timer runs, and nothing but the
sign-in page compares a kept copy's end with a clock. So a browser that never opens the sign-in
page again keeps the address and the password after the copy is over, for as long as it keeps
its storage, although by then the server refuses the pair (decision 10). And on an origin whose
demo is later turned off the key is never read, and so never removed. (Read from the code: the
removals are the three of `frontend/src/pages/LoginPage.tsx` and the two of
`frontend/src/features/demo/demoCopyStorage.ts`, the five rows above that say "Removed".)

**4. What a script that read the key would gain.** A sign-in, possible for no longer than the
copy has left, and the session that sign-in opens, which runs to its grant's end (decision 10:
60 minutes by default, 24 hours at most), to one throwaway copy of invented money that nobody
registered for. The key holds no token, no session identifier and nothing of a real person; the
address is one the pool made up.
A script running in the page could already drive that copy through the page, with the session
cookie the browser sends for it. What the key adds is that the sign-in outlives the session and
can be carried to another browser. The application uses it that way itself: at a session's
fixed end the owner's dialog signs in again with the kept password, on one press of "Stay
signed in" and with nothing typed (`frontend/src/features/auth/SessionExpiryWarning.tsx`; held
against the mock by `SessionExpiryWarning.test.tsx`, `demo, the copy's owner: one button in
place of the password field` and `it sends the stored password, and the dialog closes because
the session's end moved`; not run on the stack). So for that owner the fixed end asks no person
for the password ([ADR-0026](0026-absolute-session-cap-reauthentication.md), decision 2),
until the copy's end, from which the gate refuses the pair (decision 10). The owner reads the
same details on the dashboard, under "Show sign-in details". The page is served under
`script-src 'self'` and `connect-src 'self'`
(`backend/src/AzureBank.Bff/README.md`, "Security Headers Middleware"; the header was read back
from the compose stack on 2026-10-05), so no inline script runs and none is loaded from another
origin.
That is why no such script is expected. It is not a wall around the value: no directive of that
header stops a page from navigating away with it (read, not tried).

**5. What the browser's clock does.** The sign-in page reads the browser's clock once, when it
opens, and holds the kept copy's end against that instant. That comparison both decides what is
offered and removes the key. It is made once more, against the same instant, when "Try the
demo" is pressed (6 below).

- **A clock ahead of the server's by more than the copy has left forgets a living copy for
  good,** with no sentence shown, and offers "Try the demo". That copy is never sent to the
  server, so no 401 judges it.
- **A clock behind offers a copy that is over,** and so does a page left open past the copy's
  end. There the server is the judge: its 401 to "Continue with my copy" removes the key, and
  the page words it.
- **On the dashboard a signed-in owner's copy is not removed by the clock.** A session opened
  before a copy's end runs to its grant's end (decision 10), and through it the dashboard still
  says whose the copy is and offers "Start over", printing an end that has passed as it is.
- **The sign-in page asks nothing about the session, though.** An owner who opens `/login`
  while signed in, or `/register`, which leads there on the demo, with a copy past its end has
  the key removed like anyone else. The dashboard then gives that visitor, still signed in, the
  sentence for one who is not the owner, and no "Start over". Measured on 2026-10-05 in the
  unit environment against the mock, by a test written for it and not kept: both addresses, and
  the dashboard's panel after each; with a copy that had not ended the key stayed. No kept test
  holds this, and it was not run in a browser.

The application's rule against trusting a server instant on the browser's clock
(`frontend/CONVENTIONS.md`, "429 appears in three places") is about countdowns. This is the one
place where it holds an instant it kept from the server against the browser's clock, and it is
done there and nowhere else for two reasons. A
copy that is over is not worth offering, nor its password worth keeping. And a check at every
read would take the panel and "Start over" from an owner whose session outlives the copy. The
other way was not taken: never remove on the browser's clock, always offer, and let the
server's 401 judge, at the price of one refused press. What the way taken costs is the first
bullet, and no tolerance was added for it. (One wait is worked out against that clock as well,
and is older than the demo: a `Retry-After` header that is a date, and not a number of seconds,
becomes seconds in `frontend/src/api/problemBaseQuery.ts`. The BFF's limiter sends seconds: the
`Retry-After: 60` of the table below.)

**6. The key is the source, read each time it is asked for.** Nothing is loaded once and kept,
and nothing listens for another tab: there is no `storage` listener. So another tab's "Forget
this copy" or "Start over" reaches a tab at its next render and not before, and until then that
tab shows the copy it drew, with the balances it had fetched. What it cannot do is send a
forgotten or replaced copy's password: "Continue with my copy" and "Stay signed in" read the
pair from the key when they are pressed. While the key's string is unchanged the same object is
handed back, which is what lets a component ask at every render.

"Try the demo" reads the key when it is pressed too. The button is drawn for a browser that
keeps no copy, and another tab may have claimed one since: a claim sent then would put a second
copy in that one's place with nobody asked, and end the session the other tab is signed in
with. So if the key holds a copy the page would offer, nothing is sent and the page offers that
copy, with nothing said. A copy past its end that the browser could not remove is not in the
way of a claim. Held against the mock by `LoginPage.test.tsx`, `a copy another tab claimed
before "Try the demo" is pressed: nothing is sent, and the copy is offered` and `"Try the demo"
still claims over a copy past its end that the browser would not remove`. Seen on 2026-10-05
in Chromium 151.0.7922.34 on the dev server with the mock, with two tabs of one browser
context, twice: the second tab, drawn before the first claimed, sent nothing at the press,
offered the first tab's copy with no alert, and left the key as it was. Focus was then on the
page, and nothing was built for that. From a press that does claim until the dashboard is
drawn, the page stays as it was pressed: the answer puts the new copy under the key before the
dashboard is there, and the page does not offer the copy it has just claimed (`from the press
on "Try the demo" to the dashboard the page never offers the copy it has just claimed`).

One tab is outside all of this: a tab whose browser refused to store its copy (3 above). It
holds that copy in memory and does not read the key again until a write succeeds there or the
copy is forgotten there. So another tab's "Forget this copy" or "Start over" never reaches it,
and its two buttons, where it draws them, send the pair it holds
(`frontend/src/features/demo/demoCopyStorage.ts`, `getDemoCopySnapshot`; held by
`demoCopyStorage.test.ts`, `a browser that refuses to store still remembers the copy while the
page lives` and `leaves a copy in the key and another in memory`). No browser that refuses
storage was run.

**7. Three sentences assume the default lifetime.** "24 hours" is typed out in the notice under
"Try the demo", in the sentence for a copy that is gone, and in the dashboard's sentence for a
visitor signed in on a browser that does not keep the copy, which has no end to print.
`Demo:CopyLifetimeHours` may be anything from 1 to 168 (decision 14): on a deployment that sets
another life those three are wrong. The owner's sentences print the copy's own end.

**8. Beside the key.** The claim's answer is one more through the `unwrap` seam of
[ADR-0023](0023-runtime-response-validation.md): checked whole when it arrives, in production
too. A claim whose answer fails the check changes neither who the page says is signed in nor
the key. The session cookie that came with that 200 is in the browser all the same, so the next
load of the application finds a session on a copy the browser holds no password for. (The BFF
sets the cookie, and ends the session the request came with, before it writes its answer: read
from `BffAuthController.ClaimDemoCopy`, not run on the stack. The application's half was
measured on 2026-10-05 in the unit environment, with an answer made for it and tests not kept.
From a page with nobody signed in, the refused answer left nobody signed in and no key. From a
page signed in to a first copy, it left that copy's owner on the page and that copy in the key.
Each time a new store's first question found the new copy's owner signed in. Of this, one kept
test holds that the key stays as it was: `StartOverDialog.test.tsx`, `an answer the app will
not accept: the fallback, and the copy kept is still the first`.) `/register` leads to the
sign-in page in demo mode, so the application never sends the registration decision 1 closes.
Of the four codes of decision 8 the application words two itself, `DEMO_POOL_EMPTY` and
`DEMO_DAILY_LIMIT`, in sentences of its own and with no countdown; for `DEMO_COPY_LIMIT`
nothing is built, and a surface that prints a refusal's `detail` prints the API's sentence.
(The one kept test that answers a request with that code is the "Start over" dialog's,
`StartOverDialog.test.tsx`, `any other refusal: what the server said, or the fallback`. It
shows that the dialog prints a `detail` it has no sentence for, with an answer made for the
test: what the dialog sends is the claim, a token endpoint, which the budget never counts
(decision 11). What the surfaces the code can reach would show, every change a copy's owner
sends, is read from the code; no kept test and no run on the stack met it.) One
test reads `ErrorCodes.cs`, `BffAuthController.cs`, `SpaHostingExtensions.cs`,
`DemoRefusalException.cs` and `DemoCopyDefaults.cs` as text
(`frontend/src/features/demo/demoContract.test.ts`): it fails if the two codes, the claim's
route or the tag's name stop being what the application spells, if a refusal's sentence in a
fixture stops being the API's, or if the PIN the page prints stops being the one the server
seeds every copy with. It finds the application's side by walking `frontend/src`, and not by a
list of names: a file there that is not a test and starts to name one of the demo's codes in a
string, or any file there that starts to type a refusal out with its sentence, fails the test
until it is written into what the test expects.

**Held by tests of the application** (`frontend/src`, against the mock): in
`features/demo/demoCopyStorage.test.ts`, `keeps a claimed copy under one key, in one shape`,
`removes what it cannot read` (eight strings), `what another tab forgot is gone at the next
read`, `with the tag off nothing is read`, `a browser that refuses to store still remembers the
copy while the page lives` and `a write the browser refuses does not leave the copy before it
in the key`; in `features/demo/claim.test.ts`, `a claim that succeeded leaves the copy in the
browser, with the copy's end and not the token's`, `…and its password nowhere in the store`, `a
claim that was refused changes neither`, `an ordinary sign-in resets nothing` and `in the demo
a sign-in leaves nothing of the cache it found`; in `pages/LoginPage.test.tsx`, `a copy past
its end is not offered, and is forgotten`, `a copy whose end passes while the page is open is
still offered: the end is looked at when the page opens` and `without the tag nothing reads the
demo's key`, and the three that point 6 names; in `features/demo/DemoCopyPanel.test.tsx`, `a
stored copy is shown as text, whatever it holds` and `a kept copy past its end is still its
owner's here, and is not removed`; in `features/auth/SessionExpiryWarning.test.tsx`, `a copy
that has ended says so, and stays remembered`. On `925974ee`, the last commit of this change
that touches the application's code, its specs or their comments, `npm test` passed 92 files
and 1,363 tests and `npm run test:contract:mock` 7 files and 79.

**Measured on 2026-10-05 on the compose stack, twice** (`compose.yaml` with `compose.demo.yaml`,
Production, Docker Desktop on Windows, a pool of 50 nobody had claimed from, the BFF on
`http://localhost:5000`; headless Chromium 151.0.7922.34 under Playwright 1.62.1, 1280 x 720,
the light theme). The table is the first run's, on the three images built from commit
`d8787a2b` of the application's branch: one run of `npm run test:e2e:demo`, with
`E2E_DEMO_COMPOSE_PROJECT` naming the compose project, `15 passed (35.6s)`, exit 0, and no name
the specs ask for had to change from what the mock had shown. The second run, on images built
from `340813a5`, is under the table, with what it met otherwise.

| What was done | What was observed | Not run |
|---|---|---|
| `GET /` | 200, `text/html; charset=utf-8`, the tag once, before the end of the head | |
| The sign-in page in a fresh browser context; then `/register` with a query and a fragment | "Try the demo" and no "Create account". The address ends at `/login`, with neither the query nor the fragment | |
| "Try the demo" | `POST /bff/auth/demo/claim` 200, `Cache-Control: no-store`, `message` `Demo copy claimed`. `data.copy.expiresAt` was `2026-10-06T09:22:42.2604692Z`: seven fractional digits and a `Z`, which the check of point 2 took on the first press. Two contacts; a password of 19 characters in four groups of four. The browser's cookie is `__Host-AzureBank.Session` (`Secure`, `HttpOnly`, `SameSite=Strict`, host `localhost`): Chromium kept it and sent it on `http://localhost:5000`. The page landed on `/dashboard` | |
| The dashboard | The total reads €14,750.00, with €12,450.00 and €2,300.00 in the buttons of the group "Account scope". The panel "Your private copy" has its three lines, "Show sign-in details" and "Start over"; the details show the address and the password the key holds | |
| One euro sent to the copy's first contact, with the PIN the page prints | `POST /api/transfers/authorizations` 201, `POST /api/transfers` 201, "Transfer Complete", and the total €14,749.00 | |
| "Start over", from the keyboard, confirmed in the dialog | A second claim, 200, `no-store`; "You have a new copy."; another address in the panel and in the key; the total €14,750.00 again; focus back on the panel's "Start over" | Any instant between the press and that end. That no draw in between shows the first copy's total is held against the mock (`pages/DashboardPage.test.tsx`), not measured on the stack |
| Sign out; "Continue with my copy" | `/login` offers "Continue with my copy", "Get a new copy" and "Forget this copy", and no "Try the demo"; the key still holds the second copy. `POST /bff/auth/login` 200, and the dashboard | |
| The BFF's container restarted and then the API's, each found by its two compose labels | Before: `GET /bff/auth/me` 200 with the saved state. Docker's `StartedAt` of both moved, the BFF's 2.4 s before the API's. `/health/ready` was asked three times: a request that threw, `200 Degraded`, `200 Healthy`. The step took 6.1 s | The step's refusals (no session, no container, two containers, no `docker`) on that stack |
| From the saved state, after the restart | `GET /bff/auth/me` 401. `/dashboard` leads to `/login` with no "Your session has expired" note and the kept copy offered. "Continue with my copy": `POST /bff/auth/login` 200, and the panel shows the kept address and password. Signed out, "Forget this copy" pressed from the keyboard: the status says "This browser no longer remembers the copy.", "Try the demo" alone is offered, with focus on it, and the key is gone | |
| Four scans with the gate of the default run (`e2e/axeScan.ts`) | The sign-in page and the open "Start over" dialog: no violation. The dashboard, with the sign-in details closed and with them open: colour contrast alone, two nodes, which the gate reports and does not fail on. With the details closed axe says nothing of the toggle's `aria-controls`. The report of the scan with the details open holds no markup of the page (`grep -c '"html"'`: 0, and 2 in the closed one's) | Heading order, which no scan checks |
| The default run's own check that the demo is off, pointed at this stack (`E2E_BASE_URL=http://localhost:5000 npx playwright test e2e/auth.spec.ts --project chromium --no-deps -g 'are offered "Create account"'`) | It fails, as it must: `createAccount` 0 where 1, `tryTheDemo` 1 where 0 | |
| The claim's door, asked by hand | No body and no `Content-Type`: 415. `text/plain` with `{}`: 415. `application/json` with an empty body or with `null`: 400. With `not json` or `[]`: 400 | `text/json` and `application/…+json` |
| The same three images with `compose.yaml` alone | `GET /`: 200, 3,020 bytes, no tag, and the bytes of the flag-on page with its tag taken out (SHA-256). The claim, with `{}` and with no body: 404, `Content-Length: 0`, the headers of a path the BFF does not have. Eleven claims in a row: 404 ten times, then 429 `RATE_LIMIT_EXCEEDED` with `Retry-After: 60` | **The default suite, `npm run test:e2e`, whole, in this first run.** It ran whole on the second run's images: below |
| A search of what the runs left on disk for the passwords of the copies claimed and for the stack's nine secret values | Twelve patterns. The search named the two files planted for it, and then no file | |

**In the first run the default suite was not run whole with the demo off.** The Docker engine
stopped answering while the stack was starting with `compose.yaml` alone: the database and the
BFF were up, the API was not, and `/health/ready` said `200 Degraded`. Against that BFF the four
tests of the suite that need no session ran and passed (`4 passed (19.3s)`): that a visitor is
offered "Create account" and no "Try the demo", the redirect from a protected route, and the
scans of the sign-in and registration pages, with no violation in either. The other 44 of its
48 tests did not run there.

**The second run, the same day, on images built from `340813a5`.** Between the two the
application changed. Among other things the demo run gained two tests, and three things a
visitor meets are not as they were: what "Try the demo" does over a copy another tab claimed,
what the sign-in page shows between a claim's answer and the dashboard, and what the closed
confirm dialog does with a press. So the three images were built again, and `npm run build` on
that commit gave the page, the script and the stylesheet their container served, byte for byte
(SHA-256, three of three).

- **The demo run: `17 passed (23.4s)`, exit 0,** with no test failed, skipped or flaky: the
  `demo` project 15, `restart` 1, `demo-after-restart` 1. The two tests added since the first
  run passed. One holds where the run's two secret files are written. The other is `no screen
  of the demo's raised a Content-Security-Policy violation`: it first heard a violation made
  for it, then found the page served under a policy and no violation on any screen the run had
  drawn. `after-restart.spec.ts` ends on the same expectation, and passed.
- **Every row of the table was done again and met as the table has it, but for three things.**
  The copy's end was `2026-10-06T15:40:45.8276093Z`. After the two restarts the BFF's
  `StartedAt` was 1.0 s before the API's, and `/health/ready`, asked three times, said
  `200 Degraded` twice and then `200 Healthy`: no request threw, and the step took 4.2 s. And
  the search was made six times, for thirteen to sixteen values, session cookies among them:
  each time it named a file planted for it in every place it searched, and then no file. What
  three of the later runs wrote under `test-results`, the default suite's second whole run and
  two reruns of its red test, was replaced by the next run before a search read it there.
- **The default suite with the demo off, on those images with `compose.yaml` alone: three whole
  runs, and the first two are red on one test.** Each was `npm run test:e2e` with `CI=1`,
  `E2E_BASE_URL=http://localhost:5000` and the two probe users CI's `real-stack` job names,
  then `node scripts/assert-e2e-ran.mjs`. The first: `1 failed`, `47 passed (7.9m)`. The
  second, on a fresh volume: `1 failed`, `47 passed (9.8m)`. The third, on a fresh volume:
  `48 passed (3.3m)`, `e2e: passed=48 failed=0 flaky=0 skipped=0`. No test was skipped in any
  of the three.
- **The red test is the same both times,** `e2e/pinLockExpiry.spec.ts`, `a real 429 from the
  API disables the PIN, ticks down, and releases at zero`, with the same message: `Tearing down
  "context" exceeded the test timeout of 30000ms.` By each run's trace the test's own steps,
  every expectation among them, had ended without an error, after 7.0 s and 5.1 s. What did
  not end is the closing of the browser's context. Run by itself the test passed twice, once on
  a PIN the red run had already locked and once on a fresh volume, and once more after one
  other spec in the same worker. **Why the context did not close is not known.** It is not
  shown to be load: the two red runs are the two slow ones, but the machine's processor was at
  97 to 100 % in each of ten samples taken during the green run too, and was not sampled during
  the red ones. That spec and `playwright.config.ts` are `main`'s, and this change does not
  touch them (`git diff --stat` over the two, against `main`: no line). Not tried: `main`'s
  tree on that stack, the suite with its trace off, the browser's own log of the close. CI's
  `real-stack` job runs this suite on every pull request.
- **What the three runs agree on:** the control passed (a visitor is offered "Create account"
  and no "Try the demo"); so did every test of `e2e/accessibility.spec.ts`, its fourteen scans
  among them, and `e2e/confirmDialog.spec.ts`; and the fifteen axe reports hold no finding the
  gate fails on, colour contrast in ten of them and nothing at all in the fifteenth,
  `transfer-leave-prompt`.

After `340813a5` this change touches comments and documents only. `npm run build` on
`925974ee`, the last commit that touches a file of the application or of its specs, gives the
same three files (SHA-256, three of three), and each of the eight source files changed after
`340813a5` transpiles, with its comments taken out, to what it did there.

**Not measured:** why the browser's context did not close in two runs of the default suite; on
the stack, the three things a visitor meets that changed between the two runs, which the demo
run does not single out and which are held against the mock; in a browser, the two refusals of
a claim (`DEMO_POOL_EMPTY`, `DEMO_DAILY_LIMIT`), whose sentences are held against the mock, and
`DEMO_COPY_LIMIT`; on the stack, "Continue with my copy" on a copy past its end, and "Stay
signed in", which needs a session at its fixed end; a browser that refuses storage; a browser
whose clock is ahead or behind, which the unit tests reach with a date held still; in a
browser, a signed-in owner who opens the sign-in page with a copy past its end; on the stack, a
claim whose 200 the application refuses; on the stack, a second open tab; under a scan, the
sign-in page of a browser that keeps a copy; heading order on the dashboard (in the unit
environment the panel's `h2` comes before the page's `h1`); what a screen reader says; on the
stack, any browser but headless Chromium at 1280 x 720 in the light theme; anything on Azure,
where the demo is still off (decision 14).

### Three changes to the shared confirm dialog (added 2026-10-05)

"Start over" asks before it claims, in `ConfirmDialog`
(`frontend/src/components/shared/ConfirmDialog.tsx`), the application's one hand-rolled modal,
which the two transfer pages also open for "Leave without finishing?". Opening the demo's
dialog in a real browser showed three defects of that component that were older than this
change and on every page that opens it, with the demo off too. All three were corrected in the
shared component, so the transfer prompts changed with the demo's dialog. They are recorded
here because this is the change that made them.

**It takes focus when it opens, and keeps Tab inside.** Before, measured on 2026-10-04 in
Chrome 154 and in Chromium 151 against the mock: the dialog opened and focus stayed on the
control that had
opened it, and the next Tab walked the page behind the open dialog, whose controls were live.
The unit tests were green all the while, because jsdom gives focus to an element whatever its
`visibility`. Three lines changed. The open overlay now transitions `opacity` alone, so it is
`visible` at the instant it opens: under the transition on `visibility` it was still `hidden`
then, and a browser gives a hidden element no focus. "Close", the control that takes focus,
transitions its two colours where it transitioned `all`, which covers the `visibility` it
inherits. And the trap reads the element the key was pressed on, not the focused one: where
the dialog's last control is the document's last Tab stop, as on the transfer pages, Fluent's
tabster hears Tab first and moves focus to an element of its own, and a trap that asked where
focus was let the third Tab leave the dialog (measured with the first two lines alone). After,
in both browsers, on the sign-in page's "Get a new copy" and on a transfer's leave prompt with
no demo tag on the page: focus on "Close" at the open; Tab, Tab, Tab and Shift+Tab stay inside;
Escape closes and gives focus back. The fade is still there, sampled frame by frame: the
opening is visible from its first frame, where it had one hidden frame, and the closing is as
it was. Held in jsdom by `ConfirmDialog.test.tsx` (`open, nothing from the control that takes
focus up to the overlay delays its visibility`, `wraps Tab by where it was pressed, also when a
listener before the dialog has moved focus`) and in a browser by two specs:
`frontend/e2e/confirmDialog.spec.ts`, in the default run, on the leave prompt, and the demo
run's `"Start over" asks first, takes the keyboard, and brings a new copy`.

- **On the compose stack, 2026-10-05:** the demo run's test passed in both runs: focus on
  "Close" at the open, Tab round the three controls and Shift+Tab back, all inside, and focus
  on the panel's "Start over" once the new copy was there. `e2e/confirmDialog.spec.ts` ran in
  the three whole runs of the default suite on the second run's images and passed in each. By
  then it also held that "Leave anyway" is the document's last Tab stop before it presses Tab,
  which is what makes its third Tab a test of the trap against tabster, and it scanned the open
  prompt: `transfer-leave-prompt`, no violation.
- **Measured and not built: while it waits, a dialog that is the first or the last Tab stop of
  its document still loses Tab.** Every control is disabled then, focus is on the dialog
  itself, and the trap leaves focus where tabster put it. **One page is in that state: the
  dashboard at the desktop width when its accounts could not be read.** It then draws its
  alert and the demo's panel and nothing after them, so the dialog's controls are the
  document's last. Measured on 2026-10-05 in Chromium 151.0.7922.34 on the dev server with the
  mock and the demo's tag, at 1280 x 720, the claim left unanswered, in two runs, one with the
  accounts read answered 503 twice and one with every read of the page answered so: ten
  controls before the dialog, three in it, none after. With "Start over" confirmed and waiting,
  the first Tab put focus on tabster's own element, the second on the page, the third on a
  "Transfer" button of the page behind; Shift+Tab then reached the panel's "Start over" and
  "Show sign-in details", behind the waiting dialog. With the accounts read (nine controls
  before the dialog, fifteen after), and at 390 x 844 with the read failed (three before, five
  after), three Tabs and three Shift+Tabs left focus on the dialog. The transfer prompts never
  wait, and on the sign-in page the dialog is neither first nor last (seven controls before
  it, one after). No kept test reaches the state.

**Closed, it takes no press.** The dialog stays in the page when it closes and stays drawn
while it fades, and until this change its buttons acted for that long. Before, measured in
Chromium 151 on the mock. On 2026-10-04: a dialog closed with Escape, when it had been opened
with no control holding focus, sent a claim for an Enter 9 to 143 ms after the Escape, six
times of six, and never at 420 ms. On 2026-10-05: two presses on the dialog's "Start over" at
one spot, 78 to 246 ms apart, sent two claims and showed two toasts, five times of five, so a
double click replaced the copy it had just brought; the mock answered a claim in 11 to 23 ms.
Each handler now asks first whether the dialog is open: the confirm, the cancel, the close and
the scrim. After, measured on 2026-10-05 in Chromium 151.0.7922.34 on the dev server with the
mock: two presses 172, 176 and 283 ms apart, the second on the closed dialog while it was
still drawn, sent one claim and showed one toast each time; an Enter 73 and 136 ms after an
Escape, on the confirm that still held focus, sent none. Held by `ConfirmDialog.test.tsx`,
`closed, it takes no press: not on its buttons, not on its scrim, not from the keyboard`. All
of it is the dev build with the mock. On a stack a second press that comes before the answer
meets a disabled button, before this change and after it (read, not run). The two transfer
prompts are the same component; nothing was measured on them.

**Its title sits on a line of its own.** Before, measured on 2026-10-04 in Chromium 151 on all
four pages that open the dialog: the title's computed `display` was `inline`, and the message
began on
the title's own line, right after it ("Start over with a new copy?You'll get a …", "Leave
without finishing?This transfer has …"); the 8 px the style declares under the title did
nothing on an inline box. One style line makes the title a block. After, on all four pages:
the title on its own line, the message under it at the content's edge, and the dialog 6 px
taller (263 px where 257 on the dashboard and the sign-in page, 284 where 278 on the two
transfer pages); the title is a block in the dark theme too, and at 390 x 844 on the dashboard
and the sign-in page. Held by `ConfirmDialog.test.tsx` › `gives its title a line of its own,
with the message under it`, which reads the declaration, since jsdom lays nothing out. The
title is still the dialog's name and the message its description.

The same component also gained one slot, under its error and above its buttons, for the wait's
hint of a confirm that takes time. And it takes focus itself when such a wait starts under the
button that was pressed: every control is disabled then, and a browser hands a disabled
control's focus to the page. Held in jsdom by `ConfirmDialog.test.tsx` (`keeps Tab inside when
it starts waiting under the button that was pressed`, `closed while its caller still says it
waits, it does not take focus back`), with the browser's hand-off emulated
(`frontend/src/test/outage.ts`). Seen on 2026-10-05 in Chromium 151.0.7922.34 against the
mock, with the claim left unanswered: after the press on the confirm, focus was on the dialog
itself, in each of the four runs of the bullet above. No kept browser test meets a wait: the
demo run looks only once its claim is answered. The transfer prompts never wait.

## Related

- [ADR-0062](0062-demo-visitors-get-private-copies-from-a-prepared-pool.md): the pool, the copy
  as the unit, `recycle` and its exit codes; the notes this record put there.
- [ADR-0057](0057-the-bffs-refresh-token-is-one-reusable-grant-per-session.md): the token road
  the claim answers on, the grant a claim issues, and the precondition of decision 16.
- [ADR-0055](0055-the-api-serves-one-client-the-bff.md): the service key every call to the API
  carries.
- [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md) and
  [ADR-0059](0059-the-spa-tells-the-visitor-when-the-service-is-slow-or-down.md): what a 503
  means, and why the demo's refusals are not one.
- [ADR-0012](0012-login-attempt-limiting.md) and
  [ADR-0013](0013-registration-user-enumeration.md): the equal cost of an unknown email, which
  the gate spends too, and the `auth` limiter the claim shares.
- [ADR-0014](0014-recipient-lookup-enumeration.md): the lookup's limit, whose unit on the demo is
  a claimed copy.
- [ADR-0026](0026-absolute-session-cap-reauthentication.md): the order in which a new session
  replaces an old one.
- [ADR-0054](0054-the-bff-serves-the-built-spa-under-a-csp-measured-against-it.md): the page the
  tag is put in.
- [ADR-0061](0061-the-demo-is-deployed-to-azure-container-apps-with-no-database-password.md): the
  deployment on which the demo is still off.
- `compose.demo.yaml`: the demo on one machine.
- [`docs/runbooks/demo-pool.md`](../runbooks/demo-pool.md): the claims by client, and the copies
  at their limit.
