import { createContext, useContext } from 'react';
import type { ResolvedTheme, ThemePreference } from './themePreference';

/**
 * The preference, shared.
 *
 * Only ONE thing genuinely needs React to know the theme: `FluentProvider`, which takes a theme
 * OBJECT rather than reading CSS. Everything else in the app is already themed by custom properties
 * on `<html>`, which change without a render. So this context exists to connect exactly two points —
 * the Settings toggle that sets the preference and the provider at the root that must rebuild — and
 * deliberately not to become the place components ask "am I in dark mode".
 *
 * Split from `ThemeProvider.tsx` because `react-refresh/only-export-components` is an error here: a
 * `.tsx` file exports components or it exports helpers, never both.
 */
export interface ThemeContextValue {
  preference: ThemePreference;
  resolved: ResolvedTheme;
  setPreference: (next: ThemePreference) => void;
}

export const ThemeContext = createContext<ThemeContextValue | null>(null);

/**
 * Throws rather than returning a default, and the reason is the failure it prevents: a silent
 * fallback would let a toggle render, respond to clicks and change nothing at all, which looks
 * exactly like a working feature.
 */
export function useTheme(): ThemeContextValue {
  const value = useContext(ThemeContext);
  if (value === null) {
    throw new Error('useTheme must be used inside <ThemeProvider>');
  }
  return value;
}
