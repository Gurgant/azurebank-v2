import { readFileSync } from 'node:fs';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  DARK_MEDIA_QUERY,
  THEME_STORAGE_KEY,
  applyTheme,
  readStoredPreference,
  resolveTheme,
} from './themePreference';

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

describe('theme preference', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
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
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
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
    denied.mockRestore();
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
  describe('the pre-paint script', () => {
    const runThemeInit = () => new Function(THEME_INIT)();

    it('honours a stored choice', () => {
      stubMatchMedia(false);
      localStorage.setItem(THEME_STORAGE_KEY, 'dark');

      runThemeInit();

      // Also the drift guard: a script reading a different key would leave this at light.
      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    });

    it('follows the system when nothing is stored', () => {
      stubMatchMedia(true);

      runThemeInit();

      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    });

    it('an explicit light choice beats a dark system', () => {
      stubMatchMedia(true);
      localStorage.setItem(THEME_STORAGE_KEY, 'light');

      runThemeInit();

      expect(document.documentElement.getAttribute('data-theme')).toBe('light');
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
      denied.mockRestore();
    });

    it('does not take the page down when there is no matchMedia at all', () => {
      vi.stubGlobal('matchMedia', undefined);

      expect(() => runThemeInit()).not.toThrow();
      // Light is the safe default: a failure to detect should look like the app before dark existed.
      expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    });
  });
});
