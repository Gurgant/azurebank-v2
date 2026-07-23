import type { z } from 'zod';
import type {
  userSessionInfoSchema,
  bffSessionInfoSchema,
  bffLoginResponseSchema,
  bffMeResponseSchema,
  bffSessionStatusResponseSchema,
  bffPinVerificationResponseSchema,
} from './bffSchemas';

/**
 * Wire shapes of the BFF's own endpoints (/bff/auth/*). These are NOT in the generated OpenAPI
 * schema — the spec covers the API surface only — so they are hand-written mirrors of
 * backend/src/AzureBank.Bff/DTOs/BffResponses.cs (camelCased by ASP.NET). The single source of
 * truth is now the Zod schemas in bffSchemas.ts (validated at runtime); these types are inferred
 * from them so the type and the validator can never disagree. Request bodies stay in the generated
 * schema (LoginRequest, RegisterRequest, ...).
 */

export type UserSessionInfo = z.infer<typeof userSessionInfoSchema>;
export type BffSessionInfo = z.infer<typeof bffSessionInfoSchema>;
export type BffLoginResponse = z.infer<typeof bffLoginResponseSchema>;
export type BffMeResponse = z.infer<typeof bffMeResponseSchema>;
/** B5 — the SECOND bare (non-envelope) response besides GET /api/transactions. */
export type BffSessionStatusResponse = z.infer<typeof bffSessionStatusResponseSchema>;
export type BffPinVerificationResponse = z.infer<typeof bffPinVerificationResponseSchema>;
