import { Route, Routes } from 'react-router-dom';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { format } from 'date-fns';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { DashboardPage } from './DashboardPage';

/**
 * F1 — the LAST page off mock data. Pins: real accounts (EUR, masked numbers, primary
 * first), the recent feed with REAL ids navigating to the detail route, the money dialogs
 * opening over the real account list, the server-side monthly summary, and the D22
 * page/sectional state split (accounts gate the page; feed and summary fail alone).
 */

function renderDashboard() {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={<DashboardPage />} />
      <Route path="/transactions/:id" element={<div>TX DETAIL</div>} />
      <Route path="/accounts" element={<div>ACCOUNTS PAGE</div>} />
      <Route path="/transfer" element={<div>TRANSFER PAGE</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

describe('dashboard (F1)', () => {
  it('renders the real accounts: EUR balances, display-masked numbers, primary first', async () => {
    renderDashboard();

    // Primary hero card = the isPrimary account, EUR, bullet-masked (never raw asterisks).
    expect(await screen.findByText('Main Account')).toBeInTheDocument();
    expect(screen.getByText('€1,250.50')).toBeInTheDocument();
    expect(screen.getByText('AB-••••-••••-90')).toBeInTheDocument();
    expect(screen.queryByText('AB-****-****-90')).not.toBeInTheDocument();

    // Secondary (desktop) card.
    expect(screen.getByText('Rainy Day')).toBeInTheDocument();
    expect(screen.getByText('€830.00')).toBeInTheDocument();

    // Accounts Overview does the real math.
    expect(screen.getByText('€2,080.50')).toBeInTheDocument();
    expect(screen.getByText('Total Accounts')).toBeInTheDocument();

    // The mock-era USD world is gone.
    expect(screen.queryByText(/\$\d/)).not.toBeInTheDocument();
  });

  it('shows the recent feed with real data; a row navigates to the transaction detail', async () => {
    const user = userEvent.setup();
    renderDashboard();

    // Newest-first heroes from the stateful ledger, signed by TYPE (unsigned contract amounts).
    expect(await screen.findByText('Salary — July')).toBeInTheDocument();
    expect(screen.getByText('+€1,250.50')).toBeInTheDocument();
    // Transfer rows lead with the counterparty handle.
    expect(screen.getByText('To @john_d')).toBeInTheDocument();

    // A REAL id rides the click into the detail route (the mock era navigated to
    // fabricated ids that 404'd).
    await user.click(screen.getByText('Salary — July'));
    expect(await screen.findByText('TX DETAIL')).toBeInTheDocument();
  });

  it('opens the real DepositDialog over the full account list from the quick action', async () => {
    const user = userEvent.setup();
    renderDashboard();

    await screen.findByText('Main Account');
    // By ROLE with the exact name: the recent rows also contain "Deposit" as a type label,
    // but their accessible name is the full row concatenation, so only the quick action matches.
    await user.click(screen.getByRole('button', { name: 'Deposit' }));

    // The dialog mounts on open with the real accounts to pick from.
    expect(await screen.findByText('Deposit Money')).toBeInTheDocument();
    expect(screen.getAllByText('Main Account').length).toBeGreaterThan(1);
  });

  it('renders the monthly summary aggregate from the server-side endpoint', async () => {
    // Fixed override: the rendering contract is pinned independently of the clock and
    // the ledger's dates (the stateful handler's math is mock infra, not page behavior).
    server.use(
      http.get('*/api/transactions/summary', () =>
        HttpResponse.json({
          data: {
            totalIncome: 2000,
            totalExpenses: 500,
            netChange: 1500,
            pendingCount: 1,
            fromDate: '2026-07-01T00:00:00.0000000Z',
            toDate: '2026-07-23T12:00:00.0000000Z',
          },
          message: null,
        }),
      ),
    );
    renderDashboard();

    // Month-labelled card with signed EUR rows and the pending count.
    const monthLabel = format(new Date(), 'MMMM');
    expect(await screen.findByText(`${monthLabel} Summary`)).toBeInTheDocument();
    expect(await screen.findByText('+€2,000.00')).toBeInTheDocument();
    expect(screen.getByText('-€500.00')).toBeInTheDocument();
    expect(screen.getByText('+€1,500.00')).toBeInTheDocument();
    expect(screen.getByText('Pending Transactions')).toBeInTheDocument();
  });

  it('an accounts failure gates the page: problem detail + Retry, no stale cards', async () => {
    server.use(
      http.get('*/api/accounts', () =>
        problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Something broke.' }),
      ),
    );
    renderDashboard();

    expect(await screen.findByText(/Something broke\./)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(screen.queryByText('Main Account')).not.toBeInTheDocument();
  });

  it('a summary failure stays SECTIONAL: inline retry while the rest of the page lives', async () => {
    server.use(
      http.get('*/api/transactions/summary', () =>
        problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Aggregate down.' }),
      ),
    );
    renderDashboard();

    // The page (accounts + feed) is intact…
    expect(await screen.findByText('Main Account')).toBeInTheDocument();
    expect(await screen.findByText('Salary — July')).toBeInTheDocument();
    // …and only the summary card degrades, with its own retry. hidden:true because the
    // desktop right column is display:none mobile-first and jsdom never evaluates the
    // desktop media query — role queries would exclude it while text queries find it.
    expect(await screen.findByText('Could not load the summary.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry', hidden: true })).toBeInTheDocument();
  });

  it('zero accounts → the create-first-account CTA routes to the accounts page', async () => {
    const user = userEvent.setup();
    server.use(http.get('*/api/accounts', () => HttpResponse.json({ data: [], message: null })));
    renderDashboard();

    const cta = await screen.findByRole('button', { name: 'Create your first account' });
    await user.click(cta);
    expect(await screen.findByText('ACCOUNTS PAGE')).toBeInTheDocument();
  });
});
