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
          errors: { Name: ['An account with this name already exists.'] },
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
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await screen.findByText('Main Account');

    await userEvent.click(screen.getByText('Add New Account'));
    await screen.findByRole('dialog');
    await userEvent.type(screen.getByLabelText('Account name'), 'X');
    await userEvent.click(screen.getByRole('button', { name: 'Create Account' }));

    expect(
      await screen.findByText('Account name must be at least 2 characters'),
    ).toBeInTheDocument();
  });
});
