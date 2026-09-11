import { format } from 'date-fns';
import type { TransactionType } from '../api/enums';

/**
 * The one currency and the one locale this client formats money in. The API states its currency
 * on every balance (`"currency":"EUR"`, measured 2026-09-11), and readPath.integration.test.ts
 * asserts on the real stack that it equals CURRENCY, so a server in another currency turns that
 * test red instead of being shown in euros. The locale is FIXED, not the browser's: every amount
 * reads as en-IE euros whatever language the user runs.
 */
export const CURRENCY = 'EUR';
export const LOCALE = 'en-IE';

/** Display formatting for contract data, in CURRENCY and LOCALE. */
export function formatCurrency(amount: number): string {
  return new Intl.NumberFormat(LOCALE, {
    style: 'currency',
    currency: CURRENCY,
    minimumFractionDigits: 2,
  }).format(amount);
}

/**
 * Display masking for account numbers (AB-XXXX-XXXX-XX): keep the scheme prefix and the
 * last group, mask the middle. The API already masks SERVER-SIDE (AccountMapper emits
 * `AB-****-****-90`; the full number never leaves the backend) — here that normalizes to
 * display bullets, and doubles as defense-in-depth: if a full number ever reached the
 * client, it still would not reach the screen.
 */
/**
 * Money direction comes from the TYPE, never from the amount's sign: the API sends
 * UNSIGNED amounts (the old mock carried signed ones — the summary-math trap).
 */
export function isIncomeType(type: TransactionType): boolean {
  return type === 'Deposit' || type === 'TransferIn';
}

/** Signed display amount for a transaction row: +€50.00 / -€50.00. */
export function formatTransactionAmount(amount: number, type: TransactionType): string {
  const sign = isIncomeType(type) ? '+' : '-';
  return `${sign}${formatCurrency(Math.abs(amount))}`;
}

/** "January 5, 2026" — date-group headings. */
export function formatDateHeading(isoDate: string): string {
  return format(new Date(isoDate), 'MMMM d, yyyy');
}

/** "9:15 AM" — row-level time. */
export function formatTime(isoDate: string): string {
  return format(new Date(isoDate), 'h:mm a');
}

/** "January 5, 2026 · 9:15 AM" — detail-level timestamp. */
export function formatDateTime(isoDate: string): string {
  return format(new Date(isoDate), 'MMMM d, yyyy · h:mm a');
}

/** "15 minutes" / "45 seconds" — a STATIC PIN-lock horizon (the window is minutes long). */
export function formatLockHorizon(seconds: number): string {
  if (seconds >= 60) {
    const minutes = Math.ceil(seconds / 60);
    return `${minutes} minute${minutes === 1 ? '' : 's'}`;
  }
  return `${seconds} second${seconds === 1 ? '' : 's'}`;
}

export function maskAccountNumber(accountNumber: string): string {
  const parts = accountNumber.split('-');
  if (parts.length < 3) {
    // Unexpected format: fail CLOSED — a defense-in-depth layer that echoed the
    // input verbatim would defeat its own purpose. Keep only the last 2 characters.
    if (accountNumber.length <= 2) {
      return '••';
    }
    return `${'•'.repeat(accountNumber.length - 2)}${accountNumber.slice(-2)}`;
  }
  return [
    parts[0],
    ...parts.slice(1, -1).map((group) => '•'.repeat(group.length)),
    parts[parts.length - 1],
  ].join('-');
}
