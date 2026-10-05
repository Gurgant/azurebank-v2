# ADR-0009: Idempotency for Monetary Operations

**Status**: Accepted

**Date**: 2026-07-13

**Decision Makers**: Vladislav Aleshaev

---

## Context

The four monetary mutation endpoints (`POST /api/transactions/deposit`,
`POST /api/transactions/withdraw`, `POST /api/transfers`,
`POST /api/transfers/internal`) moved real balances with **no protection
against duplicate execution**: a client retry after a timeout, a double
click, or a network-level replay executed the same transfer twice.

Requirement: *the same operation must never execute more than once* — even
under concurrent identical requests, process crashes mid-operation, and
resilient-connection retries (`EnableRetryOnFailure` re-running committed
work after a lost commit ack).

The mechanism was adversarially design-reviewed BEFORE implementation
(10 attack surfaces + 6 additional holes found; 3 blockers fixed in the
design — see Notes).

## Decision Drivers

- **Never double-execute** — the one unforgivable failure for a bank; wins
  over availability wherever they conflict.
- Distinguish "crashed before committing" from "committed but the response
  was lost" — they demand opposite recovery actions.
- No secrets or brute-forceable material in the database.
- Contract consistency: RFC 9457 ProblemDetails + `errorCode` + `traceId`
  everywhere; OpenAPI spec 1:1 with live behavior (Schemathesis strict mode).
- Works identically in BFF mode (YARP forwards the headers untouched) and
  direct-JWT mode.

## Considered Options

1. **Two-state records (Processing/Completed) + delete-on-error** — the
   classic recipe. Rejected: it cannot distinguish crashed-before-commit
   from committed-without-response, so error paths and TTL revival can
   both resurrect a committed key (double execution); post-commit
   exceptions (e.g. response mapping after `CommitAsync`) would delete a
   live record.
2. **Three-state records + fencing token** (chosen, described below).
3. **Distributed lock service (Redis) or SQL app locks** — rejected:
   new infrastructure for a guarantee the primary key already provides.

## Decision

### Wire contract

- Header **`Idempotency-Key`** (UUID, one value, not `Guid.Empty`) is
  **required** on the four monetary endpoints, marked with
  `[RequireIdempotency]`. Missing → **400 `IDEMPOTENCY_KEY_MISSING`**;
  malformed → **400 `IDEMPOTENCY_KEY_INVALID`**.
- Retry with the same key and same payload after success → the stored
  response is replayed **byte-identically** (status, body, content-type)
  with **`Idempotency-Replayed: true`** (exposed via CORS on Api and Bff).
- Same key, different payload → **422 `IDEMPOTENCY_KEY_REUSE`**.
- Same key while the original is still running → **409
  `IDEMPOTENCY_IN_FLIGHT`** (no server-side waiting; clients retry). This
  includes the moment just after the original's business commit and before
  its response is stored: a short retry then hits the replay.
- Same key after the operation committed but its response was provably
  lost (record stuck `Executed` past the staleness window) → **409
  `IDEMPOTENCY_RESULT_UNKNOWN`** ~~("verify via GET /api/transactions")~~.
  *(Amended 2026-10-01: this answer carries **`applied: true`**. The record was read from the
  database as `Executed`, a state that reaches the database only with the commit that moves the
  money, so the operation is committed and this answer only cannot return its result. A client
  must not send it again under a new key, and the sentence that said "verify … before retrying
  with a new key" now says: "The operation sent with this idempotency key was applied, but this
  request cannot return its result. Do not send it again with a new key: look for it with GET
  /api/transactions." The same code carries `applied: true` when a retried attempt reloads the
  record under its key and finds it `Executed` or `Completed` (the transfer retry under
  Post-merge hardening), under the request hash the attempt claimed the key with. `Completed`
  there means another request with the same key and bytes took a stale claim over, committed and
  stored its answer: the payment under this key went through, and the same key sent again would
  be replayed that answer. The code carries **no** `applied` when that reload finds no row, or
  finds a record claimed with another body: nothing is proven about this request, ~~and its
  sentence says "may have been executed"~~ (struck 2026-10-03: only the no-row answer says
  that; the other has a sentence of its own, in the correction below). The reload is by key,
  and a key can come to hold
  another body's record: the request's claim is taken over as stale by an instance whose clock is
  a minute or more ahead, the request that took it is refused before any write and releases it,
  and with no record left there is no hash to refuse another body on. The attempt keeps the hash
  it claimed with and compares it after the reload, so "the same bytes" is checked there and not
  assumed; `Processing` under another hash is refused the same way, not re-armed. The SPA cannot
  send another body under a key it holds. `applied` is absent, never `false`, on every other
  409, and the document declares it on the four money 409s with `true` as its only value.)*
  *(What the flag stands on: the flip to `Executed` reaches the database only with the business
  commit, and a rollback takes it back. Since the same date tests hold both halves on SQL Server.
  A refusal thrown after the flip was written leaves no `Executed` record:
  `ExternalTransfer_RefusedAfterItsFirstSaveRan_LeavesNoExecutedRecord`, its internal twin, and
  `WhenTheConsumeMatchesZeroRows_TheWithdrawalIsRolledBackToo`. A commit refused as it starts is
  run again and reads `Processing`:
  `ExternalTransfer_CommitRefusedAsItStarts_RunsAgainAndMovesTheMoneyOnce`, its internal twin, and
  `AWithdrawalWhoseCommitIsRefusedAsItStarts_RunsAgainAndMovesTheMoneyOnce`. And
  `ProvenCommitSourceTests` counts the saves in each file of the money path, because a save on
  the request's context between the flip and the business commit would write `Executed` with no
  money moved. The reload's comparison of the request hash is held without SQL Server, by
  `ARecordClaimedAgainWithOtherBytes_RefusesToRun_AndDoesNotSayTheOperationWasApplied`, for a
  record `Executed`, `Completed` and `Processing`, and since 2026-10-03 on SQL Server too, by
  `ExternalTransfer_RecordClaimedAgainWithOtherBytes_Answers409WithoutApplied_AndNothingMoved`.
  Not in the answer: the transaction's id, since the record holds no link to a
  ledger row. With one a client could show the movement; without it the client is told that it
  exists. Nor a promise that `GET /api/transactions` lists it: a movement on an account closed
  afterwards is not listed. Still withheld for two minutes: a committed send whose answer was
  lost answers `IDEMPOTENCY_IN_FLIGHT`, without `applied`, until the claim is
  `ProcessingStaleAfter` old, because until then its stored answer may still come. The age is
  judged on the answering instance's clock against the claiming instance's `CreatedAt`: a clock
  ahead says `applied: true` sooner, never wrongly, since the record is `Executed` in the
  database either way.)*
  *(Corrected 2026-10-03: the two unproven outcomes above shared a sentence saying the record
  "is no longer there", even when the reload found another body's record under the key. That
  second case now says: "This idempotency key now holds another request's record, so the outcome
  of this request is not known. Verify via GET /api/transactions before sending it again with a
  new key." The no-row case keeps its sentence byte for byte. Both remain 409
  `IDEMPOTENCY_RESULT_UNKNOWN` with no `applied` member; only the detail of the replaced-record
  case changes.)*
- *(Added 2026-09-30, [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md).)*
  The database cannot be reached, or the request ran past its deadline → **503
  `SERVICE_UNAVAILABLE`** with `retryAfterSeconds` and `Retry-After`. It carries **`applied:
  false`** only when the request holds the claim it made itself (not a replay) and no commit has
  started, and then nothing was changed; on every other 503 `applied` is absent, because a request
  that failed reading the key, claiming it or writing a replay cannot know what an earlier one with
  the same key did. A client keeps the key on it either way: `applied: false` changes what the
  visitor is told, never which key the retry sends.
- Request body larger than 32 KB → **413 `IDEMPOTENCY_PAYLOAD_TOO_LARGE`**,
  rejected before any buffering/hashing/claim (see Placement & limits).
- **Key scope**: per user, per logical endpoint. Cross-user and
  cross-endpoint reuse of the same UUID are independent operations.
- **Window (TTL): 24h.** After it, a key is forgotten (a retry would
  re-execute); expired rows are swept hourly by a background service.
- `create-account` is deliberately **out of scope**: not a money movement,
  duplicates are visible and recoverable, and the primary-account unique
  index already guards the dangerous case.

### Storage: `IdempotencyRecords`

- **Composite PK `(UserId, Endpoint, Key)` — the uniqueness IS the lock**:
  exactly one concurrent claim INSERT can succeed. The loser re-reads and
  resolves to replay/409/422. Never two executions.
- `Endpoint` = `"{METHOD} {RoutePattern.RawText}"` (e.g.
  `POST api/transfers`) — route metadata, never the raw path (routing is
  case/trailing-slash tolerant; raw paths would split one logical endpoint
  into several idempotency scopes).
- **`RequestHash` = HMAC-SHA256(server key, raw body bytes)**, lowercase
  hex. Raw bytes (no JSON canonicalization): deterministic, parser-free,
  and real retries resend identical bytes. **Keyed**, not plain SHA-256:
  ~~the withdraw body contains a 6-digit PIN, so an unkeyed hash of a
  mostly-known payload would give anyone with DB read access a 10^6-guess
  offline oracle — defeating the reason PINs are Argon2id-hashed.~~
  *(Struck 2026-09-21: ADR-0056 moved the PIN to the withdrawal mint, so no
  monetary body carries one. The digest stays keyed on the surviving
  reason: every body here is low-entropy and mostly known — an account id,
  an amount from a small range, a short description — so an unkeyed hash
  would still let a reader of the table confirm guesses about what somebody
  moved and to whom.)* The key
  (`Idempotency:HashKey`) lives in configuration (user-secrets/env),
  never in the repo or the database; startup fails fast if absent.
- **`ClaimId` (Guid, concurrency token) = fencing + owner token.** Every
  UPDATE/DELETE is conditional on it (0 rows → `DbUpdateConcurrencyException`).
- Stored response: status code, body, content-type. Nothing else is stored
  because nothing else exists to store: the monetary 201s carry no
  Location header (pinned by a regression test); correlation/trace ids are
  correctly per-request.

### Lifecycle: Processing → Executed → Completed

1. **Claim**: INSERT `Processing`, committed immediately (pre-MVC, clean
   request DbContext).
2. **Executed flip**: the middleware marks the tracked record
   `Executed` + rotates `ClaimId` — *without saving*. The update rides the
   **first business `SaveChanges`** (for transfers: inside their explicit
   transaction) and therefore commits **atomically with the money
   movement**. This is the crux: the record state now *proves* whether the
   operation committed.
3. **Complete**: after a 2xx, the buffered response is persisted
   (`Completed`) **before the first byte reaches the client**.
   *(Amended 2026-09-30, [ADR-0058](0058-the-api-gives-up-cleanly-when-the-database-is-down.md):
   with 3 s of its own, where it had no bound (`CancellationToken.None`), and not the request's
   token, so a client that has gone does not stop it; one not stored in time leaves the record
   `Executed` while the 2xx is still sent. That the answer it stores is whole, for a client that
   hung up after the commit, is `DeadlineResultFilter`'s doing (ADR-0058 D5). An **empty** 2xx is
   never stored: no monetary success is empty, so an empty one is an answer lost while it was
   written, and stored it was replayed to every retry of the key as the answer (measured before the change: the record `Completed` with an empty body, and an empty 201
   on the retry, `RequestDeadlineSqlServerTests`). Not stored, the record stays `Executed` and a
   retry gets `IN_FLIGHT`, then `RESULT_UNKNOWN`. The fenced delete that releases a claim on the
   error path gets 3 s too; one that runs out leaves the claim `Processing` until it is stale.)*

### Failure semantics (all derived from the DB truth, not exception types)

| Situation | Record state | Behavior |
|---|---|---|
| Validation/business error (400/401/404/422), rollback | `Processing` | Fenced delete → **key stays reusable** (fixing the payload and retrying the same key works; errors are never replayed) |
| Post-commit exception (e.g. response mapping crash) | `Executed` | Record kept → retries get 409 `IDEMPOTENCY_RESULT_UNKNOWN`, with `applied: true` (2026-10-01, note below) |
| Crash before commit | stale `Processing` | Provably nothing committed → **safe takeover** after `ProcessingStaleAfter` (~~10 min~~ 2 min, see the note below): fenced delete + fresh claim |
| Crash after commit, before response stored | `Executed` | 409: `IN_FLIGHT` while the claim is fresh (response may still land), `RESULT_UNKNOWN` once stale, with `applied: true` (2026-10-01, note below); swept at TTL with a Warning log (reconciliation signal) |
| Commit ack lost, resilient strategy re-runs the commit | fence mismatch | The re-run updates `WHERE ClaimId = <old>` → 0 rows → aborts. **No in-request double execution** |
| Stale claimant resumes after a takeover | fence mismatch | Its business commit carries the flip with the old `ClaimId` → aborts atomically |
| Response persist fails after a 2xx | `Executed` | The 2xx is still sent (the operation DID succeed — client-first, deviation from review recommendation of 500); retries get `RESULT_UNKNOWN`, with `applied: true` (2026-10-01, note below), never a corrupt replay |

*(Amended 2026-10-01: each of the three rows that answer `RESULT_UNKNOWN` is a record read
`Executed` from the database, so each carries `applied: true` (the note on the wire contract
above). The table has no row for the one answer of that code that does not: a retried attempt
whose reload finds the record gone, or replaced by one claimed with another body, under
Post-merge hardening below.)*

*(Amended 2026-09-30, ADR-0058: `ProcessingStaleAfter` is **2 minutes**, down from 10. A request
now gives up at its 40-second deadline, lets no commit start after it, and gives the release of its
claim 3 s, so a claim still `Processing` two minutes on belongs to no request that is still running,
unless its process was paused; the fence covers that one, since its commit carries the old
`ClaimId` and aborts. The value matters because a release can fail exactly when the database does,
and the key then answers 409 `IN_FLIGHT` until its claim is stale: a visitor's retry waits two
minutes instead of ten. The same age ends `IN_FLIGHT` for an `Executed` record whose answer was
not stored: its key answers 409 `RESULT_UNKNOWN` after two minutes instead of ten. That is no less
true, since storing an answer starts as soon as its commit returns and is given 3 s: an answer still
missing two minutes after the claim is not coming. At start the API refuses a value less than a
minute longer than `RequestDeadline:Seconds`.)*

### Placement & limits

- Middleware sits **after `UseAuthorization`** (401/403 never create
  records) and before the endpoints; its 400/409/422 are thrown as
  `IdempotencyException : AppException` → uniform ProblemDetails.
- Body size is capped at **32 KB** (legit bodies < 2 KB). ~~`[RequestSizeLimit(32768)]`
  on the four endpoints is an MVC resource filter that runs only once the action
  executes — *after* this middleware has already buffered and HMAC-hashed the body.~~
  *(Struck 2026-10-05: wrong about when the limit applied, and the four endpoints
  no longer carry `[RequestSizeLimit]`. The attribute acted twice. As endpoint
  metadata, routing applied it when the endpoint was matched: before
  authentication and before this middleware. As an MVC authorization filter
  (`RequestSizeLimitFilter`) it tried again inside MVC; on the four idempotent
  endpoints the middleware had read the body by then, the server's limit was
  read-only, and every request the middleware passed on logged the Warning "A
  request body size limit could not be applied" (event 2 `FeatureIsReadOnly`):
  four requests, one to each endpoint, four events. They now carry
  `[EndpointRequestSizeLimit(32_768)]`, the same routing metadata without the
  filter. The 32,768-byte limit is in force from the moment the endpoint is
  matched; the middleware's own 413 for a declared length and its cap on chunked
  reads, described next, are unchanged; nothing tries to set the limit after the
  body is read. The four authorisation mints keep `[RequestSizeLimit]`: there the
  filter sets the limit and logs no warning. Measured on Kestrel by
  `KestrelRequestSizeLimitTests`: asked with no token, each of the four logs
  exactly one routing event `MaxRequestBodySizeSet` naming 32768, before the
  first authentication event; a 40 KB deposit is answered 413
  `IDEMPOTENCY_PAYLOAD_TOO_LARGE` with a `Content-Length` and chunked; no
  `RequestSizeLimitFilter` event on the four. The in-memory test host offers no
  `IHttpMaxRequestBodySizeFeature` and shows none of this.)*
  So the middleware itself rejects `Content-Length > 32768` with **413
  `IDEMPOTENCY_PAYLOAD_TOO_LARGE`** *before* buffering/hashing/claiming ~~(closing an
  authenticated hash-amplification DoS)~~ *(Struck 2026-10-05 with the sentence
  above: on Kestrel, routing's limit had already closed it. Measured with this
  check and the cap on chunked reads both taken out of the middleware:
  `KestrelRequestSizeLimitTests` still passed, 12 of 12, a 40 KB deposit still
  answered 413 `IDEMPOTENCY_PAYLOAD_TOO_LARGE` with a `Content-Length` and
  chunked. What the check still does there is what the note of 2026-09-24 below
  describes. It is the only limit where the server offers none, as in the
  in-memory test host: there the same change failed five tests of
  `IdempotencyEndpointTests` and `OversizedBodyDrainTests`.)*, caps chunked reads
  at the same limit via
  `IHttpMaxRequestBodySizeFeature`, and raises `EnableBuffering`'s threshold to 32 KB
  so an accepted body ~~(PIN included)~~ is never spooled to disk. *(Struck
  2026-09-21, ADR-0056: no monetary body carries a PIN any more — the
  withdrawal's moved to its own mint, which is not an idempotent endpoint. The
  threshold is unchanged; what is gone is the secret that made spooling one to
  disk the sharpest reason for it.)* *(Amended 2026-09-24, backlog row 42:
  refused unread, an oversized body let Kestrel abort the connection under the
  BFF's proxy — a reset for the sender, or a 502 for the next request on that
  connection. The middleware now reads and discards a body of up to 1 MiB before
  the 413, never buffering or hashing it, and for five seconds at most; above
  that size, or once the five seconds are up, it answers `Connection: close`.
  `docs/engineering-traps.md` has the measurements.)*
- The BFF needs no changes: YARP forwards `Idempotency-Key` and
  `Idempotency-Replayed` by default (verified; its transform only adds
  `Authorization`).

## Rationale

The three-state + fencing design was chosen over the classic two-state
recipe because the review proved the two-state version double-executes in
real scenarios present in THIS codebase (post-commit exception in
`TransferService`; TTL revival of a committed-but-crashed key;
`EnableRetryOnFailure` re-running a committed claim). The flip costs almost
nothing here — deposit/withdraw are a single `SaveChangesAsync`, transfers
already run in an explicit transaction — and turns crash recovery from
guesswork into a provable state machine.

## Consequences

### Positive

- Single execution proven under N≥24 parallel identical requests
  (exactly one 201-without-replay; balances mathematically exact),
  deterministically, on EF InMemory (smoke) and real SQL Server (proof).
- Honest crash semantics: clients are never told "not executed" when the
  truth is unknown.
  *(2026-10-01: nor "executed" when that is not proven. Until this date the answer written when
  the record had vanished said "was executed"; it says "may have been executed" now, and a test
  builds that case with nothing committed:
  `ExternalTransfer_RecordGoneWhenTheRetryReloadsIt_Answers409WithoutApplied_AndNothingMoved`.)*
- Business errors don't burn keys; clients keep one key per logical
  operation attempt.

### Negative

- One extra INSERT + UPDATE per monetary request (the UPDATE rides the
  business commit; measured impact negligible at this scale).
- A crashed-after-commit key stays 409 for up to 24h — deliberate
  correctness-over-availability trade-off.
- `Idempotency:HashKey` is a new required secret (dev setup: one
  user-secrets command; documented in README and .example).

### Neutral

- New EF migration `AddIdempotencyRecords`.
- Frontend (R3) must generate a UUID per user-intent and reuse it across
  retries of that intent (note for the wiring session).
  *(The client half of this protocol — the keep/rotate table per outcome, byte-identical
  step-up replay, and the RESULT_UNKNOWN verify latch — is now specified in ADR-0022.)*

## Validation

- Unit + integration suite covering the full decision table (replay,
  reuse-422, in-flight-409, result-unknown-409, stale takeover, TTL
  expiry, cross-user/cross-endpoint independence, error-releases-key,
  fencing aborts after takeover).
- **Concurrency proof**: 24 byte-identical parallel transfers (and
  deposits) with one key → exactly ONE execution, replays byte-identical,
  final balances exact — 3 rounds per run, on InMemory everywhere and on
  SQL Server via `AZUREBANK_TEST_SQLSERVER` (LocalDB locally, mssql
  container in CI).
- Schemathesis strict mode against the updated spec (required header
  documented via operation transformer).

## Post-merge hardening (deep review)

A second adversarial deep review (3 read-only reviewers over the whole PR) found
**no new double-execution path** — the fencing / three-state machine held under
tracing. It surfaced a set of bounded issues, resolved as follows.

**Fixed**

- **Transfer retry double-execution (critical).** `EnableRetryOnFailure` re-runs
  the transfer delegate against the shared request DbContext on a transient fault;
  the failed attempt's Added transactions / already-mutated balances were
  re-applied (Case A), or an already-committed transfer re-executed after a lost
  commit ack (Case B). Fixed by resetting the tracked work **and** re-reading the
  idempotency record from database truth at the top of every attempt (→ 409
  `RESULT_UNKNOWN` when already `Executed`/`Completed`). *(Amended 2026-10-01: with `applied:
  true`, since that is the record as the database holds it, when its request hash is the one the
  attempt claimed with. The arm this paragraph did not mention, a reload that finds no row,
  answers the same code without `applied`: the claim was deleted under the request, and neither a
  commit nor its absence is proven. So does a reload that finds a record claimed with another
  body, whatever its state: the request's own claim is gone there too.)*
  The regression test injects
  a one-shot transient on a *retrying* context — it fails on pre-fix code (double
  debit / 500) and passes after (single execution).
- **Money scale.** Amounts are validated to ≤ 2 decimals (columns are
  `DECIMAL(19,4)`); previously `10.12345` passed and the response echoed a
  precision the store silently rounds away. Mirrored in the OpenAPI schema
  (`multipleOf: 0.01`) so the Schemathesis contract stays 1:1.
- **Hash-amplification DoS + PIN disk-spool.** The 413 body guard above.
- **Stored-response cap.** A > 64 KB 2xx is not persisted for replay (a retry then
  gets 409 `RESULT_UNKNOWN`); bounds the stored row and the replay buffer.
  *(2026-10-01: `IN_FLIGHT` while the claim is fresh, then `RESULT_UNKNOWN` with `applied: true`,
  the record being `Executed`.)*
- **`IsDuplicateKey` narrowing.** Only an InMemory duplicate-PK `ArgumentException`
  (matched by message) is treated as a lost claim race; any other `ArgumentException`
  now propagates instead of being masked as a false 409.
- **Immutability guard on the SaveChanges funnels.** Moved onto `SaveChanges(bool)`
  / `SaveChangesAsync(bool, ct)` so a direct bool-overload call can no longer bypass
  the write-once Transaction guard.
- **Dev CORS.** The development policy now reflects only **loopback** origins (any
  localhost port) instead of any origin + credentials. *(Superseded: ADR-0018 deletes
  CORS from the BFF entirely — same-origin topology, which also ends the exposed
  replay-header dependency.)*
- **Coverage.** Added an N=24 parallel *withdrawal* overdraft proof (exactly one
  success, balance never negative) alongside the existing deposit/transfer proofs.

**Documented / accepted (by design, not defects)**

- **TTL re-execution.** After the 24 h window a forgotten key re-executes — a retry
  then moves money twice. Standard TTL semantics; the correctness-over-availability
  trade-off is deliberate (see Consequences / Window).
- **Replay restores status + body + content-type only.** Any app-set response
  header (a future `Location` on a 201, an `X-*` header) is **not** replayed. Latent
  today: the monetary 201s carry no such header, pinned by
  `Monetary201_CarriesNoLocationHeader`. **Follow-up if a header is ever added**:
  persist a header allowlist in the record and re-emit it on replay.
- **Claim INSERT precedes model validation.** An invalid body does an
  INSERT-then-fenced-DELETE (the key stays reusable). Correct outcome; the minor
  table churn under a burst of invalid payloads is accepted.
- **Minor, accepted**: fixed retry jitter (no exponential backoff); Guid-leading
  clustered PK (insert fragmentation, offset by per-user lookup locality); the
  immutability guard's coupling to the `Transaction` entity.

## Related

- ADR-0003 (Argon2id) — ~~the reason `RequestHash` must be keyed~~ *(struck
  2026-09-21, ADR-0056: it was the reason while a monetary body carried a PIN.
  The key stays, on the reason stated above — every body here is low-entropy and
  mostly known, so an unkeyed digest would still let a reader of the table
  confirm guesses about what somebody moved and to whom.)*
- docs/api/openapiv1.json — regenerated with the header + 409/422.
- Review follow-ups filed separately (pre-existing, out of scope here):
  no API-side PIN attempt limiting; BFF rate limiter defined but never
  attached to routes.

---

## Notes

Adversarial review verdicts incorporated: H1 (no blind TTL revival of
Processing rows → three-state), H2 (endpoint identity from route metadata),
H4 (request size cap), H5 (fenced expired-row deletes), H6 (release only on
proven non-execution — blocker), H8 (owner-token recognition under
`EnableRetryOnFailure`), H10 (keyed HMAC instead of plain SHA-256 —
blocker), H11 (ClaimId rotation closes in-request double execution),
H12 (CORS expose header), H15 (proof must run on real SQL Server),
H16 (OpenAPI operation transformer).
