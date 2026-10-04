import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppToaster } from '../components/feedback';
import { AuthBootstrap } from '../features/auth';
import { problem, serviceUnavailable } from '../mocks/problem';
import { server } from '../mocks/server';
import { MOCK_PASSWORD, MOCK_USER, mockState, seedMockDemoCopy } from '../mocks/state';
import { enableDemoMode } from '../test/demoMode';
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
import { renderWithProviders } from '../test/renderWithProviders';
import { LoginPage } from './LoginPage';

/*
  The sign-in page: without the demo's tag, the page it was; with it, the demo first.

  On the demo the page leads with one button, "Try the demo", which claims a private copy and
  lands on the dashboard, and the form is still there under it for whoever has a copy's email and
  password. These tests are about a browser that keeps no copy: what the page offers, what a press
  of the button sends and does, what waits while a request runs, and what the page says for each
  answer a claim can get.

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
} as const;

const CLAIM = '*/bff/auth/demo/claim';
const SIGN_IN = '*/bff/auth/login';
const ME = '*/bff/auth/me';
const KEY = 'azurebank.demoCopy';
/** The first copy of the mock's pool (src/mocks/state.ts): the one a first claim hands out. */
const FIRST_COPY = 'demo-k7m2x9q4w8e1r5t3@azurebank.example';
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
  const requests = { whoIsSignedIn: 0, claims: 0, signIns: 0 };
  server.use(
    http.get(ME, () => {
      requests.whoIsSignedIn += 1;
    }),
    http.post(CLAIM, () => {
      requests.claims += 1;
      return claim?.();
    }),
    http.post(SIGN_IN, () => {
      requests.signIns += 1;
      return signIn?.();
    }),
  );
  const view = renderWithProviders(<LoginHarness />, {
    routerEntries: [{ pathname: '/login', state }],
  });
  await waitFor(() => expect(view.store.getState().auth.status).toBe('anonymous'));
  return { ...view, requests };
}

const tryTheDemo = () =>
  screen.getByRole('button', { name: WORDS.tryTheDemo }) as HTMLButtonElement;
const signInButton = () => screen.getByRole('button', { name: WORDS.signIn }) as HTMLButtonElement;
const theForm = () => document.querySelector('form');

/** Every button on the page with exactly that name: none is an answer, not an error. */
const buttonsNamed = (name: string) =>
  screen.queryAllByRole('button', { name }) as HTMLButtonElement[];

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
    }).toStrictEqual({
      subtitle: WORDS.subtitleOffTheDemo,
      createAccount: ['/register'],
      tryTheDemo: 0,
      notice: 0,
      aboveTheForm: 0,
      dividers: 1,
      order: true,
    });
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

  it('no live region sits inside another', async () => {
    await openSignInPage({ claim: poolEmpty });

    await userEvent.click(tryTheDemo());
    // With a refusal showing. The page's other live regions, the wait's hint and the countdown,
    // are looked at where they speak: in the tests of a slow claim and of the limiter above.
    await waitFor(() => expect(alerts()).toStrictEqual([WORDS.poolEmpty]));

    expectNoNestedLiveRegions();
  });
});
