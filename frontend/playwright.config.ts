import { defineConfig, devices } from '@playwright/test';

/**
 * Phase 3 — the app in a REAL browser, against the real stack.
 *
 * What this layer adds over the two below it: React actually renders, and a person actually
 * clicks. The integration suite (Phase 2) drives the real data layer against the real backend but
 * has no React at all, and it stubs out the one genuinely visual piece of the step-up flow — the
 * modal. Everything a component does with a correct response is invisible to it.
 *
 * The topology is the dev one, unchanged: vite on 5173 proxies `/api` and `/bff` to the BFF on
 * 5000, which proxies to the API on 7215 over SQL Server. So the browser talks to exactly what a
 * developer's browser talks to, and no production code knows this suite exists.
 */

const PORT = 5173;
export const BASE_URL = `http://localhost:${PORT}`;

/** Written by the setup project, read by every test. Gitignored — it holds a live session. */
export const STORAGE_STATE = 'e2e/.auth/user.json';

export default defineConfig({
  testDir: './e2e',

  /*
    SERIAL, and not for tidiness. Three separate reasons, each sufficient on its own:
      - the BFF rate-limits auth to 10 requests / 60s / IP, so parallel workers each signing in
        would trip it immediately;
      - every worker would share the ONE seeded account, so a balance read in one test would race
        a deposit in another;
      - step-up elevation is server-side session state, so one test elevating changes what a
        concurrent test sees.
  */
  fullyParallel: false,
  workers: 1,

  /*
    No retries, deliberately. A retry here would hide exactly the flakiness this layer is most
    likely to introduce (portals, animations, refetch-on-invalidate), and a suite that is green
    only on the second attempt is the "green and wrong" state the whole test strategy exists to
    avoid. If a spec turns out to need a retry, that is a finding to fix, not a knob to turn.
  */
  retries: 0,

  forbidOnly: !!process.env.CI,
  reporter: process.env.CI ? [['github'], ['list']] : [['list']],

  use: {
    baseURL: BASE_URL,
    // Artefacts only when something failed: a trace is worth having when debugging and worth
    // nothing otherwise, and both are gitignored.
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
  },

  projects: [
    {
      name: 'setup',
      testMatch: /auth\.setup\.ts/,
    },
    {
      name: 'chromium',
      dependencies: ['setup'],
      use: { ...devices['Desktop Chrome'], storageState: STORAGE_STATE },
    },
  ],

  /*
    Playwright owns the dev server; it does NOT own the backend.
    `npm run dev` and never `dev:mock` — this suite has no mock target, and the setup project
    fails loudly rather than skipping if the API and BFF are not answering.
  */
  webServer: {
    command: 'npm run dev',
    url: BASE_URL,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
    stdout: 'ignore',
    stderr: 'pipe',
  },
});
