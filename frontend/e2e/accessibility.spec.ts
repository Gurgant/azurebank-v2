import { mkdir, writeFile } from 'node:fs/promises';
import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * The accessibility sweep: measured, reported, and gated on serious and critical findings.
 *
 * axe-core runs over the five navigation places, the Transfer wizard and the internal-transfer
 * page it links to (not the transaction detail or PIN setup), the login and register pages without
 * a session (/about is public too, but is scanned here only inside the signed-in shell), the
 * deposit dialog and the Change PIN dialog: eleven scans, with the WCAG 2.0 A/AA, 2.1 AA and 2.2 AA
 * tags. The dialog scans are scoped to the dialog, so what the page behind it fails is not reported
 * as the dialog's. Each scan writes its findings to `test-results/axe/<scan>.json` (a CI artifact)
 * and attaches them to the Playwright report, then FAILS on any serious or critical violation
 * outside `REPORT_ONLY_RULES`. Until 2026-09-17 nothing here failed on a violation: the sweep was
 * the first measurement, and a gate over findings nobody had read would have been a gate nobody
 * could pass. The two rules it found were colour contrast, which is U8's, and Tabster's focus
 * sentinels, which are excluded below with the reason.
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
const TAGS = ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'];
const REPORT_DIR = 'test-results/axe';

/** A violation of these impacts fails the scan. A violation with no impact fails it too. */
const GATED_IMPACTS = ['serious', 'critical'];

/**
 * Reported, never gated. Only a rule with an open, measured finding belongs here: colour contrast
 * is U8's (25 nodes across the scans on 2026-09-17), and gating it would block every PR until U8.
 */
const REPORT_ONLY_RULES = ['color-contrast'];

/**
 * Fluent's focus sentinels: `<i tabindex="0" role="none" data-tabster-dummy aria-hidden="true">`,
 * two on every page, which Tabster uses to catch Tab at the edges of its groups. They are
 * focusable and aria-hidden on purpose, so `aria-hidden-focus` reports them on every scan (2 nodes
 * each on nine of ten on 2026-09-17). The rule still runs on everything else, and `scan` asserts
 * that the selector matches only that markup.
 */
const TABSTER_SENTINEL = '[data-tabster-dummy]';

/**
 * The two shapes a sentinel takes, measured 2026-09-18 over seven pages and both dialogs (26
 * sentinels): `tabindex="0"` on a page, and `tabindex="-1"` on the page's two while a dialog is
 * open over it, when Tabster takes them out of the tab order. Anything else carrying the attribute
 * is not a sentinel, and hiding it from axe would hide a finding.
 */
const SENTINEL_SHAPES = ['0', '-1']
  .map((tabindex) => `i[tabindex="${tabindex}"][role="none"][aria-hidden="true"]`)
  .join(', ');

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

  // The exclusion hides sentinels and nothing else: anything else carrying the attribute fails.
  await expect(
    page.locator(`${TABSTER_SENTINEL}:not(${SENTINEL_SHAPES})`),
    `${name}: the sentinel selector matches something that is not a sentinel`,
  ).toHaveCount(0);
  const excludedElements = await page.locator(TABSTER_SENTINEL).count();

  const builder = new AxeBuilder({ page }).withTags(TAGS).exclude(TABSTER_SENTINEL);
  if (scope) {
    // One element, or the scope is not the thing named: none scans nothing, two scan a stranger.
    await expect(page.locator(scope), `${name}: the scope must name one element`).toHaveCount(1);
    builder.include(scope);
  }
  const results = await builder.analyze();

  // The control, before anything is written: a scan with no passes scanned nothing.
  expect(results.passes.length, `${name}: axe inspected no element at all`).toBeGreaterThan(0);

  // An exemption must not become a disabled rule: both rules the gate reasons about ran.
  const ran = new Set(
    [...results.passes, ...results.violations, ...results.incomplete, ...results.inapplicable].map(
      (r) => r.id,
    ),
  );
  for (const rule of [...REPORT_ONLY_RULES, 'aria-hidden-focus']) {
    expect(ran.has(rule), `${name}: axe did not run ${rule}`).toBe(true);
  }

  const report = {
    page: name,
    url: page.url(),
    scope: scope ?? null,
    tags: TAGS,
    axe: results.testEngine.version,
    gate: {
      impacts: GATED_IMPACTS,
      reportOnly: REPORT_ONLY_RULES,
      excluded: TABSTER_SENTINEL,
      excludedElements,
    },
    passes: results.passes.length,
    incomplete: results.incomplete.map((r) => ({
      id: r.id,
      impact: r.impact,
      nodes: r.nodes.length,
    })),
    violations: results.violations.map((v) => ({
      id: v.id,
      impact: v.impact,
      gated:
        (v.impact == null || GATED_IMPACTS.includes(v.impact)) && !REPORT_ONLY_RULES.includes(v.id),
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

  const summary = report.violations
    .map((v) => `${v.id} (${v.impact}, ${v.nodes}${v.gated ? ', gated' : ''})`)
    .join(', ');
  console.log(`axe ${name}: ${report.violations.length} violations — ${summary || 'none'}`);

  // Last, so a red scan still leaves its report behind.
  expect(
    report.violations.filter((v) => v.gated),
    `${name}: a serious or critical finding outside ${REPORT_ONLY_RULES.join(', ')}; see ${REPORT_DIR}/${name}.json`,
  ).toEqual([]);
  return report;
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
