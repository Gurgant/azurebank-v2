import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { renderWithProviders } from '../../test/renderWithProviders';
import { expectNoNestedLiveRegions } from '../../test/liveRegions';
import { CreateAccountDialog } from './CreateAccountDialog';
import { RenameAccountDialog } from './RenameAccountDialog';
import { RenameAzureTagDialog } from './RenameAzureTagDialog';

/**
 * A save that fails says so to someone who cannot see the bar appear.
 *
 * Each dialog renders its failure in a MessageBar, and a Fluent MessageBar's root is
 * `role="group"`, not a live region: the app mounts no Fluent announcer, so the text arrived
 * silently. `role="alert"` is the pattern LoginPage set and the money dialogs follow. On main all
 * three `findByRole('alert')` below time out. The last describe in this file is about a save that
 * is still out, not one that failed.
 */
describe('a failed save is announced', () => {
  // No business rule refuses these three with a code the dialog words in its bar, so the failure
  // is the network's: a network error (HttpResponse.error()) is a fetch that never reached a
  // server, not an invented response. (An account that is gone closes the rename dialog and is
  // said elsewhere: src/pages/accounts-closed-account.test.tsx.)
  it('renaming an account', async () => {
    server.use(http.patch('*/api/accounts/:id', () => HttpResponse.error()));
    renderWithProviders(
      <RenameAccountDialog
        account={{ id: '00000000-0000-0000-0000-00000000abcd', name: 'Main Account' }}
        onClose={vi.fn()}
      />,
    );

    const name = screen.getByRole('textbox', { name: 'Account name' });
    fireEvent.change(name, { target: { value: 'Renamed' } });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expectNoNestedLiveRegions();
  });

  it('creating an account', async () => {
    server.use(http.post('*/api/accounts', () => HttpResponse.error()));
    renderWithProviders(<CreateAccountDialog open onClose={vi.fn()} />);

    fireEvent.change(screen.getByRole('textbox', { name: /account name/i }), {
      target: { value: 'Holiday Fund' },
    });
    await userEvent.click(screen.getByRole('button', { name: 'Create Account' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expectNoNestedLiveRegions();
  });

  it('changing the public handle', async () => {
    server.use(http.patch('*/bff/auth/azuretag', () => HttpResponse.error()));
    renderWithProviders(<RenameAzureTagDialog currentTag="admin" onClose={vi.fn()} />);

    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'newhandle' } });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expectNoNestedLiveRegions();
  });
});

/**
 * A handle that is being saved cannot be walked away from.
 *
 * The save's answer calls the `onClose` the page gave the dialog, and the page's onClose closes
 * whichever dialog is open by then: a Cancel in the meantime, then a new dialog, would have the
 * first save's late 200 close the second and drop what was typed in it. Cancel and Escape wait for
 * the answer, as ChangePinDialog's do.
 */
describe('changing the public handle, while the save is out', () => {
  it('refuses Cancel and Escape, and closes once when the answer comes', async () => {
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.patch('*/bff/auth/azuretag', async () => {
        await held;
        return HttpResponse.json({ data: { azureTag: 'newhandle' }, message: 'AzureTag updated' });
      }),
    );
    const onClose = vi.fn();
    renderWithProviders(<RenameAzureTagDialog currentTag="admin" onClose={onClose} />);

    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'newhandle' } });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    try {
      await waitFor(() => expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled());
      // Aimed AT the surface: `userEvent.keyboard` goes to the focused element, which after a
      // submit is a disabled button that swallows the key, and the test would pass without the
      // guard.
      await userEvent.type(screen.getByRole('dialog'), '{Escape}');
      expect(screen.getByRole('dialog')).toBeInTheDocument();
      expect(onClose).not.toHaveBeenCalled();
    } finally {
      // The handler is parked on this promise: a failed assertion must not leave it hanging.
      release();
    }

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
  });
});
