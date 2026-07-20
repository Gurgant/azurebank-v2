/**
 * Shared mutable state for the STATEFUL mock handlers — the semantics that make the mocks
 * honest (idempotency replay, step-up level) rather than shape-only stubs.
 * Reset between tests by src/test/setup.ts.
 */

export interface StoredIdempotentResponse {
  /** Fingerprint of the RAW request body bytes — the backend hashes bytes, not JSON. */
  bodyFingerprint: string;
  status: number;
  /** Stored body, replayed BYTE-IDENTICALLY (string compare in tests proves it). */
  body: string;
}

export interface MockSessionUser {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  azureTag: string;
  hasPin: boolean;
}

interface MockState {
  /** key -> stored response, per (endpoint|key) like the backend's (user, endpoint, key). */
  idempotency: Map<string, StoredIdempotentResponse>;
  /** BFF session auth level: 1 = password, 2 = PIN-verified (transfers need 2). */
  authLevel: 1 | 2;
  /** BFF session: null = no cookie/anonymous (default — tests seed or log in explicitly). */
  session: MockSessionUser | null;
}

/** The one seeded credential pair the mock login accepts. */
export const MOCK_USER: MockSessionUser = {
  id: '7c9e6679-7425-40de-944b-e07fc1f90ae7',
  email: 'demo@azurebank.dev',
  firstName: 'Demo',
  lastName: 'User',
  azureTag: 'demo_user',
  hasPin: true,
};
export const MOCK_PASSWORD = 'Password1!';

export const mockState: MockState = {
  idempotency: new Map(),
  authLevel: 1,
  session: null,
};

/** Test helper: start authenticated without walking the login flow. */
export function seedMockSession(user: MockSessionUser = MOCK_USER): void {
  mockState.session = { ...user };
}

export function resetMockState(): void {
  mockState.idempotency.clear();
  mockState.authLevel = 1;
  mockState.session = null;
}
