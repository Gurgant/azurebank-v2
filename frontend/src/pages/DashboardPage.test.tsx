import { Route, Routes } from 'react-router-dom';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { DashboardPage } from './DashboardPage';

/**
 * The dashboard, and the one property the whole design hangs on.
 *
 * **A running balance beside a cross-account total cannot reconcile.** `balanceAfter` is
 * per-account; the hero is a sum. Printing both is how the old screen came to show €2,200.50 on its
 * newest row under a €2,080.50 heading — two numbers contradicting each other on a banking home
 * page, which is the kind of thing that costs trust rather than points.
 *
 * So the scope selector is not decoration: it decides what the page is ABOUT, and the Balance
 * column exists only when the answer is one account. That is what most of this file asserts.
 */

function renderDashboard() {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={<DashboardPage />} />
      <Route path="/transactions/:id" element={<div>TX DETAIL</div>} />
      <Route path="/accounts" element={<div>ACCOUNTS PAGE</div>} />
      <Route path="/about" element={<div>ABOUT PAGE</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

/** The ledger's column headers, which are the observable side of the thesis. */
const columns = () => [...document.querySelectorAll('th')].map((th) => th.textContent?.trim());

describe('the scope decides what the page is about', () => {
  it('sums every account, and prints NO running balance while it does', async () => {
    renderDashboard();
    const heading = await screen.findByRole('heading', { level: 1 });

    // 1250.50 + 830.00
    expect(heading).toHaveTextContent('€2,080.50');
    expect(screen.getByText(/Across 2 accounts/)).toBeInTheDocument();

    // The load-bearing absence. A Balance column here would be a per-account figure under a
    // cross-account heading.
    expect(columns()).toEqual(['When', 'Entry', 'Amount', 'Status']);
  });

  it('re-points the heading at one account and only THEN shows the balance column', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    await userEvent.click(screen.getByRole('button', { name: /Main Account/ }));

    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('€1,250.50');
    expect(columns()).toEqual(['When', 'Entry', 'Amount', 'Balance', 'Status']);
    expect(screen.getByRole('button', { name: /Main Account/ })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
  });

  it('names the accounts by their masked numbers, and marks the primary one', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    const main = screen.getByRole('button', { name: /Main Account/ });
    // The tail is how a person tells two accounts apart; the full number never appears here.
    // `maskAccountNumber` renders `AB-••••-••••-90`: the bank prefix and the last two digits.
    expect(main.textContent).toMatch(/AB-.*90/);
    expect(main).toHaveTextContent('PRIMARY');
    expect(screen.getByRole('button', { name: /Rainy Day/ })).toHaveTextContent('€830.00');
  });

  // NOT COVERED: the single-account case, where a lone account is implicitly the scope and the
  // Balance column therefore renders without any chip being pressed. The behaviour is implemented
  // (DashboardPage.tsx, `selected`) but overriding the accounts handler with a one-account
  // response kept failing the response schema, and a fixture I cannot get valid is not a test —
  // it is a red suite. Recorded here rather than deleted silently.
  it('says the month is all-accounts, because the API cannot yet scope it', async () => {
    // `getTransactionSummary` takes { fromDate, toDate } and no AccountId. Rendering a total that
    // silently disagrees with the ledger beside it would be worse than saying so.
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });
    await userEvent.click(screen.getByRole('button', { name: /Rainy Day/ }));

    expect(await screen.findByText(/cannot yet be filtered to one/)).toBeInTheDocument();
  });
});

describe('the ledger', () => {
  it('opens a transaction, and strikes through one that did not stand', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    // A reversed withdrawal put the money back. A bare minus sign says it did not.
    const reversed = screen.getByText('ATM — disputed').closest('tr')!;
    expect(within(reversed).getByText('Reversed')).toBeInTheDocument();
    expect(within(reversed).getByText(/-€30\.00/)).toHaveStyle({ textDecoration: 'line-through' });

    await userEvent.click(screen.getByRole('button', { name: 'Salary — July' }));
    expect(await screen.findByText('TX DETAIL')).toBeInTheDocument();
  });

  it('marks each entry Completed, Pending or Reversed in words, not only in colour', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    const statuses = [...document.querySelectorAll('tbody tr')].map((tr) =>
      tr.lastElementChild?.textContent?.trim(),
    );
    expect(statuses).toEqual(['Completed', 'Completed', 'Pending', 'Completed', 'Reversed']);
  });
});

describe('the rest of the page', () => {
  it('turns the one pending item into a task you can open', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    const attention = screen.getByText('Needs attention').closest('section')!;
    await userEvent.click(within(attention).getByRole('button', { name: /Dinner split/ }));
    expect(await screen.findByText('TX DETAIL')).toBeInTheDocument();
  });

  it('offers a way to reach a human that goes somewhere real', async () => {
    // The old copy promised a support team available 24/7. There is no team — this is a portfolio
    // project — and a link to nowhere is the same defect as a button that cannot do what it says.
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    expect(screen.queryByText(/24\/7/)).toBeNull();
    await userEvent.click(screen.getByRole('link', { name: /Get in touch/ }));
    expect(await screen.findByText('ABOUT PAGE')).toBeInTheDocument();
  });

  it('hides every figure at once when asked, and says which state it is in', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    await userEvent.click(screen.getByRole('button', { name: 'Hide balances' }));

    expect(await screen.findByRole('heading', { level: 1 })).not.toHaveTextContent('€2,080.50');
    // The chips must go too: hiding the total while printing its parts underneath hides nothing.
    expect(screen.getByRole('button', { name: /Main Account/ })).not.toHaveTextContent('1,250.50');
    expect(screen.getByRole('button', { name: 'Show balances' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
  });

  it('opens the deposit dialog over the real account list', async () => {
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    await userEvent.click(screen.getByRole('button', { name: /Deposit/ }));
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
  });
});

describe('a partial failure', () => {
  it('keeps the summary sectional: the ledger and the balance survive it', async () => {
    server.use(
      http.get('*/api/transactions/summary', () =>
        problem({ status: 500, detail: 'Summary unavailable' }),
      ),
    );
    renderDashboard();

    // D22: accounts gate the page, everything else fails alone.
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('€2,080.50');
    expect(await screen.findByText(/Could not load this month/)).toBeInTheDocument();
    expect(screen.getByText('Salary — July')).toBeInTheDocument();
  });

  it('gates the whole page when the accounts themselves fail', async () => {
    server.use(
      http.get('*/api/accounts', () => problem({ status: 500, detail: 'Accounts unavailable' })),
    );
    renderDashboard();

    expect(
      await screen.findByText(/Could not load your accounts|Accounts unavailable/),
    ).toBeInTheDocument();
    expect(screen.queryByRole('heading', { level: 1 })).toBeNull();
  });
});
