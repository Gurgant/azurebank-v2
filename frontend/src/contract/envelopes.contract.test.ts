import { beforeAll, describe, expect, it } from 'vitest';
import { call, login } from './client';

/**
 * The SUCCESS envelopes — the positive controls.
 *
 * These already agreed when the suite was written, and that is the point of including them: a gate
 * made only of known failures proves nothing about the parts that work, and cannot tell "the mock
 * was fixed" from "the assertion was written to match whatever the mock did".
 *
 * Note the two list shapes are genuinely DIFFERENT, and both are deliberate: accounts return
 * `{data, message}` while transactions return `{data, pagination}` with no `message`. A reader who
 * assumed one envelope for the whole API would be wrong.
 */

beforeAll(async () => {
  expect((await login()).status).toBe(200);
});

describe('contract: success envelopes', () => {
  it('returns accounts as {data, message} with the number already masked', async () => {
    // Observed: {"data":[{"id","accountNumber":"AB-****-****-01","name","type","balance",
    //                     "isPrimary","createdAt"}],"message":null}
    const { status, body } = await call('/api/accounts');
    const envelope = body as { data?: Record<string, unknown>[]; pagination?: unknown };

    expect(status).toBe(200);
    expect(Array.isArray(envelope.data)).toBe(true);
    expect(envelope).toHaveProperty('message');
    expect(envelope.pagination).toBeUndefined();

    const account = envelope.data?.[0] ?? {};
    expect(Object.keys(account).sort()).toEqual(
      ['accountNumber', 'balance', 'createdAt', 'id', 'isPrimary', 'name', 'type'].sort(),
    );
    /*
      Masked SERVER-side, and asserted here because it is a privacy property rather than a
      formatting one: the full number exists only behind the step-up-gated /full-number endpoint,
      so a mock that returned it unmasked would let a test "prove" a reveal that never happened.
    */
    expect(account.accountNumber).toMatch(/^AB-\*{4}-\*{4}-\d{2}$/);
  });

  it('returns transactions as {data, pagination} with the pagination keys spelled out', async () => {
    // Observed: {"data":[],"pagination":{"page":1,"pageSize":2,"totalItems":0,"totalPages":0,
    //                                    "hasNextPage":false,"hasPreviousPage":false}}
    const { status, body } = await call('/api/transactions?page=1&pageSize=2');
    const envelope = body as { data?: unknown[]; pagination?: Record<string, unknown> };

    expect(status).toBe(200);
    expect(Array.isArray(envelope.data)).toBe(true);
    expect(Object.keys(envelope.pagination ?? {}).sort()).toEqual(
      ['hasNextPage', 'hasPreviousPage', 'page', 'pageSize', 'totalItems', 'totalPages'].sort(),
    );
  });
});
