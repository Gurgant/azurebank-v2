import { cleanup, renderHook, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Button } from '@fluentui/react-components';
import { http } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { makeTestStore, renderWithProviders } from '../../test/renderWithProviders';
import { COPY, advance, never } from '../../test/outage';
import { apiSlice } from '../../features/api/apiSlice';
import {
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
  it('uses the thresholds the plan set: 5 s, then 20 s', () => {
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

  it('says nothing once the wait ends, and a new wait starts again from zero', async () => {
    vi.useFakeTimers();
    const { result, rerender } = renderHook(({ active }) => useWaitPhase(active), {
      initialProps: { active: true },
    });
    await advance(STILL_TRYING_AFTER_MS);
    expect(result.current).toBe('stillTrying');

    rerender({ active: false });
    expect(result.current).toBe('none');

    rerender({ active: true });
    expect(result.current).toBe('none');
    await advance(SLOW_AFTER_MS - 1);
    expect(result.current).toBe('none');
    await advance(1);
    expect(result.current).toBe('slow');
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
});

describe('useWaitLanding', () => {
  function Landing({ waiting, showLanding }: { waiting: boolean; showLanding: boolean }) {
    const { landingRef, arm } = useWaitLanding(waiting);
    return (
      <>
        <button type="button" onClick={arm}>
          Arm
        </button>
        {showLanding && (
          <button
            type="button"
            ref={(el) => {
              landingRef.current = el;
            }}
          >
            Landing
          </button>
        )}
      </>
    );
  }

  it('moves focus to the landing when an armed wait ends in one', async () => {
    const { rerender } = renderWithProviders(<Landing waiting showLanding={false} />);
    await userEvent.click(screen.getByRole('button', { name: 'Arm' }));

    rerender(<Landing waiting={false} showLanding />);

    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Landing' })),
    );
  });

  it('leaves focus alone when the wait was not armed', async () => {
    const { rerender } = renderWithProviders(<Landing waiting showLanding={false} />);
    const other = screen.getByRole('button', { name: 'Arm' });
    other.focus();

    rerender(<Landing waiting={false} showLanding />);

    expect(document.activeElement).toBe(other);
  });

  it('disarms when an armed wait ends without a landing, so a later one takes nothing', async () => {
    const { rerender } = renderWithProviders(<Landing waiting showLanding={false} />);
    const arm = screen.getByRole('button', { name: 'Arm' });
    await userEvent.click(arm);

    // Ends in success: nothing to land on.
    rerender(<Landing waiting={false} showLanding={false} />);
    // A later wait, never armed, ends in a failure.
    rerender(<Landing waiting showLanding={false} />);
    rerender(<Landing waiting={false} showLanding />);

    expect(document.activeElement).toBe(arm);
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
});
