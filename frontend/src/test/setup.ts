import '@testing-library/jest-dom/vitest';
import { resetServerActivity } from '../features/auth/sessionActivity';
import { __resetStepUpController } from '../features/auth/stepUpController';
import { server } from '../mocks/server';
import { resetMockState } from '../mocks/state';
import {
  TEST_VIEWPORT_HEIGHT,
  TEST_VIEWPORT_WIDTH,
  matchMediaStub,
  resetViewport,
} from './viewport';

// jsdom has no ResizeObserver; Fluent's MessageBar (reflow) requires one.
class ResizeObserverStub {
  observe(): void {}
  unobserve(): void {}
  disconnect(): void {}
}
globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver;

// jsdom has no matchMedia either — and WITHOUT it Fluent's useIsReducedMotion silently keeps
// animations ENABLED. On a starved CI runner a dialog's open transition can then stall for
// seconds with the surface still aria-hidden, so role queries miss buttons that are really
// there (the PR #34 PIN-modal Cancel saga). Report reduced motion as PREFERRED so Fluent
// skips its animations entirely: dialog enter/exit becomes synchronous and deterministic on
// any runner. Every other query reports no match (jsdom-neutral). NOTE: skipping animations
// also makes modal EXIT commit sooner — queries that follow an async transition must be
// findBy*/waitFor, never a bare getBy* (the P1.9 sweep).
// jsdom's default viewport is 1024x768 and nothing stated it, which matters more than it looks:
// 1024 is exactly the `lg` breakpoint, so `width >= lg` is true and every test rendered the DESKTOP
// tree. Pinning it makes that a decision instead of a coincidence — and `test/viewport.ts` now makes
// the other half true as well, because a test that wants the narrow tree can call
// `setViewportWidth` and have mounted components actually react to it.
window.innerWidth = TEST_VIEWPORT_WIDTH;
window.innerHeight = TEST_VIEWPORT_HEIGHT;
window.matchMedia ??= matchMediaStub;

// MSW lifecycle: every unhandled request is an ERROR — tests must declare the traffic they
// cause, so a missing handler is a broken contract, never a silent pass.
beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => {
  server.resetHandlers();
  resetMockState();
  // Module-level client mirror of LastActivity — never let it leak between tests.
  resetServerActivity();
  // Module-level step-up bridge (mirrors mockState.authLevel reset) — no inflight leak.
  __resetStepUpController();
  // A test that narrowed the viewport must not hand the next one a phone.
  resetViewport();
});
afterAll(() => server.close());
