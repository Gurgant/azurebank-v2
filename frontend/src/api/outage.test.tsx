import { cleanup, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../mocks/server';
import { problem, serviceUnavailable } from '../mocks/problem';
import { makeTestStore, renderWithProviders } from '../test/renderWithProviders';
import {
  COPY,
  advance,
  advanceUntil,
  committedAnswerLost,
  never,
  track,
  installFakeClock,
} from '../test/outage';
import { apiSlice } from '../features/api/apiSlice';
import { AccountsPage } from '../pages/AccountsPage';
import { CONNECTION_FAILED } from './problemMessages';

/**
 * What the data layer makes of an outage, seen where a visitor sees it.
 *
 * A 503 is one sentence on every surface, whoever wrote it — the API, the BFF, a renewal that
 * could not finish, an ingress page with no JSON — because a server's own sentence ("Try again
 * shortly.", "The session could not be renewed…") promises a time or names a session, and a 5xx
 * must do neither. A request with no answer ends at 65 s, past the BFF's worst answer (5 s renewal
 * wait + 55 s to the API), instead of spinning for ever.
 *
 * Every test here lets the retries settle on a fake clock before it looks, so what it reads is the
 * final state, not a moment between two attempts.
 */

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

function renderAccounts() {
  return renderWithProviders(<AccountsPage />, { routerEntries: ['/accounts'] });
}

/** The accounts page's error bar: the MessageBar that holds the Retry button. */
function accountsBar(): HTMLElement {
  const bar = screen.getByRole('button', { name: 'Retry' }).closest<HTMLElement>('.fui-MessageBar');
  expect(bar).not.toBeNull();
  return bar as HTMLElement;
}

describe('an outage reads as one sentence, whoever answered', () => {
  it("a 503 from the API reads as the SPA's sentence, not the server's", async () => {
    server.use(
      http.get('*/api/accounts', () =>
        serviceUnavailable({ via: 'api', instance: '/api/accounts' }),
      ),
    );
    installFakeClock();
    renderAccounts();
    // Past every retry either policy could make: 3 attempts within 2.52 s, or 1 after 10 s.
    await advance(12_000);

    expect(await screen.findByText(COPY.unavailable, { exact: false })).toBeInTheDocument();
    expect(screen.queryByText(/shortly/i)).not.toBeInTheDocument();
  });

  it('the session-renewal 503 never puts the word "session" on screen', async () => {
    server.use(
      http.get('*/api/accounts', () =>
        serviceUnavailable({
          via: 'bff',
          detail: 'The session could not be renewed just now. Try again shortly.',
          retryAfterSeconds: 3,
          instance: '/api/accounts',
        }),
      ),
    );
    installFakeClock();
    renderAccounts();
    await advance(12_000);

    expect(await screen.findByText(COPY.unavailable, { exact: false })).toBeInTheDocument();
    expect(accountsBar()).not.toHaveTextContent(/session/i);
  });

  it('a 503 without JSON (an ingress page) is the same outage, retried once', async () => {
    let calls = 0;
    server.use(
      http.get('*/api/accounts', () => {
        calls += 1;
        return serviceUnavailable({ via: 'api', contentType: 'text/plain' });
      }),
    );
    installFakeClock();
    renderAccounts();
    await advance(12_000);

    expect(await screen.findByText(COPY.unavailable, { exact: false })).toBeInTheDocument();
    expect(calls).toBe(2);
  });

  it('a request that never reached a server never shows the raw fetch error', async () => {
    server.use(http.get('*/api/accounts', () => HttpResponse.error()));
    installFakeClock();
    renderAccounts();
    await advance(12_000);

    expect(await screen.findByText(CONNECTION_FAILED, { exact: false })).toBeInTheDocument();
    expect(screen.queryByText(/TypeError/)).not.toBeInTheDocument();
  });

  it('a 500 still shows its own detail — the outage sentence belongs to a 503', async () => {
    server.use(
      http.get('*/api/accounts', () =>
        problem({
          status: 500,
          errorCode: 'INTERNAL_ERROR',
          detail: 'An unexpected error occurred. Please try again later.',
        }),
      ),
    );
    renderAccounts();

    expect(
      await screen.findByText('An unexpected error occurred. Please try again later.', {
        exact: false,
      }),
    ).toBeInTheDocument();
    expect(screen.queryByText(COPY.unavailable, { exact: false })).not.toBeInTheDocument();
  });
});

describe('the answer reaches the caller whole', () => {
  it('`applied: false` on a money send reaches the caller', async () => {
    server.use(
      http.post('*/api/transfers', () =>
        serviceUnavailable({ via: 'api', applied: false, instance: '/api/transfers' }),
      ),
    );
    const store = makeTestStore();

    await expect(
      store
        .dispatch(
          apiSlice.endpoints.transfer.initiate({
            idempotencyKey: crypto.randomUUID(),
            body: {
              fromAccountId: '11111111-2222-4333-8444-555555555555',
              recipientAzureTag: 'friend',
              amount: 5,
            },
            stepUpAuthorizationId: crypto.randomUUID(),
          }),
        )
        .unwrap(),
    ).rejects.toMatchObject({ status: 503, errorCode: 'SERVICE_UNAVAILABLE', applied: false });
  });

  it('an `applied` that is not true or false is not passed on', async () => {
    // "false" as a string would read as "not false" at every consumer, but the caller would hold a
    // value its type says cannot exist; the body is not trusted, as for any wrong-typed member.
    server.use(
      http.post('*/api/transfers', () =>
        HttpResponse.json(
          {
            title: 'Service Unavailable',
            status: 503,
            detail: 'The service is temporarily unavailable, and nothing was changed.',
            errorCode: 'SERVICE_UNAVAILABLE',
            applied: 'false',
          },
          { status: 503 },
        ),
      ),
    );
    const store = makeTestStore();

    const refused = await store
      .dispatch(
        apiSlice.endpoints.transfer.initiate({
          idempotencyKey: crypto.randomUUID(),
          body: {
            fromAccountId: '11111111-2222-4333-8444-555555555555',
            recipientAzureTag: 'friend',
            amount: 5,
          },
          stepUpAuthorizationId: crypto.randomUUID(),
        }),
      )
      .unwrap()
      .then(
        () => undefined,
        (error: unknown) => error,
      );
    expect(refused).toMatchObject({ status: 503, detail: COPY.unavailable });
    expect(refused).not.toHaveProperty('applied');
  });

  it('`applied: true` on a 409 reaches the caller', async () => {
    // The member is carried for any status, so the 409 that says a payment went through (ADR-0009)
    // arrives whole. What reads it is the idempotency hook.
    server.use(http.post('*/api/transfers', () => committedAnswerLost('/api/transfers')));
    const store = makeTestStore();

    await expect(
      store
        .dispatch(
          apiSlice.endpoints.transfer.initiate({
            idempotencyKey: crypto.randomUUID(),
            body: {
              fromAccountId: '11111111-2222-4333-8444-555555555555',
              recipientAzureTag: 'friend',
              amount: 5,
            },
            stepUpAuthorizationId: crypto.randomUUID(),
          }),
        )
        .unwrap(),
    ).rejects.toMatchObject({
      status: 409,
      errorCode: 'IDEMPOTENCY_RESULT_UNKNOWN',
      applied: true,
    });
  });
});

describe('no request waits for ever', () => {
  it('a request with no answer ends at 65 s as an outage, and is not retried', async () => {
    let calls = 0;
    let received = 0;
    let signal: AbortSignal | undefined;
    server.use(
      http.get('*/api/accounts', ({ request }) => {
        calls += 1;
        received = Date.now();
        signal = request.signal;
        return never();
      }),
    );
    installFakeClock();
    const store = makeTestStore();
    const start = Date.now();
    const query = store.dispatch(apiSlice.endpoints.getAccounts.initiate());
    const outcome = track(query.unwrap());
    await waitFor(() => expect(calls).toBe(1));

    await advanceUntil(start, 64_900);
    expect(outcome.state).toBe('pending');

    await advanceUntil(received, 65_100);
    await waitFor(() => expect(outcome.state).toBe('rejected'));
    expect(outcome.error).toMatchObject({
      status: 'NETWORK',
      errorCode: 'TIMEOUT_ERROR',
      detail: COPY.unavailable,
    });
    expect(signal?.aborted).toBe(true);

    await advanceUntil(received, 130_000);
    expect(calls).toBe(1);
    query.unsubscribe();
  });
});
