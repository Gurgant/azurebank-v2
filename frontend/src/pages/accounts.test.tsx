import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { AccountsPage } from './AccountsPage';

/**
 * A1/A4 — the first page on REAL data. Pins: FE-side masking (the full number must
 * never reach the DOM), the isPrimary badge, D22 loading/error/empty as first-class
 * states, and the create flow refreshing the list through D7 tag invalidation only.
 */
describe('accounts list (A1)', () => {
  it('renders the query data: masked numbers, EUR amounts, one Primary badge', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });

    expect(await screen.findByText('Main Account')).toBeInTheDocument();
    expect(screen.getByText('Rainy Day')).toBeInTheDocument();

    // The API masks server-side (AB-****-****-90); the FE normalizes to display
    // bullets — the raw asterisk form never reaches the screen.
    expect(screen.getByText('AB-••••-••••-90')).toBeInTheDocument();
    expect(screen.getByText('AB-••••-••••-01')).toBeInTheDocument();
    expect(screen.queryByText('AB-****-****-90')).not.toBeInTheDocument();
    expect(screen.queryByText('AB-****-****-01')).not.toBeInTheDocument();

    // Contract currency is EUR (en-IE), never the old mock USD.
    expect(screen.getByText('€1,250.50')).toBeInTheDocument();
    expect(screen.getByText('€830.00')).toBeInTheDocument();
    // Total = 2080.50, rendered by BOTH responsive headers (media queries hide one).
    expect(screen.getAllByText('€2,080.50')).toHaveLength(2);

    // Exactly one primary account in the seed.
    expect(screen.getAllByText('Primary')).toHaveLength(1);
    expect(screen.getAllByText('Checking')).toHaveLength(1);
    expect(screen.getAllByText('Savings')).toHaveLength(1);
  });

  it('an empty list still offers the Add New Account card (D22 empty state)', async () => {
    server.use(http.get('*/api/accounts', () => HttpResponse.json({ data: [], message: null })));
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });

    expect(await screen.findByText('Add New Account')).toBeInTheDocument();
    expect(screen.queryByText('Main Account')).not.toBeInTheDocument();
    expect(screen.getAllByText('€0.00')).toHaveLength(2); // both total-balance headers
  });

  it('a load failure shows the problem detail and Retry recovers (D22 error state)', async () => {
    // 500 is NOT in the retry policy (NETWORK/502/503/504 only) — fails immediately.
    server.use(
      http.get('*/api/accounts', () =>
        problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Something broke.' }),
      ),
    );
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });

    expect(await screen.findByText(/Something broke\./)).toBeInTheDocument();
    expect(screen.queryByText('Main Account')).not.toBeInTheDocument();

    // The backend recovers; Retry refetches and the grid replaces the error.
    server.resetHandlers();
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));

    expect(await screen.findByText('Main Account')).toBeInTheDocument();
    expect(screen.queryByText(/Something broke\./)).not.toBeInTheDocument();
  });
});

describe('create account (A4)', () => {
  it('creates through the dialog and the list refreshes via D7 invalidation', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    await userEvent.click(screen.getByText('Add New Account'));
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText('Account name'), 'Holiday Fund');
    await userEvent.selectOptions(screen.getByLabelText('Account type'), 'Savings');
    await userEvent.click(screen.getByRole('button', { name: 'Create Account' }));

    // Success closes the dialog; the new account arrives ONLY through the
    // {Account,'LIST'} invalidation refetch — the page never hand-patches the cache.
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(await screen.findByText('Holiday Fund')).toBeInTheDocument();
    // The server-assigned (already-masked) number renders as bullets like every other.
    expect(screen.getByText('AB-••••-••••-72')).toBeInTheDocument();
    expect(screen.queryByText('AB-****-****-72')).not.toBeInTheDocument();
  });

  it('maps a server VALIDATION_ERROR onto the offending field', async () => {
    server.use(
      http.post('*/api/accounts', () =>
        problem({
          status: 400,
          // camelCase key — exactly as the ValidationExceptionHandler emits.
          errors: { name: ['An account with this name already exists.'] },
        }),
      ),
    );
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    await userEvent.click(screen.getByText('Add New Account'));
    await screen.findByRole('dialog');
    await userEvent.type(screen.getByLabelText('Account name'), 'Main Account');
    await userEvent.click(screen.getByRole('button', { name: 'Create Account' }));

    expect(
      await screen.findByText('An account with this name already exists.'),
    ).toBeInTheDocument();
    // The dialog stays open for correction.
    expect(screen.getByRole('dialog')).toBeInTheDocument();
  });

  it('rejects a too-short name client-side before any request', async () => {
    let createRequests = 0;
    server.use(
      http.post('*/api/accounts', () => {
        createRequests += 1;
        return HttpResponse.json({ data: null, message: null }, { status: 500 });
      }),
    );
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    await userEvent.click(screen.getByText('Add New Account'));
    await screen.findByRole('dialog');
    await userEvent.type(screen.getByLabelText('Account name'), 'X');
    await userEvent.click(screen.getByRole('button', { name: 'Create Account' }));

    expect(
      await screen.findByText('Account name must be at least 2 characters'),
    ).toBeInTheDocument();
    // "before any request" is the contract — pin it.
    expect(createRequests).toBe(0);
  });

  it('a non-validation failure shows in the dialog, and cancel + reopen starts clean', async () => {
    server.use(
      http.post('*/api/accounts', () =>
        problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Creation exploded.' }),
      ),
    );
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    await userEvent.click(screen.getByText('Add New Account'));
    await screen.findByRole('dialog');
    await userEvent.type(screen.getByLabelText('Account name'), 'Doomed Fund');
    await userEvent.click(screen.getByRole('button', { name: 'Create Account' }));

    // The failure renders IN the dialog, which stays open for a retry.
    expect(await screen.findByText(/Creation exploded\./)).toBeInTheDocument();
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    // Cancel, reopen: BOTH the form and the mutation error must be gone —
    // the dialog stays mounted, so close() has to reset the mutation state too.
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await userEvent.click(screen.getByText('Add New Account'));
    await screen.findByRole('dialog');
    expect(screen.queryByText(/Creation exploded\./)).not.toBeInTheDocument();
    expect(screen.getByLabelText('Account name')).toHaveValue('');
  });
});

describe('legacy money dialogs (adapter wiring)', () => {
  it('card Deposit scopes the dialog to THAT account, pre-selected — and reopening re-scopes', async () => {
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    // Open Deposit from the Rainy Day card: the dialog must list ONLY Rainy Day.
    await userEvent.click(screen.getByRole('button', { name: 'Deposit to Rainy Day' }));
    expect(screen.getByText('Deposit Money')).toBeInTheDocument();
    expect(screen.getAllByText('Rainy Day')).toHaveLength(2); // page card + dialog row
    expect(screen.getAllByText('Main Account')).toHaveLength(1); // page card only

    // The scoped account is pre-selected: a quick-amount deposit succeeds without
    // touching the selection, and lands on the RIGHT account.
    await userEvent.click(screen.getByRole('button', { name: '$100' }));
    await userEvent.click(screen.getByRole('button', { name: 'Deposit $100.00' }));
    // The legacy mock flow resolves after a 1.5s setTimeout — give it headroom.
    expect(
      await screen.findByText('Deposited to Rainy Day', undefined, { timeout: 4000 }),
    ).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Done' }));

    // Reopen on the OTHER card: the dialog must re-scope, never reuse stale state.
    await userEvent.click(screen.getByRole('button', { name: 'Deposit to Main Account' }));
    expect(screen.getAllByText('Main Account')).toHaveLength(2);
    expect(screen.getAllByText('Rainy Day')).toHaveLength(1);
    await userEvent.click(screen.getByRole('button', { name: 'Close' }));
  });
});
