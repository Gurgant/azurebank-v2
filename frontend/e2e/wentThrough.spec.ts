import { mkdir } from 'node:fs/promises';
import { AxeBuilder } from '@axe-core/playwright';
import { expect, test, type Locator, type Page } from '@playwright/test';
import { WAIT_SLOW } from '../src/api/problemMessages';
import { SETTLE_MS } from '../src/hooks/useSettledPress';
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
 * itself is pinned by the backend's tests. The real sequence (a commit, a lost answer, the same key
 * after the stale age) is NOT staged here: it takes a second database session and two minutes for
 * each payment. ADR-0059's Validation says when it was run on the compose stack, and what was
 * measured.
 *
 * WHERE THE BODY BELOW COMES FROM. It is the 409 measured in that run, on 2026-10-01, at the
 * browser and through the BFF, for a deposit, a withdrawal and both transfers whose answer had been
 * lost: the eight members in this order, the sentence, the status and the two headers
 * (`application/json; charset=utf-8`, `no-cache,no-store`). Byte for byte the measured body once
 * its `traceId`, which every answer mints, is replaced by the one below.
 */

const SENTENCE = {
  deposit: "Your deposit went through, but we couldn't show its receipt.",
  withdrawal: "Your withdrawal went through, but we couldn't show its receipt.",
  transfer: "Your transfer went through, but we couldn't show its receipt.",
} as const;

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'];
const GATED_IMPACTS = ['serious', 'critical'];
/** Fluent's focus sentinels, focusable and aria-hidden on purpose: see `axeScan.ts`. */
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
 * The API's 409 for a send whose record is gone, for `path`: the same code and no `applied` member
 * at all, so nothing is proven and the page draws the check view. The sentence is the one the API
 * has for that answer. Unlike the body above, this one was not read at the browser on a running
 * stack: it is that body without its last member, with the other sentence.
 */
function notKnownBody(path: string) {
  return JSON.stringify({
    type: 'https://httpstatuses.com/409',
    title: 'Conflict',
    status: 409,
    detail:
      'A request with this idempotency key may have been executed: its record is no longer there, so the outcome is not known. Verify via GET /api/transactions before sending it again with a new key.',
    instance: path,
    errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
    traceId: '0af7651916cd43dd8448eb211c80319c',
  });
}

/**
 * Answers every send to `path` in the browser with a 409 whose body is `body`, and keeps each one's
 * key. The glob ends at the path, so the mint beside it (`…/authorizations`) goes to the real API.
 * With `held`, the answer waits for it: the key is kept as the send arrives, the 409 goes out when
 * it resolves.
 */
async function answerEverySend(page: Page, path: string, body: string, held?: Promise<void>) {
  const keys: string[] = [];
  await page.route(`**${path}`, async (route) => {
    keys.push(route.request().headers()['idempotency-key'] ?? '(missing)');
    await held;
    await route.fulfill({
      status: 409,
      headers: {
        'Content-Type': 'application/json; charset=utf-8',
        'Cache-Control': 'no-cache,no-store',
      },
      body,
    });
  });
  return keys;
}

/** Every send to `path` is answered that it went through: the 409 with `applied: true`. */
function answerThatItWentThrough(page: Page, path: string, held?: Promise<void>) {
  return answerEverySend(page, path, wentThroughBody(path), held);
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

/**
 * Where a click landed, as the page received it: the target's class, the control it is in, when it
 * came on the page's `performance.now()`, and whether it came back up to the document, which it
 * does only when nothing in the page stopped it on its way to that control. With the element that
 * holds a view's actions given, also whether the press went down inside it, and whether anything
 * in the page refused the default of its `mousedown`, which is what gives focus.
 */
type Landing = {
  className: string;
  control: string | null;
  at: number;
  delivered: boolean;
  inActions: boolean | null;
  refused: boolean | null;
};

/**
 * Presses the point and returns where the press landed: the class of the element the click went
 * to, and the name of the control around it (a button, a link, a field), or null when it is in
 * none. Recorded as the page receives the click, so what a test says after it is said after a
 * press that did arrive, and on that element.
 */
async function pressAt(
  page: Page,
  point: { x: number; y: number },
  actions?: Locator,
): Promise<Landing | undefined> {
  const actionsElement = actions ? await actions.elementHandle() : null;
  await page.evaluate((holder) => {
    const record = window as unknown as {
      landing?: Landing;
      wentDown?: Event;
      stopRecording?: () => void;
    };
    record.landing = undefined;
    record.wentDown = undefined;
    let pressed: Event | null = null;
    const onGoingDown = (event: Event) => {
      record.wentDown = event;
    };
    const onTheWayDown = (event: Event) => {
      pressed = event;
      const target = event.target as Element;
      const control = target.closest(
        'button, a, input, select, textarea, [role="button"], [role="link"]',
      );
      const downOn = record.wentDown?.target;
      record.landing = {
        className: String(target.className),
        control: control
          ? (control.getAttribute('aria-label') ?? control.textContent ?? '').trim()
          : null,
        at: performance.now(),
        delivered: false,
        inActions: holder && downOn instanceof Node ? holder.contains(downOn) : null,
        refused: null,
      };
    };
    const onTheWayUp = (event: Event) => {
      if (event === pressed && record.landing) record.landing.delivered = true;
    };
    document.addEventListener('mousedown', onGoingDown, { capture: true, once: true });
    document.addEventListener('click', onTheWayDown, { capture: true, once: true });
    document.addEventListener('click', onTheWayUp);
    record.stopRecording = () => document.removeEventListener('click', onTheWayUp);
  }, actionsElement);
  await page.mouse.click(point.x, point.y);
  return page.evaluate(() => {
    const record = window as unknown as {
      landing?: Landing;
      wentDown?: Event;
      stopRecording?: () => void;
    };
    record.stopRecording?.();
    // Read once the `mousedown` has been through every handler in the page.
    if (record.landing && record.wentDown) {
      record.landing.refused = record.wentDown.defaultPrevented;
    }
    return record.landing;
  });
}

/** Resolves once every animation on the page that ends has ended. */
function animationsFinished(page: Page) {
  return page.evaluate(() =>
    Promise.all(
      document
        .getAnimations()
        .filter((a) => a.effect?.getComputedTiming().iterations !== Infinity)
        .map((a) => a.finished.catch(() => undefined)),
    ),
  );
}

/**
 * The centre of `button` where a visitor presses it: once the dialog has finished opening, which
 * it does with a 250 ms animation. Read while that runs, the box is smaller and its centre higher:
 * at 1280×900, Deposit's centre at 716.84 px, against 719.5 px at rest.
 */
async function centreAtRest(page: Page, button: Locator) {
  await animationsFinished(page);
  const box = await button.boundingBox();
  if (!box) throw new Error('the button has no box to press');
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
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
 *
 * The opener is pressed inside the dashboard's actions zone: the `section` that holds the Transfer
 * card, with the two tiles. The card's name is its title and then the line under it, whose words
 * change with the recipients and are not matched here. Recent activity and Needs attention draw
 * each row as a button, and a deposit's is named after its description, or its type when it has
 * none (`transactionLabel`): an undescribed deposit is a button named "Deposit", the tile's exact
 * name, drawn before or after this press as the listing arrives. Inside the zone the exact name
 * can only be the tile.
 */
async function openMoneyDialog(page: Page, opener: 'Deposit' | 'Withdraw', name: RegExp) {
  await page.goto('/dashboard');
  await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();
  const actions = page.locator('section').filter({
    has: page.getByRole('button', { name: /^Transfer ./ }),
  });
  await expect(
    actions,
    'no single section on the dashboard holds a button named "Transfer" and a line after it: ' +
      'the actions zone, with the Deposit and Withdraw tiles, was not found',
  ).toHaveCount(1);
  await actions.getByRole('button', { name: opener, exact: true }).click();
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

  // A press on the view's actions acts once the view has settled; a sooner one is dropped.
  await page.waitForTimeout(SETTLE_MS);
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
    const where = await centreAtRest(page, deposit);

    await page.mouse.click(where.x, where.y);
    await expect.poll(() => keys.length).toBe(1);
    const dialog = page.getByRole('dialog');
    await expect(dialog).toHaveAccessibleName('Deposit Complete');
    const sentence = dialog.getByText(SENTENCE.deposit);
    await expect(sentence).toBeFocused();

    // What this test stages: the second press is on the backdrop, not on anything in the dialog.
    const pressedOn = await pressAt(page, where);
    expect(pressedOn?.className, 'the second press did not land on the backdrop').toContain(
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

  /*
    The same double click when the answer proves nothing: a 409 with no `applied`, for which the
    dialog draws the check view. Measured on the running stack at 1280×900 with the code as it
    was before this branch, when the view did not ask the listing by key: the second press landed
    on the backdrop and the dialog was gone; recorded after it, no dialog, the dashboard's
    address, focus on `body`, no alert. So "We couldn't confirm your deposit" went unread, on the
    page whose Deposit tile opens the dialog again (the tile is read in the page's code, and
    pressed by this file's helper). The press outside must leave this view where it is too.

    The view now grows when the listing answers and it says "not yet" in a status region, and
    where a press at Deposit's place lands depends on the size and on that phase. Measured on
    2026-10-17 from the first press at Deposit's centre at rest, sampled at each animation frame
    and at each change to the page that added or removed nodes or changed text (attributes were
    not watched), with the form 637 px tall at the first three sizes:
      - 1280×900: Deposit's centre at 719.5 px. From 36.5 ms, before the listing answers, the view
        is 491.5 px tall, its bottom edge at 695.75 px, and the point on the backdrop. From
        70.6 ms, with "not yet" said, it is 552.5 px tall, its edge at 726.25 px, and the point in
        its footer's padding, 18.25 px under "Check recent transactions".
      - 375×812: Deposit's centre at 675.5 px. From 39 ms, before the listing answers, the view is
        491.5 px tall, its edge at 651.75 px, and the point on the backdrop. From 109.8 ms, with
        "not yet" said, it is 552.5 px tall, its edge at 682.25 px, and the point in its footer's
        padding, 18.25 px under "Check recent transactions".
      - 360×800: Deposit's centre at 669.5 px. From 36.7 ms, before the listing answers, the view
        is 514 px tall, its edge at 657 px, and the point on the backdrop. From 70.5 ms, with
        "not yet" said, it is 597.5 px tall, and the point is on "Check recent transactions"
        (625.75 to 673.75 px).
      - 375×640: Deposit's centre at 559 px. The second press of a double click, once "not yet"
        was said, landed on "Check recent transactions".
      - 320×568: the form and the view are both 511.19 px tall, cut to the surface's `maxHeight`,
        and Deposit's centre is at 490.59 px. With "not yet" said, the view keeps its 389.5 px of
        words, from 106.41 to 495.91 px, and the surface scrolls as a whole: "Check again" is at
        512.91 to 560.91 px and "Check recent transactions" at 572.91 to 620.91 px, below the
        surface's edge at 539.59 px until it is scrolled. The second press of a double click
        landed in the view, on no control.
    That button leaves for the history and takes the view away unread: without a guard, a double
    click made of two clicks with a 130 ms wait between them, its second press 166 ms after the
    first at 360×800 and 162 ms at 375×640, went to `/history` at both, and the dialog was gone.
    So the view's actions drop a press for 500 ms after the view appears and after each change of
    what it draws (`useSettledPress`), and what is under the point no longer decides. With that,
    at 360×800, 375×640 and 320×568, the dialog stayed, the address did not change and one
    deposit was sent. Focus is on the view's title from the moment the view appears: the send
    leaves it on `body`, and the title takes it from there (`useFocusWhenLost`). Measured against
    the mock dev server on 2026-10-23, with the send answered in the page: the second press left
    it on the title at 360×800 and 375×640, and gave it to the dialog itself at 320×568, where
    the press is in the view's words.

    Each test below waits for "not yet" and presses four times. First where Deposit was, as
    soon as "not yet" is said, as the second press of a double click comes: that press must
    reach no control, and give focus to nothing in the dialog, whatever is drawn under it. Then
    outside the view, halfway between its bottom edge and the viewport's, in Deposit's column,
    which is the backdrop. axe then finds nothing serious or critical on the view. Then it
    scrolls the dialog with the wheel until "Check recent transactions" is inside it, and presses
    "Check again" once the wait has passed: a deliberate press acts, and the listing is asked
    once more. Then back up until the X is, and presses the X: the view closes and the dashboard
    is left. That a press on "Check recent transactions" after the wheel acts is held by the test
    of a dialog cut to its height, below.
  */
  for (const { name, width, height } of [
    { name: '1280×900', width: 1280, height: 900 },
    { name: '375×812', width: 375, height: 812 },
    { name: '360×800', width: 360, height: 800 },
    { name: '375×640', width: 375, height: 640 },
    { name: '320×568', width: 320, height: 568 },
  ]) {
    test(`a second press where Deposit was, or outside the check view, leaves it on screen too (${name})`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height });
      const keys = await answerEverySend(
        page,
        '/api/transactions/deposit',
        notKnownBody('/api/transactions/deposit'),
      );
      // Every ask of the listing by key, so that "Check again" below can be seen to ask.
      const asks: string[] = [];
      page.on('request', (request) => {
        if (/\/api\/transactions\?(?:[^#]*&)?IdempotencyKey=/i.test(request.url())) {
          asks.push(request.url());
        }
      });
      const form = await openMoneyDialog(page, 'Deposit', /deposit money/i);
      await form.getByRole('textbox', { name: 'Deposit amount' }).fill('1');
      const deposit = form.getByRole('button', { name: 'Deposit €1.00' });
      await expect(deposit).toBeEnabled();
      const where = await centreAtRest(page, deposit);
      // When "not yet" is first said, on the page's clock: the change the press below follows.
      await page.evaluate((notYet) => {
        const record = window as unknown as { notYetAt?: number };
        const observer = new MutationObserver(() => {
          const said = Array.from(
            document.querySelectorAll('[role="dialog"] [role="status"]'),
          ).some((region) => region.textContent === notYet);
          if (!said) return;
          record.notYetAt = performance.now();
          observer.disconnect();
        });
        observer.observe(document.body, { childList: true, characterData: true, subtree: true });
      }, NOT_YET);

      await page.mouse.click(where.x, where.y);
      await expect.poll(() => keys.length).toBe(1);
      const dialog = page.getByRole('dialog');
      const words = dialog.getByText("We couldn't confirm your deposit");
      // Resolves once the observer above has written the time; `Number` only tells the compiler.
      const notYetAt = Number(
        await (
          await page.waitForFunction(
            () => (window as unknown as { notYetAt?: number }).notYetAt,
            undefined,
            { polling: 'raf' },
          )
        ).jsonValue(),
      );

      // The press where Deposit was, as the second press of a double click comes.
      const checkTransactions = dialog.getByRole('button', { name: 'Check recent transactions' });
      const actions = checkTransactions.locator('..');
      const focusBefore = await page.evaluateHandle(() => document.activeElement);
      const whereDepositWas = await pressAt(page, where, actions);
      if (!whereDepositWas) throw new Error('the press where Deposit was did not arrive');
      // What this test stages: that press came after "not yet" was said, and inside the time the
      // view's actions wait after that change.
      expect(
        whereDepositWas.at - notYetAt,
        'the press where Deposit was came too long after "not yet" to stand for a double click',
      ).toBeLessThan(SETTLE_MS);
      expect(
        whereDepositWas.delivered ? whereDepositWas.control : null,
        'the press where Deposit was reached a control',
      ).toBeNull();
      // A press inside the actions, which they drop, takes no focus. One outside them on no
      // control, on the view's words, which nothing drops, gives focus to the dialog itself, as
      // a press on any text in it does, and to nothing in it: measured at 320×568, where the view
      // keeps its height and the point is in its words.
      const focusAfter = await page.evaluate(
        (before) =>
          document.activeElement === before
            ? 'where it was'
            : document.activeElement?.getAttribute('role') === 'dialog'
              ? 'on the dialog'
              : `on ${document.activeElement?.outerHTML.slice(0, 80)}`,
        focusBefore,
      );
      expect(
        whereDepositWas.inActions === false && whereDepositWas.control === null
          ? ['where it was', 'on the dialog']
          : ['where it was'],
        `the press where Deposit was moved focus: it is ${focusAfter}; the press went down ` +
          `${whereDepositWas.inActions ? 'inside' : 'outside'} the actions, and its mousedown ` +
          `was ${whereDepositWas.refused ? '' : 'not '}refused`,
      ).toContain(focusAfter);
      await expect(dialog, 'the press where Deposit was closed the check view').toBeVisible();
      await expect(dialog).toHaveAccessibleName('Deposit Money');
      await expect(words).toBeVisible();
      await expect(dialog.getByRole('status').filter({ hasText: NOT_YET })).toHaveText(NOT_YET);
      expect(page.url()).toMatch(/\/dashboard(?:[?#]|$)/);
      expect(keys).toHaveLength(1);
      // The view keeps the height of its words: it is no scroll box, the surface scrolls instead.
      const viewHeights = await words.locator('..').evaluate((view) => ({
        scrolled: view.scrollHeight,
        shown: view.clientHeight,
      }));
      expect(viewHeights.scrolled, 'the check view is cut to less than its words').toBe(
        viewHeights.shown,
      );

      const view = await dialog.boundingBox();
      const viewport = page.viewportSize();
      if (!view || !viewport) throw new Error('the check view has no box to press outside');
      const outside = { x: where.x, y: (view.y + view.height + viewport.height) / 2 };

      // What this test stages, as in the test above: the press outside is on the backdrop.
      const onBackdrop = await pressAt(page, outside);
      expect(onBackdrop?.className, 'the press outside did not land on the backdrop').toContain(
        'fui-DialogSurface__backdrop',
      );
      await expect(dialog, 'the press outside closed the check view').toBeVisible();
      await expect(dialog).toHaveAccessibleName('Deposit Money');
      await expect(words).toBeVisible();
      await expect(dialog.getByText(/retrying blindly could deposit twice/)).toBeVisible();
      expect(page.url()).toMatch(/\/dashboard(?:[?#]|$)/);
      expect(keys).toHaveLength(1);

      /*
        axe over the view as the visitor meets it, with the sweep's four tags, scoped to the
        dialog: nothing serious or critical. Measured with the view made a scroll box again: at
        320×568, where it is then cut, axe reported `scrollable-region-focusable` (serious) on it,
        a region that scrolls with no control in it.
      */
      const scan = await new AxeBuilder({ page })
        .withTags(TAGS)
        .include('[role="dialog"]')
        .exclude(TABSTER_SENTINEL)
        .analyze();
      console.log(
        `axe check view ${name}: ${scan.passes.length} passed, ${scan.violations.length} violations — ${
          scan.violations.map((v) => `${v.id} (${v.impact}, ${v.nodes.length})`).join(', ') ||
          'none'
        }`,
      );
      expect(scan.passes.length, 'axe inspected no element of the check view').toBeGreaterThan(0);
      expect(
        scan.violations
          .filter(
            (violation) => violation.impact == null || GATED_IMPACTS.includes(violation.impact),
          )
          .map((violation) => `${violation.id} (${violation.impact}, ${violation.nodes.length})`),
        'a serious or critical finding on the check view',
      ).toEqual([]);

      /*
        Every action is reached where it is drawn, and so is the way out. Where the view's words
        and its footer are taller than the dialog, its surface scrolls as a whole: scrolled here
        with the wheel, as a visitor scrolls it, until "Check recent transactions" is inside the
        surface's box, where the point at its centre must be that button; then back up until the
        X is inside it, where the point at its centre must be the X. Not `scrollIntoView`, which
        scrolls a box whose overflow is hidden as well, where neither a wheel nor a finger can.
      */
      const insideTheSurface = (control: Locator) =>
        control.evaluate((element) => {
          const surface = (element.closest('[role="dialog"]') as Element).getBoundingClientRect();
          const box = element.getBoundingClientRect();
          return box.top >= surface.top && box.bottom <= surface.bottom;
        });
      const wheelUntilInside = async (control: Locator, deltaY: number, what: string) => {
        await expect(async () => {
          if (!(await insideTheSurface(control))) await page.mouse.wheel(0, deltaY);
          expect(
            await insideTheSurface(control),
            `the wheel did not bring ${what} inside the dialog`,
          ).toBe(true);
        }).toPass({ timeout: 5_000 });
        expect(
          await control.evaluate((element) => {
            const box = element.getBoundingClientRect();
            const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
            return hit !== null && element.contains(hit);
          }),
          `the point at the centre of ${what} is not ${what}`,
        ).toBe(true);
      };
      await page.mouse.move(view.x + view.width / 2, view.y + view.height / 2);
      await wheelUntilInside(checkTransactions, 100, '"Check recent transactions"');

      // A deliberate press, once the wait has passed, acts in the "not yet" layout: "Check
      // again", pressed where it is drawn, asks the listing once more, and the view stays.
      const checkAgain = dialog.getByRole('button', { name: 'Check again' });
      await wheelUntilInside(checkAgain, 100, '"Check again"');
      await page.waitForTimeout(SETTLE_MS);
      const asksBefore = asks.length;
      expect(asksBefore, 'the view never asked the listing by key').toBeGreaterThan(0);
      const again = await checkAgain.boundingBox();
      if (!again) throw new Error('"Check again" has no box to press');
      const pressedAgain = await pressAt(page, {
        x: again.x + again.width / 2,
        y: again.y + again.height / 2,
      });
      expect(
        pressedAgain?.delivered ? pressedAgain.control : null,
        'the press on "Check again", after the wait, did not reach it',
      ).toBe('Check again');
      await expect
        .poll(() => asks.length, { message: '"Check again" did not ask the listing again' })
        .toBeGreaterThan(asksBefore);
      await expect(dialog.getByRole('status').filter({ hasText: NOT_YET })).toHaveText(NOT_YET);
      expect(keys).toHaveLength(1);

      // Not a trap: back up to the X, as a visitor goes back to it, and pressed where it is drawn
      // it closes the view, and what is left is the dashboard the dialog was opened on.
      const close = dialog.getByRole('button', { name: 'Close' });
      await wheelUntilInside(close, -100, 'the X');
      const reached = await close.boundingBox();
      if (!reached) throw new Error('the X has no box to press');
      const closed = await pressAt(page, {
        x: reached.x + reached.width / 2,
        y: reached.y + reached.height / 2,
      });
      expect(closed?.delivered ? closed.control : null, 'the press on the X did not reach it').toBe(
        'Close',
      );
      await expect(page.getByRole('dialog'), 'the X did not close the check view').toHaveCount(0);
      expect(page.url()).toMatch(/\/dashboard(?:[?#]|$)/);
      await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();
      expect(keys).toHaveLength(1);
    });
  }

  test('a press as the check view’s hint gains its words is dropped, and one after the wait acts', async ({
    page,
  }) => {
    /*
      The hint under the view's buttons gains its words 5 s into the listing's answer, and the
      view grows around the dialog's centre, so the buttons move: nothing the dialog renders names
      that change, and only the size of what holds the buttons says it. Here the listing is held
      9 s, so the words come while it is still asked. A press right after the words appear lands
      on the new layout, and must be dropped; one after the wait must act.
    */
    await page.setViewportSize({ width: 1280, height: 900 });
    const keys = await answerEverySend(
      page,
      '/api/transactions/deposit',
      notKnownBody('/api/transactions/deposit'),
    );
    await page.route(
      (url) => url.pathname === '/api/transactions' && url.searchParams.has('IdempotencyKey'),
      async (route) => {
        await new Promise((resolve) => setTimeout(resolve, 9_000));
        await route.continue().catch(() => undefined);
      },
    );
    const form = await openMoneyDialog(page, 'Deposit', /deposit money/i);
    await form.getByRole('textbox', { name: 'Deposit amount' }).fill('1');
    await form.getByRole('button', { name: 'Deposit €1.00' }).click();
    await expect.poll(() => keys.length).toBe(1);
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText("We couldn't confirm your deposit")).toBeVisible();
    const checkTransactions = dialog.getByRole('button', { name: 'Check recent transactions' });
    const before = await checkTransactions.boundingBox();
    // When the words appear, on the page's clock.
    await page.evaluate((words) => {
      const record = window as unknown as { wordsAt?: number };
      const observer = new MutationObserver(() => {
        if (!document.querySelector('[role="dialog"]')?.textContent?.includes(words)) return;
        record.wordsAt = performance.now();
        observer.disconnect();
      });
      observer.observe(document.body, { childList: true, characterData: true, subtree: true });
    }, WAIT_SLOW);
    // Resolves once the observer above has written the time; `Number` only tells the compiler.
    const wordsAt = Number(
      await (
        await page.waitForFunction(
          () => (window as unknown as { wordsAt?: number }).wordsAt,
          undefined,
          { polling: 'raf', timeout: 8_000 },
        )
      ).jsonValue(),
    );
    const after = await checkTransactions.boundingBox();
    if (!before || !after) throw new Error('"Check recent transactions" has no box to press');
    // What this test stages: the words moved the button.
    expect(after.y, 'the words did not move "Check recent transactions"').not.toBe(before.y);
    const centre = { x: after.x + after.width / 2, y: after.y + after.height / 2 };

    const soon = await pressAt(page, centre);
    if (!soon) throw new Error('the press as the words appeared did not arrive');
    console.log(
      `the hint's words moved "Check recent transactions" from ${before.y} to ${after.y} px; ` +
        `the press came ${(soon.at - wordsAt).toFixed(1)} ms after them`,
    );
    expect(soon.at - wordsAt, 'the press came too long after the words').toBeLessThan(SETTLE_MS);
    expect(
      soon.delivered ? soon.control : null,
      'the press as the words appeared acted',
    ).toBeNull();
    await expect(dialog).toHaveAccessibleName('Deposit Money');
    expect(page.url()).toMatch(/\/dashboard(?:[?#]|$)/);

    await page.waitForTimeout(SETTLE_MS);
    const later = await pressAt(page, centre);
    expect(later?.delivered ? later.control : null).toBe('Check recent transactions');
    await expect(page).toHaveURL(/\/history(?:[?#]|$)/);
    expect(keys).toHaveLength(1);
  });

  test('in a dialog cut to its height, one press after the hint’s words and the wait acts', async ({
    page,
  }) => {
    /*
      At 320×568 the surface is at its `maxHeight` and scrolls while the listing is asked. The
      hint's words then grow the view alone: the surface and the footer keep their sizes, and the
      footer moves down inside the surface. Nothing is pressed before the one press here, so no
      earlier press can have shown the hook where the buttons are: it must have seen the move as
      it happened. Measured with the view's size unwatched: this press was dropped.
    */
    await page.setViewportSize({ width: 320, height: 568 });
    const keys = await answerEverySend(
      page,
      '/api/transactions/deposit',
      notKnownBody('/api/transactions/deposit'),
    );
    // Held past the words at 5 s, and short of the 20 s ones.
    await page.route(
      (url) => url.pathname === '/api/transactions' && url.searchParams.has('IdempotencyKey'),
      async (route) => {
        await new Promise((resolve) => setTimeout(resolve, 12_000));
        await route.continue().catch(() => undefined);
      },
    );
    const form = await openMoneyDialog(page, 'Deposit', /deposit money/i);
    await form.getByRole('textbox', { name: 'Deposit amount' }).fill('1');
    await form.getByRole('button', { name: 'Deposit €1.00' }).click();
    await expect.poll(() => keys.length).toBe(1);
    const dialog = page.getByRole('dialog');
    const words = dialog.getByText("We couldn't confirm your deposit");
    await expect(words).toBeVisible();
    const checkTransactions = dialog.getByRole('button', { name: 'Check recent transactions' });
    // The heights of the surface, the view and the footer, and where the footer is laid out in
    // the surface, apart from its scroll.
    const parts = () =>
      checkTransactions.evaluate((button) => {
        const surface = button.closest('[role="dialog"]') as HTMLElement;
        const footer = button.parentElement as HTMLElement;
        const view = footer.previousElementSibling as HTMLElement;
        return {
          surface: surface.getBoundingClientRect().height,
          scrolls: surface.scrollHeight > surface.clientHeight,
          view: view.getBoundingClientRect().height,
          footer: footer.getBoundingClientRect().height,
          footerAt: footer.getBoundingClientRect().top + surface.scrollTop,
        };
      });
    await animationsFinished(page);
    const before = await parts();
    await expect(dialog.getByText(WAIT_SLOW)).toBeVisible({ timeout: 8_000 });
    const after = await parts();
    // What this test stages: the words grew the view and moved the footer, in a surface that
    // already scrolled, and changed the size of neither the surface nor the footer.
    console.log(`320×568, the hint's words: ${JSON.stringify(before)} -> ${JSON.stringify(after)}`);
    expect(before.scrolls, 'the surface is not cut to its height').toBe(true);
    expect(after.view, 'the words did not grow the view').toBeGreaterThan(before.view);
    expect(after.footerAt, 'the words did not move the footer').toBeGreaterThan(before.footerAt);
    expect(after.surface).toBe(before.surface);
    expect(after.footer).toBe(before.footer);

    // The wheel brings the button inside the surface, as a visitor's does; a scroll is no move.
    const surface = await dialog.boundingBox();
    if (!surface) throw new Error('the dialog has no box to scroll');
    await page.mouse.move(surface.x + surface.width / 2, surface.y + surface.height / 2);
    await expect(async () => {
      const inside = () =>
        checkTransactions.evaluate((button) => {
          const box = button.getBoundingClientRect();
          const around = (button.closest('[role="dialog"]') as Element).getBoundingClientRect();
          return box.top >= around.top && box.bottom <= around.bottom;
        });
      if (!(await inside())) await page.mouse.wheel(0, 100);
      expect(await inside(), 'the wheel did not bring the button inside the dialog').toBe(true);
    }).toPass({ timeout: 5_000 });
    await page.waitForTimeout(SETTLE_MS);
    // Still the words of a read that has not answered: nothing else changed before the press.
    await expect(dialog.getByText(WAIT_SLOW)).toBeVisible();
    await expect(dialog.getByRole('status').filter({ hasText: NOT_YET })).toHaveCount(0);

    const box = await checkTransactions.boundingBox();
    if (!box) throw new Error('"Check recent transactions" has no box to press');
    const once = await pressAt(page, { x: box.x + box.width / 2, y: box.y + box.height / 2 });
    expect(
      once?.delivered ? once.control : null,
      'the one press after the words and the wait did not act',
    ).toBe('Check recent transactions');
    await expect(page).toHaveURL(/\/history(?:[?#]|$)/);
    expect(keys).toHaveLength(1);
  });

  test('a second press while the send is still out leaves the sentence to take focus when the answer comes', async ({
    page,
  }) => {
    /*
      The other half of a double click: the answer takes longer than the gap between the presses.
      Measured on the running stack: the 409 came 192 and 225 ms after the first press, the second
      press 130 ms after it. That press came while the send was still out, and focus went to the
      dialog's own `div`; the view then appeared with focus still there and its sentence
      unfocused. Here the press is on the disabled button, as in the run that held the answer
      back. A container around the sentence is not a place the visitor chose: the sentence takes
      focus from it as it does from `body`.
    */
    await page.setViewportSize({ width: 1280, height: 900 });
    let answer = () => {};
    const held = new Promise<void>((resolve) => {
      answer = resolve;
    });
    const keys = await answerThatItWentThrough(page, '/api/transactions/deposit', held);
    const form = await openMoneyDialog(page, 'Deposit', /deposit money/i);
    await form.getByRole('textbox', { name: 'Deposit amount' }).fill('1');
    const deposit = form.getByRole('button', { name: 'Deposit €1.00' });
    await expect(deposit).toBeEnabled();
    const where = await centreAtRest(page, deposit);

    await page.mouse.click(where.x, where.y);
    await expect.poll(() => keys.length).toBe(1);
    await page.mouse.click(where.x, where.y);

    // What this test stages: the second press came before the answer, and it left focus on the
    // dialog's own surface, which is still the form's.
    const dialog = page.getByRole('dialog');
    await expect(dialog).toHaveAccessibleName('Deposit Money');
    await expect(
      dialog,
      'the press during the send did not leave focus on the dialog',
    ).toBeFocused();

    answer();
    await expect(dialog).toHaveAccessibleName('Deposit Complete');
    await expect(
      dialog.getByText(SENTENCE.deposit),
      'the sentence did not take focus from the dialog around it',
    ).toBeFocused();
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

  // A settled view, not a fading one: axe reads computed colours (see `axeScan.ts`).
  await animationsFinished(page);

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

/*
  A DEPOSIT WHOSE ANSWER WAS LOST, ASKED ABOUT BY ITS KEY (ADR-0022). Here the money does move: the
  deposit is sent to the stack with `route.fetch()`, and its real 201 is replaced in the browser by
  the 409 whose body names no outcome. The page then asks the real listing by the key it sent, on
  the account it sent it for, and must show the row the stack wrote: the reference read back here
  from that same listing. €1 on the seeded account, as `deposit.spec.ts` moves.

  The other half moves nothing: the send is answered in the browser without reaching the stack, so
  the listing is empty, and the page's clock, installed before the page loads, is moved past the
  3 min 5 s from which an empty listing says the deposit did not go through.
*/
const FOUND = { deposit: 'Your deposit went through.' } as const;
const NOT_YET =
  "It isn't in your transactions yet, and it may still go through. Keep this page open: we'll look again.";
const NONE = "It isn't in your transactions, so it didn't go through.";

/*
  Every deposit below carries a description, as `deposit.spec.ts` gives its own. A row with none is
  listed under its type, so the dashboard's Recent activity would hold a button named "Deposit"
  beside the quick action. `openMoneyDialog` presses the tile inside its own zone, but
  `deposit.spec.ts` and `accessibility.spec.ts` press it by that exact name on the whole page, and
  a later test there could find two.
*/
const DESCRIPTION = 'e2e: deposit';

/** A €1 deposit that reaches the stack, whose answer is lost on the way back. */
async function depositWhoseAnswerIsLost(page: Page) {
  const keys: string[] = [];
  const path = '/api/transactions/deposit';
  await page.route(`**${path}`, async (route) => {
    keys.push(route.request().headers()['idempotency-key'] ?? '(missing)');
    const real = await route.fetch();
    expect(real.status(), 'the deposit itself was refused').toBe(201);
    await route.fulfill({
      status: 409,
      headers: {
        'Content-Type': 'application/json; charset=utf-8',
        'Cache-Control': 'no-cache,no-store',
      },
      body: notKnownBody(path),
    });
  });
  const form = await openMoneyDialog(page, 'Deposit', /deposit money/i);
  await form.getByRole('textbox', { name: 'Deposit amount' }).fill('1');
  await form.getByRole('textbox', { name: 'Description' }).fill(DESCRIPTION);
  await form.getByRole('button', { name: 'Deposit €1.00' }).click();
  await expect.poll(() => keys.length).toBe(1);
  const dialog = page.getByRole('dialog');
  await expect(dialog).toHaveAccessibleName('Deposit Complete');
  return { dialog, key: keys[0] };
}

test.describe('a deposit whose answer was lost, asked about by its key', () => {
  test('is found, with the reference the listing gives for its key, and nothing that sends again', async ({
    page,
  }) => {
    const { dialog, key } = await depositWhoseAnswerIsLost(page);

    const listed = await page.request.get(`/api/transactions?IdempotencyKey=${key}`);
    expect(listed.status()).toBe(200);
    const rows = (await listed.json()) as {
      data: { transactionNumber: string; type: string; idempotencyKey: string | null }[];
    };
    expect(rows.data.map((row) => [row.type, row.idempotencyKey])).toEqual([['Deposit', key]]);

    const sentence = dialog.getByText(FOUND.deposit);
    await expect(sentence).toBeFocused();
    await expect(dialog.getByText('+€1.00')).toBeVisible();
    await expect(dialog.getByText(rows.data[0].transactionNumber)).toBeVisible();
    expect(await buttonNames(dialog)).toEqual(['Close', 'View History']);
    await expect(page.getByText(/didn't go through/)).toHaveCount(0);
  });

  test('says "not yet" while it may still land, and "it didn\'t go through" once nothing can', async ({
    page,
  }) => {
    await page.clock.install();
    const keys = await answerEverySend(
      page,
      '/api/transactions/deposit',
      notKnownBody('/api/transactions/deposit'),
    );
    const form = await openMoneyDialog(page, 'Deposit', /deposit money/i);
    await form.getByRole('textbox', { name: 'Deposit amount' }).fill('1');
    await form.getByRole('textbox', { name: 'Description' }).fill(DESCRIPTION);
    await form.getByRole('button', { name: 'Deposit €1.00' }).click();
    await expect.poll(() => keys.length).toBe(1);

    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('status').filter({ hasText: NOT_YET })).toHaveText(NOT_YET);
    await expect(dialog.getByRole('button', { name: /didn't go through/ })).toHaveCount(0);

    await page.clock.fastForward('03:06');
    await expect(dialog.getByRole('status').filter({ hasText: NONE })).toHaveText(NONE);
    await expect(
      dialog.getByRole('button', { name: "It didn't go through — try again" }),
    ).toBeVisible();
    expect(keys).toHaveLength(1);
  });
});

test.describe('the view of a deposit found by its key, measured', () => {
  for (const { name, phone, prepare } of VARIANTS) {
    test(`the deposit dialog (${name}) is centred, in view, and has no serious or critical finding`, async ({
      page,
    }) => {
      await prepare(page);
      await depositWhoseAnswerIsLost(page);
      await measure(
        page,
        `found-deposit-dialog-${name}`,
        '[role="dialog"]',
        FOUND.deposit,
        phone,
        false,
      );
    });
  }
});
