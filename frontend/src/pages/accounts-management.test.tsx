import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { mockState } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { AccountsPage } from './AccountsPage';

/**
 * PR-7 (A5/A6/A7) — account management from the card menu. Pins: rename through the
 * {Account,id} invalidation, the DoD blanket-'Account' invalidation on set-primary
 * (the badge MOVES and the list re-sorts primary-first), and the D17 rule that 422
 * business codes render INLINE in the confirm dialog, which stays open.
 */

async function openMenu(accountName: string) {
  await userEvent.click(screen.getByRole('button', { name: `Account actions for ${accountName}` }));
}

describe('rename account (A5)', () => {
  it('renames from the menu: dialog seeded with the current name, list refreshes', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');

    await openMenu('Rainy Day');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));

    const input = await screen.findByLabelText('Account name');
    expect(input).toHaveValue('Rainy Day');

    await userEvent.clear(input);
    await userEvent.type(input, 'Storm Fund');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    // The row's {Account,id} tag invalidates the list — never a hand-patched cache.
    expect(await screen.findByText('Storm Fund')).toBeInTheDocument();
    expect(screen.queryByText('Rainy Day')).not.toBeInTheDocument();
  });

  it('rejects a too-short name client-side before any request', async () => {
    let patches = 0;
    server.use(
      http.patch('*/api/accounts/:id', () => {
        patches += 1;
        return HttpResponse.json({ data: null, message: null }, { status: 500 });
      }),
    );
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');

    await openMenu('Rainy Day');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));
    const input = await screen.findByLabelText('Account name');
    await userEvent.clear(input);
    await userEvent.type(input, 'X');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(
      await screen.findByText('Account name must be at least 2 characters'),
    ).toBeInTheDocument();
    expect(patches).toBe(0);
  });
});

describe('set primary (A6)', () => {
  it('moves the Primary badge and re-sorts the list — the DoD blanket invalidation', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    // Seed order: primary first — Main Account leads.
    let names = screen.getAllByText(/^(Main Account|Rainy Day)$/).map((el) => el.textContent);
    expect(names[0]).toBe('Main Account');
    expect(screen.getAllByText('Primary')).toHaveLength(1);

    await openMenu('Rainy Day');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Set as primary' }));

    // Blanket 'Account' invalidation refetches the list: badge count stays 1, the
    // list re-sorts, and Rainy Day now leads.
    await waitFor(() => {
      names = screen.getAllByText(/^(Main Account|Rainy Day)$/).map((el) => el.textContent);
      expect(names[0]).toBe('Rainy Day');
    });
    expect(screen.getAllByText('Primary')).toHaveLength(1);

    // The new primary's menu no longer offers the action.
    await openMenu('Rainy Day');
    expect(await screen.findByRole('menuitem', { name: 'Rename' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Set as primary' })).not.toBeInTheDocument();
    await userEvent.keyboard('{Escape}');
  });
});

describe('delete account (A7)', () => {
  it('deletes a zero-balance account after confirmation', async () => {
    // Seed a deletable account directly (zero balance, not primary) — the create
    // FLOW is already covered by accounts.test.tsx.
    mockState.accounts.push({
      id: '019f7b3f-0000-7000-8000-0000000000a3',
      accountNumber: 'AB-****-****-33',
      name: 'Temp Fund',
      type: 'Savings',
      balance: 0,
      isPrimary: false,
      createdAt: '2026-07-10T09:00:00.0000000Z',
    });
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Temp Fund');

    await openMenu('Temp Fund');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    expect(await screen.findByRole('alertdialog')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(screen.queryByText('Temp Fund')).not.toBeInTheDocument());
    // The rest of the list is untouched.
    expect(screen.getByText('Main Account')).toBeInTheDocument();
    expect(screen.getAllByText('Primary')).toHaveLength(1);
  });

  it('a non-zero balance is refused INLINE and the dialog stays open (D17)', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Rainy Day');

    await openMenu('Rainy Day'); // balance 830 — the real business rule refuses it
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    await screen.findByRole('alertdialog');
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));

    expect(
      await screen.findByText('Only accounts with a zero balance can be deleted.'),
    ).toBeInTheDocument();
    expect(screen.getByRole('alertdialog')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.getByText('Rainy Day')).toBeInTheDocument();
  });

  it('the primary account is refused with its own mapped reason', async () => {
    // The REAL rule order checks balance BEFORE primary (AccountService), so only a
    // ZERO-balance primary reaches the PRIMARY_ACCOUNT_DELETE rule — mirror that.
    const primary = mockState.accounts.find((a) => a.isPrimary);
    primary!.balance = 0;
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    await openMenu('Main Account');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    await screen.findByRole('alertdialog');
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }));

    expect(
      await screen.findByText(
        'This is your primary account — set another account as primary first.',
      ),
    ).toBeInTheDocument();
  });
});
