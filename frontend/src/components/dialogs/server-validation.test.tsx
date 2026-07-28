import { beforeEach, describe, expect, it } from 'vitest';
import { MAIN_ACCOUNT_ID, resetMockState } from '../../mocks/state';

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
