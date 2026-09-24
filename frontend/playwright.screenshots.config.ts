import { defineConfig } from '@playwright/test';

/**
 * The app's pictures — the README's, the repository's social preview, a LinkedIn card — taken by a
 * script instead of by hand, so that they can be taken again. The UI/UX phase will change every
 * screen, and a screenshot taken by hand is the first thing in a README to go stale.
 *
 * Not a test suite, and never part of one: `npm run test:e2e` reads `playwright.config.ts`, whose
 * `testDir` is `./e2e`, so it never sees `./screenshots`. This runs only when asked:
 *
 *   1. reseed:   dotnet run --project backend/tools/AzureBank.Seeder -- reset --confirm
 *   2. run the API and the BFF, with the BFF serving the built SPA (`npm run build`, then the BFF's
 *      `Spa:RootPath` pointing at `frontend/dist`) — the product as it ships, under its real CSP
 *   3. npm run capture:screenshots    -> PNGs and a WebM under screenshots/output/ (git-ignored)
 *   4. npm run capture:publish        -> the ones the README uses, resized and compressed, in
 *                                        docs/images/ (needs ffmpeg and pngquant on the PATH)
 *
 * Reseeding first is not tidiness: the demo ledger is dated relative to the moment it was seeded,
 * so a fresh seed is what keeps the dashboard's month and the history's last days populated, and the
 * capture itself sends money, which a second run would otherwise show twice.
 */
export default defineConfig({
  testDir: './screenshots',
  testMatch: /\.capture\.ts$/,

  // One browser, one session, in order: later pictures depend on money earlier ones moved.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  reporter: [['list']],

  use: {
    baseURL: process.env.CAPTURE_BASE_URL ?? 'http://localhost:5000',
    // Dates render in the browser's own time zone (date-fns over `new Date`), so the zone is fixed
    // here and a capture made anywhere reads the same. The language already is fixed — money in
    // one locale (LOCALE in utils/format.ts), dates in date-fns's English — and the browser's
    // locale is pinned too, for anything that falls back to it.
    locale: 'en-GB',
    timezoneId: 'Europe/Rome',
    // The app reads prefers-reduced-motion (hooks/useReducedMotion.ts); each picture also freezes
    // CSS animations as it is taken (app.capture.ts), so nothing is caught half-way through.
    contextOptions: { reducedMotion: 'reduce' },
    trace: 'off',
    video: 'off',
    screenshot: 'off',
  },
  projects: [{ name: 'chromium', use: { browserName: 'chromium' } }],
});
