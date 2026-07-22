import { describe, expect, it } from 'vitest';
import { mockState, MOCK_PIN, seedMockSession } from './state';

/**
 * Executable contract for the stateful withdraw handler — the bespoke logic the dialog
 * tests only stub via server.use(): the PIN lockout state machine, the namespaced
 * idempotency store (withdraw| must not collide with deposit|), and the failure order
 * (PIN before funds), mirroring TransactionService.WithdrawAsync.
 */

const URL = '/api/transactions/withdraw';
const FIXED = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

function withdraw(key: string | null, body: unknown) {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (key) headers['Idempotency-Key'] = key;
  return fetch(URL, { method: 'POST', headers, body: JSON.stringify(body) });
}

function accountId() {
  return mockState.accounts[0].id;
}

describe('withdraw handler (PIN + idempotency contract)', () => {
  it('locks after 3 wrong PINs: 401, 401, 429 — then even a correct PIN stays 429', async () => {
    const acct = accountId();
    const r1 = await withdraw(crypto.randomUUID(), { accountId: acct, amount: 100, pin: '000000' });
    const r2 = await withdraw(crypto.randomUUID(), { accountId: acct, amount: 100, pin: '000000' });
    const r3 = await withdraw(crypto.randomUUID(), { accountId: acct, amount: 100, pin: '000000' });
    const r4 = await withdraw(crypto.randomUUID(), { accountId: acct, amount: 100, pin: MOCK_PIN });

    expect(r1.status).toBe(401);
    expect((await r1.json()).errorCode).toBe('INVALID_PIN');
    expect(r2.status).toBe(401);
    expect(r3.status).toBe(429);
    const locked = await r3.json();
    expect(locked.errorCode).toBe('PIN_LOCKED');
    expect(locked.retryAfterSeconds).toBeGreaterThan(0);
    expect(r4.status).toBe(429); // locked — a correct PIN is refused before it is even checked
  });

  it('a correct PIN resets the wrong-attempt counter', async () => {
    const acct = accountId();
    await withdraw(crypto.randomUUID(), { accountId: acct, amount: 10, pin: '000000' }); // attempts=1
    const ok = await withdraw(crypto.randomUUID(), { accountId: acct, amount: 10, pin: MOCK_PIN });
    expect(ok.status).toBe(201); // success resets the counter

    const m1 = await withdraw(crypto.randomUUID(), { accountId: acct, amount: 10, pin: '000000' });
    const m2 = await withdraw(crypto.randomUUID(), { accountId: acct, amount: 10, pin: '000000' });
    expect(m1.status).toBe(401);
    expect(m2.status).toBe(401); // still 401 (attempts 1→2), NOT 429 → the counter had reset
  });

  it('replays same key+body byte-identically and 422s on same key+different body', async () => {
    const acct = accountId();
    const body = { accountId: acct, amount: 100, pin: MOCK_PIN };
    const first = await withdraw(FIXED, body);
    const firstText = await first.text();
    const replay = await withdraw(FIXED, body);
    const replayText = await replay.text();

    expect(first.status).toBe(201);
    expect(first.headers.get('Idempotency-Replayed')).toBeNull();
    expect(replay.headers.get('Idempotency-Replayed')).toBe('true');
    expect(replayText).toBe(firstText); // byte identity

    const reuse = await withdraw(FIXED, { accountId: acct, amount: 200, pin: MOCK_PIN });
    expect(reuse.status).toBe(422);
    expect((await reuse.json()).errorCode).toBe('IDEMPOTENCY_KEY_REUSE');
  });

  it('a deposit and a withdraw sharing an Idempotency-Key do NOT collide (namespaced store)', async () => {
    const acct = accountId();
    const dep = await fetch('/api/transactions/deposit', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': FIXED },
      body: JSON.stringify({ accountId: acct, amount: 50 }),
    });
    const wd = await withdraw(FIXED, { accountId: acct, amount: 25, pin: MOCK_PIN });

    expect(dep.status).toBe(201);
    expect(wd.status).toBe(201);
    expect(wd.headers.get('Idempotency-Replayed')).toBeNull(); // NOT a replay of the deposit
    expect((await wd.json()).data.transaction.type).toBe('Withdrawal');
  });

  it('422 PIN_REQUIRED when the session user has no PIN (checked before funds)', async () => {
    seedMockSession({
      id: 'x',
      email: 'e@e.dev',
      firstName: 'F',
      lastName: 'L',
      azureTag: 't',
      hasPin: false,
    });
    const res = await withdraw(crypto.randomUUID(), {
      accountId: accountId(),
      amount: 100,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(422);
    expect((await res.json()).errorCode).toBe('PIN_REQUIRED');
  });

  it('422 INSUFFICIENT_FUNDS (with available) when the amount exceeds the balance', async () => {
    const res = await withdraw(crypto.randomUUID(), {
      accountId: accountId(),
      amount: 999_999,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(422);
    const body = await res.json();
    expect(body.errorCode).toBe('INSUFFICIENT_FUNDS');
    expect(body.available).toBe(mockState.accounts[0].balance);
  });
});
