import { mkdir, writeFile } from 'node:fs/promises';
import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * The accessibility sweep, measured and reported — not yet gated.
 *
 * axe-core runs over every page a signed-in user reaches from the navigation, the two public
 * pages, and the deposit dialog, with the WCAG 2.0 A/AA, 2.1 AA and 2.2 AA tags. Each scan
 * writes its violations to `test-results/axe/<page>.json` (a CI artifact) and attaches them to
 * the Playwright report; nothing here fails on a violation. That is deliberate: this is the
 * measurement the README used to list as "a dedicated phase not yet run", and a gate over
 * findings nobody has read yet would be a gate nobody could pass. Fixing them, and turning the
 * count into a floor, is a later item.
 *
 * What DOES fail: a scan that inspected nothing. axe reports the rules that passed as well as the
 * ones that failed, and a page with no passes was not scanned — a blank page, a redirect that
 * landed elsewhere, a session that had expired. That is the control that keeps an empty report
 * from reading as a clean one.
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

async function scan(page: Page, name: string) {
  const results = await new AxeBuilder({ page }).withTags(TAGS).analyze();

  // The control, before anything is written: a scan with no passes scanned nothing.
  expect(results.passes.length, `${name}: axe inspected no element at all`).toBeGreaterThan(0);

  const report = {
    page: name,
    url: page.url(),
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
      // Griffel's hashed class names say nothing on their own; the markup does.
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
      await scan(page, name);
    });
  }

  test('the deposit dialog is scanned and reported', async ({ page }) => {
    await page.goto('/dashboard');
    await heading(1)(page);
    await page.getByRole('button', { name: 'Deposit', exact: true }).click();
    await expect(page.getByRole('dialog', { name: /deposit money/i })).toBeVisible();
    await scan(page, 'dashboard-deposit-dialog');
  });
});

test.describe('accessibility, signed out', () => {
  // No session: the public pages, reached as a visitor reaches them.
  test.use({ storageState: { cookies: [], origins: [] } });

  for (const { name, path, ready } of PUBLIC) {
    test(`${name} is scanned and reported`, async ({ page }) => {
      await page.goto(path);
      await ready(page);
      await scan(page, name);
    });
  }
});
