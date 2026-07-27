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
  Skeleton,
  SkeletonItem,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowDownload24Regular,
  ArrowRight20Filled,
  ArrowUpload24Regular,
} from '@fluentui/react-icons';
import { format, startOfMonth } from 'date-fns';
import { atMedia } from '../../../theme/breakpoints';
import { colors, gradients, shadows, surfaces, transitions } from '../../../theme/tokens';
import { QuickActionButton } from '../../../components/shared/QuickActionButton';
import { DepositDialog, WithdrawDialog } from '../../../components';
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

/**
 * U3-SCRATCH-DASHBOARD-DIRECTIONS — direction D: **the current dashboard, re-weighted.**
 *
 * A, B and C each fixed the live page's flat hierarchy by deleting something it was right to have:
 * A deleted per-account identity and the rail, B deleted the parallel structure of the three
 * actions, C deleted "what do I do next" entirely. The live page's FOUR zones — balance, actions,
 * activity, the month — are the correct set for a bank home. What is wrong is their weighting and
 * their density. So this direction keeps all four, in that DOM order, and takes one idea from each
 * of the three experiments:
 *
 *  - from **A**: the account chips that are simultaneously the breakdown and the switcher, and the
 *    month-as-one-sentence that replaces a whole rail card with a line of prose;
 *  - from **B**: transfer as a single wide primary target rather than one of three equal tiles, and
 *    the "send again" strip derived from the feed's own transfer rows;
 *  - from **C**: the running-balance column — the balance each entry left behind, which no other
 *    direction has — and the month's totals as a `<tfoot>` under the columns they total.
 *
 * What is DELETED outright: the "Here's an overview of your accounts" subtitle (it says nothing),
 * and the whole "Accounts Overview" card ("Total Accounts: 2" is worthless when the chips above
 * already name every account and its balance). Support survives as one quiet line at the foot.
 *
 * **The 1024–1094px defect.** The live page turns on a two-column layout at 1024px, where the shell
 * hands it 705px (1009 client − 240 sidebar − 64 shell padding); its left column's min-content is
 * 399px and its rail is 360px with `flexShrink: 0`, so it needs 791px and hangs 86px outside. There
 * is no fixed-width box anywhere in this file, at any width. One column from 320 up; a second,
 * narrow column only at `xl` (1366), where the measure is ~1062px and both tracks are `minmax(0,
 * …)` fractions that cannot demand more than their share. Between 1024 and 1365 the page is a
 * single measure — which is also better reading, since 705px split two ways is two cramped columns.
 *
 * **Griffel does not sort its media buckets** — the live document emits them in first-encounter
 * order, so a rule under `atMedia.sm` can sit AFTER one under `atMedia.lg` and win above 1024px.
 * (`AuthLayout.tsx` declares `padding` under both `sm` and `lg`; its `lg` padding has never
 * applied.) Therefore **no property in this file is declared under two different `atMedia` buckets**
 * — at most base plus ONE bucket, since unqualified rules always precede media rules. Where
 * responsiveness can come from intrinsic CSS instead, it does: the `<h1>` is a `clamp()` against a
 * container query unit, and nothing about it is a media query at all.
 */

/** The dashboard shows the five most recent transactions. Skeleton and query share the constant. */
const RECENT_PAGE_SIZE = 5;

/** How many distinct @handles the "send again" strip offers. Three, per the brief. */
const SEND_AGAIN_LIMIT = 3;

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  /**
   * The measure, capped. The desktop shell already pays `24px 32px` and caps itself at 1200px, so
   * this page pads only where the shell does not (its mobile branch) and caps a little tighter, so
   * the ledger never runs to the full width of a 1920px monitor. The cap is unqualified rather than
   * declared at `lg`: below ~1120px it simply never binds, so a media query would buy nothing.
   */
  page: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    width: '100%',
    maxWidth: '1120px',
    padding: '16px',
    [atMedia.lg]: {
      padding: 0,
    },
  },

  /**
   * The four zones. One column everywhere; at `xl` the actions move BESIDE the activity list rather
   * than above it, because at 1366px the measure is ~1062px and stacking leaves the right half of
   * the screen empty while the ledger scrolls. DOM order (balance → actions → activity) is
   * unchanged — the areas do the moving, so reading order and tab order stay put.
   *
   * Both tracks are `minmax(0, …)`: the zero floor is precisely what the live page's `flexShrink: 0`
   * rail lacks, and it is what makes a track's min-content unable to become a width demand.
   */
  grid: {
    display: 'grid',
    gap: '24px',
    gridTemplateColumns: 'minmax(0, 1fr)',
    gridTemplateAreas: '"balance" "actions" "activity"',
    [atMedia.xl]: {
      gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 0.44fr)',
      gridTemplateAreas: '"balance balance" "activity actions"',
    },
  },

  // ========== ZONE 1 — BALANCE ==========
  /**
   * `inline-size` containment makes the numeral's `cqi` sizing resolve against THIS box rather than
   * the viewport, so the shell's 240px sidebar is already subtracted before the number is measured.
   */
  balanceZone: {
    gridArea: 'balance',
    containerType: 'inline-size',
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    minWidth: 0,
  },

  greeting: {
    display: 'block',
    margin: 0,
    fontSize: '15px',
    lineHeight: '20px',
    fontWeight: 400,
    color: colors.neutral[600],
  },

  microCaps: {
    fontSize: '11px',
    lineHeight: '16px',
    fontWeight: 600,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
    color: colors.neutral[500],
  },

  h1: {
    display: 'block',
    margin: 0,
    fontWeight: 700,
  },

  /**
   * ~40px at base, ~64px at `lg` and above — and NOT the 144px direction A proved dominates so hard
   * that everything else falls below the fold. One `clamp()` against a container query unit does the
   * whole ramp with no media query, so it cannot collide with any other bucket.
   *
   * The arithmetic: the container is 288px at a 320px viewport (16px of page padding a side) and
   * 705px in the 1024–1094 band. `1.4rem + 5.8cqi` gives 39.1px at 288 (floored to 40) and 63.3px at
   * 705, then the 4rem ceiling holds it there through 1062px at `xl` and 1120px at 1920.
   *
   * `lineHeight: 1` so the box is exactly `1em` tall — which is what lets the loading skeleton stand
   * in at the same height without knowing the digits yet.
   */
  amountBox: {
    display: 'flex',
    alignItems: 'baseline',
    fontSize: 'clamp(2.5rem, 1.4rem + 5.8cqi, 4rem)',
    lineHeight: 1,
    letterSpacing: '-0.03em',
    color: colors.neutral[900],
    fontVariantNumeric: 'tabular-nums',
  },

  amountSkeleton: {
    display: 'flex',
    alignItems: 'center',
    height: '1em',
    width: '100%',
  },
  // 3.4em is the measured footprint of a mid-size balance at this weight, so the bar is the
  // numeral's own shape rather than an arbitrary rectangle.
  amountSkeletonBar: {
    height: '0.6em',
    width: '3.4em',
  },

  // The "no value" slot keeps the numeral's exact 1em height, so a failed load leaves the page the
  // same shape as a good one. Drawn at half size: an em dash at 64px reads as a redaction bar.
  amountFallback: {
    display: 'flex',
    alignItems: 'center',
    height: '1em',
  },
  amountFallbackMark: {
    fontSize: '0.5em',
    lineHeight: 1,
    color: colors.neutral[300],
  },

  // ---------- account chips: the breakdown AND the switcher (direction A) ----------
  /**
   * The row wraps, and where it wraps is decided by ONE number — the chips' 150px flex basis — so
   * that the placeholder can wrap in exactly the same places.
   *
   * Measured, and this is the defect the measurement caught: chips sized to their own text laid out
   * on two rows at 375px while a skeleton of two 130px bars sat on one, and the whole page below the
   * balance settled 63.6px when the accounts arrived. A shared basis makes the break points a
   * function of the chip COUNT and the container width alone, both of which the skeleton can match:
   * 3 chips fit one row from ~466px of container, two rows at 375, one per row at 320 — loading and
   * loaded alike. `flexGrow: 1` then fills each row, so nothing looks fixed-width.
   *
   * What a placeholder still cannot know is how many accounts there are. It stands in for the common
   * two (an "All accounts" chip plus two account chips); a third account adds a chip and, at some
   * widths, a row. Stated rather than hidden — and a user with exactly ONE account gets no chip row
   * at all, per the brief, which is the one case where the reserved box is wrong by a whole row.
   */
  chipRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '8px',
    marginTop: '6px',
  },

  chip: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    flexGrow: 1,
    flexShrink: 1,
    flexBasis: '150px',
    minWidth: 0,
    maxWidth: '100%',
    padding: '8px 12px',
    borderRadius: '10px',
    border: `1px solid ${surfaces.border}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    textAlign: 'left',
    fontFamily: 'inherit',
    transition: `background-color ${transitions.fast}, border-color ${transitions.fast}`,
    // Griffel types `borderColor` as a shorthand it will not accept, so the hover and selected
    // states restate the whole `border` rather than reaching for a longhand that is not there.
    ':hover': {
      border: `1px solid ${colors.brand[90]}`,
    },
  },

  chipActive: {
    backgroundColor: colors.brand[120],
    border: `1px solid ${colors.brand[70]}`,
  },

  chipHead: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    maxWidth: '100%',
    minWidth: 0,
  },

  chipName: {
    minWidth: 0,
    fontSize: '12px',
    lineHeight: '16px',
    color: colors.neutral[600],
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  chipNameActive: { color: colors.brand[40] },

  chipTail: {
    fontFamily: 'Consolas, monospace',
    fontSize: '12px',
    lineHeight: '16px',
    color: colors.neutral[500],
    flexShrink: 0,
  },
  chipTailActive: { color: colors.brand[50] },

  /**
   * The value line, and the primary marker rides at its right end rather than beside the name.
   * Measured at 375px, where two chips share the row at ~180px each: name + `···90` + a PRIMARY
   * badge on one line left the name about 80px and rendered it "Main A…". The value line has
   * ~110px going spare, so the marker moved there and the name got its width back.
   *
   * `alignItems: 'center'` rather than `baseline`: a baseline-aligned line box comes out taller than
   * its own line-height when it mixes 15px and 10px type, which would make the chip taller than the
   * 54px its placeholder declares.
   */
  chipFoot: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
    width: '100%',
    minWidth: 0,
  },

  chipValue: {
    fontSize: '15px',
    lineHeight: '20px',
    fontWeight: 600,
    color: colors.neutral[800],
    fontVariantNumeric: 'tabular-nums',
  },
  chipValueActive: { color: colors.brand[40] },

  // The primary account is MARKED rather than merely ordered first: order is not a fact a reader
  // can check, and "which one is my main account" is exactly what this chip is being asked.
  chipPrimary: {
    fontSize: '10px',
    lineHeight: '20px',
    fontWeight: 700,
    letterSpacing: '0.06em',
    textTransform: 'uppercase',
    color: colors.brand[60],
    flexShrink: 0,
  },

  // Same 6px top margin, same wrap, same 150px basis as the real `chipRow` — the placeholder's
  // layout is the same declarations rather than a copy of a measurement.
  chipSkeletonRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '8px',
    marginTop: '6px',
  },
  // The placeholder reproduces the chip's BOX MODEL rather than its measured total: 54px of content
  // plus padding, plus the same 1px border. Declaring a flat 56px was right only at a device pixel
  // ratio of 1 — at 1.25 the browser snaps the real chip's two 1px borders to 0.8px each and the
  // chip lands at 55.6, so the page settled by 0.4px on load. A transparent border of its own makes
  // the two boxes round the same way at every ratio.
  chipSkeletonItem: {
    boxSizing: 'content-box',
    flexGrow: 1,
    flexShrink: 1,
    flexBasis: '150px',
    minWidth: 0,
    height: '54px',
    border: '1px solid transparent',
    borderRadius: '10px',
  },

  // ========== ZONE 2 — ACTIONS ==========
  actionsZone: {
    gridArea: 'actions',
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    minWidth: 0,
    alignSelf: 'start',
  },

  /**
   * Transfer is the main verb of a banking app, so it is one wide target spanning the measure and
   * taller than the other two — but sized to its content plus generous padding, not direction B's
   * near-empty 244px slab. 88px at base, 104px from `lg`: one property, one extra bucket.
   */
  transferTarget: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '16px',
    width: '100%',
    minWidth: 0,
    minHeight: '88px',
    padding: '16px 18px',
    border: 'none',
    borderRadius: '14px',
    background: gradients.brand,
    boxShadow: shadows.brand,
    color: tokens.colorNeutralForegroundOnBrand,
    textAlign: 'left',
    fontFamily: 'inherit',
    cursor: 'pointer',
    [atMedia.lg]: {
      minHeight: '104px',
      padding: '20px 24px',
    },
  },

  // Motion is opt-IN: applied only when the reader has not asked the OS for less of it.
  transferMotion: {
    transition: `transform ${transitions.fast}, box-shadow ${transitions.fast}`,
    ':hover': {
      transform: 'translateY(-1px)',
    },
    ':active': {
      transform: 'translateY(0)',
    },
  },

  transferText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '3px',
    minWidth: 0,
  },

  transferTitle: {
    display: 'block',
    fontSize: '22px',
    lineHeight: '28px',
    fontWeight: 700,
    letterSpacing: '-0.01em',
  },

  transferSupport: {
    display: 'block',
    fontSize: '13px',
    lineHeight: '18px',
    // A translucent white would mean an `rgba()` literal; opacity says the same thing about the
    // element and leaves the colour to the theme's on-brand token.
    opacity: 0.85,
  },

  transferDisc: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '40px',
    height: '40px',
    borderRadius: '50%',
    flexShrink: 0,
    backgroundColor: colors.brand[40],
  },

  // Deposit and Withdraw keep the live dashboard's icon tiles — their parallel structure and icon
  // recognisability are worth keeping, and demoting them to text rows (as B did) was a loss.
  tileRow: {
    display: 'flex',
    gap: '10px',
  },

  // ---------- send again (direction B) ----------
  /**
   * ONE line, always — at every width, for any number of chips, in every state. That is the whole
   * design of this strip, and it is what makes its skeleton honest.
   *
   * A wrapping strip's height depends on how many recipients came back and how long their handles
   * are, neither of which a placeholder can know; three chips that wrap to two rows loaded and sit
   * on one row loading is a 40px jump in the middle of the page. So the strip never wraps, and the
   * chips are `flex: 0 1 84px` with `min-width: 0` rather than a fixed width: 84px when there is
   * room (three of them plus two 8px gaps is 268px, inside the 288px a 320px viewport affords), and
   * shrunk with an ellipsis when there is not — the narrowest case being the `xl` actions column at
   * 317px, where a label plus three chips wants 350px and the chips give back 11px each. The
   * skeleton items carry the same flex, so loading and loaded are the same layout by construction.
   */
  sendAgain: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'nowrap',
    gap: '8px',
    minHeight: '32px',
    minWidth: 0,
    overflow: 'hidden',
  },

  // Shown at every width — a lone `@handle` pill with nothing naming it is a control you have to
  // guess at. It costs 74px, which the chips give back by shrinking rather than by wrapping.
  sendAgainLabel: {
    flexShrink: 0,
  },

  sendChip: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    boxSizing: 'border-box',
    flexGrow: 0,
    flexShrink: 1,
    flexBasis: '84px',
    minWidth: 0,
    height: '32px',
    padding: '0 10px',
    borderRadius: '16px',
    border: `1px solid ${colors.brand[110]}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: colors.brand[60],
    fontFamily: 'inherit',
    fontSize: '13px',
    fontWeight: 600,
    cursor: 'pointer',
    ':hover': {
      backgroundColor: colors.brand[120],
    },
  },

  sendChipText: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  sendChipSkeleton: {
    flexGrow: 0,
    flexShrink: 1,
    flexBasis: '84px',
    minWidth: 0,
    height: '32px',
    borderRadius: '16px',
  },

  sendAgainEmpty: {
    minWidth: 0,
    fontSize: '13px',
    lineHeight: '32px',
    color: colors.neutral[500],
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  // ========== ZONE 3 — ACTIVITY ==========
  activityZone: {
    gridArea: 'activity',
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    minWidth: 0,
  },

  activityHead: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '12px',
  },

  activityTitle: {
    margin: 0,
    fontSize: '13px',
    fontWeight: 600,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
    color: colors.neutral[500],
  },

  seeAll: {
    fontSize: '13px',
    fontWeight: 600,
    color: colors.brand[60],
    textDecoration: 'none',
    flexShrink: 0,
    ':hover': { textDecoration: 'underline' },
  },

  // ---------- ZONE 4a — the month as ONE sentence, below lg (direction A) ----------
  /**
   * Two lines of box, reserved in every state. Measured: the sentence is ~430px of type, so it sits
   * on one line from ~640px up and wraps to two on a phone. A skeleton reserving a single 20px line
   * would therefore be right on one class of screen and 20px short on the other.
   *
   * It disappears at `lg`, where the same three numbers reappear as the table's `<tfoot>` — one
   * property (`display`), base plus ONE bucket, so the ordering trap cannot bite.
   */
  monthLine: {
    display: 'block',
    margin: 0,
    minHeight: '40px',
    fontSize: '14px',
    lineHeight: '20px',
    color: colors.neutral[600],
    [atMedia.lg]: {
      display: 'none',
    },
  },

  monthStrong: {
    fontWeight: 600,
    fontVariantNumeric: 'tabular-nums',
  },
  monthPositive: { color: colors.semantic.success.dark },
  monthNegative: { color: colors.semantic.error.dark },

  monthSkeletonRow: {
    display: 'flex',
    alignItems: 'flex-start',
    minHeight: '40px',
    [atMedia.lg]: {
      display: 'none',
    },
  },
  // 4px of offset centres the bar in the first 20px line box rather than against the top of two.
  monthSkeletonBar: {
    height: '12px',
    width: '420px',
    maxWidth: '100%',
    marginTop: '4px',
  },

  monthErrorRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    flexWrap: 'wrap',
    minHeight: '40px',
    [atMedia.lg]: {
      display: 'none',
    },
  },
  monthErrorText: {
    fontSize: '14px',
    lineHeight: '20px',
    color: colors.semantic.error.dark,
  },

  // ---------- the ledger table (direction C's running-balance column) ----------
  /**
   * Below `md` this is not a table: rows are two-line blocks, and the column headings — which
   * describe columns that no longer exist — are removed rather than repeated per row.
   *
   * `table-layout: fixed` over PERCENTAGE tracks is what makes the sheet's used width equal to its
   * container's by construction: at 705px the tracks are 105.75 / 246.75 / 119.85 / 112.8 / 119.85,
   * and the one elastic cell (the entry label) is `nowrap` + `ellipsis`, so it has no min-content
   * floor to push with. The required width of this layout is zero at every viewport.
   */
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

  // The totals appear as a foot only at `lg` and up; below that the one-sentence summary above the
  // list carries the same three numbers.
  foot: {
    display: 'none',
    [atMedia.lg]: {
      display: 'table-footer-group',
    },
  },

  headCell: {
    padding: '8px',
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

  headCellRight: { textAlign: 'right' },

  colWhen: { width: '15%', paddingLeft: 0 },
  colEntry: { width: '35%' },
  colOut: { width: '17%' },
  colIn: { width: '16%' },
  colBalance: { width: '17%', paddingRight: 0 },

  /**
   * An entry. Below `md` a two-line block on a 2×2 area map — the two money cells share one area,
   * because an entry is only ever paid in OR out, so the empty one contributes nothing and the row's
   * height is constant. At `md` and up, a table row.
   */
  row: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gridTemplateAreas: '"entry amount" "when balance"',
    gap: '2px 12px',
    padding: '10px 0',
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

  cell: {
    padding: 0,
    [atMedia.md]: {
      display: 'table-cell',
      verticalAlign: 'top',
      padding: '10px 8px',
    },
  },

  cellWhen: {
    gridArea: 'when',
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    [atMedia.md]: {
      display: 'table-cell',
      paddingLeft: 0,
    },
  },

  // Below `md` the date and time share one line of a two-line block, separated by the cell's 6px
  // flex gap. At `md` the cell is a table cell, where inline spans have no gap to inherit — "20
  // Jul8:30 PM" — so they stack, which is also how a ruled ledger prints a timestamp.
  whenDate: {
    fontSize: '12px',
    lineHeight: '16px',
    fontWeight: 600,
    color: colors.neutral[700],
    whiteSpace: 'nowrap',
    [atMedia.md]: {
      display: 'block',
    },
  },

  whenTime: {
    fontSize: '12px',
    lineHeight: '16px',
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
    ':hover': { textDecoration: 'underline' },
    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '2px',
    },
  },

  entryMeta: {
    display: 'block',
    fontSize: '12px',
    lineHeight: '16px',
    color: colors.neutral[500],
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  // Inline status, as C does: an entry that has not settled must not read as money already gone.
  statusMarker: {
    fontWeight: 600,
    color: colors.semantic.warning.dark,
  },
  statusMarkerBad: {
    color: colors.semantic.error.dark,
  },

  money: {
    gridArea: 'amount',
    fontSize: '14px',
    lineHeight: '20px',
    fontWeight: 600,
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    [atMedia.md]: {
      display: 'table-cell',
    },
  },

  moneyOut: { color: colors.neutral[800] },
  moneyIn: { color: colors.semantic.success.dark },

  // Below md the two money columns collapse into one position, so the sign is the only thing
  // carrying direction and it has to be there. At md and up the COLUMN carries it.
  sign: {
    [atMedia.md]: {
      display: 'none',
    },
  },

  /**
   * The running balance — the balance each entry left behind. Direction C's best idea, and the one
   * thing no other direction has. Hidden below `md`, where a phone genuinely has no room for a
   * fourth number on a row.
   */
  cellBalance: {
    gridArea: 'balance',
    display: 'none',
    [atMedia.md]: {
      display: 'table-cell',
      fontSize: '14px',
      lineHeight: '20px',
      color: colors.neutral[600],
      fontVariantNumeric: 'tabular-nums',
      textAlign: 'right',
      whiteSpace: 'nowrap',
      overflow: 'hidden',
      textOverflow: 'ellipsis',
    },
  },

  // Sectional states live INSIDE the body, so a failed feed does not take the month's arithmetic
  // down with it: the head and the foot keep their shape.
  stateRow: {
    display: 'block',
    [atMedia.md]: {
      display: 'table-row',
    },
  },

  stateCell: {
    display: 'block',
    padding: '14px 0',
    [atMedia.md]: {
      display: 'table-cell',
    },
  },

  emptyText: {
    fontSize: '14px',
    lineHeight: '20px',
    color: colors.neutral[500],
  },

  // ---------- ZONE 4b — the month as column totals, lg and up (direction C) ----------
  footCell: {
    padding: '12px 8px',
    fontSize: '14px',
    lineHeight: '20px',
    fontWeight: 700,
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
    color: colors.neutral[800],
    borderTop: `3px double ${colors.neutral[300]}`,
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },

  footLabel: {
    textAlign: 'left',
    paddingLeft: 0,
    fontSize: '11px',
    lineHeight: '20px',
    fontWeight: 600,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
    color: colors.neutral[600],
  },

  footBalanceCell: { paddingRight: 0 },

  footError: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'flex-end',
    gap: '8px',
    fontSize: '13px',
    fontWeight: 400,
    color: colors.semantic.error.dark,
  },

  netPositive: { color: colors.semantic.success.dark },
  netNegative: { color: colors.semantic.error.dark },

  // ========== SKELETONS ==========
  // Each reuses the class of the thing it replaces, so its height is not measured and copied — the
  // declaration is the same one. The only numbers here are line-heights.
  skeletonWhenDate: { height: '16px', width: '40px' },
  skeletonWhenTime: { height: '16px', width: '52px' },
  skeletonEntry: { height: '20px', width: '62%' },
  skeletonEntryMeta: { height: '16px', width: '34%' },
  skeletonMoney: { height: '20px', width: '74px', marginLeft: 'auto' },
  skeletonBalance: { height: '20px', width: '80px', marginLeft: 'auto' },
  skeletonTotal: { height: '20px', width: '78px', marginLeft: 'auto' },

  /**
   * The placeholder rows exist from the FIRST frame of a load; this is what the anti-flicker delay
   * hides them with until it fires.
   *
   * `visibility: hidden` and not a mount gate, and the difference was measured: rendering the rows
   * only once `useDelayedFlag` fires left `<tbody>` at height 0 for the first 200ms of every load,
   * and the whole page below the feed — the support line, the document's own scroll height — jumped
   * 374px downwards the moment the delay elapsed. A hidden box still occupies its space, so the
   * reservation is now made at mount and the delay only decides whether anything is PAINTED in it.
   * (`hidden`, never `collapse`: on a table row `visibility: collapse` removes the row's height,
   * which is the exact bug this fixes.)
   */
  skeletonVeiled: {
    visibility: 'hidden',
  },

  // ========== FOOT ==========
  // Support stays reachable — as ONE quiet line, not a card. Informational copy only: a "Contact
  // support" button with nothing behind it would be a dead control.
  supportLine: {
    fontSize: '12px',
    lineHeight: '18px',
    color: colors.neutral[500],
  },

  emptyAccounts: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '8px',
  },

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

/** Signed EUR for the month's figures: +€x / -€x (net keeps its real sign). */
function signedCurrency(amount: number, positiveSign: '+' | '-'): string {
  return `${positiveSign}${formatCurrency(Math.abs(amount))}`;
}

/**
 * The chip's short form of an account number — `···90`, derived FROM `maskAccountNumber` rather than
 * from the raw value, so the masking is still the thing that decides what may be shown. The full
 * `AB-••••-••••-90` is ~110px of monospace and would be the widest thing in a chip whose job is to
 * let you tell two accounts apart at a glance; the tail is the part that does that.
 */
function accountTail(accountNumber: string): string {
  const masked = maskAccountNumber(accountNumber);
  const groups = masked.split('-');
  return `···${groups[groups.length - 1]}`;
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

/**
 * The distinct @handles this user most recently sent TO, newest first.
 *
 * Derived from the same five rows the list renders — there is no recent-recipients endpoint, and
 * inventing one for a scratch direction would be arguing with data rather than with layout. It is
 * also the honest reading of "recent": the strip and the list cannot disagree.
 */
function recentRecipients(transactions: TransactionResponse[], limit: number): string[] {
  const tags: string[] = [];
  for (const transaction of transactions) {
    if (transaction.type !== 'TransferOut') continue;
    const tag = transaction.recipientAzureTag;
    if (tag && !tags.includes(tag)) tags.push(tag);
    if (tags.length === limit) break;
  }
  return tags;
}

/** The money dialogs still take this minimal shape (their RHF rewrite is a separate PR). */
interface LegacyDialogAccount {
  id: string;
  name: string;
  accountNumber: string;
  balance: number;
}

const toLegacy = (account: AccountResponse): LegacyDialogAccount => ({
  id: account.id,
  name: account.name,
  accountNumber: maskAccountNumber(account.accountNumber),
  balance: account.balance,
});

// ============================================
// COMPONENT
// ============================================

export function DirectionD() {
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

  // Month start computed ONCE per mount; `toDate` deliberately omitted so the server defaults it to
  // "now" per request and a post-mutation refetch includes the transaction that triggered it.
  const [monthWindow] = useState(() => ({ fromDate: startOfMonth(new Date()).toISOString() }));
  const {
    data: summary,
    isLoading: summaryLoading,
    error: summaryError,
    refetch: refetchSummary,
  } = useGetTransactionSummaryQuery(monthWindow);

  const [isDepositOpen, setIsDepositOpen] = useState(false);
  const [isWithdrawOpen, setIsWithdrawOpen] = useState(false);

  /**
   * Multi-account, answered rather than dodged: the default number is the TOTAL, and the chips below
   * it point the same numeral at a single account. `'all'` is a state, not an id, so a stale id (an
   * account deleted under us) falls back to the total by construction — `find` simply misses.
   */
  const [view, setView] = useState<'all' | string>('all');
  const selectedAccount = accounts.find((account) => account.id === view);
  const totalBalance = accounts.reduce((sum, account) => sum + account.balance, 0);
  const shownBalance = selectedAccount ? selectedAccount.balance : totalBalance;
  const amountText = formatCurrency(shownBalance);

  const headingLabel = selectedAccount
    ? `${selectedAccount.name} available balance`
    : 'Available balance';

  const hasAccounts = !accountsLoading && !accountsProblem && accounts.length > 0;
  const needsFirstAccount = !accountsLoading && !accountsProblem && accounts.length === 0;
  const legacyAccounts = accounts.map(toLegacy);

  const entries = recent?.data ?? [];
  const sendAgain = recentRecipients(entries, SEND_AGAIN_LIMIT);
  const monthLabel = format(new Date(), 'MMMM');
  const summaryPending = summaryLoading || !summary;

  /**
   * The chips start the transfer flow and nothing more. **Verified rather than assumed:**
   * `TransferPage.tsx` imports `useNavigate` only — no `useLocation`, no `useSearchParams` — and its
   * RHF `defaultValues` are `{ fromAccountId: '', recipientTag: '', amount: '' }`. Direction B's
   * `navigate('/transfer', { state: { prefillTag } })` therefore lands nowhere: the handle is
   * carried across the navigation and silently dropped. Passing it would be an affordance that
   * pretends to do something, so this passes nothing and says so here. Prefilling is a one-line read
   * in `TransferPage`, in the PR that owns that file.
   */
  const goToTransfer = () => navigate('/transfer');

  // The row is a convenience target for a pointer; the entry link inside it is the real control, and
  // the one the keyboard uses. Clicks that land on the link are left to the link.
  const handleRowClick = (event: MouseEvent<HTMLTableRowElement>, id: string) => {
    if ((event.target as HTMLElement).closest('a')) return;
    navigate(`/transactions/${id}`);
  };

  return (
    <div className={styles.page}>
      <div className={styles.grid}>
        {/* ===== ZONE 1 — the balance ===== */}
        <section className={styles.balanceZone}>
          <Text as="p" className={styles.greeting}>
            {getGreeting()}
            {user ? `, ${user.firstName}` : ''}
          </Text>

          {/* "Available", not "Total": in a bank those differ by pending holds, and the live
              dashboard is right to say so. */}
          <Text as="p" className={styles.microCaps}>
            Available balance
          </Text>

          <Text as="h1" className={styles.h1}>
            {/* The heading's accessible name. `aria-live` because pressing a chip changes THIS
                heading and nothing else — a sighted reader sees the number swap, and this is how
                everyone else hears it. */}
            <span className={styles.srOnly} aria-live="polite">
              {accountsLoading
                ? 'Loading your balance'
                : accountsProblem
                  ? `${headingLabel} unavailable`
                  : `${headingLabel}: ${amountText}`}
            </span>

            <span className={styles.amountBox} aria-hidden="true">
              {accountsLoading ? (
                <Skeleton animation={skeletonAnimation} className={styles.amountSkeleton}>
                  <SkeletonItem className={styles.amountSkeletonBar} />
                </Skeleton>
              ) : accountsProblem ? (
                <span className={styles.amountFallback}>
                  <span className={styles.amountFallbackMark}>&mdash;</span>
                </span>
              ) : (
                amountText
              )}
            </span>
          </Text>

          {accountsLoading && (
            <Skeleton
              animation={skeletonAnimation}
              className={styles.chipSkeletonRow}
              aria-label="Loading your accounts"
            >
              {/* Three: an "All accounts" chip plus the two accounts a typical holder has. */}
              <SkeletonItem className={styles.chipSkeletonItem} />
              <SkeletonItem className={styles.chipSkeletonItem} />
              <SkeletonItem className={styles.chipSkeletonItem} />
            </Skeleton>
          )}

          {accountsProblem && (
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
          )}

          {/* The breakdown IS the switcher — one element instead of a caption plus a control. With
              exactly one account there is nothing to switch between and nothing to break down, so
              there are no chips at all. */}
          {hasAccounts && accounts.length > 1 && (
            <div className={styles.chipRow} role="group" aria-label="Choose which balance to show">
              <button
                type="button"
                className={mergeClasses(styles.chip, view === 'all' && styles.chipActive)}
                aria-pressed={view === 'all'}
                onClick={() => setView('all')}
              >
                <span className={styles.chipHead}>
                  <span
                    className={mergeClasses(
                      styles.chipName,
                      view === 'all' && styles.chipNameActive,
                    )}
                  >
                    All accounts
                  </span>
                </span>
                <span className={styles.chipFoot}>
                  <span
                    className={mergeClasses(
                      styles.chipValue,
                      view === 'all' && styles.chipValueActive,
                    )}
                  >
                    {formatCurrency(totalBalance)}
                  </span>
                </span>
              </button>

              {accounts.map((account) => {
                const active = view === account.id;
                return (
                  <button
                    key={account.id}
                    type="button"
                    className={mergeClasses(styles.chip, active && styles.chipActive)}
                    aria-pressed={active}
                    onClick={() => setView(account.id)}
                  >
                    <span className={styles.chipHead}>
                      <span
                        className={mergeClasses(styles.chipName, active && styles.chipNameActive)}
                      >
                        {account.name}
                      </span>
                      <span
                        className={mergeClasses(styles.chipTail, active && styles.chipTailActive)}
                      >
                        {accountTail(account.accountNumber)}
                      </span>
                    </span>
                    <span className={styles.chipFoot}>
                      <span
                        className={mergeClasses(styles.chipValue, active && styles.chipValueActive)}
                      >
                        {formatCurrency(account.balance)}
                      </span>
                      {account.isPrimary && <span className={styles.chipPrimary}>Primary</span>}
                    </span>
                  </button>
                );
              })}
            </div>
          )}
        </section>

        {/* ===== ZONE 2 — the actions ===== */}
        <section className={styles.actionsZone} aria-label="Move money">
          {needsFirstAccount ? (
            <div className={styles.emptyAccounts}>
              <Text className={styles.emptyText}>
                You have no accounts yet, so there is nothing to move.
              </Text>
              <Button appearance="primary" size="large" onClick={() => navigate('/accounts')}>
                Open your first account
              </Button>
            </div>
          ) : (
            <>
              <button
                type="button"
                className={mergeClasses(
                  styles.transferTarget,
                  !reducedMotion && styles.transferMotion,
                )}
                onClick={goToTransfer}
              >
                <span className={styles.transferText}>
                  <Text as="span" className={styles.transferTitle}>
                    Transfer
                  </Text>
                  <Text as="span" className={styles.transferSupport}>
                    Send to any @handle, or between your own accounts.
                  </Text>
                </span>
                <span className={styles.transferDisc}>
                  <ArrowRight20Filled />
                </span>
              </button>

              <div className={styles.tileRow}>
                <QuickActionButton
                  variant="deposit"
                  label="Deposit"
                  icon={<ArrowDownload24Regular />}
                  disabled={!hasAccounts}
                  onClick={() => setIsDepositOpen(true)}
                />
                <QuickActionButton
                  variant="withdraw"
                  label="Withdraw"
                  icon={<ArrowUpload24Regular />}
                  disabled={!hasAccounts}
                  onClick={() => setIsWithdrawOpen(true)}
                />
              </div>

              <div className={styles.sendAgain}>
                <Text className={mergeClasses(styles.microCaps, styles.sendAgainLabel)}>
                  Send again
                </Text>

                {recentLoading && (
                  // Same posture as the feed's placeholder rows: mounted from the first frame so
                  // the strip's inner layout is settled, veiled until the anti-flicker delay fires.
                  <Skeleton
                    animation={skeletonAnimation}
                    className={mergeClasses(
                      styles.sendAgain,
                      !showRecentSkeleton && styles.skeletonVeiled,
                    )}
                    aria-label="Loading recent recipients"
                  >
                    <SkeletonItem className={styles.sendChipSkeleton} />
                    <SkeletonItem className={styles.sendChipSkeleton} />
                    <SkeletonItem className={styles.sendChipSkeleton} />
                  </Skeleton>
                )}

                {!recentLoading &&
                  sendAgain.map((tag) => (
                    <button
                      key={tag}
                      type="button"
                      className={styles.sendChip}
                      // The label says what the control actually does. It opens the transfer form;
                      // it does not fill the recipient in (see `goToTransfer`).
                      aria-label={`Open a transfer, last sent to @${tag}`}
                      onClick={goToTransfer}
                    >
                      <span className={styles.sendChipText}>@{tag}</span>
                    </button>
                  ))}

                {!recentLoading && sendAgain.length === 0 && (
                  <Text className={styles.sendAgainEmpty}>No recent recipients yet.</Text>
                )}
              </div>
            </>
          )}
        </section>

        {/* ===== ZONE 3 — the activity, with ZONE 4 attached to it both ways ===== */}
        <section className={styles.activityZone}>
          <div className={styles.activityHead}>
            <Text as="h2" className={styles.activityTitle}>
              Recent activity
            </Text>
            <Link to="/history" className={styles.seeAll}>
              See all
            </Link>
          </div>

          {/* ZONE 4a — below lg, the month is ONE sentence above the list. */}
          {summaryError !== undefined ? (
            <div className={styles.monthErrorRow}>
              <Text className={styles.monthErrorText}>
                {monthLabel}&rsquo;s totals could not be loaded.
              </Text>
              <Button appearance="transparent" size="small" onClick={() => void refetchSummary()}>
                Retry
              </Button>
            </div>
          ) : summaryPending ? (
            <Skeleton
              animation={skeletonAnimation}
              className={styles.monthSkeletonRow}
              aria-label={`Loading your ${monthLabel} totals`}
            >
              <SkeletonItem className={styles.monthSkeletonBar} />
            </Skeleton>
          ) : (
            <Text as="p" className={styles.monthLine}>
              {monthLabel} so far:{' '}
              <span className={mergeClasses(styles.monthStrong, styles.monthPositive)}>
                {signedCurrency(summary.totalIncome, '+')}
              </span>{' '}
              in,{' '}
              <span className={mergeClasses(styles.monthStrong, styles.monthNegative)}>
                {signedCurrency(summary.totalExpenses, '-')}
              </span>{' '}
              out — net{' '}
              <span
                className={mergeClasses(
                  styles.monthStrong,
                  summary.netChange < 0 ? styles.monthNegative : styles.monthPositive,
                )}
              >
                {signedCurrency(summary.netChange, summary.netChange < 0 ? '-' : '+')}
              </span>
              {summary.pendingCount > 0 ? `, ${summary.pendingCount} pending` : ''}.
            </Text>
          )}

          {/*
            `TransactionItem` is NOT reused here, and the reason is structural rather than a
            preference: it is a fixed four-part flex row (icon / description+date / amount+type /
            chevron) with no slot for a fifth figure, so the running-balance column cannot be added
            without giving it a new prop — i.e. without editing a shared component that six other
            screens render. The rows below are written here instead, and the skeleton reuses the
            exact same row and cell classes, so its height is the same declaration rather than a
            copied measurement.
          */}
          <table className={styles.table}>
            <caption className={styles.srOnly}>
              Your {RECENT_PAGE_SIZE} most recent entries, newest first, with the balance each one
              left behind and this month&rsquo;s totals.
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
              aria-label={recentLoading ? 'Loading recent activity' : undefined}
            >
              {recentLoading &&
                Array.from({ length: RECENT_PAGE_SIZE }, (_, index) => (
                  <tr
                    key={index}
                    className={mergeClasses(
                      styles.row,
                      !showRecentSkeleton && styles.skeletonVeiled,
                    )}
                  >
                    <td className={mergeClasses(styles.cell, styles.cellWhen)}>
                      <SkeletonItem
                        animation={skeletonAnimation}
                        className={styles.skeletonWhenDate}
                      />
                      <SkeletonItem
                        animation={skeletonAnimation}
                        className={styles.skeletonWhenTime}
                      />
                    </td>
                    <td className={mergeClasses(styles.cell, styles.cellEntry)}>
                      <SkeletonItem
                        animation={skeletonAnimation}
                        className={styles.skeletonEntry}
                      />
                      <SkeletonItem
                        animation={skeletonAnimation}
                        className={styles.skeletonEntryMeta}
                      />
                    </td>
                    <td className={mergeClasses(styles.cell, styles.money, styles.moneyOut)}>
                      <SkeletonItem
                        animation={skeletonAnimation}
                        className={styles.skeletonMoney}
                      />
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
                      <MessageBarBody>Could not load recent transactions.</MessageBarBody>
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
                    <Text className={styles.emptyText}>No transactions yet.</Text>
                  </td>
                </tr>
              )}

              {!recentLoading &&
                !recentError &&
                entries.map((entry) => {
                  const income = isIncomeType(entry.type);
                  const pending = entry.status === 'Pending';
                  const failed = entry.status === 'Failed' || entry.status === 'Reversed';

                  return (
                    <tr
                      key={entry.id}
                      className={mergeClasses(styles.row, styles.rowClickable)}
                      onClick={(event) => handleRowClick(event, entry.id)}
                    >
                      <td className={mergeClasses(styles.cell, styles.cellWhen)}>
                        <span className={styles.whenDate}>
                          {format(new Date(entry.createdAt), 'd MMM')}
                        </span>
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

            {/* ZONE 4b — at lg and up, the same three numbers become the totals of the columns they
                are the total of, under an accounting double rule. */}
            <tfoot className={styles.foot}>
              <tr>
                <td className={mergeClasses(styles.footCell, styles.footLabel)} colSpan={2}>
                  {monthLabel} so far
                  {!summaryPending && summaryError === undefined && summary.pendingCount > 0
                    ? ` · ${summary.pendingCount} pending`
                    : ''}
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
                      {summaryPending ? (
                        <SkeletonItem
                          animation={skeletonAnimation}
                          className={styles.skeletonTotal}
                        />
                      ) : (
                        signedCurrency(summary.totalExpenses, '-')
                      )}
                    </td>

                    <td className={styles.footCell}>
                      {summaryPending ? (
                        <SkeletonItem
                          animation={skeletonAnimation}
                          className={styles.skeletonTotal}
                        />
                      ) : (
                        signedCurrency(summary.totalIncome, '+')
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
                      {summaryPending ? (
                        <SkeletonItem
                          animation={skeletonAnimation}
                          className={styles.skeletonTotal}
                        />
                      ) : (
                        signedCurrency(summary.netChange, summary.netChange < 0 ? '-' : '+')
                      )}
                    </td>
                  </>
                )}
              </tr>
            </tfoot>
          </table>
        </section>
      </div>

      <Text as="p" className={styles.supportLine}>
        Need help? Our support team is available 24/7.
      </Text>

      {/* Money dialogs — mount-on-open (fresh presence state per open), over the full account list. */}
      {isDepositOpen && (
        <DepositDialog isOpen onClose={() => setIsDepositOpen(false)} accounts={legacyAccounts} />
      )}
      {isWithdrawOpen && (
        <WithdrawDialog isOpen onClose={() => setIsWithdrawOpen(false)} accounts={legacyAccounts} />
      )}
    </div>
  );
}

export default DirectionD;
