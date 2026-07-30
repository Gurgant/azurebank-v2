/*
 * Decides the theme BEFORE the first paint. Loaded from <head> as a blocking classic script.
 *
 * WHY A FILE AND NOT AN INLINE SCRIPT. The BFF serves `script-src 'self'` with no `unsafe-inline`
 * and no nonce (SecurityHeadersMiddleware), so an inline block would run happily under Vite's dev
 * server and be refused in production — the worst shape a bug can have. Same origin, own file, no
 * CSP change needed.
 *
 * WHY IT DUPLICATES `themePreference.ts`. Nothing bundled can run this early: the module graph is
 * fetched and evaluated after the document is parsed, so a React-side decision always arrives at
 * least one paint late, and that paint is the white flash a dark-mode user sees on every load. The
 * duplication is the price of being first, and it is held in place by a test that reads this file
 * and asserts the key and the values still match the module.
 *
 * It must not throw. `localStorage` is unavailable in some privacy modes, and a theme script that
 * takes the page down with it is worse than a wrong colour.
 */
(function initTheme() {
  var STORAGE_KEY = 'azurebank.theme';

  // Two try blocks, not one, and the separation is the whole correctness of this file. With a single
  // block a denied `localStorage` — private browsing, blocked cookies — jumped straight to the
  // fallback and never asked the system at all, so the script chose LIGHT while React, whose own
  // read falls back to 'system' and then queries `matchMedia`, chose DARK. The two disagreed exactly
  // in the case this script exists for, and the user saw the flash anyway.
  var preference = 'system';
  try {
    var stored = localStorage.getItem(STORAGE_KEY);
    preference = stored === 'light' || stored === 'dark' ? stored : 'system';
  } catch (error) {
    // Cannot remember a choice. Following the device is the same fallback the module takes.
  }

  var resolved = 'light';
  try {
    resolved =
      preference === 'system'
        ? window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
          ? 'dark'
          : 'light'
        : preference;
  } catch (error) {
    // No `matchMedia`. Light is the safe default: it is what the app looked like before this existed.
    resolved = 'light';
  }
  document.documentElement.setAttribute('data-theme', resolved);
})();
