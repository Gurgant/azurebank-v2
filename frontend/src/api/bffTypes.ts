/**
 * Wire shapes of the BFF's own endpoints (/bff/auth/*). These are NOT in the generated
 * OpenAPI schema — the spec covers the API surface only — so they are hand-written
 * mirrors of backend/src/AzureBank.Bff/DTOs/BffResponses.cs (camelCased by ASP.NET).
 * If BffResponses.cs changes, this file is the other side of the contract.
 * Request bodies are NOT duplicated here: the BFF forwards the API's own request DTOs,
 * which the generated schema already types (LoginRequest, RegisterRequest, ...).
 */

export interface UserSessionInfo {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  azureTag: string;
  hasPin: boolean;
}

export interface BffSessionInfo {
  /** 1 = password session, 2 = PIN-elevated (step-up). */
  authLevel: number;
  createdAt: string;
  lastActivity: string;
  expiresAt: string;
  isPinVerified: boolean;
  pinExpiresAt: string | null;
}

export interface BffLoginResponse {
  user: UserSessionInfo;
  expiresAt: string;
}

export interface BffMeResponse {
  user: UserSessionInfo;
  session: BffSessionInfo;
}

/** B5 — the SECOND bare (non-envelope) response besides GET /api/transactions. */
export interface BffSessionStatusResponse {
  isAuthenticated: boolean;
  authLevel: number | null;
  isPinVerified: boolean | null;
}

export interface BffPinVerificationResponse {
  verified: boolean;
  authLevel: number;
  pinExpiresAt: string | null;
}
