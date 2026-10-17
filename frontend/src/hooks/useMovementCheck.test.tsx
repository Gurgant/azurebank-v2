import type { PropsWithChildren } from 'react';
import { act, renderHook, waitFor } from '@testing-library/react';
import { Provider } from 'react-redux';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { serviceUnavailable } from '../mocks/problem';
import { mockState } from '../mocks/state';
import { makeTestStore } from '../test/renderWithProviders';
import {
  advance,
  advanceUntil,
  installFakeClock,
  lookupsSeen,
  whenListedByKey,
} from '../test/outage';
import type { SentRequest } from './useIdempotentMutation';
import { useMovementCheck, type MovementTargets } from './useMovementCheck';

/**
 * The lookup's state machine (ADR-0022), apart from any flow: that it settles under StrictMode,
 * which the app renders in; that an answer about a copy no longer held says nothing; and that the
 * ask it makes by itself at the bound is one ask, not a loop.
 */

afterEach(() => {
  vi.useRealTimers();
});

const MAIN = mockState.accounts[0].id;

interface Body {
  accountId: string;
}

const target = (body: Body): MovementTargets => ({
  first: { accountId: body.accountId, type: 'Deposit' },
});

function copy(overrides: Partial<SentRequest<Body>> = {}): SentRequest<Body> {
  return {
    key: crypto.randomUUID(),
    body: { accountId: MAIN },
    lastSentAt: performance.now(),
    everyAuthorisationRefused: false,
    ...overrides,
  };
}

/*
  StrictMode by `reactStrictMode`, not by a `<StrictMode>` in the wrapper: measured, a wrapper's
  StrictMode did not run a hook's effect twice under `renderHook`, and the option did.
*/
function options({ strict }: { strict: boolean }) {
  const store = makeTestStore();
  function Wrapper({ children }: PropsWithChildren) {
    return <Provider store={store}>{children}</Provider>;
  }
  return { wrapper: Wrapper, reactStrictMode: strict };
}

/** A gate a handler waits on, opened by the test. */
function gate() {
  let open = () => {};
  const opened = new Promise<void>((resolve) => {
    open = resolve;
  });
  return { opened, open };
}

describe('useMovementCheck', () => {
  it('settles under StrictMode: the first run is aborted by its own cleanup, and the second asks', async () => {
    const seen = lookupsSeen();
    const sent = copy();
    const { result } = renderHook(
      () => useMovementCheck(sent, target, () => {}),
      options({ strict: true }),
    );

    // Asked while the request may still commit, and the listing is empty: not yet.
    await waitFor(() => expect(result.current.verdict).toBe('notYet'));
    expect(result.current.checking).toBe(false);
    expect(seen.length).toBeGreaterThanOrEqual(1);
  });

  it('is off with no copy, and asks nothing', async () => {
    const seen = lookupsSeen();
    const { result } = renderHook(
      () => useMovementCheck<Body>(null, target, () => {}),
      options({ strict: true }),
    );

    expect(result.current.verdict).toBe('off');
    expect(result.current.offerNewKey).toBe(false);
    expect(seen).toHaveLength(0);
  });

  it('drops a late answer about a copy it no longer holds', async () => {
    const first = copy();
    const second = copy();
    const held = gate();
    whenListedByKey(async (request) => {
      const key = new URL(request.url).searchParams.get('IdempotencyKey');
      if (key === first.key) {
        await held.opened;
        // What the first copy's ask would say if it counted: that it went through.
        return Response.json({
          data: [
            {
              id: '019f7b3f-0000-7000-8000-000000000d77',
              transactionNumber: 'TXN-OF-THE-OLD-KEY',
              type: 'Deposit',
              amount: 5,
              balanceAfter: 10,
              status: 'Completed',
              createdAt: '2026-07-22T10:00:00Z',
              idempotencyKey: first.key,
            },
          ],
          pagination: {
            page: 1,
            pageSize: 20,
            totalItems: 1,
            totalPages: 1,
            hasNextPage: false,
            hasPreviousPage: false,
          },
        });
      }
      return serviceUnavailable({ via: 'api', instance: '/api/transactions' });
    });
    const seen = lookupsSeen();
    const onFound = vi.fn();
    const { result, rerender } = renderHook(({ sent }) => useMovementCheck(sent, target, onFound), {
      initialProps: { sent: first },
      ...options({ strict: false }),
    });
    await waitFor(() => expect(seen.some((ask) => ask.key === first.key)).toBe(true));

    rerender({ sent: second });
    await waitFor(() => expect(result.current.verdict).toBe('unchecked'));
    held.open();
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });

    expect(result.current.verdict).toBe('unchecked');
    expect(result.current.movement).toBeNull();
    expect(onFound).not.toHaveBeenCalled();
  });

  it('asks once more, and only once, when an ask sent before the bound is answered after it', async () => {
    installFakeClock();
    const held = gate();
    let asks = 0;
    whenListedByKey(async () => {
      asks += 1;
      if (asks === 1) await held.opened;
      // A loop of asks would starve the worker: past a handful, none is answered.
      if (asks > 5) await new Promise(() => {});
      return serviceUnavailable({ via: 'api', instance: '/api/transactions' });
    });
    // Sent 3 min ago: the bound is 5 s away, inside the 65 s the ask may take.
    const sent = copy({ lastSentAt: performance.now() - 180_000 });
    const { result } = renderHook(
      () => useMovementCheck(sent, target, () => {}),
      options({ strict: false }),
    );
    await waitFor(() => expect(asks).toBe(1));
    expect(result.current.checking).toBe(true);

    // The bound passes while the ask is out: the timer leaves it to the answer.
    await advance(6_000);
    expect(asks).toBe(1);
    held.open();

    await waitFor(() => expect(asks).toBe(2));
    await waitFor(() => expect(result.current.checking).toBe(false));
    expect(result.current.verdict).toBe('unchecked');
    expect(result.current.pastLive).toBe(true);
    expect(result.current.offerNewKey).toBe(true);

    // That ask was sent after the bound and answered unchecked too: no loop.
    await advance(400_000);
    expect(asks).toBe(2);
  });

  it('asks once by itself at the bound after "not yet", and an empty page then means none', async () => {
    installFakeClock();
    const seen = lookupsSeen();
    // Counted from before the copy: the clock also moves with real time while the test waits.
    const start = Date.now();
    const sent = copy();
    const { result } = renderHook(
      () => useMovementCheck(sent, target, () => {}),
      options({ strict: false }),
    );
    await waitFor(() => expect(result.current.verdict).toBe('notYet'));
    const before = seen.length;
    expect(before).toBe(1);
    expect(result.current.offerNewKey).toBe(false);

    await advanceUntil(start, 184_000);
    expect(seen).toHaveLength(before);
    expect(result.current.verdict).toBe('notYet');

    await advanceUntil(start, 186_000);
    await waitFor(() => expect(result.current.verdict).toBe('none'));
    expect(seen).toHaveLength(before + 1);
    expect(result.current.offerNewKey).toBe(true);
    expect(result.current.offerCheckAgain).toBe(false);
  });

  it('sends nothing for "Check again" while the listing is still being asked', async () => {
    const held = gate();
    whenListedByKey(async () => {
      await held.opened;
      return serviceUnavailable({ via: 'api', instance: '/api/transactions' });
    });
    const seen = lookupsSeen();
    const sent = copy();
    const { result } = renderHook(
      () => useMovementCheck(sent, target, () => {}),
      options({ strict: false }),
    );
    await waitFor(() => expect(seen).toHaveLength(1));
    expect(result.current.checking).toBe(true);

    act(() => result.current.checkAgain());
    held.open();

    await waitFor(() => expect(result.current.verdict).toBe('unchecked'));
    expect(seen).toHaveLength(1);
  });

  it('says none at once when every authorisation under the key was refused', async () => {
    const seen = lookupsSeen();
    const sent = copy({ everyAuthorisationRefused: true });
    const { result } = renderHook(
      () => useMovementCheck(sent, target, () => {}),
      options({ strict: false }),
    );

    await waitFor(() => expect(result.current.verdict).toBe('none'));
    expect(result.current.pastLive).toBe(true);
    expect(result.current.offerNewKey).toBe(true);
    expect(seen).toHaveLength(1);
  });
});
