import { beforeEach, describe, expect, it, vi } from 'vitest';
import { http } from 'msw';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import {
  MAIN_ACCOUNT_ID,
  MOCK_PIN,
  SAVINGS_ACCOUNT_ID,
  mockState,
  seedMockSession,
} from '../../mocks/state';
import { makeTestStore, type TestStore } from '../../test/renderWithProviders';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { apiSlice } from './apiSlice';

/**
 * 404 ACCOUNT_NOT_FOUND has the accounts list read again, on every mutation that names an
 * account of the visitor's own, the reveal of its full number included (the cache tag ledger
 * in apiSlice.ts).
 *
 * The account is closed as another session closes it: it leaves the mock's state, and the mock
 * then answers each request as the API was measured to, 404 ACCOUNT_NOT_FOUND with the detail
 * "Account with identifier '<the id>' was not found.". The list held by the store still has the
 * account until that answer has it read again.
 */

const endpoints = apiSlice.endpoints;
const key = () => crypto.randomUUID();
const CLOSED = SAVINGS_ACCOUNT_ID;

/** Each request the app can make on the closed account, as the store's own mutation. */
const requests: [string, (store: TestStore) => { unwrap(): Promise<unknown> }][] = [
  [
    'rename',
    (store) =>
      store.dispatch(endpoints.renameAccount.initiate({ id: CLOSED, body: { name: 'Renamed' } })),
  ],
  ['set-primary', (store) => store.dispatch(endpoints.setPrimaryAccount.initiate(CLOSED))],
  ['delete', (store) => store.dispatch(endpoints.deleteAccount.initiate({ id: CLOSED }))],
  [
    // An elevated session: below level 2 the answer is the step-up 403, before the account is
    // looked for (measured through the BFF, as the mock orders it).
    'reveal of the full number',
    (store) => {
      mockState.authLevel = 2;
      return store.dispatch(endpoints.revealAccountNumber.initiate(CLOSED));
    },
  ],
  [
    'deposit',
    (store) =>
      store.dispatch(
        endpoints.deposit.initiate({
          idempotencyKey: key(),
          body: { accountId: CLOSED, amount: 1 },
        }),
      ),
  ],
  [
    'withdrawal',
    (store) =>
      store.dispatch(
        endpoints.withdraw.initiate({
          idempotencyKey: key(),
          body: { accountId: CLOSED, amount: 1 },
        }),
      ),
  ],
  [
    'transfer',
    (store) =>
      store.dispatch(
        endpoints.transfer.initiate({
          idempotencyKey: key(),
          body: { fromAccountId: CLOSED, recipientAzureTag: 'friend', amount: 1 },
        }),
      ),
  ],
  [
    'internal transfer, closed source',
    (store) =>
      store.dispatch(
        endpoints.transferInternal.initiate({
          idempotencyKey: key(),
          body: { fromAccountId: CLOSED, toAccountId: MAIN_ACCOUNT_ID, amount: 1 },
        }),
      ),
  ],
  [
    'internal transfer, closed destination',
    (store) =>
      store.dispatch(
        endpoints.transferInternal.initiate({
          idempotencyKey: key(),
          body: { fromAccountId: MAIN_ACCOUNT_ID, toAccountId: CLOSED, amount: 1 },
        }),
      ),
  ],
  [
    'mint of a transfer',
    (store) =>
      store.dispatch(
        endpoints.authoriseTransfer.initiate({
          fromAccountId: CLOSED,
          recipientAzureTag: 'friend',
          amount: 1,
          pin: MOCK_PIN,
        }),
      ),
  ],
  [
    'mint of an internal transfer, closed source',
    (store) =>
      store.dispatch(
        endpoints.authoriseInternalTransfer.initiate({
          fromAccountId: CLOSED,
          toAccountId: MAIN_ACCOUNT_ID,
          amount: 1,
          pin: MOCK_PIN,
        }),
      ),
  ],
  [
    'mint of an internal transfer, closed destination',
    (store) =>
      store.dispatch(
        endpoints.authoriseInternalTransfer.initiate({
          fromAccountId: MAIN_ACCOUNT_ID,
          toAccountId: CLOSED,
          amount: 1,
          pin: MOCK_PIN,
        }),
      ),
  ],
  [
    'mint of a withdrawal',
    (store) =>
      store.dispatch(
        endpoints.authoriseWithdrawal.initiate({ accountId: CLOSED, amount: 1, pin: MOCK_PIN }),
      ),
  ],
  [
    'mint of a closure',
    (store) =>
      store.dispatch(endpoints.authoriseAccountDeletion.initiate({ id: CLOSED, pin: MOCK_PIN })),
  ],
];

/** A store that holds the accounts list with a reader, and the count of its reads. */
async function storeHoldingTheList() {
  const reads = { count: 0 };
  server.use(
    http.get('*/api/accounts', () => {
      reads.count += 1;
    }),
  );
  const store = makeTestStore();
  await store.dispatch(endpoints.getAccounts.initiate()).unwrap();
  expect(reads.count).toBe(1);
  return { store, reads };
}

const heldIds = (store: TestStore) =>
  (endpoints.getAccounts.select()(store.getState()).data ?? []).map((account) => account.id);

beforeEach(() => {
  seedMockSession();
});

describe('ACCOUNT_NOT_FOUND has the accounts list read again', () => {
  it.each(requests)('%s', async (_name, send) => {
    const { store, reads } = await storeHoldingTheList();
    mockState.accounts = mockState.accounts.filter((account) => account.id !== CLOSED);
    expect(heldIds(store)).toContain(CLOSED);

    const refused = (await send(store)
      .unwrap()
      .catch((caught: unknown) => caught)) as ApiProblem;

    expect([refused.status, refused.errorCode]).toEqual([404, 'ACCOUNT_NOT_FOUND']);
    await vi.waitFor(() => expect(reads.count).toBe(2));
    await vi.waitFor(() => expect(heldIds(store)).not.toContain(CLOSED));
    expect(heldIds(store)).toContain(MAIN_ACCOUNT_ID);
  });

  it('and no other refusal of a mint does', async () => {
    const { store, reads } = await storeHoldingTheList();
    server.use(
      http.post('*/api/transfers/authorizations', () =>
        problem({
          status: 404,
          errorCode: 'RECIPIENT_NOT_FOUND',
          detail: "Recipient with identifier 'friend' was not found.",
        }),
      ),
    );

    const refused = (await store
      .dispatch(
        endpoints.authoriseTransfer.initiate({
          fromAccountId: MAIN_ACCOUNT_ID,
          recipientAzureTag: 'friend',
          amount: 1,
          pin: MOCK_PIN,
        }),
      )
      .unwrap()
      .catch((caught: unknown) => caught)) as ApiProblem;

    expect(refused.errorCode).toBe('RECIPIENT_NOT_FOUND');
    // Long enough for a refetch to have left: the list above is read again within a frame.
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(reads.count).toBe(1);
  });
});
