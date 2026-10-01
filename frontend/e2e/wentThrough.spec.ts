import { mkdir } from 'node:fs/promises';
import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { USER } from './fixtures';

/**
 * A payment the server says went through, when its receipt cannot be shown.
 *
 * The API answers a money send whose commit is in the database and whose answer was lost with
 * 409 `IDEMPOTENCY_RESULT_UNKNOWN` and `applied: true` (ADR-0009). The page then says so, under the
 * flow's own success title, and offers the history; it no longer draws the check view, whose second
 * button said the payment had not gone through and led to a second one under a new key.
 *
 * As in `slowService.spec.ts`, the answer is made in the browser, not in the stack: `page.route`
 * answers the send with the wire's own 409, so NO MONEY MOVES and the seeded account is left as it
 * was. The authorisations of the withdrawal and of the transfer are minted for real, with the right
 * PIN, and expire unused. What is proven here is the SPA's reaction to a copied wire, in a real
 * browser: focus, the Tab order, the layout at phone width and in the dark theme, and axe. The wire
 * itself is pinned by the backend's tests, and the real sequence (a commit, a lost answer, the same
 * key after the stale age) is run by hand against the compose stack.
 *
 * WHERE THE BODY BELOW COMES FROM. Its members, their order, the status and the headers are the 409
 * measured on the running stack on 2026-10-01 for a deposit and a transfer whose answer had been
 * lost (`application/json; charset=utf-8`, `no-cache,no-store`), to which this adds the `applied`
 * member and the sentence the API now sends with it.
 */

const SENTENCE = {
  deposit: "Your deposit went through, but we couldn't show its receipt.",
  withdrawal: "Your withdrawal went through, but we couldn't show its receipt.",
  transfer: "Your transfer went through, but we couldn't show its receipt.",
} as const;

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'];
const GATED_IMPACTS = ['serious', 'critical'];
/** Fluent's focus sentinels, focusable and aria-hidden on purpose: see `accessibility.spec.ts`. */
const TABSTER_SENTINEL = '[data-tabster-dummy]';
const SHOTS = 'test-results/went-through';

/** The API's 409 for a send that was committed and whose answer was lost, for `path`. */
function wentThroughBody(path: string) {
  return JSON.stringify({
    type: 'https://httpstatuses.com/409',
    title: 'Conflict',
    status: 409,
    detail:
      'The operation sent with this idempotency key was applied, but this request cannot return its result. Do not send it again with a new key: look for it with GET /api/transactions.',
    instance: path,
    errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
    traceId: '0af7651916cd43dd8448eb211c80319c',
    applied: true,
  });
}

/**
 * Answers every send to `path` in the browser with that 409, and keeps each one's key. The glob
 * ends at the path, so the mint beside it (`…/authorizations`) goes to the real API.
 */
async function answerThatItWentThrough(page: Page, path: string) {
  const keys: string[] = [];
  await page.route(`**${path}`, async (route) => {
    keys.push(route.request().headers()['idempotency-key'] ?? '(missing)');
    await route.fulfill({
      status: 409,
      headers: {
        'Content-Type': 'application/json; charset=utf-8',
        'Cache-Control': 'no-cache,no-store',
      },
      body: wentThroughBody(path),
    });
  });
  return keys;
}

async function enterPin(scope: Page | Locator) {
  for (const [index, digit] of [...USER.pin].entries()) {
    await scope.getByRole('textbox', { name: `Digit ${index + 1} of 6` }).fill(digit);
  }
}

/** What each button is called: its label when it has no text of its own (a dialog's X). */
function buttonNames(scope: Page | Locator) {
  return scope
    .getByRole('button')
    .evaluateAll((buttons) =>
      buttons.map((button) => button.getAttribute('aria-label') ?? button.textContent),
    );
}

/** A deposit of €1 from the dashboard, answered that it went through; returns the dialog. */
async function depositThatWentThrough(page: Page) {
  const keys = await answerThatItWentThrough(page, '/api/transactions/deposit');
  await page.goto('/dashboard');
  await page.getByRole('button', { name: 'Deposit', exact: true }).click();
  const form = page.getByRole('dialog', { name: /deposit money/i });
  await form.getByRole('textbox', { name: 'Deposit amount' }).fill('1');
  await form.getByRole('button', { name: 'Deposit €1.00' }).click();

  // The answer has been drawn once the send was answered and Close, barred while the key was
  // live, is back: true of whatever the dialog made of it.
  await expect.poll(() => keys.length).toBe(1);
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('button', { name: 'Close' })).toBeEnabled();
  // The check view is what the dialog drew for this answer before it read `applied`.
  await expect(dialog.getByText("We couldn't confirm your deposit")).toBeHidden();
  await expect(dialog).toHaveAccessibleName('Deposit Complete');
  return { dialog, keys };
}

/** A transfer of €1 to a seeded payee, answered that it went through. */
async function transferThatWentThrough(page: Page) {
  const keys = await answerThatItWentThrough(page, '/api/transfers');
  await page.goto('/transfer');
  await page.getByRole('textbox', { name: 'Recipient handle' }).fill('janesmith');
  await page.getByRole('button', { name: 'Verify' }).click();
  await page.getByRole('textbox', { name: 'Transfer amount' }).fill('1');
  await page.getByRole('button', { name: 'Review Transfer' }).click();
  await page.getByRole('button', { name: 'Continue' }).click();
  // The sixth digit sends: the mint is real, the send is answered by the route above.
  await enterPin(page);

  // The answer has been drawn once the send was answered and the PIN step is gone.
  await expect.poll(() => keys.length).toBe(1);
  await expect(page.getByRole('textbox', { name: 'Digit 1 of 6' })).toHaveCount(0);
  // The check view is what the page drew for this answer before it read `applied`.
  await expect(page.getByText("We couldn't confirm your transfer")).toBeHidden();
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Transfer Complete');
  return { keys };
}

/** The sentence is where focus is, one Tab reaches "View History", and it leads to the history. */
async function expectReadThenLed(page: Page, sentence: Locator, viewHistory: Locator) {
  await expect(sentence).toBeVisible();
  await expect(sentence).toBeFocused();
  await expect(page.getByText(/didn't go through/)).toHaveCount(0);

  await page.keyboard.press('Tab');
  await expect(viewHistory).toBeFocused();

  await viewHistory.click();
  await expect(page).toHaveURL(/\/history(?:[?#]|$)/);
}

test.describe('a payment the server says went through', () => {
  test('a deposit says so in its dialog, with focus on the sentence and the history one Tab away', async ({
    page,
  }) => {
    const { dialog, keys } = await depositThatWentThrough(page);

    // The dialog's X, and the one action: nothing that could send again.
    expect(await buttonNames(dialog)).toEqual(['Close', 'View History']);
    await expectReadThenLed(
      page,
      dialog.getByText(SENTENCE.deposit),
      dialog.getByRole('button', { name: 'View History' }),
    );
    expect(keys).toHaveLength(1);
  });

  test('a withdrawal says so in its dialog, with focus on the sentence and the history one Tab away', async ({
    page,
  }) => {
    const keys = await answerThatItWentThrough(page, '/api/transactions/withdraw');
    await page.goto('/dashboard');
    await page.getByRole('button', { name: 'Withdraw', exact: true }).click();
    const form = page.getByRole('dialog', { name: /withdraw money/i });
    await form.getByRole('textbox', { name: 'Withdraw amount' }).fill('1');
    await form.getByRole('button', { name: /^Continue/ }).click();
    await enterPin(form);
    await form.getByRole('button', { name: 'Withdraw €1.00' }).click();

    await expect.poll(() => keys.length).toBe(1);
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('button', { name: 'Close' })).toBeEnabled();
    await expect(dialog.getByText("We couldn't confirm your withdrawal")).toBeHidden();
    await expect(dialog).toHaveAccessibleName('Withdrawal Complete');

    expect(await buttonNames(dialog)).toEqual(['Close', 'View History']);
    await expectReadThenLed(
      page,
      dialog.getByText(SENTENCE.withdrawal),
      dialog.getByRole('button', { name: 'View History' }),
    );
    expect(keys).toHaveLength(1);
  });

  test('a transfer says so on its page, with the receipt’s two ways on and nothing else', async ({
    page,
  }) => {
    const { keys } = await transferThatWentThrough(page);

    // The page has no shell around it: these two are every control on it.
    expect(await buttonNames(page)).toEqual(['View History', 'Done']);
    await expectReadThenLed(
      page,
      page.getByText(SENTENCE.transfer),
      page.getByRole('button', { name: 'View History' }),
    );
    expect(keys).toHaveLength(1);
  });
});

/*
  THE SAME VIEW, MEASURED. The sentence is long: at 20 px bold it does not fit on one line at any
  width this app is used at, so it wraps, and a wrapped line that is left-aligned and tight reads as
  a mistake under a centred icon. Three variants of each shell, because a colour that passes on the
  light canvas can fail on the dark one, and a line that wraps into two at a desktop width can wrap
  into four on a phone.

  In each: a screenshot is kept; axe runs with the sweep's four tags, scoped to exactly one element,
  and fails on a serious or critical finding (colour contrast included: this view is new, and none
  is accepted on it); the sentence's contrast is measured from its computed colours and must be
  4.5:1 or more, and a second, narrower axe run looks only at the sentence and only at contrast and
  must have looked at it; the page does not scroll sideways; the sentence and its action are in the
  viewport; the sentence is centred; and at 375 px it takes three lines or fewer.

  WHY THE CONTRAST IS MEASURED HERE AS WELL AS BY AXE. axe decides a text's background from what
  lies under each of its lines, and it looks through the dialog to the page behind it: for a
  sentence on two lines inside a modal those stacks differ, and axe reports the node as one it
  could not decide ("partially overlaps other elements") instead of passing or failing it.
  Measured on the deposit dialog, light, dark and at 375 px; on the transfer page, whose
  background is one element, axe decides and agrees with the figure computed here. So the figure
  is the gate, and axe's run is required to have looked at the sentence and found no violation.
*/
const VARIANTS: { name: string; phone: boolean; prepare: (page: Page) => Promise<void> }[] = [
  { name: 'light', phone: false, prepare: async () => {} },
  { name: 'dark', phone: false, prepare: (page) => page.emulateMedia({ colorScheme: 'dark' }) },
  {
    name: '375',
    phone: true,
    prepare: (page) => page.setViewportSize({ width: 375, height: 812 }),
  },
];

/**
 * The WCAG contrast ratio of an element's text against what is painted behind it: its computed
 * colour, and the first opaque background colour among its ancestors. `null` when either is not a
 * plain opaque colour, which a test must treat as "not measured", never as a pass.
 */
function contrastOf(element: Element) {
  const channels = (colour: string) => {
    const parts = /^rgba?\(([^)]+)\)$/
      .exec(colour)?.[1]
      .split(/[\s,/]+/)
      .map(Number);
    if (!parts || parts.length < 3 || parts.some(Number.isNaN)) return null;
    return { rgb: parts.slice(0, 3), alpha: parts[3] ?? 1 };
  };
  const luminance = (rgb: number[]) => {
    const [r, g, b] = rgb.map((value) => {
      const c = value / 255;
      return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  };

  const text = channels(getComputedStyle(element).color);
  let background: ReturnType<typeof channels> = null;
  for (let node: Element | null = element; node; node = node.parentElement) {
    const painted = channels(getComputedStyle(node).backgroundColor);
    if (painted && painted.alpha > 0) {
      background = painted;
      break;
    }
  }
  if (!text || text.alpha !== 1 || !background || background.alpha !== 1) return null;

  const [lighter, darker] = [luminance(text.rgb), luminance(background.rgb)].sort((a, b) => b - a);
  return {
    ratio: Math.round(((lighter + 0.05) / (darker + 0.05)) * 100) / 100,
    text: text.rgb.join(' '),
    background: background.rgb.join(' '),
  };
}

/** How many lines the element's text is laid out on: the distinct tops of its line boxes. */
function lineCount(element: Element) {
  const range = document.createRange();
  range.selectNodeContents(element);
  return new Set(Array.from(range.getClientRects()).map((box) => Math.round(box.top))).size;
}

async function measure(
  page: Page,
  name: string,
  scope: string,
  sentenceText: string,
  phone: boolean,
) {
  // The sentence, by what it is: the one paragraph that takes focus without being in the Tab order.
  const sentenceSelector = `${scope} p[tabindex="-1"]`;
  const sentence = page.locator(sentenceSelector);
  await expect(sentence, `${name}: the sentence must be one focusable paragraph`).toHaveCount(1);
  await expect(sentence).toHaveText(sentenceText);
  const viewHistory = page.locator(scope).getByRole('button', { name: 'View History' });

  // A settled view, not a fading one: axe reads computed colours (see `accessibility.spec.ts`).
  await page.evaluate(() =>
    Promise.all(
      document
        .getAnimations()
        .filter((a) => a.effect?.getComputedTiming().iterations !== Infinity)
        .map((a) => a.finished.catch(() => undefined)),
    ),
  );

  await mkdir(SHOTS, { recursive: true });
  const shot = await page.screenshot({ path: `${SHOTS}/${name}.png` });
  await test.info().attach(name, { body: shot, contentType: 'image/png' });

  // Layout.
  const sideways = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(sideways, `${name}: the page scrolls sideways`).toBeLessThanOrEqual(0);
  await expect(sentence, `${name}: the sentence is not whole in the viewport`).toBeInViewport({
    ratio: 1,
  });
  await expect(viewHistory, `${name}: "View History" is not whole in the viewport`).toBeInViewport({
    ratio: 1,
  });
  expect(
    await sentence.evaluate((element) => getComputedStyle(element).textAlign),
    `${name}: the sentence is not centred`,
  ).toBe('center');
  const lines = await sentence.evaluate(lineCount);
  console.log(`went-through ${name}: the sentence takes ${lines} line(s)`);
  if (phone) {
    expect(
      lines,
      `${name}: the sentence takes more than three lines at 375 px`,
    ).toBeLessThanOrEqual(3);
  }

  // axe over the whole view: one element, at least one rule passed, nothing serious or critical.
  await expect(page.locator(scope), `${name}: the scope must name one element`).toHaveCount(1);
  const results = await new AxeBuilder({ page })
    .withTags(TAGS)
    .include(scope)
    .exclude(TABSTER_SENTINEL)
    .analyze();
  expect(results.passes.length, `${name}: axe inspected no element at all`).toBeGreaterThan(0);
  const gated = results.violations
    .filter((violation) => violation.impact == null || GATED_IMPACTS.includes(violation.impact))
    .map((violation) => `${violation.id} (${violation.impact}, ${violation.nodes.length})`);
  console.log(
    `axe went-through ${name}: ${results.passes.length} passed, ${results.violations.length} violations — ${
      results.violations.map((v) => `${v.id} (${v.impact}, ${v.nodes.length})`).join(', ') || 'none'
    }`,
  );
  expect(gated, `${name}: a serious or critical finding on the went-through view`).toEqual([]);

  // The sentence alone, contrast alone, measured from its computed colours.
  const measured = await sentence.evaluate(contrastOf);
  expect(measured, `${name}: the sentence's colours are not plain opaque colours`).not.toBeNull();

  // And axe on the sentence alone: a sentence it never looked at would pass the scan above. It
  // passes the node, or says it could not decide it (the note above); it must not skip it.
  const contrast = await new AxeBuilder({ page })
    .include(sentenceSelector)
    .withRules(['color-contrast'])
    .analyze();
  const passed = contrast.passes.find((rule) => rule.id === 'color-contrast')?.nodes ?? [];
  const undecided = contrast.incomplete.find((rule) => rule.id === 'color-contrast')?.nodes ?? [];
  const axeRatios = passed.flatMap((node) =>
    node.any.map((check) => (check.data as { contrastRatio?: number } | null)?.contrastRatio),
  );
  const reasons = undecided.flatMap((node) => node.any.map((check) => check.message));
  console.log(
    `went-through ${name}: contrast of the sentence ${measured?.ratio}:1 ` +
      `(text ${measured?.text} on ${measured?.background}); axe: ` +
      (axeRatios.length > 0
        ? `passed at ${axeRatios.join(', ')}`
        : `undecided — ${reasons.join(' | ')}`),
  );
  expect(measured?.ratio, `${name}: the sentence's contrast`).toBeGreaterThanOrEqual(4.5);
  expect(
    passed.length + undecided.length,
    `${name}: axe did not look at the sentence at all`,
  ).toBeGreaterThan(0);
  expect(
    contrast.violations.flatMap((rule) => rule.nodes.map((node) => node.target.join(' '))),
    `${name}: the sentence fails colour contrast`,
  ).toEqual([]);
}

test.describe('the went-through view, measured', () => {
  for (const { name, phone, prepare } of VARIANTS) {
    test(`the deposit dialog (${name}) is centred, in view, and has no serious or critical finding`, async ({
      page,
    }) => {
      await prepare(page);
      await depositThatWentThrough(page);
      await measure(page, `deposit-dialog-${name}`, '[role="dialog"]', SENTENCE.deposit, phone);
    });

    test(`the transfer page (${name}) is centred, in view, and has no serious or critical finding`, async ({
      page,
    }) => {
      await prepare(page);
      await transferThatWentThrough(page);
      await measure(page, `transfer-page-${name}`, '#root', SENTENCE.transfer, phone);
    });
  }
});
