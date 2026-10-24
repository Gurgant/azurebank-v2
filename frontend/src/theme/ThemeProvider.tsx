import { useMemo, type ReactNode } from 'react';
import { createDOMRenderer, FluentProvider, RendererProvider } from '@fluentui/react-components';
import { useDisabledButtonEdge } from './disabledButtonEdge';
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
 * The app's own step after the library's styles, one component a key. One object, made once and
 * not at each render. `Button` alone has a step: a disabled button keeps an edge
 * (`disabledButtonEdge.ts` has the measurements and why the theme objects could not carry it).
 */
const customStyleHooks = { useButtonStyles_unstable: useDisabledButtonEdge };

/*
  The provider paints the page's background (`colorNeutralBackground1`). Without a height it would
  end where the content ends and leave the browser's canvas under a page shorter than the window,
  such as About when signed out. `html`, `body` and `#root` are 100% tall (`index.css`), so 100%
  here is the window's height, and a taller page still grows the provider with it.

  An inline style and not a class, on purpose. Fluent hands the provider's className to every
  portal (`applyStylesToPortals` is on by default), and a portal's mount node sits at the top of
  the window, above everything (z-index 1000000), with the provider's background. With the height
  in that class, every dialog, menu and toast laid one flat colour over the whole page. The style
  prop reaches the provider's own element only. React writes it through the CSSOM, which the CSP's
  `style-src` does not govern. One object, made once and not at each render.
*/
const providerStyle = { minHeight: '100%' };

/**
 * Owns the preference and hands Fluent the matching theme.
 *
 * The document's theme IS written on mount, and this used to say the opposite. The old reasoning —
 * that `public/theme-init.js` has already done it, so repeating the write would make the script
 * look optional — confused a documentation worry with a correctness one. The script stays
 * load-bearing because it runs BEFORE the paint; the mount write is a `useLayoutEffect` in
 * `useThemePreference` that costs one idempotent write and closes the case where the two disagree:
 * the script 404s, another tab wrote the preference in between, or nothing ran it at all. Without
 * it Fluent takes the stored preference while `<html>` keeps the old theme, and the app renders two
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
        <FluentProvider
          style={providerStyle}
          theme={resolved === 'dark' ? azureBankDarkTheme : azureBankLightTheme}
          customStyleHooks_unstable={customStyleHooks}
        >
          {children}
        </FluentProvider>
      </RendererProvider>
    </ThemeContext.Provider>
  );
}
