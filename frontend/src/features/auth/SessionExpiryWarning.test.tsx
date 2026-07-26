import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { mockState, seedMockSession } from '../../mocks/state';
import { makeTestStore, renderWithProviders, type TestStore } from '../../test/renderWithProviders';
import { apiSlice } from '../api/apiSlice';
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
});
