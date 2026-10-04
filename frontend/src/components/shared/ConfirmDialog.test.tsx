import { useState } from 'react';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { emulateFocusFixup } from '../../test/outage';
import { renderWithProviders } from '../../test/renderWithProviders';
import { ConfirmDialog } from './ConfirmDialog';

/**
 * The first test this component has ever had, which is most of why the gap below survived.
 *
 * `ConfirmDialog` is the only hand-rolled modal in the app — every other dialog is a Fluent
 * `Dialog` and gets containment from tabster for free. This one declares `role="alertdialog"` and
 * `aria-modal="true"`, i.e. it PROMISES the rest of the page is unreachable, and then had an effect
 * labelled "Focus trap" that moved focus in once and never looked at Tab again. Focus walked
 * straight out into the page behind it.
 *
 * It is reached from the delete-account confirmation and from both transfer confirmations, so the
 * page behind is a money surface with live controls on it.
 */

/** Renders the dialog with a focusable sibling, which is what focus escapes TO when it escapes. */
function renderOpen(props: Partial<Parameters<typeof ConfirmDialog>[0]> = {}) {
  const onClose = vi.fn();
  const onConfirm = vi.fn();
  const ui = (overrides: Partial<Parameters<typeof ConfirmDialog>[0]>) => (
    <>
      <button>outside before</button>
      <ConfirmDialog
        isOpen
        onClose={onClose}
        onConfirm={onConfirm}
        title="Delete account?"
        message="This can't be undone."
        confirmText="Delete"
        variant="danger"
        {...props}
        {...overrides}
      />
      <button>outside after</button>
    </>
  );
  const { rerender } = renderWithProviders(ui({}));
  return {
    onClose,
    onConfirm,
    /** Re-render with a changed prop, e.g. to end a loading state mid-test. */
    update: (overrides: Partial<Parameters<typeof ConfirmDialog>[0]>) => rerender(ui(overrides)),
  };
}

const dialog = () => screen.getByRole('alertdialog');
const closeButton = () => screen.getByRole('button', { name: 'Close' });
const cancelButton = () => screen.getByRole('button', { name: 'Cancel' });
const confirmButton = () => screen.getByRole('button', { name: 'Delete' });

/**
 * Where focus is, in words: inside the dialog, on the page (`body`, where a browser leaves focus
 * that nothing holds), or on the control outside the dialog that has it, by its text.
 *
 * The dialog is found in the DOM and not by its role: closed, it is hidden from the accessibility
 * tree, and a query by role would not find it.
 */
function focusIs(): string {
  const active = document.activeElement;
  if (document.querySelector('[role="alertdialog"]')?.contains(active)) return 'inside the dialog';
  if (active === null || active === document.body) return 'on the page';
  return `on "${active.textContent}"`;
}

/**
 * The dialog as a page keeps it: mounted and closed, opened by a button of the page, closed by its
 * own cancel or by Escape, and made to wait by its confirm. `answered` is the answer arriving
 * while it waits, which closes it as a success does: nobody presses anything.
 */
function OpenedFromAButton({ answered = false }: { answered?: boolean }) {
  const [open, setOpen] = useState(false);
  const [waiting, setWaiting] = useState(false);
  return (
    <>
      <button onClick={() => setOpen(true)}>open the dialog</button>
      <ConfirmDialog
        isOpen={open && !answered}
        isLoading={waiting && !answered}
        onClose={() => setOpen(false)}
        onConfirm={() => setWaiting(true)}
        title="Delete account?"
        message="This can't be undone."
        confirmText="Delete"
      />
    </>
  );
}

describe('ConfirmDialog', () => {
  it('moves focus into the dialog when it opens', () => {
    renderOpen();
    // The close button is first in DOM order, so it is where focus lands.
    expect(closeButton()).toHaveFocus();
  });

  it('wraps Tab from the last control back to the first', async () => {
    const user = userEvent.setup();
    renderOpen();

    await user.tab(); // Close -> Cancel
    expect(cancelButton()).toHaveFocus();
    await user.tab(); // Cancel -> Delete (the last one)
    expect(confirmButton()).toHaveFocus();

    await user.tab();

    expect(closeButton()).toHaveFocus();
    expect(dialog()).toContainElement(document.activeElement as HTMLElement);
  });

  it('wraps Shift+Tab from the first control back to the last', async () => {
    const user = userEvent.setup();
    renderOpen();
    expect(closeButton()).toHaveFocus();

    await user.tab({ shift: true });

    expect(confirmButton()).toHaveFocus();
  });

  it('never lets focus reach the page behind it', async () => {
    const user = userEvent.setup();
    renderOpen();

    // More presses than there are controls, in both directions: if containment is missing, one of
    // these lands on "outside after" or "outside before" and the dialog no longer contains focus.
    for (let i = 0; i < 8; i += 1) {
      await user.tab();
      expect(dialog()).toContainElement(document.activeElement as HTMLElement);
    }
    for (let i = 0; i < 8; i += 1) {
      await user.tab({ shift: true });
      expect(dialog()).toContainElement(document.activeElement as HTMLElement);
    }
  });

  it('keeps focus inside even while every control is disabled by isLoading', async () => {
    // isLoading disables the close, cancel and confirm buttons at once, so the dialog holds NO
    // focusable element. Tab must still not escape — the trap has to survive having nothing to
    // cycle to, which is the case a naive first/last implementation crashes or leaks on.
    const user = userEvent.setup();
    renderOpen({ isLoading: true });

    await user.tab();

    expect(screen.queryByText('outside after')).not.toHaveFocus();
    expect(screen.queryByText('outside before')).not.toHaveFocus();
  });

  /*
    THE CONTAINER PATH WITH ENABLED CONTROLS, which is the branch a first/last-only trap gets wrong
    and which nothing else here reaches.

    Opening while `isLoading` puts focus on the dialog container, because every control is disabled
    and there is nothing else to focus. When loading ends the controls come back — and focus is
    still on the container, which is neither the first nor the last of them. Forward, the browser
    would happen to do the right thing; BACKWARD it steps out of the subtree entirely, which is the
    leak.

    Mutation-checked, and the result says which of the two is load-bearing: deleting `|| onContainer`
    from both branches leaves the other eight tests AND the forward one green — only the Shift+Tab
    case turns red. That is the honest split. Forward, the browser reaches the first control on its
    own, so that test documents intent rather than catching regressions; backward is the one where
    removing the guard actually loses focus to the page behind, so it is the one holding the line.
  */
  it('sends Tab to the first control when focus sits on the container', async () => {
    const user = userEvent.setup();
    const { update } = renderOpen({ isLoading: true });
    expect(dialog()).toHaveFocus();

    update({ isLoading: false });
    await user.tab();

    expect(closeButton()).toHaveFocus();
  });

  it('sends Shift+Tab to the last control when focus sits on the container', async () => {
    const user = userEvent.setup();
    const { update } = renderOpen({ isLoading: true });
    expect(dialog()).toHaveFocus();

    update({ isLoading: false });
    await user.tab({ shift: true });

    expect(confirmButton()).toHaveFocus();
  });

  it('still closes on Escape', async () => {
    const user = userEvent.setup();
    const { onClose } = renderOpen();

    await user.keyboard('{Escape}');

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not close on Escape while loading', async () => {
    const user = userEvent.setup();
    const { onClose } = renderOpen({ isLoading: true });

    await user.keyboard('{Escape}');

    expect(onClose).not.toHaveBeenCalled();
  });

  it('confirms and cancels through the buttons', async () => {
    const user = userEvent.setup();
    const { onClose, onConfirm } = renderOpen();

    await user.click(confirmButton());
    expect(onConfirm).toHaveBeenCalledTimes(1);

    await user.click(cancelButton());
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('shows what it is handed under its message, outside the alert and the description', () => {
    renderOpen({
      errorText: 'That could not be done.',
      children: <p>handed to the dialog</p>,
    });
    const handed = screen.queryByText('handed to the dialog');
    const alert = screen.getByRole('alert');
    // The description is whatever the dialog says describes it, not an id typed out here.
    const description = document.getElementById(dialog().getAttribute('aria-describedby') ?? '');
    // `later` comes after `earlier` in the document. True as well for an element INSIDE
    // `earlier`, which is why "inside" is asked separately below.
    const follows = (earlier: Element | null, later: Element | null) =>
      earlier !== null &&
      later !== null &&
      (earlier.compareDocumentPosition(later) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;

    expect({
      inTheDialog: dialog().contains(handed),
      insideTheAlert: alert.contains(handed),
      insideTheDescription: description?.contains(handed),
      // What a screen reader is given as the description: the message, and no word more.
      saidAsTheDescription: description?.textContent,
      saidAsTheAlert: alert.textContent,
      afterTheMessage: follows(description, handed),
      afterTheAlert: follows(alert, handed),
      beforeTheButtons: follows(handed, cancelButton()),
    }).toStrictEqual({
      inTheDialog: true,
      insideTheAlert: false,
      insideTheDescription: false,
      saidAsTheDescription: "This can't be undone.",
      saidAsTheAlert: 'That could not be done.',
      afterTheMessage: true,
      afterTheAlert: true,
      beforeTheButtons: true,
    });
  });

  /*
    A WAIT THAT STARTS UNDER THE VISITOR'S HANDS, which is not the wait the tests above open the
    dialog in.

    Opened already waiting, the dialog puts focus on itself, because it has no control to give it
    to. A wait that starts while the dialog is open starts on the confirm button that was just
    pressed: that button is disabled with the two others, and a browser hands a disabled control's
    focus to the page (src/test/outage.ts: measured in Chromium). From the page, Tab does not pass
    through the dialog's key handler at all, and the next stop is whatever the page behind has.

    jsdom leaves focus on the disabled button, which is not where a browser leaves it. The two
    tests below run the emulation of the browser's part, so the wait they describe starts where a
    visitor's does: with focus on the page.
  */
  it('keeps Tab inside when it starts waiting under the button that was pressed', async () => {
    const stopFixup = emulateFocusFixup();
    try {
      const user = userEvent.setup();
      const { onConfirm, update } = renderOpen();
      const pressed = confirmButton() as HTMLButtonElement;
      await user.click(pressed);
      const whenItWasPressed = focusIs();

      update({ isLoading: true });
      // The emulation hears of the disabled button a moment after the render that disabled it.
      await waitFor(() => expect(pressed).not.toHaveFocus());
      const whenItStartedWaiting = focusIs();
      await user.tab();

      expect({
        pressed: onConfirm.mock.calls.length,
        thePressedButtonIsDisabled: pressed.disabled,
        whenItWasPressed,
        whenItStartedWaiting,
        afterTab: focusIs(),
      }).toStrictEqual({
        pressed: 1,
        thePressedButtonIsDisabled: true,
        whenItWasPressed: 'inside the dialog',
        whenItStartedWaiting: 'inside the dialog',
        afterTab: 'inside the dialog',
      });
    } finally {
      stopFixup();
    }
  });

  it('gives focus back to the control that opened it, also after it waited', async () => {
    // CONTROL: green before this change
    const stopFixup = emulateFocusFixup();
    try {
      const user = userEvent.setup();
      /** Opens the dialog from the page's button, and says where focus went. */
      const open = async () => {
        await user.click(screen.getByRole('button', { name: 'open the dialog' }));
        return focusIs();
      };

      const closedByItsCancel = renderWithProviders(<OpenedFromAButton />);
      const whileOpen = await open();
      await user.click(cancelButton());
      const afterItsCancel = focusIs();
      closedByItsCancel.unmount();

      const closedByEscape = renderWithProviders(<OpenedFromAButton />);
      await open();
      await user.keyboard('{Escape}');
      const afterEscape = focusIs();
      closedByEscape.unmount();

      // A close that follows a wait: the confirm is pressed, the dialog waits, and the answer
      // closes it. The pressed button lost focus when the wait disabled it, so what the dialog
      // gives back is not focus it still holds.
      const closedByTheAnswer = renderWithProviders(<OpenedFromAButton />);
      await open();
      const pressed = confirmButton() as HTMLButtonElement;
      await user.click(pressed);
      await waitFor(() => expect(pressed).not.toHaveFocus());
      const thePressedButtonWasDisabled = pressed.disabled;
      closedByTheAnswer.rerender(<OpenedFromAButton answered />);
      const afterTheAnswer = focusIs();

      expect({
        whileOpen,
        afterItsCancel,
        afterEscape,
        thePressedButtonWasDisabled,
        afterTheAnswer,
      }).toStrictEqual({
        whileOpen: 'inside the dialog',
        afterItsCancel: 'on "open the dialog"',
        afterEscape: 'on "open the dialog"',
        thePressedButtonWasDisabled: true,
        afterTheAnswer: 'on "open the dialog"',
      });
    } finally {
      stopFixup();
    }
  });
});
