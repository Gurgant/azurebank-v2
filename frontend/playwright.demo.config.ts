import { defineConfig, devices } from '@playwright/test';

/**
 * The demo, in a real browser, on the stack a deployment runs: its own run, beside the default
 * one and never part of it.
 *
 * `npm run test:e2e` reads `playwright.config.ts`, whose `testDir` is `./e2e`, so it never sees
 * `./e2e-demo`; and that run's stack has the demo off, which one of its own tests holds
 * (`e2e/auth.spec.ts`, the anonymous visitors). This one needs the opposite stack, and it does
 * three things the default run never does. It runs only when asked.
 *
 * HOW TO RUN IT, from the repository's root and then from `frontend/`:
 *
 *   1. docker compose -f compose.yaml -f compose.demo.yaml up --build
 *      on a volume that has held nothing else (`docker compose down -v` first: compose.demo.yaml's
 *      header says why), with the variables that file's header names. The BFF then serves the
 *      built page on http://localhost:5000 with the demo's tag in its head.
 *   2. npm run test:e2e:demo
 *
 * Two variables, both optional. `E2E_DEMO_BASE_URL` is where the page is served, by default
 * http://localhost:5000. Keep the host `localhost`: the session's cookie is `Secure`, and the
 * `restart` project asks with Playwright's own request context, which sends a `Secure` cookie
 * over http to `localhost` and not to `127.0.0.1`, where a browser sends it to both.
 * `E2E_DEMO_COMPOSE_PROJECT` is the compose project's name, by default `azurebank`
 * (compose.yaml's `name:`), for a stack started with `-p`.
 *
 * WHAT IT DOES THAT THE DEFAULT RUN DOES NOT.
 *
 *   - IT RESTARTS TWO CONTAINERS. The default run owns no part of the backend
 *     (`playwright.config.ts`, above its `webServer`). This one's second project restarts the
 *     BFF's container and then the API's, with `docker restart`, to show that the copy a browser
 *     keeps still signs in once every session is gone (`e2e-demo/restart.setup.ts`). It needs
 *     the `docker` command, and it stops the stack for whoever else is using it.
 *   - IT CLAIMS COPIES AND MOVES MONEY. Two claims, two sign-ins and one transfer of one euro,
 *     inside a copy it claimed. A stack hands one client ten copies in a rolling 24 hours, and
 *     through the one published port every browser is the same client (compose.demo.yaml's
 *     header): at two claims a run, the sixth run on one volume inside a day is refused. The
 *     BFF also takes ten sign-ins and claims a minute from one address (`playwright.config.ts`,
 *     the first reason for `workers: 1`), and a run spends four of them.
 *   - IT HANDLES A PASSWORD. A claim's answer holds the copy's password, the browser keeps it
 *     (`src/features/demo/demoCopyStorage.ts`), and the dashboard shows it when asked. See below.
 *
 * THREE PROJECTS, each waiting for the one before, with a `testMatch` each: a project with none
 * takes every spec of the folder.
 *
 *   demo                 one browser context from the first click to a saved, signed-in state
 *   restart              the two containers, and the wait until the stack is whole again
 *   demo-after-restart   from the saved state: the session is gone, the kept copy still signs in
 *
 * NOTHING OF A RUN IS KEPT THAT COULD HOLD THE PASSWORD. The default run keeps a trace and a
 * screenshot of a red test (`playwright.config.ts`, `use`). Here a trace would hold the claim's
 * answer, with the password and the PIN, and the session's cookie; a screenshot could be of the
 * dashboard with the sign-in details open. So: no trace, no screenshot, no video, and the `list`
 * reporter alone, as the capture's config (`playwright.screenshots.config.ts`).
 *
 * That is not all a run writes, and no setting of this config stops the two files below:
 *   - `error-context.md`, which Playwright writes beside a red test's other output with the page
 *     as an accessibility tree: every word on it. So the specs never leave the sign-in details
 *     open: they are opened to be read or scanned and closed in a `finally`, no locator is
 *     asserted on while they are open, and a hook closes them after every test, before
 *     Playwright looks at the page (`e2e-demo/demoRun.ts`);
 *   - the report of each scan, green or red, which quotes the markup of what it found. So the
 *     scan of the dashboard with the details open asks for a report with no markup
 *     (`e2e/axeScan.ts`), and the run's first test holds that the scan honours that.
 *
 * WHAT THAT COSTS. A red run leaves the `list` reporter's lines, among them one line for each
 * status, path and header a step observed (`note` in `e2e-demo/demoRun.ts`, which also attaches
 * the line, and a red test's attachments are printed under it), the axe reports under
 * `test-results/axe/`, and `error-context.md` with the failure and the page as it was. No trace
 * to step through and no picture.
 *
 * WHAT IT LEAVES ON DISK, all of it ignored by git and named in .dockerignore:
 * `e2e-demo/.auth/copy.json`, the saved state, which holds a live session and the kept copy with
 * its password; `e2e-demo/.auth/scan-values.txt`, the passwords of the copies the run claimed,
 * one a line, so that whoever ran it can search what the run wrote for them; and
 * `test-results/`. Delete all three when the run has been read.
 */

/** Written by the `demo` project signed in, read by the two after it. It holds a live session. */
export const DEMO_STATE = 'e2e-demo/.auth/copy.json';

/** The passwords of the copies a run claimed, one a line. Never attached, logged or printed. */
export const SCAN_VALUES = 'e2e-demo/.auth/scan-values.txt';

export default defineConfig({
  testDir: './e2e-demo',

  // One browser, one copy, in order: each step stands on the one before, and the limiter and the
  // day's cap count every claim (`playwright.config.ts` has the reasons for the same settings).
  fullyParallel: false,
  workers: 1,
  retries: 0,
  // Always, and not only in CI: no CI job runs this, and a run by hand that an `.only` had
  // narrowed would read as the whole proof.
  forbidOnly: true,
  reporter: [['list']],

  use: {
    baseURL: process.env.E2E_DEMO_BASE_URL ?? 'http://localhost:5000',
    trace: 'off',
    screenshot: 'off',
    video: 'off',
  },

  projects: [
    {
      name: 'demo',
      testMatch: /[\\/]demo\.spec\.ts$/,
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'restart',
      testMatch: /[\\/]restart\.setup\.ts$/,
      dependencies: ['demo'],
      use: { storageState: DEMO_STATE },
    },
    {
      name: 'demo-after-restart',
      testMatch: /[\\/]after-restart\.spec\.ts$/,
      dependencies: ['restart'],
      use: { ...devices['Desktop Chrome'], storageState: DEMO_STATE },
    },
  ],
});
