import { z } from 'zod';

/**
 * Runtime Zod schemas for the BFF's own responses (/bff/auth/*). These are the SOURCE OF TRUTH:
 * the TypeScript types in bffTypes.ts are derived from them via z.infer, so the compile-time shape
 * and the runtime validator can never drift. The BFF DTOs are hand-written mirrors of
 * BffResponses.cs (not in the OpenAPI spec), so this is the weakest contract boundary — validating
 * it at runtime catches a silent FE↔BFF drift (which feeds the auth slice) instead of trusting a
 * cast. Unknown extra keys are stripped by default (forward-compatible); a missing/retyped field
 * fails the parse (fail-closed: the query rejects).
 */

export const userSessionInfoSchema = z.object({
  id: z.string(),
  email: z.string(),
  firstName: z.string(),
  lastName: z.string(),
  azureTag: z.string(),
  hasPin: z.boolean(),
});

export const bffSessionInfoSchema = z.object({
  authLevel: z.number(),
  createdAt: z.string(),
  lastActivity: z.string(),
  expiresAt: z.string(),
  isPinVerified: z.boolean(),
  pinExpiresAt: z.string().nullable(),
});

export const bffLoginResponseSchema = z.object({
  user: userSessionInfoSchema,
  expiresAt: z.string(),
});

export const bffMeResponseSchema = z.object({
  user: userSessionInfoSchema,
  session: bffSessionInfoSchema,
});

/** B5 — bare (non-envelope) response. */
export const bffSessionStatusResponseSchema = z.object({
  isAuthenticated: z.boolean(),
  authLevel: z.number().nullable(),
  isPinVerified: z.boolean().nullable(),
});

export const bffPinVerificationResponseSchema = z.object({
  verified: z.boolean(),
  authLevel: z.number(),
  pinExpiresAt: z.string().nullable(),
});
