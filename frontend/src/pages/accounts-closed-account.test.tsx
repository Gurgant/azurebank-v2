import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { SAVINGS_ACCOUNT_ID, mockState, seedMockSession } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { settle } from '../test/settle';
import { enterPin } from '../test/pinFlow';
import { COPY } from '../test/outage';
import { AppToaster } from '../components/feedback';
import { ACCOUNT_CLOSED } from '../api/problemMessages';
import { AccountsPage } from './AccountsPage';

/**
 * An account closed in another tab or session while the accounts page still shows its card, and
 * what each dialog opened from that card does when the server says so.
 *
 * Measured through the BFF on a spare account, closed after each request was prepared: the
 * rename, set-primary, the deposit, the withdrawal's mint and its send each answer 404 errorCode
 * ACCOUNT_NOT_FOUND, title "Not Found", detail "Account with identifier '<the id>' was not
 * found.", and GET /api/accounts no longer lists the account. The reveal of the full number
 * answers the same to an elevated session; below level 2 it answers the step-up 403 first, as
 * for an account that is there. The mock answers the same once
 * the account is out of its state, so these tests close the account there and, but for those
 * that need an answer the mock does not give by itself, override nothing.
 *
 * A dialog does not outlive its account: it closes, and the one sentence is said in the app's
 * toast outlet, which is why the outlet is rendered beside the routes here as App renders it.
 * The history is a route of its own, for the one way out that leaves the page.
 */

function renderAccounts() {
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/accounts" element={<AccountsPage />} />
        <Route path="/history" element={<div>HISTORY</div>} />
      </Routes>
      <AppToaster />
    </>,
    { routerEntries: ['/accounts'] },
  );
}

/** As another session closes it: the account leaves the server's state, the page is not told. */
function closeElsewhere() {
  mockState.accounts = mockState.accounts.filter((account) => account.id !== SAVINGS_ACCOUNT_ID);
}

/** Counts what the page asks, and lets the mock answer. */
function watchRequests() {
  const seen = { lists: 0, mints: 0, withdrawals: 0 };
  server.use(
    http.get('*/api/accounts', () => {
      seen.lists += 1;
    }),
    http.post('*/api/transactions/withdraw/authorizations', () => {
      seen.mints += 1;
    }),
    http.post('*/api/transactions/withdraw', () => {
      seen.withdrawals += 1;
    }),
  );
  return seen;
}

/** The sentence, in the toast outlet; nothing of the server's; the list read again; the card gone. */
async function expectToldAndRefreshed(seen: { lists: number }, listsBefore: number) {
  const words = await screen.findByText(ACCOUNT_CLOSED);
  expect(ACCOUNT_CLOSED).toBe('This account is closed. Choose another account.');
  // The outlet draws a toast as an item of its list, named by the toast's title: the roles the
  // rendered markup carries. A sentence drawn anywhere else on the page has neither.
  const toast = words.closest('[role="listitem"]');
  expect(toast).toHaveAccessibleName(ACCOUNT_CLOSED);
  expect(toast?.closest('[role="list"]')).toHaveAttribute('data-toaster-position');
  await waitFor(() => expect(seen.lists).toBeGreaterThan(listsBefore));
  await waitFor(() => expect(screen.queryByText('Rainy Day')).not.toBeInTheDocument());
  expect(screen.queryByText(/Account with identifier/)).not.toBeInTheDocument();
  expect(screen.queryByText('Not Found')).not.toBeInTheDocument();
  expect(screen.queryByText(/Support code/)).not.toBeInTheDocument();
  expect(document.body).not.toHaveTextContent(SAVINGS_ACCOUNT_ID);
  expect(screen.getByText('Main Account')).toBeInTheDocument();
}

beforeEach(() => {
  seedMockSession();
});

describe('an account closed elsewhere, met from its card', () => {
  it('rename: the dialog closes, the sentence is said, the card leaves', async () => {
    const seen = watchRequests();
    renderAccounts();
    await screen.findByText('Rainy Day');
    await userEvent.click(screen.getByRole('button', { name: 'Account actions for Rainy Day' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));
    fireEvent.change(await screen.findByLabelText('Account name'), {
      target: { value: 'Storm Fund' },
    });

    closeElsewhere();
    const listsBefore = seen.lists;
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Rename Account' })).not.toBeInTheDocument(),
    );
    await expectToldAndRefreshed(seen, listsBefore);
    expect(screen.queryByText('Storm Fund')).not.toBeInTheDocument();
  });

  it("set as primary: the sentence is said in place of the server's, the card leaves", async () => {
    const seen = watchRequests();
    renderAccounts();
    await screen.findByText('Rainy Day');
    await userEvent.click(screen.getByRole('button', { name: 'Account actions for Rainy Day' }));
    const setPrimary = await screen.findByRole('menuitem', { name: 'Set as primary' });

    closeElsewhere();
    const listsBefore = seen.lists;
    await userEvent.click(setPrimary);

    await expectToldAndRefreshed(seen, listsBefore);
    expect(screen.getAllByText('Primary')).toHaveLength(1);
  });

  it('reveal of the full number: the sentence is said, the card leaves', async () => {
    // Elevated already, so the request reaches the account and no PIN is asked first.
    mockState.authLevel = 2;
    const seen = watchRequests();
    renderAccounts();
    await screen.findByText('Rainy Day');

    closeElsewhere();
    const listsBefore = seen.lists;
    await userEvent.click(
      screen.getByRole('button', { name: 'Reveal full account number for Rainy Day' }),
    );

    await expectToldAndRefreshed(seen, listsBefore);
  });

  it('deposit: the dialog closes, the sentence is said, the card leaves', async () => {
    const seen = watchRequests();
    renderAccounts();
    await screen.findByText('Rainy Day');
    await userEvent.click(screen.getByRole('button', { name: 'Deposit to Rainy Day' }));
    const dialog = await screen.findByRole('dialog', { name: 'Deposit Money' });
    await userEvent.click(within(dialog).getByRole('button', { name: '€100' }));

    closeElsewhere();
    const listsBefore = seen.lists;
    await userEvent.click(within(dialog).getByRole('button', { name: 'Deposit €100.00' }));

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Deposit Money' })).not.toBeInTheDocument(),
    );
    await expectToldAndRefreshed(seen, listsBefore);
  });

  async function openWithdrawAt100() {
    renderAccounts();
    await screen.findByText('Rainy Day');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw from Rainy Day' }));
    const dialog = await screen.findByRole('dialog', { name: 'Withdraw Money' });
    await userEvent.click(within(dialog).getByRole('button', { name: '€100' }));
    return dialog;
  }

  async function expectWithdrawClosed() {
    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Withdraw Money' })).not.toBeInTheDocument(),
    );
  }

  it('withdrawal, on the check before the PIN: it closes and asks for no PIN', async () => {
    const seen = watchRequests();
    const dialog = await openWithdrawAt100();

    closeElsewhere();
    const listsBefore = seen.lists;
    await userEvent.click(within(dialog).getByRole('button', { name: /^Continue/ }));

    await expectWithdrawClosed();
    await expectToldAndRefreshed(seen, listsBefore);
    expect(seen.mints).toBe(0);
    expect(seen.withdrawals).toBe(0);
  });

  it('withdrawal, on the mint: it closes, and no withdrawal is sent', async () => {
    const seen = watchRequests();
    const dialog = await openWithdrawAt100();
    await userEvent.click(within(dialog).getByRole('button', { name: /^Continue/ }));
    await screen.findByText('Verify Withdrawal');
    await enterPin();

    closeElsewhere();
    const listsBefore = seen.lists;
    await userEvent.click(within(dialog).getByRole('button', { name: 'Withdraw €100.00' }));

    await expectWithdrawClosed();
    await expectToldAndRefreshed(seen, listsBefore);
    expect(seen.mints).toBe(1);
    expect(seen.withdrawals).toBe(0);
  });

  it('withdrawal, on the send: it closes, after one mint and one send', async () => {
    const seen = watchRequests();
    // The mint finds the account; it is closed before the send arrives.
    server.use(
      http.post('*/api/transactions/withdraw', () => {
        closeElsewhere();
      }),
    );
    const dialog = await openWithdrawAt100();
    await userEvent.click(within(dialog).getByRole('button', { name: /^Continue/ }));
    await screen.findByText('Verify Withdrawal');
    await enterPin();
    const listsBefore = seen.lists;
    await userEvent.click(within(dialog).getByRole('button', { name: 'Withdraw €100.00' }));

    await expectWithdrawClosed();
    await expectToldAndRefreshed(seen, listsBefore);
    expect(seen.mints).toBe(1);
    expect(seen.withdrawals).toBe(1);
  });

  /**
   * A send answered AUTHORIZATION_EXPIRED keeps its key for the same withdrawal with a new
   * authorisation (ADR-0022, ADR-0042): the dialog is left on its PIN step, the key held and the
   * X barred. The mock gives that answer to no send by itself, so the first one is written
   * here, and counted here: this handler answers before the one that counts.
   */
  async function withdrawUntilTheKeyIsKept(seen: { withdrawals: number }) {
    server.use(
      http.post('*/api/transactions/withdraw', () => {
        seen.withdrawals += 1;
        return seen.withdrawals === 1
          ? problem({
              status: 401,
              errorCode: 'AUTHORIZATION_EXPIRED',
              detail: 'This authorisation has expired. Enter your PIN again to confirm.',
            })
          : HttpResponse.error();
      }),
    );
    const dialog = await openWithdrawAt100();
    await userEvent.click(within(dialog).getByRole('button', { name: /^Continue/ }));
    await screen.findByText('Verify Withdrawal');
    await enterPin();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Withdraw €100.00' }));
    await within(dialog).findByText(/expired/);
    expect(within(dialog).getByRole('button', { name: 'Close' })).toBeDisabled();
    return dialog;
  }

  /** The check view over the key that was held: its words, no sentence yet, and the X open. */
  async function expectTheCheckView(dialog: HTMLElement) {
    expect(
      await within(dialog).findByText("We couldn't confirm your withdrawal"),
    ).toBeInTheDocument();
    expect(within(dialog).getByText(/may or may not have gone through/)).toBeInTheDocument();
    expect(within(dialog).queryByText('Verify Withdrawal')).not.toBeInTheDocument();
    expect(within(dialog).queryByRole('button', { name: /^Withdraw / })).not.toBeInTheDocument();
    expect(within(dialog).queryByText(/Account with identifier/)).not.toBeInTheDocument();
    expect(screen.queryByText(ACCOUNT_CLOSED)).not.toBeInTheDocument();
    // The key is let go behind the view: the X, barred while it was held, works again.
    expect(within(dialog).getByRole('button', { name: 'Close' })).toBeEnabled();
    // The listing is refused on the closed account. Its "try again" only closes the dialog.
    expect(await within(dialog).findByText(COPY.movementUnchecked)).toBeInTheDocument();
  }

  it.each(["It didn't go through — try again", 'Close'])(
    'withdrawal, on a mint made while a key is held: the check view, and "%s" closes it with the sentence',
    async (wayOut) => {
      /*
        The account is closed before the second mint, which answers ACCOUNT_NOT_FOUND. The
        dialog does not close over the key it holds, and does not stay on a PIN step that can
        only mint again: it shows the check view, and from there it closes with the sentence.
      */
      const seen = watchRequests();
      const dialog = await withdrawUntilTheKeyIsKept(seen);

      closeElsewhere();
      const listsBefore = seen.lists;
      await userEvent.click(within(dialog).getByRole('button', { name: 'Withdraw €100.00' }));

      await expectTheCheckView(dialog);
      expect(seen.mints).toBe(2);
      expect(seen.withdrawals).toBe(1);

      // The view's own button waits until the view has settled; the shell's X does not.
      if (wayOut !== 'Close') await settle();
      await userEvent.click(within(dialog).getByRole('button', { name: wayOut }));
      await expectWithdrawClosed();
      await expectToldAndRefreshed(seen, listsBefore);
      expect(seen.mints).toBe(2);
      expect(seen.withdrawals).toBe(1);
    },
  );

  it('withdrawal, on a mint made while a key is held: "Check recent transactions" leaves for the history, with nothing more said', async () => {
    const seen = watchRequests();
    const dialog = await withdrawUntilTheKeyIsKept(seen);

    closeElsewhere();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Withdraw €100.00' }));
    await expectTheCheckView(dialog);

    await settle();
    await userEvent.click(
      within(dialog).getByRole('button', { name: 'Check recent transactions' }),
    );
    expect(await screen.findByText('HISTORY')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    // The outlet is still on the page: the sentence is not said on this way out.
    expect(screen.queryByText(ACCOUNT_CLOSED)).not.toBeInTheDocument();
    expect(seen.mints).toBe(2);
    expect(seen.withdrawals).toBe(1);
  });

  it('withdrawal, on the check before the PIN while a key is held: the check view, then it closes with the sentence', async () => {
    // Back from the PIN step keeps the key; Continue then reads a list without the account.
    const seen = watchRequests();
    const dialog = await withdrawUntilTheKeyIsKept(seen);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Back' }));

    closeElsewhere();
    const listsBefore = seen.lists;
    await userEvent.click(within(dialog).getByRole('button', { name: /^Continue/ }));

    await expectTheCheckView(dialog);
    await settle();
    await userEvent.click(
      within(dialog).getByRole('button', { name: "It didn't go through — try again" }),
    );
    await expectWithdrawClosed();
    await expectToldAndRefreshed(seen, listsBefore);
    expect(seen.mints).toBe(1);
    expect(seen.withdrawals).toBe(1);
  });

  it('withdrawal, closed under a key held after no answer: while it may still land, the way out is "Close"', async () => {
    /*
      No authorisation was refused, so the withdrawal sent may still go through until the bound,
      and the view says so. Its last button only closes the dialog: named "It didn't go through"
      it would say the opposite of the words above it.
    */
    const seen = watchRequests();
    server.use(
      http.post('*/api/transactions/withdraw', () => {
        seen.withdrawals += 1;
        return HttpResponse.error();
      }),
    );
    const dialog = await openWithdrawAt100();
    await userEvent.click(within(dialog).getByRole('button', { name: /^Continue/ }));
    await screen.findByText('Verify Withdrawal');
    await enterPin();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Withdraw €100.00' }));
    await within(dialog).findByText(/Couldn't reach the server/);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Back' }));

    closeElsewhere();
    const listsBefore = seen.lists;
    await userEvent.click(within(dialog).getByRole('button', { name: /^Continue/ }));

    expect(await within(dialog).findByText(COPY.movementUncheckedNotYet)).toBeInTheDocument();
    expect(within(dialog).queryByText(/didn't go through/)).not.toBeInTheDocument();
    // The shell's X, then the view's two, "Check recent transactions" first.
    expect(
      within(dialog)
        .getAllByRole('button')
        .map((button) => button.getAttribute('aria-label') ?? button.textContent),
    ).toEqual(['Close', 'Check recent transactions', 'Close']);

    await settle();
    await userEvent.click(within(dialog).getAllByRole('button', { name: 'Close' })[1]);
    await expectWithdrawClosed();
    await expectToldAndRefreshed(seen, listsBefore);
    expect(seen.mints).toBe(1);
    expect(seen.withdrawals).toBe(1);
  });
});
