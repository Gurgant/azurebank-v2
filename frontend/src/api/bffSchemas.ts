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
  email: z.email(),
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
  /**
   * Optional on purpose, and the reason is a deploy-order trap rather than uncertainty about the
   * contract. This file fails CLOSED on a missing required field, so a frontend that demanded this
   * before the BFF emitted it would reject every response and log the user out — the exact failure
   * the field exists to prevent. It becomes required once a BFF carrying it has shipped.
   *
   * On THIS endpoint it declares the inactivity WINDOW, not time remaining: fetching /bff/auth/me is
   * itself activity, so the value is always "now plus the window" by the time it is read.
   */
  inactivityExpiresAt: z.string().optional(),
});

export const bffLoginResponseSchema = z.object({
  user: userSessionInfoSchema,
  expiresAt: z.string(),
});

export const bffMeResponseSchema = z.object({
  user: userSessionInfoSchema,
  session: bffSessionInfoSchema,
});

/**
 * B5 — bare (non-envelope) response.
 *
 * The two deadlines are meaningful HERE and nowhere else. `SessionActivityMiddleware` slides the
 * server's `LastActivity` on every cookie-bearing request and excludes exactly one route — this
 * probe (ADR-0018) — so reading these does not push them forward. Measured on a running BFF: three
 * probes over eleven seconds returned an identical `inactivityExpiresAt` while the remaining time
 * fell 599s → 593s → 589s, and a single `/bff/auth/me` in between jumped it back to the full window.
 *
 * Both optional for the same deploy-order reason as `inactivityExpiresAt` above.
 */
export const bffSessionStatusResponseSchema = z.object({
  isAuthenticated: z.boolean(),
  authLevel: z.number().nullable(),
  isPinVerified: z.boolean().nullable(),
  inactivityExpiresAt: z.string().nullish(),
  absoluteExpiresAt: z.string().nullish(),
});

export const bffPinVerificationResponseSchema = z.object({
  verified: z.boolean(),
  authLevel: z.number(),
  pinExpiresAt: z.string().nullable(),
});
