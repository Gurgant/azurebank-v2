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
    // 201, not merely "not 403": `not.toBe(403)` is satisfied by 400/401/404/500, so it proves the
    // gate is gone without proving the transfer is ALLOWED.
    //
    // An earlier version of this comment blamed that weakness for the ordering defects reaching CI
    // green. MEASURED, and it is not true: with `toBe(201)` applied to the defective handler every
    // test here still passed, because a happy-path request satisfies every gate and therefore cannot
    // observe the order they run in. What was missing was a test that FAILS a gate — the block at
    // the bottom of this file.
    expect(res.status).toBe(201);
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
    expect(res.status).toBe(201);
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

describe('the PIN gates, in the order the API applies them (ADR-0041)', () => {
  /*
    Every expectation below was MEASURED on the real API and pasted beside its assertion.

    The mock had all three wrong and nothing here caught it — not because the assertions were weak
    (they were, separately) but because every transfer test drove a request that PASSES every gate.
    Order is only observable when something fails, so these are the first tests in the file that
    deliberately fail one.
  */

  it('a MALFORMED pin is 400 from model binding, with the PascalCase Pin key', async () => {
    seedMockSession();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: '12',
    });
    // OBSERVED: 400 {"errors":{"Pin":["PIN must be exactly 6 digits."]}} — [Pin] is a
    // DataAnnotation, so the key follows the bound PROPERTY and is PascalCase, unlike
    // FluentValidation's camelCase `toAccountId` on the same endpoint.
    expect(res.status).toBe(400);
    const body = await res.json();
    expect(Object.keys(body.errors)).toEqual(['Pin']);
    expect(body.errors.Pin).toContain('PIN must be exactly 6 digits.');
  });

  it('an ABSENT pin is the missing-required-properties envelope, not a PIN error', async () => {
    seedMockSession();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
    });
    // OBSERVED: System.Text.Json refuses the deserialisation, so it never reaches any validator.
    expect(res.status).toBe(400);
    const body = await res.json();
    expect(Object.keys(body.errors).sort()).toEqual(['$', 'request']);
  });

  it('a malformed pin does NOT count towards the lockout', async () => {
    /*
      THE ONE THAT MATTERS. The gate runs before the service, so a malformed value cannot be used
      to exhaust someone's attempts. Three malformed sends then one genuinely wrong PIN: if the
      malformed ones had counted, the fourth would be 429 PIN_LOCKED instead of 401.
    */
    seedMockSession();
    for (let i = 0; i < 3; i += 1) {
      const junk = await transfer(crypto.randomUUID(), {
        fromAccountId: acct(),
        recipientAzureTag: 'friend',
        amount: 10,
        pin: 'abcdef',
      });
      expect(junk.status).toBe(400);
    }

    const wrong = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: '000000',
    });
    expect(wrong.status).toBe(401);
    expect((await wrong.json()).errorCode).toBe('INVALID_PIN');
  });

  it('a NON-STRING pin is the JSON-conversion envelope, keyed by path', async () => {
    /*
      The nastiest of the shape cases, because the previous revision COERCED with `String(pin)` —
      and `String(123456)` is "123456", which matches the six-digit rule. So the mock ACCEPTED a
      payload the API refuses outright: a client could have shipped a numeric pin and only found out
      in production.

      OBSERVED: {"pin":123456} and {"pin":true} both ->
        400 {"request":["The request field is required."],
             "$.pin":["The JSON value could not be converted to System.String. Path: $.pin | …"]}
      System.Text.Json fails the conversion before DataAnnotations runs, so the key is the JSON PATH,
      not the property name.
    */
    seedMockSession();
    for (const junk of [123456, true]) {
      const res = await transfer(crypto.randomUUID(), {
        fromAccountId: acct(),
        recipientAzureTag: 'friend',
        amount: 10,
        pin: junk as unknown as string,
      });
      expect(res.status).toBe(400);
      const body = await res.json();
      expect(Object.keys(body.errors).sort()).toEqual(['$.pin', 'request']);
      expect(body.errors['$.pin'][0]).toContain('could not be converted to System.String');
    }
  });

  it('a NULL pin fires [Required] alone, not the format rule', async () => {
    // OBSERVED: {"pin":null} -> {"Pin":["The Pin field is required."]} — one message, not two.
    // DataAnnotations skip non-Required validators on null but run them all on "".
    seedMockSession();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: null as unknown as string,
    });
    expect(res.status).toBe(400);
    expect((await res.json()).errors.Pin).toEqual(['The Pin field is required.']);
  });

  it('model state AGGREGATES every bad field, it does not stop at the first', async () => {
    /*
      OBSERVED: amount -5 AND pin "12" ->
        400 {"Pin":["PIN must be exactly 6 digits."],
             "Amount":["Amount must be between $0.01 and $100,000.00"]}
      One pass, one map. The mock used to early-return from the amount check before the pin gate
      ever ran, so a doubly-invalid body got an amount-only answer and a form could highlight one
      field where the API highlights two.
    */
    seedMockSession();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: -5,
      pin: '12',
    });
    expect(res.status).toBe(400);
    expect(Object.keys((await res.json()).errors).sort()).toEqual(['Amount', 'Pin']);
  });

  it('an unknown SOURCE account is 404 even with a wrong PIN (ownership precedes the PIN)', async () => {
    seedMockSession();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: FIXED,
      recipientAzureTag: 'friend',
      amount: 10,
      pin: '000000',
    });
    // OBSERVED on the real API, same unknown id, both a correct and a wrong pin: 404
    // ACCOUNT_NOT_FOUND. TransferAsync opens with GetAccountWithOwnershipCheckAsync.
    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
  });

  it('a same-account internal transfer is the VALIDATOR 400 even with a wrong PIN', async () => {
    seedMockSession();
    const res = await internal(crypto.randomUUID(), {
      fromAccountId: acct(),
      toAccountId: acct(),
      amount: 10,
      pin: '000000',
    });
    // OBSERVED: 400 {"title":"Validation Failed","errors":{"toAccountId":[...]}} — FluentValidation
    // runs before the service, so the PIN is never verified for this request.
    expect(res.status).toBe(400);
    expect(Object.keys((await res.json()).errors)).toContain('toAccountId');
  });
});

/**
 * The step-up protocol at the WIRE, where the pages cannot reach it.
 *
 * `transfer-step-up.test.tsx` drives the same protocol through the UI, and that is a different
 * claim: it can only exercise what a page happens to send. These reach the handler directly, so
 * they can send the things a correct client never would — an authorisation minted for €10 spent on
 * €500, one spent twice, a header that is not a GUID — which is exactly where a mock that is merely
 * "green" stops matching the server.
 *
 * Every expectation is a row of `A2-PR2-MEASURED-CONTRACT.md`, captured with curl against the API
 * on `:5068` and quoted at the assertion rather than summarised.
 */

const AUTH_URL = '/api/transfers/authorizations';
const AUTH_I_URL = '/api/transfers/internal/authorizations';

function mint(url: string, body: unknown) {
  return fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** A transfer that presents an authorisation. `null` sends no header at all. */
function transferWithAuth(auth: string | null, body: unknown) {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    'Idempotency-Key': crypto.randomUUID(),
  };
  if (auth) headers['Step-Up-Authorization'] = auth;
  return fetch(T_URL, { method: 'POST', headers, body: JSON.stringify(body) });
}

describe('step-up authorisations (ADR-0042)', () => {
  it('mints one, and the id is spendable exactly ONCE', async () => {
    /*
      The single-use property, which is the whole point of the entity: without it a captured
      authorisation is a bearer token for as many transfers as the amount allows.

      The second attempt uses a FRESH Idempotency-Key on purpose. With the same key the request
      never reaches the service at all — `IdempotencyMiddleware` replays the stored response before
      `_next`, so a same-key retry would answer 201 from the store and prove nothing about
      consumption. Measured on the real stack: same key -> 201 + `Idempotency-Replayed: true`;
      new key -> 401 AUTHORIZATION_INVALID.
    */
    seedMockSession();
    const body = { fromAccountId: acct(), recipientAzureTag: 'friend', amount: 10, pin: MOCK_PIN };

    const minted = await mint(AUTH_URL, body);
    // OBSERVED: 201 {"data":{authorizationId,expiresAt},"message":"Transfer authorised"}
    expect(minted.status).toBe(201);
    const { data } = await minted.json();
    expect(data.authorizationId).toMatch(/^[0-9a-f-]{36}$/i);
    // The window is two minutes and does not refresh (StepUpOptions.Window). Asserted as a RANGE
    // because the exact instant is the server's clock, not ours.
    const ttl = new Date(data.expiresAt).getTime() - Date.now();
    expect(ttl).toBeGreaterThan(60_000);
    expect(ttl).toBeLessThanOrEqual(120_000);

    const first = await transferWithAuth(data.authorizationId, body);
    expect(first.status).toBe(201);

    const second = await transferWithAuth(data.authorizationId, body);
    expect(second.status).toBe(401);
    expect((await second.json()).errorCode).toBe('AUTHORIZATION_INVALID');
  });

  it('refuses when the AMOUNT differs from the one authorised', async () => {
    // RTS Art. 5(1)(c): the amount and the payee are what the code is dynamically linked to. The
    // PIN in the body is CORRECT here — so a failure to bind would sail through on the in-band
    // check alone, which is precisely the hole ADR-0042 closes.
    seedMockSession();
    const authorised = {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    };
    const { data } = await (await mint(AUTH_URL, authorised)).json();

    const res = await transferWithAuth(data.authorizationId, { ...authorised, amount: 500 });
    // OBSERVED: 401 AUTHORIZATION_INVALID — the uniform refusal, not a "wrong amount" code.
    expect(res.status).toBe(401);
    expect((await res.json()).errorCode).toBe('AUTHORIZATION_INVALID');
  });

  it('refuses when the PAYEE differs, and says exactly the same thing', async () => {
    // The uniform refusal is a security property, not laziness: distinct codes for unknown /
    // not-yours / spent / mismatched would turn this endpoint into an oracle about other people's
    // authorisations. Asserted by COMPARING the two bodies rather than by reading one.
    seedMockSession();
    const authorised = {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    };
    const { data } = await (await mint(AUTH_URL, authorised)).json();

    // A DIFFERENT REAL recipient, not an invented handle: an unknown one would be refused by the
    // payee lookup, which now runs first, and the test would pass without the binding ever being
    // examined.
    const mismatched = await transferWithAuth(data.authorizationId, {
      ...authorised,
      recipientAzureTag: 'john_d',
    });
    const forged = await transferWithAuth(crypto.randomUUID(), authorised);

    expect(mismatched.status).toBe(forged.status);
    const [a, b] = [await mismatched.json(), await forged.json()];
    expect(a.errorCode).toBe('AUTHORIZATION_INVALID');
    expect(a.detail).toBe(b.detail);
  });

  it('answers an EXPIRED one distinctly, and checks expiry BEFORE the binding', async () => {
    /*
      Two claims in one request, and the second is the interesting half. An authorisation that is
      both expired AND mismatched must answer EXPIRED: someone who waited too long with the right
      details is told to re-enter a PIN, while the uniform refusal would send them back to redo the
      form. `StepUpAuthorizationService` orders the checks that way and this pins the order.
    */
    seedMockSession();
    const authorised = {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    };
    const { data } = await (await mint(AUTH_URL, authorised)).json();

    // Age it past the window. The real one was aged with SQL for the same measurement; there is no
    // way to wait two minutes in a unit test and no reason to.
    const held = mockState.stepUpAuthorizations.get(data.authorizationId);
    expect(held).toBeDefined();
    held!.expiresAtMs = Date.now() - 1;

    const res = await transferWithAuth(data.authorizationId, { ...authorised, amount: 500 });
    expect(res.status).toBe(401);
    // OBSERVED: 401 AUTHORIZATION_EXPIRED, detail "This authorisation has expired. Enter your PIN
    // again to confirm." — despite the amount ALSO being wrong.
    expect((await res.json()).errorCode).toBe('AUTHORIZATION_EXPIRED');
  });

  it('is a MODEL-BINDING 400 with no errorCode when the header is not a GUID', async () => {
    /*
      A fourth 400 envelope on these endpoints, and the one a client is most likely to mis-handle.
      `[FromHeader(Name = "Step-Up-Authorization")] Guid?` fails in model binding — before the
      action — so there is no `errorCode` for `classifyMoneyProblem` to branch on, and the errors
      dictionary is keyed by the WIRE NAME rather than the C# parameter name.

      Observed: 400 {"title":"One or more validation errors occurred.",
                     "errors":{"Step-Up-Authorization":["The value 'not-a-guid' is not valid."]}}
    */
    seedMockSession();
    const res = await transferWithAuth('not-a-guid', {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });

    expect(res.status).toBe(400);
    const body = await res.json();
    expect(body.errorCode).toBeUndefined();
    expect(Object.keys(body.errors)).toContain('Step-Up-Authorization');
  });

  it('reports that 400 in MODEL BINDING — beside a bad PIN, and before one is ever verified', async () => {
    /*
      Where the refusal happens, not merely what it says. A junk header sent with a junk PIN must
      answer 400 with BOTH keys: model binding runs before the action, so the PIN is never verified
      and no lockout attempt is charged. The first draft of the mock checked the header inside the
      consumption step — after `checkPinInBand` — which answered 401 INVALID_PIN and cost an attempt
      the real server does not take.
    */
    seedMockSession();
    const res = await transferWithAuth('not-a-guid', {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: '12',
    });

    expect(res.status).toBe(400);
    const keys = Object.keys((await res.json()).errors);
    expect(keys).toContain('Step-Up-Authorization');
    expect(keys).toContain('Pin');
  });

  it('still succeeds with NO header — PR 2 does not yet require one', async () => {
    // Backward compatibility, asserted so that flipping enforcement in PR 4 is a deliberate edit to
    // a failing test rather than a silent change of meaning. The in-band PIN is still the proof.
    seedMockSession();
    const res = await transferWithAuth(null, {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(201);
  });

  it('will not spend an INTERNAL authorisation on an EXTERNAL transfer', async () => {
    // The binding covers the OPERATION, not only the money. Both endpoints mint the same-looking
    // id, so without this a client could route one to the other and the amounts would match.
    seedMockSession();
    const { data } = await (
      await mint(AUTH_I_URL, {
        fromAccountId: acct(),
        toAccountId: acct2(),
        amount: 10,
        pin: MOCK_PIN,
      })
    ).json();

    const res = await transferWithAuth(data.authorizationId, {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(401);
    expect((await res.json()).errorCode).toBe('AUTHORIZATION_INVALID');
  });

  it('costs a PIN attempt: minting refuses a wrong PIN exactly as the transfer does', async () => {
    // Minting is the authentication event. If it were cheaper than the transfer it would become the
    // oracle for guessing a PIN — the lock has to land on the same attempt either way.
    seedMockSession();
    const res = await mint(AUTH_URL, {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: '000000',
    });
    // OBSERVED: 401 INVALID_PIN on attempts 1-2, then 429 PIN_LOCKED with retryAfterSeconds 900 ON
    // the third — the lock lands on the third miss, not after it.
    expect(res.status).toBe(401);
    expect((await res.json()).errorCode).toBe('INVALID_PIN');
  });

  it('resolves the payee BEFORE the PIN, so no authorisation ever names a stranger', async () => {
    seedMockSession();
    const res = await mint(AUTH_URL, {
      fromAccountId: acct(),
      recipientAzureTag: 'nosuchuser',
      amount: 10,
      pin: MOCK_PIN,
    });
    // OBSERVED: 404 ACCOUNT_NOT_FOUND, detail "Recipient with identifier 'nosuchuser' was not
    // found." — the general account code, not a dedicated recipient one.
    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });

  it('refuses an unowned source account with a 404, before the PIN is consulted', async () => {
    // OBSERVED, unowned id + CORRECT pin: 404 ACCOUNT_NOT_FOUND, detail "Account with identifier
    // '3f2504e0-…' was not found." `AuthoriseTransferAsync` opens with the ownership check, so
    // probing someone else's account through the MINT costs no attempt — exactly as through the
    // transfer. A mint that were cheaper would be the softer of two doors into the same lock.
    seedMockSession();
    const res = await mint(AUTH_URL, {
      fromAccountId: FIXED,
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });

  it('will not mint for a move the transfer would refuse (same account)', async () => {
    /*
      The property that makes the two endpoints one protocol rather than two. Minting for
      from == to would hand back an id the internal transfer then rejects with a validator 400 —
      and the user, having already entered a PIN, would watch a confirmed operation fail.

      OBSERVED: 400 {"title":"Validation Failed","instance":"/api/transfers/internal/authorizations",
                     "errors":{"toAccountId":["Cannot transfer to the same account."]}}
      — the validator envelope with the camelCase key, identical to the transfer's own answer.
    */
    seedMockSession();
    const res = await mint(AUTH_I_URL, {
      fromAccountId: acct(),
      toAccountId: acct(),
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(400);
    const body = await res.json();
    expect(body.title).toBe('Validation Failed');
    expect(Object.keys(body.errors)).toContain('toAccountId');
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });

  it('applies the amount annotation at the mint, in the framework envelope', async () => {
    // OBSERVED, amount 0: 400 {"Amount":["Amount must be between $0.01 and $100,000.00"]} —
    // PascalCase, no `detail`. `[MoneyRange]` is a DataAnnotation, so it fires in model state
    // before the action, and the mint answers it identically to the transfer.
    seedMockSession();
    const res = await mint(AUTH_URL, {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 0,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(400);
    expect(Object.keys((await res.json()).errors)).toContain('Amount');
  });

  it('resolves the payee before EXAMINING an authorisation too, so a typo stays a 404', async () => {
    /*
      The same ordering on the spending side, and it is the one the mock got wrong.

      `TransferAsync` runs ownership → PIN → `ResolveExternalPayeeAsync` → `ValidateAsync`, so a
      handle that does not exist is a 404 even when the presented authorisation is ALSO wrong for
      it. The mock consumed first, which answered 401 AUTHORIZATION_INVALID and would have told a
      user who mistyped a handle that their confirmation was no longer usable.
    */
    seedMockSession();
    const { data } = await (
      await mint(AUTH_URL, {
        fromAccountId: acct(),
        recipientAzureTag: 'friend',
        amount: 10,
        pin: MOCK_PIN,
      })
    ).json();

    const res = await transferWithAuth(data.authorizationId, {
      fromAccountId: acct(),
      recipientAzureTag: 'nosuchuser',
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');

    // ...and the authorisation is untouched, so the user can correct the handle and send.
    expect(mockState.stepUpAuthorizations.get(data.authorizationId)?.consumed).toBe(false);
  });
});
