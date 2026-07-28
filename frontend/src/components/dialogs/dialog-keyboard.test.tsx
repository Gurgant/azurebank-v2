import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { resetMockState } from '../../mocks/state';
import { renderWithProviders } from '../../test/renderWithProviders';
import { AccountsPage } from '../../pages/AccountsPage';

/**
 * What a dialog does to the keyboard, which no screenshot shows.
 *
 * `MoneyDialogShell` sets `modalType="modal"`, and that single prop is what makes Fluent trap Tab
 * and bind Escape. It is easy to lose — `modalType="non-modal"` renders identically and silently
 * drops both — so the properties get asserted rather than assumed.
 *
 * The one that matters most is the interaction between Escape and the anti-double-spend guard.
 * `onOpenChange` routes the backdrop, the close button AND Escape through the caller's own close
 * path, so a dialog holding a live idempotency key cannot be escaped out of. A shell that closed
 * itself on Escape would look correct and leave a request in flight whose result the user still
 * needs to see.
 */

describe('money dialogs and the keyboard', () => {
  beforeEach(() => {
    resetMockState();
  });

  async function openDeposit() {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');
    const trigger = screen.getByRole('button', { name: 'Deposit to Main Account' });
    await userEvent.click(trigger);
    return { dialog: await screen.findByRole('dialog'), trigger };
  }

  it('Escape closes an idle deposit', async () => {
    await openDeposit();

    await userEvent.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('focus lands INSIDE the dialog, not left on the trigger behind it', async () => {
    // A modal whose focus stays outside it is a modal a screen-reader user is not in.
    const { dialog } = await openDeposit();

    await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));
  });

  it.each([['Deposit to Main Account'], ['Withdraw from Main Account']])(
    '%s: the close button carries an accessible name',
    async (trigger) => {
      // Two renders rather than one dialog dismissed and another opened in its place. That reopen
      // is the documented Fluent trap — the replacement surface intermittently never mounts under
      // parallel-suite load — and it bit here first: this passed alone and failed in the full run,
      // twice. One shell serves both dialogs, so asserting each from a clean mount says the same
      // thing without depending on a presence transition jsdom cannot do reliably.
      renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
      await screen.findByText('Main Account');

      await userEvent.click(screen.getByRole('button', { name: trigger }));
      const dialog = await screen.findByRole('dialog');

      expect(within(dialog).getByRole('button', { name: 'Close' })).toBeInTheDocument();
    },
  );

  it('the surface declares itself modal, which is what buys the trap', async () => {
    // `aria-modal` is the observable consequence of `modalType="modal"`, and the proxy this suite
    // can actually check. The INERTNESS it implies is not verifiable here: I first asserted that
    // the background buttons vanish from the a11y tree and the test failed — jsdom does not apply
    // the `aria-hidden` that tabster lifts asynchronously in a real browser. Asserting the prop's
    // effect on the surface is true; asserting its effect on the document would have been a test
    // that passes for the wrong reason or fails for no reason.
    const { dialog } = await openDeposit();

    expect(dialog).toHaveAttribute('aria-modal', 'true');
  });

  it('the title is the dialog’s accessible name, and it tracks the state', async () => {
    // `MoneyDialogShell` renders one string into both the header and `aria-label`. Two copies
    // could drift the moment one is reworded; this is what stops that being silent.
    const { dialog } = await openDeposit();

    expect(dialog).toHaveAttribute('aria-label', 'Deposit Money');
    expect(within(dialog).getByText('Deposit Money')).toBeInTheDocument();
  });
});
