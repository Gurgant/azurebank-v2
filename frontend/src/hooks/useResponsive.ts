import { useCallback, useMemo, useSyncExternalStore } from 'react';
import { media } from '../theme/breakpoints';

/**
 * Viewport flags, derived from matchMedia.
 *
 * This used to hold three pieces of state, seed them to `isDesktop: true`, and correct them in an
 * effect. On a phone that meant render #1 produced the full desktop shell — 240px sidebar, centred
 * 1200px content — which then unmounted on render #2, remounting every child underneath it. One
 * real frame of the wrong layout, plus a subtree remount, on every load.
 *
 * `useSyncExternalStore` returns the true value on the first render, so there is no false frame and
 * no cascade. The hook it delegates to was already in this file, already correct, and used by
 * nothing.
 */
export function useResponsive() {
  const isDesktop = useMediaQuery(media.lg);
  const isTablet = useMediaQuery(media.md) && !isDesktop;

  return {
    isMobile: !isDesktop && !isTablet,
    isTablet,
    isDesktop,
    isTouch: !isDesktop,
  };
}

/**
 * Hook for custom media query matching.
 * useSyncExternalStore is the canonical pattern for subscribing to matchMedia: the first
 * render already returns the real match (no false-then-update flash), and there is no
 * setState-inside-effect to cascade renders.
 */
export function useMediaQuery(query: string): boolean {
  // One MediaQueryList per query: the snapshot selector runs on every render, and
  // re-parsing the query through window.matchMedia each time is avoidable work.
  const mediaQuery = useMemo(() => window.matchMedia(query), [query]);

  const subscribe = useCallback(
    (onStoreChange: () => void) => {
      mediaQuery.addEventListener('change', onStoreChange);
      return () => mediaQuery.removeEventListener('change', onStoreChange);
    },
    [mediaQuery],
  );

  return useSyncExternalStore(subscribe, () => mediaQuery.matches);
}

export default useResponsive;
