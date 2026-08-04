import { beforeAll, describe, expect, it } from 'vitest';
import { apiSlice } from '../features/api/apiSlice';
import { makeStore, run, signIn, type IntegrationStore } from './harness';

/**
 * The read path, driven through the app's OWN data layer against the real backend.
 *
 * What this asks that nothing else does: the contract suite uses a raw fetch client on purpose, so
 * it sees what the SERVER sent and stops there. Here the response goes through `transformResponse`
 * -> `unwrap` -> the STRICT Zod schemas generated from the spec. A backend that answers 200 with a
 * shape the app rejects is green in the contract suite and broken in the browser; that gap is the
 * whole reason this layer exists.
 *
 * A failure here is therefore not "the test is wrong". It is one of: the backend drifted from the
 * spec, the spec drifted from the generated schemas, or the app demands something the API never
 * promised. All three are worth a build failure.
 */

let store: IntegrationStore;

beforeAll(async () => {
  // Once per file, not per test: the BFF rate-limits auth to 10 requests / 60s / IP.
  store = makeStore();
  await signIn(store);
});

/** Surface the Zod complaint instead of a bare "expected false to be true". */
function explain(result: { ok: false; error: unknown }): string {
  const error = result.error;
  if (error instanceof Error) return `${error.name}: ${error.message}`;
  return JSON.stringify(error);
}

describe('integration: the real backend satisfies the app’s strict schemas', () => {
  it('parses GET /api/accounts through accountsListSchema', async () => {
    /*
      STRICT (fail-closed in every environment): balances drive every money screen, so a drifted
      shape must fail the query rather than render a wrong number. This is the assertion that
      would have caught a renamed or retyped balance field before a user saw it.
    */
    const result = await run(store.dispatch(apiSlice.endpoints.getAccounts.initiate()));
    expect(result.ok, result.ok ? '' : explain(result)).toBe(true);

    if (!result.ok) return;
    expect(Array.isArray(result.data)).toBe(true);
    expect(result.data.length).toBeGreaterThan(0);

    // The masking rule is server-side and load-bearing: the list endpoint must never ship a full
    // number. Asserted on the REAL payload, not on the mock's idea of one.
    for (const account of result.data) {
      expect(account.accountNumber).toMatch(/\*/);
    }
  });

  it('parses GET /api/transactions/summary through transactionSummarySchema', async () => {
    const now = new Date();
    const monthStart = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));

    const result = await run(
      store.dispatch(
        apiSlice.endpoints.getTransactionSummary.initiate({
          fromDate: monthStart.toISOString(),
          toDate: now.toISOString(),
        }),
      ),
    );
    expect(result.ok, result.ok ? '' : explain(result)).toBe(true);
  });

  it('parses the BARE paginated GET /api/transactions', async () => {
    /*
      One of the two endpoints that are BARE by contract — no `{data, message}` envelope. Running it
      through the real data layer is what proves the app still treats it that way; an envelope
      added server-side would surface here as an unwrap failure rather than as an empty history.
    */
    const result = await run(
      store.dispatch(apiSlice.endpoints.getTransactions.initiate({ page: 1, pageSize: 5 })),
    );
    expect(result.ok, result.ok ? '' : explain(result)).toBe(true);

    if (!result.ok) return;
    /*
      Field names MEASURED off the running stack, not guessed — the first draft of this test
      asserted `items` / `totalCount`, which exist nowhere in this system and failed against a
      perfectly correct backend. Observed:

        {"data":[],"pagination":{"page":1,"pageSize":2,"totalItems":0,
                                 "totalPages":0,"hasNextPage":false,"hasPreviousPage":false}}
    */
    expect(Array.isArray(result.data.data)).toBe(true);
    expect(result.data.pagination).toMatchObject({
      page: 1,
      pageSize: 5,
      totalItems: expect.any(Number),
      hasNextPage: expect.any(Boolean),
    });
  });

  it('parses GET /bff/auth/me and session-status', async () => {
    const me = await run(store.dispatch(apiSlice.endpoints.getMe.initiate()));
    expect(me.ok, me.ok ? '' : explain(me)).toBe(true);

    // BARE by contract, like the paginated list — the other endpoint with no envelope.
    const status = await run(store.dispatch(apiSlice.endpoints.getSessionStatus.initiate()));
    expect(status.ok, status.ok ? '' : explain(status)).toBe(true);
  });

  it('parses a single account and its current balance', async () => {
    const accounts = await run(store.dispatch(apiSlice.endpoints.getAccounts.initiate()));
    expect(accounts.ok).toBe(true);
    if (!accounts.ok) return;
    const id = accounts.data[0].id;

    const account = await run(store.dispatch(apiSlice.endpoints.getAccount.initiate(id)));
    expect(account.ok, account.ok ? '' : explain(account)).toBe(true);

    const balance = await run(
      store.dispatch(apiSlice.endpoints.getAccountBalance.initiate({ id })),
    );
    expect(balance.ok, balance.ok ? '' : explain(balance)).toBe(true);
  });
});
