# ADR-0022: Client-side money-mutation protocol

**Status:** Accepted · **Date:** 2026-07-25 · **Amended:** 2026-08-12, 2026-08-16 (ADR-0041,
ADR-0042), 2026-09-04, 2026-09-15, 2026-10-01 (ADR-0059) · **Decision Makers:** Vladislav Aleshaev

## Context

ADR-0009 specifies the server side of idempotency: a keyed HMAC over the raw request body bytes, a
five-state protocol, TTLs and the replay header. It gives the client one sentence: generate a UUID
per user-intent and reuse it across retries. ADR-0008 puts the auth level in the BFF session, which
makes step-up a transport concern. Between them the client is left one question for each way a
request can end: does the server possibly hold this key, or is the key spent? One wrong cell of that
table moves a customer's money twice, and none of these failures is visible at code-review time.
This record is the client half of ADR-0009; `frontend/src/hooks/useIdempotentMutation.ts` cites it.

## Decision

1. **One key per user-intent.** `useIdempotentMutation` mints a lazy `crypto.randomUUID()` into a
   ref on the first submit. It lives in memory only: never Redux, never `sessionStorage`, and never
   minted inside `baseQuery`, inside an endpoint definition or on form mount, because a key created
   before the user has expressed an intent is not tied to an intent.
2. **KEEP the key**, so that a user-driven Retry re-sends it byte-identically, on `409
   IDEMPOTENCY_IN_FLIGHT`, on `NETWORK`, on `PARSE` and on any `5xx`, because in each the server may
   have recorded the key or be recording it: a fresh key there is a client-made double-spend. A
   refused authorisation (`AUTHORIZATION_EXPIRED`, `AUTHORIZATION_INVALID`) keeps it too (ADR-0042).
3. **DROP the key** on `2xx`, on a business `4xx`, on `401 INVALID_PIN`, on `422
   IDEMPOTENCY_KEY_REUSE` and on `400 KEY_MISSING`/`KEY_INVALID`, because the server has answered
   definitively. Not in the list: a 409 on a money send whose body names no code (`HTTP_409`, read
   from the status) latches the check as `RESULT_UNKNOWN` does, because it may be an `IN_FLIGHT`.
4. **`409 IDEMPOTENCY_RESULT_UNKNOWN` drops the key and latches `verifyRequired`.** Submit refuses
   to mint a new key until the owning flow, never the generic hook, hears the user say it did not go
   through, which calls `resetIntent`, because a new key armed automatically resubmits money the
   server may already have moved. On this code the four money mutations invalidate the tags their
   success invalidates. When the 409 says `applied: true` the commit is proven: the flow says the
   payment went through and offers the history, and `resetIntent` does nothing, so no new key can
   exist in that page or dialog.
5. **Routing is on `errorCode`, never on HTTP status**, because two opposite 409s (`IN_FLIGHT` and
   `RESULT_UNKNOWN`) demand opposite behaviour and a status collapses them into one.
6. **A body-affecting form edit calls `resetIntent` only while no key is held**, because an edited
   body under the old key is a fingerprint mismatch (`422`) and the edit is a new intent. While a
   key is retained (the last outcome unknown, or its authorisation refused) an edit latches
   `verifyRequired` instead (`requireVerify`, which drops the key), because a new key there is a new
   intent while the first may still land (ADR-0059). The transfer pages disable their form while a
   key is live.
7. **`keyRetained` blocks dismissal while any key is live**, not only while a request is in flight,
   because abandoning a kept key and reopening the dialog mints a fresh key over the same money.
8. **Step-up replay is byte-identical and happens at `baseQuery` level.** After a PIN elevation the
   wrapper replays the original serialized `FetchArgs` exactly once, same body bytes and same
   `Idempotency-Key`, and never rebuilds the payload at hook level, because a different key order
   changes the fingerprint and returns `422 IDEMPOTENCY_KEY_REUSE`. At most one elevation and one
   retry; parallel 403s collapse behind an async mutex, each replaying its own stored args. The
   interceptor applies to every level-2 endpoint, which today is one: the reveal,
   `GET /api/accounts/{id}/full-number` (ADR-0020), transfers having left the gate (ADR-0041).
9. **Two PIN transports, one `PinInput` component.** INTERCEPTOR mode: a 403 with
   `X-Auth-Level-Required` opens the modal, then the request replays. BODY mode (the PIN inside the
   request body, withdraw only): withdrawn, see ADR-0056; a transfer and a withdrawal carry a minted
   authorisation in a header (ADR-0042). `hasPin: false` routes to set-pin onboarding.
10. **No attempts-remaining counter is displayed**, because no such field exists in the contract and
    a "2 attempts left" would be fabricated data on a security surface. `429 PIN_LOCKED` with its
    `retryAfterSeconds` is the only real signal.
11. **Zero optimistic updates on money**: no `updateQueryData` patching of balances or of the
    transaction list and no `useOptimistic`, because in a bank a briefly wrong balance is a
    correctness failure. Correctness comes from tag invalidation and refetch.

## Rejected

- Rejected: automatic retry on 5xx and network errors, because it turns a possible single spend into
  a probable double spend and removes the human from the one decision that needs one.
- Rejected: minting the key inside `baseQuery`, because `baseQuery` sees requests, not intents: a
  retry and a resubmission are indistinguishable there.
- Rejected: keys in `sessionStorage` so that a reload resumes an intent, because unsubmitted intent
  would survive a session boundary, and a stale key days later is worse than a lost draft.
- Rejected: a client-side guess at whether a key landed, because it is worse than asking.

## Consequences

- Every monetary mutation goes through one hook whose state machine is written down. It costs a
  hook more complex than a bare mutation, which every new money surface must adopt.
- The flow component, not the hook, owns the `RESULT_UNKNOWN` verify step and the went-through view.
  It tests `wentThrough` before `verifyRequired`, since the first is never set without the second.
- Not covered: "the server never saw it" and "the server saw it and died" look the same. KEEP is the
  safe answer for both, so a lost request occupies its key until the TTL: correctness first.
- Not covered: no endpoint answers "did key X land?". A record read as committed answers in the 409
  itself (`applied: true`); otherwise the user is asked to check: with the flag absent, a rejection
  with no HTTP status, a 409 naming no code, or an edit with a key held. When the record has
  vanished another request may be about to commit, and "start over" mints a new key.
- Not covered: the mutex bounds elevation to one retry. A second 403 after an elevation surfaces as
  an error: the session level is not sticking, which is a BFF defect to diagnose there.
- Not covered: two browser tabs. Each mints its own key for its own intent; server-side velocity
  limits are the control for paying twice on purpose, outside this record.
- Not covered: the body and key halves of decision 8's replay have no live caller, since the only
  route replayed is the bodiless, keyless reveal. The test keeps the interceptor general.

## Verified by

`policies.test.tsx`, `idempotency.test.ts`, `stepup.test.ts`, `withdrawHandler.test.ts` and
`stepup-interceptor.test.tsx` under `frontend/src`; for the `applied: true` case of behaviour 6 also
`useMoneyWizard.blocker.test.tsx` and `transfer-went-through.test.tsx`. Deleting one of them
reverses this decision. The six behaviours they hold:

1. The same key survives a 503 and a user-driven Retry; a new key appears only after a 422 and an
   edit.
2. GET retries on 503; POST never does.
3. A ProblemDetails `traceId` reaches the UI as a normalized `ApiProblem`.
4. Step-up replay carries a byte-identical body and the same key; the body is compared as raw text.
5. Withdraw with a wrong PIN does not dispatch `sessionExpired`, and drops the key.
6. `IN_FLIGHT` keeps the key across a Retry; `RESULT_UNKNOWN` forces the verify dialog before any
   new key can exist, and with `applied: true` no new key can exist in that page or dialog at all.

## Related

ADR-0008, ADR-0009, ADR-0019, ADR-0020, ADR-0041, ADR-0042, ADR-0056, ADR-0059.
