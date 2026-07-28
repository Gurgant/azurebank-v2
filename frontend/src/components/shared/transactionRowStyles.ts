import { makeStyles } from '@fluentui/react-components';
import { atMedia } from '../../theme/breakpoints';
import { colors, surfaces } from '../../theme/tokens';
import { tokens } from '@fluentui/react-components';
import type { TransactionResponse } from '../../features/api/apiSlice';

/**
 * The transaction row's styles and its two pure rules, in a JSX-free module.
 *
 * Split from `TransactionRow.tsx` because `react-refresh/only-export-components` is an ERROR here:
 * a `.tsx` may export components or it may export helpers, not both. Same reason `navItems.ts` is
 * not a `.tsx`.
 */

export const useTransactionRowStyles = makeStyles({
  td: {
    padding: '12px 8px',
    fontSize: '14px',
    color: colors.neutral[800],
    borderBottom: `1px solid ${surfaces.border}`,
    verticalAlign: 'top',
  },

  num: {
    textAlign: 'right',
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
  },

  when: {
    fontSize: '13px',
    color: colors.neutral[600],
    whiteSpace: 'nowrap',
  },

  open: {
    background: 'none',
    border: 'none',
    padding: 0,
    font: 'inherit',
    color: colors.neutral[800],
    cursor: 'pointer',
    textAlign: 'left',
    ':hover': { textDecoration: 'underline' },
    ':focus-visible': { outline: `2px solid ${colors.brand[60]}`, outlineOffset: '2px' },
  },

  in: { color: colors.semantic.success.dark, fontWeight: 600 },
  out: { color: colors.neutral[800], fontWeight: 600 },

  /**
   * A reversed or failed entry did not stand. Printing its amount as a plain debit asserts that
   * money left the account when it came back; the strike says otherwise without inventing a sign
   * the API never sent.
   */
  void: { textDecoration: 'line-through', color: colors.neutral[500] },

  /** Never colour alone — the pill carries the word. */
  pill: {
    display: 'inline-block',
    padding: '2px 8px',
    borderRadius: '999px',
    fontSize: '11px',
    fontWeight: 600,
    whiteSpace: 'nowrap',
    backgroundColor: colors.neutral[100],
    color: colors.neutral[700],
  },
  pillPending: {
    backgroundColor: colors.semantic.warning.light,
    color: colors.semantic.warning.dark,
  },
  pillDone: {
    backgroundColor: colors.semantic.success.light,
    color: colors.semantic.success.dark,
  },

  /** No room for a fifth column on a phone, and a running balance is the one that can wait. */
  balance: {
    display: 'none',
    [atMedia.md]: { display: 'table-cell' },
  },

  th: {
    padding: '8px 8px 10px',
    fontSize: '11px',
    fontWeight: 600,
    letterSpacing: '0.06em',
    textTransform: 'uppercase',
    color: colors.neutral[500],
    textAlign: 'left',
    borderBottom: `1px solid ${surfaces.border}`,
  },

  table: {
    width: '100%',
    borderCollapse: 'collapse',
    tableLayout: 'fixed',
  },

  muted: { fontSize: '13px', color: colors.neutral[500] },

  skeletonBar: {
    height: '1em',
    borderRadius: '6px',
    backgroundColor: colors.neutral[100],
  },

  dayRow: {
    padding: '14px 8px 6px',
    fontSize: '12px',
    fontWeight: 600,
    letterSpacing: '0.04em',
    textTransform: 'uppercase',
    color: colors.neutral[500],
    backgroundColor: tokens.colorNeutralBackground1,
  },
});

/**
 * What a row is CALLED, derived once.
 *
 * The API sends a description only sometimes, and names the counterparty differently by direction —
 * `recipientAzureTag` going out, `senderAzureTag` coming in. Every screen that showed transactions
 * re-derived this, and they did not agree.
 */
export function transactionLabel(t: TransactionResponse): string {
  if (t.description) return t.description;
  if (t.type === 'TransferOut' && t.recipientAzureTag) return `To @${t.recipientAzureTag}`;
  if (t.type === 'TransferIn' && t.senderAzureTag) return `From @${t.senderAzureTag}`;
  return t.type;
}

/** True when the entry did not stand, so its amount should not read as money that moved. */
export function isVoided(t: TransactionResponse): boolean {
  return t.status === 'Reversed' || t.status === 'Failed';
}

/**
 * The cell classes, for the one place that needs them outside a row: the dashboard's `<tfoot>`.
 *
 * Exported rather than re-declared, because the balance placeholder in that foot has to appear and
 * disappear on EXACTLY the same media query as the Balance column above it. Two copies of that
 * query is two chances to move one and not the other, and the failure is a column count that
 * differs between head and foot at some widths only.
 */
export function useTransactionCellStyles() {
  const styles = useTransactionRowStyles();
  return { cell: styles.td, number: styles.num, balanceOnly: styles.balance };
}
