import { expect, test, type Page } from '@playwright/test';
import { USER } from './fixtures';

/**
 * The route guard and the signed-in shell, in a real browser.
 *
 * The integration suite proves the API refuses an anonymous caller. What it cannot show is what a
 * PERSON experiences: whether the app redirects, or flashes protected content first, or renders an
 * error page. That is a rendering question, and only a browser answers it.
 */

declare global {
  interface Window {
    /** Set by the init script below the instant protected content enters the DOM. */
    __protectedContentSeen?: boolean;
  }
}

/** The text that must never appear to someone without a session. */
const PROTECTED_TEXT = /available balance/i;

/**
 * Watch for protected content from before the first byte of page script runs.
 *
 * A post-navigation `toHaveCount(0)` cannot see a dashboard that renders for 30ms and is then
 * swapped for the login form — it only proves the content is gone by the time the assertion runs.
 * That is a meaningful difference here: a flash of somebody's balance is a disclosure even if
 * React tidies it away immediately.
 *
 * `addInitScript` runs on every navigation in this context BEFORE page scripts, so the observer is
 * already watching when React first commits. It latches a boolean rather than counting, because
 * one frame is enough to matter.
 */
async function watchForProtectedContent(page: Page): Promise<void> {
  await page.addInitScript((pattern: string) => {
    const matcher = new RegExp(pattern, 'i');
    window.__protectedContentSeen = false;

    const scan = (node: Node | null): void => {
      const text = node?.textContent;
      if (text && matcher.test(text)) window.__protectedContentSeen = true;
    };

    new MutationObserver((records) => {
      for (const record of records) {
        record.addedNodes.forEach(scan);
        // characterData mutations change an existing text node in place rather than adding one.
        scan(record.target);
      }
    }).observe(document, { childList: true, subtree: true, characterData: true });
  }, PROTECTED_TEXT.source);
}

const sawProtectedContent = (page: Page) =>
  page.evaluate(() => window.__protectedContentSeen === true);

test.describe('anonymous visitors', () => {
  /*
    An empty storage state, overriding the project default. Signed-out has to be a property of the
    CONTEXT — the same lesson the integration suite learned the hard way, where a "fresh store"
    inside a signed-in file silently kept the session and the test proved nothing.
  */
  test.use({ storageState: { cookies: [], origins: [] } });

  test('are redirected from a protected route without ever rendering it', async ({ page }) => {
    await watchForProtectedContent(page);
    await page.goto('/dashboard');

    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole('heading', { name: /welcome back/i })).toBeVisible();

    // The real claim: it never rendered AT ANY POINT, not merely that it is absent now.
    expect(await sawProtectedContent(page)).toBe(false);
  });
});

test.describe('a signed-in visitor', () => {
  test('sees their real account rendered on the dashboard', async ({ page }) => {
    /*
      POSITIVE CONTROL for the test above. The same detector, on a page where the balance really
      does render, must come back TRUE — otherwise "no protected content was seen" is a statement
      about a broken observer rather than about the guard, and the anonymous test would pass just
      as happily with the MutationObserver deleted.
    */
    await watchForProtectedContent(page);
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

    expect(
      await sawProtectedContent(page),
      'The leak detector never fired on a page that DOES render the balance, so the anonymous ' +
        'test above is proving nothing. Fix the observer before trusting either.',
    ).toBe(true);
  });
});
