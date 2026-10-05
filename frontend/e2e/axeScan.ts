import { mkdir, writeFile } from 'node:fs/promises';
import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * The accessibility gate, in one place for every Playwright run that scans a page.
 *
 * `scan` runs axe-core over a page, or over one element of it, writes what it found, and fails on
 * a serious or critical finding. The default suite's sweep calls it for each of its scans
 * (`accessibility.spec.ts`, whose header says what is scanned and what else fails a scan). It is a
 * module of its own so that a second run scans through the same function: two copies of the tags,
 * the gated impacts and the exclusion would be two gates, and nothing would say when they stopped
 * agreeing.
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

/**
 * Scans the page at `path`, or the one element of it that `scope` names, writes the findings to
 * `test-results/axe/<name>.json`, attaches the same report to the test, and then fails on a
 * serious or critical finding outside `REPORT_ONLY_RULES`.
 *
 * `nodeMarkup: false` leaves every node's markup out of the report: a target keeps its selector
 * and has no `html` member. A report quotes the markup of what it found, and colour contrast is
 * found and never gated, so a scan of a page that shows what no file may keep would write it
 * down, in the report and in the attachment, on a green run as on a red one. Left out, or `true`,
 * the report is the one the sweep has always written. The demo's run asks for it where it scans
 * a dashboard that shows a copy's email and password, and its first test holds that the report
 * then quotes nothing (`../e2e-demo/demo.spec.ts`).
 */
export async function scan(
  page: Page,
  name: string,
  path: string,
  scope?: string,
  { nodeMarkup = true }: { nodeMarkup?: boolean } = {},
) {
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
      // characters of each node's markup do, for the first five nodes of each violation. The
      // selector alone where the caller asked for no markup.
      targets: v.nodes.slice(0, 5).map((n) => {
        const target = n.target.join(' ');
        return nodeMarkup ? { target, html: n.html.slice(0, 160) } : { target };
      }),
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
