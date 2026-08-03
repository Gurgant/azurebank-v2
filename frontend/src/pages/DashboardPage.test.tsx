import { Route, Routes } from 'react-router-dom';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http } from 'msw';
import { server } from '../mocks/server';
import { mockState } from '../mocks/state';
import { problem } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { DashboardPage } from './DashboardPage';
import { resolveScopedAccountId } from './dashboardScope';

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

  it('treats a lone account as the scope, chips or no chips', async () => {
    // One account means "all accounts" IS that account: one ledger, so the running balance
    // reconciles, and withholding the column would be the rule declining to apply itself where it
    // is trivially true. No chips render in that case — nothing to choose between — so nothing
    // else would ever set the scope.
    //
    // The state is trimmed rather than the handler overridden. A hand-written response has to
    // reproduce the envelope AND the field names exactly, and my first attempt got both wrong
    // (`message` missing, `accountType`/`currency` instead of `type`), so the page rendered its
    // accounts-error state and the test failed for a reason that had nothing to do with scope.
    // Trimming the seed cannot drift from the handler, because the handler still builds it.
    mockState.accounts = mockState.accounts.slice(0, 1);
    renderDashboard();

    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('€1,250.50');
    expect(columns()).toEqual(['When', 'Entry', 'Amount', 'Balance', 'Status']);
    expect(screen.queryByRole('button', { name: /All accounts/ })).toBeNull();
  });

  it('scopes the month to the selected account, figures and all', async () => {
    // This test used to assert the opposite — that the card SAID it could not be filtered, because
    // `getTransactionSummary` took no AccountId. It does now, so the assertion is the behaviour
    // that replaced the apology.
    //
    // The money figure is what makes it an assertion rather than a label check: the seed splits
    // transactions across both accounts on purpose, so a scoped total that still matched the
    // all-accounts one would mean the parameter reached the query and changed nothing.
    renderDashboard();
    await screen.findByRole('heading', { level: 1 });

    const moneyIn = async () => {
      const row = (await screen.findByText('Money in')).parentElement!;
      return row.textContent!.replace('Money in', '').trim();
    };

    /*
      ABSOLUTE figures, not "the scoped one differs from the other".

      The relative form was the original, and it is a bad oracle twice over. It passes on any wrong
      number that merely differs from the total, and it FAILS when the two are legitimately equal —
      which is exactly how it read when the month rolled over and every figure went to zero:
      `expected '+€0.00' not to be '+€0.00'`, a message that names no expectation and points at
      nothing. These come from the seed's own amounts: money in across both accounts is the 1250.50
      salary plus 175 of top-ups and a transfer in; Rainy Day's share of that is 175.
    */
    expect(await screen.findByText(/Across all 2 accounts/)).toBeInTheDocument();
    await waitFor(async () => expect(await moneyIn()).toBe('+€1,525.50'));

    await userEvent.click(screen.getByRole('button', { name: /Rainy Day/ }));

    expect(await screen.findByText('Rainy Day only')).toBeInTheDocument();
    await waitFor(async () => expect(await moneyIn()).toBe('+€175.00'));
  });
});

describe('the scope as a query argument', () => {
  // A pure function, so the stale case is reachable at all. The version written inline in the
  // component was covered by a render test that removed the account and re-rendered — and that test
  // passed with the check DELETED, because a second `render` mounts a fresh component whose `scope`
  // is back to 'all'. It asserted nothing; this asserts the rule.
  const accounts = [{ id: 'a' }, { id: 'b' }];

  it('sends the picked account while it exists', () => {
    expect(resolveScopedAccountId('b', accounts)).toBe('b');
  });

  it('sends nothing for the all-accounts state', () => {
    expect(resolveScopedAccountId('all', accounts)).toBeUndefined();
  });

  it('falls back to the total when the picked account is gone', () => {
    // The one the label already got right and the query did not: an account removed from under a
    // mounted page must not keep being asked about, or the card reports 403 for a scope the page
    // no longer shows as selected.
    expect(resolveScopedAccountId('b', [{ id: 'a' }])).toBeUndefined();
  });

  it('falls back while the accounts are still loading, rather than guessing', () => {
    expect(resolveScopedAccountId('b', [])).toBeUndefined();
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

    await userEvent.click(screen.getByRole('button', { name: 'Salary' }));
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
    // "To @john_d", not "Dinner split": the counterparty leads on a transfer now, because WHO the
    // money went to is the identifying fact and the note is why. The two screens used to disagree
    // about which of them to show for the same row.
    await userEvent.click(within(attention).getByRole('button', { name: /To @john_d/ }));
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
    expect(screen.getByText('Salary')).toBeInTheDocument();
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
