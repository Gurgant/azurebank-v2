import { expect, test } from '@playwright/test';

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
