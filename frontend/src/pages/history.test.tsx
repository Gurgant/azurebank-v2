import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { mockState } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { formatDateHeading } from '../utils/format';
import { HistoryPage } from './HistoryPage';

/**
 * T1 — the infinite history feed. Pins: the signed-mock trap is dead (amounts are
 * UNSIGNED, direction comes from the TYPE), summary math excludes Failed/Reversed,
 * pages accumulate through Load more, and D22 states are first-class.
 */
describe('history feed (T1)', () => {
  it('renders rows with type-driven signs, counterparties, and status text', async () => {
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });

    expect(await screen.findByText('Salary')).toBeInTheDocument();
    expect(screen.getByText('+€1,250.50')).toBeInTheDocument();

    // Null description falls back to the type; the amount sign comes from the TYPE.
    expect(screen.getByText('-€50.00')).toBeInTheDocument();

    // Transfers headline the counterparty and surface a non-Completed status.
    expect(screen.getByText('To @john_d')).toBeInTheDocument();
    expect(screen.getByText('Pending')).toBeInTheDocument();
    expect(screen.getByText('From @anna_k')).toBeInTheDocument();
    expect(screen.getByText('+€75.00')).toBeInTheDocument();

    /*
      The heading is FORMATTED FROM the seed's own newest entry, not named.

      It used to read 'July 20, 2026', which stopped existing the moment the ledger was re-dated
      into the current month — and would have gone stale every month even if it had not been. What
      the test actually means is "the group heading is the day of the row above it", so it says that.

      FORMATTED WITH THE PAGE'S OWN FORMATTER, the transaction-detail test's rule, and this file
      had not followed it: the expectation was built with `toLocaleDateString(..., { timeZone:
      'UTC' })` while `groupByDate` uses date-fns `format()`, which is LOCAL. The two name different
      days whenever the newest row's instant falls on a different calendar day in UTC than in the
      viewer's zone — for America/Los_Angeles that is every day from 00:00 to 07:00 UTC, which is why
      the zone leg PR #145 added went red only on a run that happened to start at 02:02Z, on a PR
      that had not touched the frontend. A guard measuring the wrong window, again: the same instant
      through the same function cannot drift.
    */
    expect(
      screen.getByText(formatDateHeading(mockState.transactions[0].createdAt)),
    ).toBeInTheDocument();
  });

  it('computes the summary from TYPES over loaded pages, excluding Reversed', async () => {
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary');

    // Page 1 = 5 heroes + 15 fillers (€10 deposits). Income = 1250.50 + 75 + 150;
    // expenses = 50 + 200 — the €30 Reversed withdrawal must NOT count.
    expect(screen.getByText('+€1,475.50')).toBeInTheDocument();
    expect(screen.getByText('-€250.00')).toBeInTheDocument();
    expect(screen.getByText('+€1,225.50')).toBeInTheDocument();
  });

  it('Load more appends page 2 and then disappears; the summary re-totals', async () => {
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary');

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
    await screen.findByText('Salary');

    await userEvent.click(screen.getByRole('button', { name: 'Deposits' }));
    expect(screen.getByText('Salary')).toBeInTheDocument();
    expect(screen.queryByText('To @john_d')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Transfers' }));
    expect(screen.getByText('To @john_d')).toBeInTheDocument();
    expect(screen.getByText('From @anna_k')).toBeInTheDocument();
    expect(screen.queryByText('Salary')).not.toBeInTheDocument();
  });

  it('announces which filter is selected, not just colours it', async () => {
    // The inline version was four bare buttons: no aria-pressed, no group, no name for the group.
    // Which one was active was carried by background colour ALONE, so a screen reader announced
    // four identical controls. That is the defect the extraction exists to fix, and this is the
    // assertion that keeps it fixed.
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary');

    const group = screen.getByRole('group', { name: /Filter transactions/i });
    expect(within(group).getByRole('button', { name: 'All' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    await userEvent.click(within(group).getByRole('button', { name: 'Deposits' }));
    expect(within(group).getByRole('button', { name: 'Deposits' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    expect(within(group).getByRole('button', { name: 'All' })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
  });

  it('can shrink again after loading more, without refetching anything', async () => {
    // Expanding used to be one-way. Every press added a page and nothing took one away, so a long
    // history left hundreds of rows on screen and only a reload to get back.
    //
    // "Show less" hides pages instead of dropping them: the cache still holds them, so pressing
    // "Load more" again neither repeats a request nor waits. That is why this asserts on rows
    // rather than on request counts — the point is what the reader sees, and the absence of a
    // refetch is what makes it instant.
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary');

    const rowsNow = () => document.querySelectorAll('tbody tr').length;
    const firstPage = rowsNow();
    expect(screen.queryByRole('button', { name: 'Show less' })).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(rowsNow()).toBeGreaterThan(firstPage));

    await userEvent.click(screen.getByRole('button', { name: 'Show less' }));
    await waitFor(() => expect(rowsNow()).toBe(firstPage));
    expect(screen.queryByRole('button', { name: 'Show less' })).toBeNull();
  });

  it('a FAILED Load more leaves no "Show less" that hides nothing', async () => {
    // `fetchNextPage` resolves whether or not the request succeeded — RTK Query reports failure in
    // the result rather than throwing — so incrementing the cap unconditionally put the view state
    // ahead of the pages that actually loaded. The failure itself replaces the feed with the D22
    // error state, so nothing looks wrong until Retry recovers: then one page is on screen and
    // "Show less" is offered anyway, pointing at a page that was never fetched.
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary');

    const rowsNow = () => document.querySelectorAll('tbody tr').length;
    const firstPage = rowsNow();

    // Page 1 is already cached; only the NEXT request fails.
    server.use(
      http.get('*/api/transactions', () =>
        problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Page two exploded.' }),
      ),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByText(/Page two exploded\./)).toBeInTheDocument();

    // The backend recovers and Retry restores exactly the one page that was ever loaded.
    server.resetHandlers();
    await userEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await screen.findByText('Salary');

    expect(rowsNow()).toBe(firstPage);
    expect(screen.queryByRole('button', { name: 'Show less' })).not.toBeInTheDocument();
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
    expect(await screen.findByText('Salary')).toBeInTheDocument();
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
