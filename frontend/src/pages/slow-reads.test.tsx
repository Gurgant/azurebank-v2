import { Route, Routes } from 'react-router-dom';
import { act, cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../mocks/server';
import { problem, serviceUnavailable } from '../mocks/problem';
import { renderWithProviders } from '../test/renderWithProviders';
import { expectNoNestedLiveRegions } from '../test/liveRegions';
import {
  COPY,
  advance,
  advanceUntil,
  alertSlot,
  fakeClockUser,
  hintRegion,
  never,
  sleep,
  installFakeClock,
} from '../test/outage';
import { LOADED_KEPT_MS } from '../hooks/useWaitPhase';
import { AccountsPage } from './AccountsPage';
import { DashboardPage } from './DashboardPage';
import { HistoryPage } from './HistoryPage';
import { InternalTransferPage } from './InternalTransferPage';
import { TransactionDetailPage } from './TransactionDetailPage';
import { TransferPage } from './TransferPage';

/**
 * A read that is slow says so, can be stopped, and a read that failed is announced — every time.
 *
 * The rules under test, in the order a visitor meets them:
 *  - nothing for 5 s, "Taking longer than usual…" at 5 s, "Still trying…" and "Stop waiting" at
 *    20 s, in a polite region that holds those words and nothing else;
 *  - "Stop waiting" aborts the request (no retry follows) and lands the visitor on the page's
 *    error bar, announced, with focus on its Retry — the button they pressed has just vanished;
 *  - Retry shows the wait again, and a second failure is announced again: a bar that stayed on
 *    screen through the retry would say nothing the second time.
 *
 * Thresholds: a "not yet" is measured from before the page rendered, a "by now" from the moment
 * the wait was certainly on screen — so neither can pass by the test being slow. For a first load
 * that is when the request reached the server: the page draws its first wait before it sends
 * anything. A Retry's wait is drawn later than its request leaves: RTK hands the page a refetch's
 * start on the next animation frame. So a Retry's "by now" is measured from its spinner.
 */

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

const T1_DEPOSIT = '019f7b3f-0000-7000-8000-000000000b01';

/** A handler that holds every request, remembering when each arrived and whether it was aborted. */
function holdAll(method: 'get', path: string) {
  const seen: { at: number; signal: AbortSignal }[] = [];
  server.use(
    http[method](path, ({ request }) => {
      seen.push({ at: Date.now(), signal: request.signal });
      return never();
    }),
  );
  return seen;
}

function renderDashboard() {
  return renderWithProviders(
    <Routes>
      <Route path="/" element={<DashboardPage />} />
      <Route path="/transactions/:id" element={<div>TX DETAIL</div>} />
      <Route path="/accounts" element={<div>ACCOUNTS PAGE</div>} />
      <Route path="/history" element={<div>HISTORY PAGE</div>} />
    </Routes>,
    { routerEntries: ['/'] },
  );
}

function renderDetail(id = T1_DEPOSIT) {
  return renderWithProviders(
    <Routes>
      <Route path="/transactions/:id" element={<TransactionDetailPage />} />
    </Routes>,
    { routerEntries: [`/transactions/${id}`] },
  );
}

/** An outage that answers at once, telling the client to wait one second. */
const outage = ({ request }: { request: Request }) =>
  serviceUnavailable({
    via: 'api',
    retryAfterSeconds: 1,
    instance: new URL(request.url).pathname,
  });

describe('a slow read says so', () => {
  it('Accounts: silent before 5 s, then slow, then still trying with a way out, in a region of its own', async () => {
    const seen = holdAll('get', '*/api/accounts');
    installFakeClock();
    const start = Date.now();
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await waitFor(() => expect(seen).toHaveLength(1));
    expect(screen.getByLabelText('Loading accounts')).toBeInTheDocument();

    await advanceUntil(start, 4_900);
    expect(screen.queryByText(COPY.slow)).not.toBeInTheDocument();

    await advanceUntil(seen[0].at, 5_000);
    hintRegion(COPY.slow);

    await advanceUntil(seen[0].at, 20_000);
    const region = hintRegion(COPY.stillTrying);
    const stop = screen.getByRole('button', { name: COPY.stopWaiting });
    expect(region).not.toContainElement(stop);
    expectNoNestedLiveRegions();
  });

  it('Accounts: "Stop waiting" aborts the read, sends no retry, and lands on an announced bar with focus on Retry', async () => {
    const seen = holdAll('get', '*/api/accounts');
    installFakeClock();
    const user = fakeClockUser();
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await waitFor(() => expect(seen).toHaveLength(1));
    await advanceUntil(seen[0].at, 20_000);

    await user.click(screen.getByRole('button', { name: COPY.stopWaiting }));

    expect(seen[0].signal.aborted).toBe(true);
    const words = await screen.findByText('Could not load your accounts.', { exact: false });
    expect(words.closest('[role="alert"]')).not.toBeNull();
    const retry = screen.getByRole('button', { name: 'Retry' });
    await waitFor(() => expect(document.activeElement).toBe(retry));
    expect(screen.queryByText(COPY.loaded)).not.toBeInTheDocument();

    await advance(30_000);
    expect(seen).toHaveLength(1);
  });

  it('Accounts: Retry after a failure shows the wait again, and a second failure is announced again', async () => {
    let holdNext = false;
    let release: () => void = () => {};
    let calls = 0;
    server.use(
      http.get('*/api/accounts', async ({ request }) => {
        calls += 1;
        if (holdNext) {
          holdNext = false;
          await new Promise<void>((resolve) => (release = resolve));
        }
        return outage({ request });
      }),
    );
    installFakeClock();
    const user = fakeClockUser();
    try {
      renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
      await advance(5_000);
      await screen.findByRole('button', { name: 'Retry' });

      holdNext = true;
      const before = calls;
      const retriedAt = Date.now();
      await user.click(screen.getByRole('button', { name: 'Retry' }));
      await waitFor(() => expect(calls).toBe(before + 1));

      // The bar goes while the read runs again; the spinner and the hint come back, timed from
      // the Retry — nothing left over from the first wait may speak early.
      expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();
      expect(screen.getByLabelText('Loading accounts')).toBeInTheDocument();
      const shownAt = Date.now();
      await advanceUntil(retriedAt, 4_900);
      expect(screen.queryByText(COPY.slow)).not.toBeInTheDocument();
      await advanceUntil(shownAt, 5_000);
      hintRegion(COPY.slow);
      await advanceUntil(retriedAt, 19_900);
      hintRegion(COPY.slow);
      expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
      await advanceUntil(shownAt, 20_000);
      hintRegion(COPY.stillTrying);
      expect(screen.getByRole('button', { name: COPY.stopWaiting })).toBeInTheDocument();

      release();
      await advance(3_000);
      const alert = await screen.findByRole('alert');
      expect(alert).toHaveTextContent(COPY.unavailable);
      await waitFor(() =>
        expect(document.activeElement).toBe(within(alert).getByRole('button', { name: 'Retry' })),
      );
    } finally {
      release();
    }
  });

  it('Dashboard: one hint for its three reads; "Stop waiting" aborts all three and lands on the accounts bar', async () => {
    const accounts = holdAll('get', '*/api/accounts');
    const recent = holdAll('get', '*/api/transactions');
    const summary = holdAll('get', '*/api/transactions/summary');
    installFakeClock();
    const user = fakeClockUser();
    renderDashboard();
    await waitFor(() => {
      expect(accounts).toHaveLength(1);
      expect(recent).toHaveLength(1);
      expect(summary).toHaveLength(1);
    });
    const firstAt = Math.max(accounts[0].at, recent[0].at, summary[0].at);

    await advanceUntil(firstAt, 5_000);
    expect(screen.getAllByText(COPY.slow)).toHaveLength(1);
    hintRegion(COPY.slow);

    await advanceUntil(firstAt, 20_000);
    await user.click(screen.getByRole('button', { name: COPY.stopWaiting }));

    expect([accounts[0], recent[0], summary[0]].map(({ signal }) => signal.aborted)).toEqual([
      true,
      true,
      true,
    ]);
    const words = await screen.findByText('Could not load your accounts.', { exact: false });
    expect(words.closest('[role="alert"]')).not.toBeNull();
    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Retry' })),
    );
  });

  it("Dashboard: the accounts bar's Retry reloads every read that failed, not only the accounts", async () => {
    let down = true;
    const failWhileDown = ({ request }: { request: Request }) =>
      down ? outage({ request }) : undefined;
    server.use(
      http.get('*/api/accounts', failWhileDown),
      http.get('*/api/transactions', failWhileDown),
      http.get('*/api/transactions/summary', failWhileDown),
    );
    installFakeClock();
    const user = fakeClockUser();
    renderDashboard();
    await advance(5_000);
    await screen.findByRole('button', { name: 'Retry' });

    down = false;
    await user.click(screen.getByRole('button', { name: 'Retry' }));
    await advance(5_000);

    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('€2,080.50');
    await advance(5_000);
    expect(screen.queryByText(/Could not load this month/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Could not load recent activity/)).not.toBeInTheDocument();
  });

  it('History: "Stop waiting" on the first page lands on its announced bar', async () => {
    const seen = holdAll('get', '*/api/transactions');
    installFakeClock();
    const user = fakeClockUser();
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await waitFor(() => expect(seen).toHaveLength(1));

    await advanceUntil(seen[0].at, 5_000);
    hintRegion(COPY.slow);
    await advanceUntil(seen[0].at, 20_000);
    hintRegion(COPY.stillTrying);
    await user.click(screen.getByRole('button', { name: COPY.stopWaiting }));

    expect(seen[0].signal.aborted).toBe(true);
    const words = await screen.findByText('Could not load your transactions.', { exact: false });
    expect(words.closest('[role="alert"]')).not.toBeNull();
    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Retry' })),
    );
  });

  it('Transaction detail: "Stop waiting" aborts the read and lands on its announced bar', async () => {
    const seen = holdAll('get', '*/api/transactions/:id');
    installFakeClock();
    const user = fakeClockUser();
    renderDetail();
    await waitFor(() => expect(seen).toHaveLength(1));

    await advanceUntil(seen[0].at, 5_000);
    hintRegion(COPY.slow);
    await advanceUntil(seen[0].at, 20_000);
    hintRegion(COPY.stillTrying);
    await user.click(screen.getByRole('button', { name: COPY.stopWaiting }));

    expect(seen[0].signal.aborted).toBe(true);
    const words = await screen.findByText('Could not load the transaction.', { exact: false });
    expect(words.closest('[role="alert"]')).not.toBeNull();
    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Retry' })),
    );
  });

  it('History: "Load more" says it is slow, but offers no stop — the rows already shown stay', async () => {
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary');

    const nextPage: number[] = [];
    server.use(
      http.get('*/api/transactions', ({ request }) => {
        if (new URL(request.url).searchParams.get('Page') !== '2') return undefined;
        nextPage.push(Date.now());
        return never();
      }),
    );
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(nextPage).toHaveLength(1));

    await advanceUntil(nextPage[0], 5_000);
    expect(screen.getAllByText(COPY.slow)).toHaveLength(1);
    hintRegion(COPY.slow);
    await advanceUntil(nextPage[0], 20_000);
    hintRegion(COPY.stillTrying);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
    expect(screen.getByText('Salary')).toBeInTheDocument();
  });
});

describe('a failed read is announced', () => {
  /*
    Into an alert that was on the page, empty, before anything failed. A screen reader reads a
    CHANGE to a live region; an alert added to the page with its words already in it is, for
    several of them, no change at all.
  */

  /** Let every retry either policy could make run out before looking. */
  async function settle() {
    await advance(5_000);
  }

  it.each([
    {
      page: 'Accounts',
      path: '*/api/accounts',
      render: () => renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] }),
    },
    {
      page: 'History',
      path: '*/api/transactions',
      render: () => renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] }),
    },
    { page: 'Transaction detail', path: '*/api/transactions/:id', render: () => renderDetail() },
    { page: 'Dashboard, the accounts', path: '*/api/accounts', render: renderDashboard },
    {
      page: 'Send Money, the accounts',
      path: '*/api/accounts',
      render: () => renderWithProviders(<TransferPage />, { routerEntries: ['/transfer'] }),
    },
    {
      page: 'Move Money, the accounts',
      path: '*/api/accounts',
      render: () =>
        renderWithProviders(<InternalTransferPage />, { routerEntries: ['/transfer/internal'] }),
    },
  ])('$page: into the alert that was there, empty, before it failed', async ({ path, render }) => {
    server.use(http.get(path, outage));
    installFakeClock();
    render();
    const slot = alertSlot();
    expect(slot).toBeEmptyDOMElement();
    // Empty, it takes no room: an empty box in the page's column would still earn its gap.
    expect(getComputedStyle(slot).position).toBe('absolute');

    await settle();

    await waitFor(() => expect(slot).toHaveTextContent(COPY.unavailable));
    expect(alertSlot()).toBe(slot);
    expect(getComputedStyle(slot).position).not.toBe('absolute');
    expect(within(slot).getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expectNoNestedLiveRegions();
  });

  it('Dashboard: this month, with the accounts on screen', async () => {
    server.use(http.get('*/api/transactions/summary', outage));
    installFakeClock();
    renderDashboard();
    const slot = alertSlot();
    expect(slot).toBeEmptyDOMElement();
    await settle();

    await screen.findByRole('heading', { level: 1 });
    const words = await screen.findByText(/Could not load this month\./);
    expect(words.closest('[role="alert"]')).toBe(slot);
  });

  it('Dashboard: recent activity, with the accounts on screen', async () => {
    server.use(http.get('*/api/transactions', outage));
    installFakeClock();
    renderDashboard();
    const slot = alertSlot();
    expect(slot).toBeEmptyDOMElement();
    await settle();

    await screen.findByRole('heading', { level: 1 });
    const words = await screen.findByText(/Could not load recent activity\./);
    expect(words.closest('[role="alert"]')).toBe(slot);
  });

  it('Dashboard: this month and recent activity failing together are one alert, saying both', async () => {
    // Two alerts at once may be read as one: a screen reader may drop what is queued when an
    // assertive change arrives.
    server.use(
      http.get('*/api/transactions/summary', outage),
      http.get('*/api/transactions', outage),
    );
    installFakeClock();
    renderDashboard();
    await settle();

    await screen.findByRole('heading', { level: 1 });
    await screen.findByText(/Could not load recent activity\./);
    const slot = alertSlot();
    expect(slot).toHaveTextContent(`Could not load this month. ${COPY.unavailable}`);
    expect(slot).toHaveTextContent(`Could not load recent activity. ${COPY.unavailable}`);
    expect(slot.textContent).toMatch(/this month\..*recent activity\./);
    expectNoNestedLiveRegions();
  });
});

describe('a read that said it was slow says when it has loaded', () => {
  /*
    A visitor who heard "Taking longer than usual…" is owed the end of the story: the region that
    said it says "Loaded." when the content is there, without showing it, and then goes. A load
    inside 5 s said nothing, so it says nothing at the end either.
  */
  const reads = [
    {
      page: 'Accounts',
      path: '*/api/accounts',
      render: () => renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] }),
    },
    {
      page: 'History',
      path: '*/api/transactions',
      render: () => renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] }),
    },
    { page: 'Transaction detail', path: '*/api/transactions/:id', render: () => renderDetail() },
    { page: 'Dashboard', path: '*/api/accounts', render: renderDashboard },
    {
      page: 'Send Money',
      path: '*/api/accounts',
      render: () => renderWithProviders(<TransferPage />, { routerEntries: ['/transfer'] }),
    },
    {
      page: 'Move Money',
      path: '*/api/accounts',
      render: () =>
        renderWithProviders(<InternalTransferPage />, { routerEntries: ['/transfer/internal'] }),
    },
  ];

  /** Answers the read `path` with the mock's own data, `ms` after it arrives. */
  function answerAfter(path: string, ms: number) {
    const seen: number[] = [];
    server.use(
      http.get(path, async () => {
        seen.push(Date.now());
        await sleep(ms);
        return undefined;
      }),
    );
    return seen;
  }

  it.each(reads)(
    '$page: "Loaded." in the region that said it was slow, unseen, then nothing',
    async ({ path, render }) => {
      const seen = answerAfter(path, 6_000);
      installFakeClock();
      render();
      await waitFor(() => expect(seen).toHaveLength(1));
      await advanceUntil(seen[0], 5_000);
      const region = hintRegion(COPY.slow);

      await advanceUntil(seen[0], 6_100);

      await waitFor(() => expect(region).toHaveTextContent(COPY.loaded));
      expect(region).toBeInTheDocument();
      expect(region.textContent).toBe(COPY.loaded);
      expect(getComputedStyle(region.closest('[data-wait-hint]') as HTMLElement).position).toBe(
        'absolute',
      );
      expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
      // Nothing failed, so every alert on the page is there and empty: one on most pages, and on
      // Send Money also the recipient check's, under the handle the loaded form now shows.
      const alerts = Array.from(document.querySelectorAll('[role="alert"]'));
      expect(alerts.length).toBeGreaterThan(0);
      for (const alert of alerts) expect(alert).toBeEmptyDOMElement();

      await advance(LOADED_KEPT_MS);
      expect(region).not.toBeInTheDocument();
      expect(screen.queryByText(COPY.loaded)).not.toBeInTheDocument();
    },
  );

  it('Accounts: a load inside 5 s says nothing at its end either', async () => {
    const seen = answerAfter('*/api/accounts', 4_000);
    installFakeClock();
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await waitFor(() => expect(seen).toHaveLength(1));

    await advanceUntil(seen[0], 4_100);
    await screen.findByText('Main Account');

    expect(screen.queryAllByRole('status')).toHaveLength(0);
    expect(screen.queryByText(COPY.loaded)).not.toBeInTheDocument();
  });

  it('History: "Load more" that said it was slow says "Loaded." when the next page is there', async () => {
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await screen.findByText('Salary');

    const nextPage: number[] = [];
    server.use(
      http.get('*/api/transactions', async ({ request }) => {
        if (new URL(request.url).searchParams.get('Page') !== '2') return undefined;
        nextPage.push(Date.now());
        await sleep(6_000);
        return undefined;
      }),
    );
    installFakeClock();
    const user = fakeClockUser();
    await user.click(screen.getByRole('button', { name: 'Load more' }));
    await waitFor(() => expect(nextPage).toHaveLength(1));
    await advanceUntil(nextPage[0], 5_000);
    const region = hintRegion(COPY.slow);

    await advanceUntil(nextPage[0], 6_100);

    await waitFor(() => expect(region).toHaveTextContent(COPY.loaded));
    expect(region.textContent).toBe(COPY.loaded);
  });
});

describe('a Retry shows its wait, and nothing the page has not loaded', () => {
  const broken = () =>
    problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Something broke.' });

  /*
    A refetch after a failure keeps the old error and leaves `isLoading` false. A page that gated
    on those would keep its bar through the Retry, or show what an empty answer looks like: no
    accounts, no transactions, "Welcome to AzureBank", €0.00.
  */
  const pages = [
    {
      page: 'Accounts',
      path: '*/api/accounts',
      render: () => renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] }),
      spinner: 'Loading accounts',
      never: ['Add New Account', '€0.00'],
    },
    {
      page: 'History',
      path: '*/api/transactions',
      render: () => renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] }),
      spinner: 'Loading transactions',
      never: ['No Transactions'],
    },
    {
      page: 'Transaction detail',
      path: '*/api/transactions/:id',
      render: () => renderDetail(),
      spinner: 'Loading transaction',
      never: ['Transaction not found'],
    },
    {
      // The dashboard's `h1` is the balance, or the welcome: neither may stand in for the wait.
      page: 'Dashboard',
      path: '*/api/accounts',
      render: renderDashboard,
      spinner: undefined,
      never: ['Welcome to AzureBank', '€0.00'],
      noHeading: true,
    },
  ];

  for (const { page, path, render, spinner, never: absent, noHeading } of pages) {
    it(`${page}: while a Retry waits, the bar is gone and only the wait is on screen`, async () => {
      let hold = false;
      const seen: number[] = [];
      server.use(
        http.get(path, () => {
          seen.push(Date.now());
          return hold ? never() : broken();
        }),
      );
      installFakeClock();
      const user = fakeClockUser();
      render();
      const bar = (await screen.findByText(/Something broke\./)).closest<HTMLElement>(
        '[role="alert"]',
      );
      expect(bar).not.toBeNull();

      hold = true;
      await user.click(within(bar as HTMLElement).getByRole('button', { name: 'Retry' }));
      await waitFor(() => expect(seen).toHaveLength(2));
      // The alert stays, emptied, so that a second failure is a change it reads again.
      await waitFor(() => expect(alertSlot()).toBeEmptyDOMElement());

      expect(document.querySelector('[data-wait-hint]')).not.toBeNull();
      if (spinner) expect(screen.getByLabelText(spinner)).toBeInTheDocument();
      for (const words of absent) expect(screen.queryByText(words)).not.toBeInTheDocument();
      if (noHeading) expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument();
      const shownAt = Date.now();
      await advanceUntil(shownAt, 5_000);
      hintRegion(COPY.slow);
    });
  }
});

describe('a Retry that fails again before its wait is drawn is announced again', () => {
  /*
    A 500 is not retried and the mock answers it at once, so the Retry's start and its failure
    can reach the page in the same frame: the page goes from one failure to the next without ever
    drawing the wait, and a bar that stayed the same element would say nothing the second time.
    Retry is pressed twice, because a press that also changes the page's own state draws the
    wait at once, and the second press of the same Retry may change nothing.
  */
  const broken = () =>
    problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Something broke.' });

  const bars = [
    {
      page: 'Accounts',
      path: '*/api/accounts',
      render: () => renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] }),
      words: /Something broke\./,
    },
    {
      page: 'History',
      path: '*/api/transactions',
      render: () => renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] }),
      words: /Something broke\./,
    },
    {
      page: 'Transaction detail',
      path: '*/api/transactions/:id',
      render: () => renderDetail(),
      words: /Something broke\./,
    },
    {
      page: 'Dashboard, the accounts',
      path: '*/api/accounts',
      render: renderDashboard,
      words: /Something broke\./,
    },
    {
      page: 'Dashboard, this month',
      path: '*/api/transactions/summary',
      render: renderDashboard,
      words: /Could not load this month\./,
    },
    {
      page: 'Dashboard, recent activity',
      path: '*/api/transactions',
      render: renderDashboard,
      words: /Could not load recent activity\./,
    },
  ];

  /**
   * The bar that says `words`, inside the page's alert. A new failure puts a new bar into the same
   * alert, and that change is what a screen reader reads.
   */
  const barOf = (words: RegExp) => {
    const bar = screen.getByText(words).closest<HTMLElement>('[role="group"]');
    expect(bar?.closest('[role="alert"]')).toBe(alertSlot());
    return bar;
  };

  for (const { page, path, render, words } of bars) {
    it(`${page}: every new failure is a new bar in the page's alert, with focus on its Retry`, async () => {
      let calls = 0;
      server.use(
        http.get(path, () => {
          calls += 1;
          return broken();
        }),
      );
      const user = userEvent.setup();
      render();
      const alertOf = () => barOf(words);
      await screen.findByText(words);

      for (let press = 1; press <= 2; press += 1) {
        const shown = alertOf();
        expect(shown).not.toBeNull();
        const before = calls;
        await user.click(within(shown as HTMLElement).getByRole('button', { name: 'Retry' }));
        await waitFor(() => expect(calls).toBe(before + 1));

        await waitFor(() => {
          expect(alertOf()).not.toBeNull();
          expect(alertOf()).not.toBe(shown);
        });
        const again = alertOf() as HTMLElement;
        await waitFor(() =>
          expect(document.activeElement).toBe(within(again).getByRole('button', { name: 'Retry' })),
        );
      }
    });
  }

  it('Dashboard: a section still loading behind the accounts bar does not hold back its landing', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/accounts', () => {
        calls += 1;
        return broken();
      }),
      http.get('*/api/transactions/summary', never),
    );
    const user = userEvent.setup();
    renderDashboard();
    const alertOf = () => barOf(/Something broke\./);
    await screen.findByText(/Something broke\./);
    const first = alertOf();

    await user.click(within(first as HTMLElement).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(calls).toBe(2));

    await waitFor(() => expect(alertOf()).not.toBe(first));
    const again = alertOf() as HTMLElement;
    await waitFor(() =>
      expect(document.activeElement).toBe(within(again).getByRole('button', { name: 'Retry' })),
    );
  });

  it("Dashboard: a section's Retry that fails again lands on that section, though the one above it failed too", async () => {
    server.use(
      http.get('*/api/transactions/summary', broken),
      http.get('*/api/transactions', broken),
    );
    const user = userEvent.setup();
    renderDashboard();
    const recentBar = () => barOf(/Could not load recent activity\./);
    await screen.findByText(/Could not load this month\./);
    const first = recentBar();
    expect(first).not.toBeNull();

    await user.click(within(first as HTMLElement).getByRole('button', { name: 'Retry' }));

    await waitFor(() => expect(recentBar()).not.toBe(first));
    const again = recentBar() as HTMLElement;
    await waitFor(() =>
      expect(document.activeElement).toBe(within(again).getByRole('button', { name: 'Retry' })),
    );
  });
});

describe('a Retry that fails after the visitor has moved on leaves them where they are', () => {
  /*
    Focus goes back to Retry because the button that was pressed has gone and focus fell to the
    page. A visitor who went on during the wait, into a dialog the page still offers, keeps their
    place: the new bar is an alert and says what happened, but it takes nothing from under their
    typing.
  */
  const broken = () =>
    problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'Something broke.' });

  /** Answers every request with a failure at once, except the second, held until released. */
  function holdTheRetry(path: string) {
    const held = { calls: 0, release: () => {} };
    server.use(
      http.get(path, async () => {
        held.calls += 1;
        if (held.calls === 2) await new Promise<void>((resolve) => (held.release = resolve));
        return broken();
      }),
    );
    return held;
  }

  /** Long enough for the page to have done whatever it does with the failure. */
  const settle = () => act(() => new Promise<void>((resolve) => setTimeout(resolve, 50)));

  it('Accounts: typing in "Add account" while a Retry waits goes on where it was when it fails', async () => {
    const held = holdTheRetry('*/api/accounts');
    const user = userEvent.setup();
    try {
      renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
      await user.click(await screen.findByRole('button', { name: 'Retry' }));
      await waitFor(() => expect(held.calls).toBe(2));
      await screen.findByLabelText('Loading accounts');

      await user.click(screen.getByRole('button', { name: 'Add account' }));
      const dialog = await screen.findByRole('dialog');
      const name = within(dialog).getByRole('textbox', { name: 'Account name' });
      await user.type(name, 'Hol');
      expect(document.activeElement).toBe(name);

      held.release();
      const words = await screen.findByText(/Something broke\./);
      expect(words.closest('[role="alert"]')).not.toBeNull();
      await settle();

      expect(document.activeElement).toBe(name);
      await user.keyboard('iday');
      expect(name).toHaveValue('Holiday');
      expect(dialog).toBeInTheDocument();
    } finally {
      held.release();
    }
  });

  it("Dashboard: a dialog opened while a section's Retry waits keeps focus when that Retry fails", async () => {
    const held = holdTheRetry('*/api/transactions/summary');
    const user = userEvent.setup();
    try {
      renderDashboard();
      const bar = (await screen.findByText(/Could not load this month\./)).closest<HTMLElement>(
        '[role="alert"]',
      );
      await user.click(within(bar as HTMLElement).getByRole('button', { name: 'Retry' }));
      await waitFor(() => expect(held.calls).toBe(2));
      await waitFor(() =>
        expect(screen.queryByText(/Could not load this month\./)).not.toBeInTheDocument(),
      );

      await user.click(screen.getByRole('button', { name: /Deposit/ }));
      const dialog = await screen.findByRole('dialog');
      const inDialog = document.activeElement as HTMLElement;
      expect(dialog).toContainElement(inDialog);

      held.release();
      const words = await screen.findByText(/Could not load this month\./);
      expect(words.closest('[role="alert"]')).not.toBeNull();
      await settle();

      expect(document.activeElement).toBe(inDialog);
    } finally {
      held.release();
    }
  });
});

describe("the Dashboard's section bars name the section and the outage", () => {
  it('this month and recent activity each say which section failed, and that the service is down', async () => {
    server.use(
      http.get('*/api/transactions/summary', outage),
      http.get('*/api/transactions', outage),
    );
    installFakeClock();
    renderDashboard();
    await advance(5_000);

    await screen.findByRole('heading', { level: 1 });
    expect(await screen.findByText(/Could not load this month\./)).toHaveTextContent(
      `Could not load this month. ${COPY.unavailable}`,
    );
    expect(screen.getByText(/Could not load recent activity\./)).toHaveTextContent(
      `Could not load recent activity. ${COPY.unavailable}`,
    );
  });

  it('a 500 keeps the section sentence alone', async () => {
    const broken = () => problem({ status: 500, errorCode: 'INTERNAL_ERROR', detail: 'boom' });
    server.use(
      http.get('*/api/transactions/summary', broken),
      http.get('*/api/transactions', broken),
    );
    renderDashboard();

    await screen.findByRole('heading', { level: 1 });
    expect((await screen.findByText(/Could not load this month\./)).textContent).toBe(
      'Could not load this month.',
    );
    expect(screen.getByText(/Could not load recent activity\./).textContent).toBe(
      'Could not load recent activity.',
    );
  });
});
