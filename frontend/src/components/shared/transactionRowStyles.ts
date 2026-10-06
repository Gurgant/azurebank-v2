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

  note: {
    display: 'block',
    fontSize: '12px',
    color: colors.neutral[500],
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

  /**
   * The Status column's width, set on its heading: the table's layout is fixed, so a column is as
   * wide as its first cell says and never as wide as what is in it.
   *
   * It was 18 % of the table at every width. Under a table of 480 px that is narrower than the
   * pill, which does not wrap: at 375 px it ran 11 px past its cell on History, off the screen,
   * and 25 px on the dashboard, over the card's edge (measured in Chromium, 2026-10-06).
   *
   * Three values, the middle one clamped between the other two, all in `cqi`: 1 % of the width of
   * `frame` below, which is the table's width. Percentages will not do it: with them in this
   * same `clamp`, Chromium laid the column out as one with no width at all and split what was
   * left evenly with Entry, 194 px each on the dashboard at 1440 px (measured the same day). The
   * viewport's width is no help either: on one phone the dashboard's table is 74 px narrower
   * than History's.
   * - `88px` is the widest pill, "Completed" at 71 px, with the cell's 16 px of padding.
   * - `58cqi - 84px` is what is left for this column once the Entry column has 84 px: When and
   *   Amount take 22 % and 20 %. So on a table too narrow for both, under 297 px, the pill
   *   gives way before a word such as "Withdrawal" (70 px) runs into the amount beside it. A flat
   *   88 px did that at 320 px on the dashboard: measured, the entry ran 23 px past its cell.
   * - `18cqi` is the 18 % it was, and is the larger from 489 px of table up: no table that wide
   *   changes.
   */
  statusHead: { width: 'clamp(18cqi, calc(58cqi - 84px), 88px)' },

  /** Around the table and as wide as it: what `cqi` above is a share of. */
  frame: { containerType: 'inline-size' },

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
  // The counterparty wins over the description on a transfer, and that resolves a genuine
  // disagreement rather than a formatting one: the dashboard printed "Dinner split" where History
  // printed "To @john_d" for the SAME row. WHO the money went to is the identifying fact on a
  // ledger — the note is why, and it survives as the second line below.
  if (t.type === 'TransferOut' && t.recipientAzureTag) return `To @${t.recipientAzureTag}`;
  if (t.type === 'TransferIn' && t.senderAzureTag) return `From @${t.senderAzureTag}`;
  if (t.description) return t.description;
  return t.type;
}

/** The note under the label, when it says something the label does not. */
export function transactionNote(t: TransactionResponse): string | null {
  const label = transactionLabel(t);
  return t.description && t.description !== label ? t.description : null;
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
