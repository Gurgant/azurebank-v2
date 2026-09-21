import { expect, test } from '@playwright/test';

/**
 * The PIN lock, end to end: real browser, real React, real BFF, real API, real SQL Server.
 *
 * The unit suite proves this against MSW, which is a convenience and never the oracle. This spec
 * exists because the defect it guards — a countdown that stored a number of seconds and never
 * touched it again — is only meaningful against a lock the SERVER actually issued, with the
 * `retryAfterSeconds` the server actually chose (measured: 900, with `lockedUntil`).
 *
 * ⚠️ IT DRIVES THE CHANGE-PIN DIALOG, AND IT USED TO DRIVE THE WITHDRAW ONE (moved 2026-09-21,
 * ADR-0056). The withdrawal earns no PIN attempts any more — its PIN moved to
 * `POST /api/transactions/withdraw/authorizations` — so a withdraw dialog that does not yet mint
 * can never reach a 429, and this spec waited out its timeout on CI run 35618553090.
 *
 * The subject did NOT change with the endpoint. What is under test is `RetryCountdown` against a
 * lock the server really issued: that it disables the entry, that it MOVES, and that it releases at
 * zero. `ChangePinDialog`, `DeleteAccountDialog` and `WithdrawDialog` render that one component
 * with the same banner, so any of the three proves it — and Change PIN is the one that needs no
 * minted authorisation and no throwaway ACCOUNT, so it leaves nothing behind. It was chosen for
 * that, not for convenience: a spare account created here could not be closed afterwards, because
 * the PIN this spec deliberately locks is the same PIN its closure would need.
 *
 * A SKIP WAS THE WRONG ANSWER AND THE REPOSITORY SAYS SO. The first attempt at this left
 * `test.skip(true, …)` with a reason; `scripts/assert-e2e-ran.mjs` failed the job, because two
 * money-safety specs once skipped on every run while the job read green. Its message names the only
 * two remedies — make it run, or delete it — and re-aiming it is the first of those.
 *
 * WHAT THIS FILE DOES NOT PROVE, said here rather than left to be discovered: the far side of the
 * lockout window. `page.clock` moves this browser and nothing else, so the server is still locked
 * when the client releases. A money movement SUCCEEDING after the window is proven against the API
 * in `TransactionEndpointTests.Withdraw_AfterPinLockoutWindowPasses_Succeeds_AndMovesMoney`.
 *
 * ⚠️ IT DELIBERATELY LOCKS A PIN, which every other spec in this directory is forbidden from doing
 * — `fixtures.ts` says so, because `PinLockoutMinutes` is 15 and the seeded admin is the only
 * account the rest of the suite has. So this spec signs in as its OWN user, created for the run,
 * and never touches the seeded one. Set `E2E_LOCK_PROBE_EMAIL` to that user's address.
 */

const PROBE_EMAIL = process.env.E2E_LOCK_PROBE_EMAIL;
const PASSWORD = 'Test123!';
const CORRECT_PIN = '123456';
const WRONG_PIN = '000000';
const NEW_PIN = '246802';

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

    await page.goto('/settings');
    // The PAGE's trigger. The dialog's submit carries the same name, so everything below is scoped
    // to the dialog.
    await page.getByRole('button', { name: 'Change PIN' }).click();
    const dialog = page.getByRole('dialog', { name: 'Change your PIN' });
    await expect(dialog).toBeVisible();

    const currentPin = dialog.getByRole('group', { name: 'Current PIN' });
    const submit = dialog.getByRole('button', { name: 'Change PIN' });

    const enter = async (group: typeof currentPin, pin: string) => {
      await group.getByLabel('Digit 1 of 6').click();
      await page.keyboard.type(pin);
    };

    // The new PIN and its confirmation are filled once: a wrong CURRENT pin clears only the current
    // boxes, so the loop below re-enters that field alone.
    await enter(dialog.getByRole('group', { name: 'New PIN' }), NEW_PIN);
    await enter(dialog.getByRole('group', { name: 'Confirm new PIN' }), NEW_PIN);

    /*
      Wrong PINs until the SERVER locks. ValidationRules.MaxPinAttempts is 3, so the third crosses
      the threshold — but this loops on the observed response rather than on a count, because the
      count is the backend's business and a spec that hard-codes it fails for the wrong reason the
      day it changes.

      It waits for the API's ANSWER to each attempt, not for a fixed delay. This used to sleep
      600 ms and re-check the banner; the attempt that trips the lock is the slow one — it writes
      the lock and its audit row — and on CI it once answered later than that. The loop then saw no
      banner, clicked a PIN box the arriving 429 had just disabled, and waited out the whole test
      timeout (run 34981468961, 2026-09-15: the snapshot shows the banner and "Try again in 14:34",
      26 s after the lock landed). A 429 ends the loop here; the banner is asserted below, where the
      assertion retries until React has rendered it.
    */
    const lockBanner = dialog.getByText(/Too many incorrect PIN attempts/);
    for (let attempt = 1; attempt <= 4; attempt++) {
      await enter(currentPin, WRONG_PIN);
      /*
        THE BROWSER'S URL IS `/bff/auth/set-pin`, NOT `/api/auth/pin`.

        `useSetPinMutation` posts to `/bff/auth/set-pin` (apiSlice.ts); `/api/auth/pin` is the
        API's ProblemDetails `instance`, which the BFF forwards inside the BODY. Matching on the
        instance would never fire, this loop would run its four attempts without breaking, and the
        test would time out -- the exact failure this spec was re-aimed to escape.

        The split is measured, not guessed: every URL in `apiSlice.ts` is either `/api/...` (money
        and accounts, proxied) or `/bff/...` (auth -- login, register, logout, azuretag, set-pin,
        verify-pin, reauthenticate). The version of this spec that drove the withdrawal matched
        `/api/transactions/withdraw` and was right to; a PIN change is on the other side of that
        line. The prefix cannot be inferred from the endpoint's subject.
      */
      const answered = page.waitForResponse(
        (response) =>
          response.request().method() === 'POST' && response.url().includes('/auth/set-pin'),
      );
      await submit.click();
      if ((await answered).status() === 429) break;
    }

    await expect(lockBanner).toBeVisible();

    // The control the lock disables.
    await expect(currentPin.getByLabel('Digit 1 of 6')).toBeDisabled();

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
    await expect(currentPin.getByLabel('Digit 1 of 6')).toBeEnabled();

    /*
      The PIN is enterable again and the submit control comes back. That is the CLIENT half of the
      release, and it is the half this file can honestly prove.

      It deliberately stops short of pressing Change PIN. `page.clock` advances THIS BROWSER only —
      the API's `lockedUntil` is real wall-clock time, still fifteen minutes out — so a submit here
      would answer 429 again, and asserting on that would be a test of the harness rather than of
      the product. The far side of the window is unreachable from a browser at all.

      That property is proven where the clock is real, against the API, by
      `TransactionEndpointTests.Withdraw_AfterPinLockoutWindowPasses_Succeeds_AndMovesMoney`: a lock
      earned through the real mint, aged past its end, then the refused withdrawal succeeding with
      the money actually moved. Between the two files the whole journey is covered; neither one
      claims the other's half.
    */
    await enter(currentPin, CORRECT_PIN);
    await expect(submit).toBeEnabled();
  });
});
