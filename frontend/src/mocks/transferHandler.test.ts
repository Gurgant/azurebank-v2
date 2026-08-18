import { describe, expect, it } from 'vitest';
import { MOCK_PIN, mockState, seedMockSession } from './state';

/**
 * Executable contract for the recipient-lookup + stateful transfer handlers: the exact-match
 * lookup (self/unknown masked as exists:false), the level-2 403 gate, and the post-elevation
 * failure order (SELF_TRANSFER → recipient ACCOUNT_NOT_FOUND → INSUFFICIENT_FUNDS → success),
 * plus namespaced idempotency.
 */

const T_URL = '/api/transfers';
/**
 * A fixed key for the idempotency assertions — value irrelevant, only that it repeats.
 *
 * Separate from `UNOWNED_ACCOUNT` below even though both are GUIDs: one stands for "the same key
 * twice", the other for "an account this caller does not have", and a single constant doing both
 * jobs reads as if the two tests were related.
 */
const FIXED = '3f2504e0-4f89-41d3-9a0c-0305e82c3301';

/** A well-formed account id nobody owns. Well-formed matters: an EMPTY guid is a 400, not a 404. */
const UNOWNED_ACCOUNT = '3f2504e0-4f89-41d3-9a0c-0305e82c3302';

function lookup(tag: string) {
  return fetch(`/api/users/${tag}`);
}

/**
 * A transfer.
 *
 * `auth` defaults to AUTO: mint an authorisation for exactly this body first, so that the many
 * tests below about something else — idempotency, the ledger, the balance, the refusal order —
 * keep being about that rather than about the header ADR-0042 now requires. Pass `null` to send
 * none, which is its own test.
 *
 * When the body is one the mint would itself refuse (an unknown recipient, a bad amount), AUTO
 * falls back to a well-formed reference bound to nothing. That is deliberate: those tests assert a
 * refusal that must happen BEFORE the binding is checked, so a worthless-but-present reference is
 * exactly what proves the ordering held.
 */
async function transfer(key: string | null, body: unknown, auth: string | null = AUTO) {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (key) headers['Idempotency-Key'] = key;
  const presented = auth === AUTO ? await autoAuthorise(T_URL, body) : auth;
  if (presented) headers['Step-Up-Authorization'] = presented;
  return fetch(T_URL, { method: 'POST', headers, body: JSON.stringify(body) });
}

/** Sentinel distinct from `null`, which means "deliberately send no header". */
const AUTO = '\u0000auto';

async function autoAuthorise(url: string, body: unknown): Promise<string> {
  const b = body as Record<string, unknown>;
  const isInternal = url === I_URL;
  const minted = await fetch(
    isInternal ? '/api/transfers/internal/authorizations' : '/api/transfers/authorizations',
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(
        isInternal
          ? {
              fromAccountId: b.fromAccountId,
              toAccountId: b.toAccountId,
              amount: b.amount,
              pin: '123456',
            }
          : {
              fromAccountId: b.fromAccountId,
              recipientAzureTag: b.recipientAzureTag,
              amount: b.amount,
              pin: '123456',
            },
      ),
    },
  );
  if (minted.status !== 201) return crypto.randomUUID();
  const payload = (await minted.json()) as { data: { authorizationId: string } };
  return payload.data.authorizationId;
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

  it('ignores a pin in the body: it is an unknown property now, not a second factor', async () => {
    /*
      The old client's exact request. `TransferRequest.Pin` is gone (ADR-0042), so System.Text.Json
      drops the extra property and the transfer is decided by its authorisation alone — a WRONG pin
      beside a valid authorisation still succeeds, because nothing reads it.

      Asserting the success rather than a refusal is deliberate: it is the only way to show the
      field is inert. A test that sent no authorisation would pass for the other reason.
    */
    seedMockSession();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: '000000',
    });
    expect(res.status).toBe(201);
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

  it('DEFAULTS the ledger description when none was sent, the way the API does', async () => {
    /*
      The mock stored null here while the product stores a sentence, so a history rendered under MSW
      showed an empty description where the real one shows text. Measured on the running stack rather
      than read off the C# (API :7215, seeded dev database, migrations applied):

        POST /api/transfers  {fromAccountId, recipientAzureTag:"janesmith", amount:1.11, pin}
          -- the description field ABSENT from the body --
        GET  /api/transactions
          -> TransferOut  1.11  "Transfer to @janesmith"

      Same run, as the control that makes this specific rather than a blanket rule: a deposit with no
      description came back with description null, because TransactionService.cs:63 assigns
      request.Description raw. Only the four transfer ledger rows default (TransferService.cs:294,
      :316, :487, :504) — and the internal transfer's RESPONSE does not, which is why
      InternalTransferResponse still carries null here.

      Falsified by restoring `?? null`.
    */
    elevate();
    await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 5,
      pin: MOCK_PIN,
    });

    const row = mockState.transactions.at(-1);
    expect(row?.type).toBe('TransferOut');
    expect(row?.description).toBe('Transfer to @friend');
  });

  it('keeps a description the caller DID send, rather than overwriting it with the default', async () => {
    elevate();
    await transfer(crypto.randomUUID(), {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 5,
      pin: MOCK_PIN,
      description: 'rent',
    });

    expect(mockState.transactions.at(-1)?.description).toBe('rent');
  });
});

const I_URL = '/api/transfers/internal';

/** The internal transfer, with the same AUTO-mint default as {@link transfer}. */
async function internal(key: string | null, body: unknown, auth: string | null = AUTO) {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (key) headers['Idempotency-Key'] = key;
  const presented = auth === AUTO ? await autoAuthorise(I_URL, body) : auth;
  if (presented) headers['Step-Up-Authorization'] = presented;
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

/** The mint: the only endpoint on the transfer path that still takes a PIN (ADR-0042). */
function authorise(body: unknown) {
  return fetch('/api/transfers/authorizations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

describe('the PIN gates at the MINT, in the order the API applies them (ADR-0042)', () => {
  /*
    Every expectation below was MEASURED on the real API and pasted beside its assertion.

    These moved endpoint rather than changing: ADR-0042's flip deleted the transfer's in-band PIN,
    so `POST /api/transfers/authorizations` is now the only place on this path where a PIN is
    presented — and therefore the only place these gates can be observed. The properties are
    unchanged, and so is why they matter: order is only visible when something fails, so these are
    the tests that deliberately fail one.

    The mint takes NO Idempotency-Key (it moves no money and is meant to be easy to call again
    after a wrong PIN), which is why `authorise` sends none.
  */

  it('a MALFORMED pin is 400 from model binding, with the PascalCase Pin key', async () => {
    seedMockSession();
    const res = await authorise({
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
    const res = await authorise({
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
      const junk = await authorise({
        fromAccountId: acct(),
        recipientAzureTag: 'friend',
        amount: 10,
        pin: 'abcdef',
      });
      expect(junk.status).toBe(400);
    }

    const wrong = await authorise({
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
      const res = await authorise({
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
    const res = await authorise({
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
    const res = await authorise({
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
    const res = await authorise({
      fromAccountId: UNOWNED_ACCOUNT,
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

  it('reports that 400 in MODEL BINDING, upstream of the authorisation check', async () => {
    /*
      Where the refusal happens, not merely what it says. A header that is not a UUID is refused by
      MVC before the action runs, so it is a 400 model-state answer and NOT the 401 that a
      well-formed but unusable reference gets. Those two are easy to conflate and drive different
      client recoveries.

      This used to assert a `Pin` key beside it, from a junk PIN in the same body. That key is gone
      with the property (ADR-0042) — a `pin` now reaches the endpoint as an unknown field and
      produces no error of its own. The mint is where a malformed PIN is refused today, and it is
      still model binding that does it, so it still costs no lockout attempt.
    */
    seedMockSession();
    const res = await transferWithAuth('not-a-guid', {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
    });

    expect(res.status).toBe(400);
    const keys = Object.keys((await res.json()).errors);
    expect(keys).toContain('Step-Up-Authorization');
    expect(keys).not.toContain('Pin');
  });

  it.each([
    ['empty', ''],
    ['a single space', ' '],
    ['a tab', '	'],
  ])('treats a header that is %s as ABSENT, not as malformed', async (_label, value) => {
    /*
      MEASURED on the real API (https://localhost:7215, all three variants sent with curl):
        Step-Up-Authorization: <empty|space|tab>  ->  401 AUTHORIZATION_REQUIRED

      MVC's `Guid?` binder trims and treats a whitespace-only value as null, so it lands on the
      same refusal as no header at all — NOT on the 400 a non-UUID gets. The mock read the raw value
      and handed '' to parseGuid, which reported a binding error, so it answered 400 where the API
      answers 401. Those two drive different client recoveries, and this is the exact row the
      enforcement flip is about.
    */
    seedMockSession();
    const res = await fetch(T_URL, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': crypto.randomUUID(),
        'Step-Up-Authorization': value,
      },
      body: JSON.stringify({
        fromAccountId: acct(),
        recipientAzureTag: 'friend',
        amount: 10,
      }),
    });

    expect(res.status).toBe(401);
    expect((await res.json()).errorCode).toBe('AUTHORIZATION_REQUIRED');
  });

  it('is refused with NO header: the authorisation is the only proof now', async () => {
    /*
      THE FLIP. This test asserted the opposite until ADR-0042's second half, precisely so that
      requiring the header would be a deliberate edit to a failing test rather than a silent change
      of meaning. It is that edit.

      A pin in the body does not rescue it — that is the point: while both proofs were accepted the
      weaker one decided, and six static digits authorising any amount to any payee is the finding
      the ADR opens with.
    */
    seedMockSession();
    const res = await transferWithAuth(null, {
      fromAccountId: acct(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(401);
    expect((await res.json()).errorCode).toBe('AUTHORIZATION_REQUIRED');
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
      fromAccountId: UNOWNED_ACCOUNT,
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

/**
 * Review round 2 — the four corrections a bot found and a measurement settled.
 *
 * Each block below pins a behaviour the mock got wrong at `ad73318`, and each expectation was
 * captured against the API on `:5068` before it was written here. Two of the four claims arrived
 * from CodeRabbit describing the MOCK's own behaviour as if it were the contract; measuring is what
 * separated them.
 */

/** The body every external test in this block sends, built after the session is seeded. */
function externalBody(amount = 10) {
  return { fromAccountId: acct(), recipientAzureTag: 'friend', amount, pin: MOCK_PIN };
}

/** Mint one external authorisation and hand back its id. */
async function mintOne(amount = 10) {
  const res = await mint(AUTH_URL, externalBody(amount));
  return (await res.json()).data.authorizationId as string;
}

/** The same GUID, in every shape `Guid.TryParse` accepts. */
function guidForms(id: string) {
  const n = id.replace(/-/g, '');
  const [a, b, c, d, e] = id.split('-');
  const bytes = d + e;
  const x =
    `{0x${a},0x${b},0x${c},{` +
    Array.from({ length: 8 }, (_, i) => `0x${bytes.slice(i * 2, i * 2 + 2)}`).join(',') +
    '}}';
  return [
    ['D uppercase', id.toUpperCase()],
    ['N', n],
    ['B', `{${id}}`],
    ['P', `(${id})`],
    ['X', x],
    ['whitespace', ` ${id} `],
  ] as const;
}

describe('the authorisation header binds the way MVC binds it', () => {
  it('accepts every GUID format the server accepts, and resolves them to the same authorisation', async () => {
    /*
      MEASURED on the running API with a well-formed id that names nothing — every one of these
      answered 401 (bound, unknown authorisation), never 400:

        D / D-uppercase / N / B / P / X

      and only `not-a-guid` answered 400. Then, with a REAL minted id: presenting it uppercased
      returned 201, and presenting it with the dashes stripped returned 201 — the server canonicalises
      before it looks anything up, so formatting cannot lose an authorisation.

      Both halves matter here. Accepting the format is not enough: the mock keys its map by the
      lowercase dashed string `crypto.randomUUID()` returns, so a mock that bound `01A0…` and then
      looked it up raw would answer AUTHORIZATION_INVALID for an authorisation the server spends —
      the most confusing possible failure, since the id is right there in the request.
    */
    seedMockSession();
    // A FRESH authorisation per format. The first draft minted one and spent it six times, which
    // reddened on the second format for the right reason — single use — and would have hidden
    // whichever formats actually failed to bind.
    for (const [label] of guidForms('00000000-0000-0000-0000-000000000000')) {
      const id = await mintOne();
      const header = guidForms(id).find(([name]) => name === label)![1];
      const res = await transferWithAuth(header, externalBody());
      expect(res.status, `${label} should spend exactly like the canonical form`).toBe(201);
      expect(
        mockState.stepUpAuthorizations.get(id)?.consumed,
        `${label} must resolve to the same
        stored authorisation, not merely bind — the map is keyed by the canonical lowercase form`,
      ).toBe(true);
    }
  });

  it('still refuses a value that is not a GUID at all, in model binding', async () => {
    seedMockSession();
    const res = await transferWithAuth('not-a-guid', externalBody());
    expect(res.status).toBe(400);
    expect(Object.keys((await res.json()).errors)).toContain('Step-Up-Authorization');
  });
});

describe('an account id that is absent or all-zero is a MODEL-STATE refusal', () => {
  /*
    `[NotEmptyGuid(ErrorMessage = "A valid account ID is required.")]` is a DataAnnotation, so it
    fires before FluentValidation, before the same-account rule and before any ownership lookup.
    MEASURED identically on all four endpoints, for an absent id and for the all-zero one:

      400 {"title":"One or more validation errors occurred.",
           "errors":{"FromAccountId":["A valid account ID is required."],
                     "ToAccountId":["A valid account ID is required."]}}

    The mock reached its later branches with `undefined` instead and answered two different wrong
    things — the internal MINT compared undefined to undefined and claimed "Cannot transfer to the
    same account", the transfers fell through to the ownership lookup and answered 404. A review bot
    then read that 404 back as the contract, which is exactly why the mock is never the oracle.
  */
  const CASES = [
    ['external mint', AUTH_URL, { recipientAzureTag: 'friend' }, ['FromAccountId']],
    ['internal mint', AUTH_I_URL, {}, ['FromAccountId', 'ToAccountId']],
  ] as const;

  for (const [label, url, extra, keys] of CASES) {
    it(`${label}: 400 keyed PascalCase, not a same-account or not-found answer`, async () => {
      seedMockSession();
      const res = await mint(url, { ...extra, amount: 10, pin: MOCK_PIN });
      expect(res.status).toBe(400);
      const body = await res.json();
      expect(body.title).toBe('One or more validation errors occurred.');
      for (const key of keys) {
        expect(Object.keys(body.errors)).toContain(key);
        expect(body.errors[key]).toEqual(['A valid account ID is required.']);
      }
      // Never the same-account rule: that is a DIFFERENT envelope with a camelCase key, and it
      // cannot be reached by a body that has no account ids at all.
      expect(Object.keys(body.errors)).not.toContain('toAccountId');
      expect(mockState.stepUpAuthorizations.size).toBe(0);
    });
  }

  it('the transfers answer it too — the same annotation on the same four endpoints', async () => {
    seedMockSession();
    const external = await transfer(crypto.randomUUID(), {
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(external.status).toBe(400);
    expect(Object.keys((await external.json()).errors)).toContain('FromAccountId');

    const internalRes = await internal(crypto.randomUUID(), {
      fromAccountId: '00000000-0000-0000-0000-000000000000',
      toAccountId: '00000000-0000-0000-0000-000000000000',
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(internalRes.status).toBe(400);
    const keys = Object.keys((await internalRes.json()).errors);
    expect(keys).toContain('FromAccountId');
    expect(keys).toContain('ToAccountId');
  });

  it('a well-formed id nobody owns is still a 404 — the two are not the same refusal', async () => {
    seedMockSession();
    const res = await transfer(crypto.randomUUID(), {
      fromAccountId: UNOWNED_ACCOUNT,
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
  });
});

describe('a refused transfer does not burn the authorisation', () => {
  it('insufficient funds leaves it spendable, and the retry says so again', async () => {
    /*
      THE ONE THAT CHANGES WHAT A USER READS.

      `TransferService` calls `ValidateAsync` early and `ConsumeAsync` inside the transfer's own
      transaction, after the funds check — so a 422 never touches the row. MEASURED, minting for
      €60,000 against a €49,952 balance:

        POST /api/transfers  ->  422 INSUFFICIENT_FUNDS
        SELECT Status, ConsumedAt FROM StepUpAuthorizations  ->  Pending | NULL

      still Pending after three attempts, and no `Idempotency-Replayed` on any of them, so each
      retry re-executed and answered INSUFFICIENT_FUNDS again. The mock consumed before the funds
      check, so its second attempt answered AUTHORIZATION_INVALID: it told the user their
      confirmation was dead when the only thing wrong was the balance.
    */
    seedMockSession();
    const balance = mockState.accounts[0].balance;
    const tooMuch = { ...externalBody(), amount: balance + 1000 };

    const id = await mintOne(tooMuch.amount);
    const first = await transferWithAuth(id, tooMuch);
    expect(first.status).toBe(422);
    expect((await first.json()).errorCode).toBe('INSUFFICIENT_FUNDS');

    // The row is untouched — the strong half, because a status alone would pass on a mock that
    // consumed and then happened to answer 422 for some other reason.
    expect(mockState.stepUpAuthorizations.get(id)?.consumed).toBe(false);

    const retry = await transferWithAuth(id, tooMuch);
    expect(retry.status).toBe(422);
    expect((await retry.json()).errorCode).toBe('INSUFFICIENT_FUNDS');

    // And it is still genuinely spendable: fund the account and the SAME authorisation goes through.
    mockState.accounts[0].balance = tooMuch.amount + 10;
    const funded = await transferWithAuth(id, tooMuch);
    expect(funded.status).toBe(201);
    expect(mockState.stepUpAuthorizations.get(id)?.consumed).toBe(true);
  });

  it('nor does an unknown payee burn it', async () => {
    // Same property one refusal earlier: the payee is resolved before the authorisation is even
    // examined, so a typo costs nothing. Already asserted for the 404 itself; this asserts the ROW.
    seedMockSession();
    const id = await mintOne();
    const res = await transferWithAuth(id, { ...externalBody(), recipientAzureTag: 'nosuchuser' });
    expect(res.status).toBe(404);
    expect(mockState.stepUpAuthorizations.get(id)?.consumed).toBe(false);
  });
});

describe('the internal transfer spends its own authorisations', () => {
  /*
    The external consumption path had six tests and the internal one had none — it was reached only
    through the mint, never through a spend. These four cover the internal call site directly.
  */
  async function mintInternal(amount = 10) {
    const res = await mint(AUTH_I_URL, {
      fromAccountId: acct(),
      toAccountId: acct2(),
      amount,
      pin: MOCK_PIN,
    });
    return (await res.json()).data.authorizationId as string;
  }

  function internalWithAuth(auth: string | null, amount = 10) {
    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      'Idempotency-Key': crypto.randomUUID(),
    };
    if (auth) headers['Step-Up-Authorization'] = auth;
    return fetch(I_URL, {
      method: 'POST',
      headers,
      body: JSON.stringify({
        fromAccountId: acct(),
        toAccountId: acct2(),
        amount,
        pin: MOCK_PIN,
      }),
    });
  }

  it('spends one exactly once', async () => {
    seedMockSession();
    const id = await mintInternal();
    expect((await internalWithAuth(id)).status).toBe(201);

    // A FRESH key, so the request reaches the handler instead of replaying the stored 201.
    const second = await internalWithAuth(id);
    expect(second.status).toBe(401);
    expect((await second.json()).errorCode).toBe('AUTHORIZATION_INVALID');
  });

  it('refuses an EXTERNAL authorisation on the internal route', async () => {
    // The mirror of the external test. Both endpoints hand back the same-looking id, so without the
    // operation in the binding a client could route one to the other with the amounts matching.
    seedMockSession();
    const external = await mintOne();
    const res = await internalWithAuth(external);
    expect(res.status).toBe(401);
    expect((await res.json()).errorCode).toBe('AUTHORIZATION_INVALID');
  });

  it('refuses a malformed header here too, in binding, before the same-account rule', async () => {
    seedMockSession();
    const res = await fetch(I_URL, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': crypto.randomUUID(),
        'Step-Up-Authorization': 'not-a-guid',
      },
      // Same account on purpose: if the header were checked after the same-account validator this
      // would answer that rule's camelCase 400 instead, and the ordering would go unnoticed.
      body: JSON.stringify({
        fromAccountId: acct(),
        toAccountId: acct(),
        amount: 10,
        pin: MOCK_PIN,
      }),
    });
    expect(res.status).toBe(400);
    const keys = Object.keys((await res.json()).errors);
    expect(keys).toContain('Step-Up-Authorization');
    expect(keys).not.toContain('toAccountId');
  });

  it('moves no money with no header at all', async () => {
    // The internal path is a separate handler with its own ownership checks, so it gets its own
    // proof rather than an assumption that the two share a code path.
    seedMockSession();
    const before = mockState.accounts[1].balance;
    const res = await internalWithAuth(null);
    expect(res.status).toBe(401);
    expect((await res.json()).errorCode).toBe('AUTHORIZATION_REQUIRED');
    expect(mockState.accounts[1].balance).toBe(before);
  });
});

describe('a GUID in the BODY is not a GUID in a HEADER', () => {
  /*
    Two parsers, and which one you get depends on where the value was bound from. MEASURED on the
    running API with one account id in six spellings:

      POST /api/transfers/authorizations   {"fromAccountId": …}
        D            -> 201        N (no dashes) -> 400 $.fromAccountId
        D UPPERCASE  -> 201        B {braces}    -> 400 $.fromAccountId
                                   X hex-groups  -> 400 $.fromAccountId
                                   "  D  "       -> 400 $.fromAccountId

      GET /api/transactions/{id}           D · N · UPPERCASE · B  -> 200, all four

    So System.Text.Json takes the D form ONLY (case-insensitively) for a body member, while MVC's
    TryParse takes all five plus whitespace for a route or a header. A review comment reasoned from
    the header fix and proposed accepting every format in the body too; that would have made the
    mock accept four shapes the server refuses.
  */
  const REFUSED: [string, (id: string) => string][] = [
    ['N (no dashes)', (id) => id.replace(/-/g, '')],
    ['B {braces}', (id) => `{${id}}`],
    ['P (parens)', (id) => `(${id})`],
    [
      'X (hex groups)',
      (id) => {
        const [a, b, c, d, e] = id.split('-');
        const bytes = d + e;
        return (
          `{0x${a},0x${b},0x${c},{` +
          Array.from({ length: 8 }, (_, i) => `0x${bytes.slice(i * 2, i * 2 + 2)}`).join(',') +
          '}}'
        );
      },
    ],
    ['leading/trailing space', (id) => `  ${id}  `],
  ];

  for (const [label, shape] of REFUSED) {
    it(`refuses ${label} in the body, the way System.Text.Json does`, async () => {
      seedMockSession();
      const res = await mint(AUTH_URL, {
        fromAccountId: shape(acct()),
        recipientAzureTag: 'friend',
        amount: 10,
        pin: MOCK_PIN,
      });

      // 400 and keyed by JSON PATH — NOT the 404 an unknown-but-well-formed id gets, and not the
      // PascalCase `[NotEmptyGuid]` message an absent one gets. Three envelopes, one field.
      expect(res.status).toBe(400);
      const keys = Object.keys((await res.json()).errors);
      expect(keys).toContain('$.fromAccountId');
      expect(keys).not.toContain('FromAccountId');
      expect(mockState.stepUpAuthorizations.size).toBe(0);
    });
  }

  it('ACCEPTS the D form uppercased, and resolves it to the same account', async () => {
    /*
      The one spelling that binds and is not already canonical — so it is the one a raw-string
      lookup loses. Before `bindAccountIds` rewrote the member, this answered 404 ACCOUNT_NOT_FOUND
      for an account the server resolves without blinking.
    */
    seedMockSession();
    const res = await mint(AUTH_URL, {
      fromAccountId: acct().toUpperCase(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });

    expect(res.status).toBe(201);
    // The STORED binding is canonical too, which is the half a status code cannot show: the
    // authorisation must be spendable by a client that then sends the id in its usual lowercase.
    //
    // The count is asserted BEFORE the record is read, and it is not defensive padding: "exactly
    // one was minted" is part of the property. `values()` yields insertion order, so a second
    // authorisation from anywhere would hand this assertion the wrong row and still pass. Same
    // idiom as `transfer-step-up.test.tsx`, which had it and these two sites had lost it.
    const minted = [...mockState.stepUpAuthorizations.values()];
    expect(minted).toHaveLength(1);
    expect(minted[0].fromAccountId).toBe(acct());
  });

  it('spends an authorisation minted with an UPPERCASE id from a lowercase transfer', async () => {
    // The end-to-end version of the same property, and the one that would break a real client:
    // the binding compares parsed values on the server, so the spelling used at mint time cannot
    // decide whether the transfer goes through.
    seedMockSession();
    const minted = await mint(AUTH_URL, {
      fromAccountId: acct().toUpperCase(),
      recipientAzureTag: 'friend',
      amount: 10,
      pin: MOCK_PIN,
    });
    const { data } = await minted.json();

    const res = await transferWithAuth(data.authorizationId, externalBody());
    expect(res.status).toBe(201);
  });

  it('binds BOTH account ids on the internal endpoints', async () => {
    seedMockSession();
    const res = await mint(AUTH_I_URL, {
      fromAccountId: acct(),
      toAccountId: acct2().toUpperCase(),
      amount: 10,
      pin: MOCK_PIN,
    });
    expect(res.status).toBe(201);
    const minted = [...mockState.stepUpAuthorizations.values()];
    expect(minted).toHaveLength(1);
    expect(minted[0].toAccountId).toBe(acct2());
  });
});

describe('a route GUID takes every format, because MVC binds it', () => {
  it('finds the same transaction from D, N, uppercase and braces', async () => {
    /*
      The third binding kind, and the last place the canonicalisation rule was missing. MEASURED:
      `GET /api/transactions/{id}` answered 200 to all four spellings of one id, because
      `[HttpGet("{id:guid}")] … Guid id` is `Guid.TryParse` and `t.Id == transactionId` is a struct
      compare.

      Reachable from the app rather than only from curl: `/transactions/:id` hands the URL segment
      to the query verbatim, so a pasted uppercase id rendered "not found" under MSW while the real
      API served the transaction.
    */
    seedMockSession();
    const canonical = mockState.transactions[0].id;
    const forms = [
      ['D', canonical],
      ['N', canonical.replace(/-/g, '')],
      ['UPPERCASE', canonical.toUpperCase()],
      ['B {braces}', `{${canonical}}`],
    ] as const;

    for (const [label, id] of forms) {
      const res = await fetch(`/api/transactions/${id}`);
      expect(res.status, `${label} must resolve the same row`).toBe(200);
    }
  });

  it('still 404s an id nobody has, and names it canonically', async () => {
    // The API's `NotFoundException` formats a BOUND Guid, so the detail is lowercase D whatever the
    // caller sent. The mock echoed the caller's spelling back.
    seedMockSession();
    const res = await fetch('/api/transactions/3F2504E0-4F89-41D3-9A0C-0305E82C3399');
    expect(res.status).toBe(404);
    expect((await res.json()).detail).toContain('3f2504e0-4f89-41d3-9a0c-0305e82c3399');
  });
});
