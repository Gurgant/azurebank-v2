import { useMediaQuery } from './useResponsive';

/**
 * Whether the reader has asked the operating system for less motion.
 *
 * Until now this preference existed in the codebase only as a matchMedia stub in the test setup —
 * the suite reported `reduce` so Fluent would skip dialog animations and keep assertions
 * deterministic. Nothing consulted it at runtime, so a real reader who had set the preference still
 * got every animation.
 *
 * The query is written strictly. A bare `(prefers-reduced-motion)` also matches
 * `no-preference`, which would report "reduce me" to everyone.
 */
export function useReducedMotion(): boolean {
  return useMediaQuery('(prefers-reduced-motion: reduce)');
}
