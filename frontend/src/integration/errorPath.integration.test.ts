import { beforeAll, describe, expect, it } from 'vitest';
import { apiSlice } from '../features/api/apiSlice';
import type { ApiProblem } from '../api/problemBaseQuery';
import { makeStore, run, signIn, type IntegrationStore } from './harness';

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
});
