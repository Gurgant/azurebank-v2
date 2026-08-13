import { expect, test } from '@playwright/test';
import { USER } from './fixtures';

/**
 * The step-up modal, end to end. This is the spec Phase 3 exists for.
 *
 * ADR-0030 named this gap explicitly: the integration suite drives the whole step-up protocol
 * against the real backend but STANDS IN for `<StepUpModal/>`, because the modal is the one
 * genuinely visual piece of the flow. Everything the component itself does — mounting on a real
 * 403, collecting six digits, enabling Verify, closing on success — was untested anywhere against
 * a real session. It is tested here, in a real browser, against a real 403 from the real BFF.
 *
 * ORDER IS LOAD-BEARING, WITHIN THIS FILE AND ACROSS THE SUITE.
 *
 * Elevation is server-side session state and there is NO way to undo it: `SetPinVerified`
 * (SessionService.cs:103) is the only mutator, and the level drops only when it expires on its own
 * after `PinValidityMinutes` — 10 in dev, 5 in the base config. So:
 *
 *   - inside this file, cancel runs before verify, because after verify no modal ever appears;
 *   - across the SUITE, nothing that elevates may run before this file. Since ADR-0041 the ONLY
 *     level-2 surface left is `/full-number`: transfers carry their PIN in the request body and the
 *     API verifies it, so they no longer elevate anything and a future transfer spec can sort
 *     wherever it likes. That widens the safe ordering rather than narrowing it — but a spec that
 *     reveals an account number still must not run before this file, or the first test below fails
 *     with "modal not found", which reads like a broken selector rather than an ordering problem;
 *   - each RUN is safe regardless, because the setup project signs in fresh and a new session
 *     starts at level 1. Elevation never leaks between runs.
 *
 * The first assertion below carries that explanation in its failure message so the next person does
 * not have to rediscover it.
 */

const REVEAL = /Reveal full account number/i;

/** Masked in the UI is BULLETS, not the asterisks the API sends — the component substitutes. */
const MASKED = /AB-•+-•+-\d+/;

test.describe.configure({ mode: 'serial' });

test.describe('step-up (PIN) for the account-number reveal', () => {
  test('declining the modal leaves the number masked', async ({ page }) => {
    await page.goto('/accounts');
    await expect(page.getByText(MASKED)).toBeVisible();

    await page.getByRole('button', { name: REVEAL }).click();

    /*
      The modal only appears because the BFF really answered 403 with X-Auth-Level-Required: 2,
      `problemBaseQuery` really turned that into STEP_UP_REQUIRED from the HEADER, and
      `baseQueryWithStepUp` really drove the controller. Nothing here is stubbed.
    */
    const modal = page.getByRole('alertdialog', { name: /verify it's you/i });
    await expect(
      modal,
      'No step-up modal appeared. Either the interceptor is broken, or the session was ALREADY ' +
        'elevated before this file ran — elevation is sticky for PinValidityMinutes (10 in dev) ' +
        'and cannot be undone, so any spec that reveals an account number must sort after this ' +
        'one. (Transfers no longer elevate anything — ADR-0041.)',
    ).toBeVisible();

    // Verify stays disabled until all six digits are present — the component's own gate.
    await expect(modal.getByRole('button', { name: 'Verify' })).toBeDisabled();

    await modal.getByRole('button', { name: 'Cancel' }).click();
    await expect(modal).toBeHidden();

    // The number is still masked: cancelling is a benign no-op, not a partial reveal.
    await expect(page.getByText(MASKED)).toBeVisible();
  });

  test('entering the PIN reveals the unmasked number', async ({ page }) => {
    await page.goto('/accounts');
    await page.getByRole('button', { name: REVEAL }).click();

    const modal = page.getByRole('alertdialog', { name: /verify it's you/i });
    await expect(modal).toBeVisible();

    /*
      Six separate boxes, not one field — measured from the rendered a11y tree. Filling them one
      by one is also what a person does, so it exercises whatever focus-advance logic the
      component has rather than bypassing it with a single `fill`.
    */
    for (const [index, digit] of [...USER.pin].entries()) {
      await modal.getByRole('textbox', { name: `Digit ${index + 1} of 6` }).fill(digit);
    }

    /*
      THE SIXTH DIGIT IS THE SUBMIT. `PinInput` fires `onComplete` once every box is filled and
      `StepUpModal` wires `onComplete={verify}` (StepUpModal.tsx:144), so verification starts
      without the button ever being clicked.

      Worth stating because the first draft of this test clicked "Verify" and failed with
      "element(s) not found" — the modal had already verified, closed and taken the button with
      it. That read like a broken selector; it was the component working as designed.
    */
    await expect(modal).toBeHidden({ timeout: 15_000 });

    const unmasked = page.getByText(/AB-\d{4}-\d{4}-\d{2}/);
    await expect(unmasked).toBeVisible({ timeout: 15_000 });
    // Belt and braces: whatever is shown must not still carry mask characters.
    expect(await unmasked.innerText()).not.toMatch(/[•*]/);
  });

  test('a second reveal does not ask again while the session stays elevated', async ({ page }) => {
    // Elevation is server-side and survives a fresh page load, which is what makes this a
    // meaningful check rather than a restatement of the previous test.
    await page.goto('/accounts');
    await page.getByRole('button', { name: REVEAL }).click();

    await expect(page.getByText(/AB-\d{4}-\d{4}-\d{2}/)).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('alertdialog', { name: /verify it's you/i })).toBeHidden();
  });
});
