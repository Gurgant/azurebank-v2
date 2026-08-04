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
      mock answered 422 and the real API answers 400.
    */
    const result = await run(
      store.dispatch(
        apiSlice.endpoints.getTransactionSummary.initiate({
          fromDate: '2026-12-31T00:00:00.000Z',
          toDate: '2026-01-01T00:00:00.000Z',
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

  it('synthesises HTTP_404 when the backend sends no errorCode', async () => {
    // A well-formed but absent id. The fallback branch of the same rule: no errors dict, no
    // errorCode on the wire, so the code is derived from the status.
    const result = await run(
      store.dispatch(
        apiSlice.endpoints.getAccount.initiate('00000000-0000-0000-0000-000000000000'),
      ),
    );

    expect(result.ok).toBe(false);
    if (result.ok) return;
    const apiProblem = problem(result.error);
    expect(apiProblem.status).toBe(404);
    // Asserted as a set rather than one value: the API may or may not label this one itself, and
    // pinning the wrong branch would make a correct backend look broken.
    expect(['HTTP_404', 'ACCOUNT_NOT_FOUND', 'NOT_FOUND']).toContain(apiProblem.errorCode);
  });

  /*
    The "carries a real errorCode through untouched" case lives in anonymous.integration.test.ts,
    not here. A fresh Redux store is NOT an anonymous caller: the cookie jar is module state, so it
    still holds the session this file signed in with, and the call simply succeeded. Signed-out has
    to be a property of the FILE.
  */
});
