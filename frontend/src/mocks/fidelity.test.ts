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

describe('failure ordering matches the service, not the handler that was easiest to write', () => {
  beforeEach(() => {
    resetMockState();
  });

  it('withdraw resolves the account BEFORE the PIN, and spends no attempt on a bad id', async () => {
    // `WithdrawAsync` opens with GetAccountWithOwnershipCheckAsync — above the PIN-set check, the
    // lockout window and the compare. With the account check last, a fumbled id burned a PIN
    // attempt and could lock you out of a mock the real API would never have let you reach.
    const res = await fetch('/api/transactions/withdraw', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': '3f2504e0-4f89-41d3-9a0c-0305e82c3390',
      },
      body: JSON.stringify({ accountId: 'no-such-account', amount: 10, pin: '000000' }),
    });

    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
    expect(mockState.pinAttempts).toBe(0);
  });

  it('transfer resolves the source account BEFORE the self-transfer guard', async () => {
    // TransferAsync's first statement is the source-account lookup; the self-transfer guard is
    // three statements later. Sending to your own handle FROM a bad id must be the 404.
    mockState.authLevel = 2;
    const res = await fetch('/api/transfers', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': '3f2504e0-4f89-41d3-9a0c-0305e82c3391',
      },
      body: JSON.stringify({
        fromAccountId: 'no-such-account',
        recipientAzureTag: mockState.session?.azureTag ?? 'demo_user',
        amount: 10,
      }),
    });

    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
  });
});

describe('the query parameters the mock used to ignore', () => {
  beforeEach(() => {
    resetMockState();
  });

  it('GET /api/transactions honours FromDate and ToDate, inclusively', async () => {
    const all = await fetch('/api/transactions?Page=1&PageSize=100').then((r) => r.json());
    const day = '2026-07-19';
    const scoped = await fetch(
      `/api/transactions?Page=1&PageSize=100&FromDate=${day}T00:00:00Z&ToDate=${day}T23:59:59Z`,
    ).then((r) => r.json());

    const expected = mockState.transactions.filter((t) => t.createdAt.startsWith(day));
    expect(expected.length).toBeGreaterThan(0);
    expect(scoped.pagination.totalItems).toBe(expected.length);
    // Strictly fewer than the unfiltered feed — proof the window did something.
    expect(scoped.pagination.totalItems).toBeLessThan(all.pagination.totalItems);
  });

  it('reports ZERO pages for an empty result, not one', async () => {
    // PaginatedResponse computes Ceiling(0/size) = 0, and the no-accounts branch hard-codes
    // TotalPages = 0. Saying 1 tells a client there is a page to render when there is not.
    const body = await fetch(
      '/api/transactions?Page=1&PageSize=20&FromDate=2001-01-01T00:00:00Z&ToDate=2001-01-02T00:00:00Z',
    ).then((r) => r.json());

    expect(body.data).toEqual([]);
    expect(body.pagination.totalItems).toBe(0);
    expect(body.pagination.totalPages).toBe(0);
  });

  it('summarises the current calendar month by default, not all of time', async () => {
    // `filter.FromDate ?? new DateTime(now.Year, now.Month, 1)`. The mock defaulted to 1970, so
    // the dashboard card labelled "<month> so far" was reporting every transaction ever recorded.
    const monthStart = new Date(Date.UTC(new Date().getUTCFullYear(), new Date().getUTCMonth(), 1));
    const body = await fetch('/api/transactions/summary').then((r) => r.json());

    expect(Date.parse(body.data.fromDate)).toBe(monthStart.getTime());
  });

  it('422s a window that ends before it starts', async () => {
    const res = await fetch(
      '/api/transactions/summary?FromDate=2026-07-20T00:00:00Z&ToDate=2026-07-01T00:00:00Z',
    );

    expect(res.status).toBe(422);
    expect((await res.json()).errorCode).toBe('INVALID_DATE_RANGE');
  });
});

describe('the two shapes every error inherits', () => {
  beforeEach(() => {
    resetMockState();
  });

  it('a validation 400 says Validation Failed, not Bad Request with an empty detail', async () => {
    // `ValidationExceptionHandler` writes its OWN ProblemDetails — title "Validation Failed",
    // detail "One or more validation errors occurred." Everything the mock built carried the
    // generic 400 title and detail: '', so a surface rendering `problem.detail` showed nothing
    // for the one error class that always has something to say.
    const res = await fetch('/api/transactions?Page=0');

    expect(res.status).toBe(400);
    const body = await res.json();
    expect(body.title).toBe('Validation Failed');
    expect(body.detail).toBe('One or more validation errors occurred.');
    expect(body.errorCode).toBeUndefined(); // validation 400s carry none
  });

  it('keys pagination errors by property, and caps PageSize at 100', async () => {
    // `TransactionFilter` is `[Range(1, 100)]` on PageSize with that exact message, and model
    // validation keys by property name. The mock invented a `pagination` key, a sentence no
    // validator produces, and NO upper bound — so paging 1000 rows worked here and 400s in prod.
    const body = await fetch('/api/transactions?PageSize=1000').then((r) => r.json());

    expect(body.errors.pageSize).toEqual(['PageSize must be between 1 and 100.']);
    expect(body.errors).not.toHaveProperty('pagination');

    const ok = await fetch('/api/transactions?PageSize=100');
    expect(ok.status).toBe(200);
  });
});
