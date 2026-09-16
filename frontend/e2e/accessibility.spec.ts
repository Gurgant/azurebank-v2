import { mkdir, writeFile } from 'node:fs/promises';
import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * The accessibility sweep, measured and reported — not yet gated.
 *
 * axe-core runs over the five navigation places, the Transfer wizard and the internal-transfer
 * page it links to (not the transaction detail or PIN setup), the login and register pages without
 * a session (/about is public too, but is scanned here only inside the signed-in shell), and the
 * deposit dialog: ten scans, with the WCAG 2.0 A/AA, 2.1 AA and 2.2 AA tags. The dialog scan is
 * scoped to the dialog, so what the dashboard behind it fails is not reported as the dialog's. Each
 * scan writes its violations to `test-results/axe/<scan>.json` (a CI artifact) and attaches them to
 * the Playwright report; nothing here fails on a violation. That is deliberate: this is the
 * measurement the README used to list as "a dedicated phase not yet run", and a gate over
 * findings nobody has read yet would be a gate nobody could pass. Fixing them, and turning the
 * count into a floor, is a later item.
 *
 * What DOES fail, so an empty or misdirected report cannot read as a clean one:
 * - a page that never became ready (each scan first waits for the page's h1 or form field);
 * - a scan that landed somewhere else. An expired session is redirected to /login, which has an h1
 *   and passing rules of its own, so neither the ready wait nor the zero-passes check would notice;
 *   the path is asserted before axe runs;
 * - a scoped scan whose selector does not name exactly one element;
 * - a scan axe reports with zero passed rules, which inspected nothing.
 * Each scan also waits for the page's animations to finish first, so a fade is not measured.
 */
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'];
const REPORT_DIR = 'test-results/axe';

type Scan = { name: string; path: string; ready: (page: Page) => Promise<void> };

const SIGNED_IN: Scan[] = [
  { name: 'dashboard', path: '/dashboard', ready: heading(1) },
  { name: 'accounts', path: '/accounts', ready: heading(1) },
  { name: 'history', path: '/history', ready: heading(1) },
  { name: 'transfer', path: '/transfer', ready: heading(1) },
  { name: 'transfer-internal', path: '/transfer/internal', ready: heading(1) },
  { name: 'settings', path: '/settings', ready: heading(1) },
  { name: 'about', path: '/about', ready: heading(1) },
];

const PUBLIC: Scan[] = [
  { name: 'login', path: '/login', ready: labelled('Email address') },
  { name: 'register', path: '/register', ready: labelled('Email') },
];

function heading(level: 1 | 2 | 3) {
  return async (page: Page) => {
    await expect(page.getByRole('heading', { level }).first()).toBeVisible();
  };
}

function labelled(label: string) {
  return async (page: Page) => {
    await expect(page.getByLabel(label, { exact: true })).toBeVisible();
  };
}

async function scan(page: Page, name: string, path: string, scope?: string) {
  // A redirect is not a scan of this page: /login would pass every other check here.
  expect(new URL(page.url()).pathname, `${name}: the scan landed on another page`).toBe(path);

  /*
    A SETTLED PAGE, NOT A FADING ONE. axe reads computed colours, and mid-animation an element's
    opacity blends it toward what is behind it. Measured 2026-09-16: the deposit dialog's open
    animation was 36-52% through when this scan started, and across fourteen runs the dialog
    reported one contrast failure twelve times, two once and three once. Waiting for every finite
    animation to finish, it reported one in eight runs of eight. An animation that is cancelled
    (its element removed) counts as finished; an infinite one, a spinner, is not waited for.
  */
  await page.evaluate(() =>
    Promise.all(
      document
        .getAnimations()
        .filter((a) => a.effect?.getComputedTiming().iterations !== Infinity)
        .map((a) => a.finished.catch(() => undefined)),
    ),
  );

  const builder = new AxeBuilder({ page }).withTags(TAGS);
  if (scope) {
    // One element, or the scope is not the thing named: none scans nothing, two scan a stranger.
    await expect(page.locator(scope), `${name}: the scope must name one element`).toHaveCount(1);
    builder.include(scope);
  }
  const results = await builder.analyze();

  // The control, before anything is written: a scan with no passes scanned nothing.
  expect(results.passes.length, `${name}: axe inspected no element at all`).toBeGreaterThan(0);

  const report = {
    page: name,
    url: page.url(),
    scope: scope ?? null,
    tags: TAGS,
    axe: results.testEngine.version,
    passes: results.passes.length,
    incomplete: results.incomplete.length,
    violations: results.violations.map((v) => ({
      id: v.id,
      impact: v.impact,
      tags: v.tags.filter((t) => TAGS.includes(t)),
      help: v.help,
      helpUrl: v.helpUrl,
      nodes: v.nodes.length,
      // Griffel's hashed class names say nothing on their own; the selector and the first 160
      // characters of each node's markup do, for the first five nodes of each violation.
      targets: v.nodes
        .slice(0, 5)
        .map((n) => ({ target: n.target.join(' '), html: n.html.slice(0, 160) })),
    })),
  };
  await mkdir(REPORT_DIR, { recursive: true });
  const body = JSON.stringify(report, null, 2);
  await writeFile(`${REPORT_DIR}/${name}.json`, body);
  await test.info().attach(`axe-${name}`, { body, contentType: 'application/json' });

  const summary = report.violations.map((v) => `${v.id} (${v.impact}, ${v.nodes})`).join(', ');
  console.log(`axe ${name}: ${report.violations.length} violations — ${summary || 'none'}`);
  return report;
}

test.describe('accessibility, signed in', () => {
  for (const { name, path, ready } of SIGNED_IN) {
    test(`${name} is scanned and reported`, async ({ page }) => {
      await page.goto(path);
      await ready(page);
      await scan(page, name, path);
    });
  }

  test('the deposit dialog is scanned and reported', async ({ page }) => {
    await page.goto('/dashboard');
    await heading(1)(page);
    await page.getByRole('button', { name: 'Deposit', exact: true }).click();
    await expect(page.getByRole('dialog', { name: /deposit money/i })).toBeVisible();
    await scan(page, 'dashboard-deposit-dialog', '/dashboard', '[role="dialog"]');
  });
});

test.describe('accessibility, signed out', () => {
  // No session: login and register, reached as a visitor reaches them.
  test.use({ storageState: { cookies: [], origins: [] } });

  for (const { name, path, ready } of PUBLIC) {
    test(`${name} is scanned and reported`, async ({ page }) => {
      await page.goto(path);
      await ready(page);
      await scan(page, name, path);
    });
  }
});
