/**
 * Shared mutable state for the STATEFUL mock handlers — the semantics that make the mocks
 * honest (idempotency replay, step-up level) rather than shape-only stubs.
 * Reset between tests by src/test/setup.ts.
 */
import type { AccountType, TransactionStatus, TransactionType } from '../api/enums';

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

/** A transfer recipient in the exact-match directory (ADR-0014 — no substring/listing). */
export interface MockRecipient {
  azureTag: string;
  /** Masked display name for privacy, e.g. "John D." */
  displayName: string;
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

export interface MockTransaction {
  id: string;
  transactionNumber: string;
  type: TransactionType;
  /** UNSIGNED, like the real contract — direction lives in `type`. */
  amount: number;
  balanceAfter: number;
  description: string | null;
  recipientAzureTag: string | null;
  senderAzureTag: string | null;
  status: TransactionStatus;
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
   * The session user's PIN — the value withdraw (PIN-in-body, D1) verifies against. Set-pin
   * overwrites it and flips session.hasPin; it defaults to MOCK_PIN so the seeded MOCK_USER
   * (hasPin:true) can withdraw with '123456'.
   */
  pin: string;
  /** Consecutive wrong-PIN count; MaxPinAttempts (3) trips the lockout, a success resets it. */
  pinAttempts: number;
  /** ISO instant the PIN lock lifts, or null. A withdraw while locked is a 429 PIN_LOCKED. */
  pinLockedUntil: string | null;
  /**
   * The session user's accounts — REAL contract shapes: PascalCase types, and numbers
   * arrive ALREADY MASKED (`AB-****-****-90`) because AccountMapper.MaskAccountNumber
   * runs server-side; the full number never leaves the API.
   */
  accounts: MockAccount[];
  /** History feed, NEWEST FIRST like the real query orders it. */
  transactions: MockTransaction[];
  /** Exact-match transfer-recipient directory. Looking up self returns exists:false. */
  recipients: MockRecipient[];
}

/** Seeded recipients — 'friend' backs the stepup.test contract; the rest are demo handles. */
function defaultRecipients(): MockRecipient[] {
  return [
    { azureTag: 'friend', displayName: 'A. Friend' },
    { azureTag: 'john_d', displayName: 'John D.' },
    { azureTag: 'anna_k', displayName: 'Anna K.' },
  ];
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

/**
 * 25 seeded transactions (newest first): 5 hand-written heroes exercising every type,
 * a Pending and a Reversed status, transfer counterparties and a null description —
 * plus 20 fillers so the list spans TWO pages at the real page size of 20.
 */
function defaultTransactions(): MockTransaction[] {
  const heroes: MockTransaction[] = [
    {
      id: '019f7b3f-0000-7000-8000-000000000b01',
      transactionNumber: 'TXN-20260720-000101',
      type: 'Deposit',
      amount: 1250.5,
      balanceAfter: 2250.5,
      description: 'Salary — July',
      recipientAzureTag: null,
      senderAzureTag: null,
      status: 'Completed',
      createdAt: '2026-07-20T09:15:00.0000000Z',
    },
    {
      id: '019f7b3f-0000-7000-8000-000000000b02',
      transactionNumber: 'TXN-20260720-000102',
      type: 'Withdrawal',
      amount: 50,
      balanceAfter: 2200.5,
      description: null,
      recipientAzureTag: null,
      senderAzureTag: null,
      status: 'Completed',
      createdAt: '2026-07-20T18:30:00.0000000Z',
    },
    {
      id: '019f7b3f-0000-7000-8000-000000000b03',
      transactionNumber: 'TXN-20260719-000103',
      type: 'TransferOut',
      amount: 200,
      balanceAfter: 2000.5,
      description: 'Dinner split',
      recipientAzureTag: 'john_d',
      senderAzureTag: null,
      status: 'Pending',
      createdAt: '2026-07-19T14:00:00.0000000Z',
    },
    {
      id: '019f7b3f-0000-7000-8000-000000000b04',
      transactionNumber: 'TXN-20260719-000104',
      type: 'TransferIn',
      amount: 75,
      balanceAfter: 2075.5,
      description: null,
      recipientAzureTag: null,
      senderAzureTag: 'anna_k',
      status: 'Completed',
      createdAt: '2026-07-19T10:00:00.0000000Z',
    },
    {
      id: '019f7b3f-0000-7000-8000-000000000b05',
      transactionNumber: 'TXN-20260718-000105',
      type: 'Withdrawal',
      amount: 30,
      balanceAfter: 2045.5,
      description: 'ATM — disputed',
      recipientAzureTag: null,
      senderAzureTag: null,
      status: 'Reversed',
      createdAt: '2026-07-18T11:00:00.0000000Z',
    },
  ];
  const fillers: MockTransaction[] = Array.from({ length: 20 }, (_, i) => ({
    id: `019f7b3f-0000-7000-8000-000000000f${String(i).padStart(2, '0')}`,
    transactionNumber: `TXN-20260710-${String(200 + i).padStart(6, '0')}`,
    type: 'Deposit' as const,
    amount: 10,
    balanceAfter: 1000 + i * 10,
    description: `Top-up #${i + 1}`,
    recipientAzureTag: null,
    senderAzureTag: null,
    status: 'Completed' as const,
    // Descending days keep the whole array newest-first.
    createdAt: `2026-07-${String(10 - Math.floor(i / 3)).padStart(2, '0')}T12:${String(59 - i).padStart(2, '0')}:00.0000000Z`,
  }));
  return [...heroes, ...fillers];
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
/** The one seeded PIN the mock verifies withdrawals (and step-up) against. */
export const MOCK_PIN = '123456';

export const mockState: MockState = {
  idempotency: new Map(),
  authLevel: 1,
  session: null,
  pin: MOCK_PIN,
  pinAttempts: 0,
  pinLockedUntil: null,
  accounts: defaultAccounts(),
  transactions: defaultTransactions(),
  recipients: defaultRecipients(),
};

/** Test helper: start authenticated without walking the login flow. */
export function seedMockSession(user: MockSessionUser = MOCK_USER): void {
  mockState.session = { ...user };
}

export function resetMockState(): void {
  mockState.idempotency.clear();
  mockState.authLevel = 1;
  mockState.session = null;
  mockState.pin = MOCK_PIN;
  mockState.pinAttempts = 0;
  mockState.pinLockedUntil = null;
  mockState.accounts = defaultAccounts();
  mockState.transactions = defaultTransactions();
  mockState.recipients = defaultRecipients();
}
