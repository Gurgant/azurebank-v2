# ADR-0022: Client-side money-mutation protocol

**Status**: Accepted

**Date**: 2026-07-25

**Decision Makers**: Vladislav Aleshaev

---

## Context

ADR-0009 specifies the **server** side of idempotency exhaustively: a keyed HMAC over the raw
request body bytes, a five-state protocol, TTLs, and the replay header. It gives the client one
neutral sentence (`0009:182` — "Frontend (R3) must generate a UUID per user-intent and reuse it
across retries"). ADR-0008 puts the auth level in the BFF session rather than the JWT, which makes
step-up a transport concern rather than a token concern.

Between them they leave the client an obligation nobody wrote down: **for each way a request can
end, does the server possibly hold this key, or is the key spent?** Getting one cell of that table
wrong moves a customer's money twice, and none of these failures is visible at code-review time —
the code looks fine, the tests that would catch it are the ones you have to know to write.

The table did exist, in a planning document outside the repository. Two comments in shipped code
point at it: `frontend/src/hooks/useIdempotentMutation.ts:11` cites "KEEP outcomes (DECISIONS
§2.3)" and `:29` cites "§2.3" again. That document is being archived. This ADR is the client half
of ADR-0009, written into the repository so the pointers have somewhere real to land.

## Decision

1. **One key per user-intent.** `useIdempotentMutation` mints a lazy `crypto.randomUUID()` into a
   ref on the first submit. It lives in memory only — never Redux, never `sessionStorage`, and it
   is never minted inside `baseQuery`, inside an endpoint definition, or on form mount. A key
   created before the user has expressed an intent is not tied to an intent.

2. **KEEP the key** — a user-driven Retry re-sends it byte-identically — on `409
   IDEMPOTENCY_IN_FLIGHT`, on `NETWORK`, on `PARSE`, and on any `5xx`. In every one of these the
   server may have recorded the key, or may be recording it right now. Re-sending with a fresh key
   is not a retry; it is a **client-manufactured double-spend**.

3. **DROP the key** on `2xx`, on business `4xx`, on `401 INVALID_PIN`, on `422
   IDEMPOTENCY_KEY_REUSE`, and on `400 KEY_MISSING`/`KEY_INVALID`. The server has answered
   definitively; the intent is finished or was never admissible.

4. **`409 IDEMPOTENCY_RESULT_UNKNOWN` drops the key *and* latches `verifyRequired`.** Submit
   refuses to mint a new key until the **owning flow** — never the generic hook — has refetched
   recent transactions and the user has explicitly said it did not go through, which calls
   `resetIntent`. Arming a new key automatically here resubmits money the server may already have
   moved, which is the exact outcome the whole protocol exists to prevent.

5. **Routing is on `errorCode`, never on HTTP status.** Two opposite 409s (`IN_FLIGHT` versus
   `RESULT_UNKNOWN`) demand opposite behaviour; status-based handling collapses them into one and
   picks the wrong one half the time.

6. **Any body-affecting form edit calls `resetIntent`.** An edited body under the old key is a byte
   fingerprint mismatch and comes back `422`. The edit is a new intent, so it gets a new key.

7. **`keyRetained` blocks dismissal while any key is live** — not merely while a request is in
   flight. Abandoning a KEPT key and reopening the dialog mints a fresh key, which is a new intent
   over the same money.

8. **Step-up replay is byte-identical and happens at baseQuery level.** After a successful PIN
   elevation the wrapper replays the **original serialized `FetchArgs`** exactly once: same body
   bytes, same `Idempotency-Key`. It is never a hook-level re-trigger that rebuilds the payload —
   re-serializing an equivalent object with a different key order changes the fingerprint, returns
   `422 IDEMPOTENCY_KEY_REUSE`, and strands the user's payment. At most one elevation plus one
   retry; N parallel 403s collapse behind an async mutex, each queued request replaying its own
   stored args.

9. **Two PIN transports, one `PinInput` component.** INTERCEPTOR mode (a 403 with
   `X-Auth-Level-Required` opens the modal, then the request replays) and BODY mode (a masked
   `autocomplete="one-time-code"` field whose value travels inside the request body). BODY mode is
   **withdraw only**. _Stale reason, corrected 2026-08-16:_ this said a transfer's PIN "merely
   raises the session level". Since ADR-0041 a transfer's PIN travels in the body too, and is
   therefore equally part of the HMAC-hashed payload. The rule survives its reason — BODY mode is
   still withdraw-only on the client — and ADR-0042 is what will retire both transports for
   transfers, replacing them with an authorisation carried in a header. A third pattern is not
   permitted: a new PIN surface picks one of these two. `hasPin: false` routes to set-pin onboarding rather than failing at submit.

10. **Never display an attempts-remaining counter.** No such field exists anywhere in the contract.
    A "2 attempts left" message would be fabricated data on a security surface. `429 PIN_LOCKED`
    with its `retryAfterSeconds` is the only real signal.

11. **Zero optimistic updates on money.** No `updateQueryData` hand-patching of balances or of the
    paginated transaction list, no `useOptimistic`. Correctness comes from tag invalidation and
    refetch. In a bank a briefly-wrong balance is a correctness failure, not a UX blemish.

The interceptor applies to **every level-2 endpoint**, stated as a rule rather than as a count. An
earlier formulation named "exactly the two transfer endpoints"; ADR-0020 has since added
`GET /api/accounts/{id}/full-number` ~~as a third~~ (struck 2026-09-04; note below), riding the same
`baseQueryWithStepUp`.

*(Correction 2026-09-04: since ADR-0041 (dd84179, 2026-08-13) the reveal is not the third level-2
endpoint but the only one — transfers left the gate and carry an authorisation in a header
(ADR-0042). "Every level-2 endpoint" still holds and is now satisfied by one route; the replay path
is exercised by the reveal alone, as `stepup.test.ts` and `money.integration.test.ts` already
say. The "third level-2 surface" line under Related is read the same way.)*

## Alternatives considered

**Auto-retry on 5xx and network errors.** Rejected: it converts a possible single spend into a
probable double spend, and it removes the human from the one decision that needs a human.

**Minting the key inside `baseQuery`.** Superficially tidier, and wrong: `baseQuery` sees requests,
not intents. A retry and a resubmission are indistinguishable at that layer.

**Persisting keys in `sessionStorage`** so a reload could resume an intent. Rejected: it makes
unsubmitted financial intent survive a session boundary, which is the data-loss policy's exact
prohibition, and a stale key resurfacing days later is worse than a lost draft.

**Unifying withdraw into the interceptor** so there is one PIN path. Rejected: withdraw's PIN is
inside the hashed body, so moving it to the session level changes the fingerprint and breaks
replay. Two transports is the smaller cost.

## Residuals (accepted, documented)

- **The client cannot distinguish "server never saw it" from "server saw it and died".** KEEP is
  the safe answer for both, which means a genuinely-lost request occupies its key until TTL
  expiry. Correctness over availability, deliberately.
- **`RESULT_UNKNOWN` puts a verification burden on the user.** The flow asks them to look at their
  own transactions and decide. There is no server endpoint that answers "did key X land?", and
  inventing a client-side guess would be worse than asking.
- **The mutex bounds elevation to one retry.** A second 403 after a successful elevation surfaces
  as an error rather than looping. If that is ever seen in the wild it means the session level is
  not sticking, which is a BFF bug and must be diagnosed there, not papered over with more retries.
- **Nothing here protects against two browser tabs.** Each tab mints its own key for its own
  intent, which is correct per-intent behaviour and does not stop a user paying twice on purpose.
  Server-side velocity limits are the control for that, and they are backlog.

## Consequences

**Positive** — every monetary mutation goes through one audited hook whose state machine is
written down; the six behaviours below are pinned by tests, so a regression fails CI rather than
reaching a customer; a reviewer has something to check a diff against.

**Negative** — the hook is more complex than a bare mutation and every new money surface must
adopt it rather than calling the endpoint directly; the flow component, not the hook, must own the
`RESULT_UNKNOWN` verify step, which is a responsibility that is easy to forget when adding a
surface.

**Forbidden from here on**: minting keys in `baseQuery`, in an endpoint, or on form mount; keys in
Redux or Web Storage; automatic retry of mutations; rebuilding a request after elevation instead
of replaying stored args; a fresh key on a post-PIN retry; folding withdraw into the interceptor;
displaying an attempts counter; optimistic patching of any money-bearing cache entry.

## Verification

Pinned by `frontend/src/api/policies.test.tsx`, `frontend/src/mocks/idempotency.test.ts`,
`frontend/src/mocks/stepup.test.ts`, `frontend/src/mocks/withdrawHandler.test.ts` and
`frontend/src/features/auth/stepup-interceptor.test.tsx`. Deleting any of these tests is
a reversal of this decision, not a test cleanup. The six behaviours they hold:

1. The same key survives a 503 and a user-driven Retry; a new key appears only after a 422 plus an
   edit.
2. GET retries on 503; POST never does.
3. A ProblemDetails `traceId` reaches the UI as a normalized `ApiProblem`.
4. Step-up replay carries a byte-identical body and the same key.
   *(Corrected 2026-08-12: `stepup-interceptor.test.tsx` was missing from the list above, and until
   that date it recorded only the `Idempotency-Key` header — the body half of this sentence was
   asserted nowhere. The key alone cannot distinguish "the same request, replayed" from "the same
   key with an edited body", which is the one case the server answers 422 `IDEMPOTENCY_KEY_REUSE`
   for. Both halves are now asserted, and the body is compared as raw text: two parsed objects
   would match even if the property order changed, which the server's HMAC fingerprint would not.)*
5. Withdraw with a wrong PIN does **not** dispatch `sessionExpired`, and drops the key.
6. `IN_FLIGHT` keeps the key across a Retry, while `RESULT_UNKNOWN` forces the verify dialog before
   any new key can exist.

## Related

- **ADR-0008** — auth levels live in the BFF session, which is what makes step-up a transport
  concern.
- **ADR-0009** — the server protocol. This ADR is its client half; read them together.
- **ADR-0019** — the SPA/BFF error channel this protocol routes on.
- **ADR-0020** — ~~the third~~ the only (since ADR-0041; struck 2026-09-04) level-2 surface, and the
  reason the interceptor rule is stated as "every
  level-2 endpoint" rather than a count.
