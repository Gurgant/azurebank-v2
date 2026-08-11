import { beforeEach, describe, expect, it } from 'vitest';
import { MAIN_ACCOUNT_ID, mockState, resetMockState, seedMockSession } from '../../mocks/state';

/**
 * The mock used to accept every account body it was given: an empty name became "New Account", an
 * unknown type was cast straight through, and any handle was lower-cased and stored. The API
 * rejects all three before the controller runs.
 *
 * **What this does NOT prove, despite the obvious guess.** Four dialogs carry a branch that maps
 * `problem.errors` onto their own fields, and none of those branches has ever executed. The tempting
 * conclusion is that a permissive mock is why — it is not. `CreateAccountDialog`,
 * `RenameAccountDialog` and `RenameAzureTagDialog` each mirror the server's rule in their own Zod
 * schema (2–100 on the names, 3–20 plus the pattern on the handle), so those inputs never leave the
 * browser. I wrote three tests driving the dialogs before checking that, and all three failed — the
 * client had blocked the submit, exactly as it should.
 *
 * So the rules below are here for contract fidelity, which is reason enough: they are what the API
 * enforces, and a mock that accepts what production refuses lets a caller that ISN'T one of those
 * forms — a future surface, a relaxed client rule, a direct call — look correct. They are tested at
 * the HTTP level because that is the only level at which they are reachable.
 */

describe('the account rules the mock did not enforce', () => {
  beforeEach(() => {
    resetMockState();
    // Re-seed: the shared setup signs in before every test, and a local reset undoes it. `/api/*`
    // is session-gated now, so without this every request here is a 401.
    seedMockSession();
  });

  const create = (body: unknown) =>
    fetch('/api/accounts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });

  /*
    KEYED `Name`, NOT `name`, AND AN EMPTY NAME RETURNS TWO MESSAGES.

    The name is rejected by DataAnnotations, so `[ApiController]` model state answers before the
    action ever runs and the key is the CLR property name. `[Required]` and `[StringLength]` are
    evaluated independently and reported together, which is why `''` produces both sentences while
    `'X'` produces only the length one. Each row below is quoted from the real stack (2026-08-04):

      {"name":""}            -> {"Name":["The Name field is required.",
                                          "Account name must be between 2 and 100 characters."]}
      {"name":"X"}           -> {"Name":["Account name must be between 2 and 100 characters."]}
      {"name":"N"x101}       -> {"Name":["Account name must be between 2 and 100 characters."]}

    This block previously asserted `errors.name` with a single "Account name is required." — the
    mock's invention, not the server's. It mattered beyond tidiness: `toFieldName` exists to absorb
    the PascalCase form, and while the mock only ever emitted camelCase, that branch was never
    exercised anywhere in the unit suite.
  */
  const LENGTH = 'Account name must be between 2 and 100 characters.';
  it.each([
    ['an empty name', { name: '', type: 'Checking' }, ['The Name field is required.', LENGTH]],
    ['a one-character name', { name: 'X', type: 'Checking' }, [LENGTH]],
    ['a 101-character name', { name: 'N'.repeat(101), type: 'Checking' }, [LENGTH]],
  ])('rejects %s with the framework’s own sentences', async (_case, body, messages) => {
    const res = await create(body);

    expect(res.status).toBe(400);
    expect((await res.json()).errors.Name).toEqual(messages);
  });

  it.each([
    ['type', { name: 'Holiday Fund' }],
    ['name', { type: 'Checking' }],
  ])('refuses to create when the required member %s is ABSENT', async (_member, body) => {
    /*
      ABSENT is not EMPTY. Both members are `required` in C#, so System.Text.Json fails before
      `[Required]` runs and the key is the document ROOT — a bare `$` — plus the body parameter's
      own name. Measured 2026-08-04:

        POST /api/accounts {"name":"No Type Probe"}
          -> {"$":["JSON deserialization … was missing required properties including: 'type'."],
              "request":["The request field is required."]}

      The mock used to DEFAULT a missing type to Checking and answer 201, persisting a row the real
      API refuses — the only remaining case where the fixture accepted a write the product rejects.
    */
    const before = mockState.accounts.length;
    const res = await create(body);

    expect(res.status).toBe(400);
    const { errors } = await res.json();
    expect(Object.keys(errors)).toEqual(expect.arrayContaining(['$', 'request']));
    // The whole point: nothing was created.
    expect(mockState.accounts).toHaveLength(before);
  });

  it('accepts a lower-case type and stores the canonical member', async () => {
    /*
      The API is case-insensitive here (`Enum.TryParse(..., ignoreCase: true)`) and the mock used to
      be STRICTER than the server — a fixture rejecting input the product accepts, which hides a
      working path. Measured: POST /api/accounts {"type":"checking"} -> 201.

      Asserting the STORED value, not just the status: the API persists the canonical `Checking`,
      so echoing the caller's casing back would drift the read model even with a correct 201.
    */
    const res = await create({ name: 'Case Probe', type: 'checking' });

    expect(res.status).toBe(201);
    expect((await res.json()).data.type).toBe('Checking');
  });

  it('rejects a type that is not one of the three', async () => {
    // `IsInEnum` — the mock used to cast whatever string arrived straight onto the account, so a
    // "Crypto" account was creatable here and impossible in production.
    const res = await create({ name: 'Holiday Fund', type: 'Crypto' });

    expect(res.status).toBe(400);
    expect((await res.json()).errors.type).toEqual([
      'Invalid account type. Must be Checking, Savings, or Investment.',
    ]);
  });

  it('still creates a valid account', async () => {
    const res = await create({ name: 'Holiday Fund', type: 'Savings' });
    expect(res.status).toBe(201);
  });

  it('guards the rename path with the same rule', async () => {
    const res = await fetch(`/api/accounts/${MAIN_ACCOUNT_ID}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: 'X' }),
    });

    expect(res.status).toBe(400);
    // Same `Name` key — measured: PATCH /api/accounts/{id} {"name":"x"}
    //   -> {"Name":["Account name must be between 2 and 100 characters."]}
    expect((await res.json()).errors.Name).toEqual([LENGTH]);
  });

  it('uses the rename DTO’s own required wording, which differs from create’s', async () => {
    /*
      Not a copy-paste of the create case. `CreateAccountRequest.Name` carries a bare `[Required]`
      and gets the framework's sentence; `UpdateAccountRequest.Name` overrides it. Measured:

        POST  /api/accounts      {"name":""} -> "The Name field is required."
        PATCH /api/accounts/{id} {"name":""} -> "Account name is required."

      The mock used the rename wording on BOTH paths, so this asymmetry was invisible.
    */
    const res = await fetch(`/api/accounts/${MAIN_ACCOUNT_ID}`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: '' }),
    });

    expect(res.status).toBe(400);
    expect((await res.json()).errors.Name).toEqual(['Account name is required.', LENGTH]);
  });

  it('refuses to change a PIN without the current one, and 401s a wrong one', async () => {
    /*
      Pins the rule the mock had inverted. It used to document itself as "set/OVERWRITE the user's
      PIN … no old PIN and no step-up required" and implement exactly that — modelling a complete
      step-up bypass as intended behaviour. Measured through the real BFF, session cookie only:

        enrol (no currentPin)      -> 200 {"message":"PIN set successfully"}
        change, no currentPin      -> 422 PIN_REQUIRED  "The current PIN is required to change it."
        change, wrong currentPin   -> 401 INVALID_PIN   "Invalid PIN."
        change, correct currentPin -> 200

      `instance` is "/api/auth/pin" on both errors — the API's own path, forwarded untouched by the
      BFF rather than authored by it.

      Without this test the mock rule is unpinned: deleting the guard leaves the whole suite green,
      which is how the original hole survived in the first place.
    */
    // hasPin true == the mock's stand-in for a stored PinHash, i.e. this is a CHANGE.
    mockState.session!.hasPin = true;

    const noProof = await fetch('/bff/auth/set-pin', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '999999' }),
    });
    expect(noProof.status).toBe(422);
    expect((await noProof.json()).errorCode).toBe('PIN_REQUIRED');

    const wrongProof = await fetch('/bff/auth/set-pin', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '999999', currentPin: '000000' }),
    });
    expect(wrongProof.status).toBe(401);
    expect((await wrongProof.json()).errorCode).toBe('INVALID_PIN');

    // And the refusals must not have moved the stored PIN.
    expect(mockState.pin).not.toBe('999999');
  });

  it('applies the full verifier contract to the current PIN, lockout included', async () => {
    /*
      CurrentPin goes through IPinVerifier on the API, so it inherits every rule that path has.
      A first draft of the mock only bumped a counter — modelling an endpoint that can be
      brute-forced without ever locking, which is worse than the hole this change closed.

      Measured against the real pipeline (WebApplicationFactory runs Program.cs verbatim; see
      PinReplacementTests):
        malformed "abc"           -> 400, errors keyed "CurrentPin"  (validation precedes the action)
        third wrong               -> 429 PIN_LOCKED
        correct, but locked       -> 429 PIN_LOCKED  (the lock is read BEFORE the comparison)
    */
    mockState.session!.hasPin = true;

    const malformed = await fetch('/bff/auth/set-pin', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '999999', currentPin: 'abc' }),
    });
    expect(malformed.status).toBe(400);
    expect(Object.keys((await malformed.json()).errors)).toContain('CurrentPin');

    // Two wrong tries stay 401; the third latches the lock.
    for (let i = 0; i < 2; i += 1) {
      const wrong = await fetch('/bff/auth/set-pin', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pin: '999999', currentPin: '000000' }),
      });
      expect(wrong.status).toBe(401);
    }
    const third = await fetch('/bff/auth/set-pin', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '999999', currentPin: '000000' }),
    });
    expect(third.status).toBe(429);
    expect((await third.json()).errorCode).toBe('PIN_LOCKED');

    // Knowing the PIN does not lift the lock.
    const correctButLocked = await fetch('/bff/auth/set-pin', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pin: '999999', currentPin: mockState.pin }),
    });
    expect(correctButLocked.status).toBe(429);
    expect(mockState.pin).not.toBe('999999');
  });

  it('answers an anonymous rename 401 even when the handle IS taken', async () => {
    /*
      ORDER, and it is a security property rather than tidiness. The BFF checks the session BEFORE
      it calls the API, so an anonymous caller gets 401 whether or not the handle exists — measured
      on the running BFF: "admin" (taken) -> 401, a free handle -> 401, a malformed one -> 400.

      A draft of the mock checked the conflict first and would have answered 409 for a taken handle
      and 401 for a free one. That is a handle-existence oracle, rebuilt inside the mock whose job
      is to model the ADRs (0013, 0014) written to deny exactly that.
    */
    mockState.session = null;

    const res = await fetch('/bff/auth/azuretag', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      // `friend` is a seeded recipient, so it IS taken — a 409 here would be the leak.
      body: JSON.stringify({ azureTag: 'friend' }),
    });

    expect(res.status).toBe(401);
    const body = await res.json();
    expect(body.detail).toBe('Session expired or invalid');
    expect(body.errorCode).toBeUndefined();
  });

  it('rejects a handle the pattern refuses, before the taken-handle conflict', async () => {
    // `[AzureTagQuery]` is model validation, so it runs ahead of the controller — a malformed
    // handle is a 400 even if it would also have collided.
    const res = await fetch('/bff/auth/azuretag', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ azureTag: '9nope' }),
    });

    expect(res.status).toBe(400);
    /*
      `AzureTag`, PascalCase, in the FRAMEWORK envelope — this route can emit no other. There is no
      `UpdateAzureTagRequest` validator and `UserController` injects none, so `[AzureTagQuery]` (a
      DataAnnotation) is the only gate and model state answers before the action. This assertion
      previously demanded the camelCase key from the FluentValidation envelope, which is the one
      shape the endpoint cannot produce.

      Measured 2026-08-04 on the API route, which the BFF endpoint now forwards to unchanged:
      PATCH /api/users/me/azuretag {"azureTag":"Bad Tag!"}
        -> {"title":"One or more validation errors occurred.",
            "errors":{"AzureTag":["AzureTag must start with a letter…"]}}
    */
    const body = await res.json();
    expect(body.title).toBe('One or more validation errors occurred.');
    expect(body.errors.AzureTag[0]).toContain('must start with a letter');
  });
});

describe('a body that cannot be read at all', () => {
  beforeEach(() => {
    resetMockState();
    seedMockSession();
    // Transfers sit behind the step-up gate, which is BFF MIDDLEWARE: unelevated they are a 403
    // whatever the body says, because it runs before the request reaches model binding at all.
    // Elevating once here keeps the table about bodies. (That cost this suite a first draft.)
    mockState.authLevel = 2;
  });

  /*
    EVERY handler that reads a body, not a sample of them.

    The review named three. I then wrote a six-row table and a comment claiming fifteen — the
    fifteen was a count of matching LINES, not of handlers, and the mismatch is what a second
    review caught. The real number is ELEVEN, enumerated below, and the table is now the
    enumeration rather than a selection from it: PATCH and both transfer paths were exactly the
    routes a POST-only table could not have protected.

    Why it matters: MSW v2 converts a thrown handler into a 500, so an unreadable body produced a
    manufactured server error where the API returns the 400 model binding gives it — and a client
    could not tell "you sent nonsense" from "the server fell over".
  */
  const KEY = { 'Idempotency-Key': '3f2504e0-4f89-41d3-9a0c-0305e82c3311' };

  const HANDLERS: ReadonlyArray<[string, string, string, Record<string, string>]> = [
    ['create account', 'POST', '/api/accounts', {}],
    ['rename account', 'PATCH', `/api/accounts/${MAIN_ACCOUNT_ID}`, {}],
    ['deposit', 'POST', '/api/transactions/deposit', KEY],
    ['withdraw', 'POST', '/api/transactions/withdraw', KEY],
    ['rename azure tag', 'PATCH', '/bff/auth/azuretag', {}],
    ['transfer', 'POST', '/api/transfers', KEY],
    ['internal transfer', 'POST', '/api/transfers/internal', KEY],
    ['verify pin', 'POST', '/bff/auth/verify-pin', {}],
    ['set pin', 'POST', '/bff/auth/set-pin', {}],
    ['login', 'POST', '/bff/auth/login', {}],
    ['register', 'POST', '/bff/auth/register', {}],
  ];

  const send = (method: string, url: string, body: string, extra: Record<string, string>) =>
    fetch(url, { method, headers: { 'Content-Type': 'application/json', ...extra }, body });

  it.each(HANDLERS)(
    '%s %s %s: malformed JSON is a 400, not a 500',
    async (_n, method, url, extra) => {
      const res = await send(method, url, 'not json', extra);
      const body = await res.json();

      expect(res.status).toBe(400);
      expect(body.errors.$).toEqual(['The request body could not be read as JSON.']);
      /*
        The ENVELOPE, not just the key. An unreadable body never reaches a FluentValidation
        validator — it fails in the input formatter, so `[ApiController]` model state renders it
        and the answer is the framework's shape: the rfc9110 type, "One or more validation errors
        occurred." as the title, and NO `detail` member at all. The mock used the handler-written
        envelope ("Validation Failed", httpstatuses type, a detail sentence), which is the one
        thing this test could not see while it asserted only `errors.$`.
      */
      expect(body.type).toBe('https://tools.ietf.org/html/rfc9110#section-15.5.1');
      expect(body.title).toBe('One or more validation errors occurred.');
      expect(body.detail).toBeUndefined();
      expect(body.instance).toBeUndefined();
    },
  );

  it.each(HANDLERS)('%s %s %s: an empty body is a 400 too', async (_n, method, url, extra) => {
    /*
      ASP.NET distinguishes the two, and it is not only the sentence that differs — the KEY does.
      This test asserted `$` for both, which is what the mock used to emit; measured on the
      running stack, an ABSENT body is keyed by the EMPTY STRING and only a malformed one by `$`:

        POST /api/accounts ""             -> {"":["A non-empty request body is required."], "request":[…]}
        POST /api/accounts "not json"     -> {"$":["'not json' is an invalid JSON literal. …"], "request":[…]}

      Same on the BFF (`POST /bff/auth/login`), so one shape covers all eleven rows. The empty
      string is a SIXTH distinct key shape in this API, after `Name`, `at`, `$`, `$.type` and
      FluentValidation's camelCase.
    */
    const res = await send(method, url, '', extra);
    const body = await res.json();

    expect(res.status).toBe(400);
    expect(body.errors['']).toEqual(['A non-empty request body is required.']);
    expect(body.errors.$).toBeUndefined();
    // The framework names the action's body parameter too, and it is `request` in every controller
    // on both hosts. Asserted because its ABSENCE was part of the old wrong shape.
    expect(body.errors.request).toEqual(['The request field is required.']);
  });

  it.each(HANDLERS)(
    '%s %s %s: a WHITESPACE body is unparseable, not absent',
    async (_n, method, url, extra) => {
      /*
        The boundary between the two branches above, and the mock had it in the wrong place: a
        `trim()` classified "   " as an empty body and answered with the `""` key. Measured on the
        running stack — whitespace is sent, so it reaches System.Text.Json and fails there:

          POST /api/accounts "   "
          -> {"$":["The input does not contain any JSON tokens. …BytePositionInLine: 3."], …}

        Absent vs unparseable is the whole distinction these two keys encode, so a body that is
        present-but-blank has to land on the parse side of it.
      */
      const res = await send(method, url, '   ', extra);
      const body = await res.json();

      expect(res.status).toBe(400);
      expect(body.errors.$).toEqual(['The request body could not be read as JSON.']);
      expect(body.errors['']).toBeUndefined();
    },
  );

  it('a JSON array is not a body either', async () => {
    // Asserting only the 400 made this VACUOUS: with the `Array.isArray` guard deleted the array
    // sails through as an object, `name` is undefined, and the NAME validator returns a 400 of its
    // own. Same status, different reason, test still green. The `$` key is what distinguishes
    // "I could not read this" from "I read it and the name is wrong".
    const res = await send('POST', '/api/accounts', '["Holiday Fund"]', {});

    expect(res.status).toBe(400);
    expect((await res.json()).errors.$).toEqual(['The request body could not be read as JSON.']);
  });

  it('a readable body with an ARRAY where a string belongs is refused, and stores nothing', async () => {
    /*
      A different bug from the ones above, and one I introduced fixing them: I widened the account
      -type check to `includes(String(body.type))`, and `String(['Checking'])` is `'Checking'`. So
      the array passed validation and was then written onto `account.type`, where the response
      contract promises a string.

      The identical trap is already documented on the azureTag handler — `RegExp.test(['abc'])`
      stringifies too — which is why the guard is a type guard now and why this asserts the STATE,
      not just the status: a 400 alone would not have proven nothing was stored.
    */
    const before = mockState.accounts.length;
    const res = await send(
      'POST',
      '/api/accounts',
      JSON.stringify({ name: 'Holiday Fund', type: ['Checking'] }),
      {},
    );

    expect(res.status).toBe(400);
    expect((await res.json()).errors.type).toEqual([
      'Invalid account type. Must be Checking, Savings, or Investment.',
    ]);
    expect(mockState.accounts).toHaveLength(before);
  });
});
