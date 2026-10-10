# ADR-0030: Running the app's own data layer against the real backend

**Status:** Accepted · **Date:** 2026-08-04 · **Amended:** 2026-08-13 (decision 7, ADR-0041) ·
**Decision Makers:** Vladislav Aleshaev

## Context

The contract suite of ADR-0029 reads the raw wire with a plain fetch client, on purpose: it sees
what the server sent. Between the wire and the screen sit `problemBaseQuery` (it synthesises error
codes the wire never carries), `unwrap` with the spec-generated Zod schemas (they can reject a
valid 200), the idempotency protocol and the step-up interceptor; and the production store adds
`sessionMiddleware`, which owns the global 401 rule (ADR-0019, D3). A backend can be correct and
the app still broken, and with MSW as the only oracle three things cannot be falsified: that real
payloads satisfy the app's strict schemas, that a replay is detected end to end, and that step-up
elevates and re-sends the original request.

## Decision

1. **A third suite, `src/integration/`, runs the app's own data layer against the real stack**: a
   real Redux store over the real `apiSlice` dispatches real endpoints, because drift lives in what
   the app makes of an answer as well as in the answer. No production code knows the suite exists.
2. **The data layer is pointed at the backend by the jsdom document URL, not by a code change**:
   the config sets `environmentOptions.jsdom.url` to `http://localhost:5000`, because
   `problemBaseQuery` pins `baseUrl` to `window.location.origin`. No production line changes.
3. **There is no mock target, and the suite fails when the stack is down; it never skips**,
   because the real server is the point, and a skipped suite reports success having asked nothing.
4. **It is excluded from `npm test` and runs through `npm run test:integration`**, because a plain
   unit run must never require a live backend.
5. **One shim, for the runtime and not for the backend: `setup.ts` stores `Set-Cookie` and sends
   it back, to the BFF's origin only**, because Node's `fetch` has no cookie jar and the session
   cookie is HttpOnly: every authenticated request would answer 401. It invents no response.
6. **The step-up "modal" is a test double for the UI component, not for the server**: the harness
   performs the same real `verify-pin` call the modal performs and settles the same controller
   promise, so what runs underneath is production: a real 403, a real PIN verified against SQL, a
   real replay. It reads `data.verified`, because a rejected PIN is a `200` with `verified: false`.
7. **Step-up is exercised through the reveal, `GET /api/accounts/{id}/full-number`, not through a
   transfer**, because it is the only route the BFF gates at level 2 (ADR-0041; a transfer carries
   its authorisation in-band, ADR-0042) and no money moves, so the suite can run at will.

## Rejected

- Rejected: running this layer against MSW, because MSW is the oracle the layer exists to check.
- Rejected: a store built from `apiSlice` alone, because it cannot observe the global 401 rule and
  the suite would pass with the rule broken: the harness mirrors `src/app/store.ts`.
- Rejected: asserting the signed-out path inside a signed-in file, because the cookie jar is shared
  by the file: it lives in `anonymous.integration.test.ts`, which asserts its jar is empty.
- Rejected: one jar for every origin, because a foreign origin then receives the session cookie.

## Consequences

- The suite proves what neither side can alone: real payloads pass the strict money schemas; the
  bare paginated shape is still bare; `VALIDATION_ERROR` is synthesised and a real `errorCode`
  carried through; a repeated key returns the same receipt and moves money once; step-up elevates,
  replays the original request once, and sticks; a 401 while authenticated expires the session
  and empties the cache (D3).
- Order is load-bearing: elevation is server-side session state, so cancel runs first and
  "elevation stuck" last, and the D3 test is last in its file because it destroys the session.
- The first 401 after a session dies does not reject: `sessionMiddleware` resets the cache while
  the request settles, so `unwrap()` resolves with `undefined`. The test pins that, then fires a
  second request that proves the server answers `401 AUTH_TOKEN_MISSING`.
- The BFF limits auth to 10 requests per 60 s per IP and a run spends about 5: `fileParallelism`
  is off, login is once per file, and `signIn` turns a 429 into an explicit message.
- Not covered: a successful transfer's own request and response shapes, and the rows of a
  transaction page: the paginated shape is asserted, not its content.
- Not covered: React. A page that mis-renders a correct payload, and the step-up modal itself,
  belong to the browser suite (ADR-0031).
- Not covered: the 5xx branches of `problemBaseQuery` (`NETWORK`, `PARSE`, the retry policy,
  `HTTP_502`): a healthy stack cannot produce them, and they stay unit-tested only.
- Not covered: isolation between runs. The idempotency test deposits €1.00 per run, so every
  assertion is relative to a balance read just before; and the auth budget is per IP, so a third
  run inside one minute fails. In CI the suite runs against a stack the job owns (ADR-0032).

## Verified by

- `npm run test:integration`: the five files of `frontend/src/integration/` (`readPath`,
  `errorPath`, `money`, `anonymous`, `cookieScope`, each `.integration.test.ts`).

## Related

ADR-0019, ADR-0029, ADR-0031, ADR-0032, ADR-0041, ADR-0042.
