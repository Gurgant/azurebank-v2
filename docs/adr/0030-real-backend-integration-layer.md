# ADR-0030: Running the app's own data layer against the real backend

**Status**: Accepted. Phase 2 of the test-layer plan opened in ADR-0029.

**Date**: 2026-08-04

**Decision Makers**: Vladislav Aleshaev

---

## Context

ADR-0029 built a contract suite that runs one set of assertions twice — against MSW and against the
real API + BFF. It uses a raw fetch client **on purpose**, so that what it observes is what the
server actually sent, unmediated by the app.

That leaves a second gap, and it is not the same gap. Between the wire and the screen sit
`problemBaseQuery` (which synthesises error codes the wire never carries), `unwrap` + the
spec-generated Zod schemas (which can reject a perfectly valid 200), the idempotency protocol, and
the step-up interceptor. A backend can be entirely correct and the app still broken, and until this
ADR every test covering those layers ran against MSW — the very oracle ADR-0029 was written because
we could not trust it.

There is a third divergence, narrower but sharper: the store itself. Production wires
`auth: authReducer` plus `sessionMiddleware` (`src/app/store.ts`), and `sessionMiddleware` owns the
global 401 rule (D3) — a 401 while authenticated dispatches `sessionExpired()` and resets the whole
RTK Query cache, so financial data cannot outlive the session it was fetched under. A store built
from `apiSlice` alone cannot observe any of that.

Concretely, three things are unfalsifiable from either side alone:

- **Does the real payload satisfy the app's own strict schemas?** The money surfaces validate
  fail-closed in every environment. If the API renames a field, the contract suite still passes (the
  server answered correctly) and the app throws.
- **Does replay detection work end to end?** `replayed` is read from an `Idempotency-Replayed`
  header. A unit test with MSW decides both sides of that question and can only agree with itself.
- **Does step-up actually elevate and replay?** It needs a real 403, a real PIN, a real session, and
  the ORIGINAL request bytes re-sent.

## Decision

**1. A third suite: `src/integration/`, the app's own data layer against the real stack.** It
constructs a real Redux store over the real `apiSlice` and dispatches real endpoints. Nothing about
the production code is aware the suite exists.

**2. The data layer is pointed at the backend by the jsdom document URL, not by a code change.**
`problemBaseQuery` pins `baseUrl` to `window.location.origin`; the config sets
`environmentOptions.jsdom.url` to `http://localhost:5000`. This is why no production line needed
touching, and why the suite cannot drift from what the browser does.

**3. There is no mock target, and the suite FAILS rather than skipping when the stack is down.**
Unlike the contract suite, "run it against MSW" is not a meaningful option here — the entire point
is the real server. A skipped suite reports success without having asked anything.

**4. It is excluded from `npm test`.** CI and a plain unit run must never require a live backend.
It runs through `npm run test:integration`.

**5. One shim, and it is a shim for the RUNTIME, not for the backend.** Measured against the running
stack before writing a line of it:

    login             -> 200, set-cookie: .AzureBank.Session=…; path=/; samesite=strict; httponly
    document.cookie   -> ""            (HttpOnly, and undici feeds nothing to jsdom's jar)
    GET /api/accounts -> 401 AUTH_TOKEN_MISSING

Node's `fetch` has no cookie jar at all, so without one every authenticated request would 401 and
the suite would be an elaborate way of testing the signed-out path. `setup.ts` stores `Set-Cookie`
and sends it back — exactly the one browser behaviour the runtime lacks, and nothing else. It never
invents a status, header or body.

**6. The step-up "modal" is a test double for the UI COMPONENT, not for the server.** When the
interceptor asks for elevation, the harness performs the same real `verify-pin` call the modal
performs and settles the same controller promise. What runs underneath is production: real 403, real
PIN verification against SQL, real replay.

**7. The reveal endpoint is the step-up probe, not a transfer.** The BFF gates
`/api/accounts/{id}/full-number` at level 2 through the identical code path as `/api/transfers`, so
the interceptor is exercised the same way — but no money moves, so the suite can run as often as it
likes and its assertions stay about the protocol.

## Consequences

**Sixteen assertions across five files, all green against the real stack, run twice.** What they buy:

| Property | Why nothing else could prove it |
|---|---|
| Real payloads satisfy the STRICT money schemas | The contract suite never runs `unwrap` or Zod |
| The BARE paginated shape is still bare | An added envelope would look like empty history |
| `VALIDATION_ERROR` synthesis | A code the wire never carries — invented above it |
| A real `errorCode` is carried through untouched | Distinguishes carrying from synthesising |
| Replay returns the same receipt AND moves money once | Needs the app to reuse a key and the API to honour it |
| Step-up elevates and replays the original request | Needs a real 403 + real PIN + real session |
| Elevation sticks | Proves the previous test elevated rather than got a pass |
| **The D3 global-401 rule** | Needs the production STORE, a real session, and a real 401 |
| The harness's own cookie jar is origin-scoped | A leak in the shim would be invisible from the product |

**The suite was falsified before being trusted.** A schema mutation (`__mutant` added to
`AccountResponse`) turned it red with a ZodError whose stack runs
`unwrap (envelope.ts:26)` ← `transformResponse (apiSlice.ts:121)` ← RTK Query, which is the proof
that the production path — not a test-local copy of it — is what runs. Reverted immediately after.

**Signed-out has to be a property of the FILE.** The cookie jar is module state shared by every test
in a file, so a "fresh store" inside a signed-in file is not anonymous and its call simply succeeds.
The first draft put that assertion in `errorPath` and it failed with `expected true to be false` for
exactly that reason. It now lives in `anonymous.integration.test.ts`, which never signs in, and
carries a negative control asserting the jar is empty — without it, every assertion in that file
would also pass while signed in.

**Order is load-bearing in the money file, and that is stated rather than implied.** Elevation is
server-side session state: once the elevate test runs, later reveals answer 200 with no 403 at all.
Cancel therefore runs first and "elevation stuck" runs last. The same applies to the D3 test, which
is last in its file because it destroys the session that file signed in with.

**The first 401 after a session dies does not surface as an error to the caller, and that took
measuring to believe.** Two drafts of the D3 test asserted the triggering query rejects; both went
green against a backend that had plainly answered 401 — `auth` had already flipped to `expired`.
What happens is that `sessionMiddleware` dispatches `resetApiState()` while that very request is
settling, wiping the cache entry the promise is about to read, so `unwrap()` resolves with
`undefined` rather than rejecting. (A forced refetch behaves differently again: RTK Query keeps the
stale `data`, so it resolves with the OLD value.) This is the rule working as intended — the global
handler takes the session over and the unlucky component has nothing useful to render — but it means
"did the 401 surface?" is the wrong question to ask of that first request. The test therefore pins
the resolve-with-`undefined`, then fires a SECOND request as the control that proves the server is
really answering `401 AUTH_TOKEN_MISSING`.

**The cookie jar is scoped to the BFF, and that was worth doing before it was reachable.** Every
request the suite makes today goes to the BFF, so nothing leaked — but that is a property of the
current test list, not of the shim, which patches `globalThis.fetch` process-wide. Measured with the
check removed: a foreign origin received the real `.AzureBank.Session` value AND its own
`Set-Cookie` entered the jar, from where it would have been replayed at the BFF. Both directions are
now asserted by `cookieScope.integration.test.ts`, which seeds a fake cookie rather than signing in
so the guard costs nothing against the auth budget.

**A hard operational limit, measured rather than guessed.** The BFF rate-limits auth to 10 requests
per 60s per IP, and the suite spends about 5 per run (three logins, two PIN verifications). Two
consecutive runs fit; **the third inside the same minute fails**, confirmed by running it. `signIn`
turns that 429 into an explicit message rather than letting it read as contract drift. This is the
main reason `fileParallelism` is off and login is once per file.

**What is NOT covered.** Honest floor, not a ceiling:

- **Transfers are untested here** — both `/api/transfers` and `/api/transfers/internal` need either a
  second account or a second user, and the dev database seeds exactly one of each. The step-up
  protocol they rely on IS covered through the reveal, but their own request/response shapes are not.
- **No React.** This drives the data layer, not components; a page that mis-renders a correct payload
  is Phase 3's job (Playwright).
- **`getTransactions` returns an empty page** against the dev seed (`totalItems: 0`), so the
  paginated shape is asserted but transaction CONTENT is not.
- **The 5xx branches of `problemBaseQuery`** (`NETWORK`, `PARSE`, the retry policy, `HTTP_502`)
  cannot be produced against a healthy stack and remain unit-tested only.
- **Money accumulates.** The idempotency test deposits €1.00 per run into the dev database. Harmless
  and deliberate — a deposit is additive and needs no counterparty — but it is not a clean-room.
