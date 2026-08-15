import { useEffect, useState } from 'react';
import { Route, Routes } from 'react-router-dom';
import { act, cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { mockState } from '../../mocks/state';
import { renderWithProviders } from '../../test/renderWithProviders';
import { WithdrawDialog } from './WithdrawDialog';

/**
 * T11 — an outflow form must not carry an amount past the source account's balance, and the PIN
 * must never be requested for an operation that is already known to fail.
 *
 * The bound itself is not new: `makeAmountSchema` has always refused an over-balance amount, and
 * Continue has always been gated on form validity. What was missing is everything AROUND that
 * bound, and all four gaps below were measured rather than guessed:
 *
 *  1. the bound is built from a CACHED balance that nothing refreshed before the PIN step;
 *  2. a 422 INSUFFICIENT_FUNDS invalidated nothing, so the stale number that caused the refusal
 *     survived it and the user could hit the same wall again;
 *  3. the PIN-step submit ran a validating `handleSubmit` while its disabled condition ignored
 *     validity — press it with a stale amount and NOTHING happened, silently;
 *  4. reaching the server at all is not free: measured against the running API, an over-balance
 *     withdrawal with a wrong PIN answers 401 INVALID_PIN — the funds check comes AFTER the PIN —
 *     so a doomed request spends one of three attempts, and three lock the PIN for fifteen minutes.
 *
 * Five of the six fail against `main`. The sixth — the fail-open branch — passes there too, and
 * says so where it is written: with no gate at all, Continue always advanced, so it characterises a
 * decision rather than catching a regression. It is kept because the decision is the kind that gets
 * quietly reversed by someone "hardening" the gate.
 */

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const MAIN = mockState.accounts[0];
const START_BALANCE = 1250.5;

/** What `/api/accounts` returns. Built from the seed, because the accounts schema is STRICT — a
 *  hand-rolled literal is missing `type`/`isPrimary`/`createdAt` and the query fails instead. */
function apiAccount(balance: number) {
  return { ...MAIN, balance };
}

/** What the dialog takes as a prop — the four fields its own `Account` type declares. */
function account(balance: number) {
  return { id: MAIN.id, name: MAIN.name, accountNumber: MAIN.accountNumber, balance };
}

/** Serve `/api/accounts` with a balance the test controls, and count the reads. */
function serveBalance(balances: number[]) {
  const reads: number[] = [];
  server.use(
    http.get('*/api/accounts', () => {
      const index = Math.min(reads.length, balances.length - 1);
      reads.push(balances[index]);
      return HttpResponse.json({ data: [apiAccount(balances[index])], message: null });
    }),
  );
  return reads;
}

/**
 * The dialog takes its accounts as a PROP, so this stands in for the parent page whose
 * `getAccounts` query re-resolved. `drop` is reached directly rather than through a button: the
 * dialog is modal, and a control outside it is exactly what a focus trap is supposed to make
 * unclickable.
 */
let dropBalanceTo: (next: number) => void = () => {};

function Harness() {
  const [balance, setBalance] = useState(START_BALANCE);
  // Published from an EFFECT, not during render: assigning to module scope while rendering is a
  // side effect, and `react-hooks/globals` is right to refuse it.
  useEffect(() => {
    dropBalanceTo = setBalance;
  }, []);
  return <WithdrawDialog isOpen onClose={() => {}} accounts={[account(balance)]} />;
}

function renderDialog() {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={<Harness />} />
      <Route path="/pin-setup" element={<div>PIN SETUP PAGE</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

async function typeAmount(value: string) {
  await userEvent.click(screen.getByLabelText('Withdraw amount'));
  await userEvent.paste(value);
}

const continueButton = () => screen.getByRole('button', { name: /^Continue/ });

describe('the balance guard', () => {
  it('does not reach the PIN step when the server says the balance has moved', async () => {
    // First read is the render; the gate's read at Continue finds the money gone.
    serveBalance([START_BALANCE, 100]);
    renderDialog();

    await typeAmount('400');
    expect(continueButton()).toBeEnabled();
    await userEvent.click(continueButton());

    // The whole point: the ceremony is never rendered.
    await waitFor(() =>
      expect(screen.getByText(/Insufficient balance for this operation/)).toBeInTheDocument(),
    );
    expect(screen.queryByText('Verify Withdrawal')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Digit 1 of 6')).not.toBeInTheDocument();
    // And it names the figure the user has not seen yet, not the one they were shown.
    expect(screen.getByText(/€100\.00 available/)).toBeInTheDocument();
  });

  it('lets the operation through when the balance cannot be re-read', async () => {
    /*
      FAIL OPEN, deliberately. The gate is a courtesy that saves a doomed round trip; the server's
      own funds check is the control. A gate that blocked on its own network trouble would turn a
      nicety into an outage, so this branch is asserted rather than left to be discovered.
    */
    let reads = 0;
    server.use(
      http.get('*/api/accounts', () => {
        reads += 1;
        return reads === 1
          ? HttpResponse.json({ data: [apiAccount(START_BALANCE)], message: null })
          : HttpResponse.json({ message: 'boom' }, { status: 500 });
      }),
    );
    renderDialog();

    await typeAmount('400');
    await userEvent.click(continueButton());

    expect(await screen.findByText('Verify Withdrawal')).toBeInTheDocument();
  });

  it('turns the figure red and points the input at the reason', async () => {
    serveBalance([START_BALANCE]);
    renderDialog();

    await typeAmount('9999');

    const input = screen.getByLabelText('Withdraw amount');
    await waitFor(() => expect(input).toHaveAttribute('aria-invalid', 'true'));
    // Resolved through the id rather than matched on the attribute: an aria-describedby that
    // points at nothing would pass the attribute check and tell a screen-reader user nothing.
    const describedBy = input.getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)).toHaveTextContent(
      'Exceeds available balance of €1,250.50.',
    );
    expect(continueButton()).toBeDisabled();

    /*
      THE COLOUR ITSELF — the thing that was asked for first ("il numero diventa rosso") and the
      one thing nothing verified. `aria-invalid` and the hint text can both be right while the
      figure still renders black, because Griffel's atomic classes share a specificity and are
      resolved by stylesheet insertion order: composing them by hand worked only as long as
      `amountInvalid` happened to be declared after `amountInput`. `mergeClasses` settles that,
      and this is what proves it stayed settled.

      jsdom does not resolve custom properties, so the token is compared as the literal it is —
      which is the honest comparison here: the assertion is that the ERROR token won, not that a
      particular RGB was painted.
    */
    const errorToken = 'var(--ab-colors-semantic-error-main)';
    expect(getComputedStyle(input).color).toBe(errorToken);
    // The € prefix travels with the numerals; a red figure beside a black € reads as a glitch.
    expect(getComputedStyle(input.previousElementSibling as HTMLElement).color).toBe(errorToken);
  });

  it('offers the maximum instead of making the user retype the balance', async () => {
    serveBalance([START_BALANCE]);
    renderDialog();

    const useMax = screen.getByRole('button', { name: 'Use maximum, €1,250.50' });

    /*
      The button's own type scale, and why it is asserted at all: the style block first wrote
      `font: 'inherit'`. Griffel does NOT expand `font` — measured, it emits `.f15dniw2 { font:
      inherit; }` verbatim — so it competed with the `fontSize`/`fontWeight` longhands as an
      ordinary same-specificity atom and won on insertion order. The measured result was a button
      rendering at the INHERITED base size instead of 13px. `fontFamily` carries the intent without
      resetting anything.
    */
    expect(getComputedStyle(useMax).fontSize).toBe('13px');

    await userEvent.click(useMax);

    expect(screen.getByLabelText('Withdraw amount')).toHaveValue('1250.5');
    await waitFor(() => expect(continueButton()).toBeEnabled());
  });

  it('refreshes the balance a refusal was measured against', async () => {
    /*
      Every money mutation invalidated NOTHING on error. Right for almost all of them — a rejected
      request changed no state — but exactly backwards for this one, where the error IS the server
      saying the cached balance is wrong. Without the fix the count stays at 1 and the form keeps
      the number that caused the rejection.
    */
    const reads = serveBalance([START_BALANCE, START_BALANCE, 100]);
    server.use(
      http.post('*/api/transactions/withdraw', () =>
        problem({
          status: 422,
          errorCode: 'INSUFFICIENT_FUNDS',
          detail: 'Insufficient funds.',
          extensions: { available: 100, requested: 400 },
        }),
      ),
    );
    renderDialog();

    await typeAmount('400');
    await userEvent.click(continueButton());
    await screen.findByText('Verify Withdrawal');
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123456');
    await userEvent.click(screen.getByRole('button', { name: /^Withdraw/ }));

    await waitFor(() => expect(reads.length).toBeGreaterThanOrEqual(3));
  });

  it('explains itself instead of doing nothing when the amount no longer fits', async () => {
    /*
      The dead-button case. `handleSubmit` refuses to call the success branch when the resolver
      fails, and the button's disabled condition never mentioned validity — so a balance that moved
      while the PIN step was open made "Withdraw €400.00" a control that responded to nothing.
    */
    const posts: string[] = [];
    serveBalance([START_BALANCE, START_BALANCE]);
    server.use(
      http.post('*/api/transactions/withdraw', async ({ request }) => {
        posts.push(await request.text());
        return HttpResponse.json({ data: null, message: 'should never be reached' });
      }),
    );
    renderDialog();

    await typeAmount('400');
    await userEvent.click(continueButton());
    await screen.findByText('Verify Withdrawal');

    // The parent's query re-resolved with less money — the dialog is holding a stale prop.
    await act(async () => {
      dropBalanceTo(100);
    });

    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123456');
    await userEvent.click(screen.getByRole('button', { name: /^Withdraw/ }));

    /*
      Back on the amount step, saying why — on BOTH surfaces. The banner explains the refused
      action; the field hint marks the value. The count is asserted EXACTLY rather than as "at
      least one": the whole point of the change is that neither surface stays silent, and a
      `toBeGreaterThanOrEqual(1)` passes in precisely the half-fixed state this test exists to
      catch. Measured: 2.
    */
    expect(await screen.findByLabelText('Withdraw amount')).toBeInTheDocument();
    const stated = await screen.findAllByText('Exceeds available balance of €100.00.');
    expect(stated).toHaveLength(2);
    // And the request never left.
    expect(posts).toEqual([]);
  });
});
