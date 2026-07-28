import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
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

  it.each([
    ['Deposit to Main Account', 'Deposit Money'],
    ['Withdraw from Main Account', 'Withdraw Money'],
  ])('%s: the title IS the accessible name', async (trigger, title) => {
    // `MoneyDialogShell` renders one string into both the header and `aria-label`. Two copies
    // could drift the moment one is reworded; this is what stops that being silent. Both dialogs,
    // because one shell serving two callers is only worth something if both are checked.
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');
    await userEvent.click(screen.getByRole('button', { name: trigger }));
    const dialog = await screen.findByRole('dialog');

    expect(dialog).toHaveAttribute('aria-label', title);
    expect(within(dialog).getByText(title)).toBeInTheDocument();
  });

  it('Escape does NOT close a deposit while the request is in flight', async () => {
    /*
      The one that matters, and the one a review disputed. `MoneyDialogShell` hands Escape and the
      backdrop to the caller's `onClose` rather than dismissing itself, and both callers' close
      path is `if (!keyLive) onClose()` — so the anti-double-spend guard covers the keyboard.

      That is an argument. This is the evidence: hold the deposit response open, press Escape, and
      the dialog is still there. Abandoning a money request whose result the user has not seen is
      the failure being prevented.
    */
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.post('*/api/transactions/deposit', async () => {
        await held;
        return HttpResponse.json({ data: null, message: null }, { status: 500 });
      }),
    );

    await openDeposit();
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: '€100' }));
    await userEvent.click(within(dialog).getByRole('button', { name: /Deposit €100/ }));

    try {
      // Aimed AT the surface. `userEvent.keyboard` targets `document.activeElement`, which after a
      // submit is a disabled button that swallows it — the first version of this test passed with
      // the guard deleted, which is the only reason I know that.
      await userEvent.type(dialog, '{Escape}');
      expect(screen.getByRole('dialog')).toBeInTheDocument();
    } finally {
      // In a `finally` because the handler is parked on this promise. If the assertion throws and
      // the release never runs, the request never settles, the state update lands after teardown,
      // and a clear failure here becomes a hang or a stray rejection in whatever runs next.
      release();
    }
  });
});
