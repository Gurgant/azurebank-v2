import { describe, expect, it } from 'vitest';
import { liveUntil, readMovement, type MovementLookupArg } from './movementByKey';

/**
 * What a page of the listing by key says about a send whose answer was lost (ADR-0022). Each
 * clause of `readMovement` is held by a case of its own: a found movement is the one thing that
 * lets a flow say "went through" without a receipt, and an empty page is the one thing that lets
 * it offer a new key.
 */

const KEY = '7b0e2a54-3c1d-4f6e-9a8b-0c1d2e3f4a5b';
const ACCOUNT = '019f7b3f-0000-7000-8000-0000000000a1';

const deposit: MovementLookupArg = { idempotencyKey: KEY, accountId: ACCOUNT, type: 'Deposit' };

function row(overrides: Record<string, unknown> = {}) {
  return {
    id: '019f7b3f-0000-7000-8000-000000000d10',
    transactionNumber: 'TXN-20260722-000016',
    type: 'Deposit',
    amount: 100,
    balanceAfter: 1350.5,
    description: null,
    recipientAzureTag: null,
    senderAzureTag: null,
    status: 'Completed',
    createdAt: '2026-07-22T10:16:00.0000000Z',
    idempotencyKey: KEY,
    ...overrides,
  };
}

function page(data: unknown[], totalItems = data.length) {
  return {
    data,
    pagination: {
      page: 1,
      pageSize: 20,
      totalItems,
      totalPages: Math.ceil(totalItems / 20),
      hasNextPage: false,
      hasPreviousPage: false,
    },
  };
}

describe('readMovement', () => {
  it('finds the row the key wrote, whatever the case of the key', () => {
    const upper = { ...deposit, idempotencyKey: KEY.toUpperCase() };

    expect(readMovement(page([row()]), upper)).toEqual({
      kind: 'found',
      movement: {
        id: '019f7b3f-0000-7000-8000-000000000d10',
        type: 'Deposit',
        transactionNumber: 'TXN-20260722-000016',
        amount: 100,
        createdAt: '2026-07-22T10:16:00.0000000Z',
      },
    });
  });

  it('keeps no key in what it finds: the stored result never carries it', () => {
    const reading = readMovement(page([row()]), deposit);

    expect(JSON.stringify(reading)).not.toContain(KEY);
    expect(reading.kind === 'found' && Object.keys(reading.movement).sort()).toEqual([
      'amount',
      'createdAt',
      'id',
      'transactionNumber',
      'type',
    ]);
  });

  it('takes the newest of the rows that match: the listing is newest first', () => {
    const newer = row({ transactionNumber: 'TXN-NEWER' });
    const older = row({ transactionNumber: 'TXN-OLDER', createdAt: '2026-07-22T09:00:00Z' });

    const reading = readMovement(page([newer, older]), deposit);

    expect(reading.kind === 'found' && reading.movement.transactionNumber).toBe('TXN-NEWER');
  });

  it("says nothing of a page an old app sends, whose rows carry no key: that is the account's latest page", () => {
    const keyless = row({ idempotencyKey: undefined });
    const nullKey = row({ idempotencyKey: null });

    expect(readMovement(page([keyless]), deposit)).toEqual({ kind: 'unknown' });
    expect(readMovement(page([nullKey]), deposit)).toEqual({ kind: 'unknown' });
  });

  it('says nothing of a row with the key and another type: one key can list more than one request', () => {
    expect(readMovement(page([row({ type: 'Withdrawal' })]), deposit)).toEqual({
      kind: 'unknown',
    });
  });

  it('says nothing of a row with the key that is not Completed', () => {
    expect(readMovement(page([row({ status: 'Pending' })]), deposit)).toEqual({
      kind: 'unknown',
    });
  });

  it('says nothing of a row with another key', () => {
    const other = row({ idempotencyKey: '00000000-1111-4222-8333-444444444444' });

    expect(readMovement(page([other]), deposit)).toEqual({ kind: 'unknown' });
  });

  it('reads an empty page that counts no rows as none', () => {
    expect(readMovement(page([], 0), deposit)).toEqual({ kind: 'none' });
  });

  it('does not read an empty page that counts rows as none: they are on another page', () => {
    expect(readMovement(page([], 3), deposit)).toEqual({ kind: 'unknown' });
  });

  it.each([
    ['a page with no data', { pagination: page([]).pagination }],
    ['a page with no pagination', { data: [] }],
    ['no page at all', null],
    ['a page whose row is not a row', page([{ id: 'not a row' }])],
    ['an envelope', { data: page([row()]), message: null }],
  ])('says nothing of %s, and does not throw', (_name, raw) => {
    expect(() => readMovement(raw, deposit)).not.toThrow();
    expect(readMovement(raw, deposit)).toEqual({ kind: 'unknown' });
  });
});

describe('liveUntil', () => {
  it('is 3 min 5 s after the latest send began: the abort, then the stale age', () => {
    expect(liveUntil({ lastSentAt: 1_000, everyAuthorisationRefused: false })).toBe(186_000);
  });

  it('is already past when every authorisation presented under the key was refused', () => {
    expect(liveUntil({ lastSentAt: 1_000, everyAuthorisationRefused: true })).toBe(-Infinity);
  });
});
