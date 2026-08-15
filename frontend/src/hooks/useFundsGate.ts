import { useCallback } from 'react';
import { useGetAccountsQuery } from '../features/api/apiSlice';
import { availableBalanceOf } from '../utils/availableBalance';

/**
 * What a fresh look at the server says about an amount this form is about to commit to.
 *
 * `unknown` is not a failure to handle later — it is the deliberate FAIL-OPEN branch. If the
 * balance cannot be re-read (offline, 5xx, the account no longer in the list), the operation must
 * still be allowed to proceed: this gate is a courtesy that saves the user a doomed round trip, and
 * the server's own `InsufficientFundsException` is the actual control. A gate that blocked on its
 * own network trouble would turn a nicety into an outage.
 */
export type FundsVerdict =
  | { status: 'ok' }
  | { status: 'insufficient'; available: number }
  | { status: 'unknown' };

/**
 * Re-read the source account's balance from the server, and answer whether an amount still fits.
 *
 * WHY A ROUND TRIP AND NOT THE CACHE. The Zod bound on every outflow form is built from
 * `getAccounts`, which is a cache. Nothing refreshed it between the moment the form rendered and
 * the moment the user commits, so the bound could be one page-load old — and a balance moved from
 * another device, or by an incoming transfer, leaves an amount the form believes is fine and the
 * server will refuse. That refusal is not free: measured against the running API, the PIN is
 * verified BEFORE the funds check (an over-balance withdrawal with a wrong PIN answers
 * 401 INVALID_PIN, not 422), so a doomed request still burns one of three PIN attempts and three
 * of those lock the PIN for fifteen minutes. Asking the server once, before spending the ceremony,
 * is what stops that.
 *
 * WHAT IT CANNOT DO. It shrinks the stale window to one deliberate round trip; it does not close
 * it. Nothing in a browser can — the balance may move between this call and the write. That is why
 * the server keeps checking, and why callers must treat a green verdict as "worth trying", never
 * as "guaranteed".
 */
export function useFundsGate() {
  // Subscribing here does not add a request: every caller's parent already holds this query, so
  // this is the same cache entry. What it adds is a `refetch` handle the dialog did not have —
  // `WithdrawDialog` receives its accounts as a PROP, and one call site passes a single-account
  // subset, so the prop cannot be trusted to contain the account being debited either.
  const { refetch } = useGetAccountsQuery();

  return useCallback(
    async (accountId: string, amount: number): Promise<FundsVerdict> => {
      try {
        const fresh = await refetch().unwrap();
        const account = fresh.find((candidate) => candidate.id === accountId);
        if (!account) {
          return { status: 'unknown' };
        }
        const available = availableBalanceOf(account);
        return amount <= available ? { status: 'ok' } : { status: 'insufficient', available };
      } catch {
        return { status: 'unknown' };
      }
    },
    [refetch],
  );
}
