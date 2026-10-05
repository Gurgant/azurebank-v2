import { expect, test } from '@playwright/test';
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
 */

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
    await page.keyboard.press('Tab');
    expect(await focusOf(page)).toEqual({ on: 'Stay on this page', inTheDialog: true });
    await page.keyboard.press('Tab');
    expect(await focusOf(page)).toEqual({ on: 'Leave anyway', inTheDialog: true });
    await page.keyboard.press('Tab');
    expect(await focusOf(page)).toEqual({ on: 'Close', inTheDialog: true });
    await page.keyboard.press('Shift+Tab');
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
