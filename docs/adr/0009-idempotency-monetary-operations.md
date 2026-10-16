# ADR-0009: Idempotency for Monetary Operations

**Status:** Accepted · **Date:** 2026-07-13 · **Amended:** 2026-09-21 (ADR-0056), 2026-09-24
(decision 7), 2026-09-30 (ADR-0058), 2026-10-01 (decision 6), 2026-10-03 (the outcome table),
2026-10-05 (decision 7), 2026-10-06 (decision 7), 2026-10-11 (the outcome table), 2026-10-16 (the
consequences) · **Decision Makers:** Vladislav Aleshaev

## Context

The four monetary endpoints (deposit, withdrawal, external and internal transfer) move balances, and
a retry after a timeout, a double click or a replay would execute an operation twice. It must never
execute more than once: not under concurrent identical requests, not after a crash, not when EF's
retry re-runs work whose commit acknowledgement was lost. That wins over availability. A crash
before the commit and a commit whose response was lost demand opposite recoveries.

## Decision

1. **Wire contract.** `Idempotency-Key`, a UUID, is required on all four: missing is 400
   `IDEMPOTENCY_KEY_MISSING`, malformed 400 `IDEMPOTENCY_KEY_INVALID`. A key's record lives 24 h,
   per user and per endpoint, which is named from route metadata because raw paths would split it.
2. **Storage: the primary key is the lock.** `IdempotencyRecords` is keyed by user, endpoint and
   key, so one concurrent claim INSERT wins and the loser re-reads. `ClaimId` fences every UPDATE
   and DELETE, so a commit that EF's retry re-runs, or a stale claimant, matches no row.
3. **The fingerprint is keyed (H10).** `RequestHash` is HMAC-SHA256 of the raw body under
   `Idempotency:HashKey`, because every body is low-entropy and mostly known: an unkeyed hash would
   let a reader of the table confirm guesses about what somebody moved and to whom.
4. **Lifecycle: `Processing` → `Executed` → `Completed`.** The claim is committed at once. Its
   record is then marked `Executed`, and `ClaimId` rotated, without saving, so the flip commits with
   the first business save, atomically with the money: the record proves whether the operation
   committed. A 2xx is stored before its first byte is sent, within 3 s, unless empty or over 64 KB.
5. **A retried attempt starts from the database**, because EF's retry, re-running a transfer on the
   same context, once re-applied a failed attempt's pending work and re-executed a transfer whose
   commit acknowledgement was lost: each attempt resets the tracked work and re-reads its record.
6. **`applied: true` only from a database read.** The 409 `IDEMPOTENCY_RESULT_UNKNOWN` says it when
   the request that answers reads the record as committed under the hash it claimed with, because
   `Executed` reaches the database only with the commit that moves the money. It never says `false`.
7. **Placement & limits.** The middleware runs after `UseAuthorization`, so a 401 or a 403 creates
   no record. A body is capped at 32 KB by `[EndpointRequestSizeLimit(32_768)]`, and the middleware
   answers 413 `IDEMPOTENCY_PAYLOAD_TOO_LARGE` before it buffers, hashes or claims, after discarding
   up to 1 MiB for up to 5 s (`OversizedBodyDrain`), because a body left unread made Kestrel abort
   the connection under the BFF. The four step-up mints answer 413 `PAYLOAD_TOO_LARGE` likewise.

| What happened | The record | The same key then gets (the client's half: ADR-0022) |
|---|---|---|
| It succeeded and the answer was stored | `Completed` | the stored status, body and content type, with `Idempotency-Replayed: true` |
| The key comes with another body | any | 422 `IDEMPOTENCY_KEY_REUSE` |
| It was refused before the commit (400, 401, 404, 422) | `Processing`, deleted under the fence within 3 s | a new execution: the key stays reusable, and an error is never replayed |
| It is still running, or committed with its answer not yet stored | fresh `Processing` or `Executed` | 409 `IDEMPOTENCY_IN_FLIGHT`; the server does not wait |
| It crashed before the commit | `Processing` older than `ProcessingStaleAfter`, 2 minutes | a takeover: a fenced delete and a new claim, since nothing was committed |
| It committed and the answer was lost | `Executed` past the same age | 409 `IDEMPOTENCY_RESULT_UNKNOWN` with `applied: true`: not to be sent again under a new key. At 24 h the row is swept with a Warning, for reconciliation |
| Inside one request, a retried attempt re-reads its record and finds it gone, or replaced by one claimed with another body | none, or another hash | that request gets 409 `IDEMPOTENCY_RESULT_UNKNOWN` without `applied`; the detail says which |
| The database is unreachable, or the deadline passed | as it was | 503 `SERVICE_UNAVAILABLE`, with `applied: false` only when the request owns its claim and started no commit (ADR-0058) |
| A deposit, a withdrawal or a transfer lost an account's row to another write on each of its 8 attempts, and its record is still its own claim | `Processing`, deleted under the fence when the release lands within its 3 s | that 503 with a wait of 1 s, and `applied: false` under the same rule. When the release lands, the same key is a new execution, which is the write itself, not a replay, and when it is made it spends the step-up authorisation the 503 left unspent, where the write has one; otherwise the key stays claimed until the claim is stale (ADR-0058 D9) |

## Rejected

- Rejected: two states and delete-on-error, because an error or the TTL can revive a committed key.
- Rejected: a lock service or SQL application locks, because the primary key already gives the lock.
- Rejected: hashing canonical JSON, because raw bytes need no parser and a real retry resends them.
- Rejected: a 500 when the answer cannot be stored after a 2xx, because the operation did succeed.
- Rejected: a key on `create-account`, because it moves no money and its duplicates are visible.

## Consequences

- One execution under 24 identical parallel requests, in memory and on SQL Server; balances exact.
- It costs an INSERT and an UPDATE per request, and one secret the API refuses to start without.
- Not covered: after 24 h a forgotten key executes again, so a retry that late moves money twice.
- Not covered: a key whose answer was lost stays 409 until then: correctness over availability.
- A movement names its request. Each ledger row a money request writes on the caller's own accounts
  carries its `Idempotency-Key`: a deposit's row, a withdrawal's, a transfer's outgoing row, and
  both rows of a move between the caller's own accounts. `GET /api/transactions?IdempotencyKey=`
  lists them, inside the caller's open accounts as every listing, so another user's key gives an
  empty page, and every answer that shows a row carries its key as `idempotencyKey`: the listing,
  a row read by its id, and a deposit's or a withdrawal's 201. The payee's row of a transfer to another user carries none: the payee is never
  handed the sender's key. The key stays on the rows for as long as they exist, after its record
  has expired. Rows written by an app without this change carry none: the demo's seeded history,
  and the rows of the app of before when a failed check puts it back on the new schema (ADR-0061).
- Not covered: the `applied: true` answer names no transaction; the listing by key finds it. A key
  that no row carries is evidence that nothing was applied only once the claim is stale, 2 minutes
  after it was made, or its record has expired, since until then the request may still commit. The
  2 minutes rest on the request deadline of ADR-0058, which a frozen API cannot enforce (its
  residual risk 4); the same key sent again once the claim is stale is fenced, and applies the
  money at most once. The listing does not show a movement on an account closed afterwards, and a
  key is unique per endpoint and for 24 h only, so one key can list the rows of more than one
  request.
- Not covered: a replay restores status, body and content type, and no header a 201 may gain later.
- Not covered: the claim precedes model validation, so an invalid body costs an INSERT and a DELETE.
- Not covered: the retry jitter is fixed, and the clustered key leads with a Guid (fragmentation).

## Verified by

`IdempotencyConcurrencyTests`, `IdempotencySqlServerConcurrencyTests`, `IdempotencyEndpointTests`,
`IdempotencyServiceTests`, `TransferTransientRetrySqlServerTests`, `ProvenCommitSourceTests`,
`KestrelRequestSizeLimitTests`, `OversizedBodyDrainTests`, `MintOversizedBodyDrainTests`,
`BalanceConcurrencySqlServerTests`, `BusyMoneyWriteSqlServerTests` (a busy account: the record
is released, and the same key is a new execution), `IdempotencyCleanupServiceTests` with
`IdempotencyCleanupSqlServerTests` (the clean-up: what it deletes and what it keeps), and
`IdempotencyKeyOnTheLedgerSqlServerTests` with `TransactionsByIdempotencyKeyTests` (the key on the
caller's own rows and never on the payee's, and the listing by it).

## Related

ADR-0018, ADR-0022, ADR-0042, ADR-0056, ADR-0058, ADR-0059.
