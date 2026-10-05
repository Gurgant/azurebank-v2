import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { scan } from './axeScan';

/**
 * The accessibility sweep: measured, reported, and gated on serious and critical findings.
 *
 * axe-core runs over the five navigation places, the Transfer wizard and the internal-transfer
 * page it links to (not the transaction detail or PIN setup), the login and register pages without
 * a session (/about is public too, but is scanned here only inside the signed-in shell), the
 * deposit dialog, the Change PIN dialog, and /accounts while its read is slow (light, dark and
 * 375 px wide): fourteen scans, with the WCAG 2.0 A/AA, 2.1 AA and 2.2 AA tags. The dialog scans
 * are scoped to the dialog, so what the page behind it fails is not reported as the dialog's.
 * Each scan writes its findings to `test-results/axe/<scan>.json` (a CI artifact)
 * and attaches them to the Playwright report, then FAILS on any serious or critical violation
 * outside `REPORT_ONLY_RULES`. Until 2026-09-17 nothing here failed on a violation: the sweep was
 * the first measurement, and a gate over findings nobody had read would have been a gate nobody
 * could pass. The two rules it found were colour contrast, which is U8's, and Tabster's focus
 * sentinels, which are excluded with the reason beside the gate. The gate is `scan`, in
 * `./axeScan.ts`: `REPORT_ONLY_RULES` and the exclusion are there with it.
 *
 * What ALSO fails, so an empty or misdirected report cannot read as a clean one:
 * - a page that never became ready (each scan first waits for the page's h1 or form field);
 * - a scan that landed somewhere else. An expired session is redirected to /login, which has an h1
 *   and passing rules of its own, so neither the ready wait nor the zero-passes check would notice;
 *   the path is asserted before axe runs;
 * - a page whose title is not its own (WCAG 2.4.2, which axe cannot judge);
 * - a scoped scan whose selector does not name exactly one element;
 * - a scan axe reports with zero passed rules, which inspected nothing;
 * - an exclusion that hides anything but a Tabster sentinel, or a rule the gate names that did not
 *   run at all.
 * Each scan also waits for the page's animations to finish first, so a fade is not measured.
 */
type Scan = { name: string; path: string; title: string; ready: (page: Page) => Promise<void> };

const SIGNED_IN: Scan[] = [
  { name: 'dashboard', path: '/dashboard', title: 'Home · AzureBank', ready: heading(1) },
  { name: 'accounts', path: '/accounts', title: 'Accounts · AzureBank', ready: heading(1) },
  { name: 'history', path: '/history', title: 'History · AzureBank', ready: heading(1) },
  { name: 'transfer', path: '/transfer', title: 'Send Money · AzureBank', ready: heading(1) },
  {
    name: 'transfer-internal',
    path: '/transfer/internal',
    title: 'Move Money · AzureBank',
    ready: heading(1),
  },
  { name: 'settings', path: '/settings', title: 'Settings · AzureBank', ready: heading(1) },
  { name: 'about', path: '/about', title: 'About this project · AzureBank', ready: heading(1) },
];

const PUBLIC: Scan[] = [
  { name: 'login', path: '/login', title: 'Sign in · AzureBank', ready: labelled('Email address') },
  {
    name: 'register',
    path: '/register',
    title: 'Create Account · AzureBank',
    ready: labelled('Email'),
  },
];

function heading(level: 1 | 2 | 3) {
  return async (page: Page) => {
    await expect(page.getByRole('heading', { level }).first()).toBeVisible();
  };
}

/** The outline the browser computes for an element, as the three values the ring is made of. */
function focusRing(el: Element) {
  const style = getComputedStyle(el);
  return { style: style.outlineStyle, width: style.outlineWidth, offset: style.outlineOffset };
}

function labelled(label: string) {
  return async (page: Page) => {
    await expect(page.getByLabel(label, { exact: true })).toBeVisible();
  };
}

test.describe('accessibility, signed in', () => {
  for (const { name, path, title, ready } of SIGNED_IN) {
    test(`${name} has no serious or critical finding`, async ({ page }) => {
      await page.goto(path);
      await ready(page);
      await expect(page).toHaveTitle(title);
      await scan(page, name, path);
    });
  }

  test('the deposit dialog has no serious or critical finding', async ({ page }) => {
    await page.goto('/dashboard');
    await heading(1)(page);
    await page.getByRole('button', { name: 'Deposit', exact: true }).click();
    await expect(page.getByRole('dialog', { name: /deposit money/i })).toBeVisible();
    await scan(page, 'dashboard-deposit-dialog', '/dashboard', '[role="dialog"]');
  });

  // PinInput under the gate: its group, six digit boxes and reveal toggle, three times over, with
  // no money moved and no PIN attempt spent.
  test('the Change PIN dialog has no serious or critical finding', async ({ page }) => {
    await page.goto('/settings');
    await heading(1)(page);
    await page.getByRole('button', { name: 'Change PIN', exact: true }).click();
    await expect(page.getByRole('dialog', { name: /change your pin/i })).toBeVisible();
    await scan(page, 'settings-change-pin-dialog', '/settings', '[role="dialog"]');
  });

  test('a route change is titled, announced and focused', async ({ page }) => {
    await page.goto('/dashboard');
    await heading(1)(page);
    await expect(page).toHaveTitle('Home · AzureBank');
    // A load is not a change: nothing is announced, and focus is left where the browser put it.
    const announcer = page.locator('[data-route-announcer]');
    await expect(announcer).toBeEmpty();

    const nav = page.getByRole('navigation', { name: 'Main navigation' });
    await nav.getByRole('link', { name: 'Accounts' }).press('Enter');
    await expect(page).toHaveTitle('Accounts · AzureBank');
    await expect(announcer).toHaveText('Accounts page loaded');
    const main = page.getByRole('main');
    await expect(main).toBeFocused();
    // The keyboard got here, so the ring is this app's, not the browser's default (`auto`).
    expect(await main.evaluate(focusRing)).toEqual({
      style: 'solid',
      width: '2px',
      offset: '-2px',
    });

    // The wizard has no shell and no <main>: its heading takes focus instead.
    await nav.getByRole('button', { name: 'Transfer' }).press('Enter');
    await expect(page).toHaveTitle('Send Money · AzureBank');
    await expect(announcer).toHaveText('Send Money page loaded');
    const wizardHeading = page.getByRole('heading', { level: 1, name: 'Send Money' });
    await expect(wizardHeading).toBeFocused();
    expect(await wizardHeading.evaluate(focusRing)).toEqual({
      style: 'solid',
      width: '2px',
      offset: '2px',
    });

    // A mouse user sees no ring: the browser does not match :focus-visible after a click.
    await page.goto('/dashboard');
    await heading(1)(page);
    await nav.getByRole('link', { name: 'Accounts' }).click();
    await expect(page).toHaveTitle('Accounts · AzureBank');
    await expect(main).toBeFocused();
    expect((await main.evaluate(focusRing)).style).toBe('none');
  });
});

/*
  A LOADING STATE, SCANNED. Every scan above waits for a page's content, so nothing here had ever
  looked at the page while it waits — the one state the "Taking longer than usual…" hint lives in.
  The read is parked with `page.route` and the page's clock moved to 20 s, so the scan sees the
  hint's last words and its "Stop waiting" button. Three variants, because the hint's grey is the
  kind of colour that passes on white and fails on the dark canvas, and its row must wrap at phone
  width. The second, narrower run looks only at the hint and only at contrast, and must have
  checked at least one node of it — a hint axe never saw would pass that check too.
*/
const SLOW_READ_VARIANTS: { name: string; prepare: (page: Page) => Promise<void> }[] = [
  { name: 'accounts-slow-read', prepare: async () => {} },
  {
    name: 'accounts-slow-read-dark',
    prepare: (page) => page.emulateMedia({ colorScheme: 'dark' }),
  },
  {
    name: 'accounts-slow-read-375',
    prepare: (page) => page.setViewportSize({ width: 375, height: 812 }),
  },
];

test.describe('accessibility while a read is slow', () => {
  for (const { name, prepare } of SLOW_READ_VARIANTS) {
    test(`/accounts while a read is slow (${name}) has no serious or critical finding`, async ({
      page,
    }) => {
      await prepare(page);
      let parked = 0;
      await page.clock.install();
      await page.route('**/api/accounts*', () => {
        // Never answered: the page waits until the test ends.
        parked += 1;
      });
      await page.goto('/accounts');
      await expect.poll(() => parked).toBe(1);
      await expect(page.getByLabel('Loading accounts')).toBeVisible();

      await page.clock.fastForward('00:20');
      await expect(page.getByRole('status').filter({ hasText: 'Still trying…' })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Stop waiting' })).toBeVisible();
      await expect(page).toHaveTitle('Accounts · AzureBank');

      await scan(page, name, '/accounts');

      const contrast = await new AxeBuilder({ page })
        .include('[data-wait-hint]')
        .withRules(['color-contrast'])
        .analyze();
      const checked = contrast.passes.find((rule) => rule.id === 'color-contrast')?.nodes ?? [];
      expect(checked.length, `${name}: axe checked no node of the hint`).toBeGreaterThan(0);
      expect(
        contrast.violations.flatMap((rule) => rule.nodes.map((node) => node.target.join(' '))),
        `${name}: the hint fails colour contrast`,
      ).toEqual([]);
    });
  }
});

test.describe('accessibility, signed out', () => {
  // No session: login and register, reached as a visitor reaches them.
  test.use({ storageState: { cookies: [], origins: [] } });

  for (const { name, path, title, ready } of PUBLIC) {
    test(`${name} has no serious or critical finding`, async ({ page }) => {
      await page.goto(path);
      await ready(page);
      await expect(page).toHaveTitle(title);
      await scan(page, name, path);
    });
  }
});
