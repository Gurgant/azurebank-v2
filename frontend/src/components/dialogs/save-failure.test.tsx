import { fireEvent, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
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
 * three `findByRole('alert')` below time out.
 */
describe('a failed save is announced', () => {
  it('renaming an account', async () => {
    /*
      The real answer to renaming an account that is gone, measured 2026-09-17 through the BFF:
        PATCH /api/accounts/00000000-0000-0000-0000-00000000abcd {"name":"Renamed"}
        -> 404 {"errorCode":"ACCOUNT_NOT_FOUND",
                "detail":"Account with identifier '00000000-0000-0000-0000-00000000abcd' was not found."}
    */
    const id = '00000000-0000-0000-0000-00000000abcd';
    server.use(
      http.patch('*/api/accounts/:id', () =>
        problem({
          status: 404,
          errorCode: 'ACCOUNT_NOT_FOUND',
          detail: `Account with identifier '${id}' was not found.`,
        }),
      ),
    );
    renderWithProviders(
      <RenameAccountDialog account={{ id, name: 'Main Account' }} onClose={vi.fn()} />,
    );

    const name = screen.getByRole('textbox', { name: 'Account name' });
    fireEvent.change(name, { target: { value: 'Renamed' } });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      `Account with identifier '${id}' was not found.`,
    );
    expectNoNestedLiveRegions();
  });

  // No business rule refuses these two with a code of its own, so the failure is the network's:
  // MSW's HttpResponse.error() is a fetch that never reached a server, not an invented response.
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
