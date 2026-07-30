import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { MOCK_PASSWORD, mockState, seedMockSession } from '../../mocks/state';
import { makeTestStore, renderWithProviders, type TestStore } from '../../test/renderWithProviders';
import { apiSlice } from '../api/apiSlice';
import { AuthBootstrap } from './AuthBootstrap';
import { SessionExpiryWarning } from './SessionExpiryWarning';

/**
 * The dialog had no test at all, which is how it shipped promising a sign-out it could not perform
 * and a countdown that was a constant. Every test here would have failed against that version.
 *
 * Time is faked with `shouldAdvanceTime` so MSW responses still resolve — freezing the clock
 * outright deadlocks any test that awaits a request, which is most of them.
 */

const INACTIVITY_WINDOW_MS = 3 * 60_000;
const ABSOLUTE_WINDOW_MS = 60 * 60_000;

/**
 * Authenticate and let the client LEARN the policy from /bff/auth/me, the way the app does.
 * Runs on real timers: the deadline is anchored to the response, so faking the clock first would
 * anchor it to a fake instant and make every later assertion meaningless.
 */
async function boot(inactivityWindowMs = INACTIVITY_WINDOW_MS): Promise<TestStore> {
  seedMockSession();
  mockState.sessionInactivityWindowMs = inactivityWindowMs;
  mockState.sessionAbsoluteWindowMs = ABSOLUTE_WINDOW_MS;
  const store = makeTestStore();
  await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
  return store;
}

const dialog = () => screen.queryByRole('alertdialog');
const countdownText = () => screen.getByRole('alertdialog').textContent ?? '';

describe('SessionExpiryWarning', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('says nothing while the deadline is far away', async () => {
    const store = await boot();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderWithProviders(<SessionExpiryWarning />, { store });

    await vi.advanceTimersByTimeAsync(30_000);

    expect(dialog()).not.toBeInTheDocument();
  });

  it('counts down, and the number actually changes', async () => {
    const store = await boot();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderWithProviders(<SessionExpiryWarning />, { store });

    // Into the 2-minute warning lead.
    await vi.advanceTimersByTimeAsync(61_000);
    await waitFor(() => expect(dialog()).toBeInTheDocument());
    const first = countdownText();

    await vi.advanceTimersByTimeAsync(10_000);

    // The whole point. The previous implementation rendered the constant "about 2 minutes" here
    // and would still be rendering it half an hour later.
    await waitFor(() => expect(countdownText()).not.toBe(first));
    expect(first).toMatch(/1:5\d/);
    expect(countdownText()).toMatch(/1:4\d/);
  });

  it('signs out when the countdown reaches zero', async () => {
    const store = await boot();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderWithProviders(<SessionExpiryWarning />, { store });

    await vi.advanceTimersByTimeAsync(INACTIVITY_WINDOW_MS + 2_000);

    // Not merely "the dialog changed" — the session state actually ends. Nothing in the old
    // frontend could reach this, because the only route to it was a 401 on a real request.
    await waitFor(() => expect(store.getState().auth.status).not.toBe('authenticated'));
  });

  it('does not sign out a session another tab kept alive', async () => {
    const store = await boot();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderWithProviders(<SessionExpiryWarning />, { store });

    await vi.advanceTimersByTimeAsync(61_000);
    await waitFor(() => expect(dialog()).toBeInTheDocument());

    // Another tab makes a request: the SERVER's clock slides, this tab's does not.
    mockState.sessionLastActivity = Date.now();
    // Past THIS tab's original zero-crossing but short of the server's new one, which is the only
    // window in which the disagreement is observable.
    await vi.advanceTimersByTimeAsync(2 * 60_000 + 5_000);

    // The zero-crossing probe finds a live session and resyncs instead of ending it. Signing
    // someone out of a session the server still honours is worse than warning them late.
    //
    // The dialog stays up, and that is correct rather than a miss: the recovered deadline is about
    // 55 seconds away, still inside the two-minute lead. What proves the resync happened is that
    // the countdown went back UP instead of sitting at zero.
    await waitFor(() => expect(countdownText()).not.toMatch(/0:0[012]\b/));
    expect(dialog()).toBeInTheDocument();
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('an ordinary API request slides the SERVER clock, not just the client mirror', async () => {
    const store = await boot();
    const probe = () =>
      store
        .dispatch(apiSlice.endpoints.getSessionStatus.initiate(undefined, { forceRefetch: true }))
        .unwrap();

    const before = await probe();
    await new Promise((resolve) => setTimeout(resolve, 5));
    // An ordinary read, not a keep-alive. The real BFF slides LastActivity on every cookie-bearing
    // request except the status probe, so this must move the deadline. Until the mock modelled that,
    // only /bff/auth/me counted and the mock clock ran FAST against the server: someone actively
    // using the app would be warned and then signed out while the server considered them alive.
    await store.dispatch(apiSlice.endpoints.getAccounts.initiate()).unwrap();
    const after = await probe();

    // Asserted against the PROBE, not against the dialog. The dialog would close either way — the
    // client mirror slides on any fulfilled api/ action regardless of what the server recorded — so
    // watching it would have tested the client middleware and quietly proved nothing about the mock.
    expect(Date.parse(after.inactivityExpiresAt!)).toBeGreaterThan(
      Date.parse(before.inactivityExpiresAt!),
    );
  });

  it('"Sign out now" ends the session without waiting for the deadline', async () => {
    const store = await boot();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderWithProviders(<SessionExpiryWarning />, { store });

    await vi.advanceTimersByTimeAsync(61_000);
    await waitFor(() => expect(dialog()).toBeInTheDocument());

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await user.click(screen.getByRole('button', { name: /sign out now/i }));

    await waitFor(() => expect(store.getState().auth.status).toBe('anonymous'));
  });

  it('leaves the warning up when the keep-alive never reaches the server', async () => {
    const store = await boot();
    // Offline for the keep-alive only. problemBaseQuery normalises this to status 'NETWORK',
    // which sessionMiddleware must NOT count as activity — the server's clock did not move.
    server.use(http.get('*/bff/auth/me', () => HttpResponse.error()));

    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderWithProviders(<SessionExpiryWarning />, { store });

    await vi.advanceTimersByTimeAsync(61_000);
    await waitFor(() => expect(dialog()).toBeInTheDocument());

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await user.click(screen.getByRole('button', { name: /stay signed in/i }));
    await vi.advanceTimersByTimeAsync(2_000);

    // The old version called setWarningDue(false) on click, so a failed keep-alive looked like a
    // successful one and quietly reset the exposure clock. The dialog stays now, which is true.
    expect(dialog()).toBeInTheDocument();
  });

  /*
    U6.7 — the ABSOLUTE branch.

    `boot()` above leaves inactivity (3 min) binding and the cap (60 min) far away, so every test so
    far exercised the inactivity branch. These flip it: a cap 2m30 away with inactivity half an hour
    out, which is the only configuration in which `isAbsoluteDeadline()` is true.

    `AuthBootstrap` is rendered alongside on purpose. Its live `useGetMeQuery` subscription is what
    turns the mutation's tag invalidation into a refetch, and a fulfilled `getMe` is the ONE action
    `sessionMiddleware` learns the policy from — so it is also what closes this dialog. Testing the
    warning without it would prove the request was sent and nothing about the user getting their
    session back.
  */
  const ABSOLUTE_SOON_MS = 150_000;

  async function bootAbsolute(): Promise<TestStore> {
    seedMockSession();
    mockState.sessionInactivityWindowMs = 30 * 60_000;
    mockState.sessionAbsoluteWindowMs = ABSOLUTE_SOON_MS;
    const store = makeTestStore();
    await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
    return store;
  }

  function renderWarning(store: TestStore) {
    return renderWithProviders(
      <>
        <AuthBootstrap />
        <SessionExpiryWarning />
      </>,
      { store },
    );
  }

  /** Reach the warning window. The probe does not mark activity, so reading is safe here. */
  async function reachTheWarning(store: TestStore) {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderWarning(store);
    await vi.advanceTimersByTimeAsync(35_000);
    await waitFor(() => expect(dialog()).toBeInTheDocument());
  }

  const password = () => screen.getByLabelText(/enter your password/i);
  const absoluteExpiry = async (store: TestStore) =>
    Date.parse(
      (
        await store
          .dispatch(apiSlice.endpoints.getSessionStatus.initiate(undefined, { forceRefetch: true }))
          .unwrap()
      ).absoluteExpiresAt!,
    );

  it('asks for the password at the cap, and drops the keep-alive that could not work there', async () => {
    const store = await bootAbsolute();
    await reachTheWarning(store);

    expect(password()).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /sign in again/i })).toBeInTheDocument();
    // The defect this closes. "Stay signed in" fires getMe, which slides the INACTIVITY deadline;
    // the cap is sessionCreatedAt + window and never moves. On this branch the button did nothing,
    // and the dialog correctly refused to pretend otherwise, so it just sat there.
    expect(screen.queryByRole('button', { name: /stay signed in/i })).not.toBeInTheDocument();
  });

  it('negative control: the inactivity branch still offers the keep-alive and no password', async () => {
    // Without this, the test above would pass against a dialog that had simply lost "Stay signed in"
    // everywhere — which would break the branch where the keep-alive genuinely works.
    const store = await boot();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderWarning(store);
    await vi.advanceTimersByTimeAsync(61_000);
    await waitFor(() => expect(dialog()).toBeInTheDocument());

    expect(screen.getByRole('button', { name: /stay signed in/i })).toBeInTheDocument();
    expect(screen.queryByLabelText(/enter your password/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /sign in again/i })).not.toBeInTheDocument();
  });

  it('the right password starts a NEW session — the cap moves and the dialog closes itself', async () => {
    const store = await bootAbsolute();
    const before = await absoluteExpiry(store);
    await reachTheWarning(store);

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    // fireEvent, not user.type: userEvent truncates a Fluent Input.
    fireEvent.change(password(), { target: { value: MOCK_PASSWORD } });
    await user.click(screen.getByRole('button', { name: /sign in again/i }));

    // The property, not the pixel: the cap is a NEW window, so a session that could not be extended
    // was replaced. A handler that only slid LastActivity would leave this number exactly equal.
    await waitFor(async () => expect(await absoluteExpiry(store)).toBeGreaterThan(before));
    // And the dialog closes because the deadline genuinely moved — nothing dismissed it.
    await waitFor(() => expect(dialog()).not.toBeInTheDocument());
    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('a wrong password stays a wrong password — it must not end the session', async () => {
    // The one that matters most. An endpoint that revoked before verifying would turn a typo into
    // the exact outcome the user was trying to avoid, which is worse than the button that did
    // nothing. The 401 carries INVALID_CREDENTIALS, which D3 exempts from the global sign-out.
    const store = await bootAbsolute();
    await reachTheWarning(store);

    // Recorded rather than sampled at the end, and that distinction is the test. Sampling
    // `auth.status` afterwards proves nothing: a bare 401 DOES dispatch sessionExpired, but
    // `AuthBootstrap`'s live probe immediately re-establishes the still-valid cookie, so the final
    // value reads 'authenticated' either way. What the exemption actually buys is never passing
    // THROUGH 'expired' — because that transition also runs resetApiState(), throwing away every
    // cached balance and account under whatever the user had open. Measured: with the errorCode
    // removed from the response, 'expired' appears here.
    const seen: string[] = [store.getState().auth.status];
    const unsubscribe = store.subscribe(() => {
      const next = store.getState().auth.status;
      if (seen[seen.length - 1] !== next) seen.push(next);
    });

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    fireEvent.change(password(), { target: { value: 'Wrong1234!' } });
    await user.click(screen.getByRole('button', { name: /sign in again/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/didn't match/i);
    unsubscribe();

    expect(dialog()).toBeInTheDocument();
    expect(store.getState().auth.status).toBe('authenticated');
    expect(seen).toEqual(['authenticated']);
  });

  it('an offline attempt leaves the warning up and says so', async () => {
    const store = await bootAbsolute();
    server.use(http.post('*/bff/auth/reauthenticate', () => HttpResponse.error()));
    await reachTheWarning(store);

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    fireEvent.change(password(), { target: { value: MOCK_PASSWORD } });
    await user.click(screen.getByRole('button', { name: /sign in again/i }));

    // Same rule as the keep-alive: a request that never reached the server must not look like one
    // that did. The dialog stays and the countdown keeps running.
    expect(await screen.findByRole('alert')).toHaveTextContent(/couldn't reach the server/i);
    expect(dialog()).toBeInTheDocument();
  });

  it('keeps the submit named while the request is in flight', async () => {
    const store = await bootAbsolute();
    server.use(http.post('*/bff/auth/reauthenticate', () => new Promise<Response>(() => {})));
    await reachTheWarning(store);

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    fireEvent.change(password(), { target: { value: MOCK_PASSWORD } });
    await user.click(screen.getByRole('button', { name: /sign in again/i }));

    // The spinner replaces the label, so without an explicit name the control vanishes from the
    // accessibility tree for exactly as long as it is busy.
    await waitFor(() => expect(screen.getByRole('button', { name: /signing in/i })).toBeDisabled());
  });

  it('a successful re-auth is not undone by a refetch that never lands', async () => {
    // The sharpest consequence of the cap becoming movable. The re-auth SUCCEEDS, but the /me that
    // would teach the client the new window fails, so the countdown keeps running toward the OLD
    // cap. At the crossing, the confirming probe can see the new cap — and until `syncFromProbe`
    // read it, it refused to look and signed out a user who had just proved their password.
    const store = await bootAbsolute();
    await reachTheWarning(store);
    server.use(http.get('*/bff/auth/me', () => HttpResponse.error()));

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    fireEvent.change(password(), { target: { value: MOCK_PASSWORD } });
    await user.click(screen.getByRole('button', { name: /sign in again/i }));

    // Past the deadline this tab still believes in.
    await vi.advanceTimersByTimeAsync(130_000);

    expect(store.getState().auth.status).toBe('authenticated');
  });

  it('the new session does not inherit the old one’s PIN elevation', async () => {
    const store = await bootAbsolute();
    mockState.authLevel = 2;
    await reachTheWarning(store);

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    fireEvent.change(password(), { target: { value: MOCK_PASSWORD } });
    await user.click(screen.getByRole('button', { name: /sign in again/i }));

    // A money-grade permission was proved to the session that just ended. Carrying it across would
    // let the cap quietly renew it — so the next transfer must ask for the PIN again.
    await waitFor(async () => {
      const probe = await store
        .dispatch(apiSlice.endpoints.getSessionStatus.initiate(undefined, { forceRefetch: true }))
        .unwrap();
      expect(probe.authLevel).toBe(1);
    });
  });
});
