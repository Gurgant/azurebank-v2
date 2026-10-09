import { expect, test } from '@playwright/test';
import { textContrast } from './contrast';

/**
 * A deposit, all the way through: a real form, a real POST, real money in SQL Server, and — the
 * part only a browser can show — the dashboard figure updating itself afterwards.
 *
 * That last step is why this spec exists. The integration suite proves the mutation and the
 * receipt; what it cannot see is RTK Query's tag invalidation causing the balance query to refetch
 * and React to re-render the new figure. A broken `invalidatesTags` would leave a user staring at
 * a stale balance after a successful deposit, and every layer below this one would still be green.
 */

const AMOUNT = 1;

test('a deposit moves the money and the dashboard figure follows', async ({ page }) => {
  await page.goto('/dashboard');

  const balance = page.getByRole('heading', { level: 1 }).first();
  const before = (await balance.innerText()).trim();

  await page.getByRole('button', { name: 'Deposit', exact: true }).click();

  const dialog = page.getByRole('dialog', { name: /deposit money/i });
  await expect(dialog).toBeVisible();

  /*
    The submit button RENAMES itself. Empty it is "Deposit" and disabled; once the amount is valid
    it becomes "Deposit €1.00" — the control restates what is about to happen, which is a real
    anti-fat-finger property worth pinning rather than routing around. The first draft matched the
    exact string "Deposit" and failed with "element(s) not found" on a form that was filled in
    perfectly, because by then the button was called something else.
  */
  const submit = dialog.getByRole('button', { name: /^Deposit(\s|$)/ });
  await expect(submit).toBeDisabled();

  await dialog.getByRole('textbox', { name: 'Deposit amount' }).fill(String(AMOUNT));
  await dialog.getByRole('textbox', { name: 'Description' }).fill('e2e: deposit');

  await expect(submit).toBeEnabled();
  await expect(submit).toHaveAccessibleName(`Deposit €${AMOUNT}.00`);

  /*
    The dialog PREDICTS the resulting balance before anything is committed. Capturing that
    prediction gives a far better oracle than arithmetic on a parsed figure: the app's own promise
    becomes the expected value, in the app's own formatting, and the assertion after the mutation
    checks whether it told the truth.

    It also pins "the money moved exactly once" for free — a double-charge would land on a
    different figure than the one previewed.
  */
  const preview = await dialog.getByText(/New balance:/).innerText();
  const predicted = preview.replace(/^\s*New balance:\s*/, '').trim();
  expect(predicted, `Could not read a predicted balance out of "${preview}"`).toMatch(/^€[\d.,]+$/);
  expect(predicted).not.toBe(before);

  await submit.click();

  /*
    The dialog does not close on success — it becomes a RECEIPT. Measured: the same surface
    re-titles itself "Deposit Complete" and shows "Deposit Successful!", the amount, the account
    and the new balance, with "View Transaction" and "Done".

    Worth stating because the first draft waited for the dialog to disappear and then looked for
    the dashboard heading; both failed, the second because the receipt is modal and the page
    behind it is inert. Waiting for the wrong thing is how an E2E suite grows sleeps.
  */
  const receipt = page.getByRole('dialog', { name: /deposit complete/i });
  await expect(receipt).toBeVisible({ timeout: 20_000 });
  await expect(receipt).toContainText(/Deposit Successful/i);

  // The receipt must agree with what the form promised before the money moved.
  await expect(receipt).toContainText(predicted);

  await receipt.getByRole('button', { name: 'Done' }).click();
  await expect(receipt).toBeHidden();

  /*
    ABSOLUTE, not relative. `expect(after).not.toBe(before)` would pass for any wrong number, and
    this project has been bitten by exactly that shape of assertion before. The figure on the
    dashboard has to become the exact one the dialog promised — which is also what proves the
    invalidation refetched, and that the money moved once rather than twice.
  */
  await expect(balance).toHaveText(predicted, { timeout: 20_000 });
  expect(predicted).not.toBe(before);
});

/*
  THE ENTRY A DEPOSIT LEAVES, LOOKED AT. Two things _(four since 2026-10-07: the two lines of a
  phone's row and the long word of an entry came after)_ can be measured only on a ledger with an
  entry in it, and this suite's user is seeded with one account and no history: the deposit above
  is what leaves the first. So they are held here, after it, and not in `accessibility.spec.ts`,
  which runs before any entry exists. Each says so if it finds the ledger empty, which is what a
  run of one of them alone on a new database would meet.

  THE STATUS PILL STAYS IN ITS COLUMN AT PHONE WIDTH. The two ledgers share one table with fixed
  columns, and its last column was 18 % wide: at 375 px that is narrower than the pill in it,
  which does not wrap. The pill then ran past its cell: off the screen on History, where the last
  letter of "Completed" was cut, and over the card's edge on the dashboard. Each row reads every
  pill of the page against the cell it is in. _(Since later on 2026-10-06 the table has no column
  at 375 px: under 480 px of its own box a transaction is drawn on two lines. The cell this reads
  the pill against is there the pill's place at the end of the second line. What holds the row
  itself is the next block.)_

  AT PHONE WIDTH A TRANSACTION IS TWO LINES, AND ITS AMOUNT IS WHOLE. Four columns did not fit a
  phone. An amount does not wrap and its column was a fifth of the table, so what did not fit ran
  out to the right, under the status: on the dashboard at 375 px, "+€1,250.50" ended 10.7 px
  under the "Completed" pill (headless Chromium, 2026-10-06). The row is now the entry and the
  amount on one line, when and the status on the line under it. For every row of the page, each
  of the two rows below holds that:
  - the lines are those two;
  - the amount is on one line, ends inside the row, and is over no other cell;
  - the pill is inside the row;
  - the row is what a finger presses, 44 px tall or more: down its middle, the element under the
    point is the entry's button, which opens the transaction's page.
  It reads the rows as they are drawn, then again with the longest amount a row can show,
  "+€100,000.00", and the longest status written into each: this suite's ledger holds one
  deposit of €1.00, which fits anything. A page that scrolls sideways fails it too. The API
  takes one amount of up to 100,000.00.

  IN THE TABLE'S COLUMNS A LONG WORD OF AN ENTRY WRAPS INSIDE ITS OWN. A description is free text
  and may be one word with no space in it. On two lines such a word wrapped; in the columns of a
  wider table nothing broke it, and it ran on over the amount and the status. Held at 800 px of
  screen, where both tables have their columns, which each row asserts first: with sixty letters
  written as the entry of every row, what the entry draws ends inside its own cell and is over no
  other, and the amount is still on one line. Before the fix the sixty letters ended 509 px past
  the Entry column on the dashboard and 473 px on History (headless Chromium, 2026-10-07).

  THE "COMPLETED" BADGE OF A TRANSACTION'S PAGE CAN BE READ. Its words wore the green of an icon
  on the badge's own pale green: 2.69 to 1 in the light theme, where 13 px text needs 4.5. Held
  in both themes, with the theme asserted first: a row that asked for dark and was drawn light
  would measure the light pair.
*/
const EMPTY_LEDGER =
  'the ledger has no entry to look at: the deposit at the top of this file leaves one';

/**
 * The status cell of the first row that is a transaction: what a row of this file waits for
 * before it measures. The status is a row's LAST cell, not its fourth: with one account in
 * view the row has a fifth, the running balance, which sits fourth and is not drawn on a phone.
 * (Until 2026-10-07 the rows waited for the fourth cell. On the real stack the suite's user has
 * one account, so at phone width on the dashboard they waited for a cell that is never drawn.)
 */
const FIRST_STATUS_CELL = 'table tbody tr:has(td:nth-child(4)) td:last-child';

/** A deposit of the most the API takes for one amount, 100,000.00, as a row writes it. */
const LONGEST_AMOUNT = '+€100,000.00';
const LONGEST_STATUS = 'Completed';

/** One transaction's row as the browser drew it, in px. Each `past` is 0 or less when inside. */
type RowDrawn = {
  amount: string;
  amountLines: number;
  amountPastRow: number;
  amountOver: string[];
  pillPastRow: number;
  whenUnderEntry: boolean;
  statusUnderAmount: boolean;
  target: number;
};

/**
 * Runs in the page, over every `<tr>` of the table's body: measures the rows that are
 * transactions. With `longest`, the amount and the status are first overwritten with the two
 * longest, in the text nodes that are there, so that the row is measured at its fullest.
 */
function drawnRows(rows: Element[], longest: { amount: string; status: string } | null) {
  const drawn: RowDrawn[] = [];
  for (const row of rows) {
    if (row.children.length < 4) continue;
    const [when, entry, amount] = Array.from(row.children);
    const status = row.lastElementChild!;
    const pill = status.querySelector('span');
    const button = entry.querySelector('button');
    if (!pill || !button) continue;

    if (longest) {
      const texts = Array.from(amount.childNodes).filter(
        (node) => node.nodeType === Node.TEXT_NODE,
      );
      texts.forEach((node, index) => {
        node.textContent = index === 0 ? longest.amount : '';
      });
      if (pill.firstChild) pill.firstChild.textContent = longest.status;
    }

    // In the middle of the screen before anything is read: clear of the bars fixed to its edges.
    row.scrollIntoView({ block: 'center' });

    // What the amount draws: the boxes of its text, which is what leaves a cell too narrow.
    const range = document.createRange();
    range.selectNodeContents(amount);
    const lines = Array.from(range.getClientRects()).filter(
      (box) => box.width > 0 && box.height > 0,
    );
    if (lines.length === 0) continue;
    const text = {
      left: Math.min(...lines.map((box) => box.left)),
      right: Math.max(...lines.map((box) => box.right)),
      top: Math.min(...lines.map((box) => box.top)),
      bottom: Math.max(...lines.map((box) => box.bottom)),
    };
    const over = (other: Element) => {
      const box = other.getBoundingClientRect();
      const wide = Math.min(text.right, box.right) - Math.max(text.left, box.left);
      const tall = Math.min(text.bottom, box.bottom) - Math.max(text.top, box.top);
      return wide > 0.5 && tall > 0.5;
    };

    const box = row.getBoundingClientRect();
    const pillBox = pill.getBoundingClientRect();
    // Down the middle of the row, a pixel at a time: how much of it is the entry's button.
    let target = 0;
    for (let y = Math.ceil(box.top); y < box.bottom; y += 1) {
      if (document.elementFromPoint(box.left + box.width / 2, y) === button) target += 1;
    }

    drawn.push({
      amount: amount.textContent ?? '',
      amountLines: new Set(lines.map((line) => Math.round(line.top))).size,
      amountPastRow: Math.max(text.right - box.right, box.left - text.left),
      amountOver: [
        over(when) ? 'when' : '',
        over(entry) ? 'entry' : '',
        over(status) ? 'status' : '',
      ].filter(Boolean),
      pillPastRow: Math.max(
        pillBox.right - box.right,
        box.left - pillBox.left,
        box.top - pillBox.top,
        pillBox.bottom - box.bottom,
      ),
      whenUnderEntry: when.getBoundingClientRect().top >= entry.getBoundingClientRect().bottom,
      statusUnderAmount: status.getBoundingClientRect().top >= text.bottom,
      target,
    });
  }
  return drawn;
}

/** Sixty letters and no space: wider than the Entry column of either table, in any font. */
const UNBROKEN_WORD = 'W'.repeat(60);

/** One transaction's row with `UNBROKEN_WORD` as its entry, in px: `past` is 0 or less inside. */
type EntryDrawn = {
  amount: string;
  inColumns: boolean;
  entryPastCell: number;
  entryOver: string[];
  amountLines: number;
};

/**
 * Runs in the page, over every `<tr>` of the table's body: writes `word` over the words of each
 * transaction's entry, in the text node that is there, and measures what the entry then draws
 * against its own cell and the three others.
 */
function entriesDrawn(rows: Element[], word: string) {
  const drawn: EntryDrawn[] = [];
  for (const row of rows) {
    if (row.children.length < 4) continue;
    const [when, entry, amount] = Array.from(row.children);
    const status = row.lastElementChild!;
    const words = entry.querySelector('button')?.firstChild;
    if (!words) continue;
    words.textContent = word;
    row.scrollIntoView({ block: 'center' });

    // What an element draws: the boxes of its text, which is what leaves a cell too narrow.
    const inked = (element: Element) => {
      const range = document.createRange();
      range.selectNodeContents(element);
      return Array.from(range.getClientRects()).filter((box) => box.width > 0 && box.height > 0);
    };
    const lines = inked(entry);
    if (lines.length === 0) continue;
    const text = {
      left: Math.min(...lines.map((box) => box.left)),
      right: Math.max(...lines.map((box) => box.right)),
      top: Math.min(...lines.map((box) => box.top)),
      bottom: Math.max(...lines.map((box) => box.bottom)),
    };
    const over = (other: Element) => {
      const box = other.getBoundingClientRect();
      const wide = Math.min(text.right, box.right) - Math.max(text.left, box.left);
      const tall = Math.min(text.bottom, box.bottom) - Math.max(text.top, box.top);
      return wide > 0.5 && tall > 0.5;
    };

    const cells = [when, entry, amount, status].map((cell) => cell.getBoundingClientRect());
    drawn.push({
      amount: amount.textContent ?? '',
      // Side by side, each cell ending where the next begins: the table's columns.
      inColumns: cells.every(
        (box, index) => index === 0 || cells[index - 1].right <= box.left + 0.5,
      ),
      entryPastCell: Math.max(text.right - cells[1].right, cells[1].left - text.left),
      entryOver: [
        over(when) ? 'when' : '',
        over(amount) ? 'amount' : '',
        over(status) ? 'status' : '',
      ].filter(Boolean),
      amountLines: new Set(inked(amount).map((line) => Math.round(line.top))).size,
    });
  }
  return drawn;
}

test.describe('the entry a deposit leaves', () => {
  for (const path of ['/dashboard', '/history']) {
    test(`its status pill stays in its column at phone width, on ${path}`, async ({ page }) => {
      await page.setViewportSize({ width: 375, height: 812 });
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();
      await expect(
        page.locator(FIRST_STATUS_CELL).first(),
        `${path}: ${EMPTY_LEDGER}`,
      ).toBeVisible();

      // How far each pill reaches past the right edge of its cell, in px: 0 or less when inside.
      const past = await page.locator('table tbody tr').evaluateAll((rows) =>
        rows.flatMap((row) => {
          const status = row.children.length >= 4 ? row.lastElementChild : null;
          const pill = status?.querySelector('span');
          if (!status || !pill) return [];
          return [pill.getBoundingClientRect().right - status.getBoundingClientRect().right];
        }),
      );
      expect(past.length, `${path}: no status pill was found to measure`).toBeGreaterThan(0);
      expect(Math.max(...past), `${path}: a pill reaches past its cell, in px`).toBeLessThanOrEqual(
        0,
      );
    });

    test(`it is two lines with its amount whole at phone width, on ${path}`, async ({ page }) => {
      await page.setViewportSize({ width: 375, height: 812 });
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();
      await expect(
        page.locator(FIRST_STATUS_CELL).first(),
        `${path}: ${EMPTY_LEDGER}`,
      ).toBeVisible();

      const rows = page.locator('table tbody tr');
      const passes: [string, { amount: string; status: string } | null][] = [
        ['as drawn', null],
        ['with the longest amount', { amount: LONGEST_AMOUNT, status: LONGEST_STATUS }],
      ];
      for (const [pass, longest] of passes) {
        const drawn = await rows.evaluateAll(drawnRows, longest);
        expect(drawn.length, `${path}, ${pass}: no row was found to measure`).toBeGreaterThan(0);
        if (longest) {
          expect(
            drawn.map((row) => row.amount),
            `${path}: the longest amount was not written into every row`,
          ).toEqual(drawn.map(() => LONGEST_AMOUNT));
        }

        // Everything wrong with every row, said at once: the first row alone would hide the rest.
        const wrong = drawn.flatMap((row) => {
          const said: string[] = [];
          if (row.amountLines !== 1) said.push(`its amount is on ${row.amountLines} lines`);
          if (!(row.amountPastRow < 0))
            said.push(`its amount ends ${row.amountPastRow} px past the row`);
          if (row.amountOver.length > 0)
            said.push(`its amount is over another cell: ${row.amountOver.join(', ')}`);
          if (row.pillPastRow > 0) said.push(`its pill reaches ${row.pillPastRow} px past the row`);
          if (!row.whenUnderEntry) said.push('when is not on a line under the entry');
          if (!row.statusUnderAmount) said.push('the status is not on a line under the amount');
          if (row.target < 44)
            said.push(`a press meets the entry's button over ${row.target} px of its height`);
          return said.map((what) => `the row of ${row.amount}: ${what}`);
        });
        expect(wrong, `${path}, ${pass}: what is wrong with a row`).toEqual([]);
      }

      const sideways = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );
      expect(sideways, `${path}: the page scrolls sideways, by px`).toBeLessThanOrEqual(0);
    });

    test(`a long word of its entry wraps inside its column, on ${path}`, async ({ page }) => {
      await page.setViewportSize({ width: 800, height: 900 });
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();
      await expect(
        page.locator(FIRST_STATUS_CELL).first(),
        `${path}: ${EMPTY_LEDGER}`,
      ).toBeVisible();

      const drawn = await page.locator('table tbody tr').evaluateAll(entriesDrawn, UNBROKEN_WORD);
      expect(drawn.length, `${path}: no row was found to measure`).toBeGreaterThan(0);
      // This row is about the columns: at a width where the table has none it would hold nothing.
      expect(
        drawn.filter((row) => !row.inColumns).length,
        `${path}: rows that are not drawn in columns at 800 px`,
      ).toBe(0);

      const wrong = drawn.flatMap((row) => {
        const said: string[] = [];
        if (row.entryPastCell > 0.5)
          said.push(`its entry ends ${row.entryPastCell} px past its own cell`);
        if (row.entryOver.length > 0)
          said.push(`its entry is over another cell: ${row.entryOver.join(', ')}`);
        if (row.amountLines !== 1) said.push(`its amount is on ${row.amountLines} lines`);
        return said.map((what) => `the row of ${row.amount}: ${what}`);
      });
      expect(wrong, `${path}: what is wrong with a row`).toEqual([]);

      const sideways = await page.evaluate(
        () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
      );
      expect(sideways, `${path}: the page scrolls sideways, by px`).toBeLessThanOrEqual(0);
    });
  }

  for (const theme of ['light', 'dark'] as const) {
    test(`the Completed badge of its page can be read, in the ${theme} theme`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: theme });
      await page.goto('/history');
      const completed = page
        .locator('table tbody tr')
        .filter({ has: page.locator('td:last-child', { hasText: 'Completed' }) });
      await expect(completed.first(), EMPTY_LEDGER).toBeVisible();
      await completed.first().getByRole('button').first().click();
      // The transaction's own page, or the pill of the list it came from would be measured.
      await expect(page).toHaveTitle('Transaction Details · AzureBank');
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme);

      const badge = page.getByRole('main').getByText('Completed', { exact: true }).first();
      await expect(badge).toBeVisible();
      const seen = await textContrast(badge);
      expect(
        seen.ratio,
        `the Completed badge (${theme}): ${seen.colours.text} on ${seen.colours.ground}`,
      ).toBeGreaterThanOrEqual(4.5);
    });
  }
});
