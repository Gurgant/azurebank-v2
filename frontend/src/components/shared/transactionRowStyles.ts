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

/**
 * UNDER 480 PX OF ITS OWN BOX THE TABLE IS DRAWN AS TWO LINES A ROW, NOT AS FOUR COLUMNS
 * (since 2026-10-06).
 *
 * The box is `frame` below, the size container around the table, and not the screen: on one phone
 * the dashboard's card is 74 px narrower than History's list.
 *
 * Four columns do not fit a phone. The Amount column is 20 % of the table with 16 px of padding
 * in it, and an amount does not wrap: what does not fit runs out of its cell to the right, where
 * the status is. Measured in Chromium that day on the dashboard: at a 375 px screen (301 px of
 * table) "+€1,250.50" ended 10.7 px under the "Completed" pill; at 320 px three amounts of five
 * ran under theirs and the pill ended 20 px past the row.
 *
 * 480 is where four columns start to hold. The longest amount the app can show, "+€12,450.00",
 * is 78.66 px wide in the row's font on that machine, and is inside its cell, clear of the
 * padding, from 474 px of table up. At 480 a font whose figures are a tenth wider still leaves it
 * inside its own cell.
 *
 * The condition is an upper bound, where `theme/breakpoints.ts` asks for `min-width` only, and
 * on purpose. The table is what a wide box keeps, so it stays the unqualified case, rule for rule
 * what it was, and everything under this key only adds to it. _(Since 2026-10-07 the table has
 * one rule more than it had, at every width: a long word of the entry wraps, `cellEntry` below.)_
 * There is no second condition here for it to fight at the boundary.
 * Where one of these rules meets a media rule on the same
 * element (`balance`), it wins by the order the styling library writes them in, container rules
 * after media rules, and not by weight.
 *
 * Under it nothing is a table to the LAYOUT: the table, its groups and its plain rows are
 * blocks, and a transaction's row is a grid. A browser may drop the role of a `<tr>` that is not
 * displayed as one, so each element says its role in the markup (`TransactionRow.tsx`).
 */
const NARROW = '@container (width < 480px)';

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
    /**
     * Under `NARROW` the whole row is what a finger presses, and not the 20 px of the entry's
     * words: the button is stretched over the row by a box of its own that draws nothing (`row`
     * is what it is placed against). Measured at a 375 px screen, 2026-10-06: a row is 73 px
     * tall, 93 px with a note, and every point of it that was tried met this button.
     */
    [NARROW]: {
      '::after': { content: '""', position: 'absolute', top: 0, right: 0, bottom: 0, left: 0 },
    },
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

  /**
   * No room for a fifth column on a phone, and a running balance is the one that can wait. Nor
   * under `NARROW`, whatever the screen's width says: a row of two lines has no column for it.
   */
  balance: {
    display: 'none',
    [atMedia.md]: { display: 'table-cell' },
    [NARROW]: { display: 'none' },
  },

  /**
   * One transaction under `NARROW`, on two lines:
   *
   *     entry (its note under it)        amount
   *     when                             status
   *
   * The second column is as wide as the amount or the pill, whichever is wider, and neither
   * wraps; the first takes what is left, and what is in it wraps. So the amount is never cut and
   * never under anything, at any width: the entry gives way to it, a word at a time, and inside a
   * word when one is longer than the line. Nothing is cut with an ellipsis, because the only way
   * to read what an ellipsis hides is a pointer that hovers, and a phone has none.
   *
   * The cells stay in the markup in the columns' order, When first: that is the order of the
   * headings, and the order a screen reader goes through a row in.
   *
   * The row takes over the padding and the rule that the cells carry in the table, 12 px by
   * 8 px and one line under it, so the words start where the day headings of History start.
   */
  row: {
    [NARROW]: {
      display: 'grid',
      gridTemplateColumns: 'minmax(0, 1fr) auto',
      gridTemplateAreas: '"entry amount" "when status"',
      columnGap: '12px',
      rowGap: '4px',
      padding: '12px 8px',
      borderBottom: `1px solid ${surfaces.border}`,
      position: 'relative',
    },
  },
  /** A cell of that row: a box of the grid, with nothing of the table cell's own left on it. */
  cell: { [NARROW]: { display: 'block', minWidth: 0, padding: 0, borderBottom: 'none' } },
  /**
   * The entry wraps inside a word at EVERY width, in the table's columns as on the two lines
   * (since 2026-10-07; on 2026-10-06 it did so under `NARROW` only). A description is free text of
   * up to 500 characters and may be one word with no space in it. In the columns nothing broke
   * such a word: it ran out of the Entry column, over the amount and the status. Measured that
   * day in Chromium at an 800 px screen with sixty letters written as the entry: they ended
   * 509 px past the Entry column on the dashboard and 473 px on History.
   *
   * The table's layout is fixed, so no column is wider or narrower for this, and a word that
   * fitted its line is drawn where it was.
   */
  cellEntry: { overflowWrap: 'anywhere', [NARROW]: { gridArea: 'entry' } },
  cellAmount: { [NARROW]: { gridArea: 'amount', justifySelf: 'end' } },
  cellWhen: { [NARROW]: { gridArea: 'when', justifySelf: 'start', alignSelf: 'center' } },
  cellStatus: { [NARROW]: { gridArea: 'status', justifySelf: 'end', alignSelf: 'center' } },

  /**
   * The date and the time are one above the other in the When column and side by side on a
   * row's second line: there the break between them is dropped and the mark between them shown.
   */
  whenBreak: { [NARROW]: { display: 'none' } },
  whenJoin: { display: 'none', [NARROW]: { display: 'inline' } },

  /**
   * The headings under `NARROW`: not drawn, since no column is under them, and still there for
   * a screen reader. Clipped to nothing, not `display: none`, which would take them from it too.
   */
  head: {
    [NARROW]: {
      position: 'absolute',
      width: '1px',
      height: '1px',
      overflow: 'hidden',
      clipPath: 'inset(50%)',
      whiteSpace: 'nowrap',
    },
  },

  /** A group of rows, a day's heading, the empty row: under `NARROW`, each a block of its own. */
  block: { [NARROW]: { display: 'block' } },

  /**
   * The foot's cells and its row: the dashboard's "October so far" and its total. Under
   * `NARROW` the label and the total share one line, the label wrapping and the total whole,
   * and the cell that stands under Status, which is empty, takes no place.
   *
   * The rule above the foot is 2 px in the table, where it and the last row's 1 px are drawn as
   * one line. Under `NARROW` both are drawn, so there it is 1 px: 2 px in all, as in the table.
   */
  foot: {
    padding: '12px 8px',
    fontSize: '13px',
    fontWeight: 600,
    color: colors.neutral[700],
    borderTop: `2px solid ${surfaces.border}`,
    [NARROW]: { display: 'block', minWidth: 0, padding: 0, borderTop: 'none' },
  },
  footRow: {
    [NARROW]: {
      display: 'grid',
      gridTemplateColumns: 'minmax(0, 1fr) auto',
      columnGap: '12px',
      padding: '12px 8px',
      borderTop: `1px solid ${surfaces.border}`,
    },
  },
  footSpare: { [NARROW]: { display: 'none' } },

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
   *
   * Since later on 2026-10-06 a table under 480 px has no columns (`NARROW` above), so this
   * width is read from 480 px up only: it is 88 px up to 488 px and 18cqi from 489. The middle
   * value, the one for a table under 297 px, no longer applies to any table.
   */
  statusHead: { width: 'clamp(18cqi, calc(58cqi - 84px), 88px)' },

  /** Around the table and as wide as it: what `cqi` above is a share of. */
  frame: { containerType: 'inline-size' },

  table: {
    width: '100%',
    borderCollapse: 'collapse',
    tableLayout: 'fixed',
    [NARROW]: { display: 'block' },
  },

  muted: { fontSize: '13px', color: colors.neutral[500] },

  skeletonBar: {
    height: '1em',
    borderRadius: '6px',
    backgroundColor: colors.neutral[100],
  },

  /**
   * A loading row's cell under `NARROW`. Its bar is 14 px tall where a line of words is 20 or
   * 24, so 4 px above and under each bar make the row 73 px, which is what a row whose entry is
   * one line measures (2026-10-06); a row with a note is 20 px more. 64 px is the width of the
   * bars that stand for an amount, a time and a status, which have no words to be as wide as.
   */
  skeletonCell: { [NARROW]: { minWidth: '64px', padding: '4px 0' } },

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
