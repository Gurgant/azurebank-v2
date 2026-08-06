import { beforeEach, describe, expect, it } from 'vitest';
import { MAIN_ACCOUNT_ID, mockState, resetMockState, seedMockSession } from './state';
import { transactionSummarySchema } from '../api/responseSchemas';

/**
 * The mock stands in for the API everywhere the frontend is tested, so a gap in it is a gap in the
 * oracle. These pin the two things an audit found that nothing else could have.
 */

describe('the mock reports its own gaps', () => {
  beforeEach(() => {
    resetMockState();
    // Re-seed: the shared setup signs in before every test, and a local reset undoes it. `/api/*`
    // is session-gated now, so without this every request here is a 401.
    seedMockSession();
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
    seedMockSession();
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
    seedMockSession();
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
    // Derived from the seed, not hardcoded: the ledger is re-dated into the CURRENT month, so a
    // fixed July instant would select nothing and this would assert against `undefined`.
    const at = mockState.transactions[2].createdAt;
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
    seedMockSession();
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
    seedMockSession();
  });

  it('GET /api/transactions honours FromDate and ToDate, inclusively', async () => {
    const all = await fetch('/api/transactions?Page=1&PageSize=100').then((r) => r.json());
    // Same reason: take a day the seed actually occupies rather than naming one.
    const day = mockState.transactions[2].createdAt.slice(0, 10);
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

  it('400s an explicitly inverted PAIR, because model validation sees it first', async () => {
    // This asserted 422 until the real stack was asked. `TransactionSummaryFilter` is an
    // `IValidatableObject`, so a pair that is provided AND inverted never reaches the action —
    // and the framework reports it on both members, since the rule names both. The backend's own
    // `Summary_WithInvertedExplicitRange_ReturnsBadRequest` had said 400 all along; the mock said
    // 422 and this test pinned the mock.
    const res = await fetch(
      '/api/transactions/summary?FromDate=2026-07-20T00:00:00Z&ToDate=2026-07-01T00:00:00Z',
    );

    expect(res.status).toBe(400);
    const body = await res.json();
    expect(body.errors.FromDate).toEqual(['FromDate must be earlier than or equal to ToDate.']);
    expect(body.errors.ToDate).toEqual(['FromDate must be earlier than or equal to ToDate.']);
  });

  it('422s a RESOLVED window that ends before it starts', async () => {
    // The other tier, and the only one the service can reach: ToDate is omitted so it defaults to
    // `now`, which a future FromDate is already past. Model validation cannot see this — it only
    // ever sees the pair the caller actually sent.
    const future = new Date(Date.now() + 10 * 86_400_000).toISOString();
    const res = await fetch(`/api/transactions/summary?FromDate=${future}`);

    expect(res.status).toBe(422);
    expect((await res.json()).errorCode).toBe('INVALID_DATE_RANGE');
  });
});

describe('the two shapes every error inherits', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  it('a model-state 400 uses the FRAMEWORK envelope, with no detail at all', async () => {
    /*
      This asserted "Validation Failed" + a detail, which is the OTHER envelope. Measured against
      the running API on 2026-07-31: `TransactionController` injects validators only for deposit
      and withdraw, so the list action never throws ValidationException and never reaches
      `ValidationExceptionHandler`. Its 400 comes from `[ApiController]` model state, rendered by
      the framework:

        {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1",
         "title":"One or more validation errors occurred.","status":400,"errors":{...},
         "traceId":"00-<32hex>-<16hex>-01"}

      No `detail` member — which matters, because `HistoryPage` renders `problem.detail` for exactly
      this endpoint and showed a sentence under MSW that production never sends. The endpoints that
      DO have a validator keep the other envelope; see the same-account transfer test.
    */
    const res = await fetch('/api/transactions?Page=0');

    expect(res.status).toBe(400);
    const body = await res.json();
    expect(body.title).toBe('One or more validation errors occurred.');
    expect(body.detail).toBeUndefined();
    expect(body.errorCode).toBeUndefined(); // validation 400s carry none
  });

  it('keys pagination errors by property, and caps PageSize at 100', async () => {
    // `TransactionFilter` is `[Range(1, 100)]` on PageSize with that exact message, and model
    // validation keys by property name. The mock invented a `pagination` key, a sentence no
    // validator produces, and NO upper bound — so paging 1000 rows worked here and 400s in prod.
    const body = await fetch('/api/transactions?PageSize=1000').then((r) => r.json());

    // PascalCase: ASP.NET keys ValidationProblemDetails by the ModelState key, which is the bound
    // CLR property, and `AddApiControllers` never sets a DictionaryKeyPolicy. Observed live as
    // `{"PageSize":[...]}`; this line asserted `pageSize` and was pinning the mock's own spelling.
    expect(body.errors.PageSize).toEqual(['PageSize must be between 1 and 100.']);
    expect(body.errors).not.toHaveProperty('pagination');

    const ok = await fetch('/api/transactions?PageSize=100');
    expect(ok.status).toBe(200);
  });
});

describe('a date that will not parse', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  it.each(['/api/transactions/summary', '/api/transactions'])(
    '%s rejects it instead of silently unfiltering',
    async (url) => {
      // NaN poisons every comparison quietly: `NaN > NaN` is false so the range guard passes, and
      // `at < NaN` / `at > NaN` are both false so the filter keeps everything. The summary answered
      // 200 with all-time totals and echoed `fromDate: "notadate"` straight back — the exact
      // failure the window fix had just been written to prevent. `[FromQuery] DateTime?` never
      // binds, so the API answers from model binding before its own code runs.
      const res = await fetch(`${url}?FromDate=notadate`);

      expect(res.status).toBe(400);
      const body = await res.json();
      // `... for <Name>.` — the suffix is part of the framework's message and the mock dropped it.
      // Observed live: "The value 'notadate' is not valid for FromDate."
      expect(body.errors.FromDate).toEqual(["The value 'notadate' is not valid for FromDate."]);
    },
  );

  it('names every bad bound, not just the first', async () => {
    const body = await fetch('/api/transactions?FromDate=nope&ToDate=alsonope').then((r) =>
      r.json(),
    );

    expect(Object.keys(body.errors).sort()).toEqual(['FromDate', 'ToDate']);
  });

  it('still accepts a well-formed window', async () => {
    const res = await fetch('/api/transactions/summary?FromDate=2026-07-01T00:00:00Z');
    expect(res.status).toBe(200);
  });
});

describe('model binding runs before the action, and the action before the service', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  it('a malformed `at` on the balance route is a 400, not today’s balance', async () => {
    // `[FromQuery] DateTime? at` fails to bind, so the action never runs. The handler used to
    // treat NaN as "no `at` given" and answer 200 with the CURRENT balance — a confident answer
    // to a question that was never validly asked.
    const res = await fetch(`/api/accounts/${MAIN_ACCOUNT_ID}/balance?at=garbage`);

    expect(res.status).toBe(400);
    /*
      NO " for at." SUFFIX. This assertion used to demand one and was green against a mock that
      invented it. Measured on the real stack (2026-08-04, twice):

        GET /api/accounts/{id}/balance?at=garbage
          -> {"at":["The value 'garbage' is not valid."]}

      The framework only names the member when it HAS one — a filter property gets
      "…is not valid for FromDate.", an action parameter like `at` gets the bare sentence. A
      fidelity file asserting the invented form is the worst kind of green.
    */
    expect((await res.json()).errors.at).toEqual(["The value 'garbage' is not valid."]);
  });

  it('binding beats the 404: a bad `at` on an unknown account is still a 400', async () => {
    const res = await fetch('/api/accounts/019f7b3f-0000-7000-8000-0000000000ff/balance?at=nope');
    expect(res.status).toBe(400);
  });

  it('binding beats the 403: a bad date with a foreign AccountId is a 400', async () => {
    // The service's ownership check is the LAST of the three. The mock had it in the middle, so
    // this exact request answered 403 where the API answers 400.
    const res = await fetch(
      '/api/transactions?AccountId=019f7b3f-0000-7000-8000-0000000000ff&FromDate=notadate',
    );

    expect(res.status).toBe(400);
    expect((await res.json()).errors.FromDate).toBeDefined();
  });

  it('a malformed AccountId is a binding failure, not an access denial', async () => {
    // `Guid? AccountId` — "abc" never becomes a Guid, so the request dies before the service can
    // form an opinion about whose account it is.
    const res = await fetch('/api/transactions?AccountId=abc');

    expect(res.status).toBe(400);
    expect((await res.json()).errors.AccountId).toEqual([
      "The value 'abc' is not valid for AccountId.",
    ]);
  });

  it('still 403s a well-formed id that is not yours', async () => {
    const res = await fetch('/api/transactions?AccountId=019f7b3f-0000-7000-8000-0000000000ff');
    expect(res.status).toBe(403);
    expect((await res.json()).errorCode).toBe('ACCESS_DENIED');
  });

  it('accepts every Guid format the server does, not just the hyphenated one', async () => {
    // `Guid.TryParse` takes N, D, B, P and X. The mock took D and answered 400 to the rest, so it
    // was STRICTER than the server — the direction that teaches a test a contract the API does not
    // offer. Each of these was sent to the running backend first and came back 200 with the right
    // scoped total; MAIN_ACCOUNT_ID is this seed's equivalent.
    const d = MAIN_ACCOUNT_ID;
    const n = d.replace(/-/g, '');
    const x = `{0x${n.slice(0, 8)},0x${n.slice(8, 12)},0x${n.slice(12, 16)},{${(
      n.slice(16).match(/../g) as string[]
    )
      .map((b) => `0x${b}`)
      .join(',')}}}`;

    for (const form of [d, n, `{${d}}`, `(${d})`, x]) {
      const res = await fetch(`/api/transactions/summary?AccountId=${encodeURIComponent(form)}`);
      expect(res.status, form).toBe(200);
    }
  });

  it('tolerates the whitespace Guid.TryParse tolerates', async () => {
    // Also measured against the running API before being written: a padded id came back 200 with
    // the same scoped total as the bare one. Cheap to get wrong in the other direction — the regex
    // is anchored, so without a trim every padded value is a 400 the server would have accepted.
    const res = await fetch(
      `/api/transactions/summary?AccountId=${encodeURIComponent(` ${MAIN_ACCOUNT_ID} `)}`,
    );

    expect(res.status).toBe(200);
  });

  it('still rejects the empty guid however it is spelled', async () => {
    // The hole the other formats would have opened: the idempotency guard compared the raw string
    // against the dashed nil, so 32 undashed zeroes would have walked through the moment N was
    // allowed. Parsing to the canonical form before comparing is what closes it.
    const res = await fetch('/api/transactions/deposit', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': '00000000000000000000000000000000',
      },
      body: JSON.stringify({ accountId: MAIN_ACCOUNT_ID, amount: 1 }),
    });

    expect(res.status).toBe(400);
  });

  it('the summary inherits the same three-step order, now that it has an AccountId', async () => {
    // Every one of these used to be moot: the summary took no AccountId at all, so there was no
    // ownership check to sequence. It has one now, and it has to sit where the list's sits.
    const malformed = await fetch('/api/transactions/summary?AccountId=abc');
    expect(malformed.status).toBe(400);
    expect((await malformed.json()).errors.AccountId).toEqual([
      "The value 'abc' is not valid for AccountId.",
    ]);

    const foreign = await fetch(
      '/api/transactions/summary?AccountId=019f7b3f-0000-7000-8000-0000000000ff',
    );
    expect(foreign.status).toBe(403);
    expect((await foreign.json()).errorCode).toBe('ACCESS_DENIED');
  });

  it('a bad window beats the 403, at BOTH tiers', async () => {
    // Ownership is the last thing checked, so a request that is wrong about dates AND about whose
    // account it is gets the date answer — but WHICH date answer depends on which tier catches it.
    // Both are asserted, because getting the tier wrong is exactly the drift the real stack found.
    const pair = await fetch(
      '/api/transactions/summary?AccountId=019f7b3f-0000-7000-8000-0000000000ff' +
        '&FromDate=2026-07-20T00:00:00Z&ToDate=2026-07-01T00:00:00Z',
    );
    expect(pair.status).toBe(400);

    const future = new Date(Date.now() + 10 * 86_400_000).toISOString();
    const resolved = await fetch(
      `/api/transactions/summary?AccountId=019f7b3f-0000-7000-8000-0000000000ff&FromDate=${future}`,
    );
    expect(resolved.status).toBe(422);
    expect((await resolved.json()).errorCode).toBe('INVALID_DATE_RANGE');
  });
});

/**
 * The guard that would have caught this a month earlier.
 *
 * The seed used to be hardcoded to July 2026 while the dashboard summarises the CURRENT calendar
 * month. On 1 August every figure on that card became €0.00 — in the demo as much as in the tests —
 * and `DashboardPage.test.tsx` went red and stayed red. Nothing was watching the property that
 * broke, so the first sign of it was a CI failure three days later with a message
 * ("expected '+€0.00' not to be '+€0.00'") that named neither the cause nor an expectation.
 *
 * These two assert the property directly, so the next re-dating that gets it wrong fails HERE, with
 * a reason, instead of surfacing as an unrelated page test.
 */
describe('the seeded ledger stays inside the window the app asks about', () => {
  it('places every entry inside the CURRENT calendar month', () => {
    const now = new Date();
    const monthStart = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1);

    expect(mockState.transactions.length).toBeGreaterThan(0);
    for (const t of mockState.transactions) {
      const at = Date.parse(t.createdAt);
      expect(at, `${t.transactionNumber} is before this month`).toBeGreaterThanOrEqual(monthStart);
      expect(at, `${t.transactionNumber} is in the future`).toBeLessThanOrEqual(Date.now());
    }
  });

  it('numbers each entry with its own date', () => {
    // TXN-yyyyMMdd-NNNNNN. The number carries a date of its own, so a re-dating that moves
    // `createdAt` and forgets the number leaves the two contradicting each other on screen.
    for (const t of mockState.transactions) {
      const stamped = t.createdAt.slice(0, 10).replace(/-/g, '');
      expect(t.transactionNumber).toContain(stamped);
    }
  });
});

/**
 * Message text the app renders VERBATIM.
 *
 * `moneyProblem` falls through to `problem.detail` and the money dialogs render `message` on
 * success, so every string below is something a user reads. Each was quoted from the running stack
 * on 2026-08-04 (the two transfer success strings are the exception, noted inline) — the mock had
 * been paraphrasing, which meant the sentence under MSW was not the sentence in production.
 */
describe('the mock quotes the server, it does not paraphrase it', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  const key = () => crypto.randomUUID();

  it('uses the API’s success wording, with no invented trailing period', async () => {
    // Measured: POST /api/accounts -> "Account created successfully"  (no period)
    const created = await fetch('/api/accounts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'Wording Probe', type: 'Savings' }),
    });
    expect((await created.json()).message).toBe('Account created successfully');

    // Measured: POST /api/transactions/deposit -> "Deposit successful"
    const deposited = await fetch('/api/transactions/deposit', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': key() },
      body: JSON.stringify({ accountId: MAIN_ACCOUNT_ID, amount: 5 }),
    });
    expect((await deposited.json()).message).toBe('Deposit successful');
  });

  it('embeds the amounts in INSUFFICIENT_FUNDS, as the API does', async () => {
    /*
      Measured: "Insufficient funds. Available: $50,042.00, Requested: $99,000.00" — the server
      formats with C#'s `:C` on en-US, so grouped, two decimals, a leading `$`. NOT the app's EUR
      display: this is the mock quoting the server's own sentence.
    */
    const account = mockState.accounts.find((a) => a.id === MAIN_ACCOUNT_ID);
    const available = account?.balance ?? 0;

    const res = await fetch('/api/transactions/deposit', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': key() },
      body: JSON.stringify({ accountId: MAIN_ACCOUNT_ID, amount: 1 }),
    });
    expect(res.status).toBe(201);

    const withdrawn = await fetch('/api/transactions/withdraw', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': key() },
      body: JSON.stringify({ accountId: MAIN_ACCOUNT_ID, amount: available + 1000, pin: '123456' }),
    });
    expect(withdrawn.status).toBe(422);
    const { detail } = await withdrawn.json();
    expect(detail).toMatch(
      /^Insufficient funds\. Available: \$[\d,]+\.\d{2}, Requested: \$[\d,]+\.\d{2}$/,
    );
  });

  it('reports a refused PIN as authLevel 1, not the caller’s current level', async () => {
    /*
      A refused PIN is a 200, and the BFF HARD-CODES authLevel 1 rather than echoing what the caller
      had. The mock returned `mockState.authLevel`, so an already-elevated user who fumbled a
      re-entry saw 2 where production says 1.

      Measured (cost one of three attempts): {"verified":false,"authLevel":1,"pinExpiresAt":null},
      message "Invalid PIN".
    */
    await fetch('/bff/auth/verify-pin', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '123456' }),
    });
    expect(mockState.authLevel).toBe(2);

    const res = await fetch('/bff/auth/verify-pin', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '000000' }),
    });

    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.data).toMatchObject({ verified: false, authLevel: 1, pinExpiresAt: null });
    expect(body.message).toBe('Invalid PIN');
  });
});

/**
 * The mock's OWN bookkeeping, which is not an envelope question at all.
 *
 * Identity was derived from `mockState.accounts.length` while delete removes the row outright, so
 * the counter goes DOWN and the next create reuses an identifier that is still in use. Every
 * `find(a => a.id === …)` in the handler file then resolves to whichever row happens to sit first,
 * and a test that deletes before creating is quietly operating on the wrong account.
 */
describe('account identity survives a delete', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  const create = (name: string) =>
    fetch('/api/accounts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, type: 'Savings' }),
    }).then((r) => r.json());

  it('never reuses an id after a delete', async () => {
    const first = (await create('First Account')).data;
    const second = (await create('Second Account')).data;

    const deleted = await fetch(`/api/accounts/${first.id}`, { method: 'DELETE' });
    expect(deleted.status).toBe(200);

    const third = (await create('Third Account')).data;

    // The whole bug: with identity derived from the array length, `third` came back wearing
    // `second`'s id, and both rows were live at once.
    expect(third.id).not.toBe(second.id);
    // And the DELETED id must not come back either — the assertion above alone would pass for a
    // generator that recycled `first`'s, which is the very thing "survives a delete" claims.
    expect(third.id).not.toBe(first.id);
    const ids = mockState.accounts.map((a) => a.id);
    expect(new Set(ids).size).toBe(ids.length);
    // A well-formed UUID node however far the counter runs, not just a unique string.
    expect(third.id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
  });

  it('does NOT claim masked account numbers are unique — they legitimately are not', async () => {
    /*
      Deliberately asserting the weaker property, because the stronger one is false in production
      and a fixture must not be stricter than the system it stands in for.

      `MaskAccountNumber` is `$"{n[..3]}****-****-{n[^2..]}"` — it keeps the last TWO characters
      only, and the generator draws that group from `GetInt32(10, 100)`. So there are 90 possible
      masked numbers in the entire system and two accounts colliding is ordinary, not a bug.

      What must hold is that the masked value is well-FORMED and not a positional artefact. The id
      is the identity; the masked number is decoration.
    */
    const first = (await create('First Account')).data;
    await fetch(`/api/accounts/${first.id}`, { method: 'DELETE' });
    const next = (await create('Next Account')).data;

    expect(next.accountNumber).toMatch(/^AB-\*{4}-\*{4}-\d{2}$/);
  });

  it('masks the number in a shape the real generator can actually produce', async () => {
    /*
      `IdGenerator.GenerateAccountNumber` is
        AB-{RandomNumberGenerator.GetInt32(1000,10000)}-{…}-{GetInt32(10,100)}
      so the final group is ALWAYS two digits. The mock built it as `70 + index`, which is three
      digits from index 30 onward — a masked number the API could never emit, and one that any
      consumer parsing the last group would trip over.
    */
    const created = await Promise.all(
      Array.from({ length: 32 }, (_, i) => create(`Bulk Account ${i}`)),
    );

    for (const { data } of created) {
      expect(data.accountNumber).toMatch(/^AB-\*{4}-\*{4}-\d{2}$/);
    }
  });
});

/**
 * The summary echoes its window back, and the app PARSES that echo strictly.
 *
 * `getTransactionSummary` is one of the strict-schema endpoints — `unwrap(response,
 * transactionSummarySchema)` calls `schema.parse`, which THROWS on drift rather than degrading. So
 * an echo the schema rejects is not cosmetic: it takes the dashboard down.
 */
describe('the summary window is normalised, not echoed', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  it('answers a date-only FromDate with a full timestamp the strict schema accepts', async () => {
    /*
      Measured on the running stack (2026-08-04):
        GET /api/transactions/summary?FromDate=2026-07-01
          -> {"fromDate":"2026-07-01T00:00:00.0000000Z",
              "toDate":"2026-08-04T13:36:48.2114544Z"}

      The API round-trips its filter through `DateTime` and writes it back with
      `Rfc3339DateTimeConverter`, so what comes out is always a full instant. The mock echoed the
      caller's raw string, so `?FromDate=2026-07-01` came back as `"2026-07-01"` — which
      `z.iso.datetime()` refuses, and `schema.parse` turns into a thrown error inside RTK Query.
    */
    const res = await fetch('/api/transactions/summary?FromDate=2026-07-01');
    expect(res.status).toBe(200);

    const body = await res.json();
    // The property under test, stated directly: it must parse, and it must be a full instant.
    expect(() => transactionSummarySchema.parse(body.data)).not.toThrow();
    expect(body.data.fromDate).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}/);
  });
});

/**
 * A deleted account's money must leave the ledger AND the totals.
 *
 * The API soft-deletes and filters on it in both queries — `!a.IsDeleted` when it resolves the
 * caller's accounts for the list (TransactionService.cs:174) and `!t.Account.IsDeleted` inside the
 * summary aggregate (:301). The mock hard-removed the account row and left its transactions
 * behind, so the feed kept showing them and the totals kept counting them: silently different
 * money figures, with no error to notice.
 */
describe('a deleted account takes its transactions with it', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  const key = () => crypto.randomUUID();

  it('drops the rows from both the feed and the summary totals', async () => {
    const created = await fetch('/api/accounts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'Doomed Account', type: 'Savings' }),
    }).then((r) => r.json());
    const id = created.data.id;

    /*
      Give it a history, then empty it — a non-zero balance cannot be deleted, in the mock or the
      API, so the account has to be drained before it can go.

      Tracked by transaction ID rather than by `accountId`, because `toWire` deliberately strips
      `accountId` from the response: the wire DTO has no such field, and asserting on one that does
      not exist is how a test passes while proving nothing. (My first draft did exactly that and
      failed on its own setup line, which is the only reason it was caught.)
    */
    const deposited = await fetch('/api/transactions/deposit', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': key() },
      body: JSON.stringify({ accountId: id, amount: 25 }),
    }).then((r) => r.json());
    const withdrawn = await fetch('/api/transactions/withdraw', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': key() },
      body: JSON.stringify({ accountId: id, amount: 25, pin: '123456' }),
    }).then((r) => r.json());
    const doomedIds = [deposited.data.transaction.id, withdrawn.data.transaction.id];

    const before = await fetch('/api/transactions?Page=1&PageSize=100').then((r) => r.json());
    // An EXPLICIT wide window, not the default. The summary defaults to the current month, and
    // whether a freshly-created row lands inside it depends on the clock — a dependency that has
    // nothing to do with what this test is about.
    const wide = '/api/transactions/summary?FromDate=2000-01-01T00:00:00Z';
    const beforeSummary = await fetch(wide).then((r) => r.json());
    const listedIds = (rows: { id: string }[]) => rows.map((t) => t.id);
    expect(listedIds(before.data)).toEqual(expect.arrayContaining(doomedIds));

    expect((await fetch(`/api/accounts/${id}`, { method: 'DELETE' })).status).toBe(200);

    const after = await fetch('/api/transactions?Page=1&PageSize=100').then((r) => r.json());
    const afterSummary = await fetch(wide).then((r) => r.json());

    // The feed must not mention rows the caller can no longer reach.
    for (const gone of doomedIds) {
      expect(listedIds(after.data)).not.toContain(gone);
    }
    // And the totals must move — this is the half that fails SILENTLY, because a wrong number
    // still renders and nothing errors. The 25 in and the 25 out both leave with the account.
    expect(afterSummary.data.totalIncome).toBe(beforeSummary.data.totalIncome - 25);
    expect(afterSummary.data.totalExpenses).toBe(beforeSummary.data.totalExpenses - 25);
  });
});

describe('the internal transfer names a real ledger row', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
    // `/api/transfers/internal` is one of the three level-2 routes; stand-in for a successful
    // /bff/auth/verify-pin, as `transferHandler.test.ts` does.
    mockState.authLevel = 2;
  });

  it('returns a transferId that dereferences to the OUTGOING row', async () => {
    /*
      `TransferService.cs:330` sets `TransferId = outgoingTransaction.Id`, so the id in the
      response is the id of the debit row the transfer just wrote — not a new identifier. Measured
      end to end, because the whole point is that the id is dereferenceable and only a real request
      can show that:

        POST /api/transfers/internal -> transferId 019fd135-37c7-78d4-9d7b-8afcde4c6e3f
        GET  /api/transactions/019fd135-37c7-78d4-9d7b-8afcde4c6e3f
          -> 200 {"id":"019fd135-37c7-78d4-9d7b-8afcde4c6e3f","type":"TransferOut",
                  "transactionNumber":"TXN-20260805-248545",...}

      The mock minted a synthetic `0xd00 + index` instead, which matched no row at all — so the
      same follow-up GET was a 404 under MSW and a 200 in production. Latent today because no
      screen navigates from a transfer receipt to the transaction, and precisely the sort of thing
      that stops being latent the moment someone builds that link.
    */
    const second = await fetch('/api/accounts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'Transfer Target', type: 'Savings' }),
    }).then((r) => r.json());

    const transfer = await fetch('/api/transfers/internal', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },
      body: JSON.stringify({
        fromAccountId: MAIN_ACCOUNT_ID,
        toAccountId: second.data.id,
        amount: 10,
        description: 'dereference me',
      }),
    }).then((r) => r.json());

    const followed = await fetch(`/api/transactions/${transfer.data.transferId}`);
    expect(followed.status).toBe(200);

    const row = (await followed.json()).data;
    expect(row.id).toBe(transfer.data.transferId);
    // The DEBIT side specifically. Naming the credit row would resolve just as happily and mean
    // the opposite thing to anyone reconciling the sender's statement.
    expect(row.type).toBe('TransferOut');
    expect(row.transactionNumber).toBe(transfer.data.transactionNumber);
  });
});

describe('a session that DIED is not a session that never was', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  /**
   * Mock-only, and deliberately so. The contract suite runs the same file against the real stack,
   * and there is no safe way to produce this state there: the session must actually expire, which
   * means waiting out the ten-minute inactivity window inside the test run. So the real behaviour
   * is quoted from a live measurement and the mock is pinned against the quote.
   *
   * Measured 2026-08-05 with a cookie that no longer resolves (an unknown id — the same code path
   * as an expired one, since `InMemoryTokenStore.GetSessionAsync` REMOVES an expired session and
   * returns null for both):
   *
   *   GET /api/accounts/{id}/full-number   -> 403, X-Auth-Level-Required: 2, X-Auth-Level-Current: 0
   *   POST /api/transfers                  -> 403, same headers
   *   POST /api/transfers/internal         -> 403, same headers
   *   GET /api/accounts                    -> 401 AUTH_TOKEN_MISSING
   *   the same three routes with NO cookie  -> 401 AUTH_TOKEN_MISSING
   */
  const expireTheSession = () => {
    // Push last activity past the inactivity window; the next request triggers the sweep.
    mockState.sessionLastActivity = Date.now() - mockState.sessionInactivityWindowMs - 1;
  };

  it('answers the level-2 routes with 403 STEP_UP_REQUIRED and level 0', async () => {
    expireTheSession();

    const reveal = await fetch(`/api/accounts/${MAIN_ACCOUNT_ID}/full-number`);
    expect(reveal.status).toBe(403);
    expect(reveal.headers.get('X-Auth-Level-Required')).toBe('2');
    // ZERO, not 1. The level belongs to the session and there is no session — a client showing
    // "you are at level 1" would be describing a session that no longer exists.
    expect(reveal.headers.get('X-Auth-Level-Current')).toBe('0');
    expect((await reveal.json()).currentLevel).toBe(0);
  });

  it('still answers ORDINARY api routes with 401, which is the control', async () => {
    // Without this the first test passes just as happily against a mock that 403s everything,
    // which would be a different and equally wrong system.
    expireTheSession();

    const list = await fetch('/api/accounts');
    expect(list.status).toBe(401);
    expect((await list.json()).errorCode).toBe('AUTH_TOKEN_MISSING');
  });

  it('answers 401 on the level-2 routes when there was never a cookie at all', async () => {
    /*
      The distinction the mock could not previously express. `AuthLevelMiddleware` only gates when
      `Cookies.TryGetValue` SUCCEEDS; with no cookie it logs "let the API handle 401" and falls
      through to the proxy, which forwards no bearer. So the discriminator is the cookie, not the
      session — and logging out deletes the cookie, where expiring does not.
    */
    resetMockState(); // no session, and no stale cookie either

    const reveal = await fetch(`/api/accounts/${MAIN_ACCOUNT_ID}/full-number`);
    expect(reveal.status).toBe(401);
    expect((await reveal.json()).errorCode).toBe('AUTH_TOKEN_MISSING');
  });

  it('treats a LOGOUT as no cookie, not as a dead one', async () => {
    // Logout deletes the cookie server-side AND in the browser, so the next request carries none.
    // Conflating it with expiry would make a signed-out user meet a PIN prompt.
    expireTheSession();
    await fetch('/bff/auth/logout', { method: 'POST' });

    const reveal = await fetch(`/api/accounts/${MAIN_ACCOUNT_ID}/full-number`);
    expect(reveal.status).toBe(401);
  });
});

describe('the PIN lockout, and the header only one path keeps', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  /**
   * Mock-only for a different reason: reaching a lockout costs three wrong PINs and fifteen
   * minutes, and the contract suite's real target is the seeded admin account that the e2e suite
   * drives. The real values below were measured on a THROWAWAY registered user
   * (`PinAccessFailedCount` is per-user), which is what made U38 affordable at all.
   *
   *   verify-pin -> 429, Retry-After ABSENT,  instance "/api/auth/pin/verify"
   *   withdraw   -> 429, Retry-After: 853,    instance "/api/transactions/withdraw"
   *   both       -> "Too many incorrect PIN attempts. Your PIN is temporarily locked; try again
   *                  later.", retryAfterSeconds 853, lockedUntil "2026-08-05T12:10:14.3341758+00:00"
   */
  const wrongPin = () =>
    fetch('/bff/auth/verify-pin', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '000000' }),
    });

  it('locks on the THIRD attempt, and drops Retry-After on the BFF action', async () => {
    expect((await wrongPin()).status).toBe(200);
    expect((await wrongPin()).status).toBe(200);

    const locked = await wrongPin();
    expect(locked.status).toBe(429);
    /*
      `ForwardUpstreamError` is `StatusCode(status, body)` — it copies no headers, so the
      `Retry-After` the API set is lost between the API and the browser. Asserting its ABSENCE is
      the whole point: a client that read the header here would work in tests and silently fall
      back to a default in production.
    */
    expect(locked.headers.get('Retry-After')).toBeNull();

    const body = await locked.json();
    expect(body.errorCode).toBe('PIN_LOCKED');
    expect(body.detail).toBe(
      'Too many incorrect PIN attempts. Your PIN is temporarily locked; try again later.',
    );
    // The API's own path, which the browser never called — the body was generated upstream.
    expect(body.instance).toBe('/api/auth/pin/verify');
    // `DateTimeOffset.ToString("o")`: seven fractional digits and a numeric offset, NOT `Z`.
    expect(body.lockedUntil).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00$/);
    expect(body.retryAfterSeconds).toBeGreaterThan(0);
  });

  it('KEEPS Retry-After on the proxied withdraw, which is the same failure', async () => {
    await wrongPin();
    await wrongPin();
    await wrongPin();

    const withdrawal = await fetch('/api/transactions/withdraw', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },
      body: JSON.stringify({ accountId: MAIN_ACCOUNT_ID, amount: 1, pin: '123456' }),
    });

    expect(withdrawal.status).toBe(429);
    // YARP forwards response headers verbatim, so this path keeps what the BFF action loses.
    // Same errorCode, same sentence, same lock — only the transport differs.
    const seconds = withdrawal.headers.get('Retry-After');
    expect(seconds).not.toBeNull();
    const body = await withdrawal.json();
    expect(body.errorCode).toBe('PIN_LOCKED');
    expect(Number(seconds)).toBe(body.retryAfterSeconds);
    expect(body.instance).toBe('/api/transactions/withdraw');
  });
});

describe('a fresh session does not inherit an elevation', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  it('drops back to level 1 when the user signs in again', async () => {
    /*
      `SessionService.CreateSession` hardcodes `AuthLevel = 1` and mints a NEW session id, so
      whatever the previous session had earned is orphaned with it. The mock replaced `session` and
      left `authLevel` alone, so signing in while elevated stayed at 2 — a step-up bypass, since
      the level is the only thing the money handlers consult.

      Measured on the running stack rather than read off that line, because "found by reading" is
      not "observed":

        verify-pin 123456                       -> 200 authLevel 2
        GET /api/accounts/{id}/full-number      -> 200
        POST /bff/auth/login  (same cookie jar) -> 200
        GET /api/accounts/{id}/full-number      -> 403 X-Auth-Level-Current: 1, currentLevel 1

      Every other route to a dead session already reset it (logout, clock expiry, resetMockState);
      login and register were the gap.
    */
    await fetch('/bff/auth/verify-pin', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '123456' }),
    });
    expect(mockState.authLevel).toBe(2);
    expect((await fetch(`/api/accounts/${MAIN_ACCOUNT_ID}/full-number`)).status).toBe(200);

    await fetch('/bff/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: 'demo@azurebank.dev', password: 'Password1!' }),
    });

    const afterRelogin = await fetch(`/api/accounts/${MAIN_ACCOUNT_ID}/full-number`);
    expect(afterRelogin.status).toBe(403);
    expect(afterRelogin.headers.get('X-Auth-Level-Current')).toBe('1');
    expect((await afterRelogin.json()).currentLevel).toBe(1);
  });
});

describe('failure modes the mock could not previously produce', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  const login = (email: string, password: string) =>
    fetch('/bff/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });

  it('U3: locks after five failures and refuses the CORRECT password', async () => {
    /*
      Mock-only, and it has to be: a contract test against the real stack would lock a real account
      for fifteen minutes and take the rest of the suite with it. Measured instead on a THROWAWAY
      registered user, 2026-08-06 — `PinAccessFailedCount` and the login counter are both per-user,
      so admin (which e2e drives) stayed clean and logged in fine afterwards:

        wrong x5 -> 401 INVALID_CREDENTIALS each, no hint a counter is running (ADR-0013,
                    enumeration-neutral: telling you about the lock tells you the account exists)
        CORRECT  -> 429 {"detail":"Too many failed login attempts. Your account is temporarily
                    locked; try again later.","instance":"/api/auth/login",
                    "errorCode":"ACCOUNT_LOCKED","retryAfterSeconds":900,
                    "lockedUntil":"2026-08-06T18:54:57.3924877+00:00"}
    */
    for (let i = 0; i < 5; i++) {
      const wrong = await login('demo@azurebank.dev', 'WrongPassword1!');
      expect(wrong.status).toBe(401);
      expect((await wrong.json()).errorCode).toBe('INVALID_CREDENTIALS');
    }

    const correct = await login('demo@azurebank.dev', 'Password1!');
    expect(correct.status).toBe(429);
    /*
      No Retry-After HEADER. Login is a BFF ACTION, so the body is forwarded by
      ForwardUpstreamError, which copies status and body and no headers — the same asymmetry as
      the PIN lockout. The rate-limit 429 below DOES carry the header, which is what makes the two
      distinguishable at all.
    */
    expect(correct.headers.get('Retry-After')).toBeNull();

    const body = await correct.json();
    expect(body.errorCode).toBe('ACCOUNT_LOCKED');
    expect(body.detail).toBe(
      'Too many failed login attempts. Your account is temporarily locked; try again later.',
    );
    // The API's path, not the BFF's — the body was generated upstream. This is the discriminator
    // against the rate-limit 429, which names /bff/auth/login.
    expect(body.instance).toBe('/api/auth/login');
    expect(body.retryAfterSeconds).toBe(900);
    expect(body.lockedUntil).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}\+00:00$/);
  });

  it('U3: a WRONG password never reveals the lock — the anti-enumeration property', async () => {
    /*
      The half that matters for security, and the half I got backwards on the first pass: I checked
      the lock BEFORE the password, so a wrong guess answered 429 and told an attacker "this address
      exists and is locked". ADR-0012 names that as the thing to avoid, and the wire agrees.

      Measured 2026-08-06 on a throwaway user:
        wrong x5  -> 401 INVALID_CREDENTIALS
        6th WRONG -> 401 INVALID_CREDENTIALS   (identical to an unknown address)
        CORRECT   -> 429 ACCOUNT_LOCKED

      Asserting the 401 here is what stops the ordering being flipped back: the other U3 test would
      pass just as happily with the check in the wrong place.
    */
    for (let i = 0; i < 5; i++) {
      expect((await login('demo@azurebank.dev', 'WrongPassword1!')).status).toBe(401);
    }

    const stillWrong = await login('demo@azurebank.dev', 'AnotherWrong1!');
    expect(stillWrong.status).toBe(401);
    expect((await stillWrong.json()).errorCode).toBe('INVALID_CREDENTIALS');

    // And the window was not extended by that sixth attempt — the correct password still reports
    // the ORIGINAL deadline rather than a fresh fifteen minutes.
    const revealed = await login('demo@azurebank.dev', 'Password1!');
    expect(revealed.status).toBe(429);
    expect((await revealed.json()).retryAfterSeconds).toBeLessThanOrEqual(900);
  });

  it('U4: the eleventh auth call in a minute is the BFF’s own 429', async () => {
    /*
      Also mock-only, and for a sharper reason: a contract test that trips the real limiter would
      lock every subsequent auth call in the suite out for sixty seconds. Measured by deliberately
      exhausting the bucket against a NONEXISTENT email, so the account-lockout 429 could not fire
      and be mistaken for this one:

        429 {"detail":"Too many requests. Please retry later.","instance":"/bff/auth/login",
             "errorCode":"RATE_LIMIT_EXCEEDED"}
        Content-Type: application/json   Retry-After: 60   Cache-Control: no-store
    */
    for (let i = 0; i < 10; i++) {
      await login('nobody@azurebank.dev', 'WhateverX1!');
    }

    const limited = await login('nobody@azurebank.dev', 'WhateverX1!');
    expect(limited.status).toBe(429);
    expect(limited.headers.get('Retry-After')).toBe('60');
    expect(limited.headers.get('Cache-Control')).toBe('no-store');
    // NOT problem+json: the limiter writes its own body and never reaches the ProblemDetails pipe.
    expect(limited.headers.get('Content-Type')).toContain('application/json');

    const body = await limited.json();
    expect(body.errorCode).toBe('RATE_LIMIT_EXCEEDED');
    expect(body.detail).toBe('Too many requests. Please retry later.');
    // The BFF's OWN path — the limiter is BFF middleware, so nothing upstream produced this.
    expect(body.instance).toBe('/bff/auth/login');
    // And NO retryAfterSeconds: the seconds live in the header here, in the body for ACCOUNT_LOCKED.
    expect(body.retryAfterSeconds).toBeUndefined();
  });
});
