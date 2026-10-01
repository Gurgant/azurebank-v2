import { Route, Routes } from 'react-router-dom';
import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../mocks/server';
import { MOCK_PASSWORD, MOCK_USER } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { expectNoNestedLiveRegions } from '../test/liveRegions';
import {
  COPY,
  advanceUntil,
  fakeClockUser,
  hintRegion,
  hintShownAt,
  installFakeClock,
  never,
} from '../test/outage';
import { LoginPage } from './LoginPage';
import { RegisterPage } from './RegisterPage';

/**
 * Signing in or registering while the service is slow: the button's spinner is not left alone to
 * say that something is happening. The words arrive under the button at 5 s and change at 20 s,
 * in a polite region of their own; there is no "Stop waiting", because a sign-in or a
 * registration the server may already be doing cannot be taken back by the page.
 */

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

async function signIn() {
  renderWithProviders(
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/dashboard" element={<div>DASHBOARD</div>} />
    </Routes>,
    { routerEntries: ['/login'] },
  );
  await userEvent.type(await screen.findByLabelText(/email address/i), MOCK_USER.email);
  await userEvent.type(screen.getByLabelText(/^password$/i), MOCK_PASSWORD);
  return screen.getByRole('button', { name: 'Sign in' });
}

async function register() {
  renderWithProviders(
    <Routes>
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/pin-setup" element={<div>PIN SETUP</div>} />
    </Routes>,
    { routerEntries: ['/register'] },
  );
  await userEvent.type(await screen.findByLabelText(/first name/i), 'Test');
  await userEvent.type(screen.getByLabelText(/last name/i), 'User');
  await userEvent.type(screen.getByLabelText(/azuretag/i), 'test_user');
  await userEvent.type(screen.getByLabelText(/^email$/i), 'fresh@azurebank.dev');
  await userEvent.type(screen.getByLabelText(/^password$/i), 'Password1!');
  await userEvent.type(screen.getByLabelText(/^confirm password$/i), 'Password1!');
  return screen.getByRole('button', { name: 'Create Account' });
}

describe('a slow sign-in or registration says so', () => {
  it.each([
    ['signing in', '*/bff/auth/login', signIn],
    ['registering', '*/bff/auth/register', register],
  ])(
    '%s: silent before 5 s, then slow, then still trying, under the button, with no way to stop it',
    async (_action, path, fill) => {
      let sentAt = 0;
      server.use(
        http.post(path, () => {
          sentAt = Date.now();
          return never();
        }),
      );
      const submit = await fill();
      installFakeClock();
      const user = fakeClockUser();
      const clickedAt = Date.now();
      await user.click(submit);
      await waitFor(() => expect(sentAt).toBeGreaterThan(0));
      const shownAt = await hintShownAt();

      await advanceUntil(clickedAt, 4_900);
      expect(screen.queryByText(COPY.slow)).not.toBeInTheDocument();
      await advanceUntil(shownAt, 5_000);
      hintRegion(COPY.slow);
      await advanceUntil(shownAt, 20_000);
      const region = hintRegion(COPY.stillTrying);

      expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
      expect(
        submit.compareDocumentPosition(region) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy();
      expectNoNestedLiveRegions();
    },
  );
});
