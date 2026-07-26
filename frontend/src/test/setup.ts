import '@testing-library/jest-dom/vitest';
import { resetServerActivity } from '../features/auth/sessionActivity';
import { __resetStepUpController } from '../features/auth/stepUpController';
import { server } from '../mocks/server';
import { resetMockState } from '../mocks/state';

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
// jsdom's default viewport is 1024x768 and nothing states it, which matters more than it looks:
// 1024 is exactly the `lg` breakpoint, so `width >= lg` is true and every test has always rendered
// the DESKTOP tree. The mobile tree has never been exercised by a single test. Pinning the value
// here does not change that — it makes it a decision instead of a coincidence, so a test that wants
// the narrow tree can say so by setting this and firing a resize.
const TEST_VIEWPORT_WIDTH = 1024;
window.innerWidth = TEST_VIEWPORT_WIDTH;
window.innerHeight = 768;

/**
 * Resolve a width condition against the pinned viewport.
 *
 * Only min-width and max-width are handled, which is enough: the app's breakpoint module is
 * min-width only by design. Anything else falls through to "no match", the jsdom-neutral answer.
 */
const matchesWidth = (query: string): boolean => {
  const conditions = [...query.matchAll(/\((min|max)-width:\s*(\d+(?:\.\d+)?)px\)/g)];
  if (conditions.length === 0) return false;
  // Multiple conditions in one query string are ANDed — the same way CSS reads `and`.
  return conditions.every(([, kind, px]) =>
    kind === 'min' ? window.innerWidth >= Number(px) : window.innerWidth <= Number(px),
  );
};

const matchMediaStub = (query: string): MediaQueryList =>
  ({
    // Strictly the REDUCE query — a bare 'prefers-reduced-motion' would also match
    // '(prefers-reduced-motion: no-preference)' and lie to both-mode checks.
    matches: query.includes('prefers-reduced-motion: reduce') || matchesWidth(query),
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  }) as unknown as MediaQueryList;
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
});
afterAll(() => server.close());
