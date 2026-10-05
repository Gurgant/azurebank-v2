import { expect, test, type Page } from '@playwright/test';
import { scan } from './axeScan';
import { USER } from './fixtures';
import { focusOf } from './focusOf';

/**
 * The confirm dialog under the keyboard, in a real browser.
 *
 * `src/components/shared/ConfirmDialog.tsx` is the app's one hand-rolled modal: it puts focus on
 * its own first control when it opens and keeps Tab inside itself. Its unit tests hold both in
 * jsdom, and two things a browser does are not there to be met:
 *
 *   - jsdom gives focus to any element, whatever its `visibility`. A browser gives none to an
 *     element whose computed `visibility` is `hidden`, and that is how this dialog is hidden while
 *     it is closed. Whether focus goes in when it opens is for a browser to say.
 *   - in those tests the dialog is never the last Tab stop of the document. Where it is, Fluent's
 *     tabster, which hears Tab before the dialog does, moves focus itself. On the page below, the
 *     dialog's last control is that stop.
 *
 * The dialog met here is the one a transfer page shows when the browser's Back is pressed while a
 * send's key is live: "Leave without finishing?" (`src/hooks/useMoneyWizard.ts`, the blocker). As
 * in `wentThrough.spec.ts` the send is answered in the browser, not in the stack: `page.route`
 * fails its connection, so NO MONEY MOVES and the seeded account is left as it was. The page keeps
 * the key, says it could not reach the server and offers "Check again". The authorisation is
 * minted for real, with the right PIN, and expires unused.
 *
 * The open dialog is also scanned here, through the gate of the accessibility sweep
 * (`./axeScan.ts`). This is the one place the default run has it open: the sweep scans /transfer
 * with the dialog closed, and the demo's run, which scans it open, is made by hand.
 */

/**
 * The last Tab stop of the document, by its name, and whether the confirm dialog holds it.
 *
 * Counted with the dialog's own selector for what takes focus
 * (`src/components/shared/ConfirmDialog.tsx`), over what is drawn, tabster's own two elements
 * left out: they are what tabster moves focus to, not stops of the page.
 */
function lastTabStopOf(page: Page) {
  return page.evaluate(() => {
    const stops = Array.from(
      document.querySelectorAll<HTMLElement>(
        'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])',
      ),
    ).filter(
      (element) =>
        !element.matches('[data-tabster-dummy]') &&
        element.getClientRects().length > 0 &&
        getComputedStyle(element).visibility !== 'hidden',
    );
    const last = stops.at(-1);
    return {
      last: last?.getAttribute('aria-label') ?? last?.textContent ?? null,
      inTheDialog: last?.closest('[role="alertdialog"]') != null,
    };
  });
}

test.describe('the confirm dialog under the keyboard', () => {
  test('the leave prompt of a transfer takes focus when it opens, keeps Tab inside, and gives focus back', async ({
    page,
  }) => {
    // The send's connection fails in the browser. The glob ends at the path, so the mint beside
    // it (`…/authorizations`) goes to the real API.
    const sends: string[] = [];
    await page.route('**/api/transfers', (route) => {
      sends.push(route.request().method());
      return route.abort('failed');
    });

    // To the transfer page by the app's own navigation, not by a load. The prompt answers the
    // browser's Back, and the app is asked only when Back stays inside the document it was
    // loaded in: from a page that was loaded, Back would leave that document.
    await page.goto('/dashboard');
    await expect(page.getByRole('heading', { level: 1 }).first()).toBeVisible();
    await page
      .getByRole('navigation', { name: 'Main navigation' })
      .getByRole('button', { name: 'Transfer' })
      .press('Enter');
    await expect(page.getByRole('heading', { level: 1, name: 'Send Money' })).toBeVisible();

    await page.getByRole('textbox', { name: 'Recipient handle' }).fill('janesmith');
    await page.getByRole('button', { name: 'Verify' }).click();
    await page.getByRole('textbox', { name: 'Transfer amount' }).fill('1');
    await page.getByRole('button', { name: 'Review Transfer' }).click();
    await page.getByRole('button', { name: 'Continue' }).click();
    // The sixth digit sends: the mint is real, the send is failed by the route above.
    await page.getByRole('textbox', { name: 'Digit 1 of 6' }).pressSequentially(USER.pin);
    await expect.poll(() => sends).toEqual(['POST']);

    // The key is live, and "Check again" is the page's one way on. A keyboard visitor is on it.
    const checkAgain = page.getByRole('button', { name: 'Check again' });
    await checkAgain.focus();
    expect(await focusOf(page)).toEqual({ on: 'Check again', inTheDialog: false });

    // The browser's Back: the app holds it, and asks.
    await page.goBack();
    const dialog = page.getByRole('alertdialog', { name: 'Leave without finishing?' });
    await expect(dialog).toBeVisible();

    // Focus is in the dialog, on its first control: not left on the page behind it.
    await expect.poll(() => focusOf(page)).toEqual({ on: 'Close', inTheDialog: true });

    // Tab goes round inside the dialog, both ways. "Leave anyway" is the last Tab stop of the
    // whole document: from it Tab goes to the dialog's first control, not out to the page. That
    // end of the document is the only one met here: this page's own controls come before the
    // dialog, so "Close" is not the document's first Tab stop and Shift+Tab from it is an
    // ordinary wrap. The wrap from a dialog that is first in the document is held by
    // src/components/shared/ConfirmDialog.test.tsx, where a listener stands in for tabster.
    //
    // That "Leave anyway" is the document's last stop is held here, and not taken from how the
    // page is built today. It is what makes the third Tab below a test of the dialog against
    // tabster. The day this page draws a control after the dialog, that Tab is an ordinary wrap:
    // it would still pass, and would no longer say anything about the line it is here for.
    expect(await lastTabStopOf(page)).toEqual({ last: 'Leave anyway', inTheDialog: true });
    await page.keyboard.press('Tab');
    expect(await focusOf(page)).toEqual({ on: 'Stay on this page', inTheDialog: true });
    await page.keyboard.press('Tab');
    expect(await focusOf(page)).toEqual({ on: 'Leave anyway', inTheDialog: true });
    await page.keyboard.press('Tab');
    expect(await focusOf(page)).toEqual({ on: 'Close', inTheDialog: true });
    await page.keyboard.press('Shift+Tab');
    expect(await focusOf(page)).toEqual({ on: 'Leave anyway', inTheDialog: true });

    // Open, and scoped to the dialog: what the page behind it fails is not the dialog's. While
    // the app holds the browser's Back the address is still the transfer's, which the scan asks
    // first. The scan leaves focus where it was.
    await scan(page, 'transfer-leave-prompt', '/transfer', '[role="alertdialog"]');
    expect(await focusOf(page)).toEqual({ on: 'Leave anyway', inTheDialog: true });

    // Escape stays: the dialog closes and focus goes back to the control it was on when the
    // dialog opened. The page is still the transfer, and nothing was sent again.
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect.poll(() => focusOf(page)).toEqual({ on: 'Check again', inTheDialog: false });
    expect(new URL(page.url()).pathname).toBe('/transfer');
    expect(sends).toEqual(['POST']);
  });
});
