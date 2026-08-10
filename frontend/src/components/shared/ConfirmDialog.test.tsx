import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
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
});
