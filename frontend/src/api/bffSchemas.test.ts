import { describe, expect, it } from 'vitest';
import {
  bffMeResponseSchema,
  bffSessionStatusResponseSchema,
  userSessionInfoSchema,
} from './bffSchemas';

// The point of the BFF schemas: bffTypes.ts is a HAND-WRITTEN mirror of BffResponses.cs (not in the
// OpenAPI spec), so a silent FE↔BFF drift would otherwise flow into the auth slice unchecked. These
// assert the validator actually rejects a bad shape (proving it's not a no-op) and strips extras.
describe('BFF response schemas', () => {
  const validUser = {
    id: 'u1',
    email: 'a@b.dev',
    firstName: 'A',
    lastName: 'B',
    azureTag: 'a_b',
    hasPin: true,
  };

  it('accepts a well-formed response', () => {
    expect(userSessionInfoSchema.safeParse(validUser).success).toBe(true);
    expect(
      bffMeResponseSchema.safeParse({
        user: validUser,
        session: {
          authLevel: 1,
          createdAt: 'x',
          lastActivity: 'x',
          expiresAt: 'x',
          isPinVerified: false,
          pinExpiresAt: null,
        },
      }).success,
    ).toBe(true);
  });

  it('REJECTS a drifted shape (wrong type / missing field)', () => {
    // hasPin retyped to a string — the exact class of silent drift this guards against.
    expect(userSessionInfoSchema.safeParse({ ...validUser, hasPin: 'yes' }).success).toBe(false);
    // session block missing → the whole /me response is invalid.
    expect(bffMeResponseSchema.safeParse({ user: validUser }).success).toBe(false);
    // authLevel must be number|null, not an arbitrary string.
    expect(
      bffSessionStatusResponseSchema.safeParse({
        isAuthenticated: true,
        authLevel: 'two',
        isPinVerified: null,
      }).success,
    ).toBe(false);
  });

  it('strips unknown extra keys (forward-compatible)', () => {
    const parsed = userSessionInfoSchema.parse({ ...validUser, somethingNew: 123 });
    expect(parsed).not.toHaveProperty('somethingNew');
  });
});
