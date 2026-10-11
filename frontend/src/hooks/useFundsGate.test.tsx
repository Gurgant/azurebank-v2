import type { PropsWithChildren } from 'react';
import { Provider } from 'react-redux';
import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { MAIN_ACCOUNT_ID, SAVINGS_ACCOUNT_ID, mockState, seedMockSession } from '../mocks/state';
import { makeTestStore } from '../test/renderWithProviders';
import { apiSlice } from '../features/api/apiSlice';
import { useFundsGate, type FundsVerdict } from './useFundsGate';

/**
 * The verdicts of the funds gate on a list read afresh. `closed` is the one a caller must not
 * take for `unknown`: `unknown` goes on to the PIN step, `closed` does not.
 */

/** The gate over a store that has received the accounts list once, as a page holds it. */
async function gateOverALoadedList() {
  const store = makeTestStore();
  const { result } = renderHook(() => useFundsGate(), {
    wrapper: ({ children }: PropsWithChildren) => <Provider store={store}>{children}</Provider>,
  });
  await waitFor(() =>
    expect(apiSlice.endpoints.getAccounts.select()(store.getState()).data).toBeDefined(),
  );
  // Under `act`: the read the gate makes lands in the store this hook is subscribed to.
  return async (...asked: Parameters<typeof result.current>) => {
    let verdict: FundsVerdict | undefined;
    await act(async () => {
      verdict = await result.current(...asked);
    });
    return verdict;
  };
}

/** As another session closes it: the account leaves the server's state, the store is not told. */
function closeElsewhere(id: string) {
  mockState.accounts = mockState.accounts.filter((account) => account.id !== id);
}

beforeEach(() => {
  seedMockSession();
});

describe('useFundsGate', () => {
  it('answers ok when the accounts it is asked about are in the list and the amount fits', async () => {
    const confirmFunds = await gateOverALoadedList();

    expect(await confirmFunds(MAIN_ACCOUNT_ID, 50, [SAVINGS_ACCOUNT_ID])).toEqual({ status: 'ok' });
  });

  it('answers closed when the account is missing from the list read again', async () => {
    const confirmFunds = await gateOverALoadedList();
    closeElsewhere(SAVINGS_ACCOUNT_ID);

    expect(await confirmFunds(SAVINGS_ACCOUNT_ID, 50)).toEqual({ status: 'closed' });
  });

  it('answers closed when an account it is also asked about is missing from that list', async () => {
    const confirmFunds = await gateOverALoadedList();
    closeElsewhere(SAVINGS_ACCOUNT_ID);

    expect(await confirmFunds(MAIN_ACCOUNT_ID, 50, [SAVINGS_ACCOUNT_ID])).toEqual({
      status: 'closed',
    });
  });

  it('answers unknown, not closed, when the list cannot be read again', async () => {
    const confirmFunds = await gateOverALoadedList();
    server.use(http.get('*/api/accounts', () => HttpResponse.error()));

    expect(await confirmFunds(SAVINGS_ACCOUNT_ID, 50)).toEqual({ status: 'unknown' });
  });
});
