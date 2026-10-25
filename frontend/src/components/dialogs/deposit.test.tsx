import { Route, Routes } from 'react-router-dom';
import { act, cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { problem, serviceUnavailable } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { renderWithProviders } from '../../test/renderWithProviders';
import { settle } from '../../test/settle';
import { expectNoNestedLiveRegions } from '../../test/liveRegions';
import {
  COPY,
  advance,
  advanceUntil,
  committedAnswerLost,
  emulateFocusFixup,
  expectTheUnreadableAnswerLogged,
  fakeClockUser,
  installFakeClock,
  lookupsSeen,
  loseAnswerAfterCommit,
  notKnown,
  whenListedByKey,
} from '../../test/outage';
import { formatDateTime } from '../../utils/format';
import { DepositDialog } from './DepositDialog';

/**
 * T3 (PR-9) — the first production idempotent mutation. Pins the useIdempotentMutation
 * contract end-to-end: replay note, KEEP-on-IN_FLIGHT with the SAME key on retry,
 * verify-first on a body edit while a key is held, RESULT_UNKNOWN verify-first flow, and the
 * generic (never-raw) message for a client-protocol KEY_REUSE bug (ADR-0022).
 */

// Unmount first, then restore the clock: withdraw.test.tsx has the reason.
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

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
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        sentAt = Date.now();
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
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await screen.findByText(/Still processing/);

    // A new key for the edited amount would be a second deposit beside one that may still land.
    await user.click(screen.getByRole('button', { name: '€200' }));
    expect(await screen.findByText("We couldn't confirm your deposit")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Deposit/ })).not.toBeInTheDocument();
    // "Tap Deposit again" goes with the button it pointed at.
    expect(screen.queryByText(/Still processing/)).not.toBeInTheDocument();
    expect(keys).toHaveLength(1);
    // The listing has nothing yet, and the first deposit may still land: no new key yet.
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /didn't go through/ })).not.toBeInTheDocument();

    // Once nothing sent under that key can land, the listing's empty page says it did not.
    await advanceUntil(sentAt, 185_000);
    expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();

    // Only the explicit "it didn't go through" starts a new intent, and so a new key.
    await settle();
    await user.click(screen.getByRole('button', { name: /didn't go through/ }));
    // The form that comes back waits too, before its Deposit sends.
    await settle();
    await user.click(screen.getByRole('button', { name: 'Deposit €200.00' }));
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
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    await settle();
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

  /*
    The visible label is a `Text` beside the field, not its `<label>`: the field's name comes from
    its `aria-label`. A visitor who speaks the word they see has to find it in that name
    (WCAG 2.5.3), so the word on screen is held inside the name, whatever its case.
  */
  it('labels the field "Amount", a word its accessible name contains', async () => {
    renderDeposit();
    // Found by waiting, so the form's first validation on mount lands inside `act`.
    const label = await screen.findByText('Amount');
    expect(screen.getByRole('textbox', { name: 'Deposit amount' })).toHaveAccessibleName(
      new RegExp(`\\b${label.textContent}\\b`, 'i'),
    );
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

/*
  The receipt replaces the form, and the Deposit button that sent was disabled for the send: a
  browser hands its focus to `body`, where a screen reader says nothing of the receipt and Escape
  reaches no dialog. The title takes that lost focus, as the went-through sentence and the check
  view's title do.
*/
describe('deposit — the receipt takes the focus the send lost', () => {
  let stopFocusFixup: () => void;
  beforeEach(() => {
    stopFocusFixup = emulateFocusFixup();
  });
  afterEach(() => {
    stopFocusFixup();
  });

  it('Enter on Deposit leaves focus on the receipt’s title, read once, and Tab goes on to its first button', async () => {
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    screen.getByRole('button', { name: 'Deposit €100.00' }).focus();
    await userEvent.keyboard('{Enter}');

    const title = await screen.findByText('Deposit Successful!');
    await waitFor(() => expect(title).toHaveFocus());
    // A paragraph that takes focus without joining the Tab order, in no live region and named by
    // no button's description: focus is what reads it, and only once.
    expect(title.tagName).toBe('P');
    expect(title).toHaveAttribute('tabindex', '-1');
    expect(title.closest('[role="alert"], [role="status"], [aria-live]')).toBeNull();
    const viewTransaction = screen.getByRole('button', { name: 'View Transaction' });
    expect(viewTransaction).not.toHaveAttribute('aria-describedby');
    expect(screen.getByRole('button', { name: 'Done' })).not.toHaveAttribute('aria-describedby');

    await userEvent.tab();
    expect(viewTransaction).toHaveFocus();
  });

  it('leaves focus where the visitor put it during the send', async () => {
    let answer = () => {};
    const held = new Promise<void>((resolve) => {
      answer = resolve;
    });
    server.use(
      http.post('*/api/transactions/deposit', async () => {
        await held;
        return depositSuccessBody(1350.5);
      }),
    );
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled());

    const elsewhere = document.createElement('button');
    elsewhere.textContent = 'Somewhere else';
    document.body.append(elsewhere);
    try {
      elsewhere.focus();
      expect(elsewhere).toHaveFocus();

      answer();
      const title = await screen.findByText('Deposit Successful!');
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
    await settle();
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
      The check view stands where the form was as well. Measured in Chromium at 1280×900, 375×812
      and 360×800, until the listing answers, the second press of a double click on Deposit lands on
      the backdrop. (Once the view says "not yet", it lands in the footer's padding at 1280×900 and
      375×812, and on "Check recent transactions" at 360×800, 375×640 and 320×568, where the footer
      drops it while the view settles: `e2e/wentThrough.spec.ts` has what was measured, and the test
      in this file that drops a press on the check view.) Measured in Chromium on the running stack
      with the code as it was before this branch, when the view did not ask the listing by key, the
      send answered 409 with no `applied`: the second press landed on the backdrop and closed the
      dialog. Recorded after it: no dialog, the dashboard's address, focus on `body`, no alert. So
      "We couldn't confirm your deposit" went unread, and the dashboard has the Deposit tile (read
      in its code, not recorded by that run) to press again: the second deposit this view is there
      to prevent.
    */
    const closed = vi.fn();
    const keys: (string | null)[] = [];
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        sentAt = Date.now();
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
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await answerDrawn(keys);
    expect(screen.getByText("We couldn't confirm your deposit")).toBeInTheDocument();
    expect(screen.queryByText(COPY.depositWentThrough)).not.toBeInTheDocument();
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    // This view puts lost focus on its title. The test moves it to an action, as a visitor who
    // has reached one has: their own place. (In Chromium one Tab from the title went to "Check
    // again", measured against the mock dev server; jsdom is not asked where a Tab goes.)
    const checkTransactions = screen.getByRole('button', { name: 'Check recent transactions' });
    checkTransactions.focus();

    await user.click(backdrop());
    expect(closed).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('dialog', { name: 'Deposit Money' })).toBeInTheDocument();
    expect(screen.getByText("We couldn't confirm your deposit")).toBeInTheDocument();
    expect(screen.getByText(/retrying blindly could deposit twice/)).toBeInTheDocument();
    // Nor does the press take the visitor's place: from `body` Escape reaches no dialog.
    expect(checkTransactions).toHaveFocus();

    // The exits nobody presses by accident are open: the view is not a trap.
    await user.keyboard('{Escape}');
    expect(closed).toHaveBeenCalledTimes(2);
    await user.click(screen.getByRole('button', { name: 'Close' }));
    expect(closed).toHaveBeenCalledTimes(3);

    // Only this view is kept. Once the listing says the deposit did not land, "try again"
    // brings the form back, and a press outside the form closes the dialog as it did before
    // the send.
    await advanceUntil(sentAt, 185_000);
    expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();
    await settle();
    await user.click(screen.getByRole('button', { name: /didn't go through/ }));
    expect(screen.getByLabelText('Deposit amount')).toBeInTheDocument();
    await user.click(backdrop());
    expect(closed).toHaveBeenCalledTimes(4);
    expect(keys).toHaveLength(1);
  });

  it('a press outside the dialog still closes the success receipt: the guard is not on it', async () => {
    /*
      Pins what was left as it was. The receipt is shorter than the form too. Measured in Chromium
      on the running stack with a real deposit: when the 201 came before the second press of a
      double click, that press landed on the backdrop and the receipt was gone 53 ms after it
      had appeared; one request, one deposit. Keeping the receipt open would be a decision of its
      own. This test fails if the guard comes to cover the receipt without one.
    */
    const closed = vi.fn();
    renderDeposit(closed);
    const backdrop = () => {
      const element = document.querySelector<HTMLElement>('.fui-DialogSurface__backdrop');
      if (!element) throw new Error('the dialog has no backdrop to press');
      return element;
    };

    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Deposit Complete' })).toBeInTheDocument();
    expect(screen.queryByText(COPY.depositWentThrough)).not.toBeInTheDocument();
    expect(closed).not.toHaveBeenCalled();

    await userEvent.click(backdrop());
    expect(closed).toHaveBeenCalledTimes(1);
  });

  it('takes focus from the dialog itself, where a press during the send had left it', async () => {
    /*
      The other half of a double click: the answer takes longer than the gap between the two
      presses. Measured in Chromium on the running stack: the second press came on the disabled
      Deposit button while the send was still out, focus went to the dialog's own `div`, and the
      view then appeared with focus still there and its sentence unfocused. jsdom moves no focus
      for a press on a disabled control, so the test puts focus where it was measured to go.
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
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    expect(screen.queryByText(/Deposit failed/)).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByText("We couldn't confirm your deposit")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Deposit/ })).not.toBeInTheDocument();
    // Not proven either: this is the check view, not the went-through one.
    expect(screen.queryByText(COPY.depositWentThrough)).not.toBeInTheDocument();
    expect(keys).toHaveLength(1);
  });
});

/**
 * A deposit whose answer was lost, and what the listing by its key then says (ADR-0022). The
 * mock cannot model a claim that is still live, so the clock is the client's: installed before
 * the send, and counted from the instant the send reached the mock.
 */
describe('deposit — the listing by key after a lost answer', () => {
  // The Deposit button is disabled during the send, and a browser hands its focus to `body`.
  let stopFocusFixup: () => void;
  beforeEach(() => {
    stopFocusFixup = emulateFocusFixup();
  });
  afterEach(() => {
    stopFocusFixup();
  });

  const names = (buttons: HTMLElement[]) =>
    buttons.map((button) => button.getAttribute('aria-label') ?? button.textContent);

  /** The buttons of the dialog's footer and body, without the shell's X. */
  const actions = () => names(screen.getAllByRole('button')).filter((name) => name !== 'Close');

  /*
    The three ways the answer of a deposit that was made is lost, each of which latches the check:
    a 409 that names no outcome, a 409 whose body names no code, and a 201 whose envelope carries
    no data (which RTK logs, once).
  */
  it.each([
    ['a 409 that names no outcome', () => notKnown('/api/transactions/deposit'), false],
    [
      'a 409 that names no code',
      () => HttpResponse.json({ title: 'Conflict', status: 409 }, { status: 409 }),
      false,
    ],
    [
      'a 201 with no data',
      () => HttpResponse.json({ data: null, message: 'Done.' }, { status: 201 }),
      true,
    ],
  ])(
    'answered %s, finds the deposit the server made, and says so with its amount, reference and time',
    async (_name, lost, logged) => {
      const expectLogged = logged ? expectTheUnreadableAnswerLogged() : () => {};
      const sentAt = loseAnswerAfterCommit('/api/transactions/deposit', lost);
      const seen = lookupsSeen();
      renderDeposit();

      await userEvent.click(screen.getByRole('button', { name: '€100' }));
      await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

      const sentence = await screen.findByText(COPY.depositFound);
      const row = mockState.transactions.find((entry) => entry.idempotencyKey === seen[0]?.key);
      expect(row).toBeDefined();
      const dialog = screen.getByRole('dialog', { name: 'Deposit Complete' });
      await waitFor(() => expect(sentence).toHaveFocus());
      expect(within(dialog).getByText('+€100.00')).toBeInTheDocument();
      expect(within(dialog).getByText('Reference').nextSibling).toHaveTextContent(
        row?.transactionNumber ?? '(no row)',
      );
      expect(within(dialog).getByText('Date & time').nextSibling).toHaveTextContent(
        formatDateTime(row?.createdAt ?? ''),
      );
      expect(names(within(dialog).getAllByRole('button'))).toEqual(['Close', 'View History']);
      expect(screen.queryByText("We couldn't confirm your deposit")).not.toBeInTheDocument();
      expect(screen.queryByText(/didn't go through/)).not.toBeInTheDocument();
      // One deposit, asked about once on the account it was sent to.
      expect(sentAt).toHaveLength(1);
      expect(seen).toHaveLength(1);
      expect(seen[0].accountId).toBe(MAIN.id);
      expectLogged();
    },
  );

  it('says "not yet" while the deposit may still land, then "it didn\'t go through" once nothing can, and only then a new key', async () => {
    const keys: (string | null)[] = [];
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        sentAt = Date.now();
        return keys.length === 1 ? notKnown('/api/transactions/deposit') : undefined;
      }),
    );
    const seen = lookupsSeen();
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    const beforeClick = Date.now();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    expect(actions()).toEqual(['Check again', 'Check recent transactions']);
    expect(screen.getByText(/may or may not have gone through/)).toBeInTheDocument();
    expectNoNestedLiveRegions();

    // A second short of the bound: still not yet, and nothing more asked.
    await advanceUntil(beforeClick, 184_000);
    expect(screen.getByText(COPY.movementNotYet)).toBeInTheDocument();
    expect(seen).toHaveLength(1);
    expect(actions()).toEqual(['Check again', 'Check recent transactions']);

    await advanceUntil(sentAt, 185_000);
    expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();
    expect(seen).toHaveLength(2);
    expect(actions()).toEqual(['Check recent transactions', "It didn't go through — try again"]);
    // "May or may not" is no longer true, and is no longer said.
    expect(screen.queryByText(/may or may not have gone through/)).not.toBeInTheDocument();

    await settle();
    await user.click(screen.getByRole('button', { name: "It didn't go through — try again" }));
    await settle();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[1]).not.toBe(keys[0]);
  });

  it('offers no new key while the listing cannot answer and the deposit may still land, and offers it once nothing can', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/deposit', () => {
        sentAt = Date.now();
        return HttpResponse.error();
      }),
    );
    whenListedByKey(() => serviceUnavailable({ via: 'api', instance: '/api/transactions' }));
    const seen = lookupsSeen();
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    const beforeClick = Date.now();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await screen.findByText(/Couldn't reach the server/);

    // The key is held after no connection; an edit asks for the check over it.
    await user.click(screen.getByRole('button', { name: '€200' }));
    expect(await screen.findByText(COPY.movementUncheckedNotYet)).toBeInTheDocument();
    expect(actions()).toEqual(['Check again', 'Check recent transactions']);

    await advanceUntil(beforeClick, 184_000);
    expect(screen.queryByRole('button', { name: /didn't go through/ })).not.toBeInTheDocument();
    expect(seen).toHaveLength(1);

    await advanceUntil(sentAt, 185_000);
    expect(await screen.findByText(COPY.movementUnchecked)).toBeInTheDocument();
    expect(seen).toHaveLength(2);
    expect(actions()).toEqual([
      'Check recent transactions',
      'Check again',
      "It didn't go through — try again",
    ]);
  });

  it('a 503 that says nothing was applied is no proof: after an edit the listing says "not yet"', async () => {
    server.use(
      http.post('*/api/transactions/deposit', () =>
        serviceUnavailable({ via: 'api', applied: false, instance: '/api/transactions/deposit' }),
      ),
    );
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await screen.findByText(COPY.nothingChanged);

    await userEvent.click(screen.getByRole('button', { name: '€200' }));

    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    expect(screen.queryByText(COPY.movementNone)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /didn't go through/ })).not.toBeInTheDocument();
  });

  it('after an edit, asks about the account the deposit was sent to, not the one now chosen', async () => {
    server.use(http.post('*/api/transactions/deposit', () => HttpResponse.error()));
    const seen = lookupsSeen();
    const savings = mockState.accounts[1];
    renderWithProviders(
      <Routes>
        <Route
          path="/"
          element={
            <DepositDialog
              isOpen
              onClose={() => {}}
              accounts={[
                seedAccount(),
                {
                  id: savings.id,
                  name: 'Rainy Day',
                  accountNumber: 'AB-****-****-34',
                  balance: 830,
                },
              ]}
            />
          }
        />
      </Routes>,
      { routerEntries: ['/'] },
    );
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    await screen.findByText(/Couldn't reach the server/);

    await userEvent.click(screen.getByRole('button', { name: /Rainy Day/ }));

    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    expect(seen.map((ask) => ask.accountId)).toEqual([MAIN.id]);
  });

  it('"Check again" asks about the same key and account again, and gives lost focus back to the first button', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/deposit', () => {
        sentAt = Date.now();
        return notKnown('/api/transactions/deposit');
      }),
    );
    // One answer per ask: the listing fails, answers, fails again at the bound, then answers.
    const failures = [true, false, true, false];
    whenListedByKey(() =>
      failures.shift()
        ? serviceUnavailable({ via: 'api', instance: '/api/transactions' })
        : undefined,
    );
    const seen = lookupsSeen();
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText(COPY.movementUncheckedNotYet)).toBeInTheDocument();
    expect(actions()).toEqual(['Check again', 'Check recent transactions']);

    // Before the bound it is the first button. It is disabled while it asks, which drops its
    // focus, and the answer gives the focus back to it.
    await settle();
    await user.click(screen.getByRole('button', { name: 'Check again' }));
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    expect(seen).toHaveLength(2);
    expect(seen[1]).toMatchObject({ key: seen[0].key, accountId: MAIN.id });
    await waitFor(() => expect(screen.getByRole('button', { name: 'Check again' })).toHaveFocus());

    // The view asks by itself at the bound, and the listing fails: "Check again" comes second.
    await advanceUntil(sentAt, 185_000);
    expect(await screen.findByText(COPY.movementUnchecked)).toBeInTheDocument();
    expect(seen).toHaveLength(3);
    expect(actions()).toEqual([
      'Check recent transactions',
      'Check again',
      "It didn't go through — try again",
    ]);

    // Pressed there, it lands on the first button as the answer leaves the view.
    await settle();
    await user.click(screen.getByRole('button', { name: 'Check again' }));
    expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();
    expect(seen).toHaveLength(4);
    expect(seen[3]).toMatchObject({ key: seen[0].key, accountId: MAIN.id });
    expect(actions()).toEqual(['Check recent transactions', "It didn't go through — try again"]);
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Check recent transactions' })).toHaveFocus(),
    );
  });

  it('the ask at the bound gives a visitor who waited on "Check again" the first button back', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/deposit', () => {
        sentAt = Date.now();
        return notKnown('/api/transactions/deposit');
      }),
    );
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    // A keyboard visitor's place: the view's first button, which the ask at the bound disables.
    screen.getByRole('button', { name: 'Check again' }).focus();

    await advanceUntil(sentAt, 185_000);
    expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Check again' })).not.toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Check recent transactions' })).toHaveFocus(),
    );
  });

  it('drops a press on the check view for 500 ms after it appears and after its verdict changes, and acts on one after', async () => {
    /*
      `useSettledPress`. Measured in Chromium on the running stack at 360×800: the second press of
      a double click on Deposit came once the view had grown to say "not yet", and landed on
      "Check recent transactions", which took the visitor to the history with the view unread.

      The fake clock also runs with real time (`installFakeClock`), so the time this test takes to
      see each change adds to it. The presses meant to be dropped therefore come at most 100 ms of
      the clock after the change they follow, which leaves 400 ms for that, and the one meant to
      act comes 500 ms after it, which real time can only make later.
    */
    server.use(
      http.post('*/api/transactions/deposit', () => notKnown('/api/transactions/deposit')),
    );
    let answer = () => {};
    const held = new Promise<void>((resolve) => {
      answer = resolve;
    });
    whenListedByKey(async () => {
      await held;
      return undefined;
    });
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText("We couldn't confirm your deposit")).toBeInTheDocument();
    const checkTransactions = () =>
      screen.getByRole('button', { name: 'Check recent transactions' });

    // The view has just appeared, and the listing is being asked.
    await user.click(checkTransactions());
    await advance(100);
    await user.click(checkTransactions());
    expect(screen.queryByText('HISTORY PAGE')).not.toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Deposit Money' })).toBeInTheDocument();
    // From here a press would act, were it not for the next change.
    await advance(400);

    // The listing answers, and the view says "not yet": the wait starts again.
    answer();
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    await user.click(checkTransactions());
    await advance(100);
    await user.click(checkTransactions());
    expect(screen.queryByText('HISTORY PAGE')).not.toBeInTheDocument();
    // Neither disabled nor renamed while it waits: a screen reader hears the same button.
    expect(checkTransactions()).toBeEnabled();
    expect(checkTransactions()).not.toHaveAttribute('aria-disabled');

    await advance(400);
    await user.click(checkTransactions());
    expect(await screen.findByText('HISTORY PAGE')).toBeInTheDocument();
  });

  it('a press the check view drops takes no focus, and one after the wait does', async () => {
    /*
      Measured in Chromium on the running stack at 360×800 and 375×640, with the wait but without
      this: the second press of a double click on Deposit was dropped on "Check recent
      transactions" and left focus there, where a later Enter would take the visitor to the
      history. Its `mousedown` is refused, as the backdrop refuses its own. The press comes as
      soon as "not yet" is seen, as in the test above.
    */
    server.use(
      http.post('*/api/transactions/deposit', () => notKnown('/api/transactions/deposit')),
    );
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    const checkTransactions = screen.getByRole('button', { name: 'Check recent transactions' });
    // The send left focus on `body` (`emulateFocusFixup`), and the view's title took it.
    const title = screen.getByText("We couldn't confirm your deposit");
    expect(title).toHaveFocus();

    await user.click(checkTransactions);
    expect(title).toHaveFocus();
    expect(screen.queryByText('HISTORY PAGE')).not.toBeInTheDocument();

    // After the wait, the press takes focus as a press does, and its click acts.
    await settle();
    await user.pointer({ keys: '[MouseLeft>]', target: checkTransactions });
    expect(checkTransactions).toHaveFocus();
    await user.pointer({ keys: '[/MouseLeft]', target: checkTransactions });
    expect(await screen.findByText('HISTORY PAGE')).toBeInTheDocument();
  });

  it('drops a press on Deposit for 500 ms after "try again" brings the form back, and sends on one after', async () => {
    /*
      The form keeps its values, so its Deposit is enabled as it comes back. Measured in Chromium
      on the running stack at 320×568 without this: the second press of a double click on "It
      didn't go through — try again" landed on "Deposit €1.00" and sent a second deposit under a
      new key, with the form never seen. The presses that must be dropped are made in the same
      task as the press that brought the form back: the fake clock follows real time only between
      tasks (`installFakeClock`), so it moves between them only as the test moves it.
    */
    let sentAt = 0;
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        sentAt = Date.now();
        return keys.length === 1 ? notKnown('/api/transactions/deposit') : undefined;
      }),
    );
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    await advanceUntil(sentAt, 185_000);
    expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();
    await settle();

    fireEvent.click(screen.getByRole('button', { name: "It didn't go through — try again" }));
    const deposit = screen.getByRole('button', { name: 'Deposit €100.00' });
    expect(deposit).toBeEnabled();
    fireEvent.click(deposit);
    act(() => void vi.advanceTimersByTime(400));
    fireEvent.click(deposit);
    // Neither disabled nor renamed while it waits.
    expect(deposit).toBeEnabled();
    expect(deposit).not.toHaveAttribute('aria-disabled');
    // A send that was not dropped would be on its way by now.
    await advance(0);
    expect(keys).toHaveLength(1);
    expect(screen.getByLabelText('Deposit amount')).toBeInTheDocument();

    act(() => void vi.advanceTimersByTime(100));
    fireEvent.click(deposit);
    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[1]).not.toBe(keys[0]);
  });

  it('drops a press on the amounts and the accounts for 500 ms after "try again" brings the form back, and acts on one after', async () => {
    /*
      The second press of a double click on "It didn't go through — try again" can land in the
      form's content as well as on its Deposit: on a quick amount or an account card, which would
      change what the next Deposit sends. The same wait covers that content (`useSettledPress`).
      Timed in one task, as the test above is.
    */
    let sentAt = 0;
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        sentAt = Date.now();
        return notKnown('/api/transactions/deposit');
      }),
    );
    const savings = mockState.accounts[1]; // seed: id a2, "Rainy Day", balance 830
    renderWithProviders(
      <DepositDialog
        isOpen
        onClose={() => {}}
        accounts={[
          seedAccount(),
          { id: savings.id, name: 'Rainy Day', accountNumber: 'AB-****-****-34', balance: 830 },
        ]}
      />,
    );
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    await advanceUntil(sentAt, 185_000);
    expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();
    await settle();

    fireEvent.click(screen.getByRole('button', { name: "It didn't go through — try again" }));
    const fifty = screen.getByRole('button', { name: '€50' });
    const rainyDay = screen.getByRole('button', { name: /Rainy Day/ });
    fireEvent.click(fifty);
    fireEvent.click(rainyDay);
    act(() => void vi.advanceTimersByTime(499));
    fireEvent.click(fifty);
    fireEvent.click(rainyDay);
    expect(screen.getByRole('button', { name: 'Deposit €100.00' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Main Account/ })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(rainyDay).toHaveAttribute('aria-pressed', 'false');
    // Refused its focus as well, as a press on the actions is.
    expect(fireEvent.mouseDown(fifty)).toBe(false);

    act(() => void vi.advanceTimersByTime(1));
    fireEvent.click(rainyDay);
    fireEvent.click(fifty);
    // The form validates what was chosen after the press, in later tasks.
    await advance(0);
    expect(rainyDay).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Deposit €50.00' })).toBeInTheDocument();
    expect(keys).toHaveLength(1);
  });

  it('drops a key pressed on the button an answer gave focus to, until the view has settled', async () => {
    /*
      The view lands focus on its title when it appears, which is no button, but an ask lands it
      on the view's first button once the answer is drawn, and that can be another button than
      the one the key was meant for: "Check again", focused, gives way at the bound to "Check
      recent transactions".
    */
    let sentAt = 0;
    server.use(
      http.post('*/api/transactions/deposit', () => {
        sentAt = Date.now();
        return notKnown('/api/transactions/deposit');
      }),
    );
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Deposit €100.00' }));
    expect(await screen.findByText(COPY.movementNotYet)).toBeInTheDocument();
    screen.getByRole('button', { name: 'Check again' }).focus();

    await advanceUntil(sentAt, 185_000);
    expect(await screen.findByText(COPY.movementNone)).toBeInTheDocument();
    const checkTransactions = screen.getByRole('button', { name: 'Check recent transactions' });
    await waitFor(() => expect(checkTransactions).toHaveFocus());

    await user.keyboard('{Enter}');
    await user.keyboard(' ');
    expect(screen.queryByText('HISTORY PAGE')).not.toBeInTheDocument();

    await settle();
    await user.keyboard('{Enter}');
    expect(await screen.findByText('HISTORY PAGE')).toBeInTheDocument();
  });
});

/*
  Choosing the account from the keyboard, and a refusal that names a field. The account cards
  are styled `div`s with the button's behaviour wired by hand, so the keys are the dialog's own
  to get right; and which account is chosen decides where the money lands.
*/
describe('deposit — choosing the account from the keyboard, and a refused field', () => {
  const SAVINGS = mockState.accounts[1]; // seed: id a2, "Rainy Day", balance 830

  function renderWithTwoAccounts() {
    return renderWithProviders(
      <Routes>
        <Route
          path="/"
          element={
            <DepositDialog
              isOpen
              onClose={() => {}}
              accounts={[
                seedAccount(),
                {
                  id: SAVINGS.id,
                  name: 'Rainy Day',
                  accountNumber: 'AB-****-****-34',
                  balance: 830,
                },
              ]}
            />
          }
        />
      </Routes>,
      { routerEntries: ['/'] },
    );
  }

  const card = (name: RegExp) => screen.getByRole('button', { name });
  const chosen = (name: RegExp) => card(name).getAttribute('aria-pressed');

  it('chooses an account with Enter and with Space, and with no other key', async () => {
    renderWithTwoAccounts();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    expect(chosen(/Main Account/)).toBe('true');
    expect(screen.getByText('New balance: €1,350.50')).toBeInTheDocument();

    card(/Rainy Day/).focus();
    await userEvent.keyboard('{Enter}');
    expect(chosen(/Rainy Day/)).toBe('true');
    expect(chosen(/Main Account/)).toBe('false');
    // The amount is kept, and the preview is the chosen account's.
    expect(screen.getByText('New balance: €930.00')).toBeInTheDocument();

    card(/Main Account/).focus();
    await userEvent.keyboard(' ');
    expect(chosen(/Main Account/)).toBe('true');
    expect(screen.getByText('New balance: €1,350.50')).toBeInTheDocument();

    card(/Rainy Day/).focus();
    await userEvent.keyboard('a');
    expect(chosen(/Main Account/)).toBe('true');
  });

  it('deposits into the account chosen from the keyboard, not into the first one', async () => {
    let sentBody: Record<string, unknown> | null = null;
    server.use(
      http.post('*/api/transactions/deposit', async ({ request }) => {
        sentBody = (await request.clone().json()) as Record<string, unknown>;
      }),
    );
    renderWithTwoAccounts();
    card(/Rainy Day/).focus();
    await userEvent.keyboard('{Enter}');
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    expect(await screen.findByText('Deposit Successful!')).toBeInTheDocument();
    expect(sentBody).toMatchObject({ accountId: SAVINGS.id, amount: 100 });
    // The mock moved the money of that account and of no other.
    expect(mockState.accounts.find((a) => a.id === SAVINGS.id)?.balance).toBe(930);
    expect(mockState.accounts.find((a) => a.id === MAIN.id)?.balance).toBe(1250.5);
  });

  /*
    A rule of the server's that the form does not have. The form's bounds are the contract's, so
    no amount typed here reaches it today. The sentence is the API's for an amount out of range,
    measured on 2026-09-03 and quoted in src/forms/moneySchemas.ts.
  */
  it("shows the server's own sentence when the server refuses a field", async () => {
    server.use(
      http.post('*/api/transactions/deposit', () =>
        problem({
          status: 400,
          errors: { Amount: ['Amount must be between 0.01 EUR and 100000.00 EUR'] },
        }),
      ),
    );
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    const words = await screen.findByText('Amount must be between 0.01 EUR and 100000.00 EUR');
    expect(words.closest('[role="alert"]')).not.toBeNull();
    expect(screen.queryByText('Deposit Successful!')).not.toBeInTheDocument();
  });

  it('says to check the amount when a refused field comes with no sentence', async () => {
    server.use(http.post('*/api/transactions/deposit', () => problem({ status: 400, errors: {} })));
    renderDeposit();
    await userEvent.click(screen.getByRole('button', { name: '€100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit €100.00' }));

    const words = await screen.findByText('Please check the amount and try again.');
    expect(words.closest('[role="alert"]')).not.toBeNull();
  });
});
