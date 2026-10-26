import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { mockState, toWire } from '../mocks/state';
import { makeTestStore, renderWithProviders } from '../test/renderWithProviders';
import {
  COPY,
  advanceUntil,
  emulateFocusFixup,
  fakeClockUser,
  hintRegion,
  installFakeClock,
  sleep,
} from '../test/outage';
import { apiSlice } from '../features/api/apiSlice';
import { formatDateHeading } from '../utils/format';
import { HistoryPage } from './HistoryPage';

/**
 * T1 — the infinite history feed. Pins: the signed-mock trap is dead (amounts are
 * UNSIGNED, direction comes from the TYPE), summary math excludes Failed/Reversed
 * and follows the rows on screen, a caption names those rows, pages accumulate
 * through Load more, and loading/error/empty states are first-class.
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
    expect(
      screen.getByText(
        'Totals of the 20 transactions shown below. Failed and reversed ones are not counted.',
      ),
    ).toBeInTheDocument();
  });

  it('Load more appends page 2 and then disappears; the summary re-totals', async () => {
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary');

    expect(screen.queryByText('Top-up #16')).not.toBeInTheDocument();
    expect(
      screen.getByText(
        'Totals of the 20 transactions shown below. Failed and reversed ones are not counted.',
      ),
    ).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));

    expect(await screen.findByText('Top-up #16')).toBeInTheDocument();
    // 25 of 25 loaded — nothing more to fetch.
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument(),
    );
    // Summary now includes page 2's five €10 deposits, and the caption counts the same rows.
    expect(screen.getByText('+€1,525.50')).toBeInTheDocument();
    expect(screen.queryByText('+€1,475.50')).not.toBeInTheDocument();
    expect(
      screen.getByText(
        'Totals of the 25 transactions shown below. Failed and reversed ones are not counted.',
      ),
    ).toBeInTheDocument();
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

    // Two transfers are left on screen: -€200.00 Pending and +€75.00. Neither is
    // failed or reversed, so the card adds those two up and does not add the exclusion sentence.
    expect(screen.getByText('-€125.00')).toBeInTheDocument();
    expect(screen.queryByText('+€1,475.50')).not.toBeInTheDocument();
    expect(screen.queryByText('-€250.00')).not.toBeInTheDocument();
    expect(screen.queryByText('+€1,225.50')).not.toBeInTheDocument();
    expect(screen.getAllByText('+€75.00')).toHaveLength(2);
    expect(screen.getAllByText('-€200.00')).toHaveLength(2);
    expect(screen.getByText('Totals of the 2 transfers shown below.')).toBeInTheDocument();
    expect(screen.queryByText(/Failed and reversed ones are not counted/)).not.toBeInTheDocument();
  });

  it('names one shown row in the singular, and skips the exclusion sentence when none is failed or reversed', async () => {
    const cases = [
      {
        type: 'Deposit' as const,
        filter: 'Deposits',
        noun: 'deposit',
        row: 'Salary',
        take: (t: (typeof mockState.transactions)[number]) => t.description === 'Salary',
      },
      {
        type: 'Withdrawal' as const,
        filter: 'Withdrawals',
        noun: 'withdrawal',
        row: 'Withdrawal',
        take: (t: (typeof mockState.transactions)[number]) => t.description === null,
      },
      {
        type: 'TransferIn' as const,
        filter: 'Transfers',
        noun: 'transfer',
        row: 'From @anna_k',
        take: (t: (typeof mockState.transactions)[number]) => t.senderAzureTag === 'anna_k',
      },
    ];

    for (const item of cases) {
      const entry = mockState.transactions.find(
        (t) => t.type === item.type && t.status === 'Completed' && item.take(t),
      );
      if (!entry) throw new Error(`the mock has no completed ${item.type} named ${item.row}`);

      server.use(
        http.get('*/api/transactions', () =>
          HttpResponse.json({
            data: [toWire(entry)],
            pagination: {
              page: 1,
              pageSize: 20,
              totalItems: 1,
              totalPages: 1,
              hasNextPage: false,
              hasPreviousPage: false,
            },
          }),
        ),
      );

      renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
      expect(await screen.findByText(item.row)).toBeInTheDocument();
      expect(screen.getByText('Totals of the 1 transaction shown below.')).toBeInTheDocument();
      expect(
        screen.queryByText(/Failed and reversed ones are not counted/),
      ).not.toBeInTheDocument();

      await userEvent.click(screen.getByRole('button', { name: item.filter }));
      expect(screen.getByText(`Totals of the 1 ${item.noun} shown below.`)).toBeInTheDocument();
      expect(
        screen.queryByText(/Failed and reversed ones are not counted/),
      ).not.toBeInTheDocument();

      if (item.noun === 'transfer') {
        await userEvent.click(screen.getByRole('button', { name: 'Deposits' }));
        expect(
          screen.getByText('No transactions found for the selected filter.'),
        ).toBeInTheDocument();
        expect(screen.queryByText(/Totals of the/)).not.toBeInTheDocument();
        expect(screen.getAllByText('+€0.00')).toHaveLength(2);
        expect(screen.getByText('-€0.00')).toBeInTheDocument();
      }

      cleanup();
      server.resetHandlers();
    }
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

  it('a FAILED refresh of the whole list keeps the rows, the filter and the totals, and says so under the list', async () => {
    // Every deposit, withdrawal and transfer invalidates the list, and the feed then asks again for
    // every page it holds. That refresh failing is not a first load that failed: the pages already
    // read stay on screen, with the failure said in the alert under them.
    const stopFixup = emulateFocusFixup();
    try {
      const store = makeTestStore();
      renderWithProviders(<HistoryPage />, { routerEntries: ['/history'], store });
      await screen.findByText('Salary');
      await userEvent.click(screen.getByRole('button', { name: 'Load more' }));
      expect(await screen.findByText('Top-up #16')).toBeInTheDocument();
      await waitFor(() =>
        expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument(),
      );

      const rowsNow = () => document.querySelectorAll('tbody tr').length;
      const twoPages = rowsNow();
      const table = screen.getByRole('table');
      const [pageSlot, listSlot, ...others] = screen.getAllByRole('alert');
      expect(others).toHaveLength(0);
      expect(
        table.compareDocumentPosition(pageSlot) & Node.DOCUMENT_POSITION_PRECEDING,
      ).toBeTruthy();
      expect(
        table.compareDocumentPosition(listSlot) & Node.DOCUMENT_POSITION_FOLLOWING,
      ).toBeTruthy();
      expect(listSlot).toBeEmptyDOMElement();

      // A new row on page 1, which answers, and page 2 failing: the refresh fails as a whole, and
      // the rows on screen stay the list that last loaded rather than half of a new one.
      mockState.transactions.unshift({
        ...mockState.transactions[0],
        id: '019f7b3f-0000-7000-8000-0000000f0001',
        description: 'Refund from the shop',
      });
      server.use(
        http.get('*/api/transactions', ({ request }) =>
          new URL(request.url).searchParams.get('Page') === '2'
            ? problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'The list exploded.' })
            : undefined,
        ),
      );
      // What a money mutation does to this feed.
      store.dispatch(apiSlice.util.invalidateTags([{ type: 'Transaction', id: 'LIST' }]));
      await waitFor(() => expect(listSlot).toHaveTextContent('The list exploded.'));

      expect(pageSlot).toBeEmptyDOMElement();
      expect(rowsNow()).toBe(twoPages);
      expect(screen.queryByText('Refund from the shop')).not.toBeInTheDocument();
      expect(screen.getByRole('group', { name: /Filter transactions/i })).toBeInTheDocument();
      expect(screen.getByText('+€1,525.50')).toBeInTheDocument();
      expect(screen.getByText('-€250.00')).toBeInTheDocument();

      // A Retry that fails again: its bar goes while it runs, and focus lands on the new one's Retry.
      const firstRetry = within(listSlot).getByRole('button', { name: 'Retry' });
      await userEvent.click(firstRetry);
      await waitFor(() => {
        const retry = within(listSlot).getByRole('button', { name: 'Retry' });
        expect(retry).not.toBe(firstRetry);
        expect(document.activeElement).toBe(retry);
      });
      expect(pageSlot).toBeEmptyDOMElement();
      expect(rowsNow()).toBe(twoPages);

      // With the server back, the Retry asks for the whole list again; the rows stay while it runs.
      server.resetHandlers();
      let release = () => {};
      const held = new Promise<void>((resolve) => (release = resolve));
      const asked: string[] = [];
      server.use(
        http.get('*/api/transactions', async ({ request }) => {
          asked.push(new URL(request.url).searchParams.get('Page') ?? '1');
          await held;
          return undefined;
        }),
      );
      await userEvent.click(within(listSlot).getByRole('button', { name: 'Retry' }));
      await waitFor(() => expect(listSlot).toBeEmptyDOMElement());
      expect(screen.queryByLabelText('Loading transactions')).toBeNull();
      expect(rowsNow()).toBe(twoPages);
      expect(screen.getByRole('group', { name: /Filter transactions/i })).toBeInTheDocument();

      release();
      expect(await screen.findByText('Refund from the shop')).toBeInTheDocument();
      expect(asked).toEqual(['1', '2']);
      expect(listSlot).toBeEmptyDOMElement();
      expect(pageSlot).toBeEmptyDOMElement();
    } finally {
      stopFixup();
    }
  });

  it('a FAILED refresh of a list with no rows keeps the empty state and says so under it', async () => {
    const emptyPage = () =>
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
      });
    server.use(http.get('*/api/transactions', emptyPage));
    const store = makeTestStore();
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'], store });
    const title = await screen.findByText('No Transactions');

    const alerts = screen.getAllByRole('alert');
    expect(alerts).toHaveLength(2);
    const [pageSlot, listSlot] = alerts;
    expect(title.compareDocumentPosition(listSlot) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(listSlot).toBeEmptyDOMElement();

    server.use(
      http.get('*/api/transactions', () => problem({ status: 500, errorCode: 'INTERNAL_ERROR' })),
    );
    store.dispatch(apiSlice.util.invalidateTags([{ type: 'Transaction', id: 'LIST' }]));
    await waitFor(() => expect(listSlot).toHaveTextContent('Could not load your transactions.'));

    expect(pageSlot).toBeEmptyDOMElement();
    expect(screen.getByText('No Transactions')).toBeInTheDocument();
    expect(screen.getByRole('group', { name: /Filter transactions/i })).toBeInTheDocument();

    server.resetHandlers();
    const asked: string[] = [];
    server.use(
      http.get('*/api/transactions', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('Page') ?? '1');
        return emptyPage();
      }),
    );
    await userEvent.click(within(listSlot).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(listSlot).toBeEmptyDOMElement());
    expect(asked).toEqual(['1']);
    expect(screen.getByText('No Transactions')).toBeInTheDocument();
    expect(pageSlot).toBeEmptyDOMElement();
  });

  for (const failsAgain of [false, true]) {
    it(`a slow Retry of the whole list says so under the rows, then ${
      failsAgain ? 'nothing more when it fails again' : '"Loaded." when the list is there'
    }`, async () => {
      // The hint under the buttons is the only place the list's Retry says it is slow: the page's
      // own hint stays silent, because the rows on screen are not its wait.
      const broken = () =>
        problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'The list exploded.' });
      const store = makeTestStore();
      renderWithProviders(<HistoryPage />, { routerEntries: ['/history'], store });
      await screen.findByText('Salary');
      const [pageSlot, listSlot] = screen.getAllByRole('alert');

      server.use(http.get('*/api/transactions', broken));
      store.dispatch(apiSlice.util.invalidateTags([{ type: 'Transaction', id: 'LIST' }]));
      await waitFor(() => expect(listSlot).toHaveTextContent('The list exploded.'));

      server.resetHandlers();
      const asked: number[] = [];
      server.use(
        http.get('*/api/transactions', async () => {
          asked.push(Date.now());
          await sleep(6_000);
          return failsAgain ? broken() : undefined;
        }),
      );
      installFakeClock();
      const user = fakeClockUser();
      await user.click(within(listSlot).getByRole('button', { name: 'Retry' }));
      await waitFor(() => expect(asked).toHaveLength(1));

      await advanceUntil(asked[0], 5_000);
      const region = hintRegion(COPY.slow);
      expect(screen.getAllByText(COPY.slow)).toHaveLength(1);
      expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
      expect(screen.queryByLabelText('Loading transactions')).toBeNull();
      expect(screen.getByText('Salary')).toBeInTheDocument();
      expect(pageSlot).toBeEmptyDOMElement();

      await advanceUntil(asked[0], 6_100);
      if (failsAgain) {
        await waitFor(() => expect(listSlot).toHaveTextContent('The list exploded.'));
        // The alert speaks for the failure; a "Loaded." next to it would say the opposite.
        expect(screen.queryByText(COPY.loaded)).not.toBeInTheDocument();
        expect(region).not.toBeInTheDocument();
      } else {
        await waitFor(() => expect(region).toHaveTextContent(COPY.loaded));
        expect(region.textContent).toBe(COPY.loaded);
        expect(listSlot).toBeEmptyDOMElement();
      }
      expect(pageSlot).toBeEmptyDOMElement();
      expect(screen.getByText('Salary')).toBeInTheDocument();
    });
  }

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
    expect(screen.queryByText(/Totals of the/)).not.toBeInTheDocument();
    expect(screen.getAllByText('+€0.00')).toHaveLength(2);
    expect(screen.getByText('-€0.00')).toBeInTheDocument();
  });
});
