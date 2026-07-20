import { Route, Routes } from 'react-router-dom';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http } from 'msw';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { MOCK_PASSWORD, MOCK_USER, seedMockSession } from '../../mocks/state';
import { makeTestStore, renderWithProviders, type TestStore } from '../../test/renderWithProviders';
import { apiSlice } from '../api/apiSlice';
import { ProtectedRoute } from '../../components/layout/ProtectedRoute';
import { LoginPage } from '../../pages/LoginPage';
import { RegisterPage } from '../../pages/RegisterPage';
import { getLastServerActivity } from './sessionActivity';

/** Runs the B3 bootstrap probe exactly like AuthBootstrap does. */
async function boot(store: TestStore) {
  await store
    .dispatch(apiSlice.endpoints.getMe.initiate())
    .unwrap()
    .catch(() => undefined);
}

describe('auth bootstrap (D6)', () => {
  it('resolves to anonymous when the probe 401s — no error surfaced', async () => {
    const store = makeTestStore();
    expect(store.getState().auth.status).toBe('unknown');

    await boot(store);

    expect(store.getState().auth.status).toBe('anonymous');
    expect(store.getState().auth.user).toBeNull();
  });

  it('resolves to authenticated with the session user when the cookie is alive', async () => {
    seedMockSession();
    const store = makeTestStore();

    await boot(store);

    expect(store.getState().auth.status).toBe('authenticated');
    expect(store.getState().auth.user?.email).toBe(MOCK_USER.email);
    // The middleware timestamps every server response (D14 client mirror).
    expect(getLastServerActivity()).not.toBeNull();
  });
});

describe('global 401 handling (D3)', () => {
  it('an errorCode-less 401 after an authenticated boot expires the session AND resets the cache', async () => {
    seedMockSession();
    const store = makeTestStore();
    await boot(store);
    expect(store.getState().auth.status).toBe('authenticated');

    server.use(
      http.get('*/api/accounts', () =>
        // The BFF's own 401 shape: ProblemDetails WITHOUT errorCode.
        problem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' }),
      ),
    );
    await store
      .dispatch(apiSlice.endpoints.getAccounts.initiate())
      .unwrap()
      .catch(() => undefined);

    expect(store.getState().auth.status).toBe('expired');
    // resetApiState wiped the financial cache along with the session.
    expect(Object.keys(store.getState().api.queries)).toHaveLength(0);
  });

  it('a 401 INVALID_CREDENTIALS stays on the login form — no session expiry', async () => {
    const store = makeTestStore();
    await boot(store);
    expect(store.getState().auth.status).toBe('anonymous');

    await store
      .dispatch(
        apiSlice.endpoints.login.initiate({ email: MOCK_USER.email, password: 'Wrong-Pass-1!' }),
      )
      .unwrap()
      .catch(() => undefined);

    expect(store.getState().auth.status).toBe('anonymous');
  });
});

describe('ProtectedRoute guard', () => {
  function Harness() {
    return (
      <Routes>
        <Route path="/login" element={<div>LOGIN_PAGE</div>} />
        <Route
          path="/secret"
          element={
            <ProtectedRoute>
              <div>SECRET_CONTENT</div>
            </ProtectedRoute>
          }
        />
      </Routes>
    );
  }

  it('holds on a spinner while the boot probe is unresolved', () => {
    const store = makeTestStore(); // status 'unknown' — probe never dispatched
    renderWithProviders(<Harness />, { store, routerEntries: ['/secret'] });

    expect(screen.getByLabelText('Checking your session')).toBeInTheDocument();
    expect(screen.queryByText('SECRET_CONTENT')).not.toBeInTheDocument();
  });

  it('redirects anonymous users to /login and authenticated users straight through', async () => {
    const store = makeTestStore();
    await boot(store); // anonymous
    renderWithProviders(<Harness />, { store, routerEntries: ['/secret'] });
    expect(screen.getByText('LOGIN_PAGE')).toBeInTheDocument();

    seedMockSession();
    const authedStore = makeTestStore();
    await boot(authedStore);
    renderWithProviders(<Harness />, { store: authedStore, routerEntries: ['/secret'] });
    expect(screen.getByText('SECRET_CONTENT')).toBeInTheDocument();
  });
});

describe('login flow (returnTo + error branching)', () => {
  function LoginHarness() {
    return (
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/dashboard" element={<div>DASHBOARD</div>} />
        <Route path="/transfer" element={<div>TRANSFER_PAGE</div>} />
      </Routes>
    );
  }

  it('signs in and lands on the guarded page the user was heading to', async () => {
    const user = userEvent.setup();
    const store = makeTestStore();
    await boot(store);
    renderWithProviders(<LoginHarness />, {
      store,
      routerEntries: [{ pathname: '/login', state: { from: { pathname: '/transfer' } } }],
    });

    await user.type(screen.getByLabelText(/email address/i), MOCK_USER.email);
    await user.type(screen.getByLabelText(/^password$/i), MOCK_PASSWORD);
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => expect(screen.getByText('TRANSFER_PAGE')).toBeInTheDocument());
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('shows the credential error inline on a wrong password', async () => {
    const user = userEvent.setup();
    const store = makeTestStore();
    await boot(store);
    renderWithProviders(<LoginHarness />, { store, routerEntries: ['/login'] });

    await user.type(screen.getByLabelText(/email address/i), MOCK_USER.email);
    await user.type(screen.getByLabelText(/^password$/i), 'Wrong-Pass-1!');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => expect(screen.getByText('Invalid email or password.')).toBeInTheDocument());
    expect(store.getState().auth.status).toBe('anonymous');
  });

  it('shows the expiry note when redirected with reason=expired', async () => {
    const store = makeTestStore();
    await boot(store);
    renderWithProviders(<LoginHarness />, {
      store,
      routerEntries: [{ pathname: '/login', state: { reason: 'expired' } }],
    });

    expect(screen.getByText(/your session has expired/i)).toBeInTheDocument();
  });
});

describe('register flow (D15 dual-path + D13 rate limit)', () => {
  async function fillRegisterForm(user: ReturnType<typeof userEvent.setup>, email: string) {
    await user.type(screen.getByLabelText(/first name/i), 'Test');
    await user.type(screen.getByLabelText(/last name/i), 'User');
    await user.type(screen.getByLabelText(/azuretag/i), 'test_user');
    await user.type(screen.getByLabelText(/^email$/i), email);
    await user.type(screen.getByLabelText(/^password$/i), 'Password1!');
    await user.type(screen.getByLabelText(/confirm password/i), 'Password1!');
    await user.click(screen.getByRole('button', { name: /create account/i }));
  }

  it('shows the neutral dual-path banner on REGISTRATION_FAILED — form values kept', async () => {
    const user = userEvent.setup();
    renderWithProviders(<RegisterPage />, { routerEntries: ['/register'] });

    await fillRegisterForm(user, 'taken@azurebank.dev');

    await waitFor(() =>
      expect(screen.getByText(/we couldn't create an account/i)).toBeInTheDocument(),
    );
    expect(screen.getByText(/sign in with this email/i)).toBeInTheDocument();
    // Field values survive (D15) — the user edits, not retypes.
    expect(screen.getByLabelText(/^email$/i)).toHaveValue('taken@azurebank.dev');
  });

  it('shows the countdown and disables submit on the shared-bucket 429', async () => {
    server.use(
      http.post('*/bff/auth/register', () =>
        problem({
          status: 429,
          errorCode: 'RATE_LIMIT_EXCEEDED',
          detail: 'Too many requests. Please retry later.',
          extensions: { retryAfterSeconds: 30 },
        }),
      ),
    );
    const user = userEvent.setup();
    renderWithProviders(<RegisterPage />, { routerEntries: ['/register'] });

    await fillRegisterForm(user, 'fresh@azurebank.dev');

    await waitFor(() => expect(screen.getByRole('timer')).toHaveTextContent(/try again in/i));
    expect(screen.getByRole('button', { name: /create account/i })).toBeDisabled();
  });
});
