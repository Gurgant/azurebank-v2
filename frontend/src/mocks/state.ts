/**
 * Shared mutable state for the STATEFUL mock handlers — the semantics that make the mocks
 * honest (idempotency replay, step-up level) rather than shape-only stubs.
 * Reset between tests by src/test/setup.ts.
 */
import type { AccountType } from '../api/enums';

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

export interface MockAccount {
  id: string;
  accountNumber: string;
  name: string;
  type: AccountType;
  balance: number;
  isPrimary: boolean;
  createdAt: string;
}

interface MockState {
  /** key -> stored response, per (endpoint|key) like the backend's (user, endpoint, key). */
  idempotency: Map<string, StoredIdempotentResponse>;
  /** BFF session auth level: 1 = password, 2 = PIN-verified (transfers need 2). */
  authLevel: 1 | 2;
  /** BFF session: null = no cookie/anonymous (default — tests seed or log in explicitly). */
  session: MockSessionUser | null;
  /**
   * The session user's accounts — REAL contract shapes: PascalCase types, and numbers
   * arrive ALREADY MASKED (`AB-****-****-90`) because AccountMapper.MaskAccountNumber
   * runs server-side; the full number never leaves the API.
   */
  accounts: MockAccount[];
}

function defaultAccounts(): MockAccount[] {
  return [
    {
      id: '019f7b3f-0000-7000-8000-0000000000a1',
      accountNumber: 'AB-****-****-90',
      name: 'Main Account',
      type: 'Checking',
      balance: 1250.5,
      isPrimary: true,
      createdAt: '2026-07-01T09:00:00.0000000Z',
    },
    {
      id: '019f7b3f-0000-7000-8000-0000000000a2',
      accountNumber: 'AB-****-****-01',
      name: 'Rainy Day',
      type: 'Savings',
      balance: 830.0,
      isPrimary: false,
      createdAt: '2026-07-05T09:00:00.0000000Z',
    },
  ];
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
  accounts: defaultAccounts(),
};

/** Test helper: start authenticated without walking the login flow. */
export function seedMockSession(user: MockSessionUser = MOCK_USER): void {
  mockState.session = { ...user };
}

export function resetMockState(): void {
  mockState.idempotency.clear();
  mockState.authLevel = 1;
  mockState.session = null;
  mockState.accounts = defaultAccounts();
}
