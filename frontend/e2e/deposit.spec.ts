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
  THE ENTRY A DEPOSIT LEAVES, LOOKED AT. Two things can be measured only on a ledger with an entry
  in it, and this suite's user is seeded with one account and no history: the deposit above is
  what leaves the first. So they are held here, after it, and not in `accessibility.spec.ts`,
  which runs before any entry exists. Each says so if it finds the ledger empty, which is what a
  run of one of them alone on a new database would meet.

  THE STATUS PILL STAYS IN ITS COLUMN AT PHONE WIDTH. The two ledgers share one table with fixed
  columns, and its last column was 18 % wide: at 375 px that is narrower than the pill in it,
  which does not wrap. The pill then ran past its cell: off the screen on History, where the last
  letter of "Completed" was cut, and over the card's edge on the dashboard. Each row reads every
  pill of the page against the cell it is in.

  THE "COMPLETED" BADGE OF A TRANSACTION'S PAGE CAN BE READ. Its words wore the green of an icon
  on the badge's own pale green: 2.69 to 1 in the light theme, where 13 px text needs 4.5. Held
  in both themes, with the theme asserted first: a row that asked for dark and was drawn light
  would measure the light pair.
*/
const EMPTY_LEDGER =
  'the ledger has no entry to look at: the deposit at the top of this file leaves one';

test.describe('the entry a deposit leaves', () => {
  for (const path of ['/dashboard', '/history']) {
    test(`its status pill stays in its column at phone width, on ${path}`, async ({ page }) => {
      await page.setViewportSize({ width: 375, height: 812 });
      await page.goto(path);
      await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();
      await expect(
        page.locator('table tbody tr td:nth-child(4)').first(),
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
