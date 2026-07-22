import { describe, expect, it } from 'vitest';
import { mockState, seedMockSession } from './state';

/**
 * Executable contract for the recipient-lookup + stateful transfer handlers: the exact-match
 * lookup (self/unknown masked as exists:false), the level-2 403 gate, and the post-elevation
 * failure order (SELF_TRANSFER → recipient ACCOUNT_NOT_FOUND → INSUFFICIENT_FUNDS → success),
 * plus namespaced idempotency.
 */

const T_URL = '/api/transfers';
const FIXED = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

function lookup(tag: string) {
  return fetch(`/api/users/${tag}`);
}

function transfer(key: string | null, body: unknown) {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (key) headers['Idempotency-Key'] = key;
  return fetch(T_URL, { method: 'POST', headers, body: JSON.stringify(body) });
}

function elevate() {
  mockState.authLevel = 2; // stand-in for a successful /bff/auth/verify-pin
}

function acct() {
  return mockState.accounts[0].id;
}

describe('recipient lookup (exact-match, ADR-0014)', () => {
  it('resolves a known handle and masks unknown/self as exists:false', async () => {
    const found = await (await lookup('friend')).json();
    expect(found.data).toMatchObject({
      azureTag: 'friend',
      displayName: 'A. Friend',
      exists: true,
    });

    const unknown = await (await lookup('nobody')).json();
    expect(unknown.data.exists).toBe(false);

    seedMockSession(); // MOCK_USER azureTag 'demo_user'
    const self = await (await lookup('demo_user')).json();
    expect(self.data.exists).toBe(false); // self is masked identically to unknown
  });
});

describe('transfer handler (step-up gate + failure order + idempotency)', () => {
  it('403s at level 1 with the bare STEP_UP_REQUIRED shape + headers', async () => {
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
    });
    expect(res.status).toBe(403);
    expect(res.headers.get('X-Auth-Level-Required')).toBe('2');
    expect((await res.json()).type).toBe('STEP_UP_REQUIRED');
  });

  it('runs the step-up gate BEFORE idempotency (no key at level 1 → 403, not 400)', async () => {
    const res = await transfer(null, {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
    });
    // If idempotency ran first, a missing key would be 400 IDEMPOTENCY_KEY_MISSING.
    expect(res.status).toBe(403);
  });

  it('422 SELF_TRANSFER_NOT_ALLOWED when the recipient is the caller', async () => {
    seedMockSession(); // demo_user
    elevate();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'demo_user',
      amount: 10,
    });
    expect(res.status).toBe(422);
    expect((await res.json()).errorCode).toBe('SELF_TRANSFER_NOT_ALLOWED');
  });

  it('404 ACCOUNT_NOT_FOUND for an unknown recipient', async () => {
    elevate();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'ghost',
      amount: 10,
    });
    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
  });

  it('422 INSUFFICIENT_FUNDS when the amount exceeds the balance', async () => {
    elevate();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 999_999,
    });
    expect(res.status).toBe(422);
    expect((await res.json()).errorCode).toBe('INSUFFICIENT_FUNDS');
  });

  it('debits the sender and replays same key+body; 422s on same key+different body', async () => {
    elevate();
    const body = { fromAccountId: acct(), recipientAzureTag: 'friend', amount: 100 };
    const first = await transfer(FIXED, body);
    const firstText = await first.text();
    expect(first.status).toBe(201);
    expect(mockState.accounts[0].balance).toBe(1150.5); // 1250.50 - 100 debited once

    const replay = await transfer(FIXED, body);
    expect(replay.headers.get('Idempotency-Replayed')).toBe('true');
    expect(await replay.text()).toBe(firstText);
    expect(mockState.accounts[0].balance).toBe(1150.5); // NOT debited twice

    const reuse = await transfer(FIXED, { ...body, amount: 200 });
    expect(reuse.status).toBe(422);
    expect((await reuse.json()).errorCode).toBe('IDEMPOTENCY_KEY_REUSE');
  });
});
