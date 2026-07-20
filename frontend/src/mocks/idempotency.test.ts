/**
 * Executable contract: the stateful idempotency handler mirrors ADR-0009 semantics.
 * These are the semantics the product's useIdempotentMutation will be tested against.
 */

const DEPOSIT_URL = '/api/transactions/deposit';
const KEY = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

function deposit(key: string | null, body: unknown) {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (key) headers['Idempotency-Key'] = key;
  return fetch(DEPOSIT_URL, { method: 'POST', headers, body: JSON.stringify(body) });
}

describe('idempotency handler (ADR-0009 semantics)', () => {
  it('replays the same key + same body BYTE-identically with the replay marker', async () => {
    const first = await deposit(KEY, { accountId: 'a', amount: 50 });
    const firstText = await first.text();

    const second = await deposit(KEY, { accountId: 'a', amount: 50 });
    const secondText = await second.text();

    expect(first.status).toBe(201);
    expect(second.status).toBe(201);
    expect(first.headers.get('Idempotency-Replayed')).toBeNull();
    expect(second.headers.get('Idempotency-Replayed')).toBe('true');
    // Byte identity, not shape equality: the backend replays the stored bytes.
    expect(secondText).toBe(firstText);
  });

  it('rejects the same key with a DIFFERENT body: 422 IDEMPOTENCY_KEY_REUSE', async () => {
    await deposit(KEY, { accountId: 'a', amount: 50 });
    const reuse = await deposit(KEY, { accountId: 'a', amount: 99 });

    expect(reuse.status).toBe(422);
    const body = await reuse.json();
    expect(body.errorCode).toBe('IDEMPOTENCY_KEY_REUSE');
    expect(body.traceId).toMatch(/^[0-9a-f]{32}$/);
  });

  it('rejects a missing key: 400 IDEMPOTENCY_KEY_MISSING', async () => {
    const res = await deposit(null, { accountId: 'a', amount: 50 });

    expect(res.status).toBe(400);
    expect((await res.json()).errorCode).toBe('IDEMPOTENCY_KEY_MISSING');
  });

  it('rejects a malformed key: 400 IDEMPOTENCY_KEY_INVALID', async () => {
    const res = await deposit('not-a-uuid', { accountId: 'a', amount: 50 });

    expect(res.status).toBe(400);
    expect((await res.json()).errorCode).toBe('IDEMPOTENCY_KEY_INVALID');
  });
});
