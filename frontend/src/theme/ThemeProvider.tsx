import { useMemo, type ReactNode } from 'react';
import { createDOMRenderer, FluentProvider, RendererProvider } from '@fluentui/react-components';
import { azureBankDarkTheme, azureBankLightTheme } from './fluentTheme';
import { compareMinWidthMediaQueries } from './mediaQueryOrder';
import { ThemeContext } from './themeContext';
import { useThemePreference } from './themePreference';

/**
 * One renderer for the app, created once at module scope.
 *
 * It exists only to carry `compareMediaQueries` — see `mediaQueryOrder.ts` for what Griffel's
 * default does and why a screen shipped for weeks with a breakpoint that matched and lost. Created
 * here rather than inside the component because a renderer owns the `<style>` elements it inserts:
 * a new one per render would re-insert the whole stylesheet.
 *
 * The document guard mirrors Griffel's own default parameter, so importing this module under a
 * non-DOM runtime stays harmless.
 */
const renderer = createDOMRenderer(typeof document === 'undefined' ? undefined : document, {
  compareMediaQueries: compareMinWidthMediaQueries,
});

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

  // `RendererProvider` sits OUTSIDE `FluentProvider` on purpose: Fluent's own components render
  // through Griffel too, so a renderer installed underneath it would leave half the stylesheet
  // ordered by the default comparator.
  return (
    <ThemeContext.Provider value={value}>
      <RendererProvider renderer={renderer}>
        <FluentProvider theme={resolved === 'dark' ? azureBankDarkTheme : azureBankLightTheme}>
          {children}
        </FluentProvider>
      </RendererProvider>
    </ThemeContext.Provider>
  );
}
