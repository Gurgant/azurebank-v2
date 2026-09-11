import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { Route, Routes, useLocation } from 'react-router-dom';
import { server } from '../../mocks/server';
import { MOCK_USER, mockState, seedMockSession, type MockSessionUser } from '../../mocks/state';
import { makeTestStore, renderWithProviders } from '../../test/renderWithProviders';
import { TEST_PIN } from '../../test/pinFlow';
import { SettingsPage } from '../../pages/SettingsPage';

afterEach(() => {
  cleanup();
  server.events.removeAllListeners();
});

/**
 * Changing a PIN from Settings — the request /pin-setup could never send, because it bounces a user
 * who already has one.
 *
 * The mock answers every case below, and each answer it gives was measured through the REAL BFF on
 * 2026-09-11 against a fresh user, on a scratch database seeded from main 7ceb16f. The measured row
 * is quoted beside each assertion that depends on it.
 */

function PinSetupProbe() {
  return (
    <>
      <div>PIN SETUP PAGE</div>
      <div data-testid="pin-setup-search">{useLocation().search}</div>
    </>
  );
}

/** Settings, signed in as `user`, with no live getMe subscription (settings.test.tsx's reason). */
async function renderSettings(user: MockSessionUser = MOCK_USER) {
  seedMockSession(user);
  const store = makeTestStore();
  store.dispatch({
    type: 'api/executeQuery/fulfilled',
    meta: { arg: { endpointName: 'getMe' } },
    payload: { user: { ...user } },
  });
  renderWithProviders(
    <Routes>
      <Route path="/settings" element={<SettingsPage />} />
      <Route path="/pin-setup" element={<PinSetupProbe />} />
    </Routes>,
    { store, routerEntries: ['/settings'] },
  );
  await screen.findByText(`${user.firstName} ${user.lastName}`);
  return store;
}

async function openDialog() {
  await userEvent.click(screen.getByRole('button', { name: 'Change PIN' }));
  return screen.findByRole('dialog', { name: 'Change your PIN' });
}

function group(name: 'Current PIN' | 'New PIN' | 'Confirm new PIN') {
  return screen.getByRole('group', { name });
}

function boxes(name: 'Current PIN' | 'New PIN' | 'Confirm new PIN') {
  return Array.from({ length: 6 }, (_, i) =>
    within(group(name)).getByLabelText(`Digit ${i + 1} of 6`),
  );
}

/** Paste, never type: `userEvent.type` truncates Fluent's Input (test/pinFlow.ts). */
async function fill(name: 'Current PIN' | 'New PIN' | 'Confirm new PIN', pin: string) {
  await userEvent.click(within(group(name)).getByLabelText('Digit 1 of 6'));
  await userEvent.paste(pin);
}

async function fillAll(current: string, next: string, confirm = next) {
  await fill('Current PIN', current);
  await fill('New PIN', next);
  await fill('Confirm new PIN', confirm);
}

/** Every set-pin body sent from now on, observed without replacing the handler that answers it. */
function observeSetPin() {
  const bodies: unknown[] = [];
  server.events.on('request:start', ({ request }) => {
    if (!request.url.endsWith('/bff/auth/set-pin')) return;
    void request
      .clone()
      .text()
      .then((body) => bodies.push(JSON.parse(body)));
  });
  return bodies;
}

describe('Settings — the PIN row', () => {
  it('offers the change to a user who has a PIN', async () => {
    await renderSettings();

    expect(screen.getByRole('button', { name: 'Change PIN' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: 'Set up PIN' })).not.toBeInTheDocument();
  });

  it('sends a user without one to set it up, and back to Settings afterwards', async () => {
    /*
      The dialog would be the wrong door: with no PIN stored, the API treats set-pin as an
      ENROLMENT, which costs the password, not a current PIN (AuthService.SetPinAsync) —
      measured M1 on 2026-09-11: {pin, password} -> 200. So the row goes to the page that asks for
      the password, and the query string carries a PATH and nothing else.
    */
    await renderSettings({ ...MOCK_USER, hasPin: false });

    expect(screen.getByText('Not set up yet.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Change PIN' })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Set up PIN' }));

    expect(await screen.findByText('PIN SETUP PAGE')).toBeInTheDocument();
    expect(screen.getByTestId('pin-setup-search')).toHaveTextContent('?returnTo=/settings');
  });
});

describe('ChangePinDialog', () => {
  it('changes the PIN with the current one, and sends nothing else', async () => {
    // Measured M4: {pin:"246802", currentPin:"135790"} -> 200 {"message":"PIN set successfully"}.
    await renderSettings();
    const sent = observeSetPin();
    const dialog = await openDialog();

    await fillAll(TEST_PIN, '246802');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Change PIN' }));

    expect(await within(dialog).findByText('Your PIN has been changed.')).toBeInTheDocument();
    // The body is the change branch's and only that: no password, which is the ENROLMENT proof.
    expect(sent).toEqual([{ pin: '246802', currentPin: TEST_PIN }]);
    expect(mockState.pin).toBe('246802');

    await userEvent.click(within(dialog).getByRole('button', { name: 'Done' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('waits for all three PINs before it will send', async () => {
    await renderSettings();
    const sent = observeSetPin();
    const dialog = await openDialog();

    await fillAll(TEST_PIN, '246802', '24680');
    const submit = within(dialog).getByRole('button', { name: 'Change PIN' });
    expect(submit).toBeDisabled();

    await userEvent.click(submit);
    expect(sent).toEqual([]);
  });

  it('checks the confirmation before sending, so a mismatch costs no attempt', async () => {
    await renderSettings();
    const sent = observeSetPin();
    const dialog = await openDialog();

    await fillAll(TEST_PIN, '246802', '246803');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Change PIN' }));

    expect(await within(dialog).findByText('PINs do not match. Please try again.')).toBeVisible();
    expect(sent).toEqual([]);
    expect(mockState.pinAttempts).toBe(0);
    // Only the confirmation is asked for again; the two PINs that were fine are kept.
    for (const box of boxes('Confirm new PIN')) {
      expect(box).toHaveValue('');
      expect(box).toHaveAttribute('aria-invalid', 'true');
    }
    expect(boxes('Current PIN')[5]).toHaveValue(TEST_PIN[5]);
    expect(boxes('New PIN')[5]).toHaveValue('2');
  });

  it('keeps a wrong current PIN in the dialog, names it, and keeps the user signed in', async () => {
    /*
      Measured M3: {pin:"246802", currentPin:"000000"} -> 401
        {"title":"Unauthorized","status":401,"detail":"Invalid PIN.","instance":"/api/auth/pin",
         "errorCode":"INVALID_PIN"}
      and GET /bff/auth/me straight after -> 200. The session survived, so the app must too:
      sessionMiddleware routes a 401 on its errorCode, and INVALID_PIN is one of the in-flow codes.
    */
    const store = await renderSettings();
    const dialog = await openDialog();

    await fillAll('000000', '246802');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Change PIN' }));

    expect(await within(dialog).findByText('That is not your current PIN.')).toBeVisible();
    expect(store.getState().auth.status).toBe('authenticated');
    expect(mockState.pin).toBe(TEST_PIN);
    for (const box of boxes('Current PIN')) {
      expect(box).toHaveValue('');
      expect(box).toHaveAttribute('aria-invalid', 'true');
    }
    expect(boxes('New PIN')[5]).toHaveValue('2');
    expect(boxes('Confirm new PIN')[5]).toHaveValue('2');

    // Re-entering the current PIN is all it takes; the new one was never the problem.
    await fill('Current PIN', TEST_PIN);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Change PIN' }));
    expect(await within(dialog).findByText('Your PIN has been changed.')).toBeInTheDocument();
    expect(mockState.pin).toBe('246802');
  });

  it('locks on the third wrong PIN and counts down, with everything disabled', async () => {
    /*
      Measured M6a-c: two wrong current PINs -> 401 INVALID_PIN, the third ->
        429 {"detail":"Too many incorrect PIN attempts. Your PIN is temporarily locked; try again
             later.","instance":"/api/auth/pin","errorCode":"PIN_LOCKED","retryAfterSeconds":900,
             "lockedUntil":"2026-09-11T13:08:28.9504552+00:00"}
      with NO Retry-After header, so the deadline has to come from the body. M6d: the CORRECT
      current PIN while locked -> the same 429, because the lock is checked first. The lock is the
      one every PIN gate shares, which is why it is worth a countdown rather than a retry.
    */
    mockState.pinAttempts = 2;
    await renderSettings();
    const dialog = await openDialog();

    await fillAll('000000', '246802');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Change PIN' }));

    expect(await within(dialog).findByText('Too many incorrect PIN attempts.')).toBeVisible();
    expect(within(dialog).getByRole('timer')).toHaveTextContent(/^Try again in 1[45]:\d\d$/);
    expect(within(dialog).getByRole('button', { name: 'Change PIN' })).toBeDisabled();
    for (const box of boxes('Current PIN')) {
      expect(box).toHaveValue('');
      expect(box).toBeDisabled();
    }
    expect(mockState.pin).toBe(TEST_PIN);
  });

  it('closes on Cancel without sending', async () => {
    await renderSettings();
    const sent = observeSetPin();
    const dialog = await openDialog();

    await fill('Current PIN', TEST_PIN);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(sent).toEqual([]);
  });
});
