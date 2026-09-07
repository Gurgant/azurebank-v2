/**
 * D17: business-rule 422s render INLINE at the owning surface, mapped by errorCode. Moved here
 * from AccountsPage unchanged — DeleteAccountDialog is the surface that owns them now, and the
 * page no longer needs them. Both the deletion mint and the DELETE answer these codes
 * (ADR-0049 D4), before the PIN is consulted on the mint (M2/M3 in
 * measure-after-main-19742ff-2026-09-06.txt).
 *
 * A `.ts` file beside the dialog rather than an export of the `.tsx`: `react-refresh/
 * only-export-components` is an error in this repo, and a component file exports components only.
 */
export const DELETE_RULES: Record<string, string> = {
  NON_ZERO_BALANCE: 'Only accounts with a zero balance can be deleted.',
  PRIMARY_ACCOUNT_DELETE: 'This is your primary account — set another account as primary first.',
};
