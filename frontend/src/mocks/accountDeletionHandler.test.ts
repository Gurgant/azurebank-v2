import { beforeEach, describe, expect, it } from 'vitest';
import { MOCK_PIN, mockState, resetMockState, seedMockSession } from './state';

/**
 * Executable contract for the account-closure handlers (ADR-0049): the deletion mint and the
 * gated DELETE. A mock/backend alignment tripwire, like `transferHandler.test.ts` — every row here
 * is a row of the after-table, and every expected value is quoted from
 * `azurebank-work/plans/account-deletion/measure-after-main-19742ff-2026-09-06.txt`, measured
 * 2026-09-06T19:16Z on main 19742ff through the BFF (:5000 -> :7215, AzureBankDev). The probe
 * letters (M0-M5, D1-D16) are that file's row labels; E1/E2 are the expiry rows of
 * `measure-after-2026-09-06.txt` (d93ba10 working tree, merged as 19742ff).
 *
 * What the mock does NOT model, so nothing below claims it: the 403 ACCESS_DENIED rows (D14/D15 —
 * `MockAccount` has no owner) and the audit rows (D1's AccountDeletionRefused, D9's
 * AccountDeleted — the mock has no audit).
 */

const MINT = (id: string) => `/api/accounts/${id}/deletion-authorizations`;
const ACCOUNT = (id: string) => `/api/accounts/${id}`;

/** A well-formed account id nobody owns. Well-formed matters: an EMPTY guid is a 400, not a 404. */
const UNKNOWN_ACCOUNT = '3f2504e0-4f89-41d3-9a0c-0305e82c3302';

async function createSpare(name = 'Closable Spare'): Promise<string> {
  const created = await fetch('/api/accounts', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, type: 'Savings' }),
  });
  expect(created.status).toBe(201);
  return ((await created.json()) as { data: { id: string } }).data.id;
}

function mint(id: string, pin: unknown = MOCK_PIN) {
  return fetch(MINT(id), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pin }),
  });
}

/** `null` sends no header at all; a string sends exactly that string, empty or not. */
function del(id: string, authorization: string | null) {
  const headers: Record<string, string> = {};
  if (authorization !== null) headers['Step-Up-Authorization'] = authorization;
  return fetch(ACCOUNT(id), { method: 'DELETE', headers });
}

async function mintedId(id: string): Promise<string> {
  const res = await mint(id);
  expect(res.status).toBe(201);
  return ((await res.json()) as { data: { authorizationId: string } }).data.authorizationId;
}

function deposit(accountId: string, amount: number) {
  return fetch('/api/transactions/deposit', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },
    body: JSON.stringify({ accountId, amount }),
  });
}

function withdraw(accountId: string, amount: number) {
  return fetch('/api/transactions/withdraw', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Idempotency-Key': crypto.randomUUID() },
    body: JSON.stringify({ accountId, amount, pin: MOCK_PIN }),
  });
}

const listed = (id: string) => mockState.accounts.some((a) => a.id === id);

beforeEach(() => {
  resetMockState();
  // Re-seed: the shared setup signs in before every test, and a local reset undoes it. `/api/*`
  // is session-gated now, so without this every request here is a 401.
  seedMockSession();
});

describe('POST /api/accounts/{id}/deletion-authorizations — the deletion mint', () => {
  it('mints one bound to (AccountDeletion, account, 0) for a two-minute window — M5', async () => {
    // Measured 2026-09-06T19:16Z on main 19742ff, probe M5: 201 "Account closure authorised",
    // authorizationId + expiresAt; row AccountDeletion Pending NULL. The window is E1's mint+2m.
    const spare = await createSpare();
    const before = Date.now();

    const res = await mint(spare);
    expect(res.status).toBe(201);
    const body = (await res.json()) as {
      data: { authorizationId: string; expiresAt: string };
      message: string;
    };
    const after = Date.now();
    expect(body.message).toBe('Account closure authorised');
    expect(body.data.authorizationId).toMatch(/^[0-9a-f-]{36}$/i);
    // E1: expiresAt = handlerNow + 2m, and before <= handlerNow <= after — so both bounds are
    // exact and scheduling-independent (no latency budget: a stalled worker cannot fail a correct
    // mock, and a widened or shortened window fails it). transferHandler.test.ts does the same.
    const expiresAtMs = new Date(body.data.expiresAt).getTime();
    expect(expiresAtMs - 120_000).toBeGreaterThanOrEqual(before);
    expect(expiresAtMs - 120_000).toBeLessThanOrEqual(after);

    expect(mockState.stepUpAuthorizations.size).toBe(1);
    expect(mockState.stepUpAuthorizations.get(body.data.authorizationId)).toMatchObject({
      operation: 'AccountDeletion',
      fromAccountId: spare,
      amount: 0,
      consumed: false,
    });
    // ADR-0049 D3: nothing else is bound — a defined payee here would refuse every DELETE.
    const held = mockState.stepUpAuthorizations.get(body.data.authorizationId)!;
    expect(held.recipientAzureTag).toBeUndefined();
    expect(held.toAccountId).toBeUndefined();
    // A correct PIN resets the counter, as on every other in-band check.
    expect(mockState.pinAttempts).toBe(0);
  });

  it('a wrong PIN is 401 INVALID_PIN and costs an attempt — M1', async () => {
    // Measured M1: 401 INVALID_PIN "Invalid PIN.", PinAccessFailedCount 0 -> 1. The 429 on the
    // THIRD miss is checkPinInBand's, measured on the transfer mints 2026-08-16 — not provoked on
    // this endpoint in the after-run, so not asserted here.
    const spare = await createSpare();
    expect(mockState.pinAttempts).toBe(0);

    const res = await mint(spare, '000000');
    expect(res.status).toBe(401);
    const body = await res.json();
    expect(body.errorCode).toBe('INVALID_PIN');
    expect(body.detail).toBe('Invalid PIN.');
    expect(mockState.pinAttempts).toBe(1);
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });

  it('a funded account is refused 422 BEFORE the PIN is consulted — M2', async () => {
    // Measured M2, sent with a WRONG pin on purpose: 422 NON_ZERO_BALANCE "Cannot delete an
    // account with a non-zero balance.", PinAccessFailedCount unchanged — the guard is ahead of
    // the PIN, so probing a funded account with a bad PIN costs nothing.
    const funded = mockState.accounts[1]; // Rainy Day, balance 830
    expect(funded.balance).not.toBe(0);

    const res = await mint(funded.id, '000000');
    expect(res.status).toBe(422);
    const body = await res.json();
    expect(body.errorCode).toBe('NON_ZERO_BALANCE');
    expect(body.detail).toBe('Cannot delete an account with a non-zero balance.');
    expect(mockState.pinAttempts).toBe(0);
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });

  it('the primary account is refused 422 PRIMARY_ACCOUNT_DELETE — M3', async () => {
    // Measured M3 (correct pin): 422 PRIMARY_ACCOUNT_DELETE "Cannot delete primary account. Set
    // another account as primary first." Balance-before-primary is code
    // (AccountService.RefuseIfNotClosable), so only a ZERO-balance primary reaches this rung.
    const primary = mockState.accounts.find((a) => a.isPrimary)!;
    primary.balance = 0;

    const res = await mint(primary.id);
    expect(res.status).toBe(422);
    const body = await res.json();
    expect(body.errorCode).toBe('PRIMARY_ACCOUNT_DELETE');
    expect(body.detail).toBe(
      'Cannot delete primary account. Set another account as primary first.',
    );
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });

  it('an unknown id is 404 ACCOUNT_NOT_FOUND on the deletion mint, with nothing minted — M4', async () => {
    // Measured M4 with a correct pin: 404 ACCOUNT_NOT_FOUND "Account with identifier '<id>' was
    // not found." The 403 half (D15, another user's account) is not modelled — no owner here.
    // With the CORRECT pin this proves nothing about the ORDER (a matching PIN resets the counter
    // to 0 either way); the ordering variant is the next test.
    const res = await mint(UNKNOWN_ACCOUNT);
    expect(res.status).toBe(404);
    const body = await res.json();
    expect(body.errorCode).toBe('ACCOUNT_NOT_FOUND');
    expect(body.detail).toBe(`Account with identifier '${UNKNOWN_ACCOUNT}' was not found.`);
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });

  it('checks ownership before the PIN: an unknown id with a WRONG pin is still 404 and spends no attempt', async () => {
    // NOT a measured row (M4 was sent with the correct pin and no counter was read after it).
    // Ownership-before-PIN is code — AccountService.AuthoriseDeletionAsync opens with
    // GetAccountWithOwnershipCheckAsync before MintAsync — pinned here as a tripwire on the
    // mock's order: consult the PIN first and this answers 401 INVALID_PIN with the counter at 1.
    const res = await mint(UNKNOWN_ACCOUNT, '000000');
    expect(res.status).toBe(404);
    expect((await res.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');
    expect(mockState.pinAttempts).toBe(0);
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });

  it('with no PIN enrolled answers 422 PIN_REQUIRED, in the generalised sentence — M0', async () => {
    // Measured M0: 422 PIN_REQUIRED "PIN must be set before authorising this operation." The
    // guards run first (M2/M3), so the rung is only reached on a closable account.
    seedMockSession({ ...mockState.session!, hasPin: false });
    const spare = await createSpare();

    const res = await mint(spare);
    expect(res.status).toBe(422);
    const body = await res.json();
    expect(body.errorCode).toBe('PIN_REQUIRED');
    expect(body.detail).toBe('PIN must be set before authorising this operation.');
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });

  it('a malformed PIN is a model-state 400 keyed Pin, before ownership and the PIN', async () => {
    // Envelopes measured on the TRANSFER mints 2026-08-16 (mintPinBindFailure /
    // pinAnnotationErrors); this DTO carries the same [Required][Pin]
    // (AccountDeletionAuthorizationRequest.cs) — not measured on /deletion-authorizations.
    const spare = await createSpare();

    const short = await mint(spare, '12');
    expect(short.status).toBe(400);
    expect((await short.json()).errors.Pin).toEqual(['PIN must be exactly 6 digits.']);

    const nul = await mint(spare, null);
    expect(nul.status).toBe(400);
    expect((await nul.json()).errors.Pin).toEqual(['The Pin field is required.']);

    expect(mockState.pinAttempts).toBe(0);
    expect(mockState.stepUpAuthorizations.size).toBe(0);
  });
});

describe('DELETE /api/accounts/{id} — gated by the authorisation', () => {
  it('refuses a headerless, empty or whitespace header as 401 AUTHORIZATION_REQUIRED — D1/D2/D3', async () => {
    // Measured D1 (absent), D2 (''), D3 ('   '): 401 AUTHORIZATION_REQUIRED "This account closure
    // has not been authorised."; the spare is still listed after D1. `[FromHeader] Guid?` trims and
    // binds all three to null (readStepUpHeader).
    const spare = await createSpare();

    for (const header of [null, '', '   ']) {
      const res = await del(spare, header);
      expect(res.status, `header ${JSON.stringify(header)}`).toBe(401);
      const body = await res.json();
      expect(body.errorCode).toBe('AUTHORIZATION_REQUIRED');
      expect(body.detail).toBe('This account closure has not been authorised.');
    }
    expect(listed(spare)).toBe(true);
    expect(mockState.pinAttempts).toBe(0);
  });

  it('a header that is not a GUID is a model-state 400 with no errorCode — D4', async () => {
    // Measured D4 on the spare: 400 {"Step-Up-Authorization":["The value 'not-a-guid' is not
    // valid."]}, keyed by the WIRE name, no errorCode. That binding precedes ownership on a
    // funded/unknown id is MVC (`[FromHeader] Guid?`) — inferred, not probed.
    const spare = await createSpare();

    const res = await del(spare, 'not-a-guid');
    expect(res.status).toBe(400);
    const body = await res.json();
    expect(body.errorCode).toBeUndefined();
    expect(body.errors).toEqual({
      'Step-Up-Authorization': ["The value 'not-a-guid' is not valid."],
    });
    expect(listed(spare)).toBe(true);
  });

  it('an unknown id and a TRANSFER authorisation on the same account are both AUTHORIZATION_INVALID — D5/D6', async () => {
    // Measured D5 (random GUID) and D6 (a transfer authorisation minted from the spare): 401
    // AUTHORIZATION_INVALID "This authorisation cannot be used.", byte-identical — uniform on
    // purpose, so the endpoint is no oracle about authorisations the caller does not hold.
    const spare = await createSpare();
    const transferMint = await fetch('/api/transfers/authorizations', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        fromAccountId: spare,
        recipientAzureTag: 'friend',
        amount: 10,
        pin: MOCK_PIN,
      }),
    });
    expect(transferMint.status).toBe(201);
    const transferAuth = ((await transferMint.json()) as { data: { authorizationId: string } }).data
      .authorizationId;

    const random = await del(spare, crypto.randomUUID());
    const transfer = await del(spare, transferAuth);
    expect(random.status).toBe(401);
    expect(transfer.status).toBe(401);
    const [a, b] = [await random.json(), await transfer.json()];
    expect(a.errorCode).toBe('AUTHORIZATION_INVALID');
    expect(b.errorCode).toBe('AUTHORIZATION_INVALID');
    expect(a.detail).toBe('This authorisation cannot be used.');
    expect(b.detail).toBe(a.detail);
    expect(listed(spare)).toBe(true);
    // Nothing spent on a refusal: the transfer authorisation is still Pending.
    expect(mockState.stepUpAuthorizations.get(transferAuth)?.consumed).toBe(false);
  });

  it('the guards answer BEFORE the presence check: funded and primary are 422 with no header — D7/D8', async () => {
    // Measured D7 (funded, no header): 422 NON_ZERO_BALANCE; D8 (primary, no header): 422
    // PRIMARY_ACCOUNT_DELETE. Guards-before-presence is what keeps money.contract.test.ts's
    // figure-free-sentence pin green on both targets.
    const funded = mockState.accounts[1]; // Rainy Day, balance 830
    const primary = mockState.accounts.find((a) => a.isPrimary)!;
    primary.balance = 0;

    const fundedRes = await del(funded.id, null);
    expect(fundedRes.status).toBe(422);
    expect((await fundedRes.json()).errorCode).toBe('NON_ZERO_BALANCE');

    const primaryRes = await del(primary.id, null);
    expect(primaryRes.status).toBe(422);
    expect((await primaryRes.json()).errorCode).toBe('PRIMARY_ACCOUNT_DELETE');

    expect(listed(funded.id)).toBe(true);
    expect(listed(primary.id)).toBe(true);
  });

  it('closes the account ONCE with the minted id, and the account is then gone for good — D9/D10/D11', async () => {
    // Measured D9: 200 "Account deleted successfully", authorisation Consumed. D10 (same, spent
    // header) and D11 (no header): 404 ACCOUNT_NOT_FOUND both — the account is gone first, so a
    // spent header is 404 not INVALID and a missing one is 404 not REQUIRED.
    const spare = await createSpare();
    const authorization = await mintedId(spare);

    const closed = await del(spare, authorization);
    expect(closed.status).toBe(200);
    expect((await closed.json()).message).toBe('Account deleted successfully');
    expect(listed(spare)).toBe(false);
    expect(mockState.stepUpAuthorizations.get(authorization)?.consumed).toBe(true);

    const spent = await del(spare, authorization);
    expect(spent.status).toBe(404);
    expect((await spent.json()).errorCode).toBe('ACCOUNT_NOT_FOUND');

    const headerless = await del(spare, null);
    expect(headerless.status).toBe(404);
    const body = await headerless.json();
    expect(body.errorCode).toBe('ACCOUNT_NOT_FOUND');
    expect(body.detail).toBe(`Account with identifier '${spare}' was not found.`);

    // mockState no longer holds the row (`listed` is a state read, not a wire call) and the
    // mock's GET answers 404 — the mock modelling the API's soft-delete filter; D9 itself records
    // the 200, the IsDeleted flag and the Consumed row, not a GET.
    expect((await fetch(ACCOUNT(spare))).status).toBe(404);
    expect(mockState.pinAttempts).toBe(0);
  });

  it('a refused closure spends nothing: the same authorisation closes the account once drained — D12/D16', async () => {
    // Measured D12: mint, deposit, DELETE with the valid id -> 422 NON_ZERO_BALANCE, authorisation
    // still Pending. D16 shows a Pending row closing the account later (200) — here the drain is
    // a withdrawal rather than D16's foreign-user detour, and the second DELETE reuses the SAME id.
    const spare = await createSpare();
    const authorization = await mintedId(spare);
    expect((await deposit(spare, 25)).status).toBe(201);

    const refused = await del(spare, authorization);
    expect(refused.status).toBe(422);
    expect((await refused.json()).errorCode).toBe('NON_ZERO_BALANCE');
    expect(mockState.stepUpAuthorizations.get(authorization)?.consumed).toBe(false);
    expect(listed(spare)).toBe(true);

    expect((await withdraw(spare, 25)).status).toBe(201);

    const closed = await del(spare, authorization);
    expect(closed.status).toBe(200);
    expect(mockState.stepUpAuthorizations.get(authorization)?.consumed).toBe(true);
    expect(listed(spare)).toBe(false);
  });

  it('answers an EXPIRED one distinctly, spends no attempt and leaves the row Pending — E1/E2', async () => {
    // Measured E2 (measure-after-2026-09-06.txt, d93ba10 working tree merged as 19742ff): a DELETE
    // 130 s after a mint whose expiresAt was mint+2m (E1) -> 401 AUTHORIZATION_EXPIRED "This
    // authorisation has expired. Enter your PIN again to confirm.", PinAccessFailedCount 0/0,
    // authorisation still Pending, /bff/auth/me 200 after. Aged by editing the stored row, as
    // transferHandler.test.ts does — there is no way to wait two minutes in a unit test.
    const spare = await createSpare();
    const authorization = await mintedId(spare);
    const held = mockState.stepUpAuthorizations.get(authorization)!;
    held.expiresAtMs = Date.now() - 1;

    const res = await del(spare, authorization);
    expect(res.status).toBe(401);
    const body = await res.json();
    expect(body.errorCode).toBe('AUTHORIZATION_EXPIRED');
    expect(body.detail).toBe('This authorisation has expired. Enter your PIN again to confirm.');
    expect(mockState.pinAttempts).toBe(0);
    expect(held.consumed).toBe(false);
    expect(listed(spare)).toBe(true);
  });
});
