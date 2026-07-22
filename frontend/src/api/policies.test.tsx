import type { PropsWithChildren } from 'react';
import { act, renderHook, waitFor } from '@testing-library/react';
import { Provider } from 'react-redux';
import { getStepUpSnapshot, settleStepUp } from '../features/auth/stepUpController';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../mocks/server';
import { problem } from '../mocks/problem';
import { seedMockSession } from '../mocks/state';
import { makeTestStore } from '../test/renderWithProviders';
import { apiSlice, useDepositMutation, useWithdrawMutation } from '../features/api/apiSlice';
import { useIdempotentMutation } from '../hooks/useIdempotentMutation';
import type { ApiProblem } from './problemBaseQuery';

/**
 * The six flagship policy tests (DECISIONS §2.4) — the data-layer contract as executable
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

function depositOk(status = 201) {
  return HttpResponse.json(
    { data: { transaction: { transactionNumber: 'TXN-TEST' }, newBalance: 100 }, message: null },
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

describe('data-layer policies (flagship, DECISIONS §2.4)', () => {
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

  it(
    '2 — auto-retries queries on 503 up to 3 total attempts; never mutations',
    { timeout: 15000 },
    async () => {
      let getCalls = 0;
      let postCalls = 0;
      server.use(
        http.get('*/api/accounts', () => {
          getCalls += 1;
          return problem({ status: 503 });
        }),
        http.post('*/api/transactions/deposit', () => {
          postCalls += 1;
          return problem({ status: 503 });
        }),
      );

      const { store } = hookWrapper();

      const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
      await expect(query.unwrap()).rejects.toMatchObject({ status: 503 });
      expect(getCalls).toBe(3);

      const mutation = store.dispatch(
        apiSlice.endpoints.deposit.initiate({
          idempotencyKey: crypto.randomUUID(),
          body: { accountId: UUID, amount: 5 },
        }),
      );
      await expect(mutation.unwrap()).rejects.toMatchObject({ status: 503 });
      expect(postCalls).toBe(1);
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

  it('4 — the step-up 403 (X-Auth-Level-Required header, D2) drives the interceptor; cancel → STEP_UP_CANCELLED', async () => {
    // The stateful transfers handler answers with the bare-token 403 at authLevel 1. The
    // interceptor (baseQueryWithStepUp) recognizes it from the normalized STEP_UP_REQUIRED
    // and requests step-up. With no modal mounted here, cancel it — the surfaced error is
    // STEP_UP_CANCELLED (carrying requiredAuthLevel), which PROVES the 403 was recognized as
    // step-up, not a generic error. The elevate+replay flagship (byte-identical, same key,
    // D21) is pinned in features/auth/stepup-interceptor.test.tsx.
    const { store } = hookWrapper();

    const pending = settle(
      store
        .dispatch(
          apiSlice.endpoints.transfer.initiate({
            idempotencyKey: crypto.randomUUID(),
            body: { fromAccountId: UUID, recipientAzureTag: 'john_doe', amount: 25 },
          }),
        )
        .unwrap(),
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
          { data: { transaction: { transactionNumber: 'TXN-W' }, newBalance: 40 }, message: null },
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

    const failed = await act(() =>
      settle(result.current.submit({ accountId: UUID, amount: 10, pin: '000000' })),
    );
    expect(failed.ok).toBe(false);
    if (failed.ok) throw new Error('unreachable');
    expect(failed.error.errorCode).toBe('INVALID_PIN');

    // NOT a session problem: still authenticated, cache intact, no sessionExpired.
    expect(store.getState().auth.status).toBe('authenticated');

    // Key DROPPED: the corrected PIN changes the body bytes (D4 — re-key on errorCode).
    const second = await act(() =>
      settle(result.current.submit({ accountId: UUID, amount: 10, pin: '123456' })),
    );
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
});
