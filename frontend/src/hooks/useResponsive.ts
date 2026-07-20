import { useState, useEffect, useCallback, useMemo, useSyncExternalStore } from 'react';
import { breakpoints } from '../theme/tokens';

/**
 * Hook for responsive design - detects current viewport size
 */
export function useResponsive() {
  const [isMobile, setIsMobile] = useState(false);
  const [isTablet, setIsTablet] = useState(false);
  const [isDesktop, setIsDesktop] = useState(true);

  useEffect(() => {
    const checkBreakpoints = () => {
      const width = window.innerWidth;
      setIsMobile(width < breakpoints.tablet);
      setIsTablet(width >= breakpoints.tablet && width < breakpoints.desktop);
      setIsDesktop(width >= breakpoints.desktop);
    };

    // Check on mount
    checkBreakpoints();

    // Listen for resize events
    window.addEventListener('resize', checkBreakpoints);

    return () => window.removeEventListener('resize', checkBreakpoints);
  }, []);

  return {
    isMobile,
    isTablet,
    isDesktop,
    isTouch: isMobile || isTablet,
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
