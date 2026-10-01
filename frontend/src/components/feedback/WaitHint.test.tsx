import { cleanup, renderHook, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Button } from '@fluentui/react-components';
import { http } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { makeTestStore, renderWithProviders } from '../../test/renderWithProviders';
import { COPY, advance, expectSilentHintTakesNoRoom, never } from '../../test/outage';
import { apiSlice } from '../../features/api/apiSlice';
import {
  LOADED_KEPT_MS,
  SLOW_AFTER_MS,
  STILL_TRYING_AFTER_MS,
  readWait,
  useWaitPhase,
} from '../../hooks/useWaitPhase';
import { abortRunning, useWaitLanding } from '../../hooks/useWaitLanding';
import { WaitHint } from './WaitHint';

/**
 * The one mechanism behind every "this is taking a while" on the site.
 *
 * A wait says nothing for 5 s, then "Taking longer than usual…", then from 20 s "Still trying…" —
 * in a polite region of its own that exists, empty, from the start of the wait (a region must be
 * in the page before its text changes, or nothing is read), and that is gone the moment nothing
 * is pending. A read may offer "Stop waiting"; a write never does, because stopping a write the
 * server may already be doing is not the visitor's to decide.
 *
 * The clock here is fully fake — nothing below awaits a response except `abortRunning`'s tests,
 * which use the real one — so each threshold is asserted to the millisecond.
 */

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('useWaitPhase', () => {
  it('waits 5 s for the first word, then 20 s for the second', () => {
    expect(SLOW_AFTER_MS).toBe(5_000);
    expect(STILL_TRYING_AFTER_MS).toBe(20_000);
  });

  it('is silent until 5 s, slow until 20 s, then still trying', async () => {
    vi.useFakeTimers();
    const { result } = renderHook(({ active }) => useWaitPhase(active), {
      initialProps: { active: true },
    });

    expect(result.current).toBe('none');
    await advance(SLOW_AFTER_MS - 1);
    expect(result.current).toBe('none');
    await advance(1);
    expect(result.current).toBe('slow');
    await advance(STILL_TRYING_AFTER_MS - SLOW_AFTER_MS - 1);
    expect(result.current).toBe('slow');
    await advance(1);
    expect(result.current).toBe('stillTrying');
  });

  it('is over once the wait ends, for a while, and a new wait starts again from zero', async () => {
    vi.useFakeTimers();
    const { result, rerender } = renderHook(({ active }) => useWaitPhase(active), {
      initialProps: { active: true },
    });
    await advance(STILL_TRYING_AFTER_MS);
    expect(result.current).toBe('stillTrying');

    rerender({ active: false });
    expect(result.current).toBe('ended');
    await advance(LOADED_KEPT_MS - 1);
    expect(result.current).toBe('ended');
    await advance(1);
    expect(result.current).toBe('none');

    rerender({ active: true });
    expect(result.current).toBe('none');
    await advance(SLOW_AFTER_MS - 1);
    expect(result.current).toBe('none');
    await advance(1);
    expect(result.current).toBe('slow');
  });

  it('a wait that ends before it said anything is simply over', async () => {
    vi.useFakeTimers();
    const { result, rerender } = renderHook(({ active }) => useWaitPhase(active), {
      initialProps: { active: true },
    });
    await advance(SLOW_AFTER_MS - 1);

    rerender({ active: false });
    expect(result.current).toBe('none');
  });

  it('is over in the very render its wait ends, not one render later', async () => {
    vi.useFakeTimers();
    const renders: Array<{ active: boolean; phase: string }> = [];
    const { rerender } = renderHook(
      ({ active }) => {
        const phase = useWaitPhase(active);
        renders.push({ active, phase });
        return phase;
      },
      { initialProps: { active: true } },
    );
    await advance(STILL_TRYING_AFTER_MS);
    expect(renders.at(-1)).toEqual({ active: true, phase: 'stillTrying' });

    rerender({ active: false });

    expect(renders.filter(({ active }) => !active).length).toBeGreaterThan(0);
    expect(
      renders.filter(({ active, phase }) => !active && phase !== 'ended' && phase !== 'none'),
    ).toEqual([]);
    expect(renders.at(-1)).toEqual({ active: false, phase: 'ended' });
  });

  it("a wait that ends early is timed afresh when the next one starts: the first wait's timers never fire into it", async () => {
    vi.useFakeTimers();
    const { result, rerender } = renderHook(({ active }) => useWaitPhase(active), {
      initialProps: { active: true },
    });
    // The first wait ends at 3 s, before either threshold; the next starts at 3.5 s.
    await advance(3_000);
    rerender({ active: false });
    await advance(500);
    rerender({ active: true });

    // 5 s after the FIRST start: a timer left from it would speak here.
    await advance(SLOW_AFTER_MS - 3_500);
    expect(result.current).toBe('none');
    await advance(3_500 - 1);
    expect(result.current).toBe('none');
    await advance(1);
    expect(result.current).toBe('slow');

    // 20 s after the first start, 16.5 s into this wait: still only slow.
    await advance(STILL_TRYING_AFTER_MS - SLOW_AFTER_MS - 3_500);
    expect(result.current).toBe('slow');
    await advance(3_500 - 1);
    expect(result.current).toBe('slow');
    await advance(1);
    expect(result.current).toBe('stillTrying');
  });
});

describe('readWait', () => {
  const error = { status: 503, errorCode: 'SERVICE_UNAVAILABLE' };

  it.each([
    ['the first load', { isLoading: true, isFetching: true }, { waiting: true, failed: false }],
    [
      'a refetch after an error',
      { isLoading: false, isFetching: true, error },
      { waiting: true, failed: false },
    ],
    [
      'a refetch with data on screen',
      { isLoading: false, isFetching: true },
      { waiting: false, failed: false },
    ],
    ['a failure', { isLoading: false, isFetching: false, error }, { waiting: false, failed: true }],
    ['a success', { isLoading: false, isFetching: false }, { waiting: false, failed: false }],
  ])('%s', (_name, query, expected) => {
    expect(readWait(query)).toEqual(expected);
  });
});

describe('WaitHint', () => {
  /** A small and a medium Fluent button beside the hint, to tell the sizes apart by class. */
  function renderHint(props: Parameters<typeof WaitHint>[0]) {
    return renderWithProviders(
      <>
        <WaitHint {...props} />
        <Button size="small">reference small</Button>
        <Button size="medium">reference medium</Button>
      </>,
    );
  }

  it('renders nothing while nothing is pending', () => {
    renderHint({ active: false, kind: 'read', onStopWaiting: vi.fn() });

    expect(document.querySelector('[data-wait-hint]')).toBeNull();
    expect(screen.queryAllByRole('status')).toHaveLength(0);
  });

  it('mounts its region empty at the start of the wait, and speaks at 5 s and 20 s', async () => {
    vi.useFakeTimers();
    renderHint({ active: true, kind: 'write' });

    const region = screen.getByRole('status');
    expect(region.closest('[data-wait-hint]')).not.toBeNull();
    expect(region.textContent).toBe('');

    await advance(SLOW_AFTER_MS);
    expect(region).toHaveTextContent(COPY.slow);
    expect(region.textContent).toBe(COPY.slow);

    await advance(STILL_TRYING_AFTER_MS - SLOW_AFTER_MS);
    expect(region.textContent).toBe(COPY.stillTrying);
  });

  it('takes no room while it is silent, and makes room only once it has words', async () => {
    vi.useFakeTimers();
    renderHint({ active: true, kind: 'read', onStopWaiting: vi.fn() });

    expectSilentHintTakesNoRoom();
    const hint = document.querySelector('[data-wait-hint]') as HTMLElement;

    await advance(SLOW_AFTER_MS);
    expect(hint).toHaveTextContent(COPY.slow);
    expect(getComputedStyle(hint).position).not.toBe('absolute');

    await advance(STILL_TRYING_AFTER_MS - SLOW_AFTER_MS);
    expect(screen.getByRole('button', { name: COPY.stopWaiting })).toBeInTheDocument();
    expect(getComputedStyle(hint).position).not.toBe('absolute');
  });

  it('offers "Stop waiting" on a read at 20 s: after the words, outside them, medium-sized', async () => {
    vi.useFakeTimers();
    const onStopWaiting = vi.fn();
    renderHint({ active: true, kind: 'read', onStopWaiting });

    await advance(SLOW_AFTER_MS);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).toBeNull();

    await advance(STILL_TRYING_AFTER_MS - SLOW_AFTER_MS);
    const region = screen.getByRole('status');
    const stop = screen.getByRole('button', { name: COPY.stopWaiting });
    expect(region).not.toContainElement(stop);
    expect(region.compareDocumentPosition(stop) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(stop.closest('[data-wait-hint]')).not.toBeNull();

    // Fluent sizes a button by class; a medium one carries none of the small one's extras.
    const small = screen.getByRole('button', { name: 'reference small' }).classList;
    const medium = screen.getByRole('button', { name: 'reference medium' }).classList;
    const smallOnly = [...small].filter((name) => !medium.contains(name));
    expect(smallOnly.length).toBeGreaterThan(0);
    expect([...stop.classList].filter((name) => smallOnly.includes(name))).toEqual([]);

    vi.useRealTimers();
    await userEvent.click(stop);
    expect(onStopWaiting).toHaveBeenCalledTimes(1);
  });

  it('never offers "Stop waiting" on a write, nor on a read that has nothing to stop', async () => {
    vi.useFakeTimers();
    renderHint({ active: true, kind: 'write', onStopWaiting: vi.fn() });
    renderHint({ active: true, kind: 'read' });

    await advance(STILL_TRYING_AFTER_MS);
    expect(screen.getAllByText(COPY.stillTrying)).toHaveLength(2);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).toBeNull();
  });

  it('its region is atomic: a change of words is read whole', () => {
    renderHint({ active: true, kind: 'read' });

    expect(screen.getByRole('status')).toHaveAttribute('aria-atomic', 'true');
  });

  it('a money send asks, from 20 s, to keep the page open; other writes and reads do not', async () => {
    vi.useFakeTimers();
    renderHint({ active: true, kind: 'moneySend' });
    renderHint({ active: true, kind: 'write' });
    renderHint({ active: true, kind: 'read' });

    await advance(SLOW_AFTER_MS);
    expect(screen.getAllByText(COPY.slow)).toHaveLength(3);

    await advance(STILL_TRYING_AFTER_MS - SLOW_AFTER_MS);
    const regions = screen.getAllByRole('status').map((region) => region.textContent);
    expect(regions).toEqual([COPY.keepPageOpen, COPY.stillTrying, COPY.stillTrying]);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).toBeNull();
  });
});

describe('WaitHint: the end of a wait', () => {
  /** A hint whose props the test changes, as its host would from one render to the next. */
  function renderChanging(props: Parameters<typeof WaitHint>[0]) {
    const utils = renderWithProviders(<WaitHint {...props} />);
    return (next: Parameters<typeof WaitHint>[0]) => utils.rerender(<WaitHint {...next} />);
  }

  it('a read that said something and then loads says "Loaded." in the same region, unseen, then goes', async () => {
    vi.useFakeTimers();
    const update = renderChanging({ active: true, kind: 'read', failed: false });
    await advance(STILL_TRYING_AFTER_MS);
    const region = screen.getByRole('status');
    expect(region.textContent).toBe(COPY.stillTrying);

    update({ active: false, kind: 'read', failed: false });

    // The same element: a new region filled at once is not a change a screen reader reads.
    expect(screen.getByRole('status')).toBe(region);
    expect(region.textContent).toBe(COPY.loaded);
    expect(screen.queryByRole('button', { name: COPY.stopWaiting })).toBeNull();
    const hint = document.querySelector('[data-wait-hint]') as HTMLElement;
    expect(getComputedStyle(hint).position).toBe('absolute');

    await advance(LOADED_KEPT_MS - 1);
    expect(screen.getByRole('status')).toBe(region);
    await advance(1);
    expect(screen.queryByRole('status')).toBeNull();
    expect(document.querySelector('[data-wait-hint]')).toBeNull();
  });

  it('a read that loads before it said anything stays quiet', async () => {
    vi.useFakeTimers();
    const update = renderChanging({ active: true, kind: 'read', failed: false });
    await advance(SLOW_AFTER_MS - 1);

    update({ active: false, kind: 'read', failed: false });

    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.queryByText(COPY.loaded)).toBeNull();
  });

  it.each([
    ['a read that failed: its alert speaks', { kind: 'read', failed: true }],
    ['a read whose host says nothing about its outcome', { kind: 'read' }],
    ['a write, which ends on its own outcome', { kind: 'write' }],
    ['a money send, which ends on its own outcome', { kind: 'moneySend' }],
  ] as const)('%s says nothing more when its wait ends', async (_name, props) => {
    vi.useFakeTimers();
    const update = renderChanging({ active: true, ...props });
    await advance(SLOW_AFTER_MS);
    expect(screen.getByRole('status').textContent).toBe(COPY.slow);

    update({ active: false, ...props });

    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.queryByText(COPY.loaded)).toBeNull();
  });

  it('a new wait that starts while "Loaded." is up starts from silence, and a fast end stays quiet', async () => {
    vi.useFakeTimers();
    const update = renderChanging({ active: true, kind: 'read', failed: false });
    await advance(SLOW_AFTER_MS);
    update({ active: false, kind: 'read', failed: false });
    expect(screen.getByRole('status').textContent).toBe(COPY.loaded);

    update({ active: true, kind: 'read', failed: false });
    expect(screen.getByRole('status').textContent).toBe('');
    await advance(SLOW_AFTER_MS - 1);
    update({ active: false, kind: 'read', failed: false });

    expect(screen.queryByRole('status')).toBeNull();
  });
});

describe('useWaitLanding', () => {
  /**
   * A host at one instant: whether its read is waiting, which request its read is on (a read gets
   * a new id each time it sends one), and which control, if any, the wait would land on. As on the
   * pages, the controls that arm it go away once pressed: "Stop" is there only while the wait is,
   * and the landing is the bar's Retry, gone while its read runs again, so focus falls to the page.
   * "Elsewhere" is anywhere else the visitor may go, such as a field in a dialog.
   */
  function Landing({
    waiting,
    request,
    landing,
  }: {
    waiting: boolean;
    request: string | undefined | readonly string[];
    landing?: string;
  }) {
    const { landingRef, arm } = useWaitLanding(waiting, request);
    return (
      <>
        {waiting && (
          <button type="button" onClick={arm}>
            Stop
          </button>
        )}
        <button type="button">Elsewhere</button>
        {landing && (
          <button
            key={landing}
            type="button"
            onClick={arm}
            ref={(el) => {
              landingRef.current = el;
            }}
          >
            {landing}
          </button>
        )}
      </>
    );
  }

  const button = (name: string) => screen.getByRole('button', { name });
  const elsewhere = () => button('Elsewhere');

  it('moves focus to the landing when a stopped wait ends in one', async () => {
    const { rerender } = renderWithProviders(<Landing waiting request="r1" />);
    await userEvent.click(button('Stop'));

    // The stopped request is rejected: same request, now at rest, with its bar. Stop is gone.
    rerender(<Landing waiting={false} request="r1" landing="Retry" />);

    await waitFor(() => expect(document.activeElement).toBe(button('Retry')));
  });

  it('leaves focus alone when the wait was not armed', () => {
    const { rerender } = renderWithProviders(<Landing waiting request="r1" />);
    expect(document.activeElement).toBe(document.body);

    rerender(<Landing waiting={false} request="r1" landing="Retry" />);

    expect(document.activeElement).toBe(document.body);
  });

  it('disarms when an armed wait ends without a landing, so a later one takes nothing', async () => {
    const { rerender } = renderWithProviders(<Landing waiting request="r1" />);
    await userEvent.click(button('Stop'));

    // Ends in success: nothing to land on. Stop is gone, and focus with it.
    rerender(<Landing waiting={false} request="r1" />);
    expect(document.activeElement).toBe(document.body);
    // A later wait, never armed, ends in a failure.
    rerender(<Landing waiting request="r2" />);
    rerender(<Landing waiting={false} request="r2" landing="Retry" />);

    expect(document.activeElement).toBe(document.body);
  });

  it('a Retry pressed while its bar is on screen stays armed until its wait ends, then lands', async () => {
    // The first load: no request on the very first render, then one, which fails.
    const { rerender } = renderWithProviders(<Landing waiting request={undefined} />);
    rerender(<Landing waiting request="r1" />);
    rerender(<Landing waiting={false} request="r1" landing="Retry" />);
    await userEvent.click(button('Retry'));

    // A render before the read's new request reaches the page: the old bar is still there.
    rerender(<Landing waiting={false} request="r1" landing="Retry" />);
    // The wait: the bar hides while the read runs again, and focus falls to the page.
    rerender(<Landing waiting request="r2" />);
    expect(document.activeElement).toBe(document.body);
    // It fails again: a new bar.
    rerender(<Landing waiting={false} request="r2" landing="Retry" />);

    await waitFor(() => expect(document.activeElement).toBe(button('Retry')));
  });

  it('a Retry whose read answers before its wait is ever shown takes nothing later', async () => {
    const { rerender } = renderWithProviders(
      <Landing waiting={false} request="r1" landing="Retry" />,
    );
    await userEvent.click(button('Retry'));

    // The read succeeded inside one render: the bar is gone, and no wait was ever on screen.
    rerender(<Landing waiting={false} request="r2" />);
    expect(document.activeElement).toBe(document.body);
    // Later, a wait the visitor did not start fails.
    rerender(<Landing waiting request="r3" />);
    rerender(<Landing waiting={false} request="r3" landing="Retry" />);

    expect(document.activeElement).toBe(document.body);
  });

  it('a Retry that fails again before its wait is ever shown takes nothing later', async () => {
    const { rerender } = renderWithProviders(
      <Landing waiting={false} request="r1" landing="Retry" />,
    );
    await userEvent.click(button('Retry'));

    // The read failed again inside one render: a bar, as before, and no wait was ever on screen.
    rerender(<Landing waiting={false} request="r2" landing="Retry" />);
    // Later, a refetch the visitor did not start (a change elsewhere reloads the read) hides the
    // bar, so focus falls to the page, and then fails.
    rerender(<Landing waiting request="r3" />);
    expect(document.activeElement).toBe(document.body);
    rerender(<Landing waiting={false} request="r3" landing="Retry" />);

    expect(document.activeElement).toBe(document.body);
  });

  it('on a page of several reads, a Retry whose read answers at once takes nothing later, though another bar is still up', async () => {
    const { rerender } = renderWithProviders(
      <Landing waiting={false} request={['a1', 'm1', 'r1']} landing="Retry this month" />,
    );
    await userEvent.click(button('Retry this month'));

    // "This month" loaded inside one render; "recent activity" is now the first failed bar, and
    // the focus lost with the pressed bar goes to it.
    rerender(
      <Landing waiting={false} request={['a1', 'm2', 'r1']} landing="Retry recent activity" />,
    );
    await waitFor(() => expect(document.activeElement).toBe(button('Retry recent activity')));
    // Later, a deposit reloads "recent activity", and it fails again after a wait.
    rerender(<Landing waiting request={['a1', 'm2', 'r2']} />);
    expect(document.activeElement).toBe(document.body);
    rerender(
      <Landing waiting={false} request={['a1', 'm2', 'r2']} landing="Retry recent activity" />,
    );

    expect(document.activeElement).toBe(document.body);
  });

  it('a Retry that ends before its wait is ever shown lands as one that ended on screen does', async () => {
    const { rerender } = renderWithProviders(
      <Landing waiting={false} request="r1" landing="Retry" />,
    );
    await userEvent.click(button('Retry'));

    // It failed again inside one render, and the page drew a new bar for the new failure.
    rerender(<Landing waiting={false} request="r2" landing="Retry again" />);

    await waitFor(() => expect(document.activeElement).toBe(button('Retry again')));
  });

  it('takes nothing from a visitor who went elsewhere while the wait ran, such as into a dialog', async () => {
    const { rerender } = renderWithProviders(
      <Landing waiting={false} request="r1" landing="Retry" />,
    );
    await userEvent.click(button('Retry'));
    // The bar hides while the read runs again, and the visitor goes on, into a dialog.
    rerender(<Landing waiting request="r2" />);
    elsewhere().focus();

    // The read fails again: a new bar, but focus was never lost, so it stays where the visitor is.
    rerender(<Landing waiting={false} request="r2" landing="Retry" />);

    expect(document.activeElement).toBe(elsewhere());
  });

  it('spends the arm even when it did not land, so focus lost later is not taken by another wait', async () => {
    const { rerender } = renderWithProviders(
      <Landing waiting={false} request="r1" landing="Retry" />,
    );
    await userEvent.click(button('Retry'));
    rerender(<Landing waiting request="r2" />);
    elsewhere().focus();
    rerender(<Landing waiting={false} request="r2" landing="Retry" />);

    // Later focus falls to the page, and a refetch the visitor did not start fails.
    elsewhere().blur();
    rerender(<Landing waiting request="r3" />);
    rerender(<Landing waiting={false} request="r3" landing="Retry" />);

    expect(document.activeElement).toBe(document.body);
  });
});

describe('abortRunning', () => {
  it('aborts the running read for a cache key, and says whether there was one', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/accounts', () => {
        calls += 1;
        return never();
      }),
    );
    const store = makeTestStore();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    await waitFor(() => expect(calls).toBe(1));

    expect(abortRunning(store.dispatch, [{ endpoint: 'getAccounts', arg: undefined }])).toBe(true);
    await expect(query.unwrap()).rejects.toMatchObject({ name: 'AbortError' });

    expect(abortRunning(store.dispatch, [{ endpoint: 'getAccounts', arg: undefined }])).toBe(false);
    query.unsubscribe();
  });

  it('stops every read it is given, not only the first one running', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/accounts', () => {
        calls += 1;
        return never();
      }),
      http.get('*/api/transactions/:id', () => {
        calls += 1;
        return never();
      }),
    );
    const store = makeTestStore();
    const accounts = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const transaction = store.dispatch(apiSlice.endpoints.getTransaction.initiate('tx-1'));
    await waitFor(() => expect(calls).toBe(2));

    expect(
      abortRunning(store.dispatch, [
        { endpoint: 'getAccounts', arg: undefined },
        { endpoint: 'getTransaction', arg: 'tx-1' },
      ]),
    ).toBe(true);
    await expect(accounts.unwrap()).rejects.toMatchObject({ name: 'AbortError' });
    await expect(transaction.unwrap()).rejects.toMatchObject({ name: 'AbortError' });

    accounts.unsubscribe();
    transaction.unsubscribe();
  });

  it('stops a read the page kept, such as a lazy trigger result, and says whether it was running', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/users/:tag', () => {
        calls += 1;
        return never();
      }),
    );
    const store = makeTestStore();
    const kept = store.dispatch(apiSlice.endpoints.lookupRecipient.initiate('ana'));
    await waitFor(() => expect(calls).toBe(1));

    expect(abortRunning(store.dispatch, [kept])).toBe(true);
    await expect(kept.unwrap()).rejects.toMatchObject({ name: 'AbortError' });

    expect(abortRunning(store.dispatch, [kept])).toBe(false);
    kept.unsubscribe();
  });

  it('a kept read that joined one already running stops the request that is actually running', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/users/:tag', () => {
        calls += 1;
        return never();
      }),
    );
    const store = makeTestStore();
    const first = store.dispatch(apiSlice.endpoints.lookupRecipient.initiate('ana'));
    await waitFor(() => expect(calls).toBe(1));
    // What a lazy trigger dispatches for the same handle while the first check still runs: it
    // starts no request of its own.
    const joined = store.dispatch(
      apiSlice.endpoints.lookupRecipient.initiate('ana', { forceRefetch: true }),
    );
    const entry = () => store.getState().api.queries[joined.queryCacheKey];
    expect(entry()?.requestId).toBe(first.requestId);
    expect(joined.requestId).not.toBe(first.requestId);

    expect(abortRunning(store.dispatch, [joined])).toBe(true);
    await expect(first.unwrap()).rejects.toMatchObject({ name: 'AbortError' });
    expect(calls).toBe(1);

    first.unsubscribe();
    joined.unsubscribe();
  });
});
