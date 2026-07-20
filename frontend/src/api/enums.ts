import type { components } from './schema';

/**
 * The SINGLE source of wire enums — re-exported from the generated schema so the values
 * can never drift from the backend (PascalCase strings on the wire; integers rejected).
 * The lowercase page-local unions still living next to mock data die with their pages'
 * own PRs; anything new imports from here.
 */
export type AccountType = components['schemas']['AccountType']; // 'Checking' | 'Savings' | 'Investment'
export type TransactionType = components['schemas']['TransactionType']; // 'Deposit' | 'Withdrawal' | 'TransferIn' | 'TransferOut'
export type TransactionStatus = components['schemas']['TransactionStatus']; // 'Pending' | 'Completed' | 'Failed' | 'Reversed'
