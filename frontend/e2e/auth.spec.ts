import { expect, test } from '@playwright/test';
import { USER } from './fixtures';

/**
 * The route guard and the signed-in shell, in a real browser.
 *
 * The integration suite proves the API refuses an anonymous caller. What it cannot show is what a
 * PERSON experiences: whether the app redirects, or flashes protected content first, or renders an
 * error page. That is a rendering question, and only a browser answers it.
 */

test.describe('anonymous visitors', () => {
  /*
    An empty storage state, overriding the project default. Signed-out has to be a property of the
    CONTEXT — the same lesson the integration suite learned the hard way, where a "fresh store"
    inside a signed-in file silently kept the session and the test proved nothing.
  */
  test.use({ storageState: { cookies: [], origins: [] } });

  test('are redirected from a protected route to the login form', async ({ page }) => {
    await page.goto('/dashboard');

    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole('heading', { name: /welcome back/i })).toBeVisible();

    // And no protected content leaked on the way past: the balance heading must never have
    // rendered, not merely be gone by now.
    await expect(page.getByText(/available balance/i)).toHaveCount(0);
  });
});

test.describe('a signed-in visitor', () => {
  test('sees their real account rendered on the dashboard', async ({ page }) => {
    await page.goto('/dashboard');

    // The identity comes from /bff/auth/me against the real BFF, not from a fixture in the page.
    await expect(page.getByText(USER.email)).toBeVisible();

    /*
      A EUR figure formatted the app's way. Asserted as a PATTERN rather than a literal, because
      the seeded balance moves every time the deposit spec runs — pinning "€50,022.00" would make
      this test a tripwire for its own neighbours rather than for the app.
    */
    await expect(page.getByRole('heading', { level: 1 }).first()).toHaveText(/^€[\d,]+\.\d{2}$/);

    // The account number is MASKED on the way in. Bullets, not the asterisks the API sends — the
    // component substitutes them, which is exactly the kind of render-only detail this layer owns.
    await expect(page.getByText(/AB-•+-•+-\d+/).first()).toBeVisible();
  });
});
