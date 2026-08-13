import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { http } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { seedMockSession } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { TransferPage } from './TransferPage';
import { InternalTransferPage } from './InternalTransferPage';

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
    expect(screen.getByText(/2 minutes/)).toBeInTheDocument();
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
    expect(screen.getByText(/2 minutes/)).toBeInTheDocument();
  });
});
