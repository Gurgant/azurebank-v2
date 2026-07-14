# ADR-0009: Idempotency for Monetary Operations

**Status**: Accepted

**Date**: 2026-07-13

**Decision Makers**: Development team (R2 session), adversarial design review

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
   new infrastructure for a guarantee the primary key already gives us.

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
  `IDEMPOTENCY_RESULT_UNKNOWN`** ("verify via GET /api/transactions").
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
  the withdraw body contains a 6-digit PIN, so an unkeyed hash of a
  mostly-known payload would give anyone with DB read access a 10^6-guess
  offline oracle — defeating the reason PINs are Argon2id-hashed. The key
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

### Failure semantics (all derived from the DB truth, not exception types)

| Situation | Record state | Behavior |
|---|---|---|
| Validation/business error (400/401/404/422), rollback | `Processing` | Fenced delete → **key stays reusable** (fixing the payload and retrying the same key works; errors are never replayed) |
| Post-commit exception (e.g. response mapping crash) | `Executed` | Record kept → retries get 409 `IDEMPOTENCY_RESULT_UNKNOWN` |
| Crash before commit | stale `Processing` | Provably nothing committed → **safe takeover** after `ProcessingStaleAfter` (10 min): fenced delete + fresh claim |
| Crash after commit, before response stored | `Executed` | 409: `IN_FLIGHT` while the claim is fresh (response may still land), `RESULT_UNKNOWN` once stale; swept at TTL with a Warning log (reconciliation signal) |
| Commit ack lost, resilient strategy re-runs the commit | fence mismatch | The re-run updates `WHERE ClaimId = <old>` → 0 rows → aborts. **No in-request double execution** |
| Stale claimant resumes after a takeover | fence mismatch | Its business commit carries the flip with the old `ClaimId` → aborts atomically |
| Response persist fails after a 2xx | `Executed` | The 2xx is still sent (the operation DID succeed — client-first, deviation from review recommendation of 500); retries get `RESULT_UNKNOWN`, never a corrupt replay |

### Placement & limits

- Middleware sits **after `UseAuthorization`** (401/403 never create
  records) and before the endpoints; its 400/409/422 are thrown as
  `IdempotencyException : AppException` → uniform ProblemDetails.
- Body size is capped at **32 KB** (legit bodies < 2 KB). `[RequestSizeLimit(32768)]`
  on the four endpoints is an MVC resource filter that runs only once the action
  executes — *after* this middleware has already buffered and HMAC-hashed the body.
  So the middleware itself rejects `Content-Length > 32768` with **413
  `IDEMPOTENCY_PAYLOAD_TOO_LARGE`** *before* buffering/hashing/claiming (closing an
  authenticated hash-amplification DoS), caps chunked reads at the same limit via
  `IHttpMaxRequestBodySizeFeature`, and raises `EnableBuffering`'s threshold to 32 KB
  so an accepted body (PIN included) is never spooled to disk.
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
  `RESULT_UNKNOWN` when already `Executed`/`Completed`). The regression test injects
  a one-shot transient on a *retrying* context — it fails on pre-fix code (double
  debit / 500) and passes after (single execution).
- **Money scale.** Amounts are validated to ≤ 2 decimals (columns are
  `DECIMAL(19,4)`); previously `10.12345` passed and the response echoed a
  precision the store silently rounds away. Mirrored in the OpenAPI schema
  (`multipleOf: 0.01`) so the Schemathesis contract stays 1:1.
- **Hash-amplification DoS + PIN disk-spool.** The 413 body guard above.
- **Stored-response cap.** A > 64 KB 2xx is not persisted for replay (a retry then
  gets 409 `RESULT_UNKNOWN`); bounds the stored row and the replay buffer.
- **`IsDuplicateKey` narrowing.** Only an InMemory duplicate-PK `ArgumentException`
  (matched by message) is treated as a lost claim race; any other `ArgumentException`
  now propagates instead of being masked as a false 409.
- **Immutability guard on the SaveChanges funnels.** Moved onto `SaveChanges(bool)`
  / `SaveChangesAsync(bool, ct)` so a direct bool-overload call can no longer bypass
  the write-once Transaction guard.
- **Dev CORS.** The development policy now reflects only **loopback** origins (any
  localhost port) instead of any origin + credentials.
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

- ADR-0003 (Argon2id) — the reason `RequestHash` must be keyed.
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
