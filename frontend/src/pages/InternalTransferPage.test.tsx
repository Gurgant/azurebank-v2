import { Route, Routes } from 'react-router-dom';
import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { confirmWithPin, enterPin } from '../test/pinFlow';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { settle } from '../test/settle';
import { COPY, emulateFocusFixup } from '../test/outage';
import { StepUpModal } from '../features/auth';
import { InternalTransferPage } from './InternalTransferPage';
import { MAIN_ACCOUNT_ID, SAVINGS_ACCOUNT_ID, mockState, seedMockSession } from '../mocks/state';
import {
  MOVE_ACCOUNT_CLOSED,
  MOVE_DESTINATION_CLOSED,
  MOVE_SOURCE_CLOSED,
} from '../api/problemMessages';

/**
 * PR-11b — internal transfer between the caller's OWN accounts, PIN collected in the page
 * (ADR-0041/0042): pick source + destination (can't be the same), amount, review, PIN → the
 * authorisation is minted and spent on Send → receipt. Until ADR-0041 Send met a level-2 403 and
 * the root StepUpModal collected the PIN; it stays mounted below to prove it no longer appears.
 */

/** The entries end on the page: one before it is where the browser's Back leads. */
function renderInternal(routerEntries: string[] = ['/']) {
  return renderWithProviders(
    <Routes>
      <Route
        path="/"
        element={
          <>
            <InternalTransferPage />
            <StepUpModal />
          </>
        }
      />
      <Route path="/dashboard" element={<div>DASHBOARD</div>} />
      <Route path="/history" element={<div>HISTORY</div>} />
      <Route path="/transfer" element={<div>SEND TO SOMEONE ELSE</div>} />
    </Routes>,
    { routerEntries },
  );
}

/*
  A session, seeded, because the step-up routes now demand one.

  The mock used to answer /bff/auth/verify-pin for a caller with NO session at all, so these
  tests never had to establish one and quietly exercised a state the product cannot reach: the
  real BFF reads the session first and answers 401. Aligning the mock (contract gate) made that
  visible. Seeding is the faithful fix — a user reaching a PIN prompt is, by construction,
  already signed in.
*/
beforeEach(() => {
  seedMockSession();
});

/**
 * T11, on a wizard page. `balance-guard.test.tsx` proves the shared pieces against the withdraw
 * dialog; this proves the WIRING here, which is per-page and is the part that can silently be
 * missing: the gate has to run on the review screen's Continue, and its refusal has to reach the
 * banner through `wizard.fail` rather than vanishing.
 */
describe('the balance guard on an internal transfer', () => {
  it('does not reach the PIN step when the server says the source balance has moved', async () => {
    let reads = 0;
    server.use(
      http.get('*/api/accounts', () => {
        reads += 1;
        // Read 1 renders the form with the seeded €1,250.50; from the gate's read onward the
        // source account holds €10, so the €50 the user is reviewing can no longer be sent.
        const balance = reads === 1 ? 1250.5 : 10;
        const [main, ...rest] = mockState.accounts;
        return HttpResponse.json({ data: [{ ...main, balance }, ...rest], message: null });
      }),
    );
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });

    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(
      await screen.findByText('Insufficient balance for this operation — €10.00 available.'),
    ).toBeInTheDocument();
    // The ceremony is never rendered — which is the whole requirement.
    expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
  });
});

describe('internal transfer (PR-11b)', () => {
  it('moves money between own accounts, authorised by an in-form PIN, and shows the receipt', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' }); // accounts loaded

    // Main is the auto-selected source; pick Rainy Day as the destination.
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    await confirmWithPin();
    // The PIN is entered in the page now (`confirmWithPin`), not by the root step-up modal
    // reacting to a 403 — see ADR-0041.
    expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument();

    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();
    expect(screen.getByText('€50.00')).toBeInTheDocument();
    expect(screen.getByText('€1,200.50')).toBeInTheDocument(); // Main: 1250.50 - 50
    // findBy: the step-up modal's EXIT is async — until its aria-hidden lifts off the
    // background, role queries can't see the receipt buttons (P1.9 sweep).
    await userEvent.click(await screen.findByRole('button', { name: 'Done' }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
  });

  it('the receipt offers History as well as Done', async () => {
    // Added with the drift fix: an internal transfer writes a transaction and invalidates the same
    // list an external one does, but only external's receipt offered to go and look at it.
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPin();
    expect(screen.queryByText("Verify it's you")).not.toBeInTheDocument();
    await screen.findByText('Transfer Complete!');

    await userEvent.click(await screen.findByRole('button', { name: 'View History' }));
    expect(await screen.findByText('HISTORY')).toBeInTheDocument();
  });

  it('cannot pick the source account as the destination', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    // Main is the source → it is disabled in the To list.
    expect(screen.getByRole('button', { name: 'To Main Account' })).toBeDisabled();
    // With no destination chosen, Review stays disabled.
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
  });

  it('on arrival, says to choose an account to move the money to', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toHaveAccessibleDescription(
      'Choose an account to move the money to.',
    );
  });

  it('with a destination and an empty amount, says to enter an amount', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toHaveAccessibleDescription(
      'Enter an amount to continue.',
    );
  });

  it.each(['0', '0.00', '.'])(
    'with a destination and %s typed, says to change the amount, then to enter it when cleared',
    async (amount) => {
      renderInternal();
      await screen.findByRole('button', { name: 'From Main Account' });
      await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
      const review = screen.getByRole('button', { name: 'Review Transfer' });
      const field = screen.getByLabelText('Transfer amount');
      await userEvent.type(field, amount);

      expect(review).toBeDisabled();
      expect(review).toHaveAccessibleDescription('Change the amount to continue.');

      await userEvent.clear(field);

      expect(review).toBeDisabled();
      expect(review).toHaveAccessibleDescription('Enter an amount to continue.');
    },
  );

  it('with a valid move the button is enabled, its accessible description is empty, and the line paragraph is still in the page', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    const review = screen.getByRole('button', { name: 'Review Transfer' });
    const hintId = review.getAttribute('aria-describedby');
    expect(hintId).toBeTruthy();
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');

    expect(review).toBeEnabled();
    expect(review).not.toHaveAccessibleDescription();
    expect(review).not.toHaveAttribute('aria-describedby');
    const hint = document.getElementById(hintId!);
    expect(hint).toBeInTheDocument();
    expect(hint).toBeEmptyDOMElement();
  });

  it('explains that a second account is needed when the user has only one', async () => {
    server.use(
      http.get('*/api/accounts', () =>
        HttpResponse.json({
          data: [
            {
              id: '019f7b3f-0000-7000-8000-0000000000a1',
              accountNumber: 'AB-****-****-90',
              name: 'Main Account',
              type: 'Checking',
              balance: 1000,
              isPrimary: true,
              createdAt: '2026-07-01T09:00:00.0000000Z',
            },
          ],
          message: null,
        }),
      ),
    );
    renderInternal();
    expect(await screen.findByText(/You need a second account to transfer/)).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Review Transfer' }),
    ).not.toHaveAccessibleDescription();
  });

  it('says the accounts could not be loaded, and Retry actually refetches', async () => {
    /*
      Two defects, one test.

      The message was gated on `!accountsLoading`, and isLoading also goes false once a request has
      FAILED — so `accounts` fell back to [] and the page told a user with five accounts they needed
      a second one, sending them to create an account to solve a network problem.

      Fixing that gate alone left SILENCE: the shell stood there with empty pickers and no
      explanation. Both pages now follow the convention AccountsPage already used — surface the
      problem and offer a way out of it.
    */
    let calls = 0;
    server.use(
      http.get('*/api/accounts', () => {
        calls += 1;
        return calls === 1
          ? problem({ status: 500, errorCode: 'INTERNAL_ERROR' })
          : HttpResponse.json({ data: [], message: null });
      }),
    );
    renderInternal();

    expect(await screen.findByText(/Could not load your accounts/)).toBeInTheDocument();
    // The wrong message must NOT be there: an empty list is not evidence of a missing account.
    expect(
      screen.queryByText(/You need a second account to transfer between your own accounts/),
    ).not.toBeInTheDocument();

    // And the form is gone: an amount field over an empty picker would stack "Available: €0.00"
    // and "Exceeds available balance of €0.00" underneath the real error.
    expect(screen.queryByLabelText('Transfer amount')).not.toBeInTheDocument();
    // Retry is not decoration — it must actually re-issue the request.
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(calls).toBe(2));
  });

  it('disables Review when the amount exceeds the source balance', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '99999');

    expect(screen.getByText(/Exceeds available balance/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toHaveAccessibleDescription(
      'Change the amount to continue.',
    );
  });

  it('after IN_FLIGHT the retained key freezes Back; Send reuses the SAME key', async () => {
    const keys: (string | null)[] = [];
    let calls = 0;
    server.use(
      http.post('*/api/transfers/internal', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        calls += 1;
        return calls === 1
          ? problem({
              status: 409,
              errorCode: 'IDEMPOTENCY_IN_FLIGHT',
              detail: 'Still processing.',
            })
          : HttpResponse.json(
              {
                data: {
                  transferId: '019f7b3f-0000-7000-8000-000000000d01',
                  transactionNumber: 'TXN-INT-1',
                  fromAccountId: '019f7b3f-0000-7000-8000-0000000000a1',
                  toAccountId: '019f7b3f-0000-7000-8000-0000000000a2',
                  amount: 50,
                  description: null,
                  fromAccountNewBalance: 1200.5,
                  toAccountNewBalance: 880,
                  processedAt: '2026-07-22T13:00:00.0000000Z',
                },
                message: 'ok',
              },
              { status: 201 },
            );
      }),
    );
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    // The spy answers IN_FLIGHT then 201 (no 403), so no modal — this isolates the PAGE's
    // retained-key guard: Back must freeze and Send must reuse the key.
    await confirmWithPin();

    expect(await screen.findByText(/Still processing/)).toBeInTheDocument();
    screen.getAllByRole('button', { name: 'Back' }).forEach((b) => expect(b).toBeDisabled());

    /*
      The retry is the banner's own `Check again`, not another walk through the flow: an
      IN_FLIGHT failure leaves the user on the PIN step with the PIN they already typed, because
      the page only clears it for PIN-specific refusals. That is the safe forward action — it
      reuses the retained key instead of minting a new one.
    */
    await userEvent.click(screen.getByRole('button', { name: 'Check again' }));
    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]); // SAME key across the retry
  });
});

/*
  What the form offers besides typing, and every way off the page. Each of these was drawn and
  pressed by no test: a shortcut that fills in another amount than it says, or a header button
  that skips a step, would have shipped green.
*/
describe('internal transfer: the shortcuts of the form and the ways off the page', () => {
  /** What the send asked the server to move, read from the request and not from the screen. */
  function watchSends() {
    const sends: { fromAccountId: string; toAccountId: string; amount: number }[] = [];
    server.use(
      http.post('*/api/transfers/internal', async ({ request }) => {
        sends.push(
          (await request.clone().json()) as {
            fromAccountId: string;
            toAccountId: string;
            amount: number;
          },
        );
      }),
    );
    return sends;
  }

  /** The review card's rows, as label and value. */
  const reviewed = (label: string) => screen.getByText(label).parentElement;

  it('a quick amount is the amount that is reviewed and sent', async () => {
    const sends = watchSends();
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    expect(reviewed('Amount')).toHaveTextContent('€100.00');
    await confirmWithPin();
    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();
    expect(sends).toEqual([
      { fromAccountId: MAIN_ACCOUNT_ID, toAccountId: SAVINGS_ACCOUNT_ID, amount: 100 },
    ]);
  });

  it('a quick amount the source cannot cover cannot be pressed', async () => {
    // The source holds €60: €100 is more than it has, €50 is not.
    server.use(
      http.get('*/api/accounts', () => {
        const [main, ...rest] = mockState.accounts;
        return HttpResponse.json({ data: [{ ...main, balance: 60 }, ...rest], message: null });
      }),
    );
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });

    expect(screen.getByRole('button', { name: '€100' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '€50' })).toBeEnabled();
  });

  it('a quick amount equal to the whole balance can still be pressed', async () => {
    // The source holds exactly €50: the server accepts an amount up to the balance, so €50 stays.
    server.use(
      http.get('*/api/accounts', () => {
        const [main, ...rest] = mockState.accounts;
        return HttpResponse.json({ data: [{ ...main, balance: 50 }, ...rest], message: null });
      }),
    );
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });

    expect(screen.getByRole('button', { name: '€50' })).toBeEnabled();
    expect(screen.getByRole('button', { name: '€100' })).toBeDisabled();
  });

  it('"Use max" fills in the whole available balance, to the cent', async () => {
    const sends = watchSends();
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));

    await userEvent.click(screen.getByRole('button', { name: 'Use maximum, €1,250.50' }));
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    expect(reviewed('Amount')).toHaveTextContent('€1,250.50');
    expect(reviewed('Main Account balance')).toHaveTextContent('€0.00');
    await confirmWithPin();
    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();
    expect(sends).toEqual([
      { fromAccountId: MAIN_ACCOUNT_ID, toAccountId: SAVINGS_ACCOUNT_ID, amount: 1250.5 },
    ]);
  });

  it('choosing the destination as the source clears the destination', async () => {
    const pressed = (name: string) =>
      screen.getByRole('button', { name }).getAttribute('aria-pressed');
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeEnabled();

    await userEvent.click(screen.getByRole('button', { name: 'From Rainy Day' }));

    // One account cannot be both ends of a move: the visitor chooses where the money goes again.
    expect(pressed('From Rainy Day')).toBe('true');
    expect(pressed('To Rainy Day')).toBe('false');
    expect(pressed('To Main Account')).toBe('false');
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toHaveAccessibleDescription(
      'Choose an account to move the money to.',
    );
  });

  it("the header's Back goes one step at a time, and from the form it leaves for the dashboard", async () => {
    const sends = watchSends();
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByLabelText('Digit 1 of 6');
    // The header's button is the first "Back" on the page; each step has its own below it.
    const headerBack = () => screen.getAllByRole('button', { name: 'Back' })[0];

    await userEvent.click(headerBack());
    expect(await screen.findByRole('heading', { name: 'Review Transfer' })).toBeInTheDocument();
    expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Continue' })).toBeEnabled();

    await userEvent.click(headerBack());
    expect(await screen.findByRole('heading', { name: 'Move Money' })).toBeInTheDocument();
    // The form comes back as it was left.
    expect(screen.getByRole('button', { name: 'To Rainy Day' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    await userEvent.click(headerBack());
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
    expect(sends).toEqual([]);
  });

  it("the header's Close leaves for the dashboard and sends nothing", async () => {
    const sends = watchSends();
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));

    await userEvent.click(screen.getByRole('button', { name: 'Close' }));

    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
    expect(sends).toEqual([]);
  });

  it('"Send to someone else" leaves for the transfer to another person', async () => {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });

    await userEvent.click(screen.getByRole('button', { name: 'Send to someone else' }));

    expect(await screen.findByText('SEND TO SOMEONE ELSE')).toBeInTheDocument();
  });

  it('the check view\'s "Check recent transactions" leaves for the history', async () => {
    // The server cannot tell whether the money moved (ADR-0022): the page shows the check view.
    server.use(
      http.post('*/api/transfers/internal', () =>
        problem({
          status: 409,
          errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
          detail: 'The result could not be confirmed.',
        }),
      ),
    );
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPin();
    await screen.findByText("We couldn't confirm your transfer");
    await screen.findByText(COPY.movementNotYet);

    await settle();
    await userEvent.click(screen.getByRole('button', { name: 'Check recent transactions' }));

    expect(await screen.findByText('HISTORY')).toBeInTheDocument();
  });

  /*
    The browser's own Back while a send's outcome is not known. Every button of the page is
    disabled then; the browser's is not the page's to disable, so the page asks (ADR-0028). The
    hook's own suite holds the blocker; this holds that the page draws the question and that
    each answer does what it says.
  */
  it("asks before the browser's Back leaves a transfer whose outcome is not known", async () => {
    server.use(
      http.post('*/api/transfers/internal', () =>
        problem({ status: 409, errorCode: 'IDEMPOTENCY_IN_FLIGHT', detail: 'Still processing.' }),
      ),
    );
    const { router } = renderInternal(['/dashboard', '/']);
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPin();
    await screen.findByText(/Still processing/);

    await act(() => router.navigate(-1));
    const question = await screen.findByRole('alertdialog', { name: 'Leave without finishing?' });
    expect(question).toHaveTextContent('check your history before sending it again');
    await userEvent.click(within(question).getByRole('button', { name: 'Stay on this page' }));

    // Still on the page, with the way to check the same transfer again.
    await waitFor(() =>
      expect(
        screen.queryByRole('alertdialog', { name: 'Leave without finishing?' }),
      ).not.toBeInTheDocument(),
    );
    expect(screen.queryByText('DASHBOARD')).not.toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Check again' })).toBeEnabled();

    await act(() => router.navigate(-1));
    await userEvent.click(
      within(
        await screen.findByRole('alertdialog', { name: 'Leave without finishing?' }),
      ).getByRole('button', { name: 'Leave anyway' }),
    );
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
  });
});

/*
  One of the two accounts of a move, closed in another tab or session while this page shows it.
  Measured through the BFF on a spare account closed after the page's requests were prepared,
  as the source and as the destination, on the mint and on the send: each answers 404 errorCode
  ACCOUNT_NOT_FOUND, detail "Account with identifier '<the id>' was not found.", and
  GET /api/accounts no longer lists the account. One code for both accounts: which of the two
  is gone is read from the list. The mock answers the same once the account is out of its
  state, so these tests close the account there.
*/
describe('an account of the move is closed under the page', () => {
  const HOLIDAY_ID = '019f7b3f-0000-7000-8000-0000000000a3';

  /** A third account, so that there is another one to choose. */
  beforeEach(() => {
    mockState.accounts.push({
      id: HOLIDAY_ID,
      accountNumber: 'AB-****-****-07',
      name: 'Holiday',
      type: 'Savings',
      balance: 300,
      isPrimary: false,
      createdAt: '2026-07-09T09:00:00.0000000Z',
    });
  });

  /** As another session closes it: the account leaves the server's state, the page is not told. */
  function closeElsewhere(id: string) {
    mockState.accounts = mockState.accounts.filter((account) => account.id !== id);
  }

  /** Counts what the page asks, and lets the mock answer. */
  function watchRequests() {
    const seen = {
      lists: 0,
      mints: 0,
      sends: [] as { fromAccountId: string; toAccountId: string }[],
    };
    server.use(
      http.get('*/api/accounts', () => {
        seen.lists += 1;
      }),
      http.post('*/api/transfers/internal/authorizations', () => {
        seen.mints += 1;
      }),
      http.post('*/api/transfers/internal', async ({ request }) => {
        seen.sends.push(
          (await request.clone().json()) as { fromAccountId: string; toAccountId: string },
        );
      }),
    );
    return seen;
  }

  async function fill(from: string, to: string) {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: `From ${from}` }));
    await userEvent.click(screen.getByRole('button', { name: `To ${to}` }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
  }

  const pressed = (name: string) =>
    screen.getByRole('button', { name }).getAttribute('aria-pressed');

  function expectNothingOfTheServers() {
    expect(screen.queryByText(/Account with identifier/)).not.toBeInTheDocument();
    expect(document.body).not.toHaveTextContent(SAVINGS_ACCOUNT_ID);
  }

  it('names the source when the list comes back without it, and puts no account in its place', async () => {
    const seen = watchRequests();
    await fill('Rainy Day', 'Main Account');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByLabelText('Digit 1 of 6');

    closeElsewhere(SAVINGS_ACCOUNT_ID);
    const listsBefore = seen.lists;
    await enterPin();

    const words = await screen.findByText(MOVE_SOURCE_CLOSED);
    expect(words.closest('[role="alert"]')).not.toBeNull();
    expect(MOVE_SOURCE_CLOSED).toBe(
      'The account you are moving money from is closed. Choose another account.',
    );
    expect(seen.lists).toBeGreaterThan(listsBefore);
    expectNothingOfTheServers();
    expect(screen.queryByRole('button', { name: 'From Rainy Day' })).not.toBeInTheDocument();
    // No silent switch: neither the primary account nor the first is made the source.
    expect(pressed('From Main Account')).toBe('false');
    expect(pressed('From Holiday')).toBe('false');
    // The destination the visitor chose is still the visitor's choice.
    expect(pressed('To Main Account')).toBe('true');
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toHaveAccessibleDescription(
      'Choose an account to move money from.',
    );
    // The amount typed is kept, and is not judged against the balance of no account.
    expect(screen.queryByText(/Exceeds available balance/)).not.toBeInTheDocument();
    expect(seen.mints).toBe(1);
    expect(seen.sends).toEqual([]);

    // The visitor chooses, and only then can the money move: from the account they picked.
    await userEvent.click(screen.getByRole('button', { name: 'From Holiday' }));
    expect(screen.queryByText(MOVE_SOURCE_CLOSED)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPin();
    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();
    expect(seen.sends).toEqual([
      expect.objectContaining({ fromAccountId: HOLIDAY_ID, toAccountId: MAIN_ACCOUNT_ID }),
    ]);
  });

  it('names the destination when the send meets it closed', async () => {
    const seen = watchRequests();
    // The mint finds both accounts; the destination is closed before the send arrives.
    server.use(
      http.post('*/api/transfers/internal', () => {
        closeElsewhere(SAVINGS_ACCOUNT_ID);
      }),
    );
    await fill('Main Account', 'Rainy Day');
    await confirmWithPin();

    const words = await screen.findByText(MOVE_DESTINATION_CLOSED);
    expect(words.closest('[role="alert"]')).not.toBeNull();
    expect(MOVE_DESTINATION_CLOSED).toBe(
      'The account you are moving money to is closed. Choose another account.',
    );
    expectNothingOfTheServers();
    expect(screen.queryByRole('button', { name: 'To Rainy Day' })).not.toBeInTheDocument();
    expect(pressed('From Main Account')).toBe('true');
    expect(pressed('To Holiday')).toBe('false');
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
    expect(seen.sends).toEqual([
      expect.objectContaining({ fromAccountId: MAIN_ACCOUNT_ID, toAccountId: SAVINGS_ACCOUNT_ID }),
    ]);
    // A refusal: nothing moved, and the key of that send is not kept.
    expect(mockState.accounts.find((a) => a.id === MAIN_ACCOUNT_ID)?.balance).toBe(1250.5);
    expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();
  });

  it('is on the form with the sentence, no PIN asked and no mint, when the list read on Continue lacks the source', async () => {
    const seen = watchRequests();
    await fill('Rainy Day', 'Main Account');

    closeElsewhere(SAVINGS_ACCOUNT_ID);
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByText(MOVE_SOURCE_CLOSED)).toBeInTheDocument();
    expect(pressed('From Main Account')).toBe('false');
    expect(pressed('From Holiday')).toBe('false');
    expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
    expect(seen.mints).toBe(0);
  });

  it('is on the form with the sentence, no PIN asked and no mint, when the list read on Continue lacks the destination', async () => {
    const seen = watchRequests();
    await fill('Main Account', 'Rainy Day');

    closeElsewhere(SAVINGS_ACCOUNT_ID);
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByText(MOVE_DESTINATION_CLOSED)).toBeInTheDocument();
    expect(pressed('From Main Account')).toBe('true');
    expect(pressed('To Holiday')).toBe('false');
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
    expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
    expect(seen.mints).toBe(0);
  });

  it('says only that an account is closed, and clears both, when the list still shows both', async () => {
    /*
      The answer, and a list that does not say which account it means: here the list, read
      again, still has both. Only "one of them" is true, so both are chosen again.
    */
    const seen = watchRequests();
    await fill('Rainy Day', 'Main Account');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByLabelText('Digit 1 of 6');
    server.use(
      http.post('*/api/transfers/internal/authorizations', () =>
        problem({
          status: 404,
          errorCode: 'ACCOUNT_NOT_FOUND',
          detail: `Account with identifier '${SAVINGS_ACCOUNT_ID}' was not found.`,
        }),
      ),
    );
    const listsBefore = seen.lists;
    await enterPin();

    const words = await screen.findByText(MOVE_ACCOUNT_CLOSED);
    expect(words.closest('[role="alert"]')).not.toBeNull();
    expect(MOVE_ACCOUNT_CLOSED).toBe(
      'An account of this transfer is closed. Choose the accounts again.',
    );
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled(),
    );
    expect(seen.lists).toBeGreaterThan(listsBefore);
    expectNothingOfTheServers();
    for (const name of ['From Main Account', 'From Rainy Day', 'From Holiday']) {
      expect(pressed(name)).toBe('false');
    }
    for (const name of ['To Main Account', 'To Rainy Day', 'To Holiday']) {
      expect(pressed(name)).toBe('false');
    }
    expect(seen.sends).toEqual([]);
  });

  it('says only that an account is closed, and clears both, when the list cannot be read again', async () => {
    /*
      The answer, and no list to say which account it means: the read that follows fails. The
      page does not wait for a list it may never get, and does not keep two accounts chosen of
      which one is closed.
    */
    await fill('Rainy Day', 'Main Account');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByLabelText('Digit 1 of 6');
    const sends: unknown[] = [];
    server.use(
      http.post('*/api/transfers/internal/authorizations', () =>
        problem({
          status: 404,
          errorCode: 'ACCOUNT_NOT_FOUND',
          detail: `Account with identifier '${SAVINGS_ACCOUNT_ID}' was not found.`,
        }),
      ),
      http.get('*/api/accounts', () => problem({ status: 500, errorCode: 'INTERNAL_ERROR' })),
      http.post('*/api/transfers/internal', () => {
        sends.push(1);
      }),
    );
    await enterPin();

    const words = await screen.findByText(MOVE_ACCOUNT_CLOSED);
    expect(words.closest('[role="alert"]')).not.toBeNull();
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled(),
    );
    expectNothingOfTheServers();
    for (const name of ['From Main Account', 'From Rainy Day', 'From Holiday']) {
      expect(pressed(name)).toBe('false');
    }
    for (const name of ['To Main Account', 'To Rainy Day', 'To Holiday']) {
      expect(pressed(name)).toBe('false');
    }
    expect(sends).toEqual([]);
  });

  it('shows the check view when a PIN typed again under a kept key meets the closed account, and starts over under a new key', async () => {
    /*
      A send answered AUTHORIZATION_EXPIRED keeps its key for the same move with a new
      authorisation (ADR-0022, ADR-0042), and the visitor stays on the PIN step. The source is
      closed before the PIN is typed again, so the mint answers ACCOUNT_NOT_FOUND. The page does
      not stay on a PIN step with its exits locked: it shows the check view, whose two buttons
      are the ways on, and the next move is a new one under a new key.
    */
    const seen = watchRequests();
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transfers/internal', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        if (keys.length === 1) {
          return problem({
            status: 401,
            errorCode: 'AUTHORIZATION_EXPIRED',
            detail: 'This authorisation has expired. Enter your PIN again to confirm.',
          });
        }
      }),
    );
    await fill('Rainy Day', 'Main Account');
    await confirmWithPin();
    await screen.findByText(/your confirmation expired/);
    expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled();

    closeElsewhere(SAVINGS_ACCOUNT_ID);
    await enterPin();

    expect(await screen.findByText("Your transfer didn't go through")).toBeInTheDocument();
    expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
    expectNothingOfTheServers();
    expect(seen.mints).toBe(2);
    expect(keys).toHaveLength(1);

    /*
      The source is closed, so the listing asks the destination, which is open and has no row of
      the key. Every authorisation presented under it was refused, so nothing under it can land:
      that empty page says the move did not go through, at once.
    */
    expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();
    await settle();
    await userEvent.click(
      screen.getByRole('button', { name: "It didn't go through — start over" }),
    );
    const words = await screen.findByText(MOVE_SOURCE_CLOSED);
    expect(words.closest('[role="alert"]')).not.toBeNull();
    expect(screen.queryByRole('button', { name: 'From Rainy Day' })).not.toBeInTheDocument();
    expect(pressed('From Main Account')).toBe('false');
    expect(pressed('From Holiday')).toBe('false');
    expect(screen.getByRole('button', { name: 'Review Transfer' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();

    // The move made next is another one: it does not carry the key that was let go.
    await settle();
    await userEvent.click(screen.getByRole('button', { name: 'From Holiday' }));
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await confirmWithPin();
    expect(await screen.findByText('Transfer Complete!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[1]).not.toBe(keys[0]);
    expect(seen.sends).toEqual([
      expect.objectContaining({ fromAccountId: HOLIDAY_ID, toAccountId: MAIN_ACCOUNT_ID }),
    ]);
  });
});

/*
  The receipt replaces the PIN step, and the PIN boxes were disabled for the send: a browser hands
  their focus to `body`, where a screen reader says nothing of the receipt. The title takes that
  lost focus. TransferPage.test.tsx has the same two cases for the transfer to another person.
*/
describe('internal transfer: the receipt takes the focus the send lost', () => {
  let stopFocusFixup: () => void;
  beforeEach(() => {
    stopFocusFixup = emulateFocusFixup();
  });
  afterEach(() => {
    stopFocusFixup();
  });

  /** €50 from Main Account to Rainy Day, on the PIN step before its first digit. */
  async function toPin() {
    renderInternal();
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByLabelText('Digit 1 of 6');
  }

  it('the sixth digit of the PIN leaves focus on the receipt’s title, read once, and Tab goes on to View History', async () => {
    await toPin();
    await enterPin();

    const title = await screen.findByText('Transfer Complete!');
    await waitFor(() => expect(title).toHaveFocus());
    expect(title.tagName).toBe('P');
    expect(title).toHaveAttribute('tabindex', '-1');
    expect(title.closest('[role="alert"], [role="status"], [aria-live]')).toBeNull();
    for (const button of screen.getAllByRole('button')) {
      expect(button).not.toHaveAttribute('aria-describedby');
    }

    await userEvent.tab();
    expect(screen.getByRole('button', { name: 'View History' })).toHaveFocus();
  });

  it('leaves focus where the visitor put it during the send', async () => {
    let answer = () => {};
    const held = new Promise<void>((resolve) => {
      answer = resolve;
    });
    let sent = false;
    server.use(
      // Returns nothing, so the send goes on to the mock's own handler once answered.
      http.post('*/api/transfers/internal', async () => {
        sent = true;
        await held;
      }),
    );
    await toPin();
    await enterPin();
    await waitFor(() => expect(sent).toBe(true));

    const elsewhere = document.createElement('button');
    elsewhere.textContent = 'Somewhere else';
    document.body.append(elsewhere);
    try {
      elsewhere.focus();
      expect(elsewhere).toHaveFocus();

      answer();
      const title = await screen.findByText('Transfer Complete!');
      await settle();
      expect(elsewhere).toHaveFocus();
      expect(title).not.toHaveFocus();
    } finally {
      // The handler is parked on this promise: a failed assertion must not leave it hanging.
      answer();
      elsewhere.remove();
    }
  });
});
