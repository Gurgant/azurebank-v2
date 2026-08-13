import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
import { resetServerActivity } from '../features/auth/sessionActivity';
import { __resetStepUpController } from '../features/auth/stepUpController';
import { server } from '../mocks/server';
import { resetMockState, seedMockSession } from '../mocks/state';
import { installLayoutStubs } from './layout';
import {
  TEST_VIEWPORT_HEIGHT,
  TEST_VIEWPORT_WIDTH,
  matchMediaStub,
  resetMediaEnvironment,
} from './viewport';

// jsdom has no ResizeObserver; Fluent's MessageBar (reflow) requires one.
class ResizeObserverStub {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}
globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver;

// jsdom has no matchMedia either. `test/viewport.ts` supplies it, along with the two defaults this
// suite depends on and the setters that let one test opt out of either: 1024x768 (which is exactly
// the `lg` breakpoint, so every test renders the DESKTOP tree unless it says otherwise) and reduced
// motion reported as PREFERRED (which is what keeps Fluent's dialog transitions synchronous, and
// therefore what keeps role queries from missing buttons that are really there). The reasoning for
// both, and the flake each one prevents, lives beside the code that implements them.
window.innerWidth = TEST_VIEWPORT_WIDTH;
window.innerHeight = TEST_VIEWPORT_HEIGHT;
window.matchMedia ??= matchMediaStub;

// jsdom does no layout either, and unlike the two stubs above that is not a missing API — it is a
// WRONG ANSWER: `offsetParent === null` and a 0x0 rect are what a real browser reports for an
// element that is not rendered. Fluent's focus trap reads those and concludes an open dialog holds
// nothing focusable, which ends with tabster putting `aria-hidden="true"` on the open dialog 250ms
// later. `test/layout.ts` has the full chain, the measurements, and why a looser query would have
// been a lie rather than a fix.
installLayoutStubs();

/*
  The OTHER half of the timing budget (the vitest half lives in vitest.config.ts).

  `findBy*` / `waitFor` default to a 1000ms `asyncUtilTimeout`, and that is what produced nine of
  the twenty failures when the suite was run under CPU contention: "Unable to find role=..." on
  dialogs that simply had not mounted yet. Fluent's Dialog is not instant even unloaded — it
  mounts, portals, and settles focus — so a one-second budget was always thin, and contention made
  it insufficient.

  Five seconds, not more: it must stay comfortably UNDER the 20s test timeout so that a query which
  will never match fails as a clear "could not find X", not as an opaque "test timed out".
*/
configure({ asyncUtilTimeout: 5_000 });

/*
  CONSOLE.ERROR IS A TEST FAILURE — and the reason this exists is that for months it was not even
  VISIBLE.

  Vitest's default reporter prints console output only for FAILING tests. Every `console.error` a
  passing test emitted — React's "not wrapped in act(...)", key warnings, invalid-prop errors,
  anything an error boundary logs — went to a stream nobody read. `npm test` and CI both showed a
  clean 584-passing run.

  Measured on main d6ea72e with `--reporter=verbose`: 3,074 act(...) warnings across 21 tests in 5
  files. Not one had ever appeared in a gate. That number is the argument: a warning channel nobody
  can see is not a warning channel, and the fix is not "read the logs more carefully", it is to make
  the channel fail the build.

  Proven falsifiable rather than assumed: a throwaway test that emitted a bare console.error and an
  unacted setState was INVISIBLE under the default reporter and visible under the verbose one. The
  first measurement taken here reported zero warnings and was itself blind — the probe is what
  caught that, which is why the probe came before the conclusion.

  THE OPT-OUT IS ALREADY IDIOMATIC HERE. A test that legitimately provokes a logged error stubs it
  with `vi.spyOn(console, 'error').mockImplementation(() => {})` — the spy replaces this wrapper for
  that test, so nothing is recorded and nothing fails. `AppErrorBoundary.test.tsx` and
  `RouteError.test.tsx` already do exactly that, and already explain why. No test file changes to
  keep them passing; the escape hatch is the one they were using anyway.

  Scope is console.error alone, and the reason is sharper than "warn is noisier". Redux Toolkit's
  `ImmutableStateInvariantMiddleware` warns when a state check exceeds 32ms — a threshold on WALL
  CLOCK, so it fires or does not depending on how loaded the machine is. It was absent from four
  consecutive runs here and present on a fifth taken under contention. Gating `console.warn` would
  therefore have bought a flaky suite, which is the one thing worse than a quiet one. React reports
  correctness problems on `error`, and `error` is deterministic.
*/
const consoleErrors: string[] = [];
const forwardConsoleError = console.error.bind(console);

beforeEach(() => {
  consoleErrors.length = 0;
  console.error = (...args: unknown[]) => {
    consoleErrors.push(args.map(String).join(' '));
    forwardConsoleError(...args);
  };
});

/*
  The assertion half, and its placement is load-bearing in the opposite direction to the obvious one.

  Vitest's default is `sequence.hooks: 'stack'` — `afterEach` hooks run in REVERSE registration
  order. So this must be registered FIRST to run LAST. Measured, because the intuition is backwards
  and a comment asserting the wrong one is worse than none: instrumenting both hooks and printing
  the order gives `file-hook -> setup:assertion -> setup:teardown`, i.e. registering the assertion
  after the teardown ran it BEFORE the teardown.

  It matters because this hook THROWS, and a throwing hook skips the ones still to run. Registered
  the wrong way round, a test that failed this check would skip the whole teardown below — MSW
  handlers, mock state, the module-level session and step-up mirrors, the viewport — and hand all of
  it to the next test. `hook-order.test.ts` pins the ordering against exactly that.

  Restores before it throws, so a failure here cannot leave the next test with a recording console.
*/
afterEach(() => {
  console.error = forwardConsoleError;
  if (consoleErrors.length === 0) return;

  const [first] = consoleErrors;
  const extra = consoleErrors.length - 1;
  throw new Error(
    `This test wrote ${consoleErrors.length} console.error message(s). ` +
      `React reports act(...) violations, invalid props and boundary-caught errors on this channel, ` +
      `so a passing test that writes to it is passing for a reason it has not stated. ` +
      `Fix the cause, or — if the error is the point — stub it with ` +
      `vi.spyOn(console, 'error').mockImplementation(() => {}) and say why.

First message:
` +
      first +
      (extra > 0
        ? `

(+${extra} more)`
        : ''),
  );
});

// MSW lifecycle: every unhandled request is an ERROR — tests must declare the traffic they
// cause, so a missing handler is a broken contract, never a silent pass.
beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));

/*
  SIGNED IN is the default state, because for these routes it is the only reachable one.

  `/api/*` is now gated on a live session in the mock, matching the real stack, where an anonymous
  proxied request comes back 401 AUTH_TOKEN_MISSING. Every page behind `ProtectedRoute` therefore
  needs a session to render anything at all — and the previous default (`session: null`, "tests seed
  or log in explicitly") made 159 tests across 16 files exercise a state the product cannot produce.

  Seeding here rather than in each file keeps the default honest: a test that wants the anonymous
  case sets `mockState.session = null` itself and says why, which is the rarer and more interesting
  claim of the two. `resetMockState` in the afterEach below nulls it again, so this runs fresh for
  every test.
*/
beforeEach(() => seedMockSession());
afterEach(() => {
  /*
    UNMOUNT FIRST — explicitly, rather than leaning on Testing Library's auto-cleanup.

    Auto-cleanup registers its own `afterEach` when a test file imports `@testing-library/react`,
    which is after this file. Under `stack` that makes it run BEFORE this hook, which is the order
    we want — but it is a property of a default nothing here controls, and every reset below depends
    on nothing being mounted: `resetMediaEnvironment` notifies live MediaQueryLists, `useMediaQuery`
    subscribes through `useSyncExternalStore`, and a re-render from here lands outside `act(...)`.
    Unmounting explicitly makes that independent of the setting. `cleanup()` is idempotent, so the
    later auto-cleanup becomes a no-op either way.
  */
  cleanup();
  server.resetHandlers();
  resetMockState();
  // Module-level client mirror of LastActivity — never let it leak between tests.
  resetServerActivity();
  // Module-level step-up bridge (mirrors mockState.authLevel reset) — no inflight leak.
  __resetStepUpController();
  // A test that narrowed the viewport must not hand the next one a phone, and one that turned
  // animations back on must not hand it the flake the stub exists to prevent.
  //
  // No `act()` around it, and that is a consequence of the `cleanup()` above rather than an
  // oversight: this NOTIFIES, so it would need an act scope if anything were still mounted —
  // exactly as `viewport.ts` says at its own call site. Nothing is, by the time this runs.
  resetMediaEnvironment();
});
afterAll(() => server.close());
