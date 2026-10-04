import { describe, expect, it } from 'vitest';
import {
  bffDemoClaimResponseSchema,
  bffMeResponseSchema,
  bffSessionStatusResponseSchema,
  demoCopyInfoSchema,
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

// A demo claim answers what a sign-in answers, plus the copy the visitor was given: the five
// members of `DemoCopyInfo` (backend/src/AzureBank.Shared/DTOs/Auth/DemoClaimResponse.cs). The
// answer carries two ends, the access token's beside `user` and the copy's inside `copy`, and
// every fixture here puts them on different instants so that neither can stand in for the other.
describe("a demo claim's answer", () => {
  // The shape `DemoCredentials.Create` draws for a copy's owner
  // (backend/tools/AzureBank.Seeder/Pool/DemoCredentials.cs): `demo-`, sixteen characters of
  // a-z and 0-9, and the reserved domain.
  const poolAddress = 'demo-k7m2x9q4w8e1r5t3@azurebank.example';

  const user = {
    id: 'u1',
    email: poolAddress,
    firstName: 'John',
    lastName: 'Smith',
    azureTag: 'john_k7m2',
    hasPin: true,
  };

  const copy = {
    email: poolAddress,
    password: 'Fixture-Pass-7!',
    pin: '987654',
    contacts: ['jane_k7m2', 'mike_k7m2'],
    expiresAt: '2026-10-05T09:00:00Z',
  };

  const tokenEnd = '2026-10-04T09:15:00Z';
  const answer = { user, expiresAt: tokenEnd, copy };

  it("accepts a claim's answer, keeps its two ends apart, and strips what it does not know", () => {
    const parsed = bffDemoClaimResponseSchema.parse({
      ...answer,
      copy: { ...copy, somethingNew: 123 },
    });

    expect(parsed.copy).toStrictEqual({
      email: 'demo-k7m2x9q4w8e1r5t3@azurebank.example',
      password: 'Fixture-Pass-7!',
      pin: '987654',
      contacts: ['jane_k7m2', 'mike_k7m2'],
      expiresAt: '2026-10-05T09:00:00Z',
    });
    expect(parsed.expiresAt).toBe('2026-10-04T09:15:00Z');
  });

  /** Whether a copy passes: alone, and inside an answer that is whole but for it. */
  function accepts(candidate: unknown) {
    return {
      alone: demoCopyInfoSchema.safeParse(candidate).success,
      inAnAnswer: bffDemoClaimResponseSchema.safeParse({ ...answer, copy: candidate }).success,
    };
  }

  it('refuses an answer with no copy', () => {
    // A sign-in's answer, whole: it is not a claim's.
    expect(bffDemoClaimResponseSchema.safeParse({ user, expiresAt: tokenEnd }).success).toBe(false);
  });

  it('refuses a copy with a member missing or retyped', () => {
    // One comparison, so a red run prints every case: each of the five members taken out alone;
    // the address not an address, the password a list, the PIN a number and the contacts a text,
    // each alone; and each text left empty alone. The end's forms have two tests of their own
    // below.
    const without = (member: keyof typeof copy) =>
      Object.fromEntries(Object.entries(copy).filter(([name]) => name !== member));
    const passes = { alone: true, inAnAnswer: true };
    const refused = { alone: false, inAnAnswer: false };

    expect({
      whole: accepts(copy),
      noEmail: accepts(without('email')),
      noPassword: accepts(without('password')),
      noPin: accepts(without('pin')),
      noContacts: accepts(without('contacts')),
      noEnd: accepts(without('expiresAt')),
      emailNotAnAddress: accepts({ ...copy, email: 'demo-k7m2x9q4w8e1r5t3' }),
      passwordAsList: accepts({ ...copy, password: ['Fixture-Pass-7!'] }),
      pinAsNumber: accepts({ ...copy, pin: 987654 }),
      contactsAsText: accepts({ ...copy, contacts: 'jane_k7m2' }),
      emptyPassword: accepts({ ...copy, password: '' }),
      emptyPin: accepts({ ...copy, pin: '' }),
      emptyHandle: accepts({ ...copy, contacts: ['jane_k7m2', ''] }),
    }).toStrictEqual({
      whole: passes,
      noEmail: refused,
      noPassword: refused,
      noPin: refused,
      noContacts: refused,
      noEnd: refused,
      emailNotAnAddress: refused,
      passwordAsList: refused,
      pinAsNumber: refused,
      contactsAsText: refused,
      emptyPassword: refused,
      emptyPin: refused,
      emptyHandle: refused,
    });
  });

  it("refuses a copy's end that names no zone", () => {
    // `new Date` reads this form as local time, so the copy's end would move by the viewer's
    // offset from UTC with nothing said. The same instant with a Z is the fixture's own.
    expect({
      withZ: accepts({ ...copy, expiresAt: '2026-10-05T09:00:00Z' }),
      noZone: accepts({ ...copy, expiresAt: '2026-10-05T09:00:00' }),
    }).toStrictEqual({
      withZ: { alone: true, inAnAnswer: true },
      noZone: { alone: false, inAnAnswer: false },
    });
  });

  it("accepts a copy's end written with a Z or with an offset", () => {
    // Seven fractional digits, as .NET writes an instant; the offset form is the one
    // `apiOffsetInstant` in src/mocks/handlers.ts records for a `DateTimeOffset`.
    const ends = ['2026-10-05T09:00:00.1234567Z', '2026-10-05T09:00:00.1234567+00:00'];

    const parsedCopies = ends.map((end) => {
      const result = bffDemoClaimResponseSchema.safeParse({
        ...answer,
        copy: { ...copy, expiresAt: end },
      });
      return result.success ? result.data.copy : 'refused';
    });

    expect(parsedCopies).toStrictEqual([
      { ...copy, expiresAt: '2026-10-05T09:00:00.1234567Z' },
      { ...copy, expiresAt: '2026-10-05T09:00:00.1234567+00:00' },
    ]);
  });

  it("an address of the pool's shape is an email", () => {
    // CONTROL: green before this change
    expect(userSessionInfoSchema.safeParse(user).success).toBe(true);
  });
});
