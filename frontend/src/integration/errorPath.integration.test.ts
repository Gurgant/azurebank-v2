import { beforeAll, describe, expect, it } from 'vitest';
import { apiSlice } from '../features/api/apiSlice';
import type { ApiProblem } from '../api/problemBaseQuery';
import {
  FIXTURES,
  authStatus,
  idempotencyKey,
  makeStore,
  run,
  signIn,
  type IntegrationStore,
} from './harness';
import { currentCookieHeader, resetCookieJar } from './setup';

/**
 * What the app MAKES of a real error, which is a different question from what the server sent.
 *
 * `problemBaseQuery` synthesises codes the wire never carries — `HTTP_<status>` when the body has
 * no `errorCode`, `VALIDATION_ERROR` for a 400 with an `errors` dict — and the contract suite
 * cannot see any of it, because it bypasses this layer on purpose to observe the raw response.
 * Every expectation below is the normalised ApiProblem, measured against the running stack.
 */

let store: IntegrationStore;

beforeAll(async () => {
  store = makeStore();
  await signIn(store);
});

const problem = (error: unknown) => error as ApiProblem;

describe('integration: problemBaseQuery normalises real backend errors', () => {
  it('synthesises VALIDATION_ERROR for a 400 that carries an errors dict', async () => {
    /*
      The API sends a 400 with `errors` and NO `errorCode` member; D5 synthesises one so consumers
      always have something to branch on. Inverted date pair — the drift PR #64 found, where the
      mock answered 422 and the real API answers 400. Measured:

        400 {"title":"One or more validation errors occurred.","status":400,
             "errors":{"ToDate":["FromDate must be earlier than or equal to ToDate."], …}}

      BOTH dates are in the past deliberately. An earlier draft inverted a future date against a
      past one, which would still pass if the 400 came from a future-date rule rather than from the
      inversion. Measured, no such rule exists today — an ORDERED pair ending 2026-12-31 answers
      200 — so that draft was not actually wrong. Two past dates keep it that way if one is ever
      added, which is the cheaper thing to depend on.
    */
    const result = await run(
      store.dispatch(
        apiSlice.endpoints.getTransactionSummary.initiate({
          fromDate: '2026-07-31T00:00:00.000Z',
          toDate: '2026-07-01T00:00:00.000Z',
        }),
      ),
    );

    expect(result.ok).toBe(false);
    if (result.ok) return;
    const apiProblem = problem(result.error);
    expect(apiProblem.status).toBe(400);
    expect(apiProblem.errorCode).toBe('VALIDATION_ERROR');
    expect(apiProblem.errors).toBeDefined();
  });

  it('carries the API’s own 404 errorCode through without synthesising one', async () => {
    /*
      This test used to claim it proved the HTTP_404 SYNTHESIS branch, and it did not — the API
      labels this one itself. Measured:

        404 {"title":"Not Found","status":404,"instance":"/api/accounts/000…000",
             "detail":"Account with identifier '000…000' was not found.",
             "errorCode":"ACCOUNT_NOT_FOUND","traceId":"<32-hex>"}

      The old assertion accepted any of three codes, which is what let a wrongly-named test pass:
      a regression that stopped carrying `errorCode` and fell back to HTTP_404 would have been
      indistinguishable from correct behaviour. Pinned to the one value the backend actually sends.

      The synthesis branch is genuinely covered — by the BFF's own bare 401 in
      anonymous.integration.test.ts, which carries no errorCode and comes back HTTP_401.
    */
    const result = await run(
      store.dispatch(
        apiSlice.endpoints.getAccount.initiate('00000000-0000-0000-0000-000000000000'),
      ),
    );

    expect(result.ok).toBe(false);
    if (result.ok) return;
    const apiProblem = problem(result.error);
    expect(apiProblem.status).toBe(404);
    expect(apiProblem.errorCode).toBe('ACCOUNT_NOT_FOUND');
  });

  /*
    The "carries a real errorCode through untouched" case lives in anonymous.integration.test.ts,
    not here. A fresh Redux store is NOT an anonymous caller: the cookie jar is module state, so it
    still holds the session this file signed in with, and the call simply succeeded. Signed-out has
    to be a property of the FILE.
  */

  it('keeps the session on 401 AUTHORIZATION_REQUIRED — a refused transfer is not a dead session', async () => {
    /*
      ADR-0042's third code, and the one the exemption list did not hold until 2026-09-05. Measured
      that day through the BFF (:5000 -> :7215), the cookie alive, first at level 1 and again at
      level 2 after verify-pin — the same answer both times, because the transfer paths have not
      been level-gated since ADR-0041:

        POST /api/transfers, Idempotency-Key set, NO Step-Up-Authorization header
          -> 401 {"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,
                  "detail":"This transfer has not been authorised.","instance":"/api/transfers",
                  "errorCode":"AUTHORIZATION_REQUIRED","traceId":"<32-hex>"}
             no WWW-Authenticate header, no X-Auth-Level-* header
        GET /bff/auth/me straight after -> 200, authLevel unchanged

      The refusal is thrown inside the [Authorize]d action, upstream of the payee lookup and of
      the two codes the middleware already exempted, so a client holding this answer has proven its
      cookie at least as well as one holding AUTHORIZATION_EXPIRED. The shipped pages never send a
      headerless transfer — both mint first and pass the id — so the request is built by hand at
      the store, the only place the middleware's reaction can be observed against the real stack.
      Before the fix this went red at the status assertion below — 'authenticated' -> 'expired'
      for a session the server had just accepted. The cache reset is the other half of the same
      dispatch block, and in the app that transition is ProtectedRoute to /login with "Your
      session has expired"; neither was observed in that run, both follow from the code.
    */
    expect(authStatus(store)).toBe('authenticated');

    // Warm the cache first, so "the balances survived" is an observation rather than a vacuity.
    const accounts = await run(store.dispatch(apiSlice.endpoints.getAccounts.initiate()));
    expect(accounts.ok, accounts.ok ? '' : JSON.stringify(accounts.error)).toBe(true);
    if (!accounts.ok) return;
    const accountsKey = Object.keys(store.getState().api.queries).find((key) =>
      key.startsWith('getAccounts'),
    );
    expect(store.getState().api.queries[accountsKey as string]?.data).toBeDefined();

    const refused = await run(
      store.dispatch(
        apiSlice.endpoints.transfer.initiate({
          idempotencyKey: idempotencyKey(),
          body: {
            fromAccountId: accounts.data[0].id,
            recipientAzureTag: FIXTURES.recipientAzureTag,
            amount: 1,
            description: 'integration: no authorisation presented',
          },
          // No stepUpAuthorizationId, on purpose: the header is omitted and the API refuses.
        }),
      ),
    );
    expect(refused.ok).toBe(false);
    if (refused.ok) return;
    const apiProblem = problem(refused.error);
    expect(apiProblem.status).toBe(401);
    expect(apiProblem.errorCode).toBe('AUTHORIZATION_REQUIRED');
    expect(apiProblem.detail).toBe('This transfer has not been authorised.');

    // The rule: still authenticated, and the balances fetched a moment ago are still cached.
    expect(authStatus(store)).toBe('authenticated');
    expect(store.getState().api.queries[accountsKey as string]?.data).toBeDefined();

    // And the server agrees the session is alive: the same cookie still reads the accounts.
    const after = await run(
      store.dispatch(apiSlice.endpoints.getAccounts.initiate(undefined, { forceRefetch: true })),
    );
    expect(after.ok, after.ok ? '' : JSON.stringify(after.error)).toBe(true);
  });

  /*
    LAST TEST IN THE FILE, deliberately: it destroys the session this file signed in with, and
    anything after it would run unauthenticated. Vitest runs tests within a file in declaration
    order, so "last" is a guarantee rather than a hope.

    It also lives here rather than in its own file to stay inside the auth budget. A new file means
    a new login, which would take the suite from ~5 to ~6 auth requests per run — and at 10 per 60s
    the SECOND consecutive run would start failing, breaking the repo's run-it-twice gate.
  */
  it('expires the session and empties the cache on a 401 while authenticated (D3)', async () => {
    /*
      The global 401 rule, end to end, and the reason mirroring the production store matters. It
      lives entirely in `sessionMiddleware`: a 401 while AUTHENTICATED dispatches `sessionExpired()`
      and resets the whole RTK Query cache, so financial data never outlives the session it was
      fetched under. A store wired from `apiSlice` alone — which is what this harness used to build
      — cannot observe any of it, and the suite would have passed with the rule entirely broken.

      The session is destroyed by dropping the cookie rather than by logging out, and that is the
      faithful choice: `logout` moves the slice to 'anonymous', which is a DIFFERENT branch of the
      rule. Losing the cookie is what an evicted or expired session looks like to the app — it
      still believes it is signed in. Measured in PR #69: anonymous, an unresolvable cookie and a
      revoked one all return the identical 401 AUTH_TOKEN_MISSING, so this is a faithful stand-in
      for all three.
    */
    expect(authStatus(store)).toBe('authenticated');

    /*
      Warm the cache with real balances first, so "the financial data was purged" is an actual
      observation rather than a vacuous claim about a cache that was already empty. The SUCCESS and
      the presence of DATA are both asserted: a failed warm-up still creates a cache entry, and a
      key with no data would satisfy the purge assertions below while proving nothing at all.
    */
    const warmed = await run(store.dispatch(apiSlice.endpoints.getAccounts.initiate()));
    expect(warmed.ok, warmed.ok ? '' : JSON.stringify(warmed.error)).toBe(true);

    const accountsKey = Object.keys(store.getState().api.queries).find((key) =>
      key.startsWith('getAccounts'),
    );
    expect(accountsKey).toBeDefined();
    expect(store.getState().api.queries[accountsKey as string]?.data).toBeDefined();

    resetCookieJar();
    // Negative control: without this, a reset that silently did nothing would leave the test
    // asserting the signed-IN path while claiming to prove the expiry one.
    expect(currentCookieHeader()).toBe('');

    /*
      The TRIGGERING query does not report an error to its caller, and that took measuring to
      believe. Two drafts asserted it rejects; both went green against a backend that had plainly
      answered 401 (auth was already 'expired'). What actually happens is that `resetApiState()`
      runs inside the middleware while this very request is settling, wiping the entry the promise
      is about to read — so `unwrap()` resolves with `undefined` instead of rejecting.

      That is the rule working, not a bug: the global handler takes the session over, and the
      component that happened to fire the unlucky request has nothing useful to render anyway.
      Worth pinning precisely, because it means "did the 401 surface?" is the wrong question to ask
      of the first request after a session dies.
    */
    const triggering = await run(
      store.dispatch(apiSlice.endpoints.getTransactions.initiate({ page: 1, pageSize: 3 })),
    );
    expect(triggering.ok).toBe(true);
    expect(triggering.ok && triggering.data).toBeUndefined();

    // The rule itself: the slice expires, and the balances fetched under the dead session are gone.
    expect(authStatus(store)).toBe('expired');
    expect(Object.keys(store.getState().api.queries)).not.toContain(accountsKey);
    for (const entry of Object.values(store.getState().api.queries)) {
      expect(entry?.data).toBeUndefined();
    }

    /*
      And the decisive control: a SECOND request proves the server really is answering 401
      AUTH_TOKEN_MISSING. Without it, "resolved with undefined" is far too weak a signal — a
      network fault or a bad URL would look identical. This one rejects normally because the slice
      is already 'expired', so the D3 branch no longer fires and no reset races the response.
    */
    const confirming = await run(
      store.dispatch(apiSlice.endpoints.getTransactions.initiate({ page: 2, pageSize: 3 })),
    );
    expect(confirming.ok).toBe(false);
    if (confirming.ok) return;
    expect(problem(confirming.error).errorCode).toBe('AUTH_TOKEN_MISSING');
  });
});
