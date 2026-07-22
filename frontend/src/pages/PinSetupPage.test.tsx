import { Route, Routes } from 'react-router-dom';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { makeTestStore, renderWithProviders, type TestStore } from '../test/renderWithProviders';
import { PinSetupPage } from './PinSetupPage';

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

describe('PIN setup wizard (PR-10)', () => {
  it('enter → confirm → success, then hands back to returnTo', async () => {
    renderPinSetup();

    expect(screen.getByText('Create your PIN')).toBeInTheDocument();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));

    expect(await screen.findByText('Confirm your PIN')).toBeInTheDocument();
    await pasteDigits('123456'); // onComplete auto-submits

    expect(await screen.findByText('PIN Setup Complete!')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    expect(await screen.findByText('ACCOUNTS PAGE')).toBeInTheDocument();
  });

  it('rejects a mismatched confirmation and keeps the user on the confirm step', async () => {
    renderPinSetup();
    await pasteDigits('123456');
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }));
    await screen.findByText('Confirm your PIN');
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
