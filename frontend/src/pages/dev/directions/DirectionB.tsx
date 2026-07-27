import { useState } from 'react';
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
  Add20Regular,
  ArrowDownload24Regular,
  ArrowRight24Filled,
  ArrowUpload24Regular,
} from '@fluentui/react-icons';
import { format, startOfMonth } from 'date-fns';
import { atMedia } from '../../../theme/breakpoints';
import { colors, gradients, shadows, surfaces, transitions } from '../../../theme/tokens';
import { IconContainer } from '../../../components/shared/IconContainer';
import { TransactionItem } from '../../../components/shared/TransactionItem';
import { TransactionListSkeleton } from '../../../components/shared/TransactionListSkeleton';
import { DepositDialog, WithdrawDialog } from '../../../components';
import { useAppSelector } from '../../../app/hooks';
import { selectCurrentUser } from '../../../features/auth/authSlice';
import { useDelayedFlag } from '../../../hooks/useDelayedFlag';
import { useReducedMotion } from '../../../hooks/useReducedMotion';
import type { ApiProblem } from '../../../api/problemBaseQuery';
import {
  useGetAccountsQuery,
  useGetTransactionsQuery,
  useGetTransactionSummaryQuery,
  type AccountResponse,
  type TransactionResponse,
} from '../../../features/api/apiSlice';
import { formatCurrency, maskAccountNumber } from '../../../utils/format';

/**
 * U3-SCRATCH-DASHBOARD-DIRECTIONS — direction B: **the page is a launchpad.**
 *
 * The thesis, stated so the layout can be argued with rather than admired: someone opening this
 * screen has already decided to DO something. So the controls are the content. The screen is a
 * single CONSOLE — one panel, divided by hairlines into bays — with the transfer bay taking the
 * largest bay in it, and the balance demoted to a readout line in the header beside the title. No
 * balance card, no row of equal 80px tiles, no right rail.
 *
 * Three consequences worth naming up front:
 *
 *  - **`QuickActionButton` is deliberately NOT reused here.** It is an 80px `flex: 1` tile with a
 *    centred icon over a 12px label — three of them side by side is exactly the "row of small tiles
 *    under a balance card" this direction exists to argue against. Reusing it would have meant
 *    keeping the shape while claiming to have replaced it. `TransactionItem`,
 *    `TransactionListSkeleton` and `IconContainer` ARE reused: their shapes are not the argument.
 *
 *  - **The launcher does not wait for data.** The transfer bay renders and is clickable on the
 *    first frame, before `getAccounts` resolves and even if it fails — `/transfer` loads its own
 *    accounts. Gating the primary action on a query the primary action does not need is the exact
 *    cost this direction is trying to delete. The one case that retargets it is a user with zero
 *    accounts, where the honest launch is "Open an account".
 *
 *  - **This month and recent activity are ONE element**, not two. Below the console sits a single
 *    context panel under one heading: the month's numbers on the left, the five most recent rows on
 *    the right. Recent activity has not earned a section of its own on a page you came to act on;
 *    it has earned being the evidence next to the month's totals. Its transfer rows also feed the
 *    send-again chips in the console, so the feed pays for its space twice.
 *
 * NO fixed-width box exists anywhere in this file. Every grid track is `minmax(0, Nfr)`, so a
 * track's min-content can never push the grid wider than its container — which is what makes the
 * 1024–1094px band a non-event rather than a special case. See `console` and `context` below.
 */

/** The dashboard shows the five most recent transactions. Skeleton and query share the number. */
const RECENT_PAGE_SIZE = 5;

/** How many distinct @handles the send-again strip offers before it stops. */
const SEND_AGAIN_LIMIT = 4;

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  /**
   * The shell already pads the desktop content box (`24px 32px`), so this page adds horizontal
   * padding ONLY where the shell has none — the mobile branch of `AppLayout`. The current dashboard
   * pads on top of the shell's padding at `lg`, which is 64px of the 705px content box spent twice.
   */
  page: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    padding: '16px',
    [atMedia.lg]: {
      padding: 0,
      gap: '20px',
    },
  },

  // ========== HEADER BAND: greeting, title, balance readout ==========
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: '14px',
    [atMedia.md]: {
      flexDirection: 'row',
      alignItems: 'flex-end',
      justifyContent: 'space-between',
      gap: '24px',
    },
  },

  headerTitles: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
  },

  greeting: {
    fontSize: '14px',
    fontWeight: 500,
    lineHeight: '20px',
    color: colors.neutral[500],
  },

  /**
   * The one `<h1>`, and under this thesis the page's subject is the verb rather than the number:
   * you are here to move money, and the money you have is the context for that (the readout to the
   * right). Today `/dashboard` ships zero headings at all.
   */
  pageTitle: {
    fontSize: '30px',
    fontWeight: 700,
    lineHeight: '36px',
    letterSpacing: '-0.02em',
    color: colors.neutral[900],
    [atMedia.lg]: {
      fontSize: '36px',
      lineHeight: '42px',
    },
  },

  readout: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    minWidth: 0,
    [atMedia.md]: {
      alignItems: 'flex-end',
    },
  },

  readoutLabel: {
    fontSize: '11px',
    fontWeight: 700,
    lineHeight: '16px',
    letterSpacing: '0.12em',
    textTransform: 'uppercase',
    color: colors.neutral[500],
  },

  // Height, not width, is what a skeleton has to match — so the value and its placeholder both
  // declare 34px, and the meta line and its placeholder both declare 18px. Nothing moves on load.
  readoutValue: {
    fontSize: '28px',
    fontWeight: 700,
    lineHeight: '34px',
    letterSpacing: '-0.02em',
    fontVariantNumeric: 'tabular-nums',
    color: colors.neutral[900],
  },
  readoutValueSkeleton: { height: '34px', width: '164px' },
  readoutMeta: {
    fontSize: '13px',
    lineHeight: '18px',
    color: colors.neutral[500],
  },
  readoutMetaSkeleton: { height: '18px', width: '120px' },
  readoutMuted: {
    fontSize: '20px',
    fontWeight: 600,
    lineHeight: '34px',
    color: colors.neutral[400],
  },

  // ========== THE CONSOLE ==========
  /**
   * One panel, not four cards. The 1px `gap` over a `surfaces.border` ground turns the grid's own
   * gutters into hairlines, so the bays read as divisions of a single object — which is the whole
   * claim: this is a control surface, not a feed of cards.
   *
   * **The 1024–1094px band.** Measured, the shell hands a page 705px there (1009 client − 240
   * sidebar − 64 shell padding). Every track here is `minmax(0, …)` or `1fr`: the zero floor is what
   * stops a track's min-content from becoming a width demand, which is the precise mechanism by
   * which the current page's 399px min-content column plus its 360px `flexShrink: 0` rail overflow
   * by 86px. At 705px the console is one column, the money group inside it is two of 352px, and no
   * arrangement of content can make either wider.
   *
   * The side-by-side console waits for `xl` (1366px) ON PURPOSE, and not to dodge the band: at
   * 705px, splitting it 1.7fr / 1fr leaves the launch bay 442px, at which point it stops being the
   * biggest thing on the screen and the thesis is gone. Below 1366 the launcher takes the whole
   * measure, which is more launchpad rather than less.
   *
   * **One media query per property, and that is a hard constraint rather than a style.** Measured in
   * this app: Griffel's DOM renderer does NOT sort media buckets, it appends a `<style>` per query
   * in FIRST-USE order. The live document orders them 1024, 1366, 480, 640 — so a rule under
   * `atMedia.md` lands after one under `atMedia.xl` and wins at every width above 640. The first
   * draft of this grid declared `gridTemplateAreas` at `md` AND at `xl`; the `xl` arrangement was
   * silently dead at 1600px, and it looked like a design choice rather than a bug. Nothing in this
   * file now sets one property under two different queries — see `moneyGroup`, which gets its
   * responsive behaviour from `auto-fit` and no query at all.
   */
  console: {
    display: 'grid',
    gap: '1px',
    backgroundColor: surfaces.border,
    borderRadius: '10px',
    overflow: 'hidden',
    boxShadow: shadows.md,
    gridTemplateAreas: '"transfer" "recents" "money"',
    [atMedia.xl]: {
      gridTemplateColumns: 'minmax(0, 1.7fr) minmax(0, 1fr)',
      gridTemplateAreas: '"transfer money" "recents recents"',
    },
  },

  /**
   * Deposit and Withdraw, as one cell of the console that subdivides itself.
   *
   * `auto-fit` + `minmax(min(240px, 100%), 1fr)` is doing the responsive work that a media query
   * would otherwise do, and it does it better here because the answer depends on the CONTAINER, not
   * the viewport: two bays side by side when this cell is at least 481px (full width from ~513px
   * up), stacked when it is not — which is exactly what happens at `xl`, where the cell is the
   * console's 1fr column at roughly 420px and the two bays stack beside the tall launch bay without
   * anyone writing a rule for it. The `min(240px, 100%)` floor is what makes overflow impossible at
   * 320px: a bare `minmax(240px, 1fr)` track keeps its 240px even in a 200px box.
   */
  moneyGroup: {
    gridArea: 'money',
    display: 'grid',
    gap: '1px',
    gridTemplateColumns: 'repeat(auto-fit, minmax(min(240px, 100%), 1fr))',
    minWidth: 0,
  },

  bayReset: {
    border: 'none',
    fontFamily: 'inherit',
    textAlign: 'left',
    cursor: 'pointer',
    minWidth: 0,
    ':disabled': {
      cursor: 'not-allowed',
      opacity: 0.55,
    },
  },

  // Motion is opt-IN: applied only when the reader has not asked the OS for less of it.
  bayMotion: {
    transition: `transform ${transitions.fast}, box-shadow ${transitions.fast}`,
    ':hover:enabled': {
      transform: 'translateY(-1px)',
    },
    ':active:enabled': {
      transform: 'translateY(0)',
    },
  },

  transferBay: {
    gridArea: 'transfer',
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'space-between',
    gap: '24px',
    minHeight: '184px',
    padding: '22px 20px',
    background: gradients.brand,
    color: tokens.colorNeutralForegroundOnBrand,
    // One query, per the note on `console`: a second bump at `xl` would land in a bucket the
    // renderer happens to order before this one, and the taller bay would never appear.
    [atMedia.lg]: {
      minHeight: '244px',
      padding: '26px 24px',
    },
  },

  bayKicker: {
    fontSize: '11px',
    fontWeight: 700,
    lineHeight: '16px',
    letterSpacing: '0.12em',
    textTransform: 'uppercase',
    // Translucent white would mean an `rgba()` literal; opacity says the same thing about the
    // element and leaves the colour to the theme's on-brand token.
    opacity: 0.78,
  },

  transferHeadline: {
    display: 'block',
    fontSize: '34px',
    fontWeight: 700,
    lineHeight: '40px',
    letterSpacing: '-0.02em',
    [atMedia.lg]: {
      fontSize: '40px',
      lineHeight: '46px',
    },
  },

  transferSupport: {
    display: 'block',
    fontSize: '14px',
    lineHeight: '20px',
    opacity: 0.85,
  },

  transferFoot: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '16px',
    minWidth: 0,
  },

  launchDisc: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '48px',
    height: '48px',
    borderRadius: '50%',
    flexShrink: 0,
    backgroundColor: colors.brand[40],
  },

  // ========== SEND AGAIN ==========
  recentsBay: {
    gridArea: 'recents',
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '10px',
    padding: '14px 20px',
    background: gradients.brandTint,
    minWidth: 0,
    [atMedia.lg]: {
      padding: '14px 24px',
    },
  },

  recentsLabel: {
    fontSize: '11px',
    fontWeight: 700,
    lineHeight: '16px',
    letterSpacing: '0.12em',
    textTransform: 'uppercase',
    color: colors.brand[40],
    flexShrink: 0,
  },

  chip: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    // `flexShrink: 0` so a handle is not ellipsised while the row still has a second line to give
    // it; `maxWidth: 100%` is the floor that keeps an absurdly long handle inside the bay. Measured
    // at 320px: without the first rule, four chips compress and clip with 130px of row unused.
    maxWidth: '100%',
    flexShrink: 0,
    height: '34px',
    padding: '0 14px',
    borderRadius: '17px',
    border: `1px solid ${colors.brand[110]}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: colors.brand[40],
    fontFamily: 'inherit',
    fontSize: '14px',
    fontWeight: 600,
    cursor: 'pointer',
    ':hover': {
      backgroundColor: colors.brand[120],
    },
  },
  chipText: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  chipSkeleton: { height: '34px', width: '112px', borderRadius: '17px' },

  // ========== DEPOSIT / WITHDRAW BAYS ==========
  moneyBay: {
    display: 'flex',
    alignItems: 'center',
    gap: '14px',
    padding: '18px 20px',
    minHeight: '86px',
    backgroundColor: tokens.colorNeutralBackground1,
    ':hover:enabled': {
      backgroundColor: colors.neutral[50],
    },
    [atMedia.lg]: {
      padding: '20px 24px',
    },
  },
  bayText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minWidth: 0,
  },
  bayName: {
    fontSize: '17px',
    fontWeight: 600,
    lineHeight: '22px',
    color: colors.neutral[800],
  },
  baySupport: {
    fontSize: '13px',
    lineHeight: '18px',
    color: colors.neutral[500],
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  // ========== CONTEXT PANEL: this month AND recent activity, one element ==========
  /**
   * Same hairline-grid technique, same `minmax(0, …)` discipline. This one CAN go side by side at
   * `lg`, because what sits in the narrow track is four label/value rows (≈200px of min-content)
   * and what sits in the wide one is `TransactionItem`, whose min-content is about 222px — 32px
   * padding, a 44px icon, two 12px gaps, a ~90px amount column and a 20px chevron, with the
   * description free to ellipsis at `minWidth: 0`. At 705px the tracks come out 281px and 422px, so
   * both clear their floor with room to spare.
   */
  context: {
    display: 'grid',
    gap: '1px',
    backgroundColor: surfaces.border,
    borderRadius: '10px',
    overflow: 'hidden',
    boxShadow: shadows.sm,
    gridTemplateAreas: '"head" "money" "feed"',
    [atMedia.lg]: {
      gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1.5fr)',
      gridTemplateAreas: '"head head" "money feed"',
    },
  },

  contextHead: {
    gridArea: 'head',
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '12px',
    padding: '14px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    minWidth: 0,
  },

  contextTitle: {
    fontSize: '15px',
    fontWeight: 700,
    lineHeight: '20px',
    color: colors.neutral[800],
  },

  seeAll: {
    fontSize: '13px',
    fontWeight: 600,
    color: colors.brand[60],
    textDecoration: 'none',
    flexShrink: 0,
    ':hover': { textDecoration: 'underline' },
  },

  moneyCell: {
    gridArea: 'money',
    display: 'flex',
    flexDirection: 'column',
    padding: '4px 16px 12px',
    backgroundColor: tokens.colorNeutralBackground1,
    minWidth: 0,
  },

  moneyRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    padding: '9px 0',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    ':last-child': { borderBottom: 'none' },
  },

  moneyLabel: {
    fontSize: '13px',
    lineHeight: '20px',
    color: colors.neutral[500],
  },
  moneyValue: {
    fontSize: '14px',
    fontWeight: 600,
    lineHeight: '20px',
    fontVariantNumeric: 'tabular-nums',
    color: colors.neutral[800],
  },
  moneyValueSkeleton: { height: '20px', width: '86px' },
  moneyPositive: { color: colors.semantic.success.dark },
  moneyNegative: { color: colors.semantic.error.dark },

  feedCell: {
    gridArea: 'feed',
    display: 'flex',
    flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1,
    minWidth: 0,
  },

  feedHead: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    padding: '14px 16px 4px',
  },

  feedTitle: {
    fontSize: '12px',
    fontWeight: 700,
    lineHeight: '16px',
    letterSpacing: '0.1em',
    textTransform: 'uppercase',
    color: colors.neutral[500],
  },

  // The last row's divider is the panel's own edge, so it is removed rather than doubled.
  feedRow: {
    ':last-child': { borderBottom: 'none' },
  },

  feedState: {
    padding: '16px',
  },

  emptyText: {
    fontSize: '14px',
    color: colors.neutral[500],
  },

  errorText: {
    fontSize: '13px',
    color: colors.semantic.error.dark,
  },

  inlineRetry: {
    display: 'flex',
    justifyContent: 'flex-start',
    paddingTop: '4px',
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

/** Transfer rows lead with the counterparty handle; other types fall back to description. */
function counterpartyLabel(transaction: TransactionResponse): string | undefined {
  if (transaction.recipientAzureTag) return `To @${transaction.recipientAzureTag}`;
  if (transaction.senderAzureTag) return `From @${transaction.senderAzureTag}`;
  return undefined;
}

/** Signed EUR for the monthly rows: +€x / −€x (net keeps its real sign). */
function signedCurrency(amount: number, positiveSign: '+' | '-'): string {
  return `${positiveSign}${formatCurrency(Math.abs(amount))}`;
}

/**
 * The distinct @handles this user most recently sent to, newest first.
 *
 * Derived from the SAME five rows the feed renders — there is no recent-recipients endpoint, and
 * inventing one for a scratch direction would be arguing with data rather than with layout. It is
 * also the honest reading of "recent": the strip and the feed cannot disagree.
 */
function recentRecipients(transactions: TransactionResponse[], limit: number): string[] {
  const tags: string[] = [];
  for (const transaction of transactions) {
    const tag = transaction.recipientAzureTag;
    if (tag && !tags.includes(tag)) tags.push(tag);
    if (tags.length === limit) break;
  }
  return tags;
}

/** The money dialogs take this minimal shape (their RHF rewrite is a separate PR). */
function toDialogAccount(account: AccountResponse) {
  return {
    id: account.id,
    name: account.name,
    accountNumber: maskAccountNumber(account.accountNumber),
    balance: account.balance,
  };
}

// ============================================
// COMPONENT
// ============================================

export function DirectionB() {
  const styles = useStyles();
  const navigate = useNavigate();
  const user = useAppSelector(selectCurrentUser);
  const reducedMotion = useReducedMotion();

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

  // Month start computed ONCE per mount; toDate is deliberately not sent so the server defaults it
  // to "now" per request and a post-mutation refetch includes the transaction that caused it.
  const [monthWindow] = useState(() => ({ fromDate: startOfMonth(new Date()).toISOString() }));
  const {
    data: summary,
    isLoading: summaryLoading,
    error: summaryError,
    refetch: refetchSummary,
  } = useGetTransactionSummaryQuery(monthWindow);

  const [isDepositOpen, setIsDepositOpen] = useState(false);
  const [isWithdrawOpen, setIsWithdrawOpen] = useState(false);

  const primaryAccount = accounts.find((account) => account.isPrimary) ?? accounts[0];
  const totalBalance = accounts.reduce((sum, account) => sum + account.balance, 0);
  const recentTransactions = recent?.data ?? [];
  const sendAgain = recentRecipients(recentTransactions, SEND_AGAIN_LIMIT);
  const monthLabel = format(new Date(), 'MMMM');

  // The ONE state that retargets the launcher: a user with no accounts cannot send anything, so the
  // primary control points at the thing that unblocks them instead of at a dead end.
  const needsFirstAccount = !accountsLoading && !accountsProblem && accounts.length === 0;
  const canMoveOwnMoney = accounts.length > 0;

  const bay = (extra: string) =>
    mergeClasses(styles.bayReset, !reducedMotion && styles.bayMotion, extra);

  // `state` is read by nobody today: TransferPage owns its own recipient lookup, and prefilling it
  // is a one-line read there in the follow-up PR. The chip is not dead in the meantime — it starts
  // the flow, which is the only thing this page is entitled to do.
  const launchTransfer = (prefillTag?: string) =>
    navigate('/transfer', prefillTag ? { state: { prefillTag } } : undefined);

  return (
    <div className={styles.page}>
      {/* ===== Header band: who you are, what this page is for, and what you have ===== */}
      <div className={styles.header}>
        <div className={styles.headerTitles}>
          <Text as="p" className={styles.greeting}>
            {getGreeting()}
            {user ? `, ${user.firstName}` : ''}
          </Text>
          <Text as="h1" className={styles.pageTitle}>
            Move money
          </Text>
        </div>

        <div className={styles.readout}>
          <Text className={styles.readoutLabel}>Available to move</Text>
          {accountsLoading && (
            <Skeleton aria-label="Loading your balance" aria-busy>
              <SkeletonItem className={styles.readoutValueSkeleton} />
            </Skeleton>
          )}
          {!accountsLoading && accountsProblem && (
            <Text className={styles.readoutMuted}>Balance unavailable</Text>
          )}
          {!accountsLoading && !accountsProblem && !canMoveOwnMoney && (
            <Text className={styles.readoutMuted}>No accounts yet</Text>
          )}
          {!accountsLoading && !accountsProblem && canMoveOwnMoney && (
            <Text className={styles.readoutValue}>{formatCurrency(totalBalance)}</Text>
          )}

          {accountsLoading ? (
            <Skeleton aria-hidden>
              <SkeletonItem className={styles.readoutMetaSkeleton} />
            </Skeleton>
          ) : (
            <Text className={styles.readoutMeta}>
              {canMoveOwnMoney && primaryAccount
                ? `${accounts.length} account${accounts.length === 1 ? '' : 's'} · ${primaryAccount.name}`
                : 'Sending still works from the transfer screen'}
            </Text>
          )}
        </div>
      </div>

      {/* Accounts failing is a SECTIONAL problem here, not a page gate: the launcher below still
          works, so the error bar reports the readout and the two own-money bays, and nothing else. */}
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

      {/* ===== THE CONSOLE ===== */}
      <div className={styles.console}>
        <button
          type="button"
          className={bay(styles.transferBay)}
          onClick={() => (needsFirstAccount ? navigate('/accounts') : launchTransfer())}
        >
          <Text className={styles.bayKicker}>
            {needsFirstAccount ? 'Start here' : 'Send to any @handle'}
          </Text>
          <div className={styles.transferFoot}>
            <span>
              <Text as="span" className={styles.transferHeadline}>
                {needsFirstAccount ? 'Open an account' : 'Transfer'}
              </Text>
              <Text as="span" className={styles.transferSupport}>
                {needsFirstAccount
                  ? 'You need an account before money can move.'
                  : 'Pick a recipient, pick an amount, confirm with your PIN.'}
              </Text>
            </span>
            <span className={styles.launchDisc}>
              <ArrowRight24Filled />
            </span>
          </div>
        </button>

        {/* Send again — the strip keeps its height in every state (chips are 34px, so are their
            placeholders), which is why the "new handle" chip is always present rather than a
            fallback. A launchpad that reflows while you reach for it is not one. */}
        <div className={styles.recentsBay}>
          <Text className={styles.recentsLabel}>Send again</Text>
          {recentLoading && showRecentSkeleton && (
            <Skeleton aria-label="Loading recent recipients" aria-busy>
              <SkeletonItem className={styles.chipSkeleton} />
            </Skeleton>
          )}
          {!recentLoading &&
            sendAgain.map((tag) => (
              <button
                key={tag}
                type="button"
                className={styles.chip}
                onClick={() => launchTransfer(tag)}
              >
                <span className={styles.chipText}>@{tag}</span>
              </button>
            ))}
          <button type="button" className={styles.chip} onClick={() => launchTransfer()}>
            <Add20Regular />
            <span className={styles.chipText}>New handle</span>
          </button>
        </div>

        {/* Own-money bays. They ARE gated on `getAccounts` — unlike the launcher, the dialogs they
            open take the account list as a prop, so offering them without one would be a dead
            control. Disabled rather than hidden: a console that loses bays reflows. */}
        <div className={styles.moneyGroup}>
          <button
            type="button"
            className={bay(styles.moneyBay)}
            disabled={!canMoveOwnMoney}
            onClick={() => setIsDepositOpen(true)}
          >
            <IconContainer variant="deposit" size="md">
              <ArrowDownload24Regular />
            </IconContainer>
            <span className={styles.bayText}>
              <Text as="span" className={styles.bayName}>
                Deposit
              </Text>
              <Text as="span" className={styles.baySupport}>
                Top up an account
              </Text>
            </span>
          </button>

          <button
            type="button"
            className={bay(styles.moneyBay)}
            disabled={!canMoveOwnMoney}
            onClick={() => setIsWithdrawOpen(true)}
          >
            <IconContainer variant="withdrawal" size="md">
              <ArrowUpload24Regular />
            </IconContainer>
            <span className={styles.bayText}>
              <Text as="span" className={styles.bayName}>
                Withdraw
              </Text>
              <Text as="span" className={styles.baySupport}>
                Take money out
              </Text>
            </span>
          </button>
        </div>
      </div>

      {/* ===== CONTEXT: the month and its evidence, under one heading ===== */}
      <section className={styles.context}>
        <div className={styles.contextHead}>
          <Text as="h2" className={styles.contextTitle}>
            {monthLabel} so far
          </Text>
          <Link to="/history" className={styles.seeAll}>
            See all activity
          </Link>
        </div>

        <div className={styles.moneyCell}>
          {summaryError !== undefined ? (
            <>
              <Text className={styles.errorText}>Could not load this month&apos;s totals.</Text>
              <div className={styles.inlineRetry}>
                <Button appearance="transparent" onClick={() => void refetchSummary()}>
                  Retry
                </Button>
              </div>
            </>
          ) : (
            <>
              <div className={styles.moneyRow}>
                <Text className={styles.moneyLabel}>Money in</Text>
                {summaryLoading || !summary ? (
                  <Skeleton aria-label="Loading this month's totals" aria-busy>
                    <SkeletonItem className={styles.moneyValueSkeleton} />
                  </Skeleton>
                ) : (
                  <Text className={mergeClasses(styles.moneyValue, styles.moneyPositive)}>
                    {signedCurrency(summary.totalIncome, '+')}
                  </Text>
                )}
              </div>
              <div className={styles.moneyRow}>
                <Text className={styles.moneyLabel}>Money out</Text>
                {summaryLoading || !summary ? (
                  <Skeleton aria-hidden>
                    <SkeletonItem className={styles.moneyValueSkeleton} />
                  </Skeleton>
                ) : (
                  <Text className={mergeClasses(styles.moneyValue, styles.moneyNegative)}>
                    {signedCurrency(summary.totalExpenses, '-')}
                  </Text>
                )}
              </div>
              <div className={styles.moneyRow}>
                <Text className={styles.moneyLabel}>Net</Text>
                {summaryLoading || !summary ? (
                  <Skeleton aria-hidden>
                    <SkeletonItem className={styles.moneyValueSkeleton} />
                  </Skeleton>
                ) : (
                  <Text
                    className={mergeClasses(
                      styles.moneyValue,
                      summary.netChange < 0 ? styles.moneyNegative : styles.moneyPositive,
                    )}
                  >
                    {signedCurrency(summary.netChange, summary.netChange < 0 ? '-' : '+')}
                  </Text>
                )}
              </div>
              <div className={styles.moneyRow}>
                <Text className={styles.moneyLabel}>Pending</Text>
                {summaryLoading || !summary ? (
                  <Skeleton aria-hidden>
                    <SkeletonItem className={styles.moneyValueSkeleton} />
                  </Skeleton>
                ) : (
                  <Text className={styles.moneyValue}>{summary.pendingCount}</Text>
                )}
              </div>
            </>
          )}
        </div>

        <div className={styles.feedCell}>
          <div className={styles.feedHead}>
            <Text as="h3" className={styles.feedTitle}>
              Last {RECENT_PAGE_SIZE} movements
            </Text>
          </div>

          {recentLoading && showRecentSkeleton && (
            <TransactionListSkeleton rows={RECENT_PAGE_SIZE} label="Loading recent transactions" />
          )}

          {!recentLoading && recentError !== undefined && (
            <div className={styles.feedState}>
              <MessageBar intent="error">
                <MessageBarBody>Could not load recent transactions.</MessageBarBody>
                <MessageBarActions>
                  <Button appearance="transparent" onClick={() => void refetchRecent()}>
                    Retry
                  </Button>
                </MessageBarActions>
              </MessageBar>
            </div>
          )}

          {!recentLoading && !recentError && recentTransactions.length === 0 && (
            <div className={styles.feedState}>
              <Text className={styles.emptyText}>
                Nothing has moved yet. The launcher above is where it starts.
              </Text>
            </div>
          )}

          {!recentLoading &&
            !recentError &&
            recentTransactions.map((transaction) => (
              <TransactionItem
                key={transaction.id}
                className={styles.feedRow}
                id={transaction.id}
                type={transaction.type}
                description={transaction.description ?? ''}
                counterparty={counterpartyLabel(transaction)}
                amount={transaction.amount}
                date={transaction.createdAt}
                onClick={(id) => navigate(`/transactions/${id}`)}
              />
            ))}
        </div>
      </section>

      {/* Money dialogs — mount-on-open, over the full account list, exactly as the live page does. */}
      {isDepositOpen && (
        <DepositDialog
          isOpen
          onClose={() => setIsDepositOpen(false)}
          accounts={accounts.map(toDialogAccount)}
        />
      )}
      {isWithdrawOpen && (
        <WithdrawDialog
          isOpen
          onClose={() => setIsWithdrawOpen(false)}
          accounts={accounts.map(toDialogAccount)}
        />
      )}
    </div>
  );
}

export default DirectionB;
