import type { ReactNode } from 'react';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { MAIN_ACCOUNT_ID, SAVINGS_ACCOUNT_ID, mockState } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { settle } from '../test/settle';
import { confirmWithPin } from '../test/pinFlow';
import { expectNoNestedLiveRegions } from '../test/liveRegions';
import {
  COPY,
  emulateFocusFixup,
  lookupsSeen,
  loseAnswerAfterCommit,
  notKnown,
} from '../test/outage';
import { formatDateTime } from '../utils/format';
import { StepUpModal } from '../features/auth';
import { TransferPage } from './TransferPage';
import { InternalTransferPage } from './InternalTransferPage';

/**
 * A transfer whose answer was lost, found by its key in the listing (ADR-0022): the page says it
 * went through, with the movement's amount, reference and time, in place of the check view. The
 * mock carries the transfer out and the answer is lost on the way back, so the row it wrote is
 * the one the page must show.
 */

let stopFocusFixup: () => void;
beforeEach(() => {
  // The PIN boxes are disabled during the send, and a browser hands their focus to `body`.
  stopFocusFixup = emulateFocusFixup();
});
afterEach(() => {
  stopFocusFixup();
});

/*
  Vitest gives up on a test at its timeout but cannot stop the body, which goes on pressing and
  querying. Measured: a body abandoned early landed on the next test's page, and that test
  failed on act warnings about its own page. So the hook holds the unmount, and with it the next
  test or the next file of the worker, until the body has ended.
*/
let body: Promise<void> = Promise.resolve();
afterEach(() => body);

/** `it`, for a body that the hook above waits for; what it throws is still this test's failure. */
function test(name: string, run: () => Promise<void>) {
  it(name, () => {
    const running = run();
    body = running.catch(() => {});
    return running;
  });
}

function render(page: ReactNode) {
  return renderWithProviders(
    <Routes>
      <Route
        path="/"
        element={
          <>
            {page}
            <StepUpModal />
          </>
        }
      />
      <Route path="/dashboard" element={<div>DASHBOARD</div>} />
      <Route path="/history" element={<div>HISTORY</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

/*
  The values go in whole, not key by key: each keystroke is a render and several turns of the event
  loop, and on a starved machine they took the external test past its budget. This file is about
  what comes after the send; TransferPage.test.tsx and InternalTransferPage.test.tsx type.
*/
async function sendExternal() {
  render(<TransferPage />);
  await screen.findByText('Main Account');
  fireEvent.change(screen.getByLabelText('Recipient handle'), { target: { value: 'friend' } });
  await userEvent.click(screen.getByRole('button', { name: 'Verify' }));
  await screen.findByText('A. Friend');
  fireEvent.change(screen.getByLabelText('Transfer amount'), { target: { value: '50' } });
  await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
  await confirmWithPin();
}

async function sendInternal() {
  render(<InternalTransferPage />);
  await screen.findByRole('button', { name: 'From Main Account' });
  await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
  fireEvent.change(screen.getByLabelText('Transfer amount'), { target: { value: '50' } });
  await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
  await confirmWithPin();
}

/** The row the mock wrote under the key the page asked about, on `accountId`. */
function rowOf(key: string | null | undefined, accountId: string) {
  const row = mockState.transactions.find(
    (entry) => entry.idempotencyKey === key && entry.accountId === accountId,
  );
  if (!row) throw new Error(`no row of the key on ${accountId}`);
  return row;
}

/** The found view: the sentence holds focus, and under it the movement the row describes. */
async function expectTheFoundView(row: ReturnType<typeof rowOf>, amount: string) {
  const sentence = await screen.findByText(COPY.transferFound);
  expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Transfer Complete');
  await waitFor(() => expect(sentence).toHaveFocus());
  expect(screen.getByText(amount)).toBeInTheDocument();
  expect(screen.getByText('Reference').nextSibling).toHaveTextContent(row.transactionNumber);
  expect(screen.getByText('Date & time').nextSibling).toHaveTextContent(
    formatDateTime(row.createdAt),
  );
  expect(screen.getAllByRole('button').map((button) => button.textContent)).toEqual([
    'View History',
    'Done',
  ]);
  expect(screen.queryByText("We couldn't confirm your transfer")).not.toBeInTheDocument();
  expect(screen.queryByText(/didn't go through/)).not.toBeInTheDocument();
  expectNoNestedLiveRegions();
}

describe('a transfer whose answer was lost, found by its key', () => {
  test('external: says the transfer went through, with the reference of the row the key wrote', async () => {
    const sentAt = loseAnswerAfterCommit('/api/transfers', () => notKnown('/api/transfers'));
    const seen = lookupsSeen();

    await sendExternal();

    await waitFor(() => expect(seen).toHaveLength(1));
    await expectTheFoundView(rowOf(seen[0].key, MAIN_ACCOUNT_ID), '-€50.00');
    expect(seen[0].accountId).toBe(MAIN_ACCOUNT_ID);
    expect(sentAt).toHaveLength(1);

    await settle();
    await userEvent.click(screen.getByRole('button', { name: 'View History' }));
    expect(await screen.findByText('HISTORY')).toBeInTheDocument();
  });

  test('internal: one movement, asked about on the account the money left', async () => {
    const sentAt = loseAnswerAfterCommit('/api/transfers/internal', () =>
      notKnown('/api/transfers/internal'),
    );
    const seen = lookupsSeen();

    await sendInternal();

    await waitFor(() => expect(seen).toHaveLength(1));
    await expectTheFoundView(rowOf(seen[0].key, MAIN_ACCOUNT_ID), '€50.00');
    expect(seen.map((ask) => ask.accountId)).toEqual([MAIN_ACCOUNT_ID]);
    expect(sentAt).toHaveLength(1);
  });

  test('internal: with the source closed after the lost answer, asks the destination and shows its own row', async () => {
    /*
      Both rows carry the key and were written in one transaction. The source is gone from the
      visitor's open accounts, so the listing refuses it (403), and the destination's TransferIn
      row, with its own number, is the movement shown.
    */
    loseAnswerAfterCommit('/api/transfers/internal', () => {
      mockState.accounts = mockState.accounts.filter((account) => account.id !== MAIN_ACCOUNT_ID);
      return notKnown('/api/transfers/internal');
    });
    const seen = lookupsSeen();

    await sendInternal();

    await waitFor(() => expect(seen).toHaveLength(2));
    const destinationRow = rowOf(seen[1].key, SAVINGS_ACCOUNT_ID);
    expect(destinationRow.type).toBe('TransferIn');
    await expectTheFoundView(destinationRow, '€50.00');
    expect(seen.map((ask) => ask.accountId)).toEqual([MAIN_ACCOUNT_ID, SAVINGS_ACCOUNT_ID]);
    expect(seen[1].key).toBe(seen[0].key);
    expect(destinationRow.transactionNumber).not.toBe(
      rowOf(seen[0].key, MAIN_ACCOUNT_ID).transactionNumber,
    );
  });
});
