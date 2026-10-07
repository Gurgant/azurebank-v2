import { useEffect, useState } from 'react';

/**
 * The control a dialog's focus goes back to: what has focus as the dialog is first drawn.
 *
 * One case needs a step more. A dialog chosen from a menu is opened by an item that is gone with
 * its choice, and the control that is left is the button that opened the menu. A menu names that
 * button as its label (`aria-labelledby`), which is how it is found here.
 */
function openerOf(active: Element | null): HTMLElement | null {
  if (!(active instanceof HTMLElement) || active === document.body) return null;
  const labelledBy = active.closest('[role="menu"]')?.getAttribute('aria-labelledby');
  return (labelledBy ? document.getElementById(labelledBy) : null) ?? active;
}

/**
 * Gives focus back, once a dialog is gone, to the control that opened it.
 *
 * The app's Fluent dialogs are opened by a state, a page's or the PIN controller's, with no
 * trigger of their own for Fluent to go back to, so a dialog that closed left focus on `body` and
 * a keyboard visitor's next Tab started again from the top of the page (WCAG 2.4.3). Call this in
 * a component that is mounted while its dialog is open and unmounted when it closes. The session
 * warning does not: the clock opens it, and no control.
 *
 * It reads the opener while the dialog is first drawn, before Fluent moves focus into it, and
 * does nothing more until the dialog is gone. Then it only gives back focus that was lost: on
 * `body`, where the dialog's own controls leave it when they go. An opener that has left the page
 * meanwhile (the dialog led to another page, or closed the account its button belonged to) is
 * left alone, and so is focus that something else has taken.
 *
 * Not Fluent's own `useRestoreFocusTarget` on the openers. That one acts whenever focus is lost
 * inside the dialog, not only when the dialog closes, and a money send loses it on purpose: the
 * control that sent is disabled while the send is out. Measured in Chromium on 2026-10-06, by
 * keys: with that mark on the dashboard's tile, focus was on the tile behind the open deposit
 * dialog while the send waited, and the page behind was no longer hidden from assistive
 * technology.
 */
export function useReturnFocus(): void {
  const [opener] = useState(() => openerOf(document.activeElement));

  useEffect(
    () => () => {
      if (opener?.isConnected && document.activeElement === document.body) opener.focus();
    },
    [opener],
  );
}
