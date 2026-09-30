import { Route, Routes } from 'react-router-dom';
import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { makeTestStore, renderWithProviders, type TestStore } from '../test/renderWithProviders';
import { PinSetupPage } from './PinSetupPage';
import { MOCK_PASSWORD, MOCK_USER, seedMockSession } from '../mocks/state';
import { server } from '../mocks/server';
import { serviceUnavailable } from '../mocks/problem';
import { COPY, advanceUntil, fakeClockUser, installFakeClock, never } from '../test/outage';
import { CONNECTION_FAILED } from '../api/problemMessages';

/**
 * PR-10 — the PIN onboarding wizard (enter → confirm → done). Pins: the confirm-must-match
 * guard, the success → returnTo hand-back, and the "already has a PIN → bounce" redirect.
 */

function renderPinSetup(
  store: TestStore = makeTestStore(),
  entry = '/pin-setup?returnTo=/accounts',
) {
  return renderWithProviders(
    <Routes>
      <Route path="/pin-setup" element={<PinSetupPage />} />
      <Route path="/accounts" element={<div>ACCOUNTS PAGE</div>} />
      <Route path="/dashboard" element={<div>DASHBOARD PAGE</div>} />
    </Routes>,
    { store, routerEntries: [entry] },
  );
}

function storeWithUser(hasPin: boolean) {
  const store = makeTestStore();
  store.dispatch({
    type: 'api/executeQuery/fulfilled',
    meta: { arg: { endpointName: 'getMe' } },
    payload: {
      user: {
        id: '019f7b3f-0000-7000-8000-0000000000a1',
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

async function pasteDigits(pin: string) {
  await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
  await userEvent.paste(pin);
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
  /*
    Seeded WITHOUT a PIN, which is the only state this page is reachable in: PinSetupPage bounces a
    user whose `hasPin` is already true. The default seed (MOCK_USER, hasPin: true) described a user
    who could never arrive here, and the server-side mock now enforces the difference — changing a
    PIN requires the current one, enrolling does not.
  */
  seedMockSession({ ...MOCK_USER, hasPin: false });
});

describe('PIN setup wizard (PR-10)', () => {
  /** The account password now gates the commit (T7/#201) — typed on the confirm step. */
  async function typePassword(value = MOCK_PASSWORD) {
    await userEvent.click(screen.getByLabelText('Account password'));
    await userEvent.paste(value);
  }

  it('enter → confirm → success, then hands back to returnTo', async () => {
    renderPinSetup();

    expect(screen.getByText('Create your PIN')).toBeInTheDocument();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByText('Confirm your PIN')).toBeInTheDocument();
    // Password FIRST: the auto-submit on the sixth digit is deliberately suppressed until one is
    // present, so that a wizard cannot spend a refusal to tell the user about a field it can see
    // is empty.
    await typePassword();
    await pasteDigits('123456'); // onComplete auto-submits once the password is in hand

    expect(await screen.findByText('PIN Setup Complete!')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText('ACCOUNTS PAGE')).toBeInTheDocument();
  });

  it('will not commit a PIN until the account password is supplied', async () => {
    /*
      The client half of T7. The server refuses a password-less enrolment with 422
      PASSWORD_REQUIRED — measured — but a form that lets the user press the button and then
      reports a refusal has taught them nothing they could not have been told for free.
    */
    renderPinSetup();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');

    await pasteDigits('123456');

    // Six digits in, PINs matching, and still nothing has been sent.
    expect(screen.queryByText('PIN Setup Complete!')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Set PIN' })).toBeDisabled();

    await typePassword();
    expect(screen.getByRole('button', { name: 'Set PIN' })).toBeEnabled();
  });

  it('says which credential was wrong when the password is rejected', async () => {
    // The PIN boxes are fine; asking the user to retype six digits because a password was
    // mistyped is the kind of friction that produces shorter passwords.
    renderPinSetup();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    await typePassword('WrongPassword1!');
    await pasteDigits('123456');

    expect(await screen.findByText('That password is not correct.')).toBeInTheDocument();
    expect(screen.queryByText('PIN Setup Complete!')).not.toBeInTheDocument();
  });

  it('rejects a mismatched confirmation and keeps the user on the confirm step', async () => {
    renderPinSetup();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    await typePassword();
    await pasteDigits('654321');

    expect(await screen.findByText('PINs do not match. Please try again.')).toBeInTheDocument();
    expect(screen.getByText('Confirm your PIN')).toBeInTheDocument();
    expect(screen.queryByText('PIN Setup Complete!')).not.toBeInTheDocument();
  });

  it('bounces a user who already has a PIN straight to returnTo', async () => {
    renderPinSetup(storeWithUser(true));

    expect(await screen.findByText('ACCOUNTS PAGE')).toBeInTheDocument();
    expect(screen.queryByText('Create your PIN')).not.toBeInTheDocument();
  });

  it('lets the user skip setup for now', async () => {
    renderPinSetup();
    await userEvent.click(screen.getByRole('button', { name: 'Skip for now' }));
    expect(await screen.findByText('ACCOUNTS PAGE')).toBeInTheDocument();
  });
});

describe('PIN setup during an outage', () => {
  // Unmount first, then restore the clock: a render still mounted on a fake clock leaks into the
  // next test.
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  /** The confirm step with the password in hand: the next six digits send. */
  async function toTheConfirmation() {
    renderPinSetup();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
    await userEvent.click(screen.getByLabelText('Account password'));
    await userEvent.paste(MOCK_PASSWORD);
  }

  it('a 503 says the PIN may have been saved', async () => {
    server.use(
      http.post('*/bff/auth/set-pin', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/set-pin' }),
      ),
    );
    await toTheConfirmation();
    await pasteDigits('123456');

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
    expect(screen.queryByText('PIN Setup Complete!')).not.toBeInTheDocument();
  });

  it('no answer in 65 s says the same, not "check your connection"', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/bff/auth/set-pin', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    await toTheConfirmation();
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste('123456');
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 65_100);

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.saveUnknown));
    expect(screen.queryByText(CONNECTION_FAILED)).not.toBeInTheDocument();
  });
});
