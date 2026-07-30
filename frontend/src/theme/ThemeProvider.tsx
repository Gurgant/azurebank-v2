import { useMemo, type ReactNode } from 'react';
import { FluentProvider } from '@fluentui/react-components';
import { azureBankDarkTheme, azureBankLightTheme } from './fluentTheme';
import { ThemeContext } from './themeContext';
import { useThemePreference } from './themePreference';

/**
 * Owns the preference and hands Fluent the matching theme.
 *
 * It does NOT set the `data-theme` attribute on mount, and that omission is deliberate:
 * `public/theme-init.js` has already done it before the first paint, and repeating the write here
 * would make the pre-paint script look optional. If this were the only writer, every load would
 * paint light first and correct itself — the flash the script exists to prevent. The attribute is
 * written again only when the preference CHANGES, which is the one moment the script cannot cover.
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
