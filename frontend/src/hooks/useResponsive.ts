import { useState, useEffect } from 'react';
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
 * Hook for custom media query matching
 */
export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(false);

  useEffect(() => {
    const mediaQuery = window.matchMedia(query);
    setMatches(mediaQuery.matches);

    const handler = (event: MediaQueryListEvent) => {
      setMatches(event.matches);
    };

    mediaQuery.addEventListener('change', handler);
    return () => mediaQuery.removeEventListener('change', handler);
  }, [query]);

  return matches;
}

export default useResponsive;
