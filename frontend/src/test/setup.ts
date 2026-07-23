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
const matchMediaStub = (query: string): MediaQueryList =>
  ({
    // Strictly the REDUCE query — a bare 'prefers-reduced-motion' would also match
    // '(prefers-reduced-motion: no-preference)' and lie to both-mode checks.
    matches: query.includes('prefers-reduced-motion: reduce'),
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
