import { beforeAll, describe, expect, it } from 'vitest';
import { asProblem, call, firstAccountId, idempotencyKey, login } from './client';
import { FIXTURES } from './target';

/**
 * The step-up protocol (ADR-0042), asserted against WHICHEVER backend is listening.
 *
 * These are the rows of `A2-PR2-MEASURED-CONTRACT.md` that can be checked without moving money or
 * spending a PIN attempt — which is most of them, and the choice is deliberate rather than timid.
 * The real target runs against a database that persists between runs, so a suite that transferred
 * on every pass would drift balances until an unrelated assertion started failing, and a suite that
 * sent a wrong PIN would lock the fixture account for fifteen minutes and take the next run with it.
 *
 * What that leaves OUT, said plainly rather than left to be discovered:
 *
 *   * single-use — needs a successful spend, so it lives in `mocks/transferHandler.test.ts` (mock)
 *     and in the API's own integration tests (real);
 *   * expiry — the window is two minutes and there is no client-side way to age a real row, so the
 *     mock-level test ages its own store and `StepUpAuthorizationServiceTests` covers the server;
 *   * the PIN lock landing on the third miss — three wrong PINs against the shared fixture.
 *
 * Everything below is a refusal or a mint, and neither touches a balance.
 */

beforeAll(async () => {
  expect((await login()).status).toBe(200);
});

/** A GUID that is well-formed and names nothing. */
const UNKNOWN_AUTHORIZATION = '3f2504e0-4f89-41d3-9a0c-0305e82c3399';

describe('contract: step-up authorisations', () => {
  it('mints one for a two-minute window, and answers 201 with an id and an expiry', async () => {
    /*
      The mint is the only endpoint here that succeeds, and it moves nothing: it writes a Pending
      row that this test never spends. Observed on the real API:

        201 {"data":{"authorizationId":"01a00a8f-…","expiresAt":"2026-08-16T12:35:21.0559540Z"},
             "message":"Transfer authorised"}

      The TTL is asserted as a RANGE, not an equality. `expiresAt` is the SERVER's clock and this
      process has its own; pinning it to exactly 120s would make a healthy stack fail on a machine
      whose clock is a second off, which is a test that fails for a reason nobody can act on.
    */
    const id = await firstAccountId();

    const { status, body } = await call('/api/transfers/authorizations', {
      method: 'POST',
      body: JSON.stringify({
        fromAccountId: id,
        recipientAzureTag: FIXTURES.recipientAzureTag,
        amount: 1,
        pin: FIXTURES.pin,
      }),
    });

    expect(status).toBe(201);
    const data = (body as { data?: { authorizationId?: string; expiresAt?: string } }).data;
    expect(data?.authorizationId).toMatch(/^[0-9a-f-]{36}$/i);

    const ttl = new Date(data?.expiresAt ?? 0).getTime() - Date.now();
    expect(ttl).toBeGreaterThan(30_000);
    expect(ttl).toBeLessThanOrEqual(120_000);
  });

  it('answers an unknown payee with ACCOUNT_NOT_FOUND, not a code of its own', async () => {
    /*
      Worth pinning because the obvious guess is wrong twice over: the exception is
      `NotFoundException("Recipient", tag)`, but that constructor emits `ACCOUNT_NOT_FOUND` for
      every resource — so the RESOURCE in the sentence and the CODE on the wire disagree, and the
      client keys off the code.

      It matters beyond tidiness: `TransferPage` already maps ACCOUNT_NOT_FOUND to "That recipient
      could not be found. Please re-check the handle.", so a dedicated code would have needed new
      copy for a case that already had some.

      Observed: 404 detail "Recipient with identifier 'nosuchuser' was not found."
    */
    const id = await firstAccountId();

    const { status, body } = await call('/api/transfers/authorizations', {
      method: 'POST',
      body: JSON.stringify({
        fromAccountId: id,
        recipientAzureTag: 'nosuchuser',
        amount: 1,
        pin: FIXTURES.pin,
      }),
    });

    expect(status).toBe(404);
    expect(asProblem(body).errorCode).toBe('ACCOUNT_NOT_FOUND');
  });

  it('checks the source account before the PIN, so a probe costs no attempt', async () => {
    // The PIN here is CORRECT, which is what makes this safe to run and also what makes it mean
    // something: the answer must be about the account, not the credential. Observed with an id the
    // caller does not own: 404 "Account with identifier '3f2504e0-…' was not found."
    const { status, body } = await call('/api/transfers/authorizations', {
      method: 'POST',
      body: JSON.stringify({
        fromAccountId: UNKNOWN_AUTHORIZATION,
        recipientAzureTag: FIXTURES.recipientAzureTag,
        amount: 1,
        pin: FIXTURES.pin,
      }),
    });

    expect(status).toBe(404);
    expect(asProblem(body).errorCode).toBe('ACCOUNT_NOT_FOUND');
  });

  it('will not mint for a same-account move, in the VALIDATOR envelope', async () => {
    /*
      The mint and the transfer must refuse the same things, or a user confirms with a PIN an
      operation that then fails. Measured, from == to with a correct PIN:

        400 {"type":"https://httpstatuses.com/400","title":"Validation Failed",
             "detail":"One or more validation errors occurred.",
             "instance":"/api/transfers/internal/authorizations",
             "errors":{"toAccountId":["Cannot transfer to the same account."]}}

      Same envelope, same camelCase key as the internal transfer's own refusal — which is the point:
      one rule, stated once, answered identically at both doors.
    */
    const id = await firstAccountId();

    const { status, body } = await call('/api/transfers/internal/authorizations', {
      method: 'POST',
      body: JSON.stringify({
        fromAccountId: id,
        toAccountId: id,
        amount: 1,
        pin: FIXTURES.pin,
      }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('Validation Failed');
    expect(problem.errorCode).toBeUndefined();
    expect(Object.keys(problem.errors ?? {})).toContain('toAccountId');
  });

  it('refuses an authorisation it does not know, and says nothing about why', async () => {
    /*
      The uniform refusal. Unknown, someone else's, already spent and wrong-binding all answer the
      same 401 AUTHORIZATION_INVALID, because a distinct code for each would make this endpoint an
      oracle about authorisations the caller does not hold.

      Sent with a well-formed GUID that names nothing, a CORRECT PIN and a valid payee, so the only
      thing wrong with the request is the authorisation — and no money moves, because the refusal
      precedes the ledger.
    */
    const id = await firstAccountId();

    const { status, body } = await call('/api/transfers', {
      method: 'POST',
      headers: {
        'Idempotency-Key': idempotencyKey(),
        'Step-Up-Authorization': UNKNOWN_AUTHORIZATION,
      },
      body: JSON.stringify({
        fromAccountId: id,
        recipientAzureTag: FIXTURES.recipientAzureTag,
        amount: 1,
        pin: FIXTURES.pin,
      }),
    });

    expect(status).toBe(401);
    expect(asProblem(body).errorCode).toBe('AUTHORIZATION_INVALID');
  });

  it('resolves the payee BEFORE the authorisation, so a typo is still a 404', async () => {
    /*
      An ordering, and the one the mock had backwards.

      `TransferAsync` runs ownership → PIN → ResolveExternalPayeeAsync → ValidateAsync. So a request
      that is wrong in two ways at once — an unknown handle AND an authorisation that names nothing
      — answers 404, not 401. The mock validated first and turned that into "your confirmation can
      no longer be used", which is both untrue and unactionable for someone who simply mistyped.

      Measured on the real stack, exactly this request: 404 ACCOUNT_NOT_FOUND.
    */
    const id = await firstAccountId();

    const { status, body } = await call('/api/transfers', {
      method: 'POST',
      headers: {
        'Idempotency-Key': idempotencyKey(),
        'Step-Up-Authorization': UNKNOWN_AUTHORIZATION,
      },
      body: JSON.stringify({
        fromAccountId: id,
        recipientAzureTag: 'nosuchuser',
        amount: 1,
        pin: FIXTURES.pin,
      }),
    });

    expect(status).toBe(404);
    expect(asProblem(body).errorCode).toBe('ACCOUNT_NOT_FOUND');
  });

  it('rejects a malformed header in MODEL BINDING — a fourth 400 shape, with no errorCode', async () => {
    /*
      `[FromHeader(Name = "Step-Up-Authorization")] Guid?` is bound by MVC, so a value that is not a
      GUID fails before the action: before ownership, before the PIN, before the service.

      Two consequences a client has to live with. There is no `errorCode`, so `classifyMoneyProblem`
      falls through every branch and the user sees the flow's fallback sentence — acceptable, since
      a malformed header is a client bug rather than a user action, but nobody should meet it as a
      surprise. And the key is the WIRE header name, not the C# parameter name: model binding keys
      by the binding source, which `[FromHeader(Name = …)]` renamed.

      Observed: 400 {"type":"…rfc9110#section-15.5.1","title":"One or more validation errors
                     occurred.","errors":{"Step-Up-Authorization":["The value 'not-a-guid' is not
                     valid."]}}
    */
    const id = await firstAccountId();

    const { status, body } = await call('/api/transfers', {
      method: 'POST',
      headers: {
        'Idempotency-Key': idempotencyKey(),
        'Step-Up-Authorization': 'not-a-guid',
      },
      body: JSON.stringify({
        fromAccountId: id,
        recipientAzureTag: FIXTURES.recipientAzureTag,
        amount: 1,
        pin: FIXTURES.pin,
      }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('One or more validation errors occurred.');
    expect(problem.errorCode).toBeUndefined();
    expect(Object.keys(problem.errors ?? {})).toContain('Step-Up-Authorization');
  });

  it('BINDS every GUID format, and reserves the 400 for a value that is not one', async () => {
    /*
      `Guid.TryParse` accepts five formats and trims whitespace, so the wire contract is not "a
      dashed lowercase GUID" — it is "anything .NET calls a GUID". The distinction is visible from
      outside without spending anything: a well-formed id that names nothing answers **401** because
      it BOUND and then missed, while a value that is not a GUID answers **400** because binding
      itself failed. So the status alone separates the two, and neither moves money.

      Measured on the real API, all six answering 401:

        D  3f2504e0-4f89-41d3-9a0c-0305e82c3399      N  3f2504e04f8941d39a0c0305e82c3399
        B  {3f2504e0-…}                              P  (3f2504e0-…)
        X  {0x3f2504e0,0x4f89,0x41d3,{0x9a,…}}       and D uppercased

      The mock refused four of these with a 400 until this test existed.
    */
    const id = await firstAccountId();
    const g = UNKNOWN_AUTHORIZATION;
    const forms: [string, string][] = [
      ['D', g],
      ['D uppercase', g.toUpperCase()],
      ['N', g.replace(/-/g, '')],
      ['B', `{${g}}`],
      ['P', `(${g})`],
    ];

    for (const [label, header] of forms) {
      const { status, body } = await call('/api/transfers', {
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey(), 'Step-Up-Authorization': header },
        body: JSON.stringify({
          fromAccountId: id,
          recipientAzureTag: FIXTURES.recipientAzureTag,
          amount: 1,
          pin: FIXTURES.pin,
        }),
      });

      expect(
        status,
        `${label} must BIND — a 400 here means the parser is narrower than the server's`,
      ).toBe(401);
      expect(asProblem(body).errorCode).toBe('AUTHORIZATION_INVALID');
    }
  });

  it('refuses an absent account id in MODEL STATE, keyed PascalCase', async () => {
    /*
      `[NotEmptyGuid(ErrorMessage = "A valid account ID is required.")]`, so it fires before
      FluentValidation and before any ownership lookup — measured identically on all four endpoints,
      for an absent id and for the all-zero one.

      Worth a contract test because it is the refusal a mock is most likely to get wrong by
      omission: skip the annotation and the request simply flows on to the next check, which answers
      something plausible. Ours answered 404 ACCOUNT_NOT_FOUND on the transfers and "Cannot transfer
      to the same account" on the internal mint — two different wrong answers, both reachable.
    */
    const { status, body } = await call('/api/transfers/authorizations', {
      method: 'POST',
      body: JSON.stringify({
        recipientAzureTag: FIXTURES.recipientAzureTag,
        amount: 1,
        pin: FIXTURES.pin,
      }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('One or more validation errors occurred.');
    expect(problem.errorCode).toBeUndefined();
    expect(problem.errors?.FromAccountId).toEqual(['A valid account ID is required.']);
  });

  it('reports the header and the body in ONE 400, which places the refusal in binding', async () => {
    /*
      Where, not just what. A junk header sent with a junk PIN comes back with BOTH keys in a single
      dictionary — which only happens if neither was reached by the action. Had the header been
      examined later, this request would answer 401 INVALID_PIN and cost a lockout attempt.

      Observed: 400 {"errors":{"Pin":["PIN must be exactly 6 digits."],
                               "Step-Up-Authorization":["The value 'not-a-guid' is not valid."]}}
    */
    const id = await firstAccountId();

    const { status, body } = await call('/api/transfers', {
      method: 'POST',
      headers: {
        'Idempotency-Key': idempotencyKey(),
        'Step-Up-Authorization': 'not-a-guid',
      },
      body: JSON.stringify({
        fromAccountId: id,
        recipientAzureTag: FIXTURES.recipientAzureTag,
        amount: 1,
        pin: '12',
      }),
    });
    const keys = Object.keys(asProblem(body).errors ?? {});

    expect(status).toBe(400);
    expect(keys).toContain('Step-Up-Authorization');
    expect(keys).toContain('Pin');
  });
});
