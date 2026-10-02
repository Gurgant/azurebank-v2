import { Route, Routes } from 'react-router-dom';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { renderWithProviders } from '../../test/renderWithProviders';
import { COPY, committedAnswerLost, emulateFocusFixup } from '../../test/outage';
import { DepositDialog } from './DepositDialog';

/**
 * T3 (PR-9) — the first production idempotent mutation. Pins the useIdempotentMutation
 * contract end-to-end: replay note (D4), KEEP-on-IN_FLIGHT with the SAME key on retry,
 * verify-first on a body edit while a key is held, RESULT_UNKNOWN verify-first flow, and the
 * generic (never-raw) message for a client-protocol KEY_REUSE bug (§2.3 / D17).
 */

const MAIN = mockState.accounts[0]; // seed: id a1, balance 1250.5

function seedAccount() {
  return { id: MAIN.id, name: 'Main Account', accountNumber: 'AB-****-****-90', balance: 1250.5 };
}

function renderDeposit(onClose: () => void = () => {}) {
  return renderWithProviders(
    <Routes>
      <Route
        path="/"
        element={<DepositDialog isOpen onClose={onClose} accounts={[seedAccount()]} />}
      />
      <Route path="/transactions/:id" element={<div>TX DETAIL PAGE</div>} />
      <Route path="/history" element={<div>HISTORY PAGE</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

function depositSuccessBody(newBalance: number, replayed = false) {
  return HttpResponse.json(
    {
      data: {
        transaction: {
          id: '019f7b3f-0000-7000-8000-000000000d99',
          transactionNumber: 'TXN-20260722-000999',
          type: 'Deposit',
          amount: 100,
          balanceAfter: newBalance,
          description: null,
          recipientAzureTag: null,
          senderAzureTag: null,
          status: 'Completed',
          createdAt: '2026-07-22T10:00:00.0000000Z',
        },
        newBalance,
      },
      message: 'Deposit completed successfully.',
    },
    { status: 201, headers: replayed ? { 'Idempotency-Replayed': 'true' } : {} },
  );
}

describe('deposit (T3 — idempotent mutation)', () => {
  it('deposits and shows the receipt with the real new balance, then navigates to the transaction', async () => {
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    expect(screen.getByText('New balance: €1,350.50')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(screen.getByText('+€100.00')).toBeInTheDocument();
    expect(screen.getByText('€1,350.50')).toBeInTheDocument(); // new balance in receipt

    await userEvent.click(screen.getByRole('button', { name: 'View Transaction' }));
    expect(await screen.findByText('TX DETAIL PAGE')).toBeInTheDocument();
  });

  it('surfaces the polite replay note on a replayed 2xx (D4)', async () => {
    server.use(http.post('*/api/transactions/deposit', () => depositSuccessBody(1350.5, true)));
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(screen.getByText(/already processed — showing the existing result/)).toBeInTheDocument();
  });

  it('KEEPS the key on IN_FLIGHT and retries with the SAME key (no client double-spend)', async () => {
    const keys: (string | null)[] = [];
    let calls = 0;
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        calls += 1;
        return calls === 1
          ? problem({
              status: 409,
              errorCode: 'IDEMPOTENCY_IN_FLIGHT',
              detail: 'Still processing.',
            })
          : depositSuccessBody(1350.5);
      }),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText(/Still processing/)).toBeInTheDocument();

    // Retry WITHOUT editing the body — same key must be reused.
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  it('asks for a check, and sends no second key, when the amount is edited while a key is held', async () => {
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        // The first attempt KEEPS its key: that deposit may still land.
        return keys.length === 1
          ? problem({
              status: 409,
              errorCode: 'IDEMPOTENCY_IN_FLIGHT',
              detail: 'Still processing.',
            })
          : depositSuccessBody(1450.5);
      }),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await screen.findByText(/Still processing/);

    // A new key for the edited amount would be a second deposit beside one that may still land.
    await userEvent.click(screen.getByRole('button', { name: '€200' }));
    expect(await screen.findByText("We couldn't confirm your deposit")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Deposit/ })).not.toBeInTheDocument();
    // "Tap Deposit again" goes with the button it pointed at.
    expect(screen.queryByText(/Still processing/)).not.toBeInTheDocument();
    expect(keys).toHaveLength(1);

    // Only the explicit "it didn't go through" starts a new intent, and so a new key.
    await userEvent.click(screen.getByRole('button', { name: /didn't go through/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €200.00' }));
    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[1]).not.toBe(keys[0]);
  });

  it('RESULT_UNKNOWN latches a verify-first flow, not a blind retry (§2.3)', async () => {
    server.use(
      http.post('*/api/transactions/deposit', () =>
        problem({
          status: 409,
          errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
          detail: 'Could not confirm.',
        }),
      ),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText("We couldn't confirm your deposit")).toBeInTheDocument();
    // The blind Deposit button is gone; only verify actions remain.
    expect(screen.queryByRole('button', { name: /^Deposit/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Check recent transactions' }));
    expect(await screen.findByText('HISTORY PAGE')).toBeInTheDocument();
  });

  it('shows a generic message for a KEY_REUSE client-protocol bug, never the raw code (D17)', async () => {
    server.use(
      http.post('*/api/transactions/deposit', () =>
        problem({
          status: 422,
          errorCode: 'IDEMPOTENCY_KEY_REUSE',
          detail: 'This idempotency key was already used with a different payload.',
        }),
      ),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
    expect(screen.queryByText(/IDEMPOTENCY_KEY_REUSE/)).not.toBeInTheDocument();
    expect(screen.queryByText(/idempotency key/i)).not.toBeInTheDocument();
  });

  it('disables the CTA until a valid amount is entered', async () => {
    renderDeposit();
    expect(screen.getByRole('button', { name: /^Deposit/ })).toBeDisabled();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    expect(screen.getByRole('button', { name: 'Deposit €100.00' })).toBeEnabled();
  });

  it('cannot be dismissed mid-flight — the close controls are disabled so the key survives', async () => {
    // Never-resolving deposit keeps the submit in flight. Unmounting the dialog now
    // would destroy the in-memory key → a reopened deposit would double-spend.
    server.use(http.post('*/api/transactions/deposit', () => new Promise<Response>(() => {})));
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled());
    expect(screen.getByText('Deposit Money')).toBeInTheDocument(); // still open
  });

  it('freezes body edits mid-flight so the pending intent cannot be rotated/nulled', async () => {
    // A body edit during submit would run onBodyEdit → resetIntent(), dropping the retained
    // key out from under the in-flight request; a later NETWORK/5xx could then close or
    // resubmit into a NEW intent. Every body control is disabled while submitting.
    server.use(http.post('*/api/transactions/deposit', () => new Promise<Response>(() => {})));
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    await waitFor(() => expect(screen.getByLabelText('Deposit amount')).toBeDisabled());
    expect(screen.getByRole('button', { name: '€200' })).toBeDisabled();
    expect(screen.getByLabelText('Description')).toBeDisabled();
  });

  it('ignores account-card selection mid-flight — the div guard holds the live intent', async () => {
    // Account cards are <div>s (no `disabled`), so handleSelectAccount's isSubmitting guard is
    // their ONLY protection: switching accounts mid-flight would run resetIntent() + change the
    // body under the pending request. The new-balance preview is the observable for which
    // account is selected (Main €1,000 vs Rainy Day €500).
    server.use(http.post('*/api/transactions/deposit', () => new Promise<Response>(() => {})));
    const a1 = {
      id: MAIN.id,
      name: 'Main Account',
      accountNumber: 'AB-••••-••••-90',
      balance: 1000,
    };
    const a2 = {
      id: '019f7b3f-0000-7000-8000-0000000000a2',
      name: 'Rainy Day',
      accountNumber: 'AB-••••-••••-01',
      balance: 500,
    };
    renderWithProviders(
      <Routes>
        <Route path="/" element={<DepositDialog isOpen onClose={() => {}} accounts={[a1, a2]} />} />
      </Routes>,
      { routerEntries: ['/'] },
    );

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    expect(screen.getByText('New balance: €1,100.00')).toBeInTheDocument(); // a1 (Main) selected
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await waitFor(() => expect(screen.getByLabelText('Deposit amount')).toBeDisabled());

    // Try to switch to Rainy Day while the request is held — must be a no-op.
    await userEvent.click(screen.getByText('Rainy Day'));
    expect(screen.getByText('New balance: €1,100.00')).toBeInTheDocument(); // still Main Account
    expect(screen.queryByText('New balance: €600.00')).not.toBeInTheDocument();
  });

  it('shows an inline hint and disables the CTA for an over-limit amount', async () => {
    // 100,001: one euro above the CONTRACT's bound and well inside the 1,000,000 the form used to
    // allow, so this is red on the old constant and green on the new — the old '1000001' proved
    // nothing about the fix, being above both.
    renderDeposit();
    await userEvent.type(screen.getByLabelText('Deposit amount'), '100001');

    expect(screen.getByText('Maximum deposit is €100,000.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Deposit/ })).toBeDisabled();
  });

  it('collapses a multi-dot amount to a single decimal', async () => {
    renderDeposit();
    await userEvent.type(screen.getByLabelText('Deposit amount'), '1.2.3');
    expect(screen.getByLabelText('Deposit amount')).toHaveValue('1.23');
  });

  it('shows a friendly message on a transport failure, never the raw error (D17)', async () => {
    server.use(http.post('*/api/transactions/deposit', () => HttpResponse.error()));
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText(/Couldn't reach the server/)).toBeInTheDocument();
    expect(screen.queryByText(/Failed to fetch/i)).not.toBeInTheDocument();
  });

  it('a double-click on the CTA sends exactly ONE request (anti-double-spend, P6 matrix)', async () => {
    let calls = 0;
    server.use(
      http.post('*/api/transactions/deposit', () => {
        calls += 1;
        return new Promise<Response>(() => {}); // hold in flight
      }),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    const cta = screen.getByRole('button', { name: 'Deposit €100.00' });
    await userEvent.click(cta);
    // Second click lands while the first is in flight — the isSubmitting guard
    // (disabled CTA) must swallow it: one request, one idempotency intent.
    await userEvent.click(cta);
    await waitFor(() => expect(cta).toBeDisabled());
    expect(calls).toBe(1);
  });

  // ===== The Fluent-Dialog shell (the RHF rewrite's a11y upgrade) =====

  it('Escape dismisses the dialog when no key is live', async () => {
    let closed = false;
    renderWithProviders(
      <Routes>
        <Route
          path="/"
          element={
            <DepositDialog
              isOpen
              onClose={() => {
                closed = true;
              }}
              accounts={[seedAccount()]}
            />
          }
        />
      </Routes>,
      { routerEntries: ['/'] },
    );

    await userEvent.keyboard('{Escape}');
    await waitFor(() => expect(closed).toBe(true));
  });

  it('Escape is a NO-OP mid-flight — the same keyLive guard as the X button', async () => {
    server.use(http.post('*/api/transactions/deposit', () => new Promise<Response>(() => {})));
    let closed = false;
    renderWithProviders(
      <Routes>
        <Route
          path="/"
          element={
            <DepositDialog
              isOpen
              onClose={() => {
                closed = true;
              }}
              accounts={[seedAccount()]}
            />
          }
        />
      </Routes>,
      { routerEntries: ['/'] },
    );

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled());

    await userEvent.keyboard('{Escape}');
    expect(closed).toBe(false);
    expect(screen.getByText('Deposit Money')).toBeInTheDocument(); // still open
  });
});

/**
 * The dialog has drawn what it made of the answer: the one send reached the server, and Close,
 * barred while the key was live, is back. True of every outcome that drops the key, so a test can
 * then say which view is on screen instead of waiting for the one it hopes for.
 */
async function answerDrawn(keys: unknown[]) {
  await waitFor(() => expect(keys).toHaveLength(1));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled());
}

/**
 * A deposit the server says went through, when its receipt cannot be shown: the API's 409
 * `IDEMPOTENCY_RESULT_UNKNOWN` carrying `applied: true` (ADR-0009). Until that member existed the
 * dialog drew the check view for it, and "It didn't go through — try again" followed by Deposit
 * was a second deposit under a new key: measured on the running app, two deposits of one amount.
 */
describe('deposit — the server says it went through', () => {
  // The Deposit button is disabled during the send, and a browser hands its focus to `body`.
  let stopFocusFixup: () => void;
  beforeEach(() => {
    stopFocusFixup = emulateFocusFixup();
  });
  afterEach(() => {
    stopFocusFixup();
  });

  /** What each button is called: its label when it has no text of its own (the shell's X). */
  const names = (buttons: HTMLElement[]) =>
    buttons.map((button) => button.getAttribute('aria-label') ?? button.textContent);

  it('says the deposit went through, under the receipt’s title, and offers the history and nothing that could send again', async () => {
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        return committedAnswerLost('/api/transactions/deposit');
      }),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    const depositButton = screen.getByRole('button', { name: 'Deposit €100.00' });
    await userEvent.click(depositButton);

    // The check view is what the dialog drew for this answer before it read `applied`.
    await answerDrawn(keys);
    expect(screen.queryByText("We couldn't confirm your deposit")).not.toBeInTheDocument();
    const sentence = screen.getByText(COPY.depositWentThrough);
    // The receipt's title, which is also the dialog's accessible name.
    const dialog = screen.getByRole('dialog', { name: 'Deposit Complete' });

    // The shell's X, and the one action. No Deposit, no "try again", no check view.
    expect(names(within(dialog).getAllByRole('button'))).toEqual(['Close', 'View History']);
    expect(within(dialog).getByRole('button', { name: 'Close' })).toBeEnabled();
    expect(screen.queryByText(/didn't go through/)).not.toBeInTheDocument();
    expect(screen.queryByText(/may or may not/)).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Deposit amount')).not.toBeInTheDocument();

    // Said once, by focus: no alert and no status bar with words in the dialog, the sentence in
    // no live region, and focus on the sentence itself.
    const spoken = Array.from(
      dialog.querySelectorAll<HTMLElement>('[role="alert"], [role="status"]'),
    ).filter((region) => region.textContent?.trim());
    expect(spoken.map((region) => region.textContent)).toEqual([]);
    expect(sentence.closest('[role="alert"], [role="status"], [aria-live]')).toBeNull();
    await waitFor(() => expect(sentence).toHaveFocus());
    // It takes focus without joining the Tab order: a paragraph is not a stop between the
    // dialog's X and its one action.
    expect(sentence).toHaveAttribute('tabindex', '-1');

    /*
      A NEW node, not the Deposit button with other words. Both are the footer's only button, so
      without a key of its own React would keep the node — and the focus that never left it: a
      second press of "Deposit" would land on "View History", and the sentence would never be
      reached.
    */
    const viewHistory = within(dialog).getByRole('button', { name: 'View History' });
    expect(viewHistory).not.toBe(depositButton);
    expect(depositButton).not.toBeInTheDocument();
    expect(viewHistory).not.toHaveAttribute('aria-describedby');

    expect(keys).toHaveLength(1);
    await userEvent.click(viewHistory);
    expect(await screen.findByText('HISTORY PAGE')).toBeInTheDocument();
    expect(keys).toHaveLength(1);
  });

  it('a press outside the dialog leaves the sentence on screen, and Close and Escape still close it', async () => {
    /*
      The view is shorter than the form it replaces, so the dialog shrinks under the pointer.
      Measured in Chromium on the running stack: the second press of a double click on Deposit
      landed on the backdrop and closed the dialog a tenth of a second after the sentence had
      appeared. The visitor was left on the page behind it, told nothing.
    */
    const closed = vi.fn();
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        return committedAnswerLost('/api/transactions/deposit');
      }),
    );
    renderDeposit(closed);
    const backdrop = () => {
      const element = document.querySelector<HTMLElement>('.fui-DialogSurface__backdrop');
      if (!element) throw new Error('the dialog has no backdrop to press');
      return element;
    };

    // The control: over the form a press outside closes the dialog, so the press does arrive.
    await userEvent.click(backdrop());
    expect(closed).toHaveBeenCalledTimes(1);

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await answerDrawn(keys);
    const sentence = screen.getByText(COPY.depositWentThrough);
    await waitFor(() => expect(sentence).toHaveFocus());

    await userEvent.click(backdrop());
    expect(closed).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('dialog', { name: 'Deposit Complete' })).toBeInTheDocument();
    expect(screen.getByText(COPY.depositWentThrough)).toBeInTheDocument();
    // Nor does the press take focus out of the dialog: from `body` Escape reaches no dialog.
    expect(sentence).toHaveFocus();

    // The exits nobody presses by accident are open: the view is not a trap. Escape as a
    // visitor presses it, wherever focus is, not aimed at the dialog.
    await userEvent.keyboard('{Escape}');
    expect(closed).toHaveBeenCalledTimes(2);
    await userEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(closed).toHaveBeenCalledTimes(3);
    expect(keys).toHaveLength(1);
  });

  it('a press outside the dialog leaves the check view on screen too, and the form that "try again" brings back closes as before', async () => {
    /*
      The check view is shorter than the form as well. Measured in Chromium on the running stack,
      the send answered 409 with no `applied`: the second press of a double click on Deposit
      landed on the backdrop and closed the dialog. The visitor was left on the page behind it,
      "We couldn't confirm your deposit" unread, beside a Deposit tile to press again: the second
      deposit this view is there to prevent.
    */
    const closed = vi.fn();
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        return problem({
          status: 409,
          errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
          detail: 'Could not confirm.',
        });
      }),
    );
    renderDeposit(closed);
    const backdrop = () => {
      const element = document.querySelector<HTMLElement>('.fui-DialogSurface__backdrop');
      if (!element) throw new Error('the dialog has no backdrop to press');
      return element;
    };

    // The control: over the form a press outside closes the dialog, so the press does arrive.
    await userEvent.click(backdrop());
    expect(closed).toHaveBeenCalledTimes(1);

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await answerDrawn(keys);
    expect(screen.getByText("We couldn't confirm your deposit")).toBeInTheDocument();
    expect(screen.queryByText(COPY.depositWentThrough)).not.toBeInTheDocument();
    // This view lands no focus. A visitor's Tab puts it on the first action: their own place.
    const checkTransactions = screen.getByRole('button', { name: 'Check recent transactions' });
    checkTransactions.focus();

    await userEvent.click(backdrop());
    expect(closed).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('dialog', { name: 'Deposit Money' })).toBeInTheDocument();
    expect(screen.getByText("We couldn't confirm your deposit")).toBeInTheDocument();
    expect(screen.getByText(/retrying blindly could deposit twice/)).toBeInTheDocument();
    // Nor does the press take the visitor's place: from `body` Escape reaches no dialog.
    expect(checkTransactions).toHaveFocus();

    // The exits nobody presses by accident are open: the view is not a trap.
    await userEvent.keyboard('{Escape}');
    expect(closed).toHaveBeenCalledTimes(2);
    await userEvent.click(screen.getByRole('button', { name: 'Close' }));
    expect(closed).toHaveBeenCalledTimes(3);

    // Only this view is kept. "Try again" brings the form back, and a press outside the form
    // closes the dialog as it did before the send.
    await userEvent.click(screen.getByRole('button', { name: /didn't go through/ }));
    expect(screen.getByLabelText('Deposit amount')).toBeInTheDocument();
    await userEvent.click(backdrop());
    expect(closed).toHaveBeenCalledTimes(4);
    expect(keys).toHaveLength(1);
  });

  it('takes focus from the dialog itself, where a press during the send had left it', async () => {
    /*
      The other half of a double click: the answer takes longer than the gap between the two
      presses. Measured in Chromium on the running stack: the second press came on the disabled
      Deposit button while the send was still out, the browser gave focus to the nearest ancestor
      that can hold it, the dialog's own surface, and the view then appeared with focus still
      there and its sentence unfocused. jsdom moves no focus for a press on a disabled control,
      so the test puts focus where the browser was measured to put it.
    */
    const keys: (string | null)[] = [];
    let answer = () => {};
    const held = new Promise<void>((resolve) => {
      answer = resolve;
    });
    server.use(
      http.post('*/api/transactions/deposit', async ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        await held;
        return committedAnswerLost('/api/transactions/deposit');
      }),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await waitFor(() => expect(keys).toHaveLength(1));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled());

    const dialog = screen.getByRole('dialog', { name: 'Deposit Money' });
    dialog.focus();
    expect(dialog).toHaveFocus();

    answer();
    await answerDrawn(keys);
    expect(screen.getByRole('dialog', { name: 'Deposit Complete' })).toBe(dialog);
    await waitFor(() => expect(screen.getByText(COPY.depositWentThrough)).toHaveFocus());
    expect(keys).toHaveLength(1);
  });

  it('a 409 whose body cannot be trusted shows the check view, not "Deposit failed"', async () => {
    /*
      A wrong-typed member makes the whole body untrusted, so this 409 names no code the SPA can
      read. It may have been the answer above, or IN_FLIGHT: a deposit that may have landed. The
      dialog said "Deposit failed. Please try again." and the next press was a new key.
    */
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        return problem({
          status: 409,
          errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
          extensions: { applied: 'true' },
        });
      }),
    );
    renderDeposit();

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    await answerDrawn(keys);
    expect(screen.queryByText(/Deposit failed/)).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByText("We couldn't confirm your deposit")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Deposit/ })).not.toBeInTheDocument();
    // Not proven either: this is the check view, not the went-through one.
    expect(screen.queryByText(COPY.depositWentThrough)).not.toBeInTheDocument();
    expect(keys).toHaveLength(1);
  });
});
