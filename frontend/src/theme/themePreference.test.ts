import { readFileSync } from 'node:fs';
import { createElement } from 'react';
import { Portal } from '@fluentui/react-components';
import { act, render, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ThemeProvider } from './ThemeProvider';
import {
  DARK_MEDIA_QUERY,
  THEME_STORAGE_KEY,
  applyTheme,
  readStoredPreference,
  resolveTheme,
  useThemePreference,
} from './themePreference';
import { pageStyleRules } from '../test/styleRules';

/**
 * Two things are pinned here, and the second is the one nothing else could catch.
 *
 * The first is ordinary: a preference resolves to a theme, survives a reload, and does not take the
 * page down when `localStorage` throws — which it does in some privacy modes, on the very first call.
 *
 * The second is the duplication `public/theme-init.js` is forced into. That script cannot import
 * anything, because it has to run before the module graph exists; it therefore repeats the storage
 * key, the media query and the default by hand. Nothing links the two copies, so the failure is
 * silent and specific: change the key here and the pre-paint script keeps reading the OLD one, so
 * every load paints light and the app corrects itself a frame later. That is precisely the flash the
 * script was added to prevent, and it would look like a rendering quirk rather than a bug.
 */

const THEME_INIT = readFileSync('public/theme-init.js', 'utf8');

/**
 * Query-AWARE on purpose. A stub that answered `matches` to anything would let the pre-paint script
 * ask for a media query nobody supports and still pass every test below — which is how the string
 * comparison these replaced used to guard the query, and the guard has to survive the rewrite.
 */
function stubMatchMedia(prefersDark: boolean) {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({
      matches: prefersDark && query === DARK_MEDIA_QUERY,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    })),
  );
}

/** The colour scheme declared on `<html>`, which the browser draws its own parts from. */
const declaredScheme = () => document.documentElement.style.getPropertyValue('color-scheme');

describe('theme preference', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    document.documentElement.removeAttribute('style');
  });

  it('treats anything it does not recognise as "follow the system"', () => {
    expect(readStoredPreference()).toBe('system');
    localStorage.setItem(THEME_STORAGE_KEY, 'lightish');
    // Not a crash and not a coin flip: an unreadable value from an older build must land on the
    // option that respects the user's machine.
    expect(readStoredPreference()).toBe('system');
  });

  it('resolves an explicit choice without consulting the system at all', () => {
    stubMatchMedia(true);
    expect(resolveTheme('light')).toBe('light');
    expect(resolveTheme('dark')).toBe('dark');
  });

  it('resolves "system" by asking the system', () => {
    stubMatchMedia(true);
    expect(resolveTheme('system')).toBe('dark');
    stubMatchMedia(false);
    expect(resolveTheme('system')).toBe('light');
  });

  it('writes the attribute and the storage together, so a reload cannot disagree', () => {
    stubMatchMedia(false);
    expect(applyTheme('dark')).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(declaredScheme()).toBe('dark');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
  });

  /*
    The attribute drives the app's colours and the scheme the browser's own parts: the scroll bars,
    its default focus ring, a select's list. Every preference, on either kind of device, sets the
    two to the same value, or those parts stay light on a dark page.
  */
  it('declares the colour scheme with the attribute, for every preference', () => {
    stubMatchMedia(false);
    for (const [preference, expected] of [
      ['dark', 'dark'],
      ['light', 'light'],
      ['system', 'light'],
    ] as const) {
      expect(applyTheme(preference)).toBe(expected);
      expect(document.documentElement.getAttribute('data-theme')).toBe(expected);
      expect(declaredScheme()).toBe(expected);
    }

    stubMatchMedia(true);
    expect(applyTheme('system')).toBe('dark');
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(declaredScheme()).toBe('dark');
  });

  it('still switches when storage is denied', () => {
    stubMatchMedia(false);
    const denied = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('SecurityError');
    });

    // The choice must hold for this page even when it cannot be remembered for the next one.
    // Refusing to switch would be the strictly worse failure.
    expect(() => applyTheme('dark')).not.toThrow();
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(declaredScheme()).toBe('dark');
    denied.mockRestore();
  });

  describe('the hook', () => {
    it('makes the document agree with the stored preference on mount, scheme included', () => {
      // What a page loaded before the preference was stored would still carry.
      document.documentElement.setAttribute('data-theme', 'light');
      document.documentElement.style.setProperty('color-scheme', 'light');
      localStorage.setItem(THEME_STORAGE_KEY, 'dark');

      renderHook(() => useThemePreference());

      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
      expect(declaredScheme()).toBe('dark');
    });

    it('moves the scheme on a switch both ways, and with the device while on "system"', () => {
      let deviceDark = false;
      const onDeviceChange = new Set<() => void>();
      vi.stubGlobal(
        'matchMedia',
        vi.fn((query: string) => ({
          matches: deviceDark && query === DARK_MEDIA_QUERY,
          addEventListener: (_type: string, listener: () => void) => onDeviceChange.add(listener),
          removeEventListener: (_type: string, listener: () => void) =>
            onDeviceChange.delete(listener),
        })),
      );
      const { result } = renderHook(() => useThemePreference());
      expect(declaredScheme()).toBe('light');

      act(() => result.current.setPreference('dark'));
      expect(declaredScheme()).toBe('dark');
      act(() => result.current.setPreference('light'));
      expect(declaredScheme()).toBe('light');

      act(() => result.current.setPreference('system'));
      deviceDark = true;
      expect(onDeviceChange.size).toBe(1);
      act(() => onDeviceChange.forEach((listener) => listener()));
      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
      expect(declaredScheme()).toBe('dark');
    });
  });

  /**
   * The script is EXECUTED here, not read.
   *
   * The first version of these matched strings — the key, the query, the word `catch` — and that is
   * exactly the kind of test that agrees with the code without agreeing with reality. It passed
   * against a script whose single `try` block sent a denied `localStorage` straight to the light
   * fallback, never asking the system at all: the script chose light, React chose dark, and the two
   * disagreed in precisely the case the script exists for. Running it makes that a failure.
   */
  /**
   * A SOURCE assertion, and the only honest kind available here.
   *
   * The mount sync must run before the browser paints, or a corrected theme arrives as a visible
   * flicker — the exact defect the pre-paint script exists to avoid. jsdom never paints, so
   * `useEffect` and `useLayoutEffect` are indistinguishable to every behavioural test in this repo:
   * swapping them leaves all seven settings tests green. Rather than leave the choice unguarded or
   * pretend a render test covers it, the reason is written down next to the check.
   */
  it('syncs the document BEFORE paint, which no behavioural test here can observe', () => {
    const source = readFileSync('src/theme/themePreference.ts', 'utf8');
    expect(source).toMatch(/useLayoutEffect\(\(\) => \{\s*writeDocumentTheme\(resolved\);/);
  });

  describe('the pre-paint script', () => {
    const runThemeInit = () => new Function(THEME_INIT)();

    it('honours a stored choice', () => {
      stubMatchMedia(false);
      localStorage.setItem(THEME_STORAGE_KEY, 'dark');

      runThemeInit();

      // Also the drift guard: a script reading a different key would leave this at light.
      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
      expect(declaredScheme()).toBe('dark');
    });

    it('follows the system when nothing is stored', () => {
      stubMatchMedia(true);

      runThemeInit();

      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
      expect(declaredScheme()).toBe('dark');
    });

    it('an explicit light choice beats a dark system', () => {
      stubMatchMedia(true);
      localStorage.setItem(THEME_STORAGE_KEY, 'light');

      runThemeInit();

      expect(document.documentElement.getAttribute('data-theme')).toBe('light');
      expect(declaredScheme()).toBe('light');
    });

    it('still asks the system when storage is denied — the case the module also covers', () => {
      stubMatchMedia(true);
      const denied = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
        throw new Error('SecurityError');
      });

      runThemeInit();

      // The module's own path: readStoredPreference() catches, returns 'system', and resolveTheme
      // then queries. If the script short-circuits to light here, every private-browsing load
      // flashes.
      expect(resolveTheme(readStoredPreference())).toBe('dark');
      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
      expect(declaredScheme()).toBe('dark');
      denied.mockRestore();
    });

    it('does not take the page down when there is no matchMedia at all', () => {
      vi.stubGlobal('matchMedia', undefined);

      expect(() => runThemeInit()).not.toThrow();
      // Light is the safe default: a failure to detect should look like the app before dark existed.
      expect(document.documentElement.getAttribute('data-theme')).toBe('light');
      expect(declaredScheme()).toBe('light');
    });
  });

  /*
    The provider paints the page's background, so it must be at least as tall as the window, or
    the browser's canvas shows under a short page (About, signed out: 685 of 900 px were the app's
    in Chromium, the rest white in the dark theme). jsdom lays nothing out and its
    `getComputedStyle` does not follow the classes Griffel writes, so these read what the element
    declares: its inline style and the rules in the page's style elements that name one of its
    classes. The height itself was measured in Chromium.

    The height must stay on the provider's own element. Fluent hands the provider's className to
    every portal (`applyStylesToPortals` is on by default), and a portal's mount node is fixed to
    the top of the window above everything at z-index 1000000, painted with the provider's
    background. Given the height through that className, every dialog, menu and toast covered the
    whole page with one flat colour.

    Inside the outer `describe` so that its `afterEach` clears the theme the provider writes on
    `<html>`.
  */
  describe('the theme provider', () => {
    function declaredMinHeights(element: Element): string[] {
      const classes = new Set(Array.from(element.classList).map((name) => `.${name}`));
      const fromRules = pageStyleRules()
        .filter((rule): rule is CSSStyleRule => 'selectorText' in rule)
        .filter((rule) => rule.selectorText.split(',').some((s) => classes.has(s.trim())))
        .map((rule) => rule.style.getPropertyValue('min-height'));
      const inline = (element as HTMLElement).style.getPropertyValue('min-height');
      return [...fromRules, inline].filter((value) => value !== '');
    }

    it('makes the provider at least as tall as the window', () => {
      const { container } = render(createElement(ThemeProvider, null, 'A short page'));
      const provider = container.querySelector('.fui-FluentProvider');
      if (!provider) throw new Error('no FluentProvider rendered');

      expect(declaredMinHeights(provider)).toEqual(['100%']);
    });

    it('gives a portal no height, so a dialog, menu or toast leaves the page visible', () => {
      render(createElement(ThemeProvider, null, createElement(Portal, null, 'In a portal')));
      const mountNode = document.querySelector('[data-portal-node="true"]');
      if (!mountNode) throw new Error('no portal mount node rendered');

      expect(mountNode.textContent).toBe('In a portal');
      expect(declaredMinHeights(mountNode)).toEqual([]);
    });
  });
});
