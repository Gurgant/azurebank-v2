import { expect, test } from '@playwright/test';

/**
 * The PIN lock, end to end: real browser, real React, real BFF, real API, real SQL Server.
 *
 * The unit suite proves this against MSW, which is a convenience and never the oracle. This spec
 * exists because the defect it guards — a countdown that stored a number of seconds and never
 * touched it again — is only meaningful against a lock the SERVER actually issued, with the
 * `retryAfterSeconds` the server actually chose (measured: 900, with `Retry-After: 900` and
 * `lockedUntil`).
 *
 * WHAT THIS FILE DOES NOT PROVE, said here rather than left to be discovered: the far side of the
 * lockout window. `page.clock` moves this browser and nothing else, so the server is still locked
 * when the client releases. A withdrawal SUCCEEDING after the window is proven against the API in
 * `TransactionEndpointTests.Withdraw_AfterPinLockoutWindowPasses_Succeeds_AndMovesMoney`.
 *
 * ⚠️ IT DELIBERATELY LOCKS AN ACCOUNT, which every other spec in this directory is forbidden from
 * doing — `fixtures.ts` says so, because `PinLockoutMinutes` is 15 and the seeded admin is the only
 * account the rest of the suite has. So this spec signs in as its OWN user, created for the run,
 * and never touches the seeded one. Set `E2E_LOCK_PROBE_EMAIL` to that user's address; the spec
 * skips rather than poisons the suite if it is absent.
 */

const PROBE_EMAIL = process.env.E2E_LOCK_PROBE_EMAIL;
const PASSWORD = 'Test123!';
const CORRECT_PIN = '123456';
const WRONG_PIN = '000000';

/** A fresh sign-in, NOT the shared storageState: that one is the seeded admin. */
test.use({ storageState: { cookies: [], origins: [] } });

test.describe('the PIN lock counts down and expires', () => {
  test.skip(!PROBE_EMAIL, 'needs E2E_LOCK_PROBE_EMAIL — a throwaway user this spec may lock');

  test('a real 429 from the API disables the PIN, ticks down, and releases at zero', async ({
    page,
  }) => {
    /*
      The browser clock is installable but NOT installed yet: the sign-in and the lock must happen
      on real time, because the BFF issues a real session and the API stamps a real lockedUntil.
      Only the waiting is fast-forwarded, further down.
    */
    await page.clock.install();

    await page.goto('/login');
    await page.getByRole('textbox', { name: /email/i }).fill(PROBE_EMAIL!);
    // By role, not by label: `getByLabel(/password/i)` also matches the "Show password" toggle.
    await page.getByRole('textbox', { name: 'Password' }).fill(PASSWORD);
    await page.getByRole('button', { name: /sign in/i }).click();
    await expect(page).toHaveURL(/\/dashboard/);

    // Open the withdraw dialog and reach the PIN step.
    await page.getByRole('button', { name: 'Withdraw', exact: true }).click();
    const dialog = page.getByRole('dialog').first();
    await dialog.getByRole('textbox', { name: /withdraw amount/i }).fill('1');
    await dialog.getByRole('button', { name: /^Continue/ }).click();
    await expect(dialog.getByText('Verify Withdrawal')).toBeVisible();

    /*
      Wrong PINs until the SERVER locks. ValidationRules.MaxPinAttempts is 3, so the third crosses
      the threshold — but this loops on the observed response rather than on a count, because the
      count is the backend's business and a spec that hard-codes it fails for the wrong reason the
      day it changes.
    */
    const lockBanner = dialog.getByText(/Too many incorrect PIN attempts/);
    for (let attempt = 1; attempt <= 4 && !(await lockBanner.isVisible()); attempt++) {
      await dialog.getByLabel('Digit 1 of 6').click();
      await page.keyboard.type(WRONG_PIN);
      await dialog.getByRole('button', { name: /^Withdraw/ }).click();
      await page.waitForTimeout(600);
    }

    await expect(lockBanner).toBeVisible();

    // The control the lock disables.
    await expect(dialog.getByLabel('Digit 1 of 6')).toBeDisabled();

    /*
      THE DEFECT, stated as an assertion: the countdown must MOVE. Before this change it rendered a
      fixed "about 15 minutes" and never changed again, on a real lock exactly as on a mocked one.
    */
    const timer = page.getByRole('timer');
    await expect(timer).toBeVisible();
    const first = (await timer.textContent())!;
    await page.clock.fastForward('00:05');
    await expect(timer).not.toHaveText(first);

    /*
      And it must END. The server's window is fifteen real minutes, so the browser clock is what
      moves — the response, the deadline and every line of app code are the real ones.
    */
    await page.clock.fastForward('16:00');

    await expect(lockBanner).toBeHidden();
    await expect(dialog.getByLabel('Digit 1 of 6')).toBeEnabled();

    /*
      The PIN is enterable again and the submit control comes back. That is the CLIENT half of the
      release, and it is the half this file can honestly prove.

      It deliberately stops short of pressing Withdraw. `page.clock` advances THIS BROWSER only —
      the API's `lockedUntil` is real wall-clock time, still fifteen minutes out — so a submit here
      would answer 429 again, and asserting on that would be a test of the harness rather than of
      the product. The far side of the window is unreachable from a browser at all.

      That property is proven where the clock is real, against the API, by
      `TransactionEndpointTests.Withdraw_AfterPinLockoutWindowPasses_Succeeds_AndMovesMoney`: a lock
      earned through the real endpoint, aged past its end, then the refused withdrawal succeeding
      with the money actually moved. Between the two files the whole journey is covered; neither
      one claims the other's half.
    */
    await dialog.getByLabel('Digit 1 of 6').click();
    await page.keyboard.type(CORRECT_PIN);
    await expect(dialog.getByRole('button', { name: /^Withdraw/ })).toBeEnabled();
  });
});
