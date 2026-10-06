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

/**
 * The copy a demo claim hands the visitor: the five members of `DemoCopyInfo`
 * (backend/src/AzureBank.Shared/DTOs/Auth/DemoClaimResponse.cs).
 *
 * `password`, `pin` and `contacts` are checked for presence and type, and their texts for not
 * being empty: for nothing else. Their shapes are the server's to choose, and this file fails
 * closed: a pattern here would turn a change of what the server hands out into a claim the
 * visitor cannot make. bffSchemas.test.ts holds both: an empty text is refused, and a password,
 * a PIN and contacts shaped unlike today's are accepted.
 */
export const demoCopyInfoSchema = z.object({
  email: z.email(),
  password: z.string().min(1),
  pin: z.string().min(1),
  contacts: z.array(z.string().min(1)),
  /**
   * The copy's end: not the session's, and not the access token's, which is the `expiresAt`
   * beside `user`.
   *
   * Strict, where every other date in this file is `z.string()`: this one is an instant to be
   * compared with a clock and shown, and `new Date` reads a string that names no zone as local
   * time, which would move the copy's end by the viewer's offset with nothing said. A `Z` and a
   * numeric offset both name an exact instant and both pass. `{ offset: true }` is what lets the
   * second through: without it `z.iso.datetime()` refuses `+00:00`, the form `apiOffsetInstant`
   * in src/mocks/handlers.ts records for a `DateTimeOffset`. bffSchemas.test.ts holds the three
   * forms.
   */
  expiresAt: z.iso.datetime({ offset: true }),
});

/** A demo claim's answer: what a sign-in answers, and the copy. */
export const bffDemoClaimResponseSchema = bffLoginResponseSchema.extend({
  copy: demoCopyInfoSchema,
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
export const bffSessionStatusResponseSchema = z
  .object({
    isAuthenticated: z.boolean(),
    authLevel: z.number().nullable(),
    isPinVerified: z.boolean().nullable(),
    /**
     * The server's clock at the moment it answered — what makes the two deadlines usable as
     * DURATIONS. Subtracting a deadline from this is pure server arithmetic; subtracting it from
     * `Date.now()` would smuggle the reader's clock skew back in, which is what publishing deadlines
     * was supposed to remove.
     */
    serverTime: z.string().nullish(),
    inactivityExpiresAt: z.string().nullish(),
    absoluteExpiresAt: z.string().nullish(),
  })
  /**
   * A deadline without `serverTime` is not merely incomplete, it is unusable: converting it to a
   * duration would need the reader's clock, which is the skew this whole contract exists to remove.
   * The consumer already refuses to act on such a response, but silently — so a BFF that shipped
   * one deadline and forgot the anchor would look like a working session that simply never warns.
   * Failing the parse turns that into a visible error instead of a quiet no-op.
   */
  .refine((v) => !(v.inactivityExpiresAt || v.absoluteExpiresAt) || Boolean(v.serverTime), {
    error: 'session-status sent a deadline without serverTime; a deadline is unusable without it',
    path: ['serverTime'],
  });

export const bffPinVerificationResponseSchema = z.object({
  verified: z.boolean(),
  authLevel: z.number(),
  pinExpiresAt: z.string().nullable(),
});
