import { Route, Routes } from 'react-router-dom';
import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { bffProblem, problem, serviceUnavailable } from '../../mocks/problem';
import { MOCK_PASSWORD, mockState, seedMockSession } from '../../mocks/state';
import { makeTestStore, renderWithProviders, type TestStore } from '../../test/renderWithProviders';
import { enterPin } from '../../test/pinFlow';
import {
  COPY,
  advance,
  advanceUntil,
  fakeClockUser,
  hintRegion,
  never,
  track,
  installFakeClock,
} from '../../test/outage';
import { ProtectedRoute } from '../../components/layout/ProtectedRoute';
import { apiSlice } from '../api/apiSlice';
import { authReducer } from './authSlice';
import { AuthBootstrap } from './AuthBootstrap';
import { SessionExpiryWarning } from './SessionExpiryWarning';
import { StepUpModal } from './StepUpModal';

/**
 * A 5xx never signs anyone out. Only a real 401 does.
 *
 * Three places could turn an outage into a sign-out, and each is pinned here from the visitor's
 * side: "Sign out now" whose logout cannot reach the server (the dialog stays and says why — a
 * silent sign-out screen over a session that may still be alive is the one thing the app's own
 * rule forbids); the boot probe failing with a 503 (a page that says the service is down, not the
 * sign-in page); and a read that times out while signed in (nothing happens to the session).
 * Beside them, the guards that must not move: a real 401, a 4xx at boot, the expiry at 0:00.
 */

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const INACTIVITY_WINDOW_MS = 3 * 60_000;

/**
 * Signed in, with the policy learned from /bff/auth/me on the real clock (the deadline is anchored
 * to it).
 */
async function boot(inactivityWindowMs = INACTIVITY_WINDOW_MS): Promise<TestStore> {
  seedMockSession();
  mockState.sessionInactivityWindowMs = inactivityWindowMs;
  mockState.sessionAbsoluteWindowMs = 60 * 60_000;
  const store = makeTestStore();
  await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
  return store;
}

/**
 * The app's frame around one guarded page: the boot probe, the guard, the warning, the sign-in
 * page.
 */
function renderApp(store: TestStore, entry = '/dashboard') {
  return renderWithProviders(
    <>
      <AuthBootstrap />
      <SessionExpiryWarning />
      <Routes>
        <Route
          path="/dashboard"
          element={
            <ProtectedRoute>
              <div>DASHBOARD</div>
            </ProtectedRoute>
          }
        />
        <Route path="/login" element={<div>LOGIN PAGE</div>} />
      </Routes>
    </>,
    { store, routerEntries: [entry] },
  );
}

const warning = () => screen.queryByRole('alertdialog');

/** Into the warning's last two minutes. */
async function reachTheWarning() {
  await advance(61_000);
  await waitFor(() => expect(warning()).toBeInTheDocument());
}

describe('"Sign out now" during an outage', () => {
  it('keeps the dialog and says why when the server cannot be reached', async () => {
    const store = await boot();
    let down = true;
    server.use(
      http.post('*/bff/auth/logout', () =>
        down ? serviceUnavailable({ via: 'bff', instance: '/bff/auth/logout' }) : undefined,
      ),
      http.get('*/bff/auth/me', () => serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' })),
    );
    installFakeClock();
    const { router } = renderApp(store);
    await reachTheWarning();
    const user = fakeClockUser();

    await user.click(screen.getByRole('button', { name: /sign out now/i }));

    await waitFor(() =>
      expect(within(screen.getByRole('alertdialog')).getByRole('alert')).toHaveTextContent(
        COPY.unavailable,
      ),
    );
    expect(store.getState().auth.status).toBe('authenticated');
    expect(router.state.location.pathname).toBe('/dashboard');
    expect(screen.queryByText('LOGIN PAGE')).not.toBeInTheDocument();
    expect(screen.queryByText(/session has expired/i)).not.toBeInTheDocument();

    // The server is back: the same button now does what it says.
    down = false;
    await user.click(screen.getByRole('button', { name: /sign out now/i }));
    await waitFor(() => expect(store.getState().auth.status).toBe('anonymous'));
  });

  it('says it is slow while the sign-out is pending', async () => {
    const store = await boot();
    let sentAt = 0;
    server.use(
      http.post('*/bff/auth/logout', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    installFakeClock();
    renderApp(store);
    await reachTheWarning();
    const user = fakeClockUser();

    await user.click(screen.getByRole('button', { name: /sign out now/i }));
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 5_000);
    hintRegion(COPY.slow, screen.getByRole('alertdialog'));
  });

  it('a 200 still signs out', async () => {
    const store = await boot();
    installFakeClock();
    renderApp(store);
    await reachTheWarning();

    await fakeClockUser().click(screen.getByRole('button', { name: /sign out now/i }));

    await waitFor(() => expect(store.getState().auth.status).toBe('anonymous'));
  });

  it('a 401 still signs out: the session was already gone, which is what was asked for', async () => {
    const store = await boot();
    server.use(
      http.post('*/bff/auth/logout', () => {
        // Gone on the server before the click: the re-probe that follows must not find it.
        mockState.session = null;
        return bffProblem({
          status: 401,
          title: 'Unauthorized',
          detail: 'Session expired or invalid',
        });
      }),
    );
    installFakeClock();
    renderApp(store);
    await reachTheWarning();

    await fakeClockUser().click(screen.getByRole('button', { name: /sign out now/i }));

    await waitFor(() => expect(store.getState().auth.status).toBe('anonymous'));
  });
});

describe('the boot probe during an outage', () => {
  it('is not a sign-out: the page says the service is down and offers to try again', async () => {
    let down = true;
    server.use(
      http.get('*/bff/auth/me', () =>
        down ? serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' }) : undefined,
      ),
    );
    installFakeClock();
    const { router } = renderApp(makeTestStore());
    await advance(15_000);

    const main = await screen.findByRole('main');
    expect(
      within(main).getByRole('heading', { level: 1, name: COPY.unavailableTitle }),
    ).toBeInTheDocument();
    expect(within(main).getByRole('alert')).toHaveTextContent(COPY.unavailable);
    const tryAgain = within(main).getByRole('button', { name: COPY.tryAgain });
    expect(router.state.location.pathname).toBe('/dashboard');
    expect(screen.queryByText('LOGIN PAGE')).not.toBeInTheDocument();

    // Still down: the same view comes back, and focus with it.
    const user = fakeClockUser();
    await user.click(tryAgain);
    await advance(15_000);
    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByRole('button', { name: COPY.tryAgain })),
    );

    // Back up: Try again lets the visitor in.
    down = false;
    await user.click(screen.getByRole('button', { name: COPY.tryAgain }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
  });

  it('says it is slow, and offers no stop: there is nothing to show without it', async () => {
    let sentAt = 0;
    server.use(
      http.get('*/bff/auth/me', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    installFakeClock();
    renderApp(makeTestStore());
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 5_000);
    expect(screen.getByLabelText('Checking your session')).toBeInTheDocument();
    hintRegion(COPY.slow);
    await advanceUntil(sentAt, 20_000);
    hintRegion(COPY.stillTrying);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
  });

  it('a skipped duplicate probe decides nothing', () => {
    // RTK rejects a forced getMe that met a pending one with `meta.condition` set: no request was
    // made, so it says nothing about the session.
    const state = authReducer(undefined, {
      type: 'api/executeQuery/rejected',
      meta: { arg: { endpointName: 'getMe' }, condition: true, requestStatus: 'rejected' },
      error: {
        name: 'ConditionError',
        message: 'Aborted due to condition callback returning false.',
      },
    });

    expect(state.status).toBe('unknown');
  });

  it.each([
    [
      401,
      () =>
        bffProblem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' }),
    ],
    [403, () => problem({ status: 403, errorCode: 'FORBIDDEN', detail: 'Forbidden.' })],
    [
      429,
      () =>
        problem({
          status: 429,
          errorCode: 'RATE_LIMITED',
          detail: 'Too many requests.',
          headers: { 'Retry-After': '10' },
        }),
    ],
  ])('a %i at boot still goes to the sign-in page, with no note', async (_status, answer) => {
    server.use(http.get('*/bff/auth/me', answer));
    const { router } = renderApp(makeTestStore());

    expect(await screen.findByText('LOGIN PAGE')).toBeInTheDocument();
    expect((router.state.location.state as { reason?: string } | null)?.reason).toBeUndefined();
  });
});

describe('a signed-in session through an outage', () => {
  it('a read with no answer ends at 65 s and the visitor stays signed in', async () => {
    const store = await boot(15 * 60_000);
    let received = 0;
    server.use(
      http.get('*/api/accounts', () => {
        received = Date.now();
        return never();
      }),
    );
    installFakeClock();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(received).toBeGreaterThan(0));

    await advanceUntil(received, 65_100);
    await waitFor(() => expect(outcome.state).toBe('rejected'));
    expect(outcome.error).toMatchObject({ status: 'NETWORK', errorCode: 'TIMEOUT_ERROR' });
    expect(store.getState().auth.status).toBe('authenticated');
    query.unsubscribe();
  });

  it.each([
    ['the API', () => serviceUnavailable({ via: 'api', instance: '/api/accounts' })],
    ['the BFF', () => serviceUnavailable({ via: 'bff', instance: '/api/accounts' })],
    [
      'a renewal that could not finish',
      () =>
        serviceUnavailable({
          via: 'bff',
          detail: 'The session could not be renewed just now. Try again shortly.',
          retryAfterSeconds: 3,
          instance: '/api/accounts',
        }),
    ],
    ['an empty body', () => new HttpResponse(null, { status: 503 })],
  ])('a 503 from %s leaves the visitor signed in', async (_who, answer) => {
    const store = await boot(15 * 60_000);
    server.use(http.get('*/api/accounts', answer));
    installFakeClock();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await advance(15_000);

    await waitFor(() => expect(outcome.state).toBe('rejected'));
    expect(outcome.error).toMatchObject({ status: 503 });
    expect(store.getState().auth.status).toBe('authenticated');
    query.unsubscribe();
  });

  it('a money send refused with nothing applied leaves the visitor signed in', async () => {
    const store = await boot(15 * 60_000);
    server.use(
      http.post('*/api/transfers', () =>
        serviceUnavailable({ via: 'api', applied: false, instance: '/api/transfers' }),
      ),
    );

    await expect(
      store
        .dispatch(
          apiSlice.endpoints.transfer.initiate({
            idempotencyKey: crypto.randomUUID(),
            body: {
              fromAccountId: mockState.accounts[0].id,
              recipientAzureTag: 'friend',
              amount: 5,
            },
            stepUpAuthorizationId: crypto.randomUUID(),
          }),
        )
        .unwrap(),
    ).rejects.toMatchObject({ status: 503 });
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('at 0:00, a status check that fails still ends the session as expired', async () => {
    const store = await boot();
    server.use(
      http.get('*/bff/auth/session-status', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/session-status' }),
      ),
    );
    installFakeClock();
    renderApp(store);

    await advance(INACTIVITY_WINDOW_MS + 2_000);
    await advance(15_000);

    await waitFor(() => expect(store.getState().auth.status).toBe('expired'));
  });
});

describe('the words where a 503 meets a PIN or a password', () => {
  it('the step-up modal says the service is unavailable', async () => {
    server.use(
      http.post('*/bff/auth/verify-pin', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/verify-pin' }),
      ),
    );
    const store = makeTestStore();
    renderWithProviders(<StepUpModal />, { store });
    void store.dispatch(apiSlice.endpoints.revealAccountNumber.initiate(mockState.accounts[0].id));

    await screen.findByText("Verify it's you");
    await enterPin();

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.unavailable));
  });

  it('the re-authentication dialog says the service is unavailable', async () => {
    seedMockSession();
    mockState.sessionInactivityWindowMs = 30 * 60_000;
    mockState.sessionAbsoluteWindowMs = 150_000;
    const store = makeTestStore();
    await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
    server.use(
      http.post('*/bff/auth/reauthenticate', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/reauthenticate' }),
      ),
    );
    installFakeClock();
    renderWithProviders(
      <>
        <AuthBootstrap />
        <SessionExpiryWarning />
      </>,
      { store },
    );
    await advance(35_000);
    await waitFor(() => expect(warning()).toBeInTheDocument());

    const user = fakeClockUser();
    fireEvent.change(screen.getByLabelText(/enter your password/i), {
      target: { value: MOCK_PASSWORD },
    });
    await user.click(screen.getByRole('button', { name: /sign in again/i }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.unavailable));
  });
});
