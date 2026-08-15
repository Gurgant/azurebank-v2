import { act, cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { http } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { seedMockSession } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { TransferPage } from './TransferPage';
import { InternalTransferPage } from './InternalTransferPage';

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
 * The PIN step's RECOVERY behaviour — the half of ADR-0041 that had no test at all.
 *
 * Every branch here is one the page claims to handle, and none of them was exercised: the PR added
 * the code and updated the existing flows, but never asserted what a REFUSED PIN does. That gap is
 * exactly wide enough for a stale-closure bug to walk through, which is what happened.
 */

function renderTransfer() {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={<TransferPage />} />
      <Route path="/pin-setup" element={<div>PIN SETUP</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

async function reachPinStep() {
  await screen.findByText('Main Account');
  await userEvent.type(screen.getByLabelText('Recipient handle'), 'friend');
  await userEvent.click(screen.getByRole('button', { name: 'Verify' }));
  await screen.findByText('A. Friend');
  await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
  await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste('123456');
}

beforeEach(() => {
  seedMockSession();
});

describe('the PIN step recovers from what the server refuses', () => {
  it('INVALID_PIN clears the boxes so the retry is usable', async () => {
    server.use(
      http.post('*/api/transfers', () =>
        problem({ status: 401, errorCode: 'INVALID_PIN', detail: 'Invalid PIN.' }),
      ),
    );
    renderTransfer();
    await reachPinStep();

    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

    /*
      The boxes must be EMPTY afterwards. Reading the digit inputs rather than a banner is
      deliberate: a banner can be produced by the generic failure path, so asserting one would pass
      even if the INVALID_PIN branch never ran. Emptying the boxes is something ONLY that branch does.
    */
    await waitFor(() => {
      expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('');
    });
    // ... and Send is disabled again, because six digits are no longer present.
    expect(screen.getByRole('button', { name: 'Send €50.00' })).toBeDisabled();
  });

  it('PIN_LOCKED shows the server lock horizon, not a client-invented one', async () => {
    server.use(
      http.post('*/api/transfers', () =>
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many attempts.',
          extensions: { retryAfterSeconds: 120 },
        }),
      ),
    );
    renderTransfer();
    await reachPinStep();

    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

    // 120s from the server, NOT the 15-minute client default — proving the value travelled.
    expect(await screen.findByText(/Too many incorrect PIN attempts/)).toBeInTheDocument();
    // 120s off the response, rendered by the shared countdown as m:ss — NOT a client-side default.
    expect(screen.getByRole('timer')).toHaveTextContent('Try again in 2:00');
  });

  it('the lock EXPIRES — the PIN boxes come back once the window closes', async () => {
    /*
      Nothing asserted this before, on any of the four surfaces that can be PIN-locked, which is how
      two of them shipped with a countdown that never moved. Here the page did tick — with its own
      private copy of the effect, one of three implementations of the same requirement. All four now
      share `RetryCountdown`, and this is the test that would catch any of them freezing again.

      The timers are installed AFTER the navigation and BEFORE the lock, and NOT with
      `shouldAdvanceTime`. Measured: with it, the countdown re-renders once per real second, and any
      second that elapses between the lock and the assertions lands outside `act(...)` — the
      console.error gate in `test/setup.ts` fails the test for it, correctly. A frozen clock plus
      `userEvent.setup({ advanceTimers })` removes the window entirely.
    */
    server.use(
      http.post('*/api/transfers', () =>
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many attempts.',
          extensions: { retryAfterSeconds: 5 },
        }),
      ),
    );
    renderTransfer();
    await reachPinStep();

    // Installed here, not at the top: before this click no countdown exists, so nothing can tick.
    // The window between the lock appearing and the advance below is where an unacted tick would
    // land — the console.error gate catches those, so it is kept to a single assertion.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

    expect(await screen.findByText(/Too many incorrect PIN attempts/)).toBeInTheDocument();

    await act(async () => {
      vi.advanceTimersByTime(6000);
    });

    await waitFor(() =>
      expect(screen.queryByText(/Too many incorrect PIN attempts/)).not.toBeInTheDocument(),
    );
    // The boxes are what the lock disabled, so they are what has to come back. Send waits for six
    // digits, because the lock branch cleared the PIN — that is the flow, not a residue.
    expect(screen.getByLabelText('Digit 1 of 6')).toBeEnabled();
  });

  it('PIN_REQUIRED routes to PIN setup, which is the only place it can be fixed', async () => {
    server.use(
      http.post('*/api/transfers', () =>
        problem({ status: 422, errorCode: 'PIN_REQUIRED', detail: 'PIN must be set.' }),
      ),
    );
    renderTransfer();
    await reachPinStep();

    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

    expect(await screen.findByText('PIN SETUP')).toBeInTheDocument();
  });
});

describe('the internal transfer recovers the same way', () => {
  /*
    Both pages carry their own copy of the recovery ladder, so both need proof. The external page's
    branches were dead for the same reason this page's were — one shared wizard bug — but nothing
    stops the two drifting apart later, and only a test per page notices.
  */
  function renderInternal() {
    return renderWithProviders(
      <Routes>
        <Route path="/" element={<InternalTransferPage />} />
        <Route path="/pin-setup" element={<div>PIN SETUP</div>} />
      </Routes>,
      { routerEntries: ['/'] },
    );
  }

  async function reachInternalPinStep() {
    // findByRole, not findByText: this page lists every account TWICE (source and destination), so
    // a text query for the account name is ambiguous by construction.
    await screen.findByRole('button', { name: 'From Main Account' });
    await userEvent.click(screen.getByRole('button', { name: 'To Rainy Day' }));
    await userEvent.type(screen.getByLabelText('Transfer amount'), '50');
    await userEvent.click(screen.getByRole('button', { name: 'Review Transfer' }));
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123456');
  }

  it('INVALID_PIN clears the boxes here too', async () => {
    server.use(
      http.post('*/api/transfers/internal', () =>
        problem({ status: 401, errorCode: 'INVALID_PIN', detail: 'Invalid PIN.' }),
      ),
    );
    renderInternal();
    await reachInternalPinStep();

    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

    await waitFor(() => {
      expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('');
    });
  });

  it('PIN_REQUIRED routes to PIN setup here too', async () => {
    server.use(
      http.post('*/api/transfers/internal', () =>
        problem({ status: 422, errorCode: 'PIN_REQUIRED', detail: 'PIN must be set.' }),
      ),
    );
    renderInternal();
    await reachInternalPinStep();

    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

    expect(await screen.findByText('PIN SETUP')).toBeInTheDocument();
  });

  it('PIN_LOCKED shows the server horizon here too', async () => {
    // The branch that reads `retryAfterSeconds` off the response — the only one where the two pages
    // could drift without a compile error, since it threads a value rather than a constant.
    server.use(
      http.post('*/api/transfers/internal', () =>
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many attempts.',
          extensions: { retryAfterSeconds: 120 },
        }),
      ),
    );
    renderInternal();
    await reachInternalPinStep();

    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

    expect(await screen.findByText(/Too many incorrect PIN attempts/)).toBeInTheDocument();
    // 120s off the response, rendered by the shared countdown as m:ss — NOT a client-side default.
    expect(screen.getByRole('timer')).toHaveTextContent('Try again in 2:00');
  });

  it('the lock EXPIRES here too — one primitive, four surfaces', async () => {
    // The sibling page carried its own copy of the tick; both now share `RetryCountdown`. This is
    // the assertion that keeps them from drifting apart again, which a shared component alone
    // cannot guarantee — the two pages could still differ in how they clear it.
    server.use(
      http.post('*/api/transfers/internal', () =>
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many attempts.',
          extensions: { retryAfterSeconds: 5 },
        }),
      ),
    );
    renderInternal();
    await reachInternalPinStep();

    // Installed here, not at the top: before this click no countdown exists, so nothing can tick.
    // The window between the lock appearing and the advance below is where an unacted tick would
    // land — the console.error gate catches those, so it is kept to a single assertion.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    await userEvent.click(screen.getByRole('button', { name: 'Send €50.00' }));

    expect(await screen.findByText(/Too many incorrect PIN attempts/)).toBeInTheDocument();

    await act(async () => {
      vi.advanceTimersByTime(6000);
    });

    await waitFor(() =>
      expect(screen.queryByText(/Too many incorrect PIN attempts/)).not.toBeInTheDocument(),
    );
    expect(screen.getByLabelText('Digit 1 of 6')).toBeEnabled();
  });
});
