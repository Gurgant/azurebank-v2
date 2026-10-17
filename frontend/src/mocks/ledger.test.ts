import { beforeEach, describe, expect, it } from 'vitest';
import {
  MAIN_ACCOUNT_ID,
  MOCK_PIN,
  SAVINGS_ACCOUNT_ID,
  mockState,
  resetMockState,
  seedMockSession,
} from './state';

/**
 * The seeded ledger has to hold together on its own, because everything downstream reads it as if
 * it did.
 *
 * The dashboard shows a running-balance column when — and only when — the feed is scoped to one
 * account, on the grounds that `balanceAfter` is per-account and a cross-account list cannot
 * reconcile. That rule was correct, and unverifiable: the mock ignored `AccountId`, so both
 * accounts returned the same rows, and `balanceAfter` was hand-typed to values that followed from
 * nothing. The column could have been showing anything and every test would still have passed.
 *
 * These are the invariants that make the seed mean something. They are properties, not fixtures:
 * no number below is copied from the seed, so re-seeding cannot quietly turn them green.
 */

/** The wire type carries no account, so the mock's own ledger column is what we group by. */
function entriesFor(accountId: string) {
  return mockState.transactions.filter((t) => t.accountId === accountId);
}

function signed(entry: { type: string; amount: number }): number {
  return entry.type === 'Deposit' || entry.type === 'TransferIn' ? entry.amount : -entry.amount;
}

describe('the seeded ledger', () => {
  beforeEach(() => {
    resetMockState();
    // Re-seed: the shared setup signs in before every test, and a local reset undoes it. `/api/*`
    // is session-gated now, so without this every request here is a 401.
    seedMockSession();
  });

  it('is ordered newest-first, the way OrderByDescending(CreatedAt) orders it', () => {
    const times = mockState.transactions.map((t) => t.createdAt);
    expect(times).toEqual([...times].sort((a, b) => b.localeCompare(a)));
    // Not vacuous: the seed must actually contain entries that could be out of order.
    expect(new Set(times).size).toBeGreaterThan(1);
  });

  it.each([
    ['Main Account', MAIN_ACCOUNT_ID],
    ['Rainy Day', SAVINGS_ACCOUNT_ID],
  ])("%s's newest entry lands exactly on the account's balance", (_name, accountId) => {
    const account = mockState.accounts.find((a) => a.id === accountId);
    const entries = entriesFor(accountId);

    expect(entries.length).toBeGreaterThan(0);
    expect(entries[0].balanceAfter).toBe(account?.balance);
  });

  it.each([
    ['Main Account', MAIN_ACCOUNT_ID],
    ['Rainy Day', SAVINGS_ACCOUNT_ID],
  ])('%s reconciles: every step equals the previous balance plus the delta', (_name, accountId) => {
    const entries = entriesFor(accountId);

    // Walk oldest -> newest so each row is checked against the one that precedes it in TIME.
    const chronological = [...entries].reverse();
    for (let i = 1; i < chronological.length; i++) {
      const previous = chronological[i - 1];
      const current = chronological[i];
      expect(Math.round(current.balanceAfter * 100)).toBe(
        Math.round((previous.balanceAfter + signed(current)) * 100),
      );
    }
  });

  it('never goes negative — a checking account with no overdraft cannot', () => {
    for (const entry of mockState.transactions) {
      expect(entry.balanceAfter).toBeGreaterThanOrEqual(0);
    }
  });

  it('gives the two accounts DIFFERENT feeds, which is what the scope control depends on', () => {
    const main = entriesFor(MAIN_ACCOUNT_ID).map((t) => t.id);
    const savings = entriesFor(SAVINGS_ACCOUNT_ID).map((t) => t.id);

    expect(main.length).toBeGreaterThan(0);
    expect(savings.length).toBeGreaterThan(0);
    expect(main).not.toEqual(savings);
    expect(main.filter((id) => savings.includes(id))).toEqual([]);
    // Every entry belongs to a real account — no orphans hiding in the cross-account list.
    expect(main.length + savings.length).toBe(mockState.transactions.length);
  });
});

describe('writing to the ledger', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  it('files a deposit against the account it names, and the balance still reconciles', async () => {
    const before = mockState.transactions.length;
    const opening = mockState.accounts.find((a) => a.id === SAVINGS_ACCOUNT_ID)?.balance ?? 0;

    const res = await fetch('/api/transactions/deposit', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': '3f2504e0-4f89-41d3-9a0c-0305e82c3399',
      },
      body: JSON.stringify({ accountId: SAVINGS_ACCOUNT_ID, amount: 25 }),
    });
    expect(res.status).toBe(201);

    expect(mockState.transactions.length).toBe(before + 1);
    // Scoped by account as well as by amount: matching on amount alone would silently pick a
    // seeded row if the seed ever grew a €25 deposit, and then assert against the wrong entry.
    const filed = mockState.transactions.find(
      (t) => t.accountId === SAVINGS_ACCOUNT_ID && t.amount === 25 && t.type === 'Deposit',
    );
    expect(filed).toBeDefined();
    // The new entry lands on the account's new balance — the same property the seed guarantees.
    expect(filed?.balanceAfter).toBe(opening + 25);
    expect(mockState.accounts.find((a) => a.id === SAVINGS_ACCOUNT_ID)?.balance).toBe(opening + 25);
  });

  it('404s on an account that does not exist, and files nothing', async () => {
    // This used to fabricate a balance and answer 201 with a transaction that no account and no
    // ledger row owned. Withholding only the ledger write was not enough either: ids come from
    // `mockState.transactions.length`, so the phantom handed its identifiers to the next real
    // write. `GetAccountWithOwnershipCheckAsync` makes an unknown account a 404, and so does this.
    const before = [...mockState.transactions];

    const res = await fetch('/api/transactions/deposit', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': '3f2504e0-4f89-41d3-9a0c-0305e82c3398',
      },
      body: JSON.stringify({ accountId: 'not-an-account', amount: 25 }),
    });

    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
    expect(mockState.transactions).toEqual(before);
  });
});

describe('GET /api/transactions honours AccountId', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  it('returns only that account, and the totals count only that account', async () => {
    const scoped = await fetch(
      `/api/transactions?AccountId=${SAVINGS_ACCOUNT_ID}&Page=1&PageSize=50`,
    ).then((r) => r.json());
    const all = await fetch('/api/transactions?Page=1&PageSize=50').then((r) => r.json());

    const expected = entriesFor(SAVINGS_ACCOUNT_ID).map((t) => t.id);
    expect(scoped.data.map((t: { id: string }) => t.id)).toEqual(expected);
    expect(scoped.pagination.totalItems).toBe(expected.length);
    // The unscoped feed is strictly larger — proof the filter did something.
    expect(all.pagination.totalItems).toBeGreaterThan(scoped.pagination.totalItems);
  });

  it('never ships accountId on the wire — TransactionResponse has no such field', async () => {
    const body = await fetch('/api/transactions?Page=1&PageSize=50').then((r) => r.json());
    for (const row of body.data) {
      expect(row).not.toHaveProperty('accountId');
    }

    const one = await fetch(`/api/transactions/${mockState.transactions[0].id}`).then((r) =>
      r.json(),
    );
    expect(one.data).not.toHaveProperty('accountId');
  });

  it.each([
    ['a missing account', '/api/transactions/deposit', 'Account'],
    [
      'a missing transaction',
      '/api/transactions/019f7b3f-0000-7000-8000-0000000000ff',
      'Transaction',
    ],
  ])('says %s the way NotFoundException says it', async (_case, url, resource) => {
    // The two-argument constructor puts the RESOURCE NAME first — "Account with identifier 'x' was
    // not found." — and hard-codes ACCOUNT_NOT_FOUND whatever the resource is. The mock had drifted
    // from both halves at once: five handlers invented a NOT_FOUND code the API cannot emit, and
    // three invented "No account was found with identifier '…'." One builder emits both now.
    const res =
      url === '/api/transactions/deposit'
        ? await fetch(url, {
            method: 'POST',
            headers: {
              'Content-Type': 'application/json',
              'Idempotency-Key': '3f2504e0-4f89-41d3-9a0c-0305e82c3396',
            },
            body: JSON.stringify({ accountId: 'nope', amount: 25 }),
          })
        : await fetch(url);

    expect(res.status).toBe(404);
    const body = await res.json();
    expect(body.errorCode).toBe('ACCOUNT_NOT_FOUND');
    expect(body.detail).toMatch(new RegExp(`^${resource} with identifier '.+' was not found\\.$`));
  });

  it('403s on an account that is not the callers, like the real service does', async () => {
    const res = await fetch('/api/transactions?AccountId=019f7b3f-0000-7000-8000-0000000000ff');

    expect(res.status).toBe(403);
    const body = await res.json();
    expect(body.errorCode).toBe('ACCESS_DENIED');
  });

  it.each(['Page=0', 'Page=-1', 'Page=1.5', 'Page=x', 'PageSize=0', 'PageSize=-20'])(
    'rejects %s instead of computing nonsense pagination',
    async (query) => {
      // PageSize=0 made totalPages Infinity and a non-numeric page sliced to nothing while the
      // metadata claimed otherwise — pagination a client could be built against and no real
      // server would ever send.
      const res = await fetch(`/api/transactions?${query}`);
      expect(res.status).toBe(400);
    },
  );
});

/**
 * The key of a money request on the rows it wrote, as `Transaction.IdempotencyKey` holds it on the
 * API (ADR-0009): the mock stores it, `GET /api/transactions?IdempotencyKey=` lists by it, and no
 * response carries it. The same listing runs against the real stack in
 * `src/contract/money.contract.test.ts`.
 */
describe('GET /api/transactions honours IdempotencyKey', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  const JSON_BODY = { 'Content-Type': 'application/json' };

  /** Mints the authorisation a withdrawal or a transfer needs, failing loudly when refused. */
  async function minted(url: string, body: Record<string, unknown>): Promise<string> {
    const res = await fetch(url, {
      method: 'POST',
      headers: JSON_BODY,
      body: JSON.stringify({ ...body, pin: MOCK_PIN }),
    });
    expect(res.status).toBe(201);
    return (await res.json()).data.authorizationId as string;
  }

  function send(url: string, key: string, body: unknown, authorizationId?: string) {
    const headers: Record<string, string> = { ...JSON_BODY, 'Idempotency-Key': key };
    if (authorizationId) headers['Step-Up-Authorization'] = authorizationId;
    return fetch(url, { method: 'POST', headers, body: JSON.stringify(body) });
  }

  async function listedBy(key: string): Promise<{
    data: { id: string; type: string; idempotencyKey: string | null }[];
    pagination: { totalItems: number; totalPages: number };
  }> {
    const res = await fetch(
      `/api/transactions?Page=1&PageSize=100&IdempotencyKey=${encodeURIComponent(key)}`,
    );
    expect(res.status).toBe(200);
    return res.json();
  }

  it('lists the rows each money request wrote by the key it was sent with', async () => {
    const [deposit, withdrawal, internal, external] = Array.from({ length: 4 }, () =>
      crypto.randomUUID(),
    );

    const deposited = await send('/api/transactions/deposit', deposit, {
      accountId: MAIN_ACCOUNT_ID,
      amount: 25,
    });
    expect(deposited.status).toBe(201);
    const depositId = (await deposited.json()).data.transaction.id;

    const out = { accountId: MAIN_ACCOUNT_ID, amount: 5 };
    const withdrawalAuth = await minted('/api/transactions/withdraw/authorizations', out);
    expect((await send('/api/transactions/withdraw', withdrawal, out, withdrawalAuth)).status).toBe(
      201,
    );

    const move = { fromAccountId: MAIN_ACCOUNT_ID, toAccountId: SAVINGS_ACCOUNT_ID, amount: 10 };
    const moveAuth = await minted('/api/transfers/internal/authorizations', move);
    expect((await send('/api/transfers/internal', internal, move, moveAuth)).status).toBe(201);

    const payment = { fromAccountId: MAIN_ACCOUNT_ID, recipientAzureTag: 'friend', amount: 7 };
    const paymentAuth = await minted('/api/transfers/authorizations', payment);
    expect((await send('/api/transfers', external, payment, paymentAuth)).status).toBe(201);

    const byDeposit = await listedBy(deposit);
    expect(byDeposit.data.map((t) => t.id)).toEqual([depositId]);
    expect(byDeposit.pagination).toMatchObject({ totalItems: 1, totalPages: 1 });
    expect((await listedBy(withdrawal)).data.map((t) => t.type)).toEqual(['Withdrawal']);
    // Both rows of a move between the caller's own accounts carry its key.
    expect((await listedBy(internal)).data.map((t) => t.type).sort()).toEqual([
      'TransferIn',
      'TransferOut',
    ]);
    expect((await listedBy(external)).data.map((t) => t.type)).toEqual(['TransferOut']);
  });

  it('finds the rows by any spelling of the key Guid.TryParse reads as the same key', async () => {
    // Every component starts with a zero, so that the short X spelling is not the full one.
    const n = [...crypto.randomUUID().replace(/-/g, '')]
      .map((digit, at) =>
        at === 0 || at === 8 || at === 12 || (at >= 16 && at % 2 === 0) ? '0' : digit,
      )
      .join('');
    const key = `${n.slice(0, 8)}-${n.slice(8, 12)}-${n.slice(12, 16)}-${n.slice(16, 20)}-${n.slice(20)}`;
    await send('/api/transactions/deposit', key, { accountId: MAIN_ACCOUNT_ID, amount: 25 });

    const bytes = (n.slice(16).match(/../g) as string[]).map((b) => `0x${b}`).join(',');
    const x = `{0x${n.slice(0, 8)},0x${n.slice(8, 12)},0x${n.slice(12, 16)},{${bytes}}}`;
    // The X spelling with every leading zero left out, and with a + or a second 0x after each
    // 0x: the API reads each as the same key.
    const xShort = x.replace(/0x0+(?=[0-9a-f])/g, '0x');
    const xPlus = x.replace(/0x/g, '0x+0x');
    const spellings = [key.toUpperCase(), n, `{${key}}`, `(${key})`, x, xShort, xPlus];
    for (const spelling of spellings) {
      expect((await listedBy(spelling)).pagination.totalItems, spelling).toBe(1);
    }
  });

  it('answers a key no row carries with an empty page, not a refusal', async () => {
    const page = await listedBy(crypto.randomUUID());

    expect(page.data).toEqual([]);
    expect(page.pagination).toMatchObject({ totalItems: 0, totalPages: 0 });
  });

  it('writes no second row when the same key and body are replayed', async () => {
    const key = crypto.randomUUID();
    const body = { accountId: MAIN_ACCOUNT_ID, amount: 25 };

    expect((await send('/api/transactions/deposit', key, body)).status).toBe(201);
    const again = await send('/api/transactions/deposit', key, body);

    expect(again.headers.get('Idempotency-Replayed')).toBe('true');
    expect(mockState.transactions.filter((t) => t.idempotencyKey === key)).toHaveLength(1);
  });

  it('ships the key on every row it answers with, and null on a row no request wrote', async () => {
    const key = crypto.randomUUID();
    const deposited = await send('/api/transactions/deposit', key, {
      accountId: MAIN_ACCOUNT_ID,
      amount: 25,
    });

    expect((await deposited.json()).data.transaction.idempotencyKey).toBe(key);
    const found = (await listedBy(key)).data;
    expect(found.map((row) => row.idempotencyKey)).toEqual([key]);
    // The seeded history was written by no request.
    const seeded = mockState.transactions.find((t) => t.idempotencyKey === null);
    expect(seeded).toBeDefined();
  });

  it.each(['not-a-key', '', ' '])(
    'refuses IdempotencyKey=%j the way the model binder does',
    async (sent) => {
      // `Guid? IdempotencyKey` on `TransactionFilter`, bound like AccountId, and an empty value is
      // refused like garbage (EmptyQueryValueRejectingBinder). Measured through the API's own
      // pipeline in TransactionsByIdempotencyKeyTests.
      const res = await fetch(`/api/transactions?IdempotencyKey=${encodeURIComponent(sent)}`);

      expect(res.status).toBe(400);
      expect((await res.json()).errors.IdempotencyKey).toEqual([
        `The value '${sent}' is not valid for IdempotencyKey.`,
      ]);
    },
  );
});

describe('money endpoints validate the amount before touching a balance', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
  });

  const deposit = (amount: unknown) =>
    fetch('/api/transactions/deposit', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': '3f2504e0-4f89-41d3-9a0c-0305e82c3397',
      },
      body: JSON.stringify({ accountId: MAIN_ACCOUNT_ID, amount }),
    });

  it.each([-100, 0, 0.001, 100_001, 'ten', null])(
    'rejects an amount of %p and leaves the balance alone',
    async (amount) => {
      // The arithmetic is symmetric and the intent is not: a negative deposit DEBITS the account,
      // and a negative withdrawal or transfer credits it. Only the internal transfer used to say so.
      const opening = mockState.accounts.find((a) => a.id === MAIN_ACCOUNT_ID)?.balance;
      const before = mockState.transactions.length;

      const res = await deposit(amount);

      expect(res.status).toBe(400);
      expect(mockState.accounts.find((a) => a.id === MAIN_ACCOUNT_ID)?.balance).toBe(opening);
      expect(mockState.transactions.length).toBe(before);
    },
  );

  /*
    The scale rule reaches the OLDER endpoints too, and that is the point of fixing it in the
    shared helper rather than on the endpoint a review happened to name: every money validator on
    the API carries `.ValidMoneyScale()`, so a mock that enforces it on one door is a mock whose
    doors disagree. `0.001` above is in the list and stays there -- it breaks the RANGE as well,
    and the binder answers first.
  */
  it('refuses a third decimal on the deposit too, with the validator envelope', async () => {
    const res = await deposit(10.001);

    expect(res.status).toBe(400);
    const body = (await res.json()) as { title: string; errors: Record<string, string[]> };
    expect(body.title).toBe('Validation Failed');
    expect(body.errors).toEqual({ amount: ['Amount cannot have more than 2 decimal places.'] });
  });
});
