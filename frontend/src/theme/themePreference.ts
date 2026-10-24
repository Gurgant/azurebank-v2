import { useCallback, useEffect, useLayoutEffect, useState } from 'react';

/**
 * The theme preference, and the one place that knows how it becomes a theme.
 *
 * Three values rather than a boolean, because "follow the system" is a real answer and not the
 * absence of one: a user who has never touched the toggle wants their OS setting honoured, and a
 * user who chose light on a dark-mode machine wants that respected tomorrow too. A boolean cannot
 * tell those two apart, and collapsing them is how apps end up ignoring a system switch made after
 * install.
 *
 * `public/theme-init.js` decides the SAME thing before the first paint and must stay in step; the
 * constants below are what its test compares against.
 */

export type ThemePreference = 'system' | 'light' | 'dark';
export type ResolvedTheme = 'light' | 'dark';

export const THEME_STORAGE_KEY = 'azurebank.theme';
export const DARK_MEDIA_QUERY = '(prefers-color-scheme: dark)';

/** Anything unrecognised — absent, corrupted, written by an older build — means "follow the system". */
export function readStoredPreference(): ThemePreference {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY);
    return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system';
  } catch {
    return 'system';
  }
}

export function systemPrefersDark(): boolean {
  return typeof window.matchMedia === 'function' && window.matchMedia(DARK_MEDIA_QUERY).matches;
}

export function resolveTheme(preference: ThemePreference): ResolvedTheme {
  if (preference === 'system') return systemPrefersDark() ? 'dark' : 'light';
  return preference;
}

/**
 * Puts a theme on the document: the attribute the stylesheet's variables key on, and the colour
 * scheme the browser draws its own parts from (scroll bars, its default focus ring, native popups).
 * One function, so that the two are never written apart; `public/theme-init.js` writes the same
 * two before the first paint.
 */
export function writeDocumentTheme(resolved: ResolvedTheme): void {
  document.documentElement.setAttribute('data-theme', resolved);
  document.documentElement.style.colorScheme = resolved;
}

/**
 * The single write. The document's theme and the storage move together, so a reload cannot
 * disagree with what is on screen.
 */
export function applyTheme(preference: ThemePreference): ResolvedTheme {
  const resolved = resolveTheme(preference);
  writeDocumentTheme(resolved);
  try {
    localStorage.setItem(THEME_STORAGE_KEY, preference);
  } catch {
    // Storage denied. The document's theme is still set, so the choice holds for this page — it
    // simply will not survive a reload, which is better than refusing to switch at all.
  }
  return resolved;
}

/**
 * Read the preference, change it, and keep following the system while the answer is "system".
 *
 * The listener is the part that is easy to leave out and impossible to notice: without it, a user on
 * `system` who flips their OS to dark at midnight keeps whatever this tab decided at load. It is
 * registered only while the preference IS `system`, so an explicit choice is never overridden by the
 * machine.
 */
export function useThemePreference() {
  const [preference, setPreferenceState] = useState<ThemePreference>(readStoredPreference);
  const [resolved, setResolved] = useState<ResolvedTheme>(() =>
    resolveTheme(readStoredPreference()),
  );

  const setPreference = useCallback((next: ThemePreference) => {
    setPreferenceState(next);
    setResolved(applyTheme(next));
  }, []);

  /**
   * Make the document agree with React, before the browser paints.
   *
   * `public/theme-init.js` has normally set this already, and the first version of this hook left it
   * alone on that reasoning. That was a mistake: it optimised away one idempotent write in exchange
   * for a divergence with no detection. Three ways the two can disagree at mount —
   *
   *  - the script never ran (a 404 behind a misconfigured static host, or any test harness);
   *  - ANOTHER TAB wrote the preference between the head script and this mount;
   *  - the stored value changed for any other reason in that window.
   *
   * — and in all three Fluent takes the stored preference while `<html>` keeps the old theme, so
   * the custom properties and the component library render two different themes at once. There is no
   * error and nothing logs; it just looks broken.
   *
   * `useLayoutEffect`, not `useEffect`: a correction that lands after paint is a visible flicker,
   * which is the thing the pre-paint script exists to avoid. Writing the document only — not
   * `applyTheme` — because the value came FROM storage and writing it back would be a pointless
   * round trip that also has to be caught when storage is denied.
   */
  useLayoutEffect(() => {
    writeDocumentTheme(resolved);
  }, [resolved]);

  useEffect(() => {
    if (preference !== 'system' || typeof window.matchMedia !== 'function') return;
    const media = window.matchMedia(DARK_MEDIA_QUERY);
    const follow = () => setResolved(applyTheme('system'));
    media.addEventListener('change', follow);
    return () => media.removeEventListener('change', follow);
  }, [preference]);

  return { preference, resolved, setPreference };
}
