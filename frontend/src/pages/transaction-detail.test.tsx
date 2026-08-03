import { Route, Routes } from 'react-router-dom';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { mockState } from '../mocks/state';
import { formatCurrency } from '../utils/format';
import { renderWithProviders } from '../test/renderWithProviders';
import { TransactionDetailPage } from './TransactionDetailPage';

const T1_DEPOSIT = '019f7b3f-0000-7000-8000-000000000b01';
const T3_TRANSFER = '019f7b3f-0000-7000-8000-000000000b03';
const T5_REVERSED = '019f7b3f-0000-7000-8000-000000000b05';

function seededBalanceAfter(id: string): number {
  const entry = mockState.transactions.find((t) => t.id === id);
  if (!entry) {
    throw new Error(`No seeded transaction ${id}`);
  }
  return entry.balanceAfter;
}

function renderDetail(id: string) {
  return renderWithProviders(
    <Routes>
      <Route path="/transactions/:id" element={<TransactionDetailPage />} />
    </Routes>,
    { routerEntries: [`/transactions/${id}`] },
  );
}

/**
 * T2 — the detail on CONTRACT fields only (the 7 fabricated mock fields are gone).
 * Pins: useParams wiring, the not-found state as a first-class outcome, and the
 * Reversed status the old page could not even represent.
 */
describe('transaction detail (T2)', () => {
  it('renders the contract fields for a deposit', async () => {
    renderDetail(T1_DEPOSIT);

    expect(await screen.findByText('+€1,250.50')).toBeInTheDocument();
    expect(screen.getAllByText('Deposit')).toHaveLength(2); // hero label + Type row
    expect(screen.getByText('Completed')).toBeInTheDocument();
    /*
      Both taken FROM the seeded entry rather than transcribed. The ledger is re-dated into the
      current month on every run, so `TXN-20260720-000101` and 'July 20, 2026' named a row that no
      longer exists — and the assertion's real content is "the page shows THIS entry's number and
      THIS entry's date", which is what it now says.
    */
    const salary = mockState.transactions.find((t) => t.description === 'Salary')!;
    expect(screen.getByText(salary.transactionNumber)).toBeInTheDocument();
    const day = new Date(salary.createdAt).toLocaleDateString('en-US', {
      month: 'long',
      day: 'numeric',
      year: 'numeric',
      timeZone: 'UTC',
    });
    expect(screen.getByText(new RegExp(day))).toBeInTheDocument();
    expect(screen.getByText('Salary')).toBeInTheDocument();
    // Balance after. Read from the seed rather than typed, because the seed DERIVES it — the
    // running balance is computed backwards from the account's real balance, so a hand-copied
    // literal here would be a second source of truth that goes stale on the next seed edit.
    expect(screen.getByText(formatCurrency(seededBalanceAfter(T1_DEPOSIT)))).toBeInTheDocument();
  });

  it('shows the transfer counterparty and the Pending status', async () => {
    renderDetail(T3_TRANSFER);

    expect(await screen.findByText('-€200.00')).toBeInTheDocument();
    expect(screen.getByText('@john_d')).toBeInTheDocument();
    expect(screen.getByText('Pending')).toBeInTheDocument();
    expect(screen.getAllByText('Transfer sent').length).toBeGreaterThan(0);
  });

  it('represents the Reversed status', async () => {
    renderDetail(T5_REVERSED);

    expect(await screen.findByText('Reversed')).toBeInTheDocument();
    expect(screen.getByText('-€30.00')).toBeInTheDocument();
  });

  it('an unknown id is a first-class not-found state, not an error bar', async () => {
    renderDetail('00000000-0000-0000-0000-000000000000');

    expect(await screen.findByText('Transaction not found')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Back to history' })).toBeInTheDocument();
    expect(screen.queryByText(/Support code/)).not.toBeInTheDocument();
  });

  it('a non-404 failure shows the problem bar and Retry recovers', async () => {
    server.use(
      http.get('*/api/transactions/:id', () =>
        problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Detail exploded.' }),
      ),
    );
    renderDetail(T1_DEPOSIT);

    expect(await screen.findByText(/Detail exploded\./)).toBeInTheDocument();

    server.resetHandlers();
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(await screen.findByText('+€1,250.50')).toBeInTheDocument();
  });
});
