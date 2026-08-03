import '@testing-library/jest-dom/vitest';
import { resetServerActivity } from '../features/auth/sessionActivity';
import { __resetStepUpController } from '../features/auth/stepUpController';
import { server } from '../mocks/server';
import { resetMockState, seedMockSession } from '../mocks/state';
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
  server.resetHandlers();
  resetMockState();
  // Module-level client mirror of LastActivity — never let it leak between tests.
  resetServerActivity();
  // Module-level step-up bridge (mirrors mockState.authLevel reset) — no inflight leak.
  __resetStepUpController();
  // A test that narrowed the viewport must not hand the next one a phone, and one that turned
  // animations back on must not hand it the flake the stub exists to prevent.
  resetMediaEnvironment();
});
afterAll(() => server.close());
