import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { MOCK_PASSWORD, mockState, seedMockDemoCopy, seedMockSession } from '../../mocks/state';
import { enableDemoMode, rememberDemoCopy } from '../../test/demoMode';
import { emulateFocusFixup } from '../../test/outage';
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

/**
 * Advance the fake clock INSIDE `act()`.
 *
 * The component ticks on `window.setInterval(tick, 1_000)`, so a bare
 * `vi.advanceTimersByTimeAsync(61_000)` fires sixty-one `setRemainingMs` calls — each re-rendering
 * the dialog surface and title — with React's act environment active and no act scope around them.
 * That is the entire source of the warnings this file used to emit: 3,055 of the 3,074 in the
 * suite, and invisible because vitest's default reporter prints console output only for FAILING
 * tests.
 *
 * `waitFor` and `userEvent` need no such wrapper — Testing Library's async wrapper already suspends
 * the act environment for their duration. Only the explicit advances were unguarded.
 */
const advance = (ms: number) => act(async () => void (await vi.advanceTimersByTimeAsync(ms)));

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

    await advance(30_000);

    expect(dialog()).not.toBeInTheDocument();
  });

  it('counts down, and the number actually changes', async () => {
    const store = await boot();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderWithProviders(<SessionExpiryWarning />, { store });

    // Into the 2-minute warning lead.
    await advance(61_000);
    await waitFor(() => expect(dialog()).toBeInTheDocument());
    const first = countdownText();

    await advance(10_000);

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

    await advance(INACTIVITY_WINDOW_MS + 2_000);

    // Not merely "the dialog changed" — the session state actually ends. Nothing in the old
    // frontend could reach this, because the only route to it was a 401 on a real request.
    await waitFor(() => expect(store.getState().auth.status).not.toBe('authenticated'));
  });

  it('does not sign out a session another tab kept alive', async () => {
    const store = await boot();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    renderWithProviders(<SessionExpiryWarning />, { store });

    await advance(61_000);
    await waitFor(() => expect(dialog()).toBeInTheDocument());

    // Another tab makes a request: the SERVER's clock slides, this tab's does not.
    mockState.sessionLastActivity = Date.now();
    // Past THIS tab's original zero-crossing but short of the server's new one, which is the only
    // window in which the disagreement is observable.
    await advance(2 * 60_000 + 5_000);

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

    await advance(61_000);
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

    await advance(61_000);
    await waitFor(() => expect(dialog()).toBeInTheDocument());

    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await user.click(screen.getByRole('button', { name: /stay signed in/i }));
    await advance(2_000);

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
    await advance(35_000);
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
    await advance(61_000);
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
    await advance(130_000);

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

  /*
    On the demo, at the cap.

    Nobody chose a demo copy's password: the claim drew it, and the browser that claimed the copy
    keeps it (src/features/demo/demoCopyStorage.ts). So the copy's owner, on that browser, is asked
    for nothing: one button sends the kept password. Anyone else gets the password field of the
    tests above: a page without the demo's tag, a browser that keeps no copy, a browser that keeps
    another copy than the one that is signed in.

    Every test starts signed in to the first copy of the mock's pool (src/mocks/state.ts), claimed
    there. The two passwords and the second address typed out below are the pool's: fixtures no
    server knows. The words are typed out too, and not imported from the product, so a test fails
    the day the words on screen are no longer these.
  */
  describe('at the cap, on the demo', () => {
    const KEY = 'azurebank.demoCopy';
    const REAUTH = '*/bff/auth/reauthenticate';
    const FIRST_PASSWORD = 'Xk7p-Rm3w-Hn8d-Tq5v';
    const SECOND_COPY = 'demo-4h9d2s7f1g6j3k8a@azurebank.example';
    const SECOND_PASSWORD = 'Fb4t-Wy9c-Kz2g-Ne6s';
    const WORDS = {
      field: 'Enter your password to continue',
      stay: 'Stay signed in',
      signInAgain: 'Sign in again',
      signOut: 'Sign out now',
      ended: 'This demo copy has ended. Sign out to get a new one.',
      tooManyAttempts: 'Too many attempts just now. Wait a moment and try again.',
    } as const;
    /** What the dialog is operated with, top to bottom, for the copy's owner and for anyone else. */
    const THE_OWNERS = [`button: ${WORDS.stay}`, `button: ${WORDS.signOut}`];
    const ANYONE_ELSES = [
      `field: ${WORDS.field}`,
      `button: ${WORDS.signOut}`,
      `button: ${WORDS.signInAgain}`,
    ];

    type KeptCopy = Parameters<typeof rememberDemoCopy>[0];

    /**
     * `bootAbsolute`, signed in to the first copy of the mock's pool.
     *
     * `kept` is what the browser keeps. Left out, it is that copy as its claim left it; an object
     * changes members of it; `null` is a browser that keeps nothing. `demo: false` leaves the tag
     * off the page.
     */
    async function bootOnACopy({
      demo = true,
      kept,
    }: { demo?: boolean; kept?: Partial<KeptCopy> | null } = {}) {
      if (demo) enableDemoMode();
      const claimed = seedMockDemoCopy();
      if (kept !== null) rememberDemoCopy({ ...claimed.copy, ...kept });
      mockState.sessionInactivityWindowMs = 30 * 60_000;
      mockState.sessionAbsoluteWindowMs = ABSOLUTE_SOON_MS;
      const store = makeTestStore();
      await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
      return { store, claimed };
    }

    /** A control as one line: a field by its label, a button by its words. */
    const asLine = (control: Element | null) =>
      control instanceof HTMLInputElement
        ? `field: ${control.labels?.[0]?.textContent ?? ''}`
        : control instanceof HTMLButtonElement
          ? `button: ${control.textContent}`
          : null;

    /** Every field and button of the open dialog, in the order of the page. `null`: no dialog. */
    const offered = () => {
      const open = dialog();
      return open ? Array.from(open.querySelectorAll('input, button')).map(asLine) : null;
    };

    /** Where focus is: on that field or button, or `null` for anything else. */
    const focused = () => asLine(document.activeElement);

    /** The owner's button, when the dialog draws it. None is `[]`, not an error. */
    const stayButtons = () =>
      within(screen.getByRole('alertdialog')).queryAllByRole<HTMLButtonElement>('button', {
        name: WORDS.stay,
      });

    /** What each alert on the page says. */
    const alerts = () => screen.queryAllByRole('alert').map((alert) => alert.textContent);

    /** The body of every re-authentication sent from here on. The mock still answers each. */
    function reauthsSent(): unknown[] {
      const bodies: unknown[] = [];
      server.use(
        http.post(REAUTH, async ({ request }) => {
          bodies.push(await request.clone().json());
          // No answer from here, so the mock's own handler gives it.
          return undefined;
        }),
      );
      return bodies;
    }

    /** Every re-authentication the store has sent has its answer. */
    const settled = (store: TestStore) =>
      act(async () => {
        await Promise.all(store.dispatch(apiSlice.util.getRunningMutationsThunk()));
      });

    /** Every sign-in state the store passes through from here on, starting with the one it is in. */
    function statusesSeen(store: TestStore): string[] {
      const seen: string[] = [store.getState().auth.status];
      store.subscribe(() => {
        const next = store.getState().auth.status;
        if (seen[seen.length - 1] !== next) seen.push(next);
      });
      return seen;
    }

    it("demo, the copy's owner: one button in place of the password field", async () => {
      const { store } = await bootOnACopy();
      await reachTheWarning(store);

      // No field and no "Sign in again"; "Stay signed in" first, with focus, and "Sign out now".
      await waitFor(() =>
        expect({ offered: offered(), focused: focused() }).toStrictEqual({
          offered: THE_OWNERS,
          focused: THE_OWNERS[0],
        }),
      );
      // The button is found by its role and name too, once, and it is the form's submit: Enter
      // and Space press it where it stands.
      expect(stayButtons().map((button) => button.type)).toStrictEqual(['submit']);
      // The sentence above it is the cap's own, as for everyone else.
      expect(screen.getByRole('alertdialog')).toHaveAccessibleDescription(
        /^This session has reached its maximum length\. For your security it ends on a fixed schedule, whether or not you are using it\. You will be signed out in \d:\d\d\.$/,
      );
    });

    it("it sends the stored password, and the dialog closes because the session's end moved", async () => {
      const { store } = await bootOnACopy();
      const before = await absoluteExpiry(store);
      const sent = reauthsSent();
      await reachTheWarning(store);
      await waitFor(() =>
        expect({ offered: offered(), focused: focused() }).toStrictEqual({
          offered: THE_OWNERS,
          focused: THE_OWNERS[0],
        }),
      );

      // From the keyboard, with focus where the dialog put it.
      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await user.keyboard('{Enter}');

      // The password alone: the BFF takes the address from the session.
      await waitFor(() => expect(sent).toStrictEqual([{ password: FIRST_PASSWORD }]));
      // As for a typed password: the cap is a new one, and that is what closes the dialog.
      await waitFor(async () => expect(await absoluteExpiry(store)).toBeGreaterThan(before));
      await waitFor(() => expect(dialog()).not.toBeInTheDocument());
      expect(store.getState().auth.status).toBe('authenticated');
      expect(sent).toHaveLength(1);
    });

    it('while it waits the button keeps its name', async () => {
      const { store } = await bootOnACopy();
      server.use(http.post(REAUTH, () => new Promise<Response>(() => {})));
      await reachTheWarning(store);
      expect(offered()).toStrictEqual(THE_OWNERS);

      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await user.click(stayButtons()[0]);

      // Still found by its name, so its words are still its name; and it says that it is busy.
      await waitFor(() =>
        expect({
          stay: stayButtons().map((button) => ({
            disabled: button.disabled,
            spinners: within(button).queryAllByRole('progressbar').length,
          })),
          signingIn: screen.queryAllByRole('button', { name: /signing in/i }).length,
          offered: offered(),
        }).toStrictEqual({
          stay: [{ disabled: true, spinners: 1 }],
          signingIn: 0,
          offered: THE_OWNERS,
        }),
      );
    });

    it('a copy another tab replaced while the dialog is open: the password field at the next tick', async () => {
      const { store, claimed } = await bootOnACopy();
      const sent = reauthsSent();
      await reachTheWarning(store);
      expect(offered()).toStrictEqual(THE_OWNERS);

      // What "Start over" in another tab leaves behind: the key holds another copy, and nothing on
      // this page was told (src/test/demoMode.ts). The dialog draws itself again at each tick of
      // its countdown, a second apart.
      rememberDemoCopy({ ...claimed.copy, email: SECOND_COPY, password: SECOND_PASSWORD });
      await advance(1_000);

      await waitFor(() => expect(offered()).toStrictEqual(ANYONE_ELSES));
      expect({ sent, alerts: alerts() }).toStrictEqual({ sent: [], alerts: [] });
    });

    it('a copy another tab replaced, pressed before the next tick: nothing is sent', async () => {
      const { store, claimed } = await bootOnACopy();
      const sent = reauthsSent();
      await reachTheWarning(store);
      expect(offered()).toStrictEqual(THE_OWNERS);

      // In one turn, so that no tick and no draw comes between the two lines: the button pressed
      // is the one drawn for the owner, over a key that is no longer the owner's. The password
      // kept for the first copy would be sent under the second copy's session, be refused, and be
      // worded as a copy that has ended.
      rememberDemoCopy({ ...claimed.copy, email: SECOND_COPY, password: SECOND_PASSWORD });
      fireEvent.click(stayButtons()[0]);

      await advance(1_000);
      await settled(store);
      expect({ sent, offered: offered(), alerts: alerts() }).toStrictEqual({
        sent: [],
        offered: ANYONE_ELSES,
        alerts: [],
      });
    });

    it('a copy that has ended says so, and stays remembered', async () => {
      const { store } = await bootOnACopy();
      const sent = reauthsSent();
      const kept = localStorage.getItem(KEY);
      expect(kept).toContain(FIRST_PASSWORD);
      await reachTheWarning(store);
      expect(offered()).toStrictEqual(THE_OWNERS);
      // The copy has ended on the server and its session has not: the mock no longer counts the
      // copy among the claimed ones, and answers its session's re-authentication as it answers a
      // wrong password, whatever the password (`reauthenticate` in src/mocks/handlers.ts).
      mockState.demoCopies = [];
      const statuses = statusesSeen(store);

      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await user.click(stayButtons()[0]);

      await waitFor(() => expect(alerts()).toStrictEqual([WORDS.ended]));
      // The right password was sent and refused. The button is still there, the browser still
      // keeps the copy, and the session was never taken for one that ended.
      expect({
        sent,
        offered: offered(),
        kept: localStorage.getItem(KEY),
        statuses,
      }).toStrictEqual({
        sent: [{ password: FIRST_PASSWORD }],
        offered: THE_OWNERS,
        kept,
        statuses: ['authenticated'],
      });
    });

    it('after a refused request focus is back on "Stay signed in"', async () => {
      const { store } = await bootOnACopy();
      // The copy has ended on the server, as in the test above, and the answer is held until the
      // wait has been drawn: an answer that came at once would never show the button waiting.
      mockState.demoCopies = [];
      let answer = () => {};
      const held = new Promise<void>((resolve) => {
        answer = resolve;
      });
      server.use(
        http.post(REAUTH, async () => {
          await held;
          // No answer from here, so the mock's own handler gives it.
          return undefined;
        }),
      );
      // While it waits the button is disabled, and a browser then hands its focus to the page.
      const stopFixup = emulateFocusFixup();
      try {
        await reachTheWarning(store);
        await waitFor(() => expect(focused()).toBe(THE_OWNERS[0]));

        const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
        await user.keyboard('{Enter}');
        await waitFor(() =>
          expect({
            disabled: stayButtons().map((button) => button.disabled),
            focusIsOnThePage: document.activeElement === document.body,
          }).toStrictEqual({ disabled: [true], focusIsOnThePage: true }),
        );

        answer();
        await waitFor(() => expect(alerts()).toStrictEqual([WORDS.ended]));
        // On the button that was pressed, which can be pressed again, with "Sign out now" one
        // Tab away. Left on the page, the next key a visitor pressed would act on nothing.
        await waitFor(() =>
          expect({ offered: offered(), focused: focused() }).toStrictEqual({
            offered: THE_OWNERS,
            focused: THE_OWNERS[0],
          }),
        );
      } finally {
        stopFixup();
      }
    });

    /** A new session on the same page, brought to its own cap: what signing in again leaves. */
    async function signedInAgain(store: TestStore, signIn: () => void) {
      signIn();
      mockState.sessionInactivityWindowMs = 30 * 60_000;
      mockState.sessionAbsoluteWindowMs = ABSOLUTE_SOON_MS;
      await act(async () => {
        await store.dispatch(apiSlice.endpoints.getMe.initiate(undefined, { forceRefetch: true }));
      });
      expect(store.getState().auth.status).toBe('authenticated');
      await advance(35_000);
      await waitFor(() => expect(dialog()).toBeInTheDocument());
    }

    it("a refusal's words are not said to the next session", async () => {
      const { store } = await bootOnACopy();
      await reachTheWarning(store);
      expect(offered()).toStrictEqual(THE_OWNERS);
      // Signed out in another tab: the session is gone on the server while this dialog is still
      // open. The BFF answers the button's request itself, with a 401 that carries no code, and
      // that answer ends the session on this page as well. The dialog goes with it.
      mockState.session = null;

      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await user.click(stayButtons()[0]);
      await waitFor(() => expect(store.getState().auth.status).toBe('expired'));
      await settled(store);
      expect(dialog()).not.toBeInTheDocument();

      // The visitor signs in to a copy again, on the same page, and reaches its cap. The copy is
      // a living one: "This demo copy has ended" was the last session's answer, and not even
      // about the copy.
      await signedInAgain(store, () => rememberDemoCopy(seedMockDemoCopy().copy));

      expect({ offered: offered(), alerts: alerts() }).toStrictEqual({
        offered: THE_OWNERS,
        alerts: [],
      });
    });

    it("nor a typed password's, off the demo", async () => {
      const store = await bootAbsolute();
      await reachTheWarning(store);
      expect(offered()).toStrictEqual(ANYONE_ELSES);
      // The same end of the session, met by "Sign in again".
      mockState.session = null;

      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      fireEvent.change(password(), { target: { value: MOCK_PASSWORD } });
      await user.click(screen.getByRole('button', { name: /sign in again/i }));
      await waitFor(() => expect(store.getState().auth.status).toBe('expired'));
      await settled(store);
      expect(dialog()).not.toBeInTheDocument();

      await signedInAgain(store, () => seedMockSession());

      // No "That password didn't match." over a field nobody has submitted in this session.
      expect({ offered: offered(), alerts: alerts() }).toStrictEqual({
        offered: ANYONE_ELSES,
        alerts: [],
      });
    });

    it("the owner's other failures keep the dialog's own words", async () => {
      const { store } = await bootOnACopy();
      await reachTheWarning(store);
      expect(offered()).toStrictEqual(THE_OWNERS);
      // The mock's own limiter, with its budget spent (`AUTH_PERMIT_LIMIT` in src/mocks/handlers.ts,
      // ten): it answers before anybody looks at the password.
      mockState.authCallTimes = Array.from({ length: 10 }, () => Date.now());

      const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
      await user.click(stayButtons()[0]);

      await waitFor(() => expect(alerts()).toStrictEqual([WORDS.tooManyAttempts]));
      expect(offered()).toStrictEqual(THE_OWNERS);
    });

    it('demo, a stored copy for another address: the password field, as today', async () => {
      // CONTROL: green before this change
      const { store } = await bootOnACopy({
        kept: { email: SECOND_COPY, password: SECOND_PASSWORD },
      });
      await reachTheWarning(store);

      expect(offered()).toStrictEqual(ANYONE_ELSES);
      // The browser does keep that copy: a key the page could not read as a copy would be gone
      // by now, and this would be the test below.
      expect(localStorage.getItem(KEY)).toContain(SECOND_COPY);
    });

    it('demo, no stored copy: the password field, as today', async () => {
      // CONTROL: green before this change
      const { store } = await bootOnACopy({ kept: null });
      await reachTheWarning(store);

      expect(offered()).toStrictEqual(ANYONE_ELSES);
      expect(localStorage.getItem(KEY)).toBeNull();
    });

    it("without the tag nothing reads the demo's key", async () => {
      const reads = vi.spyOn(Storage.prototype, 'getItem');
      const readsOfTheKey = () => reads.mock.calls.filter(([key]) => key === KEY).length;
      try {
        // Signed in to a copy this browser keeps: everything the owner has, but for the tag.
        const { store } = await bootOnACopy({ demo: false });
        await reachTheWarning(store);
        const offTheDemo = { offered: offered(), readsOfTheKey: readsOfTheKey() };

        // The same dialog once the page says it is the demo, so the silence above is this spy's
        // to report: the dialog reads the key at its next draw, a tick away, and is the owner's.
        enableDemoMode();
        await advance(1_000);

        await waitFor(() =>
          expect({
            offTheDemo,
            onTheDemo: { offered: offered(), readTheKey: readsOfTheKey() > 0 },
          }).toStrictEqual({
            offTheDemo: { offered: ANYONE_ELSES, readsOfTheKey: 0 },
            onTheDemo: { offered: THE_OWNERS, readTheKey: true },
          }),
        );
      } finally {
        reads.mockRestore();
      }
    });
  });
});
