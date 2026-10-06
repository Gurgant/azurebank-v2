import type { Page } from '@playwright/test';

/**
 * What has focus, by its name, and whether the confirm dialog holds it.
 *
 * Read by name so that a red comparison says where focus went, and not only that it was not
 * where it was expected. A module of its own so that every spec that opens the app's one
 * hand-rolled modal from the keyboard reads focus the same way
 * (`src/components/shared/ConfirmDialog.tsx`, an `alertdialog` wherever it is opened).
 */
export function focusOf(page: Page) {
  return page.evaluate(() => {
    const active = document.activeElement;
    if (active === null || active === document.body) return { on: 'the page', inTheDialog: false };
    return {
      on: active.getAttribute('aria-label') ?? active.textContent,
      inTheDialog: active.closest('[role="alertdialog"]') !== null,
    };
  });
}
