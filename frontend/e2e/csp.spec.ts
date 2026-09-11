import { expect, test } from '@playwright/test';

/**
 * The built app, served by the BFF under its real Content-Security-Policy (ADR-0054).
 *
 * The policy was written for this bundle long before anything served the bundle under it: e2e ran
 * against vite's dev server, which sends no CSP, so `script-src 'self'` had never met the real
 * build. When it finally did, it found two things a reading of the code had not. Zod probes for
 * `eval` on every page load (six violations in six page loads), and Fluent's styling engine creates
 * `<style>` elements that `style-src 'self'` refuses — 99 violations, and a Fluent button's computed
 * border-radius at 0px instead of 10px.
 * Both are handled now (`src/zodConfig.ts`, and the empty-string hash in the header), and this walk
 * is what keeps them handled: a violation anywhere on it fails the run.
 *
 * Run it against the served build: `npm run build`, start the BFF with `Spa:RootPath` pointing at
 * `frontend/dist`, then `E2E_BASE_URL=http://localhost:5000 npm run test:e2e`. CI does exactly that.
 */
test('the served build raises no Content-Security-Policy violation', async ({ page }) => {
  const violations: string[] = [];
  await page.exposeFunction('__cspViolation', (violation: string) => violations.push(violation));
  await page.addInitScript(() => {
    document.addEventListener('securitypolicyviolation', (event) => {
      const report = (window as unknown as { __cspViolation: (v: string) => void }).__cspViolation;
      report(`${event.violatedDirective} blocked ${event.blockedURI} on ${location.pathname}`);
    });
  });

  const response = await page.goto('/dashboard');
  const policy = response?.headers()['content-security-policy'];
  if (process.env.E2E_BASE_URL) {
    expect(
      policy,
      'the page came back without the BFF CSP, so nothing below would test it',
    ).toBeTruthy();
  }
  test.skip(!policy, 'vite dev server: no CSP to meet. Set E2E_BASE_URL to the BFF-served build.');

  // Pinned in the browser too: the property the header comment claims is that nothing is unsafe.
  expect(policy).not.toContain("'unsafe-inline'");
  expect(policy).not.toContain("'unsafe-eval'");

  await expect(page).toHaveURL(/\/dashboard/);
  for (const path of ['/accounts', '/history', '/transfer', '/settings']) {
    await page.goto(path);
    await page.waitForLoadState('networkidle');
  }

  // A Fluent dialog: a portal, focus management and a burst of new styles.
  await page.getByRole('button', { name: 'Change', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('button', { name: 'Cancel' }).click();

  // The theme script runs before the first paint and is the one script not in the bundle.
  await page.getByRole('radio', { name: 'Dark' }).check();
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.getByRole('radio', { name: 'System' }).check();

  expect(violations).toEqual([]);
});
