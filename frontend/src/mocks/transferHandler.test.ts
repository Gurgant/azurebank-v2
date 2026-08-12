import { describe, expect, it } from 'vitest';
import { MOCK_PIN, mockState, seedMockSession } from './state';

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

describe('transfer handler (in-band PIN + failure order + idempotency)', () => {
  it('does NOT step up at level 1 — the PIN travels in the body instead (ADR-0041)', async () => {
    /*
      This asserted the exact opposite until ADR-0041, and the inversion IS the change: a 403 here
      meant the BFF was deciding, off a session flag that stayed hot for five minutes and that a
      caller reaching the API directly never met at all.
    */
    seedMockSession();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).not.toBe(403);
    expect(res.headers.get('X-Auth-Level-Required')).toBeNull();
  });

  it('401 INVALID_PIN for a wrong PIN, whatever the auth level', async () => {
    seedMockSession();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: '000000',
    });
    expect(res.status).toBe(401);
    expect((await res.json()).errorCode).toBe('INVALID_PIN');
  });

  it('idempotency is checked BEFORE the PIN (no key → 400, even with the right PIN)', async () => {
    seedMockSession();
    const res = await transfer(null, {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });
    /*
      The API's idempotency filter is an action filter, so it runs before the controller action and
      therefore before the service's PIN check. MEASURED on the real pipeline, not inferred from
      this handler: TransferPinVerificationTests.IdempotencyKeyIsCheckedBeforeThePin observes
      400 IDEMPOTENCY_KEY_MISSING for a request carrying a correct PIN and no key.
    */
    expect(res.status).toBe(400);
    expect((await res.json()).errorCode).toBe('IDEMPOTENCY_KEY_MISSING');
  });

  it('422 SELF_TRANSFER_NOT_ALLOWED when the recipient is the caller', async () => {
    seedMockSession(); // demo_user
    elevate();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'demo_user',
      amount: 10,
      pin: MOCK_PIN,
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
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
  });

  it('422 INSUFFICIENT_FUNDS when the amount exceeds the balance', async () => {
    elevate();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      // Over the balance but INSIDE the contract's range: >100,000 is now rejected as invalid
      // input before the funds check, exactly as FluentValidation rejects it before the service.
      amount: 50_000,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(422);
    expect((await res.json()).errorCode).toBe('INSUFFICIENT_FUNDS');
  });

  it('debits the sender and replays same key+body; 422s on same key+different body', async () => {
    elevate();
    const body = { fromAccountId: acct(), recipientAzureTag: 'friend', amount: 100, pin: MOCK_PIN };
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

const I_URL = '/api/transfers/internal';

function internal(key: string | null, body: unknown) {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (key) headers['Idempotency-Key'] = key;
  return fetch(I_URL, { method: 'POST', headers, body: JSON.stringify(body) });
}

function acct2() {
  return mockState.accounts[1].id;
}

describe('internal transfer handler (own accounts, double-entry)', () => {
  it('does NOT step up at level 1 either — same in-band PIN as the external transfer', async () => {
    const res = await internal(crypto.randomUUID(), {
      fromAccountId: acct(),
      toAccountId: acct2(),
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).not.toBe(403);
    expect(res.headers.get('X-Auth-Level-Required')).toBeNull();
  });

  it('400 with a toAccountId field error when source == destination', async () => {
    /*
      This asserted `422 SAME_ACCOUNT_TRANSFER`, a response the API cannot produce.

      `InternalTransferRequestValidator` carries `ToAccountId.NotEqual(x => x.FromAccountId)` and
      `ValidateAndThrowAsync` runs BEFORE the service, so the service's own
      `BusinessRuleException(SAME_ACCOUNT_TRANSFER)` never reaches the wire — `ErrorCodes.cs`
      documents it as defence-in-depth for non-HTTP callers. Measured live on 2026-07-31:
        400 {"title":"Validation Failed","detail":"One or more validation errors occurred.",
             "errors":{"toAccountId":["Cannot transfer to the same account."]}}
      with NO errorCode. The test was pinning an invented code as if it were the contract.
    */
    elevate();
    const res = await internal(crypto.randomUUID(), {
      fromAccountId: acct(),
      toAccountId: acct(),
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(400);
    const body = await res.json();
    expect(body.errorCode).toBeUndefined();
    expect(body.errors.toAccountId).toEqual(['Cannot transfer to the same account.']);
  });

  it('404 ACCOUNT_NOT_FOUND for an unknown destination OR source account', async () => {
    elevate();
    const badTo = await internal(crypto.randomUUID(), {
      fromAccountId: acct(),
      toAccountId: '019f7b3f-0000-7000-8000-0000000000ff',
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(badTo.status).toBe(404);
    expect((await badTo.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');

    // The other half of the ownership guard: an unknown SOURCE account.
    const badFrom = await internal(crypto.randomUUID(), {
      fromAccountId: '019f7b3f-0000-7000-8000-0000000000ff',
      toAccountId: acct2(),
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(badFrom.status).toBe(404);
    expect((await badFrom.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
  });

  it('422 INSUFFICIENT_FUNDS when the amount exceeds the source balance', async () => {
    elevate();
    const res = await internal(crypto.randomUUID(), {
      fromAccountId: acct(),
      toAccountId: acct2(),
      // Over the balance but INSIDE the contract's range: >100,000 is now rejected as invalid
      // input before the funds check, exactly as FluentValidation rejects it before the service.
      amount: 50_000,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(422);
    expect((await res.json()).errorCode).toBe('INSUFFICIENT_FUNDS');
  });

  it('400 VALIDATION_ERROR for a non-positive amount, BEFORE any balance change', async () => {
    elevate();
    const fromBefore = mockState.accounts[0].balance;
    const toBefore = mockState.accounts[1].balance;

    const zero = await internal(crypto.randomUUID(), {
      fromAccountId: acct(),
      toAccountId: acct2(),
      amount: 0,
      pin: MOCK_PIN,
    });
    expect(zero.status).toBe(400);
    /*
      `Amount`, PascalCase. `[MoneyRange]` is a DataAnnotation, so an out-of-range amount is a
      model-state failure keyed by the CLR property — measured on the real stack (2026-08-04) for
      amounts 0, 0.005, 100000.01 and 250000, all four answering identically:
        {"title":"One or more validation errors occurred.",
         "errors":{"Amount":["Amount must be between $0.01 and $100,000.00"]}}
      (A bad decimal SCALE is the other envelope, keyed lowercase `amount` — see `rejectBadAmount`.)
    */
    expect((await zero.json()).errors.Amount).toEqual([
      'Amount must be between $0.01 and $100,000.00',
    ]);

    // A negative amount must NOT invert the transfer (credit source / debit destination).
    const neg = await internal(crypto.randomUUID(), {
      fromAccountId: acct(),
      toAccountId: acct2(),
      amount: -50,
      pin: MOCK_PIN,
    });
    expect(neg.status).toBe(400);
    expect(mockState.accounts[0].balance).toBe(fromBefore);
    expect(mockState.accounts[1].balance).toBe(toBefore);
  });

  it('double-entry: debits source, credits destination, writes both ledger rows, replays bytes', async () => {
    elevate();
    const fromBefore = mockState.accounts[0].balance; // 1250.5
    const toBefore = mockState.accounts[1].balance; // 830
    const body = { fromAccountId: acct(), toAccountId: acct2(), amount: 100, pin: MOCK_PIN };

    const first = await internal(FIXED, body);
    const firstText = await first.text();
    expect(first.status).toBe(201);
    expect(mockState.accounts[0].balance).toBe(fromBefore - 100);
    expect(mockState.accounts[1].balance).toBe(toBefore + 100);

    // Two linked ledger rows, each carrying its own account's post-transfer balance.
    const [out, incoming] = mockState.transactions.slice(-2);
    expect(out.type).toBe('TransferOut');
    expect(out.amount).toBe(100);
    expect(out.balanceAfter).toBe(fromBefore - 100);
    expect(incoming.type).toBe('TransferIn');
    expect(incoming.amount).toBe(100);
    expect(incoming.balanceAfter).toBe(toBefore + 100);

    const replay = await internal(FIXED, body);
    expect(replay.status).toBe(201);
    expect(replay.headers.get('Idempotency-Replayed')).toBe('true');
    expect(await replay.text()).toBe(firstText); // byte-identical stored response
    expect(mockState.accounts[0].balance).toBe(fromBefore - 100); // NOT double-applied
    expect(mockState.accounts[1].balance).toBe(toBefore + 100);
  });

  it('the internal| idempotency namespace does not cross-replay the external transfer|', async () => {
    elevate();
    const ext = await transfer(FIXED, {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 25,
      pin: MOCK_PIN,
    });
    expect(ext.status).toBe(201);

    // Same key to the INTERNAL endpoint must be a fresh execution, not a replay of the external.
    const int = await internal(FIXED, {
      fromAccountId: acct(),
      toAccountId: acct2(),
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(int.status).toBe(201);
    expect(int.headers.get('Idempotency-Replayed')).toBeNull();
    expect((await int.json()).data.toAccountNewBalance).toBeDefined(); // an internal-shaped body
  });
});
