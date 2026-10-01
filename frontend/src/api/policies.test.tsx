import type { PropsWithChildren } from 'react';
import { act, renderHook, waitFor } from '@testing-library/react';
import { Provider } from 'react-redux';
import { getStepUpSnapshot, settleStepUp } from '../features/auth/stepUpController';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../mocks/server';
import { problem, serviceUnavailable } from '../mocks/problem';
import { mockState, seedMockSession } from '../mocks/state';
import { makeTestStore, type TestStore } from '../test/renderWithProviders';
import {
  COPY,
  advance,
  advanceUntil,
  committedAnswerLost,
  never,
  sleep,
  track,
  installFakeClock,
} from '../test/outage';
import {
  apiSlice,
  useDepositMutation,
  useGetAccountQuery,
  useGetAccountsQuery,
  useGetTransactionsQuery,
  useWithdrawMutation,
} from '../features/api/apiSlice';
import { useIdempotentMutation, type IdempotentTrigger } from '../hooks/useIdempotentMutation';
import type { BaseQueryApi } from '@reduxjs/toolkit/query';
import { problemBaseQuery, toApiProblem, type ApiProblem } from './problemBaseQuery';
import { classifyMoneyProblem } from './moneyProblem';

afterEach(() => {
  vi.useRealTimers();
});

/**
 * The six flagship policy tests (ADR-0022) — the data-layer contract as executable
 * ADR. Tests 4 and 5 pin their FOUNDATION half here; their full-flow forms (interceptor
 * byte-identity replay, sessionExpired dispatch) land with PR-11 and PR-4/PR-10.
 */

const UUID = '11111111-2222-4333-8444-555555555555';

type Settled<T> = { ok: true; value: T } | { ok: false; error: ApiProblem };

function settle<T>(promise: Promise<T>): Promise<Settled<T>> {
  return promise.then(
    (value) => ({ ok: true as const, value }),
    (error: unknown) => ({ ok: false as const, error: error as ApiProblem }),
  );
}

/** A FULL contract-valid receipt — the strict deposit schema parses it fail-closed. */
function depositOk(status = 201) {
  return HttpResponse.json(
    {
      data: {
        transaction: {
          id: '019f7b3f-0000-7000-8000-000000000e01',
          transactionNumber: 'TXN-TEST',
          type: 'Deposit',
          amount: 50,
          balanceAfter: 100,
          description: null,
          recipientAzureTag: null,
          senderAzureTag: null,
          status: 'Completed',
          createdAt: '2026-07-22T10:00:00.0000000Z',
        },
        newBalance: 100,
      },
      message: null,
    },
    { status },
  );
}

function hookWrapper() {
  const store = makeTestStore();
  function Wrapper({ children }: PropsWithChildren) {
    return <Provider store={store}>{children}</Provider>;
  }
  return { store, Wrapper };
}

function useDepositIntent() {
  const [trigger] = useDepositMutation();
  return useIdempotentMutation(trigger);
}

function useWithdrawIntent() {
  const [trigger] = useWithdrawMutation();
  return useIdempotentMutation(trigger);
}

describe('data-layer policies (flagship, ADR-0022)', () => {
  it('1 — keeps the same Idempotency-Key across 503 + user Retry; a fresh key only after 422 + edit', async () => {
    const keys: string[] = [];
    let call = 0;
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '(missing)');
        call += 1;
        if (call === 1) return problem({ status: 503, title: 'Service Unavailable' });
        if (call === 2) return depositOk();
        if (call === 3) return problem({ status: 422, errorCode: 'IDEMPOTENCY_KEY_REUSE' });
        return depositOk();
      }),
    );

    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useDepositIntent(), { wrapper: Wrapper });
    const body = { accountId: UUID, amount: 50 };

    const first = await act(() => settle(result.current.submit(body)));
    expect(first.ok).toBe(false);

    // User-driven Retry re-sends the SAME key (5xx = server may have recorded it).
    const second = await act(() => settle(result.current.submit(body)));
    expect(second.ok).toBe(true);
    expect(keys[1]).toBe(keys[0]);

    // New intent after success: fresh key.
    const third = await act(() => settle(result.current.submit(body)));
    expect(third.ok).toBe(false);
    if (third.ok) throw new Error('unreachable');
    expect(third.error.errorCode).toBe('IDEMPOTENCY_KEY_REUSE');
    expect(keys[2]).not.toBe(keys[1]);

    // 422 dropped the key; the body edit resets the intent; next submit re-keys.
    act(() => result.current.resetIntent());
    const fourth = await act(() => settle(result.current.submit({ ...body, amount: 60 })));
    expect(fourth.ok).toBe(true);
    expect(keys[3]).not.toBe(keys[2]);
  });

  it('1b — keeps the same Idempotency-Key when the 503 says nothing was applied', async () => {
    // `applied: false` changes the words a visitor reads, never the key — ADR-0058: "A client never
    // drops the key on `applied: false`". The same key is what lets the server answer the retry
    // from the first attempt's record, if there is one.
    const keys: string[] = [];
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '(missing)');
        return keys.length === 1
          ? serviceUnavailable({
              via: 'api',
              applied: false,
              instance: '/api/transactions/deposit',
            })
          : depositOk();
      }),
    );

    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useDepositIntent(), { wrapper: Wrapper });
    const body = { accountId: UUID, amount: 50 };

    const first = await act(() => settle(result.current.submit(body)));
    expect(first.ok).toBe(false);
    expect(result.current.keyRetained).toBe(true);

    const second = await act(() => settle(result.current.submit(body)));
    expect(second.ok).toBe(true);
    expect(keys).toHaveLength(2);
    expect(keys[1]).toBe(keys[0]);
  });

  /** Records the fake instant of every call, answering each with `answer`. */
  function recordReads(answer: () => Response | Promise<Response>) {
    const times: number[] = [];
    server.use(
      http.get('*/api/accounts', () => {
        times.push(Date.now());
        return answer();
      }),
    );
    return times;
  }

  it("2 — retries a read's 503 once, after the answer's retryAfterSeconds, then gives up", async () => {
    const times = recordReads(() =>
      serviceUnavailable({ via: 'api', retryAfterSeconds: 10, instance: '/api/accounts' }),
    );
    installFakeClock();
    const { store } = hookWrapper();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    // Never earlier than asked, and at most a fifth later: the spread is random here.
    await advanceUntil(first, 9_900);
    expect(times).toHaveLength(1);

    await advanceUntil(first, 12_100);
    await waitFor(() => expect(times).toHaveLength(2));

    await advanceUntil(first, 60_000);
    expect(times).toHaveLength(2);
    expect(outcome.state).toBe('rejected');
    expect(outcome.error).toMatchObject({ status: 503 });
    query.unsubscribe();
  });

  it("2b — never retries a mutation's 503", async () => {
    let postCalls = 0;
    server.use(
      http.post('*/api/transactions/deposit', () => {
        postCalls += 1;
        return serviceUnavailable({ via: 'api', instance: '/api/transactions/deposit' });
      }),
    );
    installFakeClock();
    const { store } = hookWrapper();

    const mutation = store.dispatch(
      apiSlice.endpoints.deposit.initiate({
        idempotencyKey: crypto.randomUUID(),
        body: { accountId: UUID, amount: 5 },
      }),
    );
    const outcome = track(mutation.unwrap());
    await advance(60_000);

    await waitFor(() => expect(outcome.state).toBe('rejected'));
    expect(outcome.error).toMatchObject({ status: 503 });
    expect(postCalls).toBe(1);
  });

  it('2c — a read retry that would not fit the two-minute budget is not sent', async () => {
    // Held 55 s, as the BFF answers when the API does not: 55 + 70 leaves no room for a retry.
    const times = recordReads(async () => {
      await sleep(55_000);
      return serviceUnavailable({ via: 'bff', retryAfterSeconds: 70, instance: '/api/accounts' });
    });
    installFakeClock();
    const { store } = hookWrapper();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await advanceUntil(first, 180_000);
    expect(times).toHaveLength(1);
    expect(outcome.state).toBe('rejected');
    expect(outcome.error).toMatchObject({ status: 503 });
    expect((outcome.at ?? 0) - first).toBeGreaterThanOrEqual(55_000);
    expect((outcome.at ?? 0) - first).toBeLessThan(55_500);
    query.unsubscribe();
  });

  it('2d — a read retry that fits is sent, and is cut where the two-minute budget ends', async () => {
    const times = recordReads(async () => {
      await sleep(55_000);
      return serviceUnavailable({ via: 'bff', retryAfterSeconds: 10, instance: '/api/accounts' });
    });
    installFakeClock();
    const { store } = hookWrapper();
    const start = Date.now();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await advanceUntil(start, 119_900);
    expect(times).toHaveLength(2);
    expect(outcome.state).toBe('pending');

    await advanceUntil(first, 120_100);
    await waitFor(() => expect(outcome.state).toBe('rejected'));
    expect(outcome.error).toMatchObject({ status: 'NETWORK', errorCode: 'TIMEOUT_ERROR' });
    expect((outcome.at ?? 0) - start).toBeGreaterThanOrEqual(119_900);
    expect((outcome.at ?? 0) - first).toBeLessThanOrEqual(120_100);
    expect(times).toHaveLength(2);
    query.unsubscribe();
  });

  it('2e — a request that never reached a server is retried once, a second later, for a read', async () => {
    const times = recordReads(() => HttpResponse.error());
    installFakeClock();
    const { store } = hookWrapper();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await advanceUntil(first, 900);
    expect(times).toHaveLength(1);

    await advanceUntil(first, 1_300);
    await waitFor(() => expect(times).toHaveLength(2));

    await advanceUntil(first, 10_000);
    expect(times).toHaveLength(2);
    expect(outcome.state).toBe('rejected');
    query.unsubscribe();
  });

  it.each([
    ['a 502', () => problem({ status: 502 })],
    ['a 502 with no body', () => new HttpResponse(null, { status: 502 })],
    ['a 504', () => problem({ status: 504 })],
    ['a 503 that names no wait', () => problem({ status: 503 })],
    [
      'a 503 that is not JSON and names no wait',
      () => serviceUnavailable({ via: 'api', contentType: 'text/plain' }),
    ],
  ])('2r — %s is retried once, five seconds later, for a read', async (_name, answer) => {
    // A gateway's answer that names no wait says the service behind it is down, not that a
    // connection blinked: a second later it is most likely still down.
    const times = recordReads(answer);
    installFakeClock();
    const { store } = hookWrapper();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await advanceUntil(first, 4_900);
    expect(times).toHaveLength(1);

    await advanceUntil(first, 6_100);
    await waitFor(() => expect(times).toHaveLength(2));

    await advanceUntil(first, 30_000);
    expect(times).toHaveLength(2);
    expect(outcome.state).toBe('rejected');
    query.unsubscribe();
  });

  it('2f — a read retry that would have less than five seconds left is not sent', async () => {
    // 55 + 62 leaves 3 s of the two minutes: too little for an answer, so the visitor is told now.
    const times = recordReads(async () => {
      await sleep(55_000);
      return serviceUnavailable({ via: 'bff', retryAfterSeconds: 62, instance: '/api/accounts' });
    });
    installFakeClock();
    const { store } = hookWrapper();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await advanceUntil(first, 180_000);
    expect(times).toHaveLength(1);
    expect(outcome.state).toBe('rejected');
    expect(outcome.error).toMatchObject({ status: 503 });
    expect((outcome.at ?? 0) - first).toBeLessThan(55_500);
    query.unsubscribe();
  });

  it('2g — a read stopped while it waits for its retry sends nothing more', async () => {
    const times = recordReads(() =>
      serviceUnavailable({ via: 'api', retryAfterSeconds: 10, instance: '/api/accounts' }),
    );
    installFakeClock();
    const { store } = hookWrapper();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await advanceUntil(first, 5_000);
    query.abort();
    await waitFor(() => expect(outcome.state).toBe('rejected'));
    expect(outcome.error).toMatchObject({ name: 'AbortError' });

    await advanceUntil(first, 30_000);
    expect(times).toHaveLength(1);
    query.unsubscribe();
  });

  it.each([
    ['a 500', () => problem({ status: 500, errorCode: 'INTERNAL_ERROR' })],
    [
      'a 429 that names a wait',
      () =>
        problem({ status: 429, errorCode: 'RATE_LIMITED', extensions: { retryAfterSeconds: 1 } }),
    ],
    [
      'a 502 that is not JSON',
      () =>
        new HttpResponse('Bad Gateway', {
          status: 502,
          headers: { 'Content-Type': 'text/plain' },
        }),
    ],
  ])('2h — %s is an answer, and a read never sends it again', async (_name, answer) => {
    const times = recordReads(answer);
    installFakeClock();
    const { store } = hookWrapper();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await waitFor(() => expect(outcome.state).toBe('rejected'));
    await advanceUntil(first, 10_000);
    expect(times).toHaveLength(1);
    query.unsubscribe();
  });

  it('2i — a wait before the retry that ends late, as when a phone suspends the page, still leaves the retry its own time', async () => {
    // The answer names 10 s, and two seconds in the page is suspended for two minutes: by the wall
    // clock the budget is spent when the wait ends. The retry was allowed when it was planned, so
    // it goes out with a limit of its own and sees the service that came back meanwhile.
    const times: number[] = [];
    server.use(
      http.get('*/api/accounts', async () => {
        times.push(Date.now());
        if (times.length === 1) {
          return serviceUnavailable({
            via: 'api',
            retryAfterSeconds: 10,
            instance: '/api/accounts',
          });
        }
        // Back, and a second to answer: the mock's own accounts, from the handler behind this one.
        await sleep(1_000);
        return undefined;
      }),
    );
    installFakeClock();
    const { store } = hookWrapper();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times).toHaveLength(1));

    await advanceUntil(times[0], 2_000);
    // Suspended: the wall clock jumps, and no timer runs meanwhile.
    vi.setSystemTime(Date.now() + 120_000);
    await advance(10_000);
    await waitFor(() => expect(times).toHaveLength(2));

    await advance(2_000);
    await waitFor(() => expect(outcome.state).not.toBe('pending'));
    expect(outcome.error).toBeUndefined();
    expect(outcome.state).toBe('fulfilled');
    expect(times).toHaveLength(2);
    query.unsubscribe();
  });

  it('2j — a read stopped while it waits for its retry gives up at once, not when the wait would have ended', async () => {
    // Seen from the base query itself: the store rejects a stopped read at once whatever the base
    // query goes on doing, so only here does a wait that ignores the stop differ from one that ends.
    const times = recordReads(() =>
      serviceUnavailable({ via: 'api', retryAfterSeconds: 10, instance: '/api/accounts' }),
    );
    installFakeClock();
    const stop = new AbortController();
    const api = {
      signal: stop.signal,
      abort: (reason?: string) => stop.abort(reason),
      dispatch: () => undefined,
      getState: () => ({}),
      extra: undefined,
      endpoint: 'getAccounts',
      type: 'query',
    } as unknown as BaseQueryApi;
    const outcome = track(Promise.resolve(problemBaseQuery('/api/accounts', api, {})));
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await advanceUntil(first, 2_000);
    expect(outcome.state).toBe('pending');
    stop.abort();

    await waitFor(() => expect(outcome.state).toBe('fulfilled'));
    expect(outcome.value).toMatchObject({ error: { status: 503 } });
    expect((outcome.at ?? 0) - first).toBeLessThan(3_000);
    expect(times).toHaveLength(1);
  });

  it('2k — a 503 that is not JSON waits what its Retry-After header asks before the retry', async () => {
    const times = recordReads(
      () =>
        new HttpResponse('Service Unavailable', {
          status: 503,
          headers: { 'Content-Type': 'text/plain', 'Retry-After': '30' },
        }),
    );
    installFakeClock();
    const { store } = hookWrapper();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await advanceUntil(first, 29_900);
    expect(times).toHaveLength(1);

    await advanceUntil(first, 36_100);
    await waitFor(() => expect(times).toHaveLength(2));
    await waitFor(() => expect(outcome.state).toBe('rejected'));
    expect(outcome.error).toMatchObject({
      status: 503,
      errorCode: 'HTTP_503',
      retryAfterSeconds: 30,
    });
    query.unsubscribe();
  });

  it('2l — a Retry-After that is a date waits until that date, never less', async () => {
    // RFC 9110 lets Retry-After be an HTTP-date. It has whole seconds, so the date 30 s ahead,
    // written at the answer, is between 29 and 30 s ahead.
    const times = recordReads(
      () =>
        new HttpResponse('Service Unavailable', {
          status: 503,
          headers: {
            'Content-Type': 'text/plain',
            'Retry-After': new Date(Date.now() + 30_000).toUTCString(),
          },
        }),
    );
    installFakeClock();
    const { store } = hookWrapper();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await advanceUntil(first, 28_900);
    expect(times).toHaveLength(1);

    await advanceUntil(first, 36_100);
    await waitFor(() => expect(times).toHaveLength(2));
    await waitFor(() => expect(outcome.state).toBe('rejected'));
    const named = (outcome.error as ApiProblem).retryAfterSeconds ?? 0;
    expect(named).toBeGreaterThanOrEqual(29);
    expect(named).toBeLessThanOrEqual(30);
    query.unsubscribe();
  });

  /** What RTK hands the base query for a read, with a stop of its own. */
  function readApi() {
    const stop = new AbortController();
    return {
      signal: stop.signal,
      abort: (reason?: string) => stop.abort(reason),
      dispatch: () => undefined,
      getState: () => ({}),
      extra: undefined,
      endpoint: 'getAccounts',
      type: 'query',
    } as unknown as BaseQueryApi;
  }

  it.each([
    [0, 10_000],
    [0.5, 11_000],
    [0.999, 11_998],
  ])(
    '2m — the wait before a retry is the one asked for plus up to a fifth: random %s waits %s ms',
    async (random, waitMs) => {
      // Every reader in one outage hears the same Retry-After; without a spread they all come
      // back in the same second.
      const times = recordReads(() =>
        serviceUnavailable({ via: 'api', retryAfterSeconds: 10, instance: '/api/accounts' }),
      );
      installFakeClock();
      const outcome = track(
        Promise.resolve(problemBaseQuery('/api/accounts', readApi(), { random: () => random })),
      );
      await waitFor(() => expect(times.length).toBeGreaterThan(0));
      const [first] = times;

      await advanceUntil(first, waitMs - 100);
      expect(times).toHaveLength(1);

      await advanceUntil(first, waitMs + 100);
      await waitFor(() => expect(times).toHaveLength(2));
      await waitFor(() => expect(outcome.state).toBe('fulfilled'));
    },
  );

  it('2n — the two-minute budget is checked against the wait with its spread', async () => {
    // Held 55 s, then 55 s asked for: 110 s, which would leave the retry 10 s. With the spread the
    // wait is 65.89 s, past the budget, so no retry is sent and the visitor is told at 55 s.
    const times = recordReads(async () => {
      await sleep(55_000);
      return serviceUnavailable({ via: 'bff', retryAfterSeconds: 55, instance: '/api/accounts' });
    });
    installFakeClock();
    const outcome = track(
      Promise.resolve(problemBaseQuery('/api/accounts', readApi(), { random: () => 0.99 })),
    );
    await waitFor(() => expect(times.length).toBeGreaterThan(0));
    const [first] = times;

    await advanceUntil(first, 180_000);
    expect(times).toHaveLength(1);
    expect(outcome.state).toBe('fulfilled');
    expect(outcome.value).toMatchObject({ error: { status: 503 } });
    expect((outcome.at ?? 0) - first).toBeLessThan(55_500);
  });

  it('2o — a Retry-After date with part of a second to go is rounded up: the retry never goes before it', async () => {
    // A date has whole seconds and the answer comes partway through one, so the wait to the date
    // has a fraction. Rounded down, the retry would go up to a second before the date.
    let named = 0;
    const times = recordReads(() => {
      // The first answer names the date; the retry is answered with the same one.
      if (named === 0) named = (Math.floor(Date.now() / 1000) + 30) * 1000;
      return new HttpResponse('Service Unavailable', {
        status: 503,
        headers: { 'Content-Type': 'text/plain', 'Retry-After': new Date(named).toUTCString() },
      });
    });
    installFakeClock();
    // Half a second into a second: the date is about 29.5 s ahead when the answer is read.
    vi.setSystemTime(Math.floor(Date.now() / 1000) * 1000 + 500);
    const outcome = track(
      Promise.resolve(problemBaseQuery('/api/accounts', readApi(), { random: () => 0 })),
    );
    await waitFor(() => expect(times.length).toBeGreaterThan(0));

    await advanceUntil(named, -50);
    expect(times).toHaveLength(1);

    await advanceUntil(named, 1_100);
    await waitFor(() => expect(times).toHaveLength(2));
    expect(times[1]).toBeGreaterThanOrEqual(named);
    await waitFor(() => expect(outcome.state).toBe('fulfilled'));
  });

  /** A 503 whose body is not JSON, as an ingress answers, with `retryAfter` as its header. */
  function plainOutage(retryAfter: string): ApiProblem {
    return toApiProblem(
      {
        status: 'PARSING_ERROR',
        originalStatus: 503,
        data: 'Service Unavailable',
        error: 'SyntaxError: Unexpected token',
      },
      new Response(null, { status: 503, headers: { 'Retry-After': retryAfter } }),
    );
  }

  it.each([
    ['IMF-fixdate', 'Thu, 01 Oct 2026 12:00:30 GMT'],
    ['RFC 850', 'Thursday, 01-Oct-26 12:00:30 GMT'],
    ['asctime', 'Thu Oct  1 12:00:30 2026'],
  ])('2p — a Retry-After date in the %s form is read as GMT', (_form, date) => {
    // RFC 9110 §5.6.7: a recipient accepts all three, and all three are GMT, the asctime one
    // without saying so. Read in the local zone, it was hours off anywhere but UTC: early east of
    // it, so the retry went at once, late west of it. In UTC both readings agree, so this can only
    // fail in another zone (the suite's two zone runs).
    vi.useFakeTimers({ now: Date.UTC(2026, 9, 1, 12, 0, 0), toFake: ['Date'] });
    expect(plainOutage(date).retryAfterSeconds).toBe(30);
  });

  it('2s — an RFC 850 date whose two-digit year would be more than 50 years ahead is in the past', () => {
    // RFC 9110 §5.6.7: "77" in 2026 is 1977, a date already past, so no wait. Read as 2077 it
    // would be a wait of 51 years.
    vi.useFakeTimers({ now: Date.UTC(2026, 9, 1, 12, 0, 0), toFake: ['Date'] });
    expect(plainOutage('Saturday, 01-Oct-77 12:00:30 GMT').retryAfterSeconds).toBe(0);
  });

  it.each(['1.5', '-5', 'Thu, 01 Oct 2026 12:00:30', 'soon'])(
    '2q — a Retry-After of "%s" is neither a number of seconds nor a date, and names no wait',
    (value) => {
      // Not a wait of 0: the default for the answer's kind applies (2e).
      vi.useFakeTimers({ now: Date.UTC(2026, 9, 1, 12, 0, 0), toFake: ['Date'] });
      expect(plainOutage(value).retryAfterSeconds).toBeUndefined();
    },
  );

  it('3 — normalizes ProblemDetails to ApiProblem (traceId through); synthesizes VALIDATION_ERROR on 400 + errors', async () => {
    server.use(
      http.post('*/api/transactions/deposit', () =>
        problem({ status: 422, errorCode: 'INSUFFICIENT_FUNDS', detail: 'Balance too low' }),
      ),
    );
    const { store } = hookWrapper();

    const business = await settle(
      store
        .dispatch(
          apiSlice.endpoints.deposit.initiate({
            idempotencyKey: crypto.randomUUID(),
            body: { accountId: UUID, amount: 999 },
          }),
        )
        .unwrap(),
    );
    expect(business.ok).toBe(false);
    if (business.ok) throw new Error('unreachable');
    expect(business.error).toMatchObject({
      status: 422,
      errorCode: 'INSUFFICIENT_FUNDS',
      detail: 'Balance too low',
    });
    expect(business.error.traceId).toMatch(/^[0-9a-f]{32}$/);

    // Validation 400s carry an errors dict but NO errorCode — the normalizer synthesizes one.
    server.use(
      http.post('*/api/transactions/deposit', () =>
        problem({ status: 400, errors: { amount: ['Amount must be at least $0.01.'] } }),
      ),
    );
    const validation = await settle(
      store
        .dispatch(
          apiSlice.endpoints.deposit.initiate({
            idempotencyKey: crypto.randomUUID(),
            body: { accountId: UUID, amount: 0 },
          }),
        )
        .unwrap(),
    );
    expect(validation.ok).toBe(false);
    if (validation.ok) throw new Error('unreachable');
    expect(validation.error.errorCode).toBe('VALIDATION_ERROR');
    expect(validation.error.errors).toEqual({ amount: ['Amount must be at least $0.01.'] });
  });

  it("3b — carries DAILY_LIMIT_EXCEEDED's four extension members through the same channel", async () => {
    /*
      ADR-0050's four members, and this test DISPATCHES A REAL ENDPOINT THROUGH THE REAL BASE QUERY
      on purpose. A test written against `problemDetailsBodySchema` alone passes today, before any
      change, and certifies nothing: the members already survive the parse — the schema is a
      `z.looseObject` — and the drop is the return literal's allow-list in `toApiProblem`.

      MEASURED A8 (2026-09-07T14:17:53Z, PR #156's working tree on 3c30122, merged as fda7ff7, BFF
      :5000 -> API :7215, AzureBankDev, DailyLimit:Amount default; transcript
      plans/daily-limit/measure-after-2026-09-07.txt): `limit` / `used` / `requested` arrive as
      top-level JSON NUMBERS and `resetsAt` as a STRING, beside errorCode and traceId, unchanged
      through the BFF.

      The values asserted are the PARSED ones. The wire spells the decimals `4900.0` / `0.0` /
      `4600.0` — a C# decimal artifact `JSON.stringify` cannot reproduce — so an assertion on raw
      body text would be red for a reason that has nothing to do with alignment.
      `money.contract.test.ts` documents the same trap.
    */
    server.use(
      http.post('*/api/transfers', () =>
        problem({
          status: 422,
          errorCode: 'DAILY_LIMIT_EXCEEDED',
          detail: 'Daily transfer limit exceeded.',
          extensions: {
            limit: 5000,
            used: 4900,
            requested: 400,
            resetsAt: '2026-09-08T00:00:00Z',
          },
        }),
      ),
    );
    const { store } = hookWrapper();

    const refused = await settle(
      store
        .dispatch(
          apiSlice.endpoints.transfer.initiate({
            idempotencyKey: crypto.randomUUID(),
            body: { fromAccountId: UUID, recipientAzureTag: 'friend', amount: 400 },
          }),
        )
        .unwrap(),
    );
    expect(refused.ok).toBe(false);
    if (refused.ok) throw new Error('unreachable');
    expect(refused.error).toMatchObject({
      status: 422,
      errorCode: 'DAILY_LIMIT_EXCEEDED',
      detail: 'Daily transfer limit exceeded.',
      limit: 5000,
      used: 4900,
      requested: 400,
      resetsAt: '2026-09-08T00:00:00Z',
    });
    // The types, separately from the values: three numbers and one string is what A8 measured, and
    // a stringified number would satisfy neither `toMatchObject` above nor a consumer doing
    // arithmetic on `limit - used`.
    expect(typeof refused.error.limit).toBe('number');
    expect(typeof refused.error.used).toBe('number');
    expect(typeof refused.error.requested).toBe('number');
    expect(typeof refused.error.resetsAt).toBe('string');
  });

  it('4 — the step-up 403 (X-Auth-Level-Required header, D2) drives the interceptor; cancel → STEP_UP_CANCELLED', async () => {
    /*
      Driven through the REVEAL endpoint, not a transfer.

      This test used to dispatch a transfer, because the stateful transfers handler answered a bare
      403 at authLevel 1. Since ADR-0041 it does not: the PIN travels in the transfer body and the
      API verifies it, so a transfer never produces the 403 this test is about. Reveal keeps the
      session model deliberately (D3a) and is now the only endpoint that emits one.

      The property is unchanged. The interceptor (baseQueryWithStepUp) recognizes the bare 403 from
      the normalized STEP_UP_REQUIRED and requests step-up; with no modal mounted here, cancelling
      surfaces STEP_UP_CANCELLED carrying requiredAuthLevel, which PROVES it was read as step-up
      rather than as a generic error. The elevate+replay flagship is pinned in
      features/auth/stepup-interceptor.test.tsx.
    */
    const { store } = hookWrapper();

    const pending = settle(
      store.dispatch(apiSlice.endpoints.revealAccountNumber.initiate(UUID)).unwrap(),
    );

    // The interceptor opened a step-up request; cancel it to unblock the replay path.
    await waitFor(() => expect(getStepUpSnapshot()).not.toBeNull());
    settleStepUp('cancelled');

    const denied = await pending;
    expect(denied.ok).toBe(false);
    if (denied.ok) throw new Error('unreachable');
    expect(denied.error).toMatchObject({
      status: 403,
      errorCode: 'STEP_UP_CANCELLED',
      requiredAuthLevel: 2,
    });
    // The bare 403 body never passed through toApiProblem: nothing from it leaked.
    expect(denied.error.title).toBeUndefined();
    expect(denied.error.traceId).toBeUndefined();
  });

  it('5 — withdraw 401 INVALID_PIN stays in-flow: ApiProblem to the caller, key dropped, auth state untouched', async () => {
    const keys: string[] = [];
    let call = 0;
    server.use(
      http.post('*/api/transactions/withdraw', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '(missing)');
        call += 1;
        if (call === 1)
          return problem({ status: 401, errorCode: 'INVALID_PIN', detail: 'Invalid PIN' });
        return HttpResponse.json(
          {
            data: {
              transaction: {
                id: '019f7b3f-0000-7000-8000-000000000e02',
                transactionNumber: 'TXN-W',
                type: 'Withdrawal',
                amount: 60,
                balanceAfter: 40,
                description: null,
                recipientAzureTag: null,
                senderAzureTag: null,
                status: 'Completed',
                createdAt: '2026-07-22T11:00:00.0000000Z',
              },
              newBalance: 40,
            },
            message: null,
          },
          { status: 200 },
        );
      }),
    );

    const { store, Wrapper } = hookWrapper();
    // Full flagship form: boot AUTHENTICATED, then prove the wrong-PIN 401 cannot expire
    // the session (D3 routes INVALID_PIN to the calling form, never to sessionExpired).
    seedMockSession();
    await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
    expect(store.getState().auth.status).toBe('authenticated');
    const { result } = renderHook(() => useWithdrawIntent(), { wrapper: Wrapper });

    const failed = await act(() => settle(result.current.submit({ accountId: UUID, amount: 10 })));
    expect(failed.ok).toBe(false);
    if (failed.ok) throw new Error('unreachable');
    expect(failed.error.errorCode).toBe('INVALID_PIN');

    // NOT a session problem: still authenticated, cache intact, no sessionExpired.
    expect(store.getState().auth.status).toBe('authenticated');

    // Key DROPPED: the corrected PIN changes the body bytes (D4 — re-key on errorCode).
    const second = await act(() => settle(result.current.submit({ accountId: UUID, amount: 10 })));
    expect(second.ok).toBe(true);
    expect(keys[1]).not.toBe(keys[0]);
  });

  it('6 — 409 IN_FLIGHT keeps the key for the next Retry; RESULT_UNKNOWN latches verifyRequired before any new key', async () => {
    const keys: string[] = [];
    let call = 0;
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '(missing)');
        call += 1;
        if (call === 1) return problem({ status: 409, errorCode: 'IDEMPOTENCY_IN_FLIGHT' });
        if (call === 2) return depositOk();
        if (call === 3) return problem({ status: 409, errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN' });
        return depositOk();
      }),
    );

    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useDepositIntent(), { wrapper: Wrapper });
    const body = { accountId: UUID, amount: 75 };

    const inFlight = await act(() => settle(result.current.submit(body)));
    expect(inFlight.ok).toBe(false);
    if (inFlight.ok) throw new Error('unreachable');
    expect(inFlight.error.errorCode).toBe('IDEMPOTENCY_IN_FLIGHT');

    // "Still processing…" → the user's Retry re-sends the SAME key. A fresh key here
    // would be a client-manufactured double-spend.
    const retried = await act(() => settle(result.current.submit(body)));
    expect(retried.ok).toBe(true);
    expect(keys[1]).toBe(keys[0]);

    const unknown = await act(() => settle(result.current.submit(body)));
    expect(unknown.ok).toBe(false);
    if (unknown.ok) throw new Error('unreachable');
    expect(unknown.error.errorCode).toBe('IDEMPOTENCY_RESULT_UNKNOWN');
    expect(result.current.verifyRequired).toBe(true);

    // No new key may exist until the flow's explicit action resets the intent (§2.3):
    const refused = await act(() => settle(result.current.submit(body)));
    expect(refused.ok).toBe(false);
    expect(call).toBe(3); // refused client-side — no HTTP request happened

    act(() => result.current.resetIntent());
    expect(result.current.verifyRequired).toBe(false);
    const fresh = await act(() => settle(result.current.submit(body)));
    expect(fresh.ok).toBe(true);
    expect(keys[3]).not.toBe(keys[2]);
  });

  /** Counts the deposits that reach the server, answering each with `answer`. */
  function countDeposits(answer: () => Response) {
    const sent = { count: 0 };
    server.use(
      http.post('*/api/transactions/deposit', () => {
        sent.count += 1;
        return answer();
      }),
    );
    return sent;
  }

  it('6b — RESULT_UNKNOWN that says applied: true is known to have gone through, and still sends nothing more', async () => {
    // The API read this key's record as committed (ADR-0009). The key is dropped and the check is
    // latched exactly as without the flag; what the flag adds is that the hook can say which.
    const sent = countDeposits(() => committedAnswerLost('/api/transactions/deposit'));
    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useDepositIntent(), { wrapper: Wrapper });
    const body = { accountId: UUID, amount: 75 };

    const first = await act(() => settle(result.current.submit(body)));
    expect(first.ok).toBe(false);
    if (first.ok) throw new Error('unreachable');
    expect(first.error).toMatchObject({
      status: 409,
      errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
      applied: true,
    });
    expect(result.current.keyRetained).toBe(false);
    expect(result.current.verifyRequired).toBe(true);

    const refused = await act(() => settle(result.current.submit(body)));
    expect(refused.ok).toBe(false);
    expect(sent.count).toBe(1); // refused client-side — no HTTP request happened

    expect(result.current.wentThrough).toBe(true);
  });

  it('6c — after applied: true, resetIntent does nothing: no new key can exist in this page or dialog', async () => {
    /*
      "It didn't go through — start over" is `resetIntent`, and over a payment the server has just
      called committed it is a second payment under a new key. A flow that showed the check view by
      mistake must find that button dead; a new payment needs a new mount.
    */
    const sent = countDeposits(() => committedAnswerLost('/api/transactions/deposit'));
    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useDepositIntent(), { wrapper: Wrapper });
    const body = { accountId: UUID, amount: 75 };

    const first = await act(() => settle(result.current.submit(body)));
    expect(first.ok).toBe(false);
    expect(result.current.verifyRequired).toBe(true);

    act(() => result.current.resetIntent());
    expect(result.current.verifyRequired).toBe(true);

    const again = await act(() => settle(result.current.submit(body)));
    expect(again.ok).toBe(false);
    expect(sent.count).toBe(1);
    expect(result.current.wentThrough).toBe(true);
  });

  it.each([
    ['409 RESULT_UNKNOWN that carries no applied', () => ({})],
    ['409 RESULT_UNKNOWN whose applied is false', () => ({ applied: false })],
  ])(
    '6d — %s asks for a check, and is not known to have gone through',
    async (_name, extensions) => {
      countDeposits(() =>
        problem({
          status: 409,
          errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
          extensions: extensions(),
        }),
      );
      const { Wrapper } = hookWrapper();
      const { result } = renderHook(() => useDepositIntent(), { wrapper: Wrapper });

      const first = await act(() => settle(result.current.submit({ accountId: UUID, amount: 75 })));
      expect(first.ok).toBe(false);
      expect(result.current.verifyRequired).toBe(true);
      expect(result.current.wentThrough).toBe(false);
    },
  );

  it('6e — a check the flow asks for itself is not known to have gone through', async () => {
    // `requireVerify`: the visitor edited the form while a key was held. Nothing was answered.
    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useDepositIntent(), { wrapper: Wrapper });

    act(() => result.current.requireVerify());

    expect(result.current.verifyRequired).toBe(true);
    expect(result.current.wentThrough).toBe(false);
  });

  it('6f — applied must be the boolean true: any other value is not known to have gone through', async () => {
    /*
      `problemBaseQuery` lets only a boolean through, so no real answer can carry this; the hook
      is handed it directly. "true" as a string is truthy, and a truthiness read would say the
      payment went through on a member the server never wrote as true.
    */
    const unwrap = vi.fn(() =>
      Promise.reject({ status: 409, errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN', applied: 'true' }),
    );
    const trigger: IdempotentTrigger<{ amount: number }, unknown> = () => ({ unwrap });
    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useIdempotentMutation(trigger), { wrapper: Wrapper });

    const first = await act(() => settle(result.current.submit({ amount: 5 })));
    expect(first.ok).toBe(false);
    expect(result.current.verifyRequired).toBe(true);
    expect(result.current.wentThrough).toBe(false);
  });

  it('6g — a 409 on a money send that names no code asks for a check before any new key', async () => {
    /*
      A body with a wrong-typed member is not trusted at all (`toApiProblem` falls back to an
      empty body), so this 409 reaches the hook with the code the SPA derives from the status,
      HTTP_409. It may have been IN_FLIGHT or RESULT_UNKNOWN: a send that may have landed.
      Dropping the key with no latch let the next press go out under a new key.
    */
    const sent = countDeposits(() =>
      problem({
        status: 409,
        errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
        extensions: { applied: 'true' },
      }),
    );
    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useDepositIntent(), { wrapper: Wrapper });
    const body = { accountId: UUID, amount: 75 };

    const first = await act(() => settle(result.current.submit(body)));
    expect(first.ok).toBe(false);
    if (first.ok) throw new Error('unreachable');
    expect(first.error).toMatchObject({ status: 409, errorCode: 'HTTP_409' });
    expect(first.error).not.toHaveProperty('applied');
    expect(result.current.keyRetained).toBe(false);

    expect(result.current.verifyRequired).toBe(true);
    const second = await act(() => settle(result.current.submit(body)));
    expect(second.ok).toBe(false);
    expect(sent.count).toBe(1);
    expect(result.current.wentThrough).toBe(false);
  });

  it.each([
    [
      '409 IN_FLIGHT, whose key is kept',
      { status: 409, errorCode: 'IDEMPOTENCY_IN_FLIGHT', applied: true },
      { keyRetained: true, verifyRequired: false },
    ],
    [
      'a 409 that names no code, which asks for a check',
      { status: 409, errorCode: 'HTTP_409', applied: true },
      { keyRetained: false, verifyRequired: true },
    ],
    [
      '422 KEY_REUSE, whose key is dropped',
      { status: 422, errorCode: 'IDEMPOTENCY_KEY_REUSE', applied: true },
      { keyRetained: false, verifyRequired: false },
    ],
  ])(
    '6h — applied: true under any other code is not known to have gone through: %s',
    async (_name, rejection, expected) => {
      /*
        Only RESULT_UNKNOWN can say that the payment went through. The API writes `applied: true`
        on no other answer (ADR-0009), so the hook is handed each of these directly, as in 6f. A
        read of the member alone, without the code, would put "went through" on the page, which
        every flow shows ahead of anything else: under IN_FLIGHT, over a send that is still
        running. What each code does to the key and to the check is unchanged by the member.
      */
      const unwrap = vi.fn(() => Promise.reject(rejection));
      const trigger: IdempotentTrigger<{ amount: number }, unknown> = () => ({ unwrap });
      const { Wrapper } = hookWrapper();
      const { result } = renderHook(() => useIdempotentMutation(trigger), { wrapper: Wrapper });

      const first = await act(() => settle(result.current.submit({ amount: 5 })));
      expect(first.ok).toBe(false);
      expect(result.current.keyRetained).toBe(expected.keyRetained);
      expect(result.current.verifyRequired).toBe(expected.verifyRequired);
      expect(result.current.wentThrough).toBe(false);
    },
  );

  it('7 — a money send with no answer ends at 65 s and keeps its key for the retry', async () => {
    const keys: string[] = [];
    let received = 0;
    server.use(
      http.post('*/api/transactions/deposit', ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key') ?? '(missing)');
        if (keys.length === 1) {
          received = Date.now();
          return never();
        }
        return depositOk();
      }),
    );
    installFakeClock();
    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useDepositIntent(), { wrapper: Wrapper });
    const body = { accountId: UUID, amount: 50 };

    let first!: ReturnType<typeof track<unknown>>;
    act(() => {
      first = track(result.current.submit(body));
    });
    await waitFor(() => expect(keys).toHaveLength(1));

    await advanceUntil(received, 65_100);
    await waitFor(() => expect(first.state).toBe('rejected'));
    expect(first.error).toMatchObject({
      status: 'NETWORK',
      errorCode: 'TIMEOUT_ERROR',
      detail: COPY.unavailable,
    });
    expect(result.current.keyRetained).toBe(true);

    const second = await act(() => settle(result.current.submit(body)));
    expect(second.ok).toBe(true);
    expect(keys).toHaveLength(2);
    expect(keys[1]).toBe(keys[0]);
  });

  it('8 — a money send rejected with no HTTP status asks for a check before any new key', async () => {
    /*
      A rejection with no status is a 2xx whose body failed its schema, or an abort: either way the
      request may have been acted on. Dropping the key quietly and letting the next press mint a
      new one is a second payment over an unknown first — so it latches verify-first, exactly as
      RESULT_UNKNOWN does.
    */
    const unwrap = vi.fn(() =>
      Promise.reject({ name: 'SchemaError', message: 'The answer did not match its schema.' }),
    );
    const trigger: IdempotentTrigger<{ amount: number }, unknown> = () => ({ unwrap });
    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useIdempotentMutation(trigger), { wrapper: Wrapper });

    const first = await act(() => settle(result.current.submit({ amount: 5 })));
    expect(first.ok).toBe(false);
    expect(result.current.keyRetained).toBe(false);
    expect(result.current.verifyRequired).toBe(true);

    const second = await act(() => settle(result.current.submit({ amount: 5 })));
    expect(second.ok).toBe(false);
    expect(unwrap).toHaveBeenCalledTimes(1);
  });

  it('8b — the money classifier words a send with no HTTP status as a check, a mint with none as a failure', () => {
    // The same rejection the hook latches on: no status at all.
    const noStatus = { name: 'Error', message: 'API success envelope carried no data.' };
    const opts = { messages: {}, fallback: 'Transfer failed. Please try again.' };

    // A send may have moved the money: the verify view, never "failed, try again".
    expect(
      classifyMoneyProblem(noStatus as unknown as ApiProblem, { ...opts, phase: 'send' }),
    ).toEqual({ kind: 'verify' });
    // A mint moved nothing, and keeps the flow's own sentence.
    expect(
      classifyMoneyProblem(noStatus as unknown as ApiProblem, { ...opts, phase: 'mint' }),
    ).toEqual({ kind: 'message', text: 'Transfer failed. Please try again.', scope: 'attempt' });
  });

  it('8c — a money send rejected with no HTTP status is not known to have gone through', async () => {
    // The same rejection as 8: it may have landed, and nothing says that it did.
    const unwrap = vi.fn(() =>
      Promise.reject({ name: 'SchemaError', message: 'The answer did not match its schema.' }),
    );
    const trigger: IdempotentTrigger<{ amount: number }, unknown> = () => ({ unwrap });
    const { Wrapper } = hookWrapper();
    const { result } = renderHook(() => useIdempotentMutation(trigger), { wrapper: Wrapper });

    const first = await act(() => settle(result.current.submit({ amount: 5 })));
    expect(first.ok).toBe(false);
    expect(result.current.verifyRequired).toBe(true);
    expect(result.current.wentThrough).toBe(false);
  });

  it('8d — the money classifier words a 409 that names no code as a check for a send, as a failure for a mint', () => {
    // What `toApiProblem` makes of a 409 whose body it could not trust: the status, and the code
    // derived from it. The hook latches the check for it (6g), so the words must match the view.
    const unnamed: ApiProblem = { status: 409, errorCode: 'HTTP_409' };
    const opts = { messages: {}, fallback: 'Transfer failed. Please try again.' };

    // A mint holds no key and moved nothing: the flow's own sentence, as before.
    expect(classifyMoneyProblem(unnamed, { ...opts, phase: 'mint' })).toEqual({
      kind: 'message',
      text: 'Transfer failed. Please try again.',
      scope: 'attempt',
    });
    // A send may have moved the money: the verify view, never "failed, try again".
    expect(classifyMoneyProblem(unnamed, { ...opts, phase: 'send' })).toEqual({ kind: 'verify' });
  });

  describe('what RESULT_UNKNOWN does to the cache', () => {
    /*
      The flow tells the visitor to look at their transactions, and with `applied: true` it tells
      them the payment went through: the balance and the list the page holds are from before it.
      So the four money mutations invalidate on this code what they invalidate on success. Measured
      on the running app before this: behind a deposit that had landed, the dashboard kept the old
      balance through the loss, the two minutes and the check view.

      Asserted at the store, with a reader of each list mounted: a transfer page reads accounts
      only and a dialog reads nothing, so a refetch asserted there would have nothing to refetch.
    */
    const MAIN = mockState.accounts[0].id;
    const SAVINGS = mockState.accounts[1].id;
    const HISTORY = { page: 1, pageSize: 20 };

    function useListReaders() {
      return { accounts: useGetAccountsQuery(), transactions: useGetTransactionsQuery(HISTORY) };
    }

    /** Counts the reads of the two lists, letting each one through to the mock. */
    function countListReads() {
      const reads = { accounts: 0, transactions: 0 };
      server.use(
        http.get('*/api/accounts', () => {
          reads.accounts += 1;
          return undefined;
        }),
        http.get('*/api/transactions', () => {
          reads.transactions += 1;
          return undefined;
        }),
      );
      return reads;
    }

    async function mountReaders(reads: { accounts: number; transactions: number }) {
      const { store, Wrapper } = hookWrapper();
      const { result } = renderHook(() => useListReaders(), { wrapper: Wrapper });
      await waitFor(() => {
        expect(result.current.accounts.isSuccess).toBe(true);
        expect(result.current.transactions.isSuccess).toBe(true);
      });
      expect(reads).toEqual({ accounts: 1, transactions: 1 });
      return store;
    }

    /**
     * One more request, sent now and answered: whatever the store sent before it has reached its
     * handler by then, so a count read after this is not a count read too soon.
     */
    async function afterARoundTrip(store: TestStore) {
      const probe = store.dispatch(apiSlice.endpoints.getAccount.initiate(SAVINGS));
      await probe.unwrap();
      probe.unsubscribe();
    }

    const SENDS: {
      name: string;
      path: string;
      /** The caller's accounts whose balance the send changes. */
      moves: string[];
      send: (store: TestStore) => Promise<unknown>;
    }[] = [
      {
        name: 'deposit',
        path: '/api/transactions/deposit',
        moves: [MAIN],
        send: (store) =>
          store
            .dispatch(
              apiSlice.endpoints.deposit.initiate({
                idempotencyKey: crypto.randomUUID(),
                body: { accountId: MAIN, amount: 5 },
              }),
            )
            .unwrap(),
      },
      {
        name: 'withdrawal',
        path: '/api/transactions/withdraw',
        moves: [MAIN],
        send: (store) =>
          store
            .dispatch(
              apiSlice.endpoints.withdraw.initiate({
                idempotencyKey: crypto.randomUUID(),
                body: { accountId: MAIN, amount: 5 },
                stepUpAuthorizationId: crypto.randomUUID(),
              }),
            )
            .unwrap(),
      },
      {
        name: 'transfer',
        path: '/api/transfers',
        // The other side is another user's account, which this visitor never reads.
        moves: [MAIN],
        send: (store) =>
          store
            .dispatch(
              apiSlice.endpoints.transfer.initiate({
                idempotencyKey: crypto.randomUUID(),
                body: { fromAccountId: MAIN, recipientAzureTag: 'friend', amount: 5 },
                stepUpAuthorizationId: crypto.randomUUID(),
              }),
            )
            .unwrap(),
      },
      {
        name: 'transfer between own accounts',
        path: '/api/transfers/internal',
        moves: [MAIN, SAVINGS],
        send: (store) =>
          store
            .dispatch(
              apiSlice.endpoints.transferInternal.initiate({
                idempotencyKey: crypto.randomUUID(),
                body: { fromAccountId: MAIN, toAccountId: SAVINGS, amount: 5 },
                stepUpAuthorizationId: crypto.randomUUID(),
              }),
            )
            .unwrap(),
      },
    ];

    const ANSWERS: { flag: string; answer: (path: string) => Response }[] = [
      { flag: 'with applied: true', answer: (path) => committedAnswerLost(path) },
      {
        flag: 'without applied',
        answer: (path) =>
          problem({ status: 409, errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN', instance: path }),
      },
    ];

    it.each(SENDS.flatMap((send) => ANSWERS.map((answer) => ({ ...send, ...answer }))))(
      '9 — a $name answered RESULT_UNKNOWN $flag reads the accounts and the transactions once more',
      async ({ path, send, answer }) => {
        const reads = countListReads();
        const store = await mountReaders(reads);
        server.use(http.post(`*${path}`, () => answer(path)));

        const outcome = await settle(send(store));
        expect(outcome.ok).toBe(false);
        if (outcome.ok) throw new Error('unreachable');
        expect(outcome.error.errorCode).toBe('IDEMPOTENCY_RESULT_UNKNOWN');

        await waitFor(() => expect(reads).toEqual({ accounts: 2, transactions: 2 }));
        // Once more, not twice.
        await afterARoundTrip(store);
        expect(reads).toEqual({ accounts: 2, transactions: 2 });
      },
    );

    function useAccountReaders() {
      return { main: useGetAccountQuery(MAIN), savings: useGetAccountQuery(SAVINGS) };
    }

    /** Counts the reads of each account on its own, letting each one through to the mock. */
    function countAccountReads() {
      const reads: Record<string, number> = { [MAIN]: 0, [SAVINGS]: 0 };
      server.use(
        http.get('*/api/accounts/:id', ({ params }) => {
          const id = String(params.id);
          reads[id] = (reads[id] ?? 0) + 1;
          return undefined;
        }),
      );
      return reads;
    }

    it.each(SENDS.flatMap((send) => ANSWERS.map((answer) => ({ ...send, ...answer }))))(
      '9d — a $name answered RESULT_UNKNOWN $flag reads again each account it moves, and no other',
      async ({ path, send, answer, moves }) => {
        /*
          Test 9 counts the accounts list, and the list carries a tag for every account: it is
          read again when any one of them is invalidated, so it cannot tell one account from two.
          A reader of a single account holds that account's tag only. A transfer between own
          accounts moves both balances, and a page reading the receiving one would keep the old
          figure if only the sending one were refreshed.
        */
        const reads = countAccountReads();
        const { store, Wrapper } = hookWrapper();
        const { result } = renderHook(() => useAccountReaders(), { wrapper: Wrapper });
        await waitFor(() => {
          expect(result.current.main.isSuccess).toBe(true);
          expect(result.current.savings.isSuccess).toBe(true);
        });
        expect(reads).toEqual({ [MAIN]: 1, [SAVINGS]: 1 });
        server.use(http.post(`*${path}`, () => answer(path)));

        const outcome = await settle(send(store));
        expect(outcome.ok).toBe(false);
        if (outcome.ok) throw new Error('unreachable');
        expect(outcome.error.errorCode).toBe('IDEMPOTENCY_RESULT_UNKNOWN');

        const expected = {
          [MAIN]: moves.includes(MAIN) ? 2 : 1,
          [SAVINGS]: moves.includes(SAVINGS) ? 2 : 1,
        };
        await waitFor(() => expect(reads).toEqual(expected));
        // Once more, not twice. The list is the request sent after them here: both single
        // accounts have a reader, so asking for one of those again would send nothing.
        const probe = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
        await probe.unwrap();
        probe.unsubscribe();
        expect(reads).toEqual(expected);
      },
    );

    it.each([
      [
        '409 IN_FLIGHT',
        () => problem({ status: 409, errorCode: 'IDEMPOTENCY_IN_FLIGHT' }),
        'IDEMPOTENCY_IN_FLIGHT',
      ],
      [
        '422 KEY_REUSE',
        () => problem({ status: 422, errorCode: 'IDEMPOTENCY_KEY_REUSE' }),
        'IDEMPOTENCY_KEY_REUSE',
      ],
    ])('9b — a deposit answered %s reads neither list again', async (_name, answer, errorCode) => {
      // IN_FLIGHT committed nothing that an answer will not report; a refusal changed nothing.
      const reads = countListReads();
      const store = await mountReaders(reads);
      server.use(http.post('*/api/transactions/deposit', answer));

      const outcome = await settle(SENDS[0].send(store));
      expect(outcome.ok).toBe(false);
      if (outcome.ok) throw new Error('unreachable');
      expect(outcome.error.errorCode).toBe(errorCode);

      await afterARoundTrip(store);
      expect(reads).toEqual({ accounts: 1, transactions: 1 });
    });

    it('9c — a cached history nobody is reading is dropped, so the history page reads it afresh', async () => {
      /*
        "View History" is the one action the visitor is given. The history's list has no reader
        on a transfer page or under a dialog, and an invalidated entry with no reader is not
        refetched: it is removed. Without the invalidation, a list cached in the last minute would
        be shown on arrival, without the movement the page had just said went through.
      */
      const { store } = hookWrapper();
      const read = store.dispatch(apiSlice.endpoints.getTransactions.initiate(HISTORY));
      await read.unwrap();
      read.unsubscribe();
      const cached = () => apiSlice.endpoints.getTransactions.select(HISTORY)(store.getState());
      expect(cached().status).toBe('fulfilled');

      server.use(
        http.post('*/api/transactions/deposit', () =>
          committedAnswerLost('/api/transactions/deposit'),
        ),
      );
      const outcome = await settle(SENDS[0].send(store));
      expect(outcome.ok).toBe(false);

      await waitFor(() => expect(cached().status).toBe('uninitialized'));
    });
  });
});
