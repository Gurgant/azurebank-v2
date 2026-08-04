import { expect, test as setup } from '@playwright/test';
import { STORAGE_STATE } from '../playwright.config';
import { BFF_ORIGIN, USER } from './fixtures';

/**
 * Sign in ONCE per run and save the session for every spec.
 *
 * Not an optimisation. The BFF allows 10 auth requests per 60s per IP, so a suite that signed in
 * per test would rate-limit itself into red before it tested anything. One login here, reused via
 * `storageState`, keeps the whole run at a single auth request.
 *
 * The saved state is perishable and gitignored: the dev session is 10 minutes' inactivity and 20
 * absolute, so it is regenerated every run and must never be committed — it holds a live session.
 */

setup('authenticate', async ({ page, request }) => {
  /*
    Preflight the BACKEND before touching the browser, and FAIL rather than skip — the same rule
    the integration suite follows. Without this, a stack that is down surfaces as an inscrutable
    login-form timeout thirty seconds later instead of one sentence naming the cause.
  */
  let backendAnswered = false;
  try {
    const probe = await request.get(`${BFF_ORIGIN}/api/accounts`, {
      failOnStatusCode: false,
      timeout: 5_000,
    });
    backendAnswered = probe.status() > 0;
  } catch {
    backendAnswered = false;
  }
  expect(
    backendAnswered,
    `Nothing answered at ${BFF_ORIGIN}. The E2E suite drives a real browser against the REAL ` +
      'stack: start azurebank-api (:7215) and azurebank-bff (:5000) with the database migrated ' +
      'and seeded, then re-run. There is no mock target by design.',
  ).toBe(true);

  await page.goto('/login');

  // "Email address", not "Email" — taken from the page's own accessibility snapshot rather than
  // guessed. The first draft assumed "Email" and timed out against a perfectly working form.
  await page.getByLabel('Email address', { exact: true }).fill(USER.email);
  /*
    `exact` matters here: the field ships a "Show password" toggle button whose aria-label also
    contains "Password", so a loose /password/i matches two elements and Playwright refuses to
    guess. Pinning the exact label is the fix, not reaching for `.first()` — which would have
    silently kept working if the two ever swapped order.
  */
  await page.getByLabel('Password', { exact: true }).fill(USER.password);
  await page.getByRole('button', { name: /sign in|log ?in/i }).click();

  /*
    Landing on a protected route is the signal that the session cookie was actually set — a
    "success" toast would not prove it, and neither would the absence of an error.

    This assertion doubles as the MOCK DETECTOR, which is why it is worth the comment. The mock
    validates credentials against `demo@azurebank.dev`, so these ones come back
    401 INVALID_CREDENTIALS there and 200 here. If someone ever points this suite at `dev:mock`,
    it dies right here instead of quietly testing the mock for the rest of the run.
  */
  /*
    `/dashboard` REQUIRED, not optional. The first draft was `/\/(dashboard)?$|\/dashboard/`, whose
    left branch matches a bare `/` — and `/` is itself a dashboard route (App.tsx:69), so the
    assertion would have passed on a redirect it never meant to accept. Measured: with no `returnTo`
    in history, LoginPage navigates to `navState.from?.pathname ?? '/dashboard'` (LoginPage.tsx:131),
    so landing anywhere else here is a real change worth failing on.
  */
  await expect(page).toHaveURL(/\/dashboard(?:[?#]|$)/, { timeout: 15_000 });

  /*
    And a URL is not a session. This is the assertion that actually proves the cookie was set and
    the guard let us through: `ProtectedShell` only renders its chrome for an authenticated user,
    and the nav landmark appears before any data resolves, so it is the earliest honest signal.
  */
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();

  await page.context().storageState({ path: STORAGE_STATE });
});
