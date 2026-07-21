/**
 * Display formatting for contract data. The API's currency is EUR (BalanceResponse
 * default) — the old mock pages showed USD, which dies as pages go live.
 */
export function formatCurrency(amount: number): string {
  return new Intl.NumberFormat('en-IE', {
    style: 'currency',
    currency: 'EUR',
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
