import { useMemo, type ReactNode } from 'react';
import { FluentProvider } from '@fluentui/react-components';
import { azureBankDarkTheme, azureBankLightTheme } from './fluentTheme';
import { ThemeContext } from './themeContext';
import { useThemePreference } from './themePreference';

/**
 * Owns the preference and hands Fluent the matching theme.
 *
 * The attribute IS written on mount, and this used to say the opposite. The old reasoning — that
 * `public/theme-init.js` has already done it, so repeating the write would make the script look
 * optional — confused a documentation worry with a correctness one. The script stays load-bearing
 * because it runs BEFORE the paint; the mount write is a `useLayoutEffect` in `useThemePreference`
 * that costs one idempotent `setAttribute` and closes the case where the two disagree: the script
 * 404s, another tab wrote the preference in between, or nothing ran it at all. Without it Fluent
 * takes the stored preference while `<html>` keeps the old attribute, and the app renders two
 * themes at once with nothing to log.
 *
 * The two themes are the same objects `fluentTheme.ts` has exported since U1; dark has simply been
 * waiting for the palette underneath it to be theme-aware.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const { preference, resolved, setPreference } = useThemePreference();

  const value = useMemo(
    () => ({ preference, resolved, setPreference }),
    [preference, resolved, setPreference],
  );

  return (
    <ThemeContext.Provider value={value}>
      <FluentProvider theme={resolved === 'dark' ? azureBankDarkTheme : azureBankLightTheme}>
        {children}
      </FluentProvider>
    </ThemeContext.Provider>
  );
}
