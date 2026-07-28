import { beforeEach, describe, expect, it } from 'vitest';
import { MAIN_ACCOUNT_ID, mockState, resetMockState } from './state';

/**
 * The mock stands in for the API everywhere the frontend is tested, so a gap in it is a gap in the
 * oracle. These pin the two things an audit found that nothing else could have.
 */

describe('the mock reports its own gaps', () => {
  beforeEach(() => {
    resetMockState();
  });

  it('answers 501 for an /api route it does not handle, naming the route', async () => {
    // `sessionActivity` is `http.all('*/api/*')` returning undefined so the real handler runs after
    // it. MSW counts that as a MATCH, so `onUnhandledRequest: 'error'` never fires and the request
    // escapes to the network — measured before this fix as a bare `fetch failed`, identical to an
    // outage. Two real API routes sat unmocked behind exported hooks precisely because of it.
    const res = await fetch('/api/nothing-handles-this');

    expect(res.status).toBe(501);
    const body = await res.json();
    expect(body.errorCode).toBe('MOCK_HANDLER_MISSING');
    expect(body.detail).toContain('GET /api/nothing-handles-this');
  });

  it('does not shadow a route that IS handled', async () => {
    // The sentinel is last, so every real handler still wins. If this fails, the sentinel has been
    // moved up the array and is now answering for everything.
    const res = await fetch('/api/accounts');
    expect(res.status).toBe(200);
  });
});

describe('GET /api/accounts/{id}', () => {
  beforeEach(() => {
    resetMockState();
  });

  it('returns the account enveloped, like ApiResponse<T>.Success', async () => {
    const res = await fetch(`/api/accounts/${MAIN_ACCOUNT_ID}`);

    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.data.id).toBe(MAIN_ACCOUNT_ID);
    expect(body.message).toBeNull();
    // Masked server-side by AccountMapper — the full number never leaves the API on this route.
    expect(body.data.accountNumber).toMatch(/\*\*\*\*/);
  });

  it('404s an unknown id the way NotFoundException words it', async () => {
    const res = await fetch('/api/accounts/019f7b3f-0000-7000-8000-0000000000ff');

    expect(res.status).toBe(404);
    const body = await res.json();
    expect(body.errorCode).toBe('ACCOUNT_NOT_FOUND');
  });
});

describe('GET /api/accounts/{id}/balance', () => {
  beforeEach(() => {
    resetMockState();
  });

  const balance = (query = '') =>
    fetch(`/api/accounts/${MAIN_ACCOUNT_ID}/balance${query}`).then((r) => r.json());

  it('with no `at`, reports the current balance and says it is not historical', async () => {
    const account = mockState.accounts.find((a) => a.id === MAIN_ACCOUNT_ID);
    const body = await balance();

    expect(body.data.balance).toBe(account?.balance);
    expect(body.data.isHistorical).toBe(false);
    expect(body.data.currency).toBe('EUR');
  });

  it('treats an `at` in the FUTURE as current, not as historical', async () => {
    // `GetBalanceAsync`: `if (atTime == null || atTime >= DateTime.UtcNow)` — one branch covers
    // both. The obvious reading, that any `at` means historical, is wrong.
    const account = mockState.accounts.find((a) => a.id === MAIN_ACCOUNT_ID);
    const body = await balance('?at=2099-01-01T00:00:00Z');

    expect(body.data.balance).toBe(account?.balance);
    expect(body.data.isHistorical).toBe(false);
  });

  it('reports the balance the ledger actually left at that moment', async () => {
    // Derived from the seed rather than hard-coded: the answer must be the `balanceAfter` of the
    // newest entry at or before `at`, which is the same property the seed guarantees.
    const at = '2026-07-19T00:00:00Z';
    const expected = mockState.transactions
      .filter((t) => t.accountId === MAIN_ACCOUNT_ID && Date.parse(t.createdAt) <= Date.parse(at))
      .sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0];

    const body = await balance(`?at=${at}`);

    expect(expected).toBeDefined();
    expect(body.data.balance).toBe(expected.balanceAfter);
    expect(body.data.isHistorical).toBe(true);
  });

  it('reports 0 — not the opening balance — before the account had any entry', async () => {
    const body = await balance('?at=2020-01-01T00:00:00Z');

    expect(body.data.balance).toBe(0);
    expect(body.data.isHistorical).toBe(true);
  });
});
