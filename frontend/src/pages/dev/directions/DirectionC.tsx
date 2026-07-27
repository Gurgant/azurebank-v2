import { useState } from 'react';
import type { MouseEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  Button,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  SkeletonItem,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowDownload20Regular,
  ArrowSwap20Regular,
  ArrowUpload20Regular,
} from '@fluentui/react-icons';
import { format, isSameDay, isToday, isYesterday, startOfMonth } from 'date-fns';
import { atMedia } from '../../../theme/breakpoints';
import { colors, shadows, surfaces } from '../../../theme/tokens';
import { useAppSelector } from '../../../app/hooks';
import { selectCurrentUser } from '../../../features/auth/authSlice';
import { useDelayedFlag } from '../../../hooks/useDelayedFlag';
import { useReducedMotion } from '../../../hooks/useReducedMotion';
import type { ApiProblem } from '../../../api/problemBaseQuery';
import type { TransactionType } from '../../../api/enums';
import type { AccountResponse, TransactionResponse } from '../../../features/api/apiSlice';
import {
  useGetAccountsQuery,
  useGetTransactionsQuery,
  useGetTransactionSummaryQuery,
} from '../../../features/api/apiSlice';
import { formatCurrency, formatTime, isIncomeType, maskAccountNumber } from '../../../utils/format';
import { DepositDialog, WithdrawDialog } from '../../../components';

/**
 * U3 scratch — direction C: **the page is a ledger**. `U3-SCRATCH-DASHBOARD-DIRECTIONS`
 *
 * The thesis, stated so the layout can be argued with rather than admired: this screen is a
 * statement sheet, not a set of cards. Someone opening it wants to know what HAPPENED — did the
 * salary land, did that charge go through — so the entries are the page and everything else is
 * positioned relative to them.
 *
 * Three consequences, all structural:
 *
 * 1. **The balance is the top of the balance column, not a hero card.** It sits right-aligned to
 *    the same edge as every `balanceAfter` figure below it and as the month's net at the foot, so
 *    one vertical line runs from "where you ended up" down through the running balances to the
 *    month's contribution. A card would break that line, which is the only thing that makes the
 *    number read as a RESULT of the list rather than as a fact beside it.
 * 2. **The month summary is the ledger's column totals**, in a real `<tfoot>` under an accounting
 *    double rule. `totalExpenses` totals the "Paid out" column, `totalIncome` totals "Paid in",
 *    `netChange` totals the balance column. Parked in a rail those three numbers are trivia; under
 *    their own columns they are arithmetic the reader can follow.
 * 3. **There is no rail, at any width.** See the 1024–1094 note on `sheet` below.
 *
 * The data's SHAPE decided the columns, not its values: `TransactionResponse` carries
 * `balanceAfter`, which is what makes a running-balance column possible at all, and `status`, which
 * is what makes a Pending entry readable as in-flight rather than as money already gone.
 */

/** The ledger shows the five most recent entries. Query and skeleton share the constant. */
const RECENT_PAGE_SIZE = 5;

/** The money dialogs still take this minimal shape (their RHF rewrite is a separate PR). */
interface LegacyDialogAccount {
  id: string;
  name: string;
  accountNumber: string;
  balance: number;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  // The shell paints the canvas and, at >= lg, the 32px gutter. Below that the shell's mobile
  // content box has no padding, so the page supplies its own — and stops supplying it exactly
  // where the shell takes over, rather than double-padding at desktop.
  page: {
    display: 'flex',
    flexDirection: 'column',
    padding: '16px',
    [atMedia.lg]: {
      padding: 0,
    },
  },

  /**
   * The sheet. ONE box, full width, at every viewport — which is the whole answer to the defect
   * this phase inherits.
   *
   * The old layout put a 399px min-content column beside a `flexShrink: 0` 360px rail and turned
   * that on at 1024px, where the content box is 705px: 399 + 32 + 360 = 791, so 86px hung outside.
   * A ledger has no rail to place, so the failing band has nothing to fail with. What replaces the
   * two columns is a fixed-layout table whose five tracks are PERCENTAGES of the sheet (16/32/17/
   * 17/18), so the table's used width is the container's width by construction — at 705px the
   * tracks are 112.8 / 225.6 / 119.9 / 119.9 / 126.9px — and the one elastic cell (the entry
   * description) is `nowrap` + `ellipsis`, so it has no min-content floor to push with. The
   * required width of this layout is zero at every viewport; there is nothing that can overflow.
   */
  sheet: {
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${surfaces.border}`,
    boxShadow: shadows.sm,

    /**
     * ONE gutter, inherited by every band and every edge cell, so the balance figure's right edge
     * and the running-balance column's right edge cannot drift apart. Declared as a custom
     * property rather than repeated per class for a measured reason: Griffel emits `md` after `lg`
     * in the media bucket, so a class that sets the same property in BOTH blocks resolves to the
     * `md` value at desktop. The first cut of this file did exactly that and the figure landed 8px
     * inside the column it is supposed to head. A variable is overridden once, base -> lg, which is
     * the one ordering that is not ambiguous.
     */
    '--ledger-gutter': '16px',
    [atMedia.lg]: {
      '--ledger-gutter': '24px',
    },
  },

  // ========== MASTHEAD ==========
  // Title and actions go side by side at `lg` and not at `md`, which is one breakpoint later than
  // it first looked. `lg` is where the shell grows its 240px sidebar; below it the shell is the
  // touch one, and squeezing the buttons in beside the title at 640 cost the title its line ("Good
  // afternoon, / Demo" over two, the subtitle over three) to save vertical space a phone has more
  // of than horizontal. The breakpoint that matters here is the shell's, not a number of its own.
  masthead: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '20px var(--ledger-gutter)',
    borderBottom: `1px solid ${surfaces.border}`,
    [atMedia.lg]: {
      flexDirection: 'row',
      alignItems: 'flex-end',
      justifyContent: 'space-between',
      gap: '24px',
    },
  },

  identity: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
  },

  greeting: {
    fontSize: '14px',
    lineHeight: '20px',
    fontWeight: 500,
    color: colors.neutral[500],
  },

  title: {
    display: 'block',
    margin: 0,
    fontSize: '26px',
    lineHeight: '32px',
    fontWeight: 700,
    letterSpacing: '-0.02em',
    color: colors.neutral[900],
    [atMedia.lg]: {
      fontSize: '30px',
      lineHeight: '36px',
    },
  },

  subtitle: {
    fontSize: '13px',
    lineHeight: '18px',
    color: colors.neutral[500],
  },

  // Transfer outranks the other two by size, fill and order — not by a highlight on one of three
  // equal tiles. Three equal tiles rank three actions equally, whatever colour one of them is.
  actions: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    [atMedia.lg]: {
      flexDirection: 'row',
      alignItems: 'center',
      flexShrink: 0,
    },
  },

  transferButton: {
    width: '100%',
    [atMedia.lg]: {
      width: 'auto',
    },
  },

  minorActions: {
    display: 'flex',
    gap: '8px',
    '> *': {
      flex: 1,
    },
    [atMedia.lg]: {
      '> *': {
        flex: '0 0 auto',
      },
    },
  },

  // ========== BALANCE CARRIED FORWARD ==========
  // Right padding here is the same 16/24px the balance CELL carries, which is what makes the
  // figure's right edge land on the running-balance column's right edge. Alignment by shared
  // gutter rather than by a shared grid: no second source of truth to drift.
  carried: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '18px var(--ledger-gutter)',
    backgroundColor: colors.neutral[50],
    borderBottom: `1px solid ${surfaces.border}`,
    [atMedia.md]: {
      flexDirection: 'row',
      alignItems: 'flex-end',
      justifyContent: 'space-between',
      gap: '24px',
    },
  },

  carriedFigure: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
    [atMedia.md]: {
      alignItems: 'flex-end',
      textAlign: 'right',
      flexShrink: 0,
    },
  },

  microCaps: {
    fontSize: '11px',
    lineHeight: '16px',
    fontWeight: 600,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
    color: colors.neutral[500],
  },

  // Explicit line-height, so the skeleton that stands in for it can be exactly as tall.
  figure: {
    fontSize: '32px',
    lineHeight: '38px',
    fontWeight: 700,
    letterSpacing: '-0.02em',
    fontVariantNumeric: 'tabular-nums',
    color: colors.neutral[900],
    maxWidth: '100%',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    [atMedia.lg]: {
      fontSize: '42px',
      lineHeight: '50px',
    },
  },

  figureSkeleton: {
    height: '38px',
    width: '190px',
    [atMedia.lg]: {
      height: '50px',
      width: '250px',
    },
  },

  // One line, ALWAYS — a skeleton while the summary is in flight, "Nothing pending" when there is
  // nothing to say. An in-flight count that appears out of nowhere when the summary lands would
  // push the whole table down by 18px, which is the shift a skeleton exists to prevent.
  pendingNote: {
    fontSize: '12px',
    lineHeight: '16px',
    color: colors.semantic.warning.dark,
    whiteSpace: 'nowrap',
  },

  pendingNoteQuiet: {
    color: colors.neutral[500],
  },

  pendingSkeleton: {
    height: '16px',
    width: '150px',
  },

  // The accounts the total is made of. A line of text, not two more cards: on a ledger the split
  // is provenance for the top line, and provenance belongs in small type.
  // Two lines of floor. The one number this page cannot know before the response is HOW MANY
  // accounts there are, so the skeleton stands in for the common one-or-two and the floor keeps
  // both of those from moving. Three or more still grows by a line — stated rather than hidden.
  accountsStrip: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    minWidth: 0,
    minHeight: '40px',
    [atMedia.md]: {
      order: -1,
    },
  },

  // `center`, not `baseline`, and the three flex rows in this file that mix type sizes all say so
  // for the same measured reason: baseline alignment makes the line box taller than its own
  // line-height (13px text against a 12px number resolved to 19px, not 18), and a skeleton written
  // from the declared line-height is then 1px short per row. Centring keeps the box equal to what
  // the stylesheet says it is.
  // Exactly one line, at 320 as at 1920: the NAME is the elastic part and it truncates, because
  // the masked number and the balance are the parts a reader is checking. Left to wrap, the first
  // account took a second line at 320 and the whole table moved down 18px when the response
  // landed — the skeleton was right and the content was the thing that changed shape.
  accountEntry: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    fontSize: '13px',
    lineHeight: '18px',
    color: colors.neutral[600],
    whiteSpace: 'nowrap',
  },

  accountName: {
    fontWeight: 600,
    color: colors.neutral[700],
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },

  accountNumber: {
    fontFamily: 'Consolas, monospace',
    fontSize: '12px',
    color: colors.neutral[500],
    flexShrink: 0,
  },

  accountBalance: {
    fontVariantNumeric: 'tabular-nums',
    flexShrink: 0,
  },

  accountsSkeletonShort: {
    height: '18px',
    width: '170px',
  },

  accountsSkeleton: {
    height: '18px',
    width: '220px',
  },

  emptyAccounts: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '8px',
  },

  emptyText: {
    fontSize: '14px',
    lineHeight: '20px',
    color: colors.neutral[500],
  },

  errorSlot: {
    padding: '16px var(--ledger-gutter)',
    borderBottom: `1px solid ${surfaces.border}`,
  },

  // ========== THE LEDGER TABLE ==========
  // Below md the table is not a table: rows become two-line blocks and the column headers, which
  // describe columns that no longer exist, are removed rather than repeated per row.
  table: {
    display: 'block',
    width: '100%',
    borderCollapse: 'collapse',
    [atMedia.md]: {
      display: 'table',
      tableLayout: 'fixed',
    },
  },

  head: {
    display: 'none',
    [atMedia.md]: {
      display: 'table-header-group',
    },
  },

  body: {
    display: 'block',
    [atMedia.md]: {
      display: 'table-row-group',
    },
  },

  foot: {
    display: 'block',
    [atMedia.md]: {
      display: 'table-footer-group',
    },
  },

  headCell: {
    padding: '10px 8px',
    fontSize: '11px',
    lineHeight: '16px',
    fontWeight: 600,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
    textAlign: 'left',
    color: colors.neutral[500],
    borderBottom: `1px solid ${colors.neutral[300]}`,
    whiteSpace: 'nowrap',
  },

  headCellRight: {
    textAlign: 'right',
  },

  colWhen: {
    width: '16%',
    paddingLeft: 'var(--ledger-gutter)',
  },
  colEntry: { width: '32%' },
  colOut: { width: '17%' },
  colIn: { width: '17%' },
  colBalance: {
    width: '18%',
    paddingRight: 'var(--ledger-gutter)',
  },

  /**
   * An entry. Below md a two-line block; at md and up a table row.
   *
   * The two-line block places the four data cells into a 2x2 area map — the two money cells share
   * one area because an entry is only ever paid in OR out, so the empty one contributes nothing.
   * The row's height is therefore constant whether or not the date label is present, which is what
   * lets the skeleton reuse these exact classes and be exactly as tall.
   */
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gridTemplateAreas: `"entry amount" "when balance"`,
    gap: '4px 12px',
    padding: '12px var(--ledger-gutter)',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    [atMedia.md]: {
      display: 'table-row',
      padding: 0,
    },
  },

  rowClickable: {
    cursor: 'pointer',
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  /**
   * A new day gets a heavier rule and the only date label in the group — the way a ruled ledger
   * marks a day, rather than a full-width heading band. The band is the tempting version and it is
   * why this is not one: the number of days inside five entries is not knowable before the
   * response, so a skeleton could never place the right number of bands and every load would jump.
   *
   * `marginTop: -1px` is what keeps even this rule free: below md the day row's border would
   * otherwise stack on the previous row's, adding a pixel per day — three days, three pixels of
   * shift between the skeleton and the data. Pulling the row up by exactly the border it gained
   * lands the two rules on the same line. At md and up `border-collapse` already does it.
   */
  rowDayStart: {
    borderTop: `1px solid ${colors.neutral[300]}`,
    marginTop: '-1px',
  },

  cell: {
    padding: 0,
    [atMedia.md]: {
      display: 'table-cell',
      verticalAlign: 'top',
      padding: '12px 8px',
    },
  },

  cellWhen: {
    gridArea: 'when',
    display: 'flex',
    flexDirection: 'row',
    alignItems: 'center',
    gap: '6px',
    [atMedia.md]: {
      display: 'table-cell',
      paddingLeft: 'var(--ledger-gutter)',
    },
  },

  whenDate: {
    fontSize: '12px',
    lineHeight: '20px',
    fontWeight: 600,
    color: colors.neutral[700],
    whiteSpace: 'nowrap',
    [atMedia.md]: {
      display: 'block',
      lineHeight: '16px',
    },
  },

  whenTime: {
    fontSize: '13px',
    lineHeight: '20px',
    color: colors.neutral[500],
    fontVariantNumeric: 'tabular-nums',
    whiteSpace: 'nowrap',
    [atMedia.md]: {
      display: 'block',
    },
  },

  cellEntry: {
    gridArea: 'entry',
    minWidth: 0,
  },

  entryLink: {
    display: 'block',
    fontSize: '15px',
    lineHeight: '20px',
    fontWeight: 500,
    color: colors.neutral[800],
    textDecoration: 'none',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    ':hover': {
      textDecoration: 'underline',
    },
    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '2px',
    },
  },

  entryMeta: {
    display: 'block',
    marginTop: '2px',
    fontSize: '12px',
    lineHeight: '16px',
    color: colors.neutral[500],
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  statusMarker: {
    fontWeight: 600,
    color: colors.semantic.warning.dark,
  },

  statusMarkerBad: {
    color: colors.semantic.error.dark,
  },

  money: {
    fontSize: '14px',
    lineHeight: '20px',
    fontWeight: 600,
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    gridArea: 'amount',
    [atMedia.md]: {
      display: 'table-cell',
    },
  },

  moneyOut: {
    color: colors.neutral[800],
  },

  moneyIn: {
    color: colors.semantic.success.dark,
  },

  // Below md the two money columns collapse into one position, so the sign is the only thing
  // carrying direction and it has to be there. At md and up the COLUMN carries it and a sign
  // would be the same fact printed twice.
  sign: {
    [atMedia.md]: {
      display: 'none',
    },
  },

  cellBalance: {
    gridArea: 'balance',
    fontSize: '13px',
    lineHeight: '20px',
    color: colors.neutral[500],
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    [atMedia.md]: {
      display: 'table-cell',
      fontSize: '14px',
      color: colors.neutral[700],
      paddingRight: 'var(--ledger-gutter)',
    },
  },

  // ========== SECTIONAL STATES INSIDE THE BODY ==========
  // The sheet keeps its shape through every state: only the rows between the column headings and
  // the totals change, so a failed feed does not take the month's arithmetic down with it.
  stateRow: {
    display: 'block',
    [atMedia.md]: {
      display: 'table-row',
    },
  },

  stateCell: {
    display: 'block',
    padding: '16px var(--ledger-gutter)',
    [atMedia.md]: {
      display: 'table-cell',
    },
  },

  // ========== TOTALS (tfoot) ==========
  footRow: {
    display: 'block',
    padding: '12px var(--ledger-gutter)',
    borderTop: `3px double ${colors.neutral[300]}`,
    backgroundColor: colors.neutral[50],
    [atMedia.md]: {
      display: 'table-row',
      padding: 0,
    },
  },

  footCell: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: '12px',
    padding: '3px 0',
    fontSize: '14px',
    lineHeight: '20px',
    fontWeight: 700,
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
    color: colors.neutral[800],
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    [atMedia.md]: {
      display: 'table-cell',
      padding: '14px 8px',
      borderTop: `3px double ${colors.neutral[300]}`,
      backgroundColor: colors.neutral[50],
    },
  },

  footLabel: {
    justifyContent: 'flex-start',
    fontSize: '11px',
    lineHeight: '16px',
    fontWeight: 600,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
    color: colors.neutral[600],
    textAlign: 'left',
    paddingBottom: '6px',
    [atMedia.md]: {
      paddingLeft: 'var(--ledger-gutter)',
      paddingBottom: '14px',
      verticalAlign: 'middle',
    },
  },

  footBalanceCell: {
    [atMedia.md]: {
      paddingRight: 'var(--ledger-gutter)',
    },
  },

  // At md the column heading names the figure; below it there are no columns, so each total
  // carries its own label.
  footMicroLabel: {
    fontSize: '11px',
    fontWeight: 600,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
    color: colors.neutral[500],
    [atMedia.md]: {
      display: 'none',
    },
  },

  netPositive: {
    color: colors.semantic.success.dark,
  },

  netNegative: {
    color: colors.semantic.error.dark,
  },

  footError: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    fontSize: '13px',
    fontWeight: 400,
    color: colors.semantic.error.dark,
    whiteSpace: 'normal',
  },

  // ========== SHEET FOOT ==========
  sheetFoot: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '14px var(--ledger-gutter)',
    borderTop: `1px solid ${surfaces.border}`,
    [atMedia.md]: {
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'space-between',
      gap: '16px',
    },
  },

  footNote: {
    fontSize: '12px',
    lineHeight: '18px',
    color: colors.neutral[500],
  },

  fullLedgerLink: {
    fontSize: '14px',
    fontWeight: 600,
    color: colors.brand[60],
    textDecoration: 'none',
    whiteSpace: 'nowrap',
    ':hover': {
      textDecoration: 'underline',
    },
  },

  // ========== SKELETONS ==========
  // Every one of these reuses the class of the thing it replaces, so its height is not measured
  // and copied — it is the same declaration. The only numbers here are line-heights.
  skeletonWhen: { height: '20px', width: '72px' },
  skeletonEntry: { height: '20px', width: '65%' },
  skeletonEntryMeta: { height: '16px', width: '35%', marginTop: '2px' },
  skeletonMoney: { height: '20px', width: '76px', marginLeft: 'auto' },
  skeletonBalance: { height: '20px', width: '84px', marginLeft: 'auto' },
  // `alignSelf` matters here and nowhere else: below md the totals row is a flex line with
  // `align-items: baseline`, and a bare box has no baseline, so it aligns its BOTTOM edge to the
  // label's baseline and adds the label's descender to the line — 5px per total, 15px of shift
  // when the numbers arrive. Centring it takes it out of baseline alignment entirely.
  skeletonTotal: { height: '20px', width: '80px', marginLeft: 'auto', alignSelf: 'center' },

  srOnly: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    padding: 0,
    margin: '-1px',
    overflow: 'hidden',
    clipPath: 'inset(50%)',
    whiteSpace: 'nowrap',
  },
});

// ============================================
// HELPERS
// ============================================

function getGreeting(): string {
  const hour = new Date().getHours();
  if (hour < 12) return 'Good morning';
  if (hour < 18) return 'Good afternoon';
  return 'Good evening';
}

/** The day rule's label: only the first entry of a day carries one. */
function dayLabel(iso: string): string {
  const date = new Date(iso);
  if (isToday(date)) return 'Today';
  if (isYesterday(date)) return 'Yesterday';
  return format(date, 'd MMM');
}

function typeLabel(type: TransactionType): string {
  switch (type) {
    case 'Deposit':
      return 'Deposit';
    case 'Withdrawal':
      return 'Withdrawal';
    case 'TransferIn':
      return 'Transfer in';
    default:
      return 'Transfer out';
  }
}

/** Transfers lead with the counterparty handle; everything else with its description. */
function entryLabel(transaction: TransactionResponse): string {
  if (transaction.recipientAzureTag) return `To @${transaction.recipientAzureTag}`;
  if (transaction.senderAzureTag) return `From @${transaction.senderAzureTag}`;
  return transaction.description || typeLabel(transaction.type);
}

// ============================================
// COMPONENT
// ============================================

export function DirectionC() {
  const styles = useStyles();
  const navigate = useNavigate();
  const user = useAppSelector(selectCurrentUser);
  const reducedMotion = useReducedMotion();
  const skeletonAnimation = reducedMotion ? 'pulse' : 'wave';

  const {
    data: accounts = [],
    isLoading: accountsLoading,
    error: accountsError,
    refetch: refetchAccounts,
  } = useGetAccountsQuery();
  const accountsProblem = accountsError as ApiProblem | undefined;

  const {
    data: recent,
    isLoading: recentLoading,
    error: recentError,
    refetch: refetchRecent,
  } = useGetTransactionsQuery({ page: 1, pageSize: RECENT_PAGE_SIZE });
  const showRecentSkeleton = useDelayedFlag(recentLoading);

  // Month start computed ONCE per mount; toDate deliberately omitted so the server's "now"
  // includes the very mutation that triggered the refetch (same reasoning as DashboardPage).
  const [monthWindow] = useState(() => ({
    fromDate: startOfMonth(new Date()).toISOString(),
  }));
  const {
    data: summary,
    isLoading: summaryLoading,
    error: summaryError,
    refetch: refetchSummary,
  } = useGetTransactionSummaryQuery(monthWindow);

  const [isDepositOpen, setIsDepositOpen] = useState(false);
  const [isWithdrawOpen, setIsWithdrawOpen] = useState(false);

  const hasAccounts = accounts.length > 0;
  const totalBalance = accounts.reduce((sum, account) => sum + account.balance, 0);
  const toLegacy = (account: AccountResponse): LegacyDialogAccount => ({
    id: account.id,
    name: account.name,
    accountNumber: maskAccountNumber(account.accountNumber),
    balance: account.balance,
  });
  const legacyAccounts = accounts.map(toLegacy);

  const monthLabel = format(new Date(), 'MMMM');
  const entries = recent?.data ?? [];
  const summaryPending = summaryLoading || !summary;

  // The row is a convenience target for a pointer; the description link inside it is the real
  // control, and the one the keyboard uses. Clicks that land on the link are left to the link.
  const handleRowClick = (event: MouseEvent<HTMLTableRowElement>, id: string) => {
    if ((event.target as HTMLElement).closest('a')) return;
    navigate(`/transactions/${id}`);
  };

  return (
    <div className={styles.page}>
      <section className={styles.sheet}>
        <header className={styles.masthead}>
          <div className={styles.identity}>
            <Text className={styles.greeting}>
              {getGreeting()}
              {user ? `, ${user.firstName}` : ''}
            </Text>
            <Text as="h1" className={styles.title}>
              Your ledger
            </Text>
            <Text className={styles.subtitle}>Newest first, totals at the foot</Text>
          </div>

          <div className={styles.actions}>
            <Button
              appearance="primary"
              size="large"
              icon={<ArrowSwap20Regular />}
              className={styles.transferButton}
              onClick={() => navigate('/transfer')}
            >
              Transfer money
            </Button>
            <div className={styles.minorActions}>
              <Button
                icon={<ArrowDownload20Regular />}
                disabled={!hasAccounts}
                onClick={() => setIsDepositOpen(true)}
              >
                Deposit
              </Button>
              <Button
                icon={<ArrowUpload20Regular />}
                disabled={!hasAccounts}
                onClick={() => setIsWithdrawOpen(true)}
              >
                Withdraw
              </Button>
            </div>
          </div>
        </header>

        {accountsProblem ? (
          <div className={styles.errorSlot}>
            <MessageBar intent="error">
              <MessageBarBody>
                {accountsProblem.detail || 'Could not load your accounts.'}
                {accountsProblem.traceId ? ` Support code: ${accountsProblem.traceId}` : ''}
              </MessageBarBody>
              <MessageBarActions>
                <Button appearance="transparent" onClick={() => void refetchAccounts()}>
                  Retry
                </Button>
              </MessageBarActions>
            </MessageBar>
          </div>
        ) : (
          <div className={styles.carried}>
            <div className={styles.carriedFigure}>
              <Text className={styles.microCaps}>Balance carried forward</Text>
              {accountsLoading ? (
                <SkeletonItem animation={skeletonAnimation} className={styles.figureSkeleton} />
              ) : (
                <Text className={styles.figure}>{formatCurrency(totalBalance)}</Text>
              )}
              {summaryError !== undefined ? (
                <Text className={mergeClasses(styles.pendingNote, styles.pendingNoteQuiet)}>
                  Pending entries unknown
                </Text>
              ) : summaryPending ? (
                <SkeletonItem animation={skeletonAnimation} className={styles.pendingSkeleton} />
              ) : summary.pendingCount > 0 ? (
                <Text className={styles.pendingNote}>
                  {summary.pendingCount} pending {summary.pendingCount === 1 ? 'entry' : 'entries'},
                  not counted
                </Text>
              ) : (
                <Text className={mergeClasses(styles.pendingNote, styles.pendingNoteQuiet)}>
                  Nothing pending
                </Text>
              )}
            </div>

            {accountsLoading ? (
              <div className={styles.accountsStrip}>
                <SkeletonItem animation={skeletonAnimation} className={styles.accountsSkeleton} />
                <SkeletonItem
                  animation={skeletonAnimation}
                  className={styles.accountsSkeletonShort}
                />
              </div>
            ) : hasAccounts ? (
              <div className={styles.accountsStrip}>
                {accounts.map((account) => (
                  <span key={account.id} className={styles.accountEntry}>
                    <span className={styles.accountName}>{account.name}</span>
                    <span className={styles.accountNumber}>
                      {maskAccountNumber(account.accountNumber)}
                    </span>
                    <span className={styles.accountBalance}>{formatCurrency(account.balance)}</span>
                  </span>
                ))}
              </div>
            ) : (
              <div className={styles.emptyAccounts}>
                <Text className={styles.emptyText}>
                  No accounts yet — a ledger needs somewhere to post to.
                </Text>
                <Button appearance="primary" onClick={() => navigate('/accounts')}>
                  Open your first account
                </Button>
              </div>
            )}
          </div>
        )}

        <table className={styles.table}>
          <caption className={styles.srOnly}>
            Your five most recent entries, newest first, with the balance each one left behind and
            this month&apos;s totals.
          </caption>
          <thead className={styles.head}>
            <tr>
              <th scope="col" className={mergeClasses(styles.headCell, styles.colWhen)}>
                When
              </th>
              <th scope="col" className={mergeClasses(styles.headCell, styles.colEntry)}>
                Entry
              </th>
              <th
                scope="col"
                className={mergeClasses(styles.headCell, styles.headCellRight, styles.colOut)}
              >
                Paid out
              </th>
              <th
                scope="col"
                className={mergeClasses(styles.headCell, styles.headCellRight, styles.colIn)}
              >
                Paid in
              </th>
              <th
                scope="col"
                className={mergeClasses(styles.headCell, styles.headCellRight, styles.colBalance)}
              >
                Balance
              </th>
            </tr>
          </thead>

          <tbody
            className={styles.body}
            aria-busy={recentLoading || undefined}
            aria-label={recentLoading ? 'Loading ledger entries' : undefined}
          >
            {recentLoading &&
              showRecentSkeleton &&
              Array.from({ length: RECENT_PAGE_SIZE }, (_, index) => (
                <tr key={index} className={styles.row}>
                  <td className={mergeClasses(styles.cell, styles.cellWhen)}>
                    <SkeletonItem animation={skeletonAnimation} className={styles.skeletonWhen} />
                  </td>
                  <td className={mergeClasses(styles.cell, styles.cellEntry)}>
                    <SkeletonItem animation={skeletonAnimation} className={styles.skeletonEntry} />
                    <SkeletonItem
                      animation={skeletonAnimation}
                      className={styles.skeletonEntryMeta}
                    />
                  </td>
                  <td className={mergeClasses(styles.cell, styles.money, styles.moneyOut)}>
                    <SkeletonItem animation={skeletonAnimation} className={styles.skeletonMoney} />
                  </td>
                  <td className={mergeClasses(styles.cell, styles.money, styles.moneyIn)} />
                  <td className={mergeClasses(styles.cell, styles.cellBalance)}>
                    <SkeletonItem
                      animation={skeletonAnimation}
                      className={styles.skeletonBalance}
                    />
                  </td>
                </tr>
              ))}

            {!recentLoading && recentError !== undefined && (
              <tr className={styles.stateRow}>
                <td className={styles.stateCell} colSpan={5}>
                  <MessageBar intent="error">
                    <MessageBarBody>Could not load your recent entries.</MessageBarBody>
                    <MessageBarActions>
                      <Button appearance="transparent" onClick={() => void refetchRecent()}>
                        Retry
                      </Button>
                    </MessageBarActions>
                  </MessageBar>
                </td>
              </tr>
            )}

            {!recentLoading && !recentError && entries.length === 0 && (
              <tr className={styles.stateRow}>
                <td className={styles.stateCell} colSpan={5}>
                  <Text className={styles.emptyText}>
                    Nothing posted yet. Your first deposit or transfer opens the ledger.
                  </Text>
                </td>
              </tr>
            )}

            {!recentLoading &&
              !recentError &&
              entries.map((entry, index) => {
                const previous = index > 0 ? entries[index - 1] : undefined;
                const dayStart =
                  !previous || !isSameDay(new Date(previous.createdAt), new Date(entry.createdAt));
                const income = isIncomeType(entry.type);
                const pending = entry.status === 'Pending';
                const failed = entry.status === 'Failed' || entry.status === 'Reversed';

                return (
                  <tr
                    key={entry.id}
                    className={mergeClasses(
                      styles.row,
                      styles.rowClickable,
                      dayStart && styles.rowDayStart,
                    )}
                    onClick={(event) => handleRowClick(event, entry.id)}
                  >
                    <td className={mergeClasses(styles.cell, styles.cellWhen)}>
                      {dayStart && (
                        <span className={styles.whenDate}>{dayLabel(entry.createdAt)}</span>
                      )}
                      <span className={styles.whenTime}>{formatTime(entry.createdAt)}</span>
                    </td>

                    <td className={mergeClasses(styles.cell, styles.cellEntry)}>
                      <Link to={`/transactions/${entry.id}`} className={styles.entryLink}>
                        {entryLabel(entry)}
                      </Link>
                      <span className={styles.entryMeta}>
                        {typeLabel(entry.type)}
                        {(pending || failed) && (
                          <span
                            className={mergeClasses(
                              styles.statusMarker,
                              failed && styles.statusMarkerBad,
                            )}
                          >
                            {' · '}
                            {entry.status}
                          </span>
                        )}
                      </span>
                    </td>

                    <td className={mergeClasses(styles.cell, styles.money, styles.moneyOut)}>
                      {!income && (
                        <>
                          <span className={styles.sign}>-</span>
                          {formatCurrency(entry.amount)}
                        </>
                      )}
                    </td>

                    <td className={mergeClasses(styles.cell, styles.money, styles.moneyIn)}>
                      {income && (
                        <>
                          <span className={styles.sign}>+</span>
                          {formatCurrency(entry.amount)}
                        </>
                      )}
                    </td>

                    <td className={mergeClasses(styles.cell, styles.cellBalance)}>
                      {formatCurrency(entry.balanceAfter)}
                    </td>
                  </tr>
                );
              })}
          </tbody>

          {/* The month summary, as the totals of the columns it is the total of. */}
          <tfoot className={styles.foot}>
            <tr className={styles.footRow}>
              <td className={mergeClasses(styles.footCell, styles.footLabel)} colSpan={2}>
                {monthLabel} so far
              </td>

              {summaryError !== undefined ? (
                <td className={mergeClasses(styles.footCell, styles.footBalanceCell)} colSpan={3}>
                  <span className={styles.footError}>
                    Totals unavailable.
                    <Button
                      appearance="transparent"
                      size="small"
                      onClick={() => void refetchSummary()}
                    >
                      Retry
                    </Button>
                  </span>
                </td>
              ) : (
                <>
                  <td className={styles.footCell}>
                    <span className={styles.footMicroLabel}>Paid out</span>
                    {summaryPending ? (
                      <SkeletonItem
                        animation={skeletonAnimation}
                        className={styles.skeletonTotal}
                      />
                    ) : (
                      <>
                        <span className={styles.sign}>-</span>
                        {formatCurrency(summary.totalExpenses)}
                      </>
                    )}
                  </td>

                  <td className={styles.footCell}>
                    <span className={styles.footMicroLabel}>Paid in</span>
                    {summaryPending ? (
                      <SkeletonItem
                        animation={skeletonAnimation}
                        className={styles.skeletonTotal}
                      />
                    ) : (
                      <>
                        <span className={styles.sign}>+</span>
                        {formatCurrency(summary.totalIncome)}
                      </>
                    )}
                  </td>

                  <td
                    className={mergeClasses(
                      styles.footCell,
                      styles.footBalanceCell,
                      !summaryPending &&
                        (summary.netChange < 0 ? styles.netNegative : styles.netPositive),
                    )}
                  >
                    <span className={styles.footMicroLabel}>Net</span>
                    {summaryPending ? (
                      <SkeletonItem
                        animation={skeletonAnimation}
                        className={styles.skeletonTotal}
                      />
                    ) : (
                      `${summary.netChange < 0 ? '-' : '+'}${formatCurrency(Math.abs(summary.netChange))}`
                    )}
                  </td>
                </>
              )}
            </tr>
          </tfoot>
        </table>

        <div className={styles.sheetFoot}>
          <Text className={styles.footNote}>
            Balance is the account each entry left behind. Totals count completed entries only.
          </Text>
          <Link to="/history" className={styles.fullLedgerLink}>
            See the full ledger
          </Link>
        </div>
      </section>

      {isDepositOpen && (
        <DepositDialog isOpen onClose={() => setIsDepositOpen(false)} accounts={legacyAccounts} />
      )}
      {isWithdrawOpen && (
        <WithdrawDialog isOpen onClose={() => setIsWithdrawOpen(false)} accounts={legacyAccounts} />
      )}
    </div>
  );
}

export default DirectionC;
