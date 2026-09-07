import { expect, test, type Page } from '@playwright/test';
import { USER } from './fixtures';

/**
 * Closing an account costs a PIN (ADR-0049), end to end: real browser, real React, real BFF, real
 * API, real SQL Server. This is the only layer where `DeleteAccountDialog` renders against the
 * real stack — the page tests run on the mock and the contract/integration layers have no React —
 * so it is the one place a broken mint-then-delete would show as a card that never leaves.
 *
 * Both tests create their own spare through `page.request` (the storageState cookies ride along,
 * so these are real calls from the real session) and never touch the seeded accounts. The
 * dialog is `MoneyDialogShell`'s Fluent dialog — role 'dialog', named by its title — NOT the
 * 'alertdialog' stepUp.spec.ts queries, which is StepUpModal's.
 *
 * NO wrong-PIN case here, deliberately: fixtures.ts forbids a wrong PIN on the only seeded user
 * (three misses lock the whole suite for fifteen minutes). The wrong-PIN branch is proven at the
 * mock level (DeleteAccountDialog.test.tsx) and was measured on the real stack (M1,
 * measure-after-main-19742ff-2026-09-06.txt). "No PIN attempt spent" on the funded case is a DB
 * column (PinAccessFailedCount) a browser cannot read; what this spec observes is that the mint
 * answered 422 and the dialog stayed open — the counter is the manual pass ADR-0049 names.
 *
 * Sort/elevation: this spec mints, it never verify-pins, so it does not elevate the session and
 * cannot break stepUp.spec.ts's precondition wherever it sorts. The OTHER way to break it is a
 * leftover: the API does not enforce name uniqueness, and a probe left on the seeded admin adds a
 * masked number to the page that stepUp.spec.ts's strict-mode locators trip over
 * (measure-spa-2026-09-06.txt, run 1). So the names carry a per-run suffix — a leftover can never
 * shadow the next run's locator — and every cleanup step is LOUD: a refused drain or mint throws
 * with its own status instead of leaving the account behind silently.
 */

test.describe.configure({ mode: 'serial' });

/** Per-run suffix: a leftover from a failed cleanup must never collide with the next run's name. */
const RUN = Date.now().toString(36);

async function createSpare(page: Page, name: string) {
  const created = await page.request.post('/api/accounts', { data: { name, type: 'Savings' } });
  expect(created.status()).toBe(201);
  return (await created.json()).data.id as string;
}

/**
 * Mint, then DELETE with the header — the same shape the dialog sends, from outside React.
 * `tolerate404` is for the case the dialog already closed the account (D10/D11: the mint answers
 * 404 ACCOUNT_NOT_FOUND once it is gone); anything else that is not a 201 throws, so a leftover
 * is a failure with a status in it, not a silent one.
 */
async function closeViaApi(page: Page, id: string, { tolerate404 = false } = {}): Promise<void> {
  const mint = await page.request.post(`/api/accounts/${id}/deletion-authorizations`, {
    data: { pin: USER.pin },
  });
  if (tolerate404 && mint.status() === 404) return;
  if (mint.status() !== 201) {
    throw new Error(
      `cleanup: deletion mint for ${id} answered ${mint.status()}, account left behind`,
    );
  }
  const authorizationId = (await mint.json()).data.authorizationId as string;
  const deleted = await page.request.delete(`/api/accounts/${id}`, {
    headers: { 'Step-Up-Authorization': authorizationId },
  });
  if (deleted.status() !== 200) {
    throw new Error(`cleanup: DELETE ${id} answered ${deleted.status()}, account left behind`);
  }
}

async function openDeleteDialog(page: Page, name: string) {
  await page.goto('/accounts');
  await page.getByRole('button', { name: `Account actions for ${name}` }).click();
  await page.getByRole('menuitem', { name: 'Delete' }).click();

  const dialog = page.getByRole('dialog', { name: 'Delete account?' });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Delete' }).click();
  return dialog;
}

test.describe('closing an account (ADR-0049)', () => {
  test('closing an account costs the PIN and removes the card', async ({ page }) => {
    const name = `E2E Closable ${RUN}`;
    const id = await createSpare(page, name);

    try {
      const dialog = await openDeleteDialog(page, name);

      /*
        Six separate boxes, filled one by one as a person does. THE SIXTH DIGIT IS THE SUBMIT:
        the dialog mints on `onComplete` and then sends the DELETE — there is no button to click
        afterwards (stepUp.spec.ts records the same lesson for the step-up modal).
      */
      for (const [index, digit] of [...USER.pin].entries()) {
        await dialog.getByRole('textbox', { name: `Digit ${index + 1} of 6` }).fill(digit);
      }

      await expect(dialog).toBeHidden({ timeout: 15_000 });
      // The list refetched through the mutation's own tags: the card is gone, not hidden.
      await expect(page.getByText(name)).toBeHidden({ timeout: 15_000 });
    } finally {
      // Harmless once the dialog closed it (the mint answers 404 and the helper returns).
      await closeViaApi(page, id, { tolerate404: true });
    }
  });

  test('a funded account is refused inline: one 422 mint response, the dialog stays open with the mapped sentence', async ({
    page,
  }) => {
    const name = `E2E Funded ${RUN}`;
    const id = await createSpare(page, name);
    const mintStatuses: number[] = [];
    page.on('response', (response) => {
      if (
        response.request().method() === 'POST' &&
        response.url().includes('/deletion-authorizations')
      ) {
        mintStatuses.push(response.status());
      }
    });

    try {
      const deposit = await page.request.post('/api/transactions/deposit', {
        headers: { 'Idempotency-Key': crypto.randomUUID() },
        data: { accountId: id, amount: 1, description: 'e2e: fund the closure probe' },
      });
      expect(deposit.status()).toBe(201);

      const dialog = await openDeleteDialog(page, name);
      for (const [index, digit] of [...USER.pin].entries()) {
        await dialog.getByRole('textbox', { name: `Digit ${index + 1} of 6` }).fill(digit);
      }

      // The mint refuses with 422 before the DELETE is attempted and the dialog maps the code
      // inline and stays open (D17). That it does so before the PIN is consulted is M2 (a
      // wrong-PIN probe with the counter read on the DB) — not observed here; see the header.
      await expect(
        dialog.getByText('Only accounts with a zero balance can be deleted.'),
      ).toBeVisible({ timeout: 15_000 });
      await expect(dialog).toBeVisible();
      expect(mintStatuses).toEqual([422]);

      await dialog.getByRole('button', { name: 'Cancel' }).click();
      await expect(dialog).toBeHidden();
    } finally {
      // Drain with the PIN (in the body, as the API takes it), then mint + DELETE. The drain MUST
      // answer 201 before the mint is attempted: a funded leftover is exactly the account the
      // mint refuses (422), and it would sit on the seeded admin until someone drained it by hand.
      const drain = await page.request.post('/api/transactions/withdraw', {
        headers: { 'Idempotency-Key': crypto.randomUUID() },
        data: { accountId: id, amount: 1, pin: USER.pin, description: 'e2e: drain the probe' },
      });
      // `soft`, so a failed drain is recorded WITHOUT replacing the primary failure; closeViaApi
      // then throws on the 422 the funded leftover earns, with the status in the message.
      expect
        .soft(
          drain.status(),
          `cleanup: drain of ${name} must answer 201 or the funded probe stays on the seeded admin`,
        )
        .toBe(201);
      await closeViaApi(page, id);
    }
  });
});
