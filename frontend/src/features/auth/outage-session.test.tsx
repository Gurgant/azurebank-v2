import { Provider } from 'react-redux';
import {
  createMemoryRouter,
  createRoutesFromElements,
  Route,
  RouterProvider,
  Routes,
} from 'react-router-dom';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { bffProblem, problem, serviceUnavailable } from '../../mocks/problem';
import { MOCK_PASSWORD, MOCK_USER, mockState, seedMockSession } from '../../mocks/state';
import { makeTestStore, renderWithProviders, type TestStore } from '../../test/renderWithProviders';
import { TEST_PIN, enterPin } from '../../test/pinFlow';
import {
  COPY,
  advance,
  advanceUntil,
  alertSlot,
  emulateFocusFixup,
  expectSilentHintTakesNoRoom,
  fakeClockUser,
  hintRegion,
  hintShownAt,
  never,
  track,
  installFakeClock,
} from '../../test/outage';
import { AppToaster } from '../../components/feedback';
import { ProtectedRoute } from '../../components/layout/ProtectedRoute';
import { ProtectedShell } from '../../components/layout/ProtectedShell';
import { RouteAnnouncer } from '../../components/layout/RouteAnnouncer';
import { ThemeProvider } from '../../theme/ThemeProvider';
import { SettingsPage } from '../../pages/SettingsPage';
import { apiSlice } from '../api/apiSlice';
import { authReducer } from './authSlice';
import { AuthBootstrap } from './AuthBootstrap';
import { SessionExpiryWarning } from './SessionExpiryWarning';
import { getLastServerActivity } from './sessionActivity';
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

/** The connection sentence, typed out for the same reason as `COPY`. */
const CONNECTION = "Couldn't reach the server — check your connection and try again.";

/** What the warning's own alert says, once it says it. */
async function warningSays(text: string) {
  await waitFor(() =>
    expect(within(screen.getByRole('alertdialog')).getByRole('alert')).toHaveTextContent(text),
  );
}

/**
 * Signed in with a 150 s absolute cap: the warning that asks for the password comes 30 s in, and
 * `reachTheCapWarning` goes 35 s in.
 */
async function bootAtTheCap(): Promise<TestStore> {
  seedMockSession();
  mockState.sessionInactivityWindowMs = 30 * 60_000;
  mockState.sessionAbsoluteWindowMs = 150_000;
  const store = makeTestStore();
  await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
  return store;
}

async function reachTheCapWarning() {
  await advance(35_000);
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

  it('moves nothing in the dialog while the sign-out is pending and its hint has no words', async () => {
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
    const dialog = screen.getByRole('alertdialog');

    await fakeClockUser().click(within(dialog).getByRole('button', { name: /sign out now/i }));
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));
    await hintShownAt(dialog);

    expectSilentHintTakesNoRoom(dialog);
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

  it.each([
    ['a lost connection', () => HttpResponse.error(), CONNECTION],
    [
      // A gateway's own error page: an answer came back, but not one the app can read.
      'an answer that cannot be read',
      () =>
        new HttpResponse('<html>Bad Gateway</html>', {
          status: 502,
          headers: { 'Content-Type': 'text/html' },
        }),
      CONNECTION,
    ],
    [
      'a 500',
      () =>
        problem({
          status: 500,
          errorCode: 'INTERNAL_ERROR',
          detail: 'An unexpected error occurred. Please try again later.',
        }),
      COPY.unavailable,
    ],
    [
      'a 429 from the rate limiter',
      () =>
        problem({
          status: 429,
          errorCode: 'RATE_LIMITED',
          detail: 'Too many requests.',
          headers: { 'Retry-After': '10' },
        }),
      COPY.unavailable,
    ],
  ])(
    '%s keeps the dialog, the session and what was fetched under it, and says why',
    async (_what, answer, words) => {
      const store = await boot();
      server.use(
        http.post('*/bff/auth/logout', answer),
        // A cleared cache would ask /me again, and this answer would lose what it held.
        http.get('*/bff/auth/me', () =>
          serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' }),
        ),
      );
      installFakeClock();
      renderApp(store);
      await reachTheWarning();

      await fakeClockUser().click(screen.getByRole('button', { name: /sign out now/i }));
      await warningSays(words);

      // Still there a while later, also where the BFF answered the failure itself (the 500, the
      // 429): that answer moved its clock, and the deadline with it, past the warning's two minutes.
      await advance(5_000);
      expect(within(screen.getByRole('alertdialog')).getByRole('alert')).toHaveTextContent(words);
      expect(screen.getByRole('button', { name: /sign out now/i })).toBeEnabled();
      expect(store.getState().auth.status).toBe('authenticated');
      expect(apiSlice.endpoints.getMe.select()(store.getState()).data?.user.email).toBe(
        MOCK_USER.email,
      );
    },
  );

  it('pressed again after a failure, it keeps the dialog while it waits, without the old words', async () => {
    const store = await boot();
    let attempts = 0;
    let secondAt = 0;
    server.use(
      http.post('*/bff/auth/logout', () => {
        attempts += 1;
        if (attempts === 1) {
          return problem({
            status: 429,
            errorCode: 'RATE_LIMITED',
            detail: 'Too many requests.',
            headers: { 'Retry-After': '10' },
          });
        }
        secondAt = Date.now();
        return never();
      }),
    );
    installFakeClock();
    renderApp(store);
    await reachTheWarning();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: /sign out now/i }));
    await warningSays(COPY.unavailable);

    await user.click(screen.getByRole('button', { name: /sign out now/i }));
    await waitFor(() => expect(secondAt).toBeGreaterThan(0));
    await advanceUntil(secondAt, 5_000);

    const dialog = screen.getByRole('alertdialog');
    hintRegion(COPY.slow, dialog);
    expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('no answer in 65 s says the service is unavailable, not the connection', async () => {
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

    await fakeClockUser().click(screen.getByRole('button', { name: /sign out now/i }));
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));
    await advanceUntil(sentAt, 65_100);

    await warningSays(COPY.unavailable);
    expect(screen.queryByText(CONNECTION)).not.toBeInTheDocument();
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('after a failed sign-out, "Stay signed in" keeps the session and the dialog closes', async () => {
    const store = await boot();
    server.use(http.post('*/bff/auth/logout', () => HttpResponse.error()));
    installFakeClock();
    renderApp(store);
    await reachTheWarning();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: /sign out now/i }));
    await warningSays(CONNECTION);

    await user.click(screen.getByRole('button', { name: /stay signed in/i }));

    await waitFor(() => expect(warning()).not.toBeInTheDocument());
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('after a failed sign-out, signing in again at the cap starts the new session and the dialog closes', async () => {
    const store = await bootAtTheCap();
    server.use(http.post('*/bff/auth/logout', () => HttpResponse.error()));
    installFakeClock();
    renderApp(store);
    await reachTheCapWarning();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: /sign out now/i }));
    await warningSays(CONNECTION);

    fireEvent.change(screen.getByLabelText(/enter your password/i), {
      target: { value: MOCK_PASSWORD },
    });
    await user.click(screen.getByRole('button', { name: /sign in again/i }));

    await waitFor(() => expect(warning()).not.toBeInTheDocument());
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('at the cap, a failed sign-out replaces the words of a failed sign-in instead of adding to them', async () => {
    const store = await bootAtTheCap();
    server.use(
      http.post('*/bff/auth/reauthenticate', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/reauthenticate' }),
      ),
      http.post('*/bff/auth/logout', () => HttpResponse.error()),
    );
    installFakeClock();
    renderApp(store);
    await reachTheCapWarning();
    const user = fakeClockUser();
    fireEvent.change(screen.getByLabelText(/enter your password/i), {
      target: { value: MOCK_PASSWORD },
    });
    await user.click(screen.getByRole('button', { name: /sign in again/i }));
    await warningSays(COPY.unavailable);

    await user.click(screen.getByRole('button', { name: /sign out now/i }));

    const alertTexts = () =>
      within(screen.getByRole('alertdialog'))
        .getAllByRole('alert')
        .map((alert) => alert.textContent);
    const signOutWords = `${COPY.signOutFailed} ${CONNECTION}`;
    await waitFor(() => expect(alertTexts()).toContain(signOutWords));
    expect(alertTexts()).toEqual([signOutWords]);
  });

  it('the words of a failed sign-out do not follow the visitor into their next session', async () => {
    const store = await boot();
    server.use(
      http.post('*/bff/auth/logout', () => HttpResponse.error()),
      http.get('*/api/accounts', () => problem({ status: 401, title: 'Unauthorized' })),
    );
    installFakeClock();
    renderApp(store);
    await reachTheWarning();
    await fakeClockUser().click(screen.getByRole('button', { name: /sign out now/i }));
    await warningSays(CONNECTION);

    // The session ends another way (a 401 on a read), and the bootstrap probe finds a new one.
    const seen: string[] = [];
    const unsubscribe = store.subscribe(() => {
      const next = store.getState().auth.status;
      if (seen[seen.length - 1] !== next) seen.push(next);
    });
    await act(async () => {
      await store
        .dispatch(apiSlice.endpoints.getAccounts.initiate(undefined, { subscribe: false }))
        .unwrap()
        .catch(() => undefined);
    });
    await waitFor(() => expect(seen).toEqual(['expired', 'authenticated']));
    unsubscribe();

    // Its deadline is minutes away: no warning, and no words from the session before.
    await advance(2_000);
    expect(warning()).not.toBeInTheDocument();
    expect(screen.queryByText(CONNECTION)).not.toBeInTheDocument();
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

    // Back up: Try again lets the visitor in. A plain click, so that the answer lands while the
    // test is waiting for it: the session it starts sets its countdown in an effect, and after a
    // `user.click` that render can fall between the click and the wait, outside both.
    down = false;
    fireEvent.click(screen.getByRole('button', { name: COPY.tryAgain }));
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

    // Its first word comes a second after every other wait's (`SESSION_CHECK_SLOW_AFTER_MS`).
    await advanceUntil(sentAt, 5_500);
    expect(screen.getByLabelText('Checking your session')).toBeInTheDocument();
    expect(screen.queryByText(COPY.slow)).toBeNull();
    await advanceUntil(sentAt, 6_000);
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

  it.each([
    ['a lost connection', () => HttpResponse.error()],
    [
      'a 500',
      () =>
        problem({
          status: 500,
          errorCode: 'INTERNAL_ERROR',
          detail: 'An unexpected error occurred. Please try again later.',
        }),
    ],
    [
      'an answer that cannot be read',
      () => new HttpResponse('<html></html>', { headers: { 'Content-Type': 'text/html' } }),
    ],
  ])('%s at boot is not a sign-out either', async (_what, answer) => {
    server.use(http.get('*/bff/auth/me', answer));
    installFakeClock();
    const { router } = renderApp(makeTestStore());
    await advance(15_000);

    const main = await screen.findByRole('main');
    expect(
      within(main).getByRole('heading', { level: 1, name: COPY.unavailableTitle }),
    ).toBeInTheDocument();
    expect(within(main).getByRole('alert')).toHaveTextContent(COPY.unavailable);
    expect(within(main).getByRole('button', { name: COPY.tryAgain })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/dashboard');
  });

  it('a body that fails its schema at boot is not a sign-out either', async () => {
    // RTK reports an answer its schema refused on console.error in development. That refusal is
    // this case, so the report is expected and checked here instead of failing the test.
    const report = vi.spyOn(console, 'error').mockImplementation(() => {});
    server.use(http.get('*/bff/auth/me', () => HttpResponse.json({ data: { nothing: true } })));
    installFakeClock();
    const { router } = renderApp(makeTestStore());
    await advance(15_000);

    const main = await screen.findByRole('main');
    expect(within(main).getByRole('alert')).toHaveTextContent(COPY.unavailable);
    expect(router.state.location.pathname).toBe('/dashboard');
    expect(
      report.mock.calls.some(([first]) => String(first).includes('the endpoint "getMe"')),
    ).toBe(true);
    report.mockRestore();
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

  it.each([
    [
      'not JSON (an ingress page)',
      () => serviceUnavailable({ via: 'bff', contentType: 'text/plain' }),
    ],
    ['empty', () => new HttpResponse(null, { status: 503 })],
  ])(
    'a 503 that neither server wrote, %s, does not move the expiry clock: the server never saw it',
    async (_what, answer) => {
      const store = await boot(15 * 60_000);
      server.use(http.get('*/api/accounts', answer));
      installFakeClock();
      await advance(30_000);
      const before = getLastServerActivity();
      const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
      const outcome = track(query.unwrap());
      await advance(15_000);

      await waitFor(() => expect(outcome.state).toBe('rejected'));
      expect(outcome.error).toMatchObject({ status: 503, errorCode: 'HTTP_503' });
      expect(getLastServerActivity()).toBe(before);
      query.unsubscribe();
    },
  );

  it("the BFF's own 503 still moves the expiry clock: the BFF saw the request", async () => {
    const store = await boot(15 * 60_000);
    server.use(
      http.get('*/api/accounts', () =>
        serviceUnavailable({ via: 'bff', instance: '/api/accounts' }),
      ),
    );
    installFakeClock();
    await advance(30_000);
    const before = getLastServerActivity() ?? 0;
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await advance(15_000);

    await waitFor(() => expect(outcome.state).toBe('rejected'));
    expect(outcome.error).toMatchObject({ status: 503, errorCode: 'SERVICE_UNAVAILABLE' });
    expect(getLastServerActivity()).toBeGreaterThan(before);
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

  it('at 0:00, activity that moves the deadline while the sign-out is on its way does not bring the dialog back', async () => {
    const store = await boot();
    let logoutAt = 0;
    server.use(
      http.get('*/bff/auth/session-status', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/session-status' }),
      ),
      http.post('*/bff/auth/logout', () => {
        logoutAt = Date.now();
        return never();
      }),
      // The BFF's own 503 counts as activity; a wait longer than the read's budget means no retry.
      http.get('*/api/accounts', () =>
        serviceUnavailable({ via: 'bff', instance: '/api/accounts', retryAfterSeconds: 500 }),
      ),
    );
    installFakeClock();
    renderApp(store);
    await advance(INACTIVITY_WINDOW_MS + 2_000);
    await advance(15_000);
    await waitFor(() => expect(logoutAt).toBeGreaterThan(0));
    expect(warning()).not.toBeInTheDocument();

    const before = getLastServerActivity() ?? 0;
    await act(async () => {
      await store
        .dispatch(apiSlice.endpoints.getAccounts.initiate(undefined, { subscribe: false }))
        .unwrap()
        .catch(() => undefined);
    });
    expect(getLastServerActivity()).toBeGreaterThan(before);
    await advance(2_000);

    // Outside the warning's two minutes, and nobody asked to sign out: no dialog.
    expect(warning()).not.toBeInTheDocument();
    await advanceUntil(logoutAt, 65_100);
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

  it('the step-up modal says it is slow, and with no answer in 65 s that the service is unavailable', async () => {
    let sentAt = 0;
    server.use(
      http.post('*/bff/auth/verify-pin', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    installFakeClock();
    const store = makeTestStore();
    renderWithProviders(<StepUpModal />, { store });
    void store.dispatch(apiSlice.endpoints.revealAccountNumber.initiate(mockState.accounts[0].id));
    await screen.findByText("Verify it's you");
    const user = fakeClockUser();
    await user.click(screen.getByLabelText('Digit 1 of 6'));
    await user.paste(TEST_PIN);
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 5_000);
    hintRegion(COPY.slow, screen.getByRole('alertdialog'));

    await advanceUntil(sentAt, 65_100);
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(COPY.unavailable));
    expect(screen.queryByText(/check your connection/i)).not.toBeInTheDocument();
  });

  it('signing in again says it is slow, and with no answer in 65 s that the service is unavailable', async () => {
    const store = await bootAtTheCap();
    let sentAt = 0;
    server.use(
      http.post('*/bff/auth/reauthenticate', () => {
        sentAt = Date.now();
        return never();
      }),
    );
    installFakeClock();
    renderApp(store);
    await reachTheCapWarning();

    fireEvent.change(screen.getByLabelText(/enter your password/i), {
      target: { value: MOCK_PASSWORD },
    });
    await fakeClockUser().click(screen.getByRole('button', { name: /sign in again/i }));
    await waitFor(() => expect(sentAt).toBeGreaterThan(0));

    await advanceUntil(sentAt, 5_000);
    hintRegion(COPY.slow, screen.getByRole('alertdialog'));

    await advanceUntil(sentAt, 65_100);
    await warningSays(COPY.unavailable);
    expect(screen.queryByText(CONNECTION)).not.toBeInTheDocument();
  });
});

describe('the expiry warning, for a keyboard and a screen reader', () => {
  it('opens on "Stay signed in", which comes first, so the key that presses a button keeps the session', async () => {
    const store = await boot();
    const asked = { me: 0, logout: 0 };
    server.use(
      http.get('*/bff/auth/me', () => {
        asked.me += 1;
        return undefined;
      }),
      http.post('*/bff/auth/logout', () => {
        asked.logout += 1;
        return undefined;
      }),
    );
    installFakeClock();
    renderApp(store);
    await reachTheWarning();

    const dialog = screen.getByRole('alertdialog');
    const stay = within(dialog).getByRole('button', { name: /stay signed in/i });
    expect(within(dialog).getAllByRole('button')[0]).toBe(stay);
    await waitFor(() => expect(document.activeElement).toBe(stay));

    const before = { ...asked };
    await fakeClockUser().keyboard(' ');

    await waitFor(() => expect(asked.me).toBe(before.me + 1));
    await advance(1_000);
    expect(asked.me).toBe(before.me + 1);
    expect(asked.logout).toBe(before.logout);
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('is described by its sentence and its countdown, and by nothing it may show later', async () => {
    const store = await boot();
    server.use(http.post('*/bff/auth/logout', () => HttpResponse.error()));
    installFakeClock();
    renderApp(store);
    await reachTheWarning();
    const dialog = screen.getByRole('alertdialog');
    const description =
      /^You have been inactive for a while\. You will be signed out in \d:\d\d\.$/;
    expect(dialog).toHaveAccessibleDescription(description);

    // A failed sign-out's alert and a wait's words are inside the dialog, never inside its
    // description: they speak for themselves.
    await fakeClockUser().click(screen.getByRole('button', { name: /sign out now/i }));
    await warningSays(CONNECTION);
    expect(dialog).toHaveAccessibleDescription(description);
  });

  it('at the cap, is described by its own sentence and countdown, not by the password field', async () => {
    const store = await bootAtTheCap();
    installFakeClock();
    renderApp(store);
    await reachTheCapWarning();

    expect(screen.getByRole('alertdialog')).toHaveAccessibleDescription(
      /^This session has reached its maximum length\. For your security it ends on a fixed schedule, whether or not you are using it\. You will be signed out in \d:\d\d\.$/,
    );
  });

  it('after a failed "Sign out now", focus is back on it', async () => {
    const store = await boot();
    server.use(http.post('*/bff/auth/logout', () => HttpResponse.error()));
    installFakeClock();
    // While it is pending the button is disabled, and a browser then hands focus to the page.
    const stopFixup = emulateFocusFixup();
    try {
      renderApp(store);
      await reachTheWarning();

      await fakeClockUser().click(screen.getByRole('button', { name: /sign out now/i }));
      await warningSays(CONNECTION);

      await waitFor(() =>
        expect(document.activeElement).toBe(screen.getByRole('button', { name: /sign out now/i })),
      );
    } finally {
      stopFixup();
    }
  });
});

describe('a sign-out that fails says the visitor is still signed in', () => {
  it.each([
    [
      'the service is down',
      () => serviceUnavailable({ via: 'bff', instance: '/bff/auth/logout' }),
      COPY.unavailable,
    ],
    ['the connection failed', () => HttpResponse.error(), CONNECTION],
  ])('"Sign out now", when %s: that sentence first, then why', async (_what, answer, why) => {
    const store = await boot();
    server.use(http.post('*/bff/auth/logout', answer));
    installFakeClock();
    renderApp(store);
    await reachTheWarning();

    await fakeClockUser().click(screen.getByRole('button', { name: /sign out now/i }));

    await waitFor(() =>
      expect(within(screen.getByRole('alertdialog')).getByRole('alert').textContent).toBe(
        `${COPY.signOutFailed} ${why}`,
      ),
    );
  });

  /** A signed-in page in the app's shell, with the toasts' outlet, and the sign-in page. */
  function renderShell(store: TestStore, page = <div>PAGE</div>) {
    return renderWithProviders(
      <>
        <AppToaster />
        <Routes>
          <Route path="/login" element={<div>LOGIN PAGE</div>} />
          <Route path="/dashboard" element={<ProtectedShell>{page}</ProtectedShell>} />
        </Routes>
      </>,
      { store, routerEntries: ['/dashboard'] },
    );
  }

  /** The toast's body that holds `start`, whole. */
  async function toastBodyStartingWith(start: string): Promise<string> {
    const words = await screen.findByText((content) => content.startsWith(start));
    return words.textContent ?? '';
  }

  it("the shell's Sign out, on an outage: that sentence before the outage's", async () => {
    const store = await boot(15 * 60_000);
    server.use(
      http.post('*/bff/auth/logout', () =>
        serviceUnavailable({ via: 'bff', instance: '/bff/auth/logout' }),
      ),
    );
    renderShell(store);

    await userEvent.click(screen.getByRole('button', { name: /sign out/i }));

    const body = await toastBodyStartingWith(COPY.signOutFailed);
    expect(body.startsWith(`${COPY.signOutFailed} ${COPY.unavailable}`)).toBe(true);
    expect(store.getState().auth.status).toBe('authenticated');
    expect(screen.queryByText('LOGIN PAGE')).not.toBeInTheDocument();
  });

  it("Settings' Log out, when the connection failed: that sentence before the connection's", async () => {
    const store = await boot(15 * 60_000);
    server.use(http.post('*/bff/auth/logout', () => HttpResponse.error()));
    renderShell(store, <SettingsPage />);
    await screen.findByRole('heading', { level: 1, name: 'Settings' });

    await userEvent.click(screen.getByRole('button', { name: 'Log out' }));

    expect(await toastBodyStartingWith(COPY.signOutFailed)).toBe(
      `${COPY.signOutFailed} ${CONNECTION}`,
    );
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it("a 401 from the shell's Sign out says nothing of the kind: that session is gone", async () => {
    const store = await boot(15 * 60_000);
    server.use(
      http.post('*/bff/auth/logout', () => {
        mockState.session = null;
        return bffProblem({
          status: 401,
          title: 'Unauthorized',
          detail: 'Session expired or invalid',
        });
      }),
    );
    renderShell(store);

    await userEvent.click(screen.getByRole('button', { name: /sign out/i }));

    await waitFor(() => expect(store.getState().auth.status).not.toBe('authenticated'));
    await act(async () => {});
    expect(
      screen.queryByText((content) => content.includes(COPY.signOutFailed)),
    ).not.toBeInTheDocument();
  });

  it("a 401 from Settings' Log out says nothing of the kind either", async () => {
    const store = await boot(15 * 60_000);
    let answered = false;
    server.use(
      http.post('*/bff/auth/logout', () => {
        mockState.session = null;
        answered = true;
        return bffProblem({
          status: 401,
          title: 'Unauthorized',
          detail: 'Session expired or invalid',
        });
      }),
    );
    renderShell(store, <SettingsPage />);
    await screen.findByRole('heading', { level: 1, name: 'Settings' });

    await userEvent.click(screen.getByRole('button', { name: 'Log out' }));

    await waitFor(() => expect(answered).toBe(true));
    await waitFor(() => expect(store.getState().auth.status).not.toBe('authenticated'));
    await act(async () => {});
    expect(screen.queryAllByText((content) => content.includes(COPY.signOutFailed))).toEqual([]);
  });
});

describe('the outage page at start-up', () => {
  it('says the outage into the alert that was there, empty, while the session was checked', async () => {
    // No answer: the check gives up at 65 s, and that is the outage.
    server.use(http.get('*/bff/auth/me', never));
    installFakeClock();
    renderApp(makeTestStore());
    await screen.findByLabelText('Checking your session');
    const slot = alertSlot();
    expect(slot).toBeEmptyDOMElement();

    await advance(65_100);

    await screen.findByRole('heading', { level: 1, name: COPY.unavailableTitle });
    expect(alertSlot()).toBe(slot);
    expect(slot).toHaveTextContent(COPY.unavailable);
  });

  it('every "Try again" that fails again puts a new bar into the alert, even inside one frame', async () => {
    /*
      A 500 is not retried and the mock answers it at once, so the check's start and its failure can
      reach the page in the same frame: unavailable to unavailable, with no check drawn between. A
      bar that stayed the same element would say nothing the second time.
    */
    let calls = 0;
    server.use(
      http.get('*/bff/auth/me', () => {
        calls += 1;
        return problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Something broke.' });
      }),
    );
    renderApp(makeTestStore());
    await screen.findByRole('heading', { level: 1, name: COPY.unavailableTitle });
    const barOf = () => screen.getByText(COPY.unavailable).closest<HTMLElement>('[role="group"]');

    for (let press = 1; press <= 2; press += 1) {
      const shown = barOf();
      expect(shown?.closest('[role="alert"]')).toBe(alertSlot());
      const before = calls;
      await userEvent.click(screen.getByRole('button', { name: COPY.tryAgain }));
      await waitFor(() => expect(calls).toBe(before + 1));

      await waitFor(() => {
        expect(barOf()).not.toBeNull();
        expect(barOf()).not.toBe(shown);
      });
    }
  });

  it('is titled "Temporarily unavailable", and gives the page its title back once the service answers', async () => {
    let down = true;
    server.use(
      http.get('*/bff/auth/me', () =>
        down ? serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' }) : undefined,
      ),
    );
    document.title = 'AzureBank';
    installFakeClock();
    renderTitledApp(['/dashboard']);
    await advance(15_000);
    await screen.findByRole('heading', { level: 1, name: COPY.unavailableTitle });

    expect(document.title).toBe('Temporarily unavailable · AzureBank');

    down = false;
    fireEvent.click(screen.getByRole('button', { name: COPY.tryAgain }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
    expect(document.title).toBe('Home · AzureBank');
  });

  it('stays titled "Temporarily unavailable" when Back changes the route under it, and says so', async () => {
    /*
      Every guarded route renders the same guard in the same place, so Back from one to another
      keeps the outage page on screen. The route's own title and "<route> page loaded" would name a
      page that is not there.
    */
    server.use(
      http.get('*/bff/auth/me', () => serviceUnavailable({ via: 'bff', instance: '/bff/auth/me' })),
    );
    document.title = 'AzureBank';
    installFakeClock();
    const router = renderTitledApp(['/dashboard', '/accounts']);
    await advance(15_000);
    await screen.findByRole('heading', { level: 1, name: COPY.unavailableTitle });
    expect(router.state.location.pathname).toBe('/accounts');
    const region = document.querySelector<HTMLElement>('[data-route-announcer]') as HTMLElement;
    const said: string[] = [];
    const observer = new MutationObserver(() => said.push(region.textContent ?? ''));
    observer.observe(region, { childList: true, characterData: true, subtree: true });

    await act(async () => {
      await router.navigate(-1);
    });
    await advance(1_000);
    observer.disconnect();

    expect(router.state.location.pathname).toBe('/dashboard');
    expect(screen.getByRole('heading', { level: 1, name: COPY.unavailableTitle })).toBeVisible();
    expect(document.title).toBe('Temporarily unavailable · AzureBank');
    expect(said).not.toContain('Home page loaded');
    expect(said).toContain('Temporarily unavailable page loaded');
  });
});

/**
 * The app's route tree in small: the route announcer above two titled, guarded routes, as
 * `App.tsx` has it, so that the page's title and the announcement of a route change are the
 * app's own. Opens on the last of `entries`.
 */
function renderTitledApp(entries: string[]) {
  const router = createMemoryRouter(
    createRoutesFromElements(
      <Route element={<RouteAnnouncer />}>
        <Route
          path="/dashboard"
          handle={{ title: 'Home' }}
          element={
            <ProtectedRoute>
              <div>DASHBOARD</div>
            </ProtectedRoute>
          }
        />
        <Route
          path="/accounts"
          handle={{ title: 'Accounts' }}
          element={
            <ProtectedRoute>
              <div>ACCOUNTS</div>
            </ProtectedRoute>
          }
        />
      </Route>,
    ),
    { initialEntries: entries, initialIndex: entries.length - 1 },
  );
  render(
    <ThemeProvider>
      <Provider store={makeTestStore()}>
        <AuthBootstrap />
        <RouterProvider router={router} />
      </Provider>
    </ThemeProvider>,
  );
  return router;
}
