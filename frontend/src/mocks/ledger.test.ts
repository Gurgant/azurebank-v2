import { beforeEach, describe, expect, it } from 'vitest';
import { MAIN_ACCOUNT_ID, SAVINGS_ACCOUNT_ID, mockState, resetMockState } from './state';

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

describe('money endpoints validate the amount before touching a balance', () => {
  beforeEach(() => {
    resetMockState();
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
});
