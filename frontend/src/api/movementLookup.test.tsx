import type { PropsWithChildren } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { Provider } from 'react-redux';
import { http } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../mocks/server';
import { serviceUnavailable } from '../mocks/problem';
import { mockState } from '../mocks/state';
import { makeTestStore, type TestStore } from '../test/renderWithProviders';
import { advance, installFakeClock, lookupsSeen, whenListedByKey } from '../test/outage';
import { apiSlice, useGetAccountsQuery, useGetTransactionsQuery } from '../features/api/apiSlice';
import type { MovementLookupArg } from './movementByKey';

/**
 * The listing by key as the data layer asks it (`findMovementByKey`, ADR-0022): what goes on the
 * wire, what the store keeps, and what a found movement makes stale.
 *
 * Asserted at the store, as `policies.test.tsx` asserts what a lost answer does to the cache, with
 * a reader of each list mounted: a refetch needs a reader to have anything to refetch.
 */

const MAIN = mockState.accounts[0].id;
const HISTORY = { page: 1, pageSize: 20 };

function hookWrapper(store: TestStore) {
  function Wrapper({ children }: PropsWithChildren) {
    return <Provider store={store}>{children}</Provider>;
  }
  return Wrapper;
}

/** A deposit the mock carries out under `key`, with nothing of it kept in the store. */
async function depositUnder(store: TestStore, key: string) {
  await store
    .dispatch(
      apiSlice.endpoints.deposit.initiate(
        { idempotencyKey: key, body: { accountId: MAIN, amount: 5 } },
        { track: false },
      ),
    )
    .unwrap();
}

function lookUp(store: TestStore, arg: MovementLookupArg) {
  return store.dispatch(apiSlice.endpoints.findMovementByKey.initiate(arg));
}

/** Counts the reads of the two lists, letting each one through to the mock. Not the lookups. */
function countListReads() {
  const reads = { accounts: 0, transactions: 0 };
  server.use(
    http.get('*/api/accounts', () => {
      reads.accounts += 1;
      return undefined;
    }),
    http.get('*/api/transactions', ({ request }) => {
      if (!new URL(request.url).searchParams.has('IdempotencyKey')) reads.transactions += 1;
      return undefined;
    }),
  );
  return reads;
}

async function mountReaders(store: TestStore, reads: { accounts: number; transactions: number }) {
  const { result } = renderHook(
    () => ({ accounts: useGetAccountsQuery(), transactions: useGetTransactionsQuery(HISTORY) }),
    { wrapper: hookWrapper(store) },
  );
  await waitFor(() => {
    expect(result.current.accounts.isSuccess).toBe(true);
    expect(result.current.transactions.isSuccess).toBe(true);
  });
  expect(reads).toEqual({ accounts: 1, transactions: 1 });
}

/**
 * One more request, sent now and answered: whatever the store sent before it has reached its
 * handler by then, so a count read after this is not a count read too soon.
 */
async function afterARoundTrip(store: TestStore) {
  const probe = store.dispatch(apiSlice.endpoints.getAccount.initiate(MAIN));
  await probe.unwrap();
  probe.unsubscribe();
}

describe('the listing by key, in the data layer', () => {
  it('asks the listing with the account and the key, and with nothing else', async () => {
    const store = makeTestStore();
    const seen = lookupsSeen();
    const key = crypto.randomUUID();

    const reading = await lookUp(store, {
      idempotencyKey: key,
      accountId: MAIN,
      type: 'Deposit',
    }).unwrap();

    expect(reading).toEqual({ kind: 'none' });
    expect(seen).toHaveLength(1);
    expect(seen[0]).toMatchObject({ accountId: MAIN, key, query: 'AccountId,IdempotencyKey' });
  });

  it('finds the deposit the key wrote, and the store keeps the movement without its key', async () => {
    const store = makeTestStore();
    const key = crypto.randomUUID();
    await depositUnder(store, key);
    const written = mockState.transactions.find((row) => row.idempotencyKey === key);
    expect(written).toBeDefined();

    const request = lookUp(store, { idempotencyKey: key, accountId: MAIN, type: 'Deposit' });
    const reading = await request.unwrap();

    expect(reading).toEqual({
      kind: 'found',
      movement: {
        id: written?.id,
        type: 'Deposit',
        transactionNumber: written?.transactionNumber,
        amount: 5,
        createdAt: written?.createdAt,
      },
    });
    const kept = store.getState().api.mutations[request.requestId];
    expect(kept?.data).toEqual(reading);
    expect(JSON.stringify(store.getState().api.mutations)).not.toContain(key);
    request.reset();
  });

  it('is asked once when the service is down: a lookup is never retried by itself', async () => {
    installFakeClock();
    const store = makeTestStore();
    whenListedByKey(() =>
      serviceUnavailable({ via: 'api', instance: '/api/transactions', retryAfterSeconds: 1 }),
    );
    const seen = lookupsSeen();

    const request = lookUp(store, {
      idempotencyKey: crypto.randomUUID(),
      accountId: MAIN,
      type: 'Deposit',
    });
    await expect(request.unwrap()).rejects.toMatchObject({ status: 503 });
    // Well past the wait the answer named, and the one a read would take before its retry.
    await advance(15_000);
    await afterARoundTrip(store);

    expect(seen).toHaveLength(1);
  });

  it('refetches the listing and the accounts that are read when it finds the movement', async () => {
    const store = makeTestStore();
    const key = crypto.randomUUID();
    await depositUnder(store, key);
    const reads = countListReads();
    await mountReaders(store, reads);

    await lookUp(store, { idempotencyKey: key, accountId: MAIN, type: 'Deposit' }).unwrap();
    await afterARoundTrip(store);

    expect(reads).toEqual({ accounts: 2, transactions: 2 });
  });

  it('refetches nothing when it finds nothing', async () => {
    const store = makeTestStore();
    const reads = countListReads();
    await mountReaders(store, reads);

    const reading = await lookUp(store, {
      idempotencyKey: crypto.randomUUID(),
      accountId: MAIN,
      type: 'Deposit',
    }).unwrap();
    await afterARoundTrip(store);

    expect(reading).toEqual({ kind: 'none' });
    expect(reads).toEqual({ accounts: 1, transactions: 1 });
  });

  it('is refused with 403 on an account that is not one of the open ones', async () => {
    const store = makeTestStore();

    await expect(
      lookUp(store, {
        idempotencyKey: crypto.randomUUID(),
        accountId: '019f7b3f-0000-7000-8000-0000000000ff',
        type: 'Deposit',
      }).unwrap(),
    ).rejects.toMatchObject({ status: 403, errorCode: 'ACCESS_DENIED' });
  });
});
