import { act, cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppToaster } from '../components/feedback';
import { apiSlice } from '../features/api/apiSlice';
import { AuthBootstrap } from '../features/auth';
import { problem, serviceUnavailable } from '../mocks/problem';
import { server } from '../mocks/server';
import { MOCK_PASSWORD, MOCK_USER, mockState, seedMockDemoCopy } from '../mocks/state';
import { enableDemoMode, rememberDemoCopy } from '../test/demoMode';
import { expectNoNestedLiveRegions } from '../test/liveRegions';
import {
  COPY,
  advanceUntil,
  emulateFocusFixup,
  fakeClockUser,
  hintRegion,
  hintShownAt,
  installFakeClock,
  never,
} from '../test/outage';
import { renderWithProviders } from '../test/renderWithProviders';
import { LoginPage } from './LoginPage';

/*
  The sign-in page: without the demo's tag, the page it was; with it, the demo first.

  On the demo the page leads with the demo, and the form is still there under it for whoever has
  a copy's email and password.

  In a browser that keeps no copy the demo is one button, "Try the demo", which claims a private
  copy and lands on the dashboard: what the page offers, what a press of the button sends and
  does, what waits while a request runs, and what the page says for each answer a claim can get.

  In a browser that keeps a copy the page offers that copy instead: "Continue with my copy" signs
  in with what the browser kept, "Get a new copy" asks before it claims another, and "Forget this
  copy" leaves nothing behind. What each sends, what the page says when the copy is gone, locked
  or past its end, and what the page does about a copy another tab forgot or replaced.

  The words are typed out here and not imported from the product, so a test fails the day the words
  on screen are no longer these. The apostrophes are ASCII; the dash in the connection's sentence is
  U+2014.
*/
const WORDS = {
  title: 'Welcome back',
  subtitle: 'Try the demo with one click. No sign-up needed.',
  subtitleOffTheDemo: 'Sign in to your account to continue',
  tryTheDemo: 'Try the demo',
  noticeFirst: 'You get a private copy of a demo bank account with invented money.',
  noticeRest:
    "It has two accounts, two months of history and two contacts you can pay. Don't enter real personal data. The copy works for 24 hours, then it is closed and deleted. This browser remembers the copy's sign-in details so you can come back to it. To limit abuse, a one-way code of your network address is kept with the copy and removed when the copy is deleted.",
  divider: 'or',
  aboveTheForm: "Already have a copy's email and password? Sign in here.",
  signIn: 'Sign in',
  createAccount: 'Create account',
  tooManyAttempts: 'Too many attempts from your connection.',
  poolEmpty:
    'All demo copies are in use right now. New ones are added regularly. Please try again later.',
  dailyLimit: 'This network has used its demo copies for today. Please try again tomorrow.',
  wrongPassword: 'Invalid email or password.',
  connection: "Couldn't reach the server — check your connection and try again.",
  fallback: 'Something went wrong. Please try again.',
  continue: 'Continue with my copy',
  getNew: 'Get a new copy',
  forget: 'Forget this copy',
  forgotten: 'This browser no longer remembers the copy.',
  copyGone:
    'That demo copy is no longer available. Copies are closed after 24 hours. You can get a new one.',
  locked: 'Too many failed sign-in attempts — your account is temporarily locked.',
  startOverTitle: 'Start over with a new copy?',
  startOver: 'Start over',
  keepThisCopy: 'Keep this copy',
  newCopy: 'You have a new copy.',
} as const;

/** "This browser remembers a demo copy. It works until January 5, 2026 · 9:15 AM." */
const REMEMBERED =
  /^This browser remembers a demo copy\. It works until ([A-Z][a-z]+ \d{1,2}, \d{4}) · (\d{1,2}:\d{2} (?:AM|PM))\.$/;

const CLAIM = '*/bff/auth/demo/claim';
const SIGN_IN = '*/bff/auth/login';
const ME = '*/bff/auth/me';
const KEY = 'azurebank.demoCopy';
/** The first copy of the mock's pool (src/mocks/state.ts): the one a first claim hands out. */
const FIRST_COPY = 'demo-k7m2x9q4w8e1r5t3@azurebank.example';
/** The pool's second copy: the one a claim hands out once the first is somebody's. */
const SECOND_COPY = 'demo-4h9d2s7f1g6j3k8a@azurebank.example';
/**
 * A copy's end that is a whole minute in the middle of July, far from any day a zone changes its
 * clocks, and years ahead: the day and the hour it is printed as name one instant in whichever
 * zone the suite runs.
 */
const A_FAR_END = '2031-07-15T12:30:00.000Z';
/** An address nobody has an account under, on the demo or off it. */
const NOBODY = 'visitor@azurebank.example';

type Answer = () => Response | Promise<Response> | undefined;

/** The API's refusal for an empty pool, with the API's own sentence: not the page's. */
const poolEmpty = () =>
  problem({
    status: 429,
    errorCode: 'DEMO_POOL_EMPTY',
    detail: 'All demo copies are in use right now. Please try again later.',
    instance: '/api/auth/demo/claim',
  });

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function LoginHarness() {
  return (
    <>
      {/* The app asks who is signed in from its root, beside its routes (src/App.tsx). */}
      <AuthBootstrap />
      {/* The toasts' outlet, which the app mounts once at its root and the test providers do not. */}
      <AppToaster />
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/dashboard" element={<div>DASHBOARD</div>} />
        <Route path="/transfer" element={<div>TRANSFER</div>} />
      </Routes>
    </>
  );
}

/**
 * The sign-in page, opened by a visitor nobody is signed in as, with the app's first question
 * (who is signed in) already answered.
 *
 * `claim` and `signIn` are this test's answers to those two requests. Without one, or when it
 * returns nothing, the request goes on to the mock's own handler. Either way each request is
 * counted here first, and so is each ask of who is signed in.
 */
async function openSignInPage({
  demo = true,
  state,
  claim,
  signIn,
}: { demo?: boolean; state?: unknown; claim?: Answer; signIn?: Answer } = {}) {
  // Nobody is signed in: the suite's setup starts every test signed in as the mock's user
  // (src/test/setup.ts), and that is not the visitor this page is for.
  mockState.session = null;
  if (demo) enableDemoMode();
  const requests = { whoIsSignedIn: 0, claims: 0, signIns: 0, signInBodies: [] as unknown[] };
  server.use(
    http.get(ME, () => {
      requests.whoIsSignedIn += 1;
    }),
    http.post(CLAIM, () => {
      requests.claims += 1;
      return claim?.();
    }),
    http.post(SIGN_IN, async ({ request }) => {
      requests.signIns += 1;
      // What was sent, read from a copy of the request: the mock's own handler reads it after.
      requests.signInBodies.push(await request.clone().json());
      return signIn?.();
    }),
  );
  const view = renderWithProviders(<LoginHarness />, {
    routerEntries: [{ pathname: '/login', state }],
  });
  await waitFor(() => expect(view.store.getState().auth.status).toBe('anonymous'));
  return {
    ...view,
    requests,
    /**
     * Every sign-in and claim the store has sent has its answer. A request that a press set going
     * is running by the time the press returns, so a count read after this is a count that would
     * have moved.
     */
    settled: () =>
      act(async () => {
        await Promise.all(view.store.dispatch(apiSlice.util.getRunningMutationsThunk()));
      }),
  };
}

type KeptCopy = ReturnType<typeof seedMockDemoCopy>['copy'];

/**
 * A copy of the mock's pool that somebody claimed in this browser, and that the browser still
 * keeps, as a claim leaves it: the next free copy of the pool, the first unless a test took it.
 * `change` is what this test's kept copy says otherwise. Called before the page is opened.
 */
function rememberAClaimedCopy(change: Partial<KeptCopy> = {}): KeptCopy {
  const kept = { ...seedMockDemoCopy().copy, ...change };
  rememberDemoCopy(kept);
  return kept;
}

const tryTheDemo = () =>
  screen.getByRole('button', { name: WORDS.tryTheDemo }) as HTMLButtonElement;
const signInButton = () => screen.getByRole('button', { name: WORDS.signIn }) as HTMLButtonElement;
const continueButton = () =>
  screen.getByRole('button', { name: WORDS.continue }) as HTMLButtonElement;
const getNewButton = () => screen.getByRole('button', { name: WORDS.getNew }) as HTMLButtonElement;
/** "Forget this copy" looks like a link and is a button: it goes nowhere. */
const forgetLink = () => screen.getByRole('button', { name: WORDS.forget }) as HTMLButtonElement;
const theForm = () => document.querySelector('form');

/** Every button on the page with exactly that name: none is an answer, not an error. */
const buttonsNamed = (name: string) =>
  screen.queryAllByRole('button', { name }) as HTMLButtonElement[];

/** Whether each button of that name is disabled. No button of that name is `[]`, not an error. */
const disabledOf = (name: string) => buttonsNamed(name).map((button) => button.disabled);

/** Every "or" rule on the page. */
const dividers = () => screen.queryAllByText(WORDS.divider);

/** What each alert on the page says. */
const alerts = () =>
  Array.from(document.querySelectorAll('[role="alert"]')).map((alert) => alert.textContent);

/** What each countdown on the page says. */
const timers = () => screen.queryAllByRole('timer').map((timer) => timer.textContent);

/** How many progress indicators `control` holds. */
const spinnersIn = (control: Element | null) =>
  control?.querySelectorAll('[role="progressbar"]').length;

/** Whether each element is in the page and comes after the one before it, in document order. */
function inOrder(...elements: (Element | null | undefined)[]): boolean {
  return elements.every((element, index) => {
    if (!element) return false;
    const before = elements[index - 1];
    return (
      index === 0 ||
      (!!before &&
        (before.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0)
    );
  });
}

/** The demo's block: what holds its first button, whichever of the two the page is offering. */
const demoBlock = () =>
  (buttonsNamed(WORDS.continue)[0] ?? buttonsNamed(WORDS.tryTheDemo)[0])?.parentElement ?? null;

/**
 * The demo block's own status region: the one that says a copy was forgotten. Not the wait's,
 * which lives inside the hint.
 */
const forgetStatuses = () =>
  Array.from(demoBlock()?.querySelectorAll<HTMLElement>('[role="status"]') ?? []).filter(
    (region) => region.closest('[data-wait-hint]') === null,
  );

/** The title of each dialog that is open. A closed one is in the page, hidden, and is not one. */
const openDialogs = () =>
  screen
    .queryAllByRole('alertdialog')
    .map((dialog) => within(dialog).queryByRole('heading')?.textContent);

/** Whose copy the browser keeps, read from its storage with nothing of the product in between. */
function keptAddress(): string | null {
  const raw = localStorage.getItem(KEY);
  return raw === null ? null : (JSON.parse(raw) as { email: string }).email;
}

/** Which of the harness's pages is on screen. */
function where(): string {
  if (screen.queryByText('DASHBOARD')) return 'the dashboard';
  if (screen.queryByText('TRANSFER')) return 'the transfer page';
  return document.querySelector('h1')?.textContent === WORDS.title ? 'the sign-in page' : 'nowhere';
}

async function fillTheForm(email: string, password: string) {
  await userEvent.type(screen.getByLabelText('Email address'), email);
  await userEvent.type(screen.getByLabelText('Password'), password);
}

describe('the sign-in page without the demo', () => {
  it('without the tag: "Create account", and no "Try the demo"', async () => {
    // CONTROL: green before this change
    await openSignInPage({ demo: false });

    expect({
      subtitle: document.querySelector('h1')?.nextElementSibling?.textContent,
      createAccount: screen
        .queryAllByRole('link', { name: WORDS.createAccount })
        .map((link) => link.getAttribute('href')),
      tryTheDemo: screen.queryAllByRole('button', { name: WORDS.tryTheDemo }).length,
      notice: screen.queryAllByText(WORDS.noticeFirst).length,
      aboveTheForm: screen.queryAllByText(WORDS.aboveTheForm).length,
      // The divider is where it was: one, after the form, before the link.
      dividers: dividers().length,
      order: inOrder(
        theForm(),
        dividers()[0],
        screen.queryAllByRole('link', { name: WORDS.createAccount })[0],
      ),
      // No dialog in the page, open or closed: the one that asks before a new copy is the demo's.
      dialogs: document.querySelectorAll('[role="alertdialog"], [role="dialog"]').length,
    }).toStrictEqual({
      subtitle: WORDS.subtitleOffTheDemo,
      createAccount: ['/register'],
      tryTheDemo: 0,
      notice: 0,
      aboveTheForm: 0,
      dividers: 1,
      order: true,
      dialogs: 0,
    });
  });

  it("without the tag nothing reads the demo's key", async () => {
    // A copy under the key, as a visit to the demo on this origin would have left one.
    rememberDemoCopy({
      email: FIRST_COPY,
      password: 'Xk7p-Rm3w-Hn8d-Tq5v',
      pin: '123456',
      contacts: ['jane_k7m2', 'mike_k7m2'],
      expiresAt: A_FAR_END,
    });
    const reads = vi.spyOn(Storage.prototype, 'getItem');
    const readsOfTheKey = () => reads.mock.calls.filter(([key]) => key === KEY).length;
    try {
      await openSignInPage({ demo: false });
      const offTheDemo = {
        readsOfTheKey: readsOfTheKey(),
        continue: buttonsNamed(WORDS.continue).length,
        signIn: buttonsNamed(WORDS.signIn).length,
      };

      // The same page with the tag on, so the silence above is this spy's to report: it sees the
      // page's read the moment there is one.
      cleanup();
      await openSignInPage();
      const onTheDemo = {
        readTheKey: readsOfTheKey() > 0,
        continue: buttonsNamed(WORDS.continue).length,
      };

      expect({ offTheDemo, onTheDemo }).toStrictEqual({
        offTheDemo: { readsOfTheKey: 0, continue: 0, signIn: 1 },
        onTheDemo: { readTheKey: true, continue: 1 },
      });
    } finally {
      reads.mockRestore();
    }
  });

  it('without the tag: a lock takes "Sign in" away, and says so with its countdown', async () => {
    // CONTROL: green before this change
    // Locked after too many wrong passwords, and the right password typed: the one answer that
    // says the lock (src/mocks/handlers.ts).
    mockState.loginLockedUntil[MOCK_USER.email] = new Date(Date.now() + 30_000).toISOString();
    const { requests } = await openSignInPage({ demo: false });
    await fillTheForm(MOCK_USER.email, MOCK_PASSWORD);

    await userEvent.click(signInButton());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.locked]));
    expect({
      signIns: requests.signIns,
      timers: timers().map((words) => /^Try again in [1-3]?\ds$/.test(words ?? '')),
      // Gone, not disabled, until the countdown ends.
      signInButtons: buttonsNamed(WORDS.signIn).length,
      where: where(),
    }).toStrictEqual({ signIns: 1, timers: [true], signInButtons: 0, where: 'the sign-in page' });
  });

  it('without the tag: too many attempts are said under "Sign in", inside the form', async () => {
    // CONTROL: green before this change
    const { requests } = await openSignInPage({ demo: false });
    // The mock's own limiter, with its budget spent (`AUTH_PERMIT_LIMIT` in src/mocks/handlers.ts,
    // ten): the next sign-in is answered by the limiter.
    mockState.authCallTimes = Array.from({ length: 10 }, () => Date.now());
    await fillTheForm(MOCK_USER.email, MOCK_PASSWORD);

    await userEvent.click(signInButton());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.tooManyAttempts]));
    const banner = document.querySelector('[role="alert"]');
    expect({
      signIns: requests.signIns,
      inTheForm: theForm()?.contains(banner),
      underSignIn: inOrder(signInButton(), banner, screen.queryByRole('timer')),
      timers: timers().length,
      signInDisabled: signInButton().disabled,
    }).toStrictEqual({
      signIns: 1,
      inTheForm: true,
      underSignIn: true,
      timers: 1,
      signInDisabled: true,
    });
  });
});

describe('the sign-in page on the demo, in a browser that keeps no copy', () => {
  it('with the tag the page leads with the demo', async () => {
    await openSignInPage();
    const first = screen.queryByText(WORDS.noticeFirst);
    const notice = first?.parentElement;

    expect({
      // One level-one heading, the page's own, counted in the document: the brand panel beside
      // the form is hidden by a style jsdom applies, so a query by role would not see a second.
      levelOneHeadings: Array.from(document.querySelectorAll('h1')).map((h) => h.textContent),
      subtitle: document.querySelector('h1')?.nextElementSibling?.textContent,
      buttons: buttonsNamed(WORDS.tryTheDemo).length,
      notice: notice?.textContent,
      dividers: dividers().length,
      aboveTheForm: screen.queryAllByText(WORDS.aboveTheForm).map((line) => line.tagName),
      // The form is still the form: its two fields and its button, by their names.
      emailFields: screen.queryAllByLabelText('Email address').length,
      passwordFields: screen.queryAllByLabelText('Password').length,
      signInButtons: buttonsNamed(WORDS.signIn).length,
      order: inOrder(
        buttonsNamed(WORDS.tryTheDemo)[0],
        notice,
        dividers()[0],
        screen.queryAllByText(WORDS.aboveTheForm)[0],
        theForm(),
      ),
      // What the form holds is inside it, not beside it.
      formHoldsItsButton: theForm()?.contains(buttonsNamed(WORDS.signIn)[0] ?? null),
    }).toStrictEqual({
      levelOneHeadings: [WORDS.title],
      subtitle: WORDS.subtitle,
      buttons: 1,
      notice: `${WORDS.noticeFirst} ${WORDS.noticeRest}`,
      dividers: 1,
      // Words to read, not a control: the page has one thing named "Sign in".
      aboveTheForm: ['P'],
      emailFields: 1,
      passwordFields: 1,
      signInButtons: 1,
      order: true,
      formHoldsItsButton: true,
    });
  });

  it('with the tag there is no "Create account"', async () => {
    await openSignInPage();

    expect({
      // The page is there to be looked at: its form is on screen.
      signInButtons: screen.queryAllByRole('button', { name: WORDS.signIn }).length,
      createAccount: screen.queryAllByRole('link', { name: WORDS.createAccount }).length,
      linksToRegistration: document.querySelectorAll('a[href="/register"]').length,
      asksAboutAnAccount: screen.queryAllByText("Don't have an account?", { exact: false }).length,
    }).toStrictEqual({
      signInButtons: 1,
      createAccount: 0,
      linksToRegistration: 0,
      asksAboutAnAccount: 0,
    });
  });

  it("the button is described by the notice's first sentence, and by nothing more", async () => {
    await openSignInPage();
    const ids = (tryTheDemo().getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);

    expect(ids.map((id) => document.getElementById(id)?.textContent)).toStrictEqual([
      WORDS.noticeFirst,
    ]);
    expect(tryTheDemo()).toHaveAccessibleDescription(WORDS.noticeFirst);
  });

  it('a claim that succeeds lands on the dashboard, with the copy remembered and the session read again', async () => {
    // Sent here by the guard on the way to another page. A sign-in would go back there; a claim
    // goes to the dashboard, where a new copy starts.
    const { store, router, requests } = await openSignInPage({
      state: { from: { pathname: '/transfer' } },
    });
    const before = { asked: requests.whoIsSignedIn, kept: keptAddress(), where: where() };

    await userEvent.click(tryTheDemo());

    await waitFor(() => expect(where()).toBe('the dashboard'));
    // The claim dropped the whole cache, the answer to "who is signed in" with it, so the app
    // asks again, and is told it is the copy's owner.
    await waitFor(() => expect(requests.whoIsSignedIn).toBe(2));
    expect({
      before,
      claims: requests.claims,
      signIns: requests.signIns,
      kept: keptAddress(),
      signedInAs: store.getState().auth.user?.email,
      status: store.getState().auth.status,
      // In the sign-in page's place in the history, as a sign-in lands: Back from the dashboard
      // does not go to a sign-in page.
      landedBy: router.state.historyAction,
    }).toStrictEqual({
      before: { asked: 1, kept: null, where: 'the sign-in page' },
      claims: 1,
      signIns: 0,
      kept: FIRST_COPY,
      signedInAs: FIRST_COPY,
      status: 'authenticated',
      landedBy: 'REPLACE',
    });
  });

  it('while the claim runs every control waits, and the pressed one still has its name', async () => {
    const { requests } = await openSignInPage({ claim: never });
    installFakeClock();

    await fakeClockUser().click(tryTheDemo());

    // Still found by its name while it waits: the spinner is beside the words, not in their
    // place.
    await waitFor(() =>
      expect(buttonsNamed(WORDS.tryTheDemo).map((button) => button.disabled)).toStrictEqual([true]),
    );
    const shownAt = await hintShownAt();
    const pressed = tryTheDemo();
    const described = document.getElementById(pressed.getAttribute('aria-describedby') ?? '');
    expect({
      claims: requests.claims,
      pressed: { text: pressed.textContent, spinners: spinnersIn(pressed) },
      // Still named exactly "Sign in", so it kept its words: it was not the one pressed.
      signIn: buttonsNamed(WORDS.signIn).map((button) => ({
        disabled: button.disabled,
        spinners: spinnersIn(button),
      })),
      hints: document.querySelectorAll('[data-wait-hint]').length,
      wordsBefore5s: document.querySelector('[data-wait-hint]')?.textContent,
    }).toStrictEqual({
      claims: 1,
      pressed: { text: WORDS.tryTheDemo, spinners: 1 },
      signIn: [{ disabled: true, spinners: 0 }],
      hints: 1,
      wordsBefore5s: '',
    });

    await advanceUntil(shownAt, 5_000);

    // In a status region of its own that holds those words and nothing else, and in no alert.
    const region = hintRegion(COPY.slow);
    expect({
      said: screen.getAllByText(COPY.slow).length,
      underTheButton: inOrder(tryTheDemo(), region, described),
      insideTheDescription: described?.contains(region),
      inTheForm: theForm()?.contains(region),
    }).toStrictEqual({
      said: 1,
      underTheButton: true,
      insideTheDescription: false,
      inTheForm: false,
    });
    expect(tryTheDemo()).toHaveAccessibleDescription(WORDS.noticeFirst);
    expectNoNestedLiveRegions();

    // From 20 s, the words of a write that is not a money send, and no way to stop waiting: a
    // claim the server may already be making cannot be taken back by the page.
    await advanceUntil(shownAt, 20_000);
    hintRegion(COPY.stillTrying);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
  });

  it("while the form's sign-in runs the demo waits, and one hint speaks", async () => {
    const { requests } = await openSignInPage({ signIn: never });
    await fillTheForm(NOBODY, 'Any-Pass-1!');
    const submit = signInButton();
    installFakeClock();

    await fakeClockUser().click(submit);

    await waitFor(() =>
      expect(buttonsNamed(WORDS.tryTheDemo).map((button) => button.disabled)).toStrictEqual([true]),
    );
    const shownAt = await hintShownAt();
    expect({
      signIns: requests.signIns,
      claims: requests.claims,
      // Disabled, and not spinning: it waits for a request it did not send.
      demo: { text: tryTheDemo().textContent, spinners: spinnersIn(tryTheDemo()) },
      // The form's own button, as it was before the demo: its spinner takes its words' place.
      submit: { disabled: (submit as HTMLButtonElement).disabled, spinners: spinnersIn(submit) },
      hints: document.querySelectorAll('[data-wait-hint]').length,
    }).toStrictEqual({
      signIns: 1,
      claims: 0,
      demo: { text: WORDS.tryTheDemo, spinners: 0 },
      submit: { disabled: true, spinners: 1 },
      hints: 1,
    });

    await advanceUntil(shownAt, 5_000);

    const region = hintRegion(COPY.slow);
    expect({
      said: screen.getAllByText(COPY.slow).length,
      // Under the button that was pressed: the form's.
      inTheForm: theForm()?.contains(region),
      underItsButton: inOrder(submit, region),
    }).toStrictEqual({ said: 1, inTheForm: true, underItsButton: true });
    expectNoNestedLiveRegions();
  });

  it("all copies in use: the page's sentence, one alert, no countdown", async () => {
    const { requests } = await openSignInPage({ claim: poolEmpty });

    await userEvent.click(tryTheDemo());

    // Not the API's sentence, which the answer carries: the page's own.
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.poolEmpty]));
    expect({
      claims: requests.claims,
      timers: timers(),
      where: where(),
      kept: keptAddress(),
      // Nothing is counted down, so nothing waits: both can be pressed again.
      disabled: [tryTheDemo().disabled, signInButton().disabled],
      spinners: spinnersIn(tryTheDemo()),
      // Above the demo, where the page's alerts are.
      aboveTheDemo: inOrder(document.querySelector('[role="alert"]'), tryTheDemo()),
    }).toStrictEqual({
      claims: 1,
      timers: [],
      where: 'the sign-in page',
      kept: null,
      disabled: [false, false],
      spinners: 0,
      aboveTheDemo: true,
    });
  });

  it("this network's copies are used up: its sentence, one alert, no countdown", async () => {
    await openSignInPage({
      claim: () =>
        // The API's refusal, which names a wait in its body and in its header. The page counts
        // nothing down for it and stops nothing: its sentence says when to come back.
        problem({
          status: 429,
          errorCode: 'DEMO_DAILY_LIMIT',
          detail: 'This network has used its demo copies for today. Please try again later.',
          instance: '/api/auth/demo/claim',
          extensions: { retryAfterSeconds: 600 },
          headers: { 'Retry-After': '600' },
        }),
    });

    await userEvent.click(tryTheDemo());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.dailyLimit]));
    expect({
      timers: timers(),
      disabled: [tryTheDemo().disabled, signInButton().disabled],
      aboveTheDemo: inOrder(document.querySelector('[role="alert"]'), tryTheDemo()),
      kept: keptAddress(),
    }).toStrictEqual({ timers: [], disabled: [false, false], aboveTheDemo: true, kept: null });
  });

  it('too many attempts: the existing banner and countdown, above the demo, and every control waits it out', async () => {
    const { requests, store } = await openSignInPage();
    // The mock's own limiter, with its budget spent (`AUTH_PERMIT_LIMIT` in src/mocks/handlers.ts,
    // ten): it answers before the claim's handler takes a copy, in the limiter's own shape (a
    // `Retry-After` header and no wait in the body).
    mockState.authCallTimes = Array.from({ length: 10 }, () => Date.now());

    await userEvent.click(tryTheDemo());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.tooManyAttempts]));
    const banner = document.querySelector('[role="alert"]');
    expect({
      claims: requests.claims,
      // What the store kept of the refusal: the limiter's, with the header's wait read into it.
      refusals: Object.values(store.getState().api.mutations).map((entry) => entry?.error),
      timers: timers().map((words) => /^Try again in (1:00|[1-5]?\ds)$/.test(words ?? '')),
      aboveTheDemo: inOrder(banner, screen.queryByRole('timer'), tryTheDemo()),
      inTheForm: theForm()?.contains(banner),
      disabled: [tryTheDemo().disabled, signInButton().disabled],
      spinners: [spinnersIn(tryTheDemo()), spinnersIn(signInButton())],
      kept: keptAddress(),
    }).toStrictEqual({
      claims: 1,
      refusals: [
        expect.objectContaining({ errorCode: 'RATE_LIMIT_EXCEEDED', retryAfterSeconds: 60 }),
      ],
      timers: [true],
      aboveTheDemo: true,
      inTheForm: false,
      disabled: [true, true],
      spinners: [0, 0],
      kept: null,
    });
    expectNoNestedLiveRegions();
  });

  it("too many attempts on the form's sign-in: the banner is above the demo, and every control waits it out", async () => {
    const { requests } = await openSignInPage();
    // The same limiter with its budget spent, met by the form's sign-in this time. On the demo
    // the banner's place and what waits are the same whichever control was refused.
    mockState.authCallTimes = Array.from({ length: 10 }, () => Date.now());
    await fillTheForm(NOBODY, 'Any-Pass-1!');

    await userEvent.click(signInButton());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.tooManyAttempts]));
    const banner = document.querySelector('[role="alert"]');
    expect({
      requests: [requests.signIns, requests.claims],
      timers: timers().length,
      aboveTheDemo: inOrder(banner, screen.queryByRole('timer'), tryTheDemo()),
      inTheForm: theForm()?.contains(banner),
      disabled: [tryTheDemo().disabled, signInButton().disabled],
    }).toStrictEqual({
      requests: [1, 0],
      timers: 1,
      aboveTheDemo: true,
      inTheForm: false,
      disabled: [true, true],
    });
  });

  it('when the countdown ends the banner goes and every control can be pressed again', async () => {
    await openSignInPage();
    mockState.authCallTimes = Array.from({ length: 10 }, () => Date.now());
    installFakeClock();
    const pressedAt = Date.now();

    await fakeClockUser().click(tryTheDemo());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.tooManyAttempts]));
    const whileItCounted = {
      timers: timers().length,
      disabled: [tryTheDemo().disabled, signInButton().disabled],
    };

    // The limiter asked for 60 s.
    await advanceUntil(pressedAt, 61_500);

    await waitFor(() => expect(timers()).toStrictEqual([]));
    expect({
      whileItCounted,
      alerts: alerts(),
      disabled: [tryTheDemo().disabled, signInButton().disabled],
    }).toStrictEqual({
      whileItCounted: { timers: 1, disabled: [true, true] },
      alerts: [],
      disabled: [false, false],
    });
  });

  it('the service does not answer: the outage sentence, one alert', async () => {
    await openSignInPage({
      claim: () => serviceUnavailable({ via: 'bff', instance: '/bff/auth/demo/claim' }),
    });

    await userEvent.click(tryTheDemo());

    await waitFor(() => expect(alerts()).toStrictEqual([COPY.unavailable]));
    // The outage's answer names a wait too, and it is not this page's to count down.
    expect({
      timers: timers(),
      disabled: tryTheDemo().disabled,
      kept: keptAddress(),
    }).toStrictEqual({ timers: [], disabled: false, kept: null });
  });

  it('the connection fails: the connection sentence, one alert', async () => {
    await openSignInPage({ claim: () => HttpResponse.error() });

    await userEvent.click(tryTheDemo());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.connection]));
    expect({ where: where(), kept: keptAddress() }).toStrictEqual({
      where: 'the sign-in page',
      kept: null,
    });
  });

  it('any other refusal: what the server said, or the fallback', async () => {
    const answers = [
      // The API's own 500, with its sentence.
      () =>
        problem({
          status: 500,
          errorCode: 'INTERNAL_ERROR',
          detail: 'An unexpected error occurred. Please try again later.',
        }),
      // A refusal that says nothing.
      () => new HttpResponse(null, { status: 500 }),
    ];
    const { requests } = await openSignInPage({ claim: () => answers.shift()?.() });

    await userEvent.click(tryTheDemo());
    await waitFor(() =>
      expect(alerts()).toStrictEqual(['An unexpected error occurred. Please try again later.']),
    );

    // Pressed again: the refusal takes the place of the one before it.
    await userEvent.click(tryTheDemo());
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.fallback]));
    expect(requests.claims).toBe(2);
  });

  it('an answer the app will not accept: the fallback, and the visitor is still on the page with nothing kept', async () => {
    // Redux Toolkit reports an answer it could not transform on console.error, and this suite
    // fails any test that writes there (src/test/setup.ts). Here the refused answer is the
    // subject, so the report is stubbed, and counted.
    const report = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      const { store } = await openSignInPage({
        claim: () =>
          // A claim's answer with no copy in it: a sign-in's answer, which the claim's check
          // refuses.
          HttpResponse.json({
            data: {
              user: {
                id: 'u1',
                email: FIRST_COPY,
                firstName: 'John',
                lastName: 'Smith',
                azureTag: 'john_k7m2',
                hasPin: true,
              },
              expiresAt: '2026-10-04T09:15:00.000Z',
            },
            message: 'Demo copy claimed',
          }),
      });

      await userEvent.click(tryTheDemo());

      await waitFor(() => expect(alerts()).toStrictEqual([WORDS.fallback]));
      expect({
        reports: report.mock.calls.length,
        where: where(),
        kept: keptAddress(),
        status: store.getState().auth.status,
      }).toStrictEqual({ reports: 1, where: 'the sign-in page', kept: null, status: 'anonymous' });
    } finally {
      report.mockRestore();
    }
  });

  it("a failed sign-in followed by a failed claim leaves one alert, the claim's", async () => {
    const { requests } = await openSignInPage({ claim: poolEmpty });
    await fillTheForm(NOBODY, 'Wrong-Pass-1!');

    await userEvent.click(signInButton());
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.wrongPassword]));

    await userEvent.click(tryTheDemo());

    // One alert, and the sign-in's sentence is no longer on the page.
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.poolEmpty]));
    expect({
      requests: [requests.signIns, requests.claims],
      saysWrongPassword: screen.queryAllByText(WORDS.wrongPassword).length,
    }).toStrictEqual({ requests: [1, 1], saysWrongPassword: 0 });
  });

  it("a failed claim followed by a failed sign-in leaves one alert, the sign-in's", async () => {
    const { requests } = await openSignInPage({ claim: poolEmpty });

    await userEvent.click(tryTheDemo());
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.poolEmpty]));

    await fillTheForm(NOBODY, 'Wrong-Pass-1!');
    await userEvent.click(signInButton());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.wrongPassword]));
    expect({
      requests: [requests.claims, requests.signIns],
      saysAllCopiesInUse: screen.queryAllByText(WORDS.poolEmpty).length,
    }).toStrictEqual({ requests: [1, 1], saysAllCopiesInUse: 0 });
  });

  it('a refused sign-in is no longer said while the claim that follows it runs', async () => {
    await openSignInPage({ claim: never });
    await fillTheForm(NOBODY, 'Wrong-Pass-1!');
    await userEvent.click(signInButton());
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.wrongPassword]));

    await userEvent.click(tryTheDemo());

    // The claim is on its way and has no answer: its button waits. The page reads the claim's
    // refusal from the press on, not from the claim's answer on, and there is none to read yet.
    await waitFor(() =>
      expect(buttonsNamed(WORDS.tryTheDemo).map((button) => button.disabled)).toStrictEqual([true]),
    );
    expect(alerts()).toStrictEqual([]);
  });

  it('the form still signs in on the demo, and lands where the visitor was going', async () => {
    // CONTROL: green before this change
    // A copy somebody claimed earlier, whose email and password the visitor types: on the demo
    // nobody else has an account (src/mocks/handlers.ts).
    const { copy } = seedMockDemoCopy();
    const { requests, store } = await openSignInPage({
      state: { from: { pathname: '/transfer' } },
    });
    await fillTheForm(copy.email, copy.password);

    await userEvent.click(signInButton());

    await waitFor(() => expect(where()).toBe('the transfer page'));
    expect({
      requests: [requests.signIns, requests.claims],
      signedInAs: store.getState().auth.user?.email,
    }).toStrictEqual({ requests: [1, 0], signedInAs: FIRST_COPY });
  });

  it('a lock met by the form takes "Sign in" away until its countdown ends, on the demo too', async () => {
    // CONTROL: green before this change
    // A copy somebody claimed, locked after too many wrong passwords, and its right password
    // typed into the form: the one answer that says the lock (src/mocks/handlers.ts).
    const { copy } = seedMockDemoCopy();
    mockState.loginLockedUntil[copy.email] = new Date(Date.now() + 30_000).toISOString();
    const { requests } = await openSignInPage();
    await fillTheForm(copy.email, copy.password);

    await userEvent.click(signInButton());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.locked]));
    expect({
      signIns: requests.signIns,
      timers: timers().length,
      // The form's own lock: its button is gone, not disabled, as before the demo.
      signInButtons: buttonsNamed(WORDS.signIn).length,
      // The demo is another way in, for another copy: the lock is not its to wait out.
      demo: buttonsNamed(WORDS.tryTheDemo).map((button) => button.disabled),
    }).toStrictEqual({ signIns: 1, timers: 1, signInButtons: 0, demo: [false] });
  });

  it('no live region sits inside another', async () => {
    await openSignInPage({ claim: poolEmpty });

    await userEvent.click(tryTheDemo());
    // With a refusal showing. The page's other live regions, the wait's hint and the countdown,
    // are looked at where they speak: in the tests of a slow claim and of the limiter above.
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.poolEmpty]));

    expectNoNestedLiveRegions();
  });
});

describe('the sign-in page on the demo, in a browser that keeps a copy', () => {
  it('offers the remembered copy, and not a new one by accident', async () => {
    const kept = rememberAClaimedCopy({ expiresAt: A_FAR_END });
    await openSignInPage();
    const remembered = screen.queryAllByText(REMEMBERED);
    const printed = REMEMBERED.exec(remembered[0]?.textContent ?? '');
    const notice = screen.queryByText(WORDS.noticeFirst)?.parentElement;
    const blockButtons = Array.from(demoBlock()?.querySelectorAll('button') ?? []);

    expect({
      remembered: remembered.length,
      // The instant printed is the kept copy's end, in the zone the suite runs in: read back from
      // the words, and never typed out as an hour.
      until: printed && new Date(`${printed[1]} ${printed[2]}`).toISOString(),
      // What the block offers, by name, in the order it offers it.
      offers: blockButtons.map((button) => button.textContent),
      tryTheDemo: buttonsNamed(WORDS.tryTheDemo).length,
      statuses: forgetStatuses().map((region) => region.textContent),
      notice: notice?.textContent,
      // The notice is words to read here: "Try the demo" is the one button it describes.
      described: blockButtons.filter((button) => button.hasAttribute('aria-describedby')).length,
      order: inOrder(
        remembered[0],
        buttonsNamed(WORDS.continue)[0],
        buttonsNamed(WORDS.getNew)[0],
        buttonsNamed(WORDS.forget)[0],
        forgetStatuses()[0],
        notice,
        dividers()[0],
        screen.queryAllByText(WORDS.aboveTheForm)[0],
        theForm(),
      ),
      // The rest of the page is the page it was: one title, one "or", the form and its button.
      levelOneHeadings: Array.from(document.querySelectorAll('h1')).map((h) => h.textContent),
      dividers: dividers().length,
      signInButtons: buttonsNamed(WORDS.signIn).length,
      alerts: alerts(),
      kept: keptAddress(),
    }).toStrictEqual({
      remembered: 1,
      until: kept.expiresAt,
      offers: [WORDS.continue, WORDS.getNew, WORDS.forget],
      tryTheDemo: 0,
      statuses: [''],
      notice: `${WORDS.noticeFirst} ${WORDS.noticeRest}`,
      described: 0,
      order: true,
      levelOneHeadings: [WORDS.title],
      dividers: 1,
      signInButtons: 1,
      alerts: [],
      kept: FIRST_COPY,
    });
  });

  it('"Continue with my copy" signs in with the stored pair', async () => {
    const kept = rememberAClaimedCopy();
    const { requests, store, router } = await openSignInPage();

    await userEvent.click(continueButton());

    await waitFor(() => expect(where()).toBe('the dashboard'));
    expect({
      sent: requests.signInBodies,
      claims: requests.claims,
      signedInAs: store.getState().auth.user?.email,
      status: store.getState().auth.status,
      // Still the browser's copy: a sign-in keeps what a claim left.
      kept: localStorage.getItem(KEY),
      landedBy: router.state.historyAction,
    }).toStrictEqual({
      sent: [{ email: kept.email, password: kept.password }],
      claims: 0,
      signedInAs: FIRST_COPY,
      status: 'authenticated',
      kept: JSON.stringify({ v: 1, ...kept }),
      landedBy: 'REPLACE',
    });
  });

  it('…and lands where the visitor was going', async () => {
    rememberAClaimedCopy();
    // Sent here by the guard on the way to another page: the copy's sign-in goes back there, as
    // the form's does.
    const { requests } = await openSignInPage({ state: { from: { pathname: '/transfer' } } });

    await userEvent.click(continueButton());

    await waitFor(() => expect(where()).toBe('the transfer page'));
    expect([requests.signIns, requests.claims]).toStrictEqual([1, 0]);
  });

  it('"Continue with my copy" sends the pair the browser keeps when it is pressed', async () => {
    rememberAClaimedCopy();
    const { requests, store } = await openSignInPage();
    const offered = buttonsNamed(WORDS.continue).length;
    // Another tab started over: the key now holds the pool's second copy. This tab is told
    // nothing and has drawn nothing since, so the copy it drew its button for is the first.
    const second = rememberAClaimedCopy();

    await userEvent.click(continueButton());

    await waitFor(() => expect(where()).toBe('the dashboard'));
    expect({
      offered,
      sent: requests.signInBodies,
      signedInAs: store.getState().auth.user?.email,
    }).toStrictEqual({
      offered: 1,
      sent: [{ email: SECOND_COPY, password: second.password }],
      signedInAs: SECOND_COPY,
    });
  });

  it('a copy another tab forgot before "Continue with my copy" is pressed: nothing is sent, and the demo is offered', async () => {
    rememberAClaimedCopy();
    const { requests, settled } = await openSignInPage();
    const offered = buttonsNamed(WORDS.continue).length;
    // Another tab pressed "Forget this copy". This tab is told nothing: its button is still there.
    localStorage.removeItem(KEY);
    const stillOffered = buttonsNamed(WORDS.continue).length;

    await userEvent.click(continueButton());
    await settled();

    expect({
      offered,
      stillOffered,
      signIns: requests.signIns,
      claims: requests.claims,
      continue: buttonsNamed(WORDS.continue).length,
      tryTheDemo: buttonsNamed(WORDS.tryTheDemo).map((button) => button.disabled),
      alerts: alerts(),
      where: where(),
    }).toStrictEqual({
      offered: 1,
      stillOffered: 1,
      signIns: 0,
      claims: 0,
      continue: 0,
      tryTheDemo: [false],
      alerts: [],
      where: 'the sign-in page',
    });
  });

  it('a copy that is gone: one sentence, the copy forgotten, the demo offered again', async () => {
    // The browser kept a password the server no longer takes: what a copy that ended, or one that
    // was handed to somebody else, answers. The same 401 as any wrong password.
    rememberAClaimedCopy({ password: 'Wrong-Pass-1!' });
    const { requests } = await openSignInPage();
    const keptBefore = keptAddress();

    await userEvent.click(continueButton());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.copyGone]));
    expect({
      keptBefore,
      signIns: requests.signIns,
      saysWrongPassword: screen.queryAllByText(WORDS.wrongPassword).length,
      kept: keptAddress(),
      continue: buttonsNamed(WORDS.continue).length,
      tryTheDemo: buttonsNamed(WORDS.tryTheDemo).map((button) => button.disabled),
      // The sentence says what happened; the block's own status says only what was asked for.
      statuses: forgetStatuses().map((region) => region.textContent),
      aboveTheDemo: inOrder(
        document.querySelector('[role="alert"]'),
        buttonsNamed(WORDS.tryTheDemo)[0],
      ),
      where: where(),
    }).toStrictEqual({
      keptBefore: FIRST_COPY,
      signIns: 1,
      saysWrongPassword: 0,
      kept: null,
      continue: 0,
      tryTheDemo: [false],
      statuses: [''],
      aboveTheDemo: true,
      where: 'the sign-in page',
    });
    expectNoNestedLiveRegions();
  });

  it('after a copy that is gone, "Try the demo" claims a new one and the sentence goes', async () => {
    rememberAClaimedCopy({ password: 'Wrong-Pass-1!' });
    const { requests, store } = await openSignInPage();
    await userEvent.click(continueButton());
    await waitFor(() =>
      expect({ alerts: alerts(), tryTheDemo: disabledOf(WORDS.tryTheDemo) }).toStrictEqual({
        alerts: [WORDS.copyGone],
        tryTheDemo: [false],
      }),
    );

    // "You can get a new one.": by the button the page now offers.
    await userEvent.click(tryTheDemo());

    await waitFor(() => expect(where()).toBe('the dashboard'));
    expect({
      requests: [requests.signIns, requests.claims],
      alerts: alerts(),
      kept: keptAddress(),
      signedInAs: store.getState().auth.user?.email,
    }).toStrictEqual({
      requests: [1, 1],
      alerts: [],
      // The pool's next copy: the first is still somebody's, as far as the mock knows.
      kept: SECOND_COPY,
      signedInAs: SECOND_COPY,
    });
  });

  it('a copy that is gone, on a page where "Forget this copy" was pressed before: the one sentence, and nothing about forgetting', async () => {
    rememberAClaimedCopy();
    const { rerender } = await openSignInPage();
    await userEvent.click(forgetLink());
    const afterForget = forgetStatuses().map((region) => region.textContent);
    // Another tab claimed a copy since, and this tab draws its page again: it offers that copy.
    // The browser kept a password for it that the server no longer takes.
    rememberAClaimedCopy({ password: 'Wrong-Pass-1!' });
    rerender(<LoginHarness />);
    const offeredAgain = {
      continue: buttonsNamed(WORDS.continue).length,
      statuses: forgetStatuses().map((region) => region.textContent),
    };

    await userEvent.click(continueButton());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.copyGone]));
    expect({
      afterForget,
      offeredAgain,
      // The alert says what happened to this copy. The polite sentence was about the other one,
      // which was forgotten on request, and is not said a second time for this.
      statuses: forgetStatuses().map((region) => region.textContent),
      kept: keptAddress(),
    }).toStrictEqual({
      afterForget: [WORDS.forgotten],
      offeredAgain: { continue: 1, statuses: [''] },
      statuses: [''],
      kept: null,
    });
  });

  it("a wrong password typed into the form is still said in the form's words, and forgets nothing", async () => {
    const kept = rememberAClaimedCopy();
    const { requests } = await openSignInPage();
    await fillTheForm(kept.email, 'Wrong-Pass-1!');

    await userEvent.click(signInButton());

    // The same 401 as a copy that is gone. Which sentence, and whether the copy is forgotten,
    // goes by the control that sent the request.
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.wrongPassword]));
    expect({
      signIns: requests.signIns,
      kept: keptAddress(),
      continue: buttonsNamed(WORDS.continue).map((button) => button.disabled),
    }).toStrictEqual({ signIns: 1, kept: FIRST_COPY, continue: [false] });
  });

  it('"Continue with my copy" met by an outage or a failed connection: the sentence for it, and the copy is kept', async () => {
    rememberAClaimedCopy();
    const answers: Answer[] = [
      () => serviceUnavailable({ via: 'bff', instance: '/bff/auth/login' }),
      () => HttpResponse.error(),
    ];
    const { requests } = await openSignInPage({ signIn: () => answers.shift()?.() });

    await userEvent.click(continueButton());
    await waitFor(() => expect(alerts()).toStrictEqual([COPY.unavailable]));
    // Neither answer says anything about the copy: it is still the browser's, and still offered.
    // The outage's answer names a wait, and nothing is counted down for it, so nothing waits.
    expect({
      kept: keptAddress(),
      continue: disabledOf(WORDS.continue),
      timers: timers(),
    }).toStrictEqual({ kept: FIRST_COPY, continue: [false], timers: [] });

    await userEvent.click(continueButton());
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.connection]));

    expect({
      signIns: requests.signIns,
      kept: keptAddress(),
      continue: disabledOf(WORDS.continue),
      tryTheDemo: buttonsNamed(WORDS.tryTheDemo).length,
    }).toStrictEqual({ signIns: 2, kept: FIRST_COPY, continue: [false], tryTheDemo: 0 });
  });

  it('a locked copy: the existing banner and countdown; "Continue" waits; the form keeps "Sign in"', async () => {
    const kept = rememberAClaimedCopy();
    // Locked after too many wrong passwords. The kept password is the right one: the one answer
    // that says the lock (src/mocks/handlers.ts).
    mockState.loginLockedUntil[kept.email] = new Date(Date.now() + 30_000).toISOString();
    const { requests } = await openSignInPage();

    await userEvent.click(continueButton());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.locked]));
    expect({
      signIns: requests.signIns,
      timers: timers().map((words) => /^Try again in [1-3]?\ds$/.test(words ?? '')),
      aboveTheDemo: inOrder(
        document.querySelector('[role="alert"]'),
        screen.queryByRole('timer'),
        buttonsNamed(WORDS.continue)[0],
      ),
      // The lock is the kept copy's. "Continue with my copy" waits its countdown out; a new copy,
      // forgetting this one, and the form, which may be for another address, do not.
      continue: buttonsNamed(WORDS.continue).map((button) => ({
        disabled: button.disabled,
        spinners: spinnersIn(button),
      })),
      others: [disabledOf(WORDS.getNew), disabledOf(WORDS.forget)],
      signIn: disabledOf(WORDS.signIn),
      // Not forgotten: a locked copy is still the browser's copy.
      kept: keptAddress(),
    }).toStrictEqual({
      signIns: 1,
      timers: [true],
      aboveTheDemo: true,
      continue: [{ disabled: true, spinners: 0 }],
      others: [[false], [false]],
      signIn: [false],
      kept: FIRST_COPY,
    });
    expectNoNestedLiveRegions();
  });

  it('when the lock\'s countdown ends the banner goes and "Continue with my copy" can be pressed again', async () => {
    const kept = rememberAClaimedCopy();
    mockState.loginLockedUntil[kept.email] = new Date(Date.now() + 30_000).toISOString();
    await openSignInPage();
    installFakeClock();
    const pressedAt = Date.now();

    await fakeClockUser().click(continueButton());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.locked]));
    const whileItCounted = { timers: timers().length, disabled: disabledOf(WORDS.continue) };

    // The lock had 30 s left when the page opened.
    await advanceUntil(pressedAt, 31_500);

    await waitFor(() => expect(timers()).toStrictEqual([]));
    expect({
      whileItCounted,
      alerts: alerts(),
      disabled: disabledOf(WORDS.continue),
    }).toStrictEqual({
      whileItCounted: { timers: 1, disabled: [true] },
      alerts: [],
      disabled: [false],
    });
  });

  it('too many attempts on "Continue": the banner, and every control waits', async () => {
    rememberAClaimedCopy();
    const { requests } = await openSignInPage();
    // The mock's own limiter, with its budget spent (`AUTH_PERMIT_LIMIT` in src/mocks/handlers.ts,
    // ten): the sign-in is answered by the limiter before anybody looks at the pair.
    mockState.authCallTimes = Array.from({ length: 10 }, () => Date.now());

    await userEvent.click(continueButton());

    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.tooManyAttempts]));
    const banner = document.querySelector('[role="alert"]');
    expect({
      signIns: requests.signIns,
      timers: timers().length,
      aboveTheDemo: inOrder(banner, screen.queryByRole('timer'), buttonsNamed(WORDS.continue)[0]),
      inTheForm: theForm()?.contains(banner),
      // Every control of the page that sends a request, and the link beside them.
      disabled: [
        disabledOf(WORDS.continue),
        disabledOf(WORDS.getNew),
        disabledOf(WORDS.forget),
        disabledOf(WORDS.signIn),
      ],
      spinners: [...buttonsNamed(WORDS.continue), ...buttonsNamed(WORDS.signIn)].map(spinnersIn),
      kept: keptAddress(),
    }).toStrictEqual({
      signIns: 1,
      timers: 1,
      aboveTheDemo: true,
      inTheForm: false,
      disabled: [[true], [true], [true], [true]],
      spinners: [0, 0],
      kept: FIRST_COPY,
    });
  });

  it('a copy past its end is not offered, and is forgotten', async () => {
    // Ended a minute ago, by this browser's clock.
    rememberAClaimedCopy({ expiresAt: new Date(Date.now() - 60_000).toISOString() });
    const keptBefore = keptAddress();
    const { requests } = await openSignInPage();

    await waitFor(() => expect(keptAddress()).toBeNull());
    expect({
      keptBefore,
      continue: buttonsNamed(WORDS.continue).length,
      tryTheDemo: buttonsNamed(WORDS.tryTheDemo).map((button) => button.disabled),
      // Nothing is said about it: the page offers the demo as it does to a browser with no copy.
      alerts: alerts(),
      statuses: forgetStatuses().map((region) => region.textContent),
      requests: [requests.signIns, requests.claims],
    }).toStrictEqual({
      keptBefore: FIRST_COPY,
      continue: 0,
      tryTheDemo: [false],
      alerts: [],
      statuses: [''],
      requests: [0, 0],
    });
  });

  it('a copy whose end passes while the page is open is still offered: the end is looked at when the page opens', async () => {
    // Half a minute left, by this browser's clock, when the page opens.
    rememberAClaimedCopy({ expiresAt: new Date(Date.now() + 30_000).toISOString() });
    const { rerender } = await openSignInPage();
    installFakeClock();
    const openedAt = Date.now();
    const whenItOpened = buttonsNamed(WORDS.continue).length;

    await advanceUntil(openedAt, 45_000);
    rerender(<LoginHarness />);

    // Not taken from under the visitor: the server's answer to "Continue with my copy" is what
    // says a copy has ended, once the page has offered it.
    expect({
      whenItOpened,
      continue: buttonsNamed(WORDS.continue).map((button) => button.disabled),
      tryTheDemo: buttonsNamed(WORDS.tryTheDemo).length,
      kept: keptAddress(),
    }).toStrictEqual({ whenItOpened: 1, continue: [false], tryTheDemo: 0, kept: FIRST_COPY });
  });

  it('"Forget this copy": nothing is kept, the demo is offered, the page says so politely, and focus is on the button', async () => {
    rememberAClaimedCopy();
    const { requests, settled } = await openSignInPage();
    // In the block, empty, before the press: a polite region has to be on the page before its
    // words change, or the change is not read.
    const before = forgetStatuses();
    const saidBefore = before.map((region) => region.textContent);
    const keptBefore = keptAddress();

    await userEvent.click(forgetLink());
    await settled();

    const after = forgetStatuses();
    expect({
      saidBefore,
      keptBefore,
      kept: keptAddress(),
      continue: buttonsNamed(WORDS.continue).length,
      tryTheDemo: buttonsNamed(WORDS.tryTheDemo).map((button) => button.disabled),
      // The link went under the press. Focus is on the button that took the block's place, not
      // on the page.
      focusOnTryTheDemo: document.activeElement === buttonsNamed(WORDS.tryTheDemo)[0],
      // The region that was on the page before the press, not a new one in its place.
      sameRegion: after.length === 1 && after[0] === before[0],
      said: after.map((region) => region.textContent),
      alerts: alerts(),
      requests: [requests.signIns, requests.claims],
    }).toStrictEqual({
      saidBefore: [''],
      keptBefore: FIRST_COPY,
      kept: null,
      continue: 0,
      tryTheDemo: [false],
      focusOnTryTheDemo: true,
      sameRegion: true,
      said: [WORDS.forgotten],
      alerts: [],
      requests: [0, 0],
    });
    expectNoNestedLiveRegions();
  });

  it('after "Forget this copy", the page drawn again leaves focus where the visitor put it', async () => {
    rememberAClaimedCopy();
    await openSignInPage();
    await userEvent.click(forgetLink());
    const atTheForgetting = document.activeElement === buttonsNamed(WORDS.tryTheDemo)[0];

    // The visitor goes on to the form. The password's eye button draws the page again, for a
    // reason of its own: its name changes with the press, so the page was drawn again.
    await userEvent.click(screen.getByRole('button', { name: 'Show password' }));
    const focused = document.activeElement;

    expect({
      atTheForgetting,
      drawnAgain: buttonsNamed('Hide password').length,
      // Still said, and that it is said does not take the focus back to "Try the demo".
      said: forgetStatuses().map((region) => region.textContent),
      // By its name: the eye button's is a label, a button with words is named by them.
      focusOn: focused?.getAttribute('aria-label') ?? focused?.textContent,
    }).toStrictEqual({
      atTheForgetting: true,
      drawnAgain: 1,
      said: [WORDS.forgotten],
      focusOn: 'Hide password',
    });
  });

  it('"Get a new copy" opens the one dialog, and "Keep this copy" keeps the copy', async () => {
    rememberAClaimedCopy();
    const { requests, settled } = await openSignInPage();
    const keyBefore = localStorage.getItem(KEY);
    // A browser hands a disabled button's focus to the page; jsdom does not (src/test/outage.ts).
    // Nothing here waits, so it changes nothing: it is on so that focus is read as a browser
    // would leave it.
    const stopEmulating = emulateFocusFixup();
    try {
      await userEvent.click(getNewButton());
      await waitFor(() => expect(openDialogs()).toStrictEqual([WORDS.startOverTitle]));
      // Focus moved off the button when the dialog opened, so focus on the button afterwards is
      // a return, not a button that never lost it. That it moves is this test page's doing:
      // whether a browser moves it when the dialog opens is not something this test can see.
      const focusLeftTheButton = document.activeElement !== getNewButton();

      await userEvent.click(
        within(screen.getByRole('alertdialog')).getByRole('button', { name: WORDS.keepThisCopy }),
      );
      const afterKeepThisCopy = {
        dialogs: openDialogs(),
        focusOnGetANewCopy: document.activeElement === getNewButton(),
      };

      await userEvent.click(getNewButton());
      await waitFor(() => expect(openDialogs()).toStrictEqual([WORDS.startOverTitle]));
      await userEvent.keyboard('{Escape}');
      const afterEscape = {
        dialogs: openDialogs(),
        focusOnGetANewCopy: document.activeElement === getNewButton(),
      };
      await settled();

      expect({
        focusLeftTheButton,
        afterKeepThisCopy,
        afterEscape,
        claims: requests.claims,
        keyUnchanged: localStorage.getItem(KEY) === keyBefore,
        kept: keptAddress(),
        where: where(),
      }).toStrictEqual({
        focusLeftTheButton: true,
        afterKeepThisCopy: { dialogs: [], focusOnGetANewCopy: true },
        afterEscape: { dialogs: [], focusOnGetANewCopy: true },
        claims: 0,
        keyUnchanged: true,
        kept: FIRST_COPY,
        where: 'the sign-in page',
      });
    } finally {
      stopEmulating();
    }
  });

  it('starting over from here lands on the dashboard with the new copy', async () => {
    rememberAClaimedCopy();
    // Sent here by the guard on the way to another page. A new copy starts at its dashboard,
    // whichever button claimed it.
    const { requests, store, router } = await openSignInPage({
      state: { from: { pathname: '/transfer' } },
    });

    await userEvent.click(getNewButton());
    await waitFor(() => expect(openDialogs()).toStrictEqual([WORDS.startOverTitle]));
    // Inside the dialog: "Start over" is also the first words of its title.
    await userEvent.click(
      within(screen.getByRole('alertdialog')).getByRole('button', { name: WORDS.startOver }),
    );

    await waitFor(() => expect(where()).toBe('the dashboard'));
    const said = await screen.findAllByText(WORDS.newCopy);
    expect({
      claims: requests.claims,
      signIns: requests.signIns,
      said: said.length > 0,
      kept: keptAddress(),
      signedInAs: store.getState().auth.user?.email,
      landedBy: router.state.historyAction,
    }).toStrictEqual({
      claims: 1,
      signIns: 0,
      said: true,
      kept: SECOND_COPY,
      signedInAs: SECOND_COPY,
      landedBy: 'REPLACE',
    });
  });

  it('while "Continue with my copy" runs every other control waits, and only the pressed one says so', async () => {
    rememberAClaimedCopy();
    const { requests } = await openSignInPage({ signIn: never });
    installFakeClock();

    await fakeClockUser().click(continueButton());

    // Still found by its name while it waits: the spinner is beside the words, not in their
    // place.
    await waitFor(() =>
      expect(buttonsNamed(WORDS.continue).map((button) => button.disabled)).toStrictEqual([true]),
    );
    const shownAt = await hintShownAt();
    expect({
      signIns: requests.signIns,
      claims: requests.claims,
      pressed: { text: continueButton().textContent, spinners: spinnersIn(continueButton()) },
      others: [
        { disabled: getNewButton().disabled, spinners: spinnersIn(getNewButton()) },
        { disabled: forgetLink().disabled, spinners: spinnersIn(forgetLink()) },
      ],
      // Still named exactly "Sign in", so it kept its words: it was not the one pressed, although
      // the request is a sign-in.
      signIn: buttonsNamed(WORDS.signIn).map((button) => ({
        disabled: button.disabled,
        spinners: spinnersIn(button),
      })),
      hints: document.querySelectorAll('[data-wait-hint]').length,
    }).toStrictEqual({
      signIns: 1,
      claims: 0,
      pressed: { text: WORDS.continue, spinners: 1 },
      others: [
        { disabled: true, spinners: 0 },
        { disabled: true, spinners: 0 },
      ],
      signIn: [{ disabled: true, spinners: 0 }],
      hints: 1,
    });

    await advanceUntil(shownAt, 5_000);

    const region = hintRegion(COPY.slow);
    expect({
      said: screen.getAllByText(COPY.slow).length,
      // Under the buttons of the block it was pressed in, above the link, and not in the form.
      inTheBlock: demoBlock()?.contains(region),
      underTheButtons: inOrder(continueButton(), getNewButton(), region, forgetLink()),
      inTheForm: theForm()?.contains(region),
    }).toStrictEqual({ said: 1, inTheBlock: true, underTheButtons: true, inTheForm: false });
    expectNoNestedLiveRegions();

    // From 20 s the words of a write, and no way to stop waiting: a sign-in the server may
    // already be doing cannot be taken back by the page.
    await advanceUntil(shownAt, 20_000);
    hintRegion(COPY.stillTrying);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
  });

  it("while the form's sign-in runs the remembered copy waits", async () => {
    rememberAClaimedCopy();
    const { requests } = await openSignInPage({ signIn: never });
    await fillTheForm(NOBODY, 'Any-Pass-1!');
    const submit = signInButton();

    await userEvent.click(submit);

    await waitFor(() =>
      expect(buttonsNamed(WORDS.continue).map((button) => button.disabled)).toStrictEqual([true]),
    );
    const hints = Array.from(document.querySelectorAll('[data-wait-hint]'));
    expect({
      signIns: requests.signIns,
      // Disabled, and none spinning: they wait for a request the form sent.
      block: [
        { disabled: continueButton().disabled, spinners: spinnersIn(continueButton()) },
        { disabled: getNewButton().disabled, spinners: spinnersIn(getNewButton()) },
        { disabled: forgetLink().disabled, spinners: spinnersIn(forgetLink()) },
      ],
      submit: { disabled: submit.disabled, spinners: spinnersIn(submit) },
      // One hint, the form's own.
      hints: hints.map((hint) => theForm()?.contains(hint)),
    }).toStrictEqual({
      signIns: 1,
      block: [
        { disabled: true, spinners: 0 },
        { disabled: true, spinners: 0 },
        { disabled: true, spinners: 0 },
      ],
      submit: { disabled: true, spinners: 1 },
      hints: [true],
    });
  });

  it('a refused sign-in is no longer said while "Continue with my copy" runs', async () => {
    rememberAClaimedCopy();
    // The first sign-in goes on to the mock, which refuses it; the second never gets an answer.
    const answers: Answer[] = [() => undefined, never];
    await openSignInPage({ signIn: () => answers.shift()?.() });
    await fillTheForm(NOBODY, 'Wrong-Pass-1!');
    await userEvent.click(signInButton());
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.wrongPassword]));

    await userEvent.click(continueButton());

    await waitFor(() =>
      expect(buttonsNamed(WORDS.continue).map((button) => button.disabled)).toStrictEqual([true]),
    );
    expect(alerts()).toStrictEqual([]);
  });

  it('a refused claim is no longer said while the "Continue with my copy" that follows it runs', async () => {
    const { rerender } = await openSignInPage({ claim: poolEmpty, signIn: never });
    await userEvent.click(tryTheDemo());
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.poolEmpty]));
    // Another tab claimed a copy meanwhile, and this tab draws its page again: it now offers
    // that copy, under the sentence of its own refused claim.
    rememberAClaimedCopy();
    rerender(<LoginHarness />);
    expect({ alerts: alerts(), continue: buttonsNamed(WORDS.continue).length }).toStrictEqual({
      alerts: [WORDS.poolEmpty],
      continue: 1,
    });

    await userEvent.click(continueButton());

    // The sign-in is on its way and has no answer. The page reads the sign-in's refusal from the
    // press on, not from its answer on, and there is none to read yet.
    await waitFor(() =>
      expect(buttonsNamed(WORDS.continue).map((button) => button.disabled)).toStrictEqual([true]),
    );
    expect(alerts()).toStrictEqual([]);
  });

  it('a copy another tab forgot is not offered at the next render', async () => {
    rememberAClaimedCopy();
    const { rerender } = await openSignInPage();
    const before = {
      continue: buttonsNamed(WORDS.continue).length,
      tryTheDemo: buttonsNamed(WORDS.tryTheDemo).length,
    };

    // Another tab pressed "Forget this copy". This tab is told nothing; it finds out when it
    // draws its page again.
    localStorage.removeItem(KEY);
    rerender(<LoginHarness />);

    expect({
      before,
      continue: buttonsNamed(WORDS.continue).length,
      tryTheDemo: buttonsNamed(WORDS.tryTheDemo).length,
    }).toStrictEqual({ before: { continue: 1, tryTheDemo: 0 }, continue: 0, tryTheDemo: 1 });
  });

  it('a browser that refuses to be read still shows the page', async () => {
    rememberAClaimedCopy();
    // A browser with storage denied throws when storage is read. Nothing is written to
    // console.error for it: this suite fails any test that writes there (src/test/setup.ts).
    const reads = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('SecurityError');
    });
    try {
      await openSignInPage();

      expect({
        // The page asked for the key, and was refused: the demo it offers is its answer to that.
        askedForTheKey: reads.mock.calls.some(([key]) => key === KEY),
        where: where(),
        tryTheDemo: buttonsNamed(WORDS.tryTheDemo).map((button) => button.disabled),
        continue: buttonsNamed(WORDS.continue).length,
        signInButtons: buttonsNamed(WORDS.signIn).length,
        alerts: alerts(),
      }).toStrictEqual({
        askedForTheKey: true,
        where: 'the sign-in page',
        tryTheDemo: [false],
        continue: 0,
        signInButtons: 1,
        alerts: [],
      });
    } finally {
      reads.mockRestore();
    }
    // A read the browser refused is not a copy that could not be read: the key is as it was.
    expect(keptAddress()).toBe(FIRST_COPY);
  });
});
