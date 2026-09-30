import { Route, Routes } from 'react-router-dom';
import { cleanup, screen, waitFor, within } from '@testing-library/react';
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
  fakeClockUser,
  hintRegion,
  never,
  installFakeClock,
} from '../test/outage';
import { AccountsPage } from './AccountsPage';
import { DashboardPage } from './DashboardPage';
import { HistoryPage } from './HistoryPage';
import { TransactionDetailPage } from './TransactionDetailPage';

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
 * the request reached the server — so neither can pass by the test being slow.
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

    await advance(30_000);
    expect(seen).toHaveLength(1);
  });

  it('Accounts: Retry after a failure shows the wait again, and a second failure is announced again', async () => {
    let holdNext = false;
    let release: () => void = () => {};
    let calls = 0;
    let heldAt = 0;
    server.use(
      http.get('*/api/accounts', async ({ request }) => {
        calls += 1;
        if (holdNext) {
          holdNext = false;
          heldAt = Date.now();
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
      await advanceUntil(retriedAt, 4_900);
      expect(screen.queryByText(COPY.slow)).not.toBeInTheDocument();
      await advanceUntil(heldAt, 5_000);
      hintRegion(COPY.slow);
      await advanceUntil(retriedAt, 19_900);
      hintRegion(COPY.slow);
      expect(screen.queryByRole('button', { name: COPY.stopWaiting })).not.toBeInTheDocument();
      await advanceUntil(heldAt, 20_000);
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
  /** Let every retry either policy could make run out before looking. */
  async function settle() {
    await advance(5_000);
  }

  it('Accounts', async () => {
    server.use(http.get('*/api/accounts', outage));
    installFakeClock();
    renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
    await settle();

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });

  it('History', async () => {
    server.use(http.get('*/api/transactions', outage));
    installFakeClock();
    renderWithProviders(<HistoryPage />, { routerEntries: ['/history'] });
    await settle();

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });

  it('Transaction detail', async () => {
    server.use(http.get('*/api/transactions/:id', outage));
    installFakeClock();
    renderDetail();
    await settle();

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });

  it('Dashboard: the accounts', async () => {
    server.use(http.get('*/api/accounts', outage));
    installFakeClock();
    renderDashboard();
    await settle();

    const alert = await screen.findByRole('alert');
    expect(within(alert).getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  });

  it('Dashboard: this month, with the accounts on screen', async () => {
    server.use(http.get('*/api/transactions/summary', outage));
    installFakeClock();
    renderDashboard();
    await settle();

    await screen.findByRole('heading', { level: 1 });
    const words = await screen.findByText(/Could not load this month\./);
    expect(words.closest('[role="alert"]')).not.toBeNull();
  });

  it('Dashboard: recent activity, with the accounts on screen', async () => {
    server.use(http.get('*/api/transactions', outage));
    installFakeClock();
    renderDashboard();
    await settle();

    await screen.findByRole('heading', { level: 1 });
    const words = await screen.findByText(/Could not load recent activity\./);
    expect(words.closest('[role="alert"]')).not.toBeNull();
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
