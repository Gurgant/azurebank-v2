import { Route, Routes } from 'react-router-dom';
import { act, cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { makeTestStore, renderWithProviders } from '../../test/renderWithProviders';
import { expectNoNestedLiveRegions } from '../../test/liveRegions';
import { WithdrawDialog } from './WithdrawDialog';

/*
  UNMOUNT FIRST, then restore the clock — the order is load-bearing and cost a debugging round.

  Vitest runs afterEach hooks in reverse registration order, so this file's hook runs BEFORE the
  `cleanup()` in `test/setup.ts`. Restoring real timers here first left the countdown still mounted
  with a live FAKE interval; its unmount then called `clearInterval` with a fake id against the real
  implementation, so the interval was never cancelled and kept firing setState into the NEXT test —
  which the console.error gate caught as act(...) violations, and only when the whole file ran.
  `cleanup()` is idempotent, so calling it here and again in setup.ts is free.
*/
afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

/**
 * PR-10 — withdraw is the deposit protocol PLUS the PIN-in-body gate (D1). Pins: the two-step
 * amount→PIN flow, a wrong PIN (401 INVALID_PIN) staying IN the dialog (never a logout), the
 * PIN_LOCKED countdown, the hasPin gate routing to /pin-setup, the mid-flight dismiss guard,
 * plus the shared idempotency seams (IN_FLIGHT keeps the key, a PIN edit rotates it,
 * RESULT_UNKNOWN verify-first, replay note).
 */

const MAIN = mockState.accounts[0]; // seed: id a1, balance 1250.5

function seedAccount() {
  return { id: MAIN.id, name: 'Main Account', accountNumber: 'AB-••••-••••-90', balance: 1250.5 };
}

function renderWithdraw(store = makeTestStore()) {
  return renderWithProviders(
    <Routes>
      <Route
        path="/"
        element={<WithdrawDialog isOpen onClose={() => {}} accounts={[seedAccount()]} />}
      />
      <Route path="/transactions/:id" element={<div>TX DETAIL PAGE</div>} />
      <Route path="/history" element={<div>HISTORY PAGE</div>} />
      <Route path="/pin-setup" element={<div>PIN SETUP PAGE</div>} />
    </Routes>,
    { store, routerEntries: ['/'] },
  );
}

/** Seed the store's auth.user (as a getMe-fulfilled payload would) — for the hasPin gate. */
function storeWithUser(hasPin: boolean) {
  const store = makeTestStore();
  store.dispatch({
    type: 'api/executeQuery/fulfilled',
    meta: { arg: { endpointName: 'getMe' } },
    payload: {
      user: {
        id: MAIN.id,
        azureTag: 'demo_user',
        email: 'demo@azurebank.dev',
        firstName: 'Demo',
        lastName: 'User',
        hasPin,
      },
    },
  });
  return store;
}

async function enterPin(pin: string) {
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste(pin);
}

function withdrawSuccessBody(newBalance: number, replayed = false) {
  return HttpResponse.json(
    {
      data: {
        transaction: {
          id: '019f7b3f-0000-7000-8000-000000000e99',
          transactionNumber: 'TXN-20260722-000999',
          type: 'Withdrawal',
          amount: 100,
          balanceAfter: newBalance,
          description: null,
          recipientAzureTag: null,
          senderAzureTag: null,
          status: 'Completed',
          createdAt: '2026-07-22T11:00:00.0000000Z',
        },
        newBalance,
      },
      message: 'Withdrawal successful',
    },
    { status: 201, headers: replayed ? { 'Idempotency-Replayed': 'true' } : {} },
  );
}

async function goToPinStep(amountLabel = '€100') {
  await userEvent.click(screen.getByRole('button', { name: amountLabel }));
  await userEvent.click(screen.getByRole('button', { name: /^Continue/ }));
  expect(await screen.findByText('Verify Withdrawal')).toBeInTheDocument();
}

describe('withdraw (the idempotent mutation; its PIN moved to the mint, ADR-0056)', () => {
  const MINTED = '019f7b3f-0000-7000-8000-0000000a0001';
  const MINTED_SECOND = '019f7b3f-0000-7000-8000-0000000a0002';

  /*
    THE MINT, restored against the mint — which is what the interim block that stood here asked
    PR-C to do. Between the two PRs this dialog sent a withdrawal with no authorisation and was
    answered 401, and the test here pinned THAT, because it was what a user actually met.

    Both halves are asserted from the REQUESTS, not from the rendered outcome: a dialog that minted
    and then sent the authorisation in the BODY would show the same success screen, and the server
    fingerprints the body — so where the reference travels is the assertion, not a detail.
  */
  it('mints with the PIN, then sends the withdrawal with that reference in the header', async () => {
    let mintBody: Record<string, unknown> | null = null;
    let sentHeader: string | null = null;
    let sentBody: Record<string, unknown> | null = null;
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', async ({ request }) => {
        mintBody = (await request.json()) as Record<string, unknown>;
        return HttpResponse.json(
          {
            data: { authorizationId: MINTED, expiresAt: '2026-07-22T11:02:00.0000000Z' },
            message: 'Withdrawal authorised',
          },
          { status: 201 },
        );
      }),
      http.post('*/api/transactions/withdraw', async ({ request }) => {
        sentHeader = request.headers.get('Step-Up-Authorization');
        sentBody = (await request.json()) as Record<string, unknown>;
        return withdrawSuccessBody(900);
      }),
    );

    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText('Withdrawal Successful!')).toBeInTheDocument();
    // MAIN.id, not expect.any(String): this dialog has an account SELECTOR, so "some account"
    // is the one shape that passes while the money leaves the wrong one. The mint binds the
    // authorisation to an account and an amount, and both are named here for that reason.
    expect(mintBody).toEqual({ accountId: MAIN.id, amount: 100, pin: '123456' });
    expect(sentHeader).toBe(MINTED);
    // And the PIN is NOT in the withdrawal's body: it left with ADR-0056, and a body that still
    // carried it would be a different fingerprint from the one the retry path replays.
    expect(sentBody).not.toHaveProperty('pin');
  });

  it('a wrong PIN is refused by the MINT, stays in the dialog, and sends no withdrawal', async () => {
    // AUTHENTICATED store: only then does sessionMiddleware's 401 logout branch run, so the
    // INVALID_PIN exemption is genuinely load-bearing (the negative control below still logs out).
    let withdrawals = 0;
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () =>
        problem({ status: 401, errorCode: 'INVALID_PIN', detail: 'Invalid PIN.' }),
      ),
      http.post('*/api/transactions/withdraw', () => {
        withdrawals += 1;
        return withdrawSuccessBody(900);
      }),
    );
    const store = storeWithUser(true);
    renderWithdraw(store);
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText('Invalid PIN. Please try again.')).toBeInTheDocument();
    expect(screen.getByText('Verify Withdrawal')).toBeInTheDocument();
    expect(store.getState().auth.status).toBe('authenticated');
    // THE WITHDRAWAL NEVER LEFT. Asserting only the message would pass on a dialog that minted,
    // failed, and sent anyway -- which is the money-moving version of this bug.
    expect(withdrawals).toBe(0);
  });

  it('re-presents the SAME authorisation on a retained key, it does not mint twice', async () => {
    /*
      A retry of a RETAINED key is the same intent: the first authorisation may already have been
      consumed by the attempt whose answer never arrived, and if it was not, it is still the
      authorisation for these exact fields. Minting again leaves an orphan row and tells the server
      a different story than the first attempt did.
    */
    let mints = 0;
    const headers: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () => {
        mints += 1;
        /*
          TWO REAL UUIDs, because the STRICT unwrap validates `authorizationId` and refused a
          fabricated `${MINTED}${mints}` outright. Distinct on purpose: if the dialog minted a
          second time, the second request would carry the SECOND id and the equality below would
          name it.
        */
        return HttpResponse.json(
          {
            data: {
              authorizationId: mints === 1 ? MINTED : MINTED_SECOND,
              expiresAt: '2026-07-22T11:02:00.0000000Z',
            },
            message: 'Withdrawal authorised',
          },
          { status: 201 },
        );
      }),
      http.post('*/api/transactions/withdraw', ({ request }) => {
        headers.push(request.headers.get('Step-Up-Authorization'));
        return headers.length === 1
          ? problem({
              status: 409,
              errorCode: 'IDEMPOTENCY_IN_FLIGHT',
              detail: 'A request with this key is in flight.',
            })
          : withdrawSuccessBody(900);
      }),
    );

    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText(/Still processing/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText('Withdrawal Successful!')).toBeInTheDocument();

    expect(mints).toBe(1);
    expect(headers).toEqual([MINTED, MINTED]);
  });

  it('negative control: a NON-INVALID_PIN 401 while authenticated DOES expire the session', async () => {
    // Proves the exemption above is what keeps the session — a plain 401 must still log out.
    server.use(
      http.post('*/api/transactions/withdraw', () =>
        problem({ status: 401, errorCode: 'TOKEN_EXPIRED', detail: 'Session expired.' }),
      ),
    );
    const store = storeWithUser(true);
    renderWithdraw(store);
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    await waitFor(() => expect(store.getState().auth.status).toBe('expired'));
  });

  /*
    AIMED AT THE MINT, and that is the whole correction (ADR-0056). These two stubbed
    `/api/transactions/withdraw` and kept passing after the PIN moved -- the mint answered 201, the
    stub then answered 429, and the dialog rendered the countdown. Green, and pinning a response
    that endpoint can no longer produce: the withdrawal checks no PIN, so it cannot lock one.
  */
  it('surfaces the lock countdown and disables Withdraw on a 429 PIN_LOCKED', async () => {
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () =>
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many attempts.',
          extensions: { retryAfterSeconds: 900, lockedUntil: '2026-07-22T11:15:00.0000000Z' },
          headers: { 'Retry-After': '900' },
        }),
      ),
    );
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText(/Too many incorrect PIN attempts/)).toBeInTheDocument();
    // 900s off the response, rendered live by the shared countdown rather than frozen prose.
    expect(screen.getByRole('timer')).toHaveTextContent('Try again in 15:00');
    expect(screen.getByRole('button', { name: /^Withdraw/ })).toBeDisabled();
    /*
      The countdown must not sit INSIDE the alert. `role="alert"` implies `aria-atomic="true"`, so a
      timer nested in it would re-announce the whole banner every second — assertively. A review
      caught this on the first version of the change; the oracle now treats any `aria-live` element
      as a region, not just role=alert/status, because `role="timer"` carries the attribute
      explicitly rather than implicitly.
    */
    expectNoNestedLiveRegions();
  });

  it('the lock EXPIRES — Withdraw is usable again once the window closes', async () => {
    /*
      The property nothing asserted until now, and the reason this dialog shipped broken: it stored
      the number of seconds and never touched it again, so the countdown froze and Withdraw stayed
      disabled for as long as the dialog was open. Closing and reopening escaped it — at the cost of
      the whole form — which is why the defect read as cosmetic and survived review.

      Fake timers are installed BEFORE the render, not after: `RetryCountdown` creates its interval
      on mount, and an interval scheduled against the real clock is invisible to
      `advanceTimersByTime`. `shouldAdvanceTime` keeps userEvent's own zero-delay waits resolving.
    */
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () =>
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many attempts.',
          extensions: { retryAfterSeconds: 5 },
        }),
      ),
    );
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');

    // Installed here, not at the top: before this click no countdown exists, so nothing can tick.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText(/Too many incorrect PIN attempts/)).toBeInTheDocument();

    await act(async () => {
      vi.advanceTimersByTime(6000);
    });

    await waitFor(() =>
      expect(screen.queryByText(/Too many incorrect PIN attempts/)).not.toBeInTheDocument(),
    );

    /*
      The PIN BOXES are what the lock disabled, so they are what must come back. Withdraw stays
      disabled a moment longer and that is correct, not a residue: the lock branch clears the PIN
      (WithdrawDialog.tsx), so the button is waiting for six digits rather than for the lock. The
      first version of this test asserted the button directly and failed for exactly that reason —
      worth recording, because "the control is still disabled" reads like the bug it is not.

      Re-entering the PIN is the real proof: the user can finish the withdrawal they were locked out
      of, which is the property, rather than merely seeing a message disappear.
    */
    expect(screen.getByLabelText('Digit 1 of 6')).toBeEnabled();
    await enterPin('123456');
    expect(screen.getByRole('button', { name: /^Withdraw/ })).toBeEnabled();
  });

  it('a mid-flight INSUFFICIENT_FUNDS returns to the amount step with a message', async () => {
    server.use(
      http.post('*/api/transactions/withdraw', () =>
        problem({
          status: 422,
          errorCode: 'INSUFFICIENT_FUNDS',
          detail: 'Insufficient funds.',
          extensions: { available: 50, requested: 100 },
        }),
      ),
    );
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText(/your balance changed/)).toBeInTheDocument();
    // Back on the amount step — the account cards are visible again.
    expect(screen.getByText('Select Account')).toBeInTheDocument();
  });

  it('KEEPS the key on IN_FLIGHT and retries with the SAME key', async () => {
    const keys: (string | null)[] = [];
    let calls = 0;
    server.use(
      http.post('*/api/transactions/withdraw', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        calls += 1;
        return calls === 1
          ? problem({
              status: 409,
              errorCode: 'IDEMPOTENCY_IN_FLIGHT',
              detail: 'Still processing.',
            })
          : withdrawSuccessBody(1150.5);
      }),
    );
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText(/Still processing/)).toBeInTheDocument();

    // Retry WITHOUT editing the body — same key must be reused.
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText('Withdrawal Successful!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  /*
    ~~ROTATES the key when the PIN is edited between attempts (the PIN is part of the body).~~
    INVERTED 2026-09-22, ADR-0056: the PIN LEFT the body, so editing it cannot change the
    fingerprint and must NOT rotate the key.

    This is not a formality. `keyRetained` is exactly the state where the key is the only way
    forward -- an attempt whose outcome is unknown -- and the PIN input is live again there,
    because it is disabled only while submitting. Under the old rule a user who retyped a digit
    while holding a retained key destroyed their own re-send, and the withdrawal whose answer never
    arrived became unresettable from the dialog.
  */
  it('KEEPS the key when the PIN is edited between attempts (the PIN is no longer in the body)', async () => {
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/withdraw', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        return keys.length === 1
          ? problem({
              status: 409,
              errorCode: 'IDEMPOTENCY_IN_FLIGHT',
              detail: 'Still processing.',
            })
          : withdrawSuccessBody(1150.5);
      }),
    );
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    await screen.findByText(/Still processing/);

    // Edit the PIN (backspace + retype the last digit). It is not body-affecting any more.
    await userEvent.type(screen.getByLabelText('Digit 6 of 6'), '{backspace}');
    await userEvent.click(screen.getByLabelText('Digit 6 of 6'));
    await userEvent.paste('6');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText('Withdrawal Successful!')).toBeInTheDocument();
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  /*
    ~~THE SIBLING THAT STILL ROTATES, kept as the control.~~ Rewritten in review on #199, and the
    finding is a money one: editing the amount on a RETAINED key minted a new key for a NEW intent
    while the first attempt's outcome was still unknown, so if the balance covered both, both could
    debit. That is the double-spend the whole protocol exists to prevent, and a sibling test had it
    written down as the escape hatch.

    It is answered rather than blocked. Blocking the edit would be a hard trap, with Close already
    disabled on `keyLive`; latching verify-first tells the user what is actually true -- the request
    may or may not have gone through -- and drops the key, so dismissal works again.
  */
  /*
    A REFUSED AUTHORISATION MUST NOT BE RE-PRESENTED, and this test exists because the fix for it
    had none: a mutant that kept the dead reference left all twenty tests green.

    `shouldKeepKey` retains the idempotency key on AUTHORIZATION_INVALID and AUTHORIZATION_EXPIRED,
    with the measurement written beside the rule -- `key K + FRESH authorisation -> 201`. So the
    sanctioned recovery is the SAME key with a NEW authorisation, and holding the refused one made
    it unreachable: the retry re-presented the dead id, the server refused again, and the retained
    key kept Close disabled.
  */
  it('mints AFRESH on a retry after AUTHORIZATION_EXPIRED, on the same key', async () => {
    let mints = 0;
    const keys: (string | null)[] = [];
    const headers: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () => {
        mints += 1;
        return HttpResponse.json(
          {
            data: {
              authorizationId: mints === 1 ? MINTED : MINTED_SECOND,
              expiresAt: '2026-07-22T11:02:00.0000000Z',
            },
            message: 'Withdrawal authorised',
          },
          { status: 201 },
        );
      }),
      http.post('*/api/transactions/withdraw', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        headers.push(request.headers.get('Step-Up-Authorization'));
        return keys.length === 1
          ? problem({
              status: 401,
              errorCode: 'AUTHORIZATION_EXPIRED',
              detail: 'That authorisation has expired.',
            })
          : withdrawSuccessBody(1150.5);
      }),
    );

    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText(/no longer valid|expired/i)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText('Withdrawal Successful!')).toBeInTheDocument();

    // A SECOND mint, and the SECOND reference presented -- not the refused one again.
    expect(mints).toBe(2);
    expect(headers).toEqual([MINTED, MINTED_SECOND]);
    // And the SAME key throughout: the body never changed, so the intent never did.
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  /*
    A WRONG PIN ON A RETAINED INTENT MUST NOT RELEASE THE KEY, which is what the comment on the
    INVALID_PIN branch now says and what nothing asserted. Raised in review on #199: that comment
    used to claim the hook had dropped the key, true while the PIN was in the BODY and false since
    the mint answers this refusal without passing through `submit`. A reader matching the code to
    the old sentence would call `resetIntent()` there -- a NEW intent over an unresolved one, which
    is the double-spend closed one round earlier, returning through a stale comment.

    The whole chain in one test: an expired authorisation retains the key, a wrong PIN on the
    re-mint leaves it retained, and the corrected PIN mints afresh and presents it on that SAME key.
  */
  it('a wrong PIN while the key is RETAINED keeps it, and the corrected PIN mints on it', async () => {
    let mints = 0;
    const keys: (string | null)[] = [];
    const headers: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/withdraw/authorizations', () => {
        mints += 1;
        if (mints === 2) {
          // The wrong PIN, answered by the MINT and never by the withdrawal.
          return problem({ status: 401, errorCode: 'INVALID_PIN', detail: 'Invalid PIN.' });
        }
        return HttpResponse.json(
          {
            data: {
              authorizationId: mints === 1 ? MINTED : MINTED_SECOND,
              expiresAt: '2026-07-22T11:02:00.0000000Z',
            },
            message: 'Withdrawal authorised',
          },
          { status: 201 },
        );
      }),
      http.post('*/api/transactions/withdraw', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        headers.push(request.headers.get('Step-Up-Authorization'));
        return keys.length === 1
          ? problem({
              status: 401,
              errorCode: 'AUTHORIZATION_EXPIRED',
              detail: 'That authorisation has expired.',
            })
          : withdrawSuccessBody(1150.5);
      }),
    );

    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText(/no longer valid|expired/i)).toBeInTheDocument();
    // The key is RETAINED from here on: the dialog may not be abandoned.
    expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled();

    // Second attempt: the mint refuses the PIN. The key must survive that.
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText('Invalid PIN. Please try again.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled();
    expect(keys).toHaveLength(1);

    // Third: the corrected PIN mints afresh and is presented on the SAME key.
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    expect(await screen.findByText('Withdrawal Successful!')).toBeInTheDocument();

    expect(mints).toBe(3);
    expect(headers).toEqual([MINTED, MINTED_SECOND]);
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  it('an amount edit on a RETAINED intent asks for verification, and sends nothing', async () => {
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/api/transactions/withdraw', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'));
        return problem({
          status: 409,
          errorCode: 'IDEMPOTENCY_IN_FLIGHT',
          detail: 'Still processing.',
        });
      }),
    );
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));
    await screen.findByText(/Still processing/);

    await userEvent.click(screen.getByRole('button', { name: /^Back/ }));
    await userEvent.click(screen.getByRole('button', { name: '€50' }));

    // The protocol's own answer to "the outcome is unknown", not a silent re-key.
    expect(await screen.findByText(/couldn.t confirm your withdrawal/i)).toBeInTheDocument();
    expect(screen.getByText(/may or may not have gone through/i)).toBeInTheDocument();
    // NOT a trap: latching drops the key, so the dialog can be left.
    expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();
    // And the second intent was never sent. One request, one key.
    expect(keys).toHaveLength(1);
  });

  it('RESULT_UNKNOWN latches a verify-first flow, not a blind retry (§2.3)', async () => {
    server.use(
      http.post('*/api/transactions/withdraw', () =>
        problem({
          status: 409,
          errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
          detail: 'Could not confirm.',
        }),
      ),
    );
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText("We couldn't confirm your withdrawal")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Withdraw/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Check recent transactions' }));
    expect(await screen.findByText('HISTORY PAGE')).toBeInTheDocument();
  });

  it('surfaces the polite replay note on a replayed 2xx (D4)', async () => {
    server.use(http.post('*/api/transactions/withdraw', () => withdrawSuccessBody(1150.5, true)));
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText('Withdrawal Successful!')).toBeInTheDocument();
    expect(screen.getByText(/already processed — showing the existing result/)).toBeInTheDocument();
  });

  it('shows a generic message for a KEY_REUSE client-protocol bug, never the raw code (D17)', async () => {
    server.use(
      http.post('*/api/transactions/withdraw', () =>
        problem({
          status: 422,
          errorCode: 'IDEMPOTENCY_KEY_REUSE',
          detail: 'This idempotency key was already used with a different payload.',
        }),
      ),
    );
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText('Something went wrong. Please try again.')).toBeInTheDocument();
    expect(screen.queryByText(/IDEMPOTENCY_KEY_REUSE/)).not.toBeInTheDocument();
  });

  it('cannot be dismissed mid-flight — Close is disabled so the key survives', async () => {
    server.use(http.post('*/api/transactions/withdraw', () => new Promise<Response>(() => {})));
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled());
    expect(screen.getByText('Withdraw Money')).toBeInTheDocument(); // still open
  });

  it('after a NETWORK failure the key is retained, so Close stays disabled — until the body is edited', async () => {
    server.use(http.post('*/api/transactions/withdraw', () => HttpResponse.error()));
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    expect(await screen.findByText(/Couldn't reach the server/)).toBeInTheDocument();
    // The hook KEEPS the key on a transport failure — dismissal must stay blocked so the
    // intent can't be abandoned then re-minted (double-spend). isSubmitting is already false.
    expect(screen.getByRole('button', { name: 'Close' })).toBeDisabled();

    // Escape hatch, not a hard trap -- and INFORMED since #199. Editing the body no longer
    // rotates the key silently: it latches verify-first, which drops the key (so Close re-enables)
    // and says the request may or may not have gone through. The escape survives; what went is the
    // silent part, which was a second intent over an unresolved one.
    await userEvent.click(screen.getByRole('button', { name: 'Back' }));
    await userEvent.click(screen.getByRole('button', { name: '€200' }));
    expect(screen.getByRole('button', { name: 'Close' })).toBeEnabled();
  });

  it('gates a PIN-less user to /pin-setup instead of the withdraw form (hasPin=false)', async () => {
    renderWithdraw(storeWithUser(false));

    expect(screen.getByText('Set up a PIN to withdraw')).toBeInTheDocument();
    expect(screen.queryByText('Select Account')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Set up PIN' }));
    expect(await screen.findByText('PIN SETUP PAGE')).toBeInTheDocument();
  });

  it('disables Continue and hints when the amount exceeds the balance', async () => {
    renderWithdraw();
    await userEvent.type(screen.getByLabelText('Withdraw amount'), '2000');

    expect(screen.getByText('Exceeds available balance of €1,250.50.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /^Continue/ })).toBeDisabled();
  });

  it('gates the Withdraw CTA until a full 6-digit PIN is entered', async () => {
    renderWithdraw();
    await goToPinStep();
    expect(screen.getByRole('button', { name: /^Withdraw/ })).toBeDisabled();
    await enterPin('123456');
    expect(screen.getByRole('button', { name: 'Withdraw €100.00' })).toBeEnabled();
  });

  /*
    This dialog is the ONLY money surface that had hand-rolled `<div role="alert">` / `role="status"`
    wrappers, written before `MessageBar` carried a role of its own. Giving the banners the role —
    the pattern `LoginPage` set — therefore produced a live region inside a live region here and
    nowhere else. These two pin the announcement to ONE element, and to the right one.
  */
  it('announces a failure exactly once — the banner is the live region, not its wrapper', async () => {
    server.use(http.post('*/api/transactions/withdraw', () => HttpResponse.error()));
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    const banner = await screen.findByText(/Couldn't reach the server/);
    expectNoNestedLiveRegions();

    // Exactly one, and it is the MessageBar. The count also proves Fluent does not swallow the
    // `role` prop — its root ships as `role="group"`, so if the prop were dropped this would be 0.
    const alerts = screen.getAllByRole('alert');
    expect(alerts).toHaveLength(1);
    expect(alerts[0]).toContainElement(banner);

    // The wrapper keeps its id, which is the actual reason it exists: `PinInput` points the PIN
    // group's `aria-describedby` at it, so someone returning to the field still hears why the
    // attempt failed. Dropping the role must not orphan that reference — this is the assertion that
    // would catch a "cleanup" deleting the whole div. (The reference sits on the `role="group"`
    // wrapping the six boxes, not on each box: the group is the labelled control, the boxes are
    // its parts.)
    const describedBy = screen
      .getByRole('group', { name: 'Enter your PIN' })
      .getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy as string)).toContainElement(banner);
  });

  it('announces the in-flight note exactly once, and politely', async () => {
    server.use(
      http.post('*/api/transactions/withdraw', () =>
        problem({ status: 409, errorCode: 'IDEMPOTENCY_IN_FLIGHT', detail: 'Still processing.' }),
      ),
    );
    renderWithdraw();
    await goToPinStep();
    await enterPin('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Withdraw €100.00' }));

    const note = await screen.findByText(/Still processing/);
    expectNoNestedLiveRegions();

    // `status`, not `alert`: it is a progress note about a retry that is SAFE, and interrupting
    // someone mid-sentence to say "still working" is the assertive role being spent on nothing.
    const statuses = screen.getAllByRole('status');
    expect(statuses).toHaveLength(1);
    expect(statuses[0]).toContainElement(note);
  });
});
