import { beforeEach, describe, expect, it } from 'vitest';
import { MAIN_ACCOUNT_ID, mockState, resetMockState } from '../../mocks/state';
import { seedMockSession } from '../../mocks/state';

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

  it.each([
    ['an empty name', { name: '', type: 'Checking' }, 'Account name is required.'],
    [
      'a one-character name',
      { name: 'X', type: 'Checking' },
      'Account name must be between 2 and 100 characters.',
    ],
    [
      'a 101-character name',
      { name: 'N'.repeat(101), type: 'Checking' },
      'Account name must be between 2 and 100 characters.',
    ],
  ])('rejects %s with the validator’s own sentence', async (_case, body, message) => {
    const res = await create(body);

    expect(res.status).toBe(400);
    expect((await res.json()).errors.name).toEqual([message]);
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
    expect((await res.json()).errors.name).toEqual([
      'Account name must be between 2 and 100 characters.',
    ]);
  });

  it('rejects a handle the pattern refuses, before the taken-handle conflict', async () => {
    // `[AzureTagQuery]` is model validation, so it runs ahead of the controller — a malformed
    // handle is a 400 even if it would also have collided.
    const res = await fetch('/api/users/me/azuretag', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ azureTag: '9nope' }),
    });

    expect(res.status).toBe(400);
    expect((await res.json()).errors.azureTag[0]).toContain('must start with a letter');
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
    ['rename azure tag', 'PATCH', '/api/users/me/azuretag', {}],
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

      expect(res.status).toBe(400);
      expect((await res.json()).errors.$).toEqual(['The request body could not be read as JSON.']);
    },
  );

  it.each(HANDLERS)('%s %s %s: an empty body is a 400 too', async (_n, method, url, extra) => {
    // ASP.NET distinguishes the two, and the sentence differs: a missing body is "A non-empty
    // request body is required." The malformed-JSON text embeds a parse position, so that one is
    // an honest approximation — the KEY, `$`, is faithful either way.
    const res = await send(method, url, '', extra);

    expect(res.status).toBe(400);
    expect((await res.json()).errors.$).toEqual(['A non-empty request body is required.']);
  });

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
