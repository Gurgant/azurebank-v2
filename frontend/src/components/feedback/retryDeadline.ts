/**
 * Absolute client-side deadline from the relative `retryAfterSeconds` (D13) — computed
 * from the local clock, never from server timestamps like `lockedUntil` (clock skew).
 */
export function retryDeadline(retryAfterSeconds: number): number {
  return Date.now() + retryAfterSeconds * 1000;
}
