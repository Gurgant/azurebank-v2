import { describe, expect, it } from 'vitest';
import { mockState, MOCK_PIN, seedMockSession } from './state';

/**
 * Executable contract for the stateful withdraw handler — the bespoke logic the dialog tests only
 * stub via server.use(): the PIN lockout state machine, the namespaced idempotency store
 * (withdraw| must not collide with deposit|), and the failure order.
 *
 * THE ORDER INVERTED ON 2026-09-21 (ADR-0056), and it used to read "PIN before funds". It is now
 * FUNDS before the authorisation, and there is no PIN on this endpoint at all — the PIN moved to
 * the mint. Measured against the running API that day, one request each
 * (`evidence-withdraw-after-2026-09-21.txt` in the working-state repo):
 *
 *   withdraw 5000 over a 100 balance, NO authorisation  -> 422 INSUFFICIENT_FUNDS
 *   withdraw 10, NO authorisation                       -> 401 AUTHORIZATION_REQUIRED
 *   withdraw 20 with the one minted for 10              -> 401 AUTHORIZATION_INVALID
 *   mint 10, WRONG PIN                                  -> 401 INVALID_PIN
 *
 * The PIN cases below therefore drive the MINT. They were not deleted: they are the only statement
 * in this file that a withdrawal costs a PIN attempt at all, and they still hold — one endpoint
 * later.
 */

const URL = '/api/transactions/withdraw';
const MINT = '/api/transactions/withdraw/authorizations';
const FIXED = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

function mint(accountId: string, amount: number, pin: unknown = MOCK_PIN) {
  return fetch(MINT, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ accountId, amount, pin }),
  });
}

/** Mints and returns the reference, failing loudly rather than handing back `undefined`. */
async function mintedId(accountId: string, amount: number): Promise<string> {
  const res = await mint(accountId, amount);
  expect(res.status).toBe(201);
  return (await res.json()).data.authorizationId as string;
}

function withdraw(key: string | null, body: unknown, authorizationId?: string | null) {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (key) headers['Idempotency-Key'] = key;
  if (authorizationId) headers['Step-Up-Authorization'] = authorizationId;
  return fetch(URL, { method: 'POST', headers, body: JSON.stringify(body) });
}

function accountId() {
  return mockState.accounts[0].id;
}

describe('the withdrawal mint (where the PIN now lives)', () => {
  it('locks after 3 wrong PINs: 401, 401, 429 — then even a correct PIN stays 429', async () => {
    const acct = accountId();
    const r1 = await mint(acct, 100, '000000');
    const r2 = await mint(acct, 100, '000000');
    const r3 = await mint(acct, 100, '000000');
    const r4 = await mint(acct, 100, MOCK_PIN);

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
    await mint(acct, 10, '000000'); // attempts=1
    expect((await mint(acct, 10, MOCK_PIN)).status).toBe(201); // success resets the counter

    const m1 = await mint(acct, 10, '000000');
    const m2 = await mint(acct, 10, '000000');
    expect(m1.status).toBe(401);
    expect(m2.status).toBe(401); // still 401 (attempts 1→2), NOT 429 → the counter had reset
  });

  it('422 PIN_REQUIRED when the session user has no PIN', async () => {
    seedMockSession({
      id: 'x',
      email: 'e@e.dev',
      firstName: 'F',
      lastName: 'L',
      azureTag: 't',
      hasPin: false,
    });
    const res = await mint(accountId(), 100);
    expect(res.status).toBe(422);
    expect((await res.json()).errorCode).toBe('PIN_REQUIRED');
  });

  it('mints for MORE than the balance — a mint checks no funds (ADR-0050 D4)', async () => {
    // Measured: 201. Refusing here would teach a caller the balance at no cost and add a second
    // place for the two checks to disagree; the 422 arrives at the withdrawal.
    const res = await mint(accountId(), 50_000);
    expect(res.status).toBe(201);
  });
});

describe('withdraw handler (authorisation + idempotency contract)', () => {
  it('422 INSUFFICIENT_FUNDS (with available) BEFORE any authorisation is looked for', async () => {
    // No Step-Up-Authorization header at all, deliberately: this is what pins ADR-0056 D4. If the
    // two rungs ever swap, this answers 401 AUTHORIZATION_REQUIRED and says so.
    const res = await withdraw(crypto.randomUUID(), {
      accountId: accountId(),
      // Over the balance but INSIDE the contract's range: >100,000 is rejected as invalid input
      // before the funds check, exactly as FluentValidation rejects it before the service.
      amount: 50_000,
    });
    expect(res.status).toBe(422);
    const body = await res.json();
    expect(body.errorCode).toBe('INSUFFICIENT_FUNDS');
    expect(body.available).toBe(mockState.accounts[0].balance);
  });

  it('401 AUTHORIZATION_REQUIRED when an affordable withdrawal presents none', async () => {
    const res = await withdraw(crypto.randomUUID(), { accountId: accountId(), amount: 10 });
    expect(res.status).toBe(401);
    expect((await res.json()).errorCode).toBe('AUTHORIZATION_REQUIRED');
  });

  it('401 AUTHORIZATION_INVALID when the authorisation was minted for another amount', async () => {
    const acct = accountId();
    const auth = await mintedId(acct, 10);
    // The amount is inside the binding hash: one cheap authorisation must not pay for a larger
    // withdrawal. This is the single attack the binding exists to refuse.
    const res = await withdraw(crypto.randomUUID(), { accountId: acct, amount: 20 }, auth);
    expect(res.status).toBe(401);
    expect((await res.json()).errorCode).toBe('AUTHORIZATION_INVALID');
  });

  it('401 AUTHORIZATION_INVALID when the same authorisation is presented twice', async () => {
    const acct = accountId();
    const auth = await mintedId(acct, 10);
    expect(
      (await withdraw(crypto.randomUUID(), { accountId: acct, amount: 10 }, auth)).status,
    ).toBe(201);
    const second = await withdraw(crypto.randomUUID(), { accountId: acct, amount: 10 }, auth);
    expect(second.status).toBe(401);
    expect((await second.json()).errorCode).toBe('AUTHORIZATION_INVALID');
  });

  it('replays same key+body byte-identically and 422s on same key+different body', async () => {
    const acct = accountId();
    const body = { accountId: acct, amount: 100 };
    const first = await withdraw(FIXED, body, await mintedId(acct, 100));
    const firstText = await first.text();

    /*
      THE REPLAY CARRIES NO AUTHORISATION, and that is the assertion rather than a shortcut. The
      fingerprint is taken over the BODY only, and the authorisation rides a header — so a retry is
      byte-identical to the middleware even though its first attempt spent a single-use reference
      that cannot be presented again. Putting the authorisation in the body would make every honest
      retry a 422 IDEMPOTENCY_KEY_REUSE.
    */
    const replay = await withdraw(FIXED, body);
    const replayText = await replay.text();

    expect(first.status).toBe(201);
    expect(first.headers.get('Idempotency-Replayed')).toBeNull();
    expect(replay.headers.get('Idempotency-Replayed')).toBe('true');
    expect(replayText).toBe(firstText); // byte identity

    const reuse = await withdraw(FIXED, { accountId: acct, amount: 200 });
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
    const wd = await withdraw(FIXED, { accountId: acct, amount: 25 }, await mintedId(acct, 25));

    expect(dep.status).toBe(201);
    expect(wd.status).toBe(201);
    expect(wd.headers.get('Idempotency-Replayed')).toBeNull(); // NOT a replay of the deposit
    expect((await wd.json()).data.transaction.type).toBe('Withdrawal');
  });
});
