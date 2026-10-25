import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { mockState } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { emulateFocusFixup } from '../test/outage';
import { formatDateHeading } from '../utils/format';
import { HistoryPage } from './HistoryPage';

/**
 * T1 — the infinite history feed. Pins: the signed-mock trap is dead (amounts are
 * UNSIGNED, direction comes from the TYPE), summary math excludes Failed/Reversed,
 * pages accumulate through Load more, and loading/error/empty states are first-class.
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

  it('a FAILED Load more keeps the rows on screen and says so under its button', async () => {
    // The error of `fetchNextPage` is the query's error. Drawn as the page's failure, it would put
    // one bar in place of the filter, the totals and every row already read; it is said instead in
    // an alert under the buttons, and the rows stay.
    //
    // `fetchNextPage` resolves whether or not the request succeeded, so the cap of pages shown
    // must not run ahead of the pages loaded: no "Show less" that would hide nothing.
    const stopFixup = emulateFocusFixup();
    try {
      renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
      await screen.findByText('Salary');

      const rowsNow = () => document.querySelectorAll('tbody tr').length;
      const firstPage = rowsNow();
      const loadMore = screen.getByRole('button', { name: 'Load more' });
      // The alert under the buttons is on the page before anything fails, so its bar is announced.
      const underLoadMore = () =>
        screen
          .getAllByRole('alert')
          .filter((a) => loadMore.compareDocumentPosition(a) & Node.DOCUMENT_POSITION_FOLLOWING);
      expect(underLoadMore()).toHaveLength(1);
      const slot = underLoadMore()[0];
      expect(slot).toBeEmptyDOMElement();
      const pageSlot = screen.getAllByRole('alert').find((a) => a !== slot);

      // Page 1 is already cached; only the NEXT request fails, once the button shows its wait.
      let fail = () => {};
      const failing = new Promise<void>((resolve) => (fail = resolve));
      server.use(
        http.get('*/api/transactions', async () => {
          await failing;
          return problem({
            status: 500,
            errorCode: 'INTERNAL_ERROR',
            detail: 'Page two exploded.',
          });
        }),
      );
      await userEvent.click(loadMore);
      await waitFor(() => expect(loadMore).toBeDisabled());
      fail();
      await waitFor(() => expect(slot).toHaveTextContent('Page two exploded.'));

      expect(pageSlot).toBeEmptyDOMElement();
      expect(rowsNow()).toBe(firstPage);
      expect(screen.getByRole('group', { name: /Filter transactions/i })).toBeInTheDocument();
      expect(screen.getByText('+€1,475.50')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Show less' })).not.toBeInTheDocument();
      // The pressed button was disabled while it waited, so focus fell to the page: it lands on
      // the Retry of the new bar.
      const retry = within(slot).getByRole('button', { name: 'Retry' });
      await waitFor(() => expect(document.activeElement).toBe(retry));

      // The Retry asks for the next page again; while it runs, the bar goes and the rows stay.
      server.resetHandlers();
      let release = () => {};
      const held = new Promise<void>((resolve) => (release = resolve));
      server.use(
        http.get('*/api/transactions', async ({ request }) => {
          if (new URL(request.url).searchParams.get('Page') !== '2') return undefined;
          await held;
          return undefined;
        }),
      );
      await userEvent.click(retry);
      await waitFor(() => expect(slot).toBeEmptyDOMElement());
      expect(pageSlot).toBeEmptyDOMElement();
      expect(rowsNow()).toBe(firstPage);

      release();
      expect(await screen.findByText('Top-up #16')).toBeInTheDocument();
      expect(rowsNow()).toBeGreaterThan(firstPage);

      // "Show less" now hides a page that was loaded.
      await userEvent.click(screen.getByRole('button', { name: 'Show less' }));
      await waitFor(() => expect(rowsNow()).toBe(firstPage));
    } finally {
      stopFixup();
    }
  });

  it('the Retry under "Load more" asks for the failed page even while a loaded page is hidden', async () => {
    // "Load more" first shows a loaded page that "Show less" hid. The Retry under the buttons is for
    // the page that failed: after "Show less" it must still ask for that page, not show a hidden
    // one under a bar that goes on saying the failure.
    mockState.transactions.push(
      ...mockState.transactions.slice(5, 25).map((t, i) => ({
        ...t,
        id: `019f7b3f-0000-7000-8000-0000000e${String(i).padStart(4, '0')}`,
        description: `Extra #${i + 1}`,
        createdAt: `2020-01-01T00:${String(59 - i)}:00.0000000Z`,
      })),
    );
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary');
    const loadMore = screen.getByRole('button', { name: 'Load more' });
    const slot = screen
      .getAllByRole('alert')
      .find((a) => loadMore.compareDocumentPosition(a) & Node.DOCUMENT_POSITION_FOLLOWING);
    if (!slot) throw new Error('no alert under "Load more"');

    await userEvent.click(loadMore);
    expect(await screen.findByText('Extra #15')).toBeInTheDocument();

    // Page 3 fails with no detail of its own, so the bar says the page's own sentence.
    server.use(
      http.get('*/api/transactions', ({ request }) =>
        new URL(request.url).searchParams.get('Page') === '3'
          ? problem({ status: 500, errorCode: 'INTERNAL_ERROR' })
          : undefined,
      ),
    );
    await userEvent.click(loadMore);
    await waitFor(() => expect(slot).toHaveTextContent('Could not load your transactions.'));

    await userEvent.click(screen.getByRole('button', { name: 'Show less' }));
    expect(screen.queryByText('Extra #15')).not.toBeInTheDocument();
    expect(slot).toHaveTextContent('Could not load your transactions.');

    server.resetHandlers();
    const asked: string[] = [];
    server.use(
      http.get('*/api/transactions', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('Page') ?? '1');
        return undefined;
      }),
    );
    await userEvent.click(within(slot).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(asked).toEqual(['3']));
    await waitFor(() => expect(slot).toBeEmptyDOMElement());

    // Page 3 is loaded now: "Load more" shows the hidden pages without asking again.
    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('Extra #20')).toBeInTheDocument();
    expect(asked).toEqual(['3']);
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
