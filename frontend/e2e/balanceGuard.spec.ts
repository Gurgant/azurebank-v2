import { expect, test } from '@playwright/test';

/**
 * The balance guard, end to end: real browser, real React, real BFF, real API, real SQL Server.
 *
 * This is the scenario the change exists for, and it cannot be staged against MSW: the balance has
 * to move on the SERVER while a form is open in front of it. The unit suite proves the mechanism
 * with a mock; this proves the product.
 *
 * What it demonstrates, in the user's own terms: you open Withdraw for everything you have, the
 * money leaves the account by another route, you press Continue — and the app stops you there,
 * naming the balance as it is NOW, without ever asking for your PIN. Which matters beyond tidiness:
 * measured against this same API, an over-balance withdrawal with a mistyped PIN answers
 * `401 INVALID_PIN` (the funds check runs AFTER the PIN), so reaching that step on a stale balance
 * spends one of three attempts, and three lock the PIN for fifteen minutes.
 *
 * Uses a THROWAWAY user, never the seeded admin — the rest of the suite shares that account and
 * this spec deliberately empties the one it signs in as.
 */

const PROBE_EMAIL = process.env.E2E_PROBE_EMAIL ?? process.env.E2E_LOCK_PROBE_EMAIL;
const PASSWORD = 'Test123!';
const PIN = '123456';

test.use({ storageState: { cookies: [], origins: [] } });

test.describe('an outflow cannot pass the balance behind it', () => {
  test.skip(!PROBE_EMAIL, 'needs E2E_PROBE_EMAIL — a throwaway user this spec may drain');

  test('the PIN is never requested once the server says the money is gone', async ({ page }) => {
    // Every request the PAGE makes, so "no withdrawal was attempted" is observed rather than
    // inferred from the absence of a receipt.
    const withdrawPosts: string[] = [];
    page.on('request', (request) => {
      if (request.method() === 'POST' && request.url().includes('/api/transactions/withdraw')) {
        withdrawPosts.push(request.url());
      }
    });

    await page.goto('/login');
    await page.getByRole('textbox', { name: /email/i }).fill(PROBE_EMAIL!);
    // By role, not by label: `getByLabel(/password/i)` also matches the "Show password" toggle.
    await page.getByRole('textbox', { name: 'Password' }).fill(PASSWORD);
    await page.getByRole('button', { name: /sign in/i }).click();
    await expect(page).toHaveURL(/\/dashboard/);

    /*
      `page.request` shares the browser context's cookies, so these are real calls from this real
      session — the same thing a second device would do. Deposit first so the spec does not depend
      on whatever the account happens to hold.
    */
    const accounts = await (await page.request.get('/api/accounts')).json();
    const accountId: string = accounts.data[0].id;

    const deposit = await page.request.post('/api/transactions/deposit', {
      headers: { 'Idempotency-Key': crypto.randomUUID() },
      data: { accountId, amount: 200, description: 'balance-guard fund' },
    });
    expect(deposit.status()).toBe(201);
    const funded: number = (await deposit.json()).data.newBalance;

    // Reload so the form is built from the funded balance, exactly as a user arriving would see it.
    await page.reload();
    await page.getByRole('button', { name: 'Withdraw', exact: true }).click();
    const dialog = page.getByRole('dialog').first();
    await dialog.getByRole('textbox', { name: /withdraw amount/i }).fill(String(funded));
    await expect(dialog.getByRole('button', { name: /^Continue/ })).toBeEnabled();

    // THE MOVE: the money leaves by another route while this form sits there believing otherwise.
    const drain = await page.request.post('/api/transactions/withdraw', {
      headers: { 'Idempotency-Key': crypto.randomUUID() },
      data: { accountId, amount: funded, pin: PIN, description: 'balance-guard drain' },
    });
    expect(drain.status()).toBe(201);
    expect((await drain.json()).data.newBalance).toBe(0);

    await dialog.getByRole('button', { name: /^Continue/ }).click();

    // Stopped, and told why — with the balance as it is now, which the user has not seen.
    await expect(dialog.getByText(/Insufficient balance for this operation/)).toBeVisible();
    // The requirement, stated as an assertion: the ceremony is never reached.
    await expect(dialog.getByText('Verify Withdrawal')).toBeHidden();
    await expect(dialog.getByLabel('Digit 1 of 6')).toHaveCount(0);
    // And the page itself never asked the server to do the impossible thing.
    expect(withdrawPosts).toEqual([]);

    /*
      The other half of the promise, and the reason a clamp on keystrokes was rejected: the user is
      not stranded. The figure they typed is still there, marked as too large, and "Use max" puts a
      legal amount in with one tap.
    */
    await expect(dialog.getByRole('textbox', { name: /withdraw amount/i })).toHaveValue(
      String(funded),
    );
    await expect(dialog.getByText(/Exceeds available balance of €0\.00/)).toBeVisible();
  });
});
