import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { HistoryPage } from './HistoryPage';

/**
 * T1 — the infinite history feed. Pins: the signed-mock trap is dead (amounts are
 * UNSIGNED, direction comes from the TYPE), summary math excludes Failed/Reversed,
 * pages accumulate through Load more, and D22 states are first-class.
 */
describe('history feed (T1)', () => {
  it('renders rows with type-driven signs, counterparties, and status text', async () => {
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });

    expect(await screen.findByText('Salary — July')).toBeInTheDocument();
    expect(screen.getByText('+€1,250.50')).toBeInTheDocument();

    // Null description falls back to the type; the amount sign comes from the TYPE.
    expect(screen.getByText('-€50.00')).toBeInTheDocument();

    // Transfers headline the counterparty and surface a non-Completed status.
    expect(screen.getByText('To @john_d')).toBeInTheDocument();
    expect(screen.getByText('Pending')).toBeInTheDocument();
    expect(screen.getByText('From @anna_k')).toBeInTheDocument();
    expect(screen.getByText('+€75.00')).toBeInTheDocument();

    // Date group headings from createdAt.
    expect(screen.getByText('July 20, 2026')).toBeInTheDocument();
  });

  it('computes the summary from TYPES over loaded pages, excluding Reversed', async () => {
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary — July');

    // Page 1 = 5 heroes + 15 fillers (€10 deposits). Income = 1250.50 + 75 + 150;
    // expenses = 50 + 200 — the €30 Reversed withdrawal must NOT count.
    expect(screen.getByText('+€1,475.50')).toBeInTheDocument();
    expect(screen.getByText('-€250.00')).toBeInTheDocument();
    expect(screen.getByText('+€1,225.50')).toBeInTheDocument();
  });

  it('Load more appends page 2 and then disappears; the summary re-totals', async () => {
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary — July');

    expect(screen.queryByText('Top-up #16')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));

    expect(await screen.findByText('Top-up #16')).toBeInTheDocument();
    // 25 of 25 loaded — nothing more to fetch.
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument(),
    );
    // Summary now includes page 2's five €10 deposits.
    expect(screen.getByText('+€1,525.50')).toBeInTheDocument();
  });

  it('filter tabs slice the LOADED pages client-side', async () => {
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary — July');

    await userEvent.click(screen.getByRole('button', { name: 'Deposits' }));
    expect(screen.getByText('Salary — July')).toBeInTheDocument();
    expect(screen.queryByText('To @john_d')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Transfers' }));
    expect(screen.getByText('To @john_d')).toBeInTheDocument();
    expect(screen.getByText('From @anna_k')).toBeInTheDocument();
    expect(screen.queryByText('Salary — July')).not.toBeInTheDocument();
  });

  it('a load failure shows the problem detail and Retry recovers (D22)', async () => {
    server.use(
      http.get('*/api/transactions', () =>
        problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Feed exploded.' }),
      ),
    );
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });

    expect(await screen.findByText(/Feed exploded\./)).toBeInTheDocument();

    server.resetHandlers();
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('Salary — July')).toBeInTheDocument();
  });

  it('an empty feed is a first-class state (D22)', async () => {
    server.use(
      http.get('*/api/transactions', () =>
        HttpResponse.json({
          data: [],
          pagination: {
            page: 1,
            pageSize: 20,
            totalItems: 0,
            totalPages: 1,
            hasNextPage: false,
            hasPreviousPage: false,
          },
        }),
      ),
    );
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });

    expect(await screen.findByText('No Transactions')).toBeInTheDocument();
    expect(screen.getByText('Your transactions will appear here.')).toBeInTheDocument();
  });
});
