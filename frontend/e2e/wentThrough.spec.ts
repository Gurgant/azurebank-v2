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

/**
 * The PIN, typed as a visitor types it: one key per digit, each box handing focus to the next.
 * Typed and not filled, because it decides what is drawn afterwards: a browser matches
 * `:focus-visible` on an element that a script gave focus to when the visitor's last action was a
 * key, and a filled box is no key at all.
 */
async function enterPin(scope: Page | Locator) {
  await scope.getByRole('textbox', { name: 'Digit 1 of 6' }).pressSequentially(USER.pin);
}

/** What each button is called: its label when it has no text of its own (a dialog's X). */
function buttonNames(scope: Page | Locator) {
  return scope
    .getByRole('button')
    .evaluateAll((buttons) =>
      buttons.map((button) => button.getAttribute('aria-label') ?? button.textContent),
    );
}

/** The outline the browser computes for an element, as the three values the ring is made of. */
function focusRing(element: Element) {
  const style = getComputedStyle(element);
  return { style: style.outlineStyle, width: style.outlineWidth, offset: style.outlineOffset };
}

/** The app's ring, as `index.css` draws it on a container that was given focus. */
const APP_RING = { style: 'solid', width: '2px', offset: '2px' };

/**
 * How far the drawn ring reaches past the viewport, in px, on its worst side: 0 or less when the
 * whole ring is on screen. The ring is the element's box grown by the outline's offset and width.
 */
function ringOverflow(element: Element) {
  const style = getComputedStyle(element);
  const reach = parseFloat(style.outlineOffset) + parseFloat(style.outlineWidth);
  const box = element.getBoundingClientRect();
  return Math.max(
    reach - box.left,
    reach - box.top,
    box.right + reach - document.documentElement.clientWidth,
    box.bottom + reach - document.documentElement.clientHeight,
  );
}

/**
 * Opens one of the dashboard's two money dialogs once the accounts are on the page, and returns it
 * with its account chosen.
 *
 * The wait is not a courtesy. "Deposit" and "Withdraw" can be pressed before the accounts have
 * loaded, and a dialog picks its account when it opens: opened early it has none, and its send
 * button stays disabled whatever is typed. Measured with the accounts read held back 1.5 s: no
 * account selected, "Deposit €1.00" disabled. The balance, the page's level-1 heading, is drawn
 * only once the accounts have arrived.
 */
async function openMoneyDialog(page: Page, opener: 'Deposit' | 'Withdraw', name: RegExp) {
  await page.goto('/dashboard');
  await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();
  await page.getByRole('button', { name: opener, exact: true }).click();
  const form = page.getByRole('dialog', { name });
  await expect(form.locator('[aria-pressed="true"]'), 'no account is selected').toHaveCount(1);
  return form;
}

/**
 * A deposit of €1 from the dashboard, answered that it went through; returns the dialog. `send` is
 * how the Deposit button is pressed: the browser shows a focus ring after a key, not after a click.
 */
async function depositThatWentThrough(page: Page, send: 'click' | 'Enter' = 'click') {
  const keys = await answerThatItWentThrough(page, '/api/transactions/deposit');
  const form = await openMoneyDialog(page, 'Deposit', /deposit money/i);
  await form.getByRole('textbox', { name: 'Deposit amount' }).fill('1');
  const deposit = form.getByRole('button', { name: 'Deposit €1.00' });
  if (send === 'Enter') {
    // A click waits for the button to be enabled; a key press does not.
    await expect(deposit).toBeEnabled();
    await deposit.press('Enter');
  } else {
    await deposit.click();
  }

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

/**
 * A withdrawal of €1 from the dashboard, answered that it went through; returns the dialog. The PIN
 * is typed either way; `send` is how the Withdraw button is then pressed.
 */
async function withdrawalThatWentThrough(page: Page, send: 'click' | 'Enter' = 'click') {
  const keys = await answerThatItWentThrough(page, '/api/transactions/withdraw');
  const form = await openMoneyDialog(page, 'Withdraw', /withdraw money/i);
  await form.getByRole('textbox', { name: 'Withdraw amount' }).fill('1');
  await form.getByRole('button', { name: /^Continue/ }).click();
  await enterPin(form);
  const withdraw = form.getByRole('button', { name: 'Withdraw €1.00' });
  if (send === 'Enter') {
    // A click waits for the button to be enabled; a key press does not.
    await expect(withdraw).toBeEnabled();
    await withdraw.press('Enter');
  } else {
    await withdraw.click();
  }

  await expect.poll(() => keys.length).toBe(1);
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('button', { name: 'Close' })).toBeEnabled();
  // The check view is what the dialog drew for this answer before it read `applied`.
  await expect(dialog.getByText("We couldn't confirm your withdrawal")).toBeHidden();
  await expect(dialog).toHaveAccessibleName('Withdrawal Complete');
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
  // The sixth digit sends: the mint is real, the send is answered by the route above. So the
  // visitor's last action before the view is a key.
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
  // Focused, and not a stop of its own in the Tab order.
  await expect(sentence).toHaveAttribute('tabindex', '-1');
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
    const { dialog, keys } = await withdrawalThatWentThrough(page);

    expect(await buttonNames(dialog)).toEqual(['Close', 'View History']);
    // The PIN was typed, but the send was a click on Withdraw: no ring around the sentence. Focus
    // first: a sentence that focus never reached has no ring either.
    await expect(dialog.getByText(SENTENCE.withdrawal)).toBeFocused();
    expect((await dialog.getByText(SENTENCE.withdrawal).evaluate(focusRing)).style).toBe('none');
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

  test('in a dialog the sentence shows the app’s focus ring when the send was a key, and none after a click', async ({
    page,
  }) => {
    // Focus lands on the sentence either way. What is drawn follows the visitor's last action:
    // after a click on Deposit the browser does not match :focus-visible on the sentence, so no
    // box is drawn around the paragraph.
    const clicked = await depositThatWentThrough(page);
    const afterClick = clicked.dialog.getByText(SENTENCE.deposit);
    await expect(afterClick).toBeFocused();
    expect((await afterClick.evaluate(focusRing)).style).toBe('none');

    // After a key it does, and the ring is this app's, not the browser's default (`auto`).
    const pressed = await depositThatWentThrough(page, 'Enter');
    const afterKey = pressed.dialog.getByText(SENTENCE.deposit);
    await expect(afterKey).toBeFocused();
    expect(await afterKey.evaluate(focusRing)).toEqual(APP_RING);

    // The withdrawal's button is another node in another footer: Enter on Withdraw, the same ring.
    const withdrawn = await withdrawalThatWentThrough(page, 'Enter');
    const afterKeyOnWithdraw = withdrawn.dialog.getByText(SENTENCE.withdrawal);
    await expect(afterKeyOnWithdraw).toBeFocused();
    expect(await afterKeyOnWithdraw.evaluate(focusRing)).toEqual(APP_RING);
  });

  test('a second press where Deposit was, outside the shorter dialog, leaves the sentence on screen', async ({
    page,
  }) => {
    /*
      A double click on Deposit. The went-through view is shorter than the form, so the dialog
      shrinks under the pointer, and the place the button was is outside it: the second press
      lands on the backdrop. Measured on the running stack at this viewport: the dialog was gone a
      tenth of a second after its sentence appeared, and the visitor was left on the dashboard,
      told nothing. The press outside must leave the view where it is; Escape and the X close it.
    */
    await page.setViewportSize({ width: 1280, height: 900 });
    const keys = await answerThatItWentThrough(page, '/api/transactions/deposit');
    const form = await openMoneyDialog(page, 'Deposit', /deposit money/i);
    await form.getByRole('textbox', { name: 'Deposit amount' }).fill('1');
    const deposit = form.getByRole('button', { name: 'Deposit €1.00' });
    await expect(deposit).toBeEnabled();
    const box = await deposit.boundingBox();
    if (!box) throw new Error('the Deposit button has no box to press');
    const where = { x: box.x + box.width / 2, y: box.y + box.height / 2 };

    await page.mouse.click(where.x, where.y);
    await expect.poll(() => keys.length).toBe(1);
    const dialog = page.getByRole('dialog');
    await expect(dialog).toHaveAccessibleName('Deposit Complete');
    const sentence = dialog.getByText(SENTENCE.deposit);
    await expect(sentence).toBeFocused();

    // What this test stages: the second press is on the backdrop, not on anything in the dialog.
    // The press is recorded as the page receives it, so "the dialog is still there" below is said
    // after a press that did arrive, and on that element.
    await page.evaluate(() => {
      const record = window as unknown as { pressedOn?: string };
      record.pressedOn = undefined;
      document.addEventListener(
        'click',
        (event) => {
          record.pressedOn = (event.target as Element).className;
        },
        { capture: true, once: true },
      );
    });
    await page.mouse.click(where.x, where.y);
    const pressedOn = await page.evaluate(
      () => (window as unknown as { pressedOn?: string }).pressedOn,
    );
    expect(pressedOn, 'the second press did not land on the backdrop').toContain(
      'fui-DialogSurface__backdrop',
    );

    await expect(dialog).toBeVisible();
    await expect(dialog).toHaveAccessibleName('Deposit Complete');
    await expect(sentence).toBeVisible();
    expect(page.url()).toMatch(/\/dashboard(?:[?#]|$)/);
    expect(keys).toHaveLength(1);
    // Nor does it take focus out of the dialog: from `body` Escape reaches no dialog, and the
    // view that stayed would have lost its keyboard exit.
    await expect(sentence, 'the press outside took focus off the sentence').toBeFocused();

    // Not a trap: the exit nobody presses by accident closes it.
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    expect(keys).toHaveLength(1);
  });

  test('on a transfer page the PIN is typed, so the sentence shows the app’s focus ring', async ({
    page,
  }) => {
    // Not the keyboard visitor's case alone: the sixth digit is the send, so when that send is
    // the one answered, whoever typed the PIN arrives at the view from a key and the browser
    // draws a ring around the sentence. It must be this app's, whole on screen, and not the
    // browser's default (`auto`).
    await transferThatWentThrough(page);
    const sentence = page.getByText(SENTENCE.transfer);
    await expect(sentence).toBeFocused();
    expect(await sentence.evaluate((element) => element.matches(':focus-visible'))).toBe(true);
    expect(await sentence.evaluate(focusRing)).toEqual(APP_RING);
    expect(await sentence.evaluate(ringOverflow)).toBeLessThanOrEqual(0);
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

  EACH SHELL AS ITS VISITOR MEETS IT. The transfer page is reached with the PIN typed, so its
  three pictures have the focus ring around the sentence, and the ring must be the app's and whole
  in the viewport, at 375 px too. The deposit is sent with a click, so its three have none.

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
  ringDrawn: boolean,
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

  // What is drawn around the sentence, checked before the picture is taken so that each picture
  // is known to show its case: the app's ring, whole on screen, or no ring at all. Focus first,
  // for both: a sentence that focus never reached has no ring, and would pass for the second.
  await expect(sentence, `${name}: focus is not on the sentence`).toBeFocused();
  const ring = await sentence.evaluate(focusRing);
  if (ringDrawn) {
    expect(ring, `${name}: the ring around the sentence is not the app's`).toEqual(APP_RING);
    expect(
      await sentence.evaluate(ringOverflow),
      `${name}: the ring around the sentence leaves the viewport`,
    ).toBeLessThanOrEqual(0);
  } else {
    expect(ring.style, `${name}: a ring is drawn around the sentence after a click`).toBe('none');
  }

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
      await measure(
        page,
        `deposit-dialog-${name}`,
        '[role="dialog"]',
        SENTENCE.deposit,
        phone,
        false,
      );
    });

    test(`the transfer page (${name}) is centred, in view, and has no serious or critical finding`, async ({
      page,
    }) => {
      await prepare(page);
      await transferThatWentThrough(page);
      await measure(page, `transfer-page-${name}`, '#root', SENTENCE.transfer, phone, true);
    });
  }
});
