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
  ArrowDownload20Regular,
  ArrowSwap20Regular,
  ArrowUpload20Regular,
} from '@fluentui/react-icons';
import { format, startOfMonth } from 'date-fns';
import { colors, surfaces, transitions } from '../../../theme/tokens';
import { atMedia } from '../../../theme/breakpoints';
import { TransactionItem } from '../../../components/shared/TransactionItem';
import { TransactionListSkeleton } from '../../../components/shared/TransactionListSkeleton';
import { DepositDialog, WithdrawDialog } from '../../../components';
import { useDelayedFlag } from '../../../hooks/useDelayedFlag';
import { useReducedMotion } from '../../../hooks/useReducedMotion';
import { useAppSelector } from '../../../app/hooks';
import { selectCurrentUser } from '../../../features/auth/authSlice';
import type { ApiProblem } from '../../../api/problemBaseQuery';
import type { AccountResponse, TransactionResponse } from '../../../features/api/apiSlice';
import {
  useGetAccountsQuery,
  useGetTransactionsQuery,
  useGetTransactionSummaryQuery,
} from '../../../features/api/apiSlice';
import { formatCurrency, maskAccountNumber } from '../../../utils/format';

/**
 * U3-SCRATCH-DASHBOARD-DIRECTIONS — direction A: **the balance is the page.**
 *
 * The thesis, stated as a layout rule rather than a mood: ONE number is the page's subject, it is
 * the `<h1>`, and every other required piece of content is demoted until it reads as an annotation
 * on that number. Nothing here is a peer of the balance, so nothing here is a card, a rail or a
 * column. There is exactly one column, at every width, and the composition is carried by type size
 * — roughly 120px against 15px — instead of by boxes.
 *
 * Three consequences worth naming, because they are what make this direction NOT the other two:
 *
 *  1. **No card under the number.** A hero card would put the balance inside a container, and a
 *     container is a thing you can put other containers beside. Set on the bare canvas the number
 *     has no siblings — the shell's ground IS its background.
 *  2. **The month summary is one sentence, not a panel.** Income / expenses / net are three facts
 *     about the same number, so they are typeset as one line of prose under it. A three-row card
 *     would claim the summary is a second subject.
 *  3. **The account breakdown and the account switcher are the same element.** With two accounts
 *     "the balance" is ambiguous; the chips under the number resolve it by showing the parts AND
 *     letting you point the big number at any of them (default: the total).
 *
 * The 1024–1094px band that breaks the current dashboard cannot occur here: there is no fixed-width
 * sibling anywhere to sum against, and the only element that could out-grow its box is the numeral,
 * which is clamped against its own container (`cqi`) in three length buckets — see `amount*`.
 */

/** The dashboard shows the five most recent transactions. Skeleton and query share it. */
const RECENT_PAGE_SIZE = 5;

/**
 * A mount rise for the one thing the reader came for. Gated on `useReducedMotion` — the balance is
 * the last element that should insist on moving for someone who asked the OS to stop.
 */
const riseIn = {
  from: { opacity: 0, transform: 'translateY(10px)' },
  to: { opacity: 1, transform: 'translateY(0)' },
};

const useStyles = makeStyles({
  /**
   * One column. Horizontal padding at base only: the desktop shell (`AppLayout.desktopContent`)
   * already pays 32px a side, and the second helping is a third of what made the old two-column row
   * fight for 641px inside a 705px box.
   */
  page: {
    display: 'flex',
    flexDirection: 'column',
    gap: '32px',
    padding: '16px 16px 24px',
    [atMedia.lg]: {
      gap: '44px',
      padding: '8px 0 32px',
    },
  },

  // ========== THE BALANCE ==========
  // `inline-size` containment makes the numeral's `cqi` sizing resolve against THIS box rather than
  // the viewport, so the shell's 240px sidebar is already subtracted before the number is measured.
  balanceBlock: {
    containerType: 'inline-size',
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },

  rise: {
    animationName: riseIn,
    animationDuration: '280ms',
    animationTimingFunction: 'ease-out',
    animationFillMode: 'both',
  },

  greeting: {
    display: 'block',
    margin: 0,
    fontSize: '15px',
    lineHeight: '20px',
    fontWeight: 400,
    color: colors.neutral[600],
  },

  h1: {
    display: 'block',
    margin: 0,
    fontWeight: 700,
  },

  /**
   * The numeral's box. `lineHeight: 1` so its height is exactly `1em` — which is what lets the
   * loading skeleton stand in at the same height without knowing the digits yet.
   */
  amountBox: {
    display: 'flex',
    alignItems: 'baseline',
    lineHeight: 1,
    color: colors.neutral[900],
    letterSpacing: '-0.035em',
    fontVariantNumeric: 'tabular-nums',
  },

  /**
   * Three buckets by character count, because one clamp cannot serve both `€480.36` and
   * `€1,234,567.89` — and a numeral that only *usually* fits is the 1024px defect all over again.
   *
   * Measured in Chrome rather than guessed — `€2,080.50` is 3.50em, `€1,234,567.89` 4.96em and
   * `€999,999,999.99` 5.97em at this weight. Against the 705px container a 1024px viewport
   * affords, the three buckets render 469px, 525px and 505px; against the 288px one a 320px
   * viewport affords, 208px, 214px and 207px. Every case clears its box, which is the whole point:
   * a numeral that only USUALLY fits is the 1024px defect all over again.
   */
  amountXl: { fontSize: 'clamp(2.5rem, 19cqi, 9rem)' },
  amountLg: { fontSize: 'clamp(2.25rem, 15cqi, 7rem)' },
  amountMd: { fontSize: 'clamp(2rem, 12cqi, 5.5rem)' },

  // Cents ride small and quiet: at a glance you read the euros, and the decimals are there when you
  // look for them. This is the whole "check it like a watch" argument in one declaration.
  cents: {
    fontSize: '0.42em',
    fontWeight: 600,
    letterSpacing: '0',
    color: colors.neutral[500],
  },

  amountSkeleton: {
    display: 'flex',
    alignItems: 'center',
    height: '1em',
    width: '100%',
  },
  // 3.5em is the measured width of a real mid-size balance at this type size, so the bar is the
  // numeral's own footprint rather than an arbitrary rectangle.
  amountSkeletonBar: {
    height: '0.6em',
    width: '3.5em',
  },

  /**
   * The "no value" slot, and it keeps the numeral's exact 1em height so a failed load leaves the
   * page the same shape as a good one. The dash is drawn at half size and in a light neutral on
   * purpose: an em dash at the full 134px is a solid black bar that reads as a redaction rather
   * than as an absence — a real thing this direction shipped for one screenshot before it was
   * measured.
   */
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

  // ========== ACCOUNT CHIPS = THE BREAKDOWN AND THE SWITCHER ==========
  chipRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '8px',
    marginTop: '2px',
    [atMedia.lg]: {
      maxWidth: '640px',
    },
  },

  chip: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    minWidth: 0,
    maxWidth: '100%',
    padding: '8px 12px',
    borderRadius: '4px',
    border: `1px solid ${surfaces.border}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    textAlign: 'left',
    transition: `background-color ${transitions.fast}, border-color ${transitions.fast}`,
    // Griffel types `borderColor` as a shorthand it will not accept, so the hover and selected
    // states restate the whole `border` rather than reaching for a longhand that is not there.
    ':hover': {
      border: `1px solid ${colors.brand[90]}`,
    },
  },

  chipActive: {
    backgroundColor: colors.brand[120],
    border: `1px solid ${colors.brand[110]}`,
  },

  chipLabel: {
    fontSize: '12px',
    lineHeight: '16px',
    color: colors.neutral[600],
    maxWidth: '160px',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  chipLabelActive: { color: colors.brand[40] },

  chipValue: {
    fontSize: '15px',
    lineHeight: '20px',
    fontWeight: 600,
    color: colors.neutral[800],
    fontVariantNumeric: 'tabular-nums',
  },
  chipValueActive: { color: colors.brand[40] },

  // Same 2px top margin as the real `chipRow`. Measured: without it the page below the balance
  // settled 2px when the accounts arrived — the exact shift a skeleton exists to prevent.
  chipSkeletonRow: {
    display: 'flex',
    gap: '8px',
    marginTop: '2px',
  },
  // 56px is a measured chip: 16px label + 2px gap + 20px value + 16px padding + 2px border.
  chipSkeletonItem: {
    height: '56px',
    width: '132px',
    borderRadius: '4px',
  },

  // ========== EVERYTHING ELSE — the footnote column ==========
  // A measure, not a column, and only from `lg` up. On desktop 640px sits well under the 705px the
  // 1024px band affords (65px of slack, and nothing here has a min-content floor that could eat
  // it), which is what makes the annotations hug the left under a numeral that spans the full box.
  // Below `lg` there is no sidebar to compose against, so capping there would just leave a gutter.
  footnotes: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    width: '100%',
    [atMedia.lg]: {
      maxWidth: '640px',
    },
  },

  actionRow: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '8px',
  },

  // Transfer is the one thing you do TO a balance, so it is the only filled control on the page.
  // Deposit and Withdraw are subtle text buttons beside it — same row, a third of the weight.
  // Full width on a phone, where three controls on one line wraps "Withdraw" onto a lonely row.
  transferButton: {
    width: '100%',
    [atMedia.md]: {
      width: 'auto',
      minWidth: '184px',
    },
  },

  /**
   * Income / expenses / net as one sentence — and the sentence's box is reserved at TWO lines
   * everywhere, by both the text and its skeleton.
   *
   * The measurement that forced it: the rendered sentence is 453px of type, so it sits on one line
   * on a desktop and wraps to two on a phone. A skeleton with a single 22px line would therefore
   * have been correct on one class of screen and 22px short on the other. Reserving 44px in both
   * states costs a little air under a footnote and makes the shift structurally impossible,
   * including for a longer sentence than this account happens to produce.
   */
  summaryLine: {
    display: 'block',
    margin: 0,
    minHeight: '44px',
    fontSize: '15px',
    lineHeight: '22px',
    color: colors.neutral[600],
  },
  summaryStrong: {
    fontWeight: 600,
    fontVariantNumeric: 'tabular-nums',
  },
  summaryPositive: { color: colors.semantic.success.dark },
  summaryNegative: { color: colors.semantic.error.dark },

  summarySkeletonRow: {
    display: 'flex',
    alignItems: 'flex-start',
    minHeight: '44px',
  },
  // 440px ≈ the sentence's own measured footprint; 5px of offset centres the bar in the first
  // 22px line box rather than against the top of the reserved two.
  summarySkeletonBar: {
    height: '12px',
    width: '440px',
    maxWidth: '100%',
    marginTop: '5px',
  },

  summaryErrorRow: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '4px',
    minHeight: '44px',
    flexWrap: 'wrap',
  },
  summaryErrorText: {
    fontSize: '15px',
    lineHeight: '22px',
    color: colors.semantic.error.dark,
  },

  // ========== RECENT ACTIVITY ==========
  recent: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    width: '100%',
    [atMedia.lg]: {
      maxWidth: '640px',
    },
  },

  recentHeader: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '12px',
  },

  // A footnote's heading, sized like one: 13px tracked caps, not an 18px section title.
  recentTitle: {
    margin: 0,
    fontSize: '13px',
    fontWeight: 600,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
    color: colors.neutral[500],
  },

  seeAll: {
    fontSize: '13px',
    fontWeight: 500,
    color: colors.brand[60],
    textDecoration: 'none',
    ':hover': { textDecoration: 'underline' },
  },

  // No radius, no shadow: square rows under a hairline read as a ledger appended to the page, not
  // as a card competing with it.
  recentList: {
    borderTop: `1px solid ${surfaces.border}`,
  },

  recentState: {
    padding: '16px 0',
    fontSize: '14px',
    color: colors.neutral[500],
  },

  emptyBody: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '12px',
  },
  emptyText: {
    fontSize: '15px',
    lineHeight: '22px',
    color: colors.neutral[600],
  },

  srOnly: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    padding: 0,
    margin: '-1px',
    overflow: 'hidden',
    clip: 'rect(0, 0, 0, 0)',
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

/** Transfer rows lead with the counterparty handle; other types fall back to description. */
function counterpartyLabel(transaction: TransactionResponse): string | undefined {
  if (transaction.recipientAzureTag) return `To @${transaction.recipientAzureTag}`;
  if (transaction.senderAzureTag) return `From @${transaction.senderAzureTag}`;
  return undefined;
}

function signedCurrency(amount: number, positiveSign: '+' | '-'): string {
  return `${positiveSign}${formatCurrency(Math.abs(amount))}`;
}

/** Euros and cents, split so the cents can recede. `en-IE` always uses `.` as the separator. */
function splitAmount(text: string): { euros: string; cents: string } {
  const at = text.lastIndexOf('.');
  if (at === -1) return { euros: text, cents: '' };
  return { euros: text.slice(0, at), cents: text.slice(at) };
}

// The dialogs still take this minimal shape (their RHF rewrite is a separate PR).
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

export function DirectionA() {
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

  // Month start computed ONCE per mount; toDate deliberately omitted so the server defaults it to
  // "now" and a post-mutation refetch includes the transaction that triggered it.
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
   * Multi-account, answered rather than dodged: the default number is the TOTAL, and the chips
   * below it let the reader point the same numeral at a single account. `'all'` is a state, not an
   * id, and a stale id (an account deleted under us) falls back to the total by construction —
   * `find` simply misses.
   */
  const [view, setView] = useState<'all' | string>('all');
  const selectedAccount = accounts.find((account) => account.id === view);
  const totalBalance = accounts.reduce((sum, account) => sum + account.balance, 0);
  const shownBalance = selectedAccount ? selectedAccount.balance : totalBalance;

  const amountText = formatCurrency(accountsProblem ? 0 : shownBalance);
  const { euros, cents } = splitAmount(amountText);
  const amountSizeClass =
    amountText.length <= 10
      ? styles.amountXl
      : amountText.length <= 13
        ? styles.amountLg
        : styles.amountMd;

  const headingLabel = selectedAccount
    ? `${selectedAccount.name} balance`
    : accounts.length > 1
      ? 'Total balance across all accounts'
      : 'Total balance';

  const legacyAccounts = accounts.map(toLegacy);
  const recentTransactions = recent?.data ?? [];
  const monthLabel = format(new Date(), 'MMMM');
  const hasAccounts = !accountsLoading && !accountsProblem && accounts.length > 0;

  return (
    <div className={styles.page}>
      {/* ===== The subject of the page ===== */}
      <section className={mergeClasses(styles.balanceBlock, !reducedMotion && styles.rise)}>
        <Text as="p" className={styles.greeting}>
          {getGreeting()}
          {user ? `, ${user.firstName}` : ''}
        </Text>

        <Text as="h1" className={styles.h1}>
          {/* The heading's accessible name, and the only place the amount is readable text: the
              visual numeral is split into euros and cents, which a screen reader would voice as two
              fragments. `aria-live` because pressing a chip changes THIS heading and nothing else —
              a sighted reader sees the number swap, and this is how everyone else hears it. */}
          <span className={styles.srOnly} aria-live="polite">
            {accountsLoading
              ? 'Loading your balance'
              : accountsProblem
                ? `${headingLabel} unavailable`
                : `${headingLabel}: ${amountText}`}
          </span>

          <span className={mergeClasses(styles.amountBox, amountSizeClass)} aria-hidden="true">
            {accountsLoading ? (
              // Same 1em box as the numeral it replaces, so the arriving balance moves nothing.
              <Skeleton
                animation={reducedMotion ? 'pulse' : 'wave'}
                className={styles.amountSkeleton}
              >
                <SkeletonItem className={styles.amountSkeletonBar} />
              </Skeleton>
            ) : accountsProblem ? (
              <span className={styles.amountFallback}>
                <span className={styles.amountFallbackMark}>&mdash;</span>
              </span>
            ) : (
              <>
                {euros}
                <span className={styles.cents}>{cents}</span>
              </>
            )}
          </span>
        </Text>

        {accountsLoading && (
          <Skeleton
            animation={reducedMotion ? 'pulse' : 'wave'}
            className={styles.chipSkeletonRow}
            aria-label="Loading your accounts"
          >
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

        {/* The breakdown IS the switcher. One element instead of a caption plus a control. */}
        {hasAccounts && accounts.length > 1 && (
          <div className={styles.chipRow} role="group" aria-label="Choose which balance to show">
            <button
              type="button"
              className={mergeClasses(styles.chip, view === 'all' && styles.chipActive)}
              aria-pressed={view === 'all'}
              onClick={() => setView('all')}
            >
              <span
                className={mergeClasses(styles.chipLabel, view === 'all' && styles.chipLabelActive)}
              >
                All {accounts.length} accounts
              </span>
              <span
                className={mergeClasses(styles.chipValue, view === 'all' && styles.chipValueActive)}
              >
                {formatCurrency(totalBalance)}
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
                  <span
                    className={mergeClasses(styles.chipLabel, active && styles.chipLabelActive)}
                  >
                    {account.name}
                  </span>
                  <span
                    className={mergeClasses(styles.chipValue, active && styles.chipValueActive)}
                  >
                    {formatCurrency(account.balance)}
                  </span>
                </button>
              );
            })}
          </div>
        )}

        {/* One account: no switcher to offer, so the chip slot carries the identity instead. */}
        {hasAccounts && accounts.length === 1 && (
          <div className={styles.chipRow}>
            <span className={styles.chipLabel}>
              {accounts[0].name} · {maskAccountNumber(accounts[0].accountNumber)}
            </span>
          </div>
        )}
      </section>

      {/* ===== Annotations on the number ===== */}
      <section className={styles.footnotes}>
        {!accountsLoading && !accountsProblem && accounts.length === 0 ? (
          <div className={styles.emptyBody}>
            <Text className={styles.emptyText}>
              You have no accounts yet, so there is nothing to show above.
            </Text>
            <Button appearance="primary" size="large" onClick={() => navigate('/accounts')}>
              Open your first account
            </Button>
          </div>
        ) : (
          <div className={styles.actionRow}>
            <Button
              appearance="primary"
              size="large"
              className={styles.transferButton}
              icon={<ArrowSwap20Regular />}
              disabled={!hasAccounts}
              onClick={() => navigate('/transfer')}
            >
              Transfer money
            </Button>
            <Button
              appearance="subtle"
              icon={<ArrowDownload20Regular />}
              disabled={!hasAccounts}
              onClick={() => setIsDepositOpen(true)}
            >
              Deposit
            </Button>
            <Button
              appearance="subtle"
              icon={<ArrowUpload20Regular />}
              disabled={!hasAccounts}
              onClick={() => setIsWithdrawOpen(true)}
            >
              Withdraw
            </Button>
          </div>
        )}

        {/* This month, as a sentence. Three rows in a panel would claim a second subject. */}
        {summaryError !== undefined ? (
          <div className={styles.summaryErrorRow}>
            <Text className={styles.summaryErrorText}>
              {monthLabel}&rsquo;s summary could not be loaded.
            </Text>
            <Button appearance="transparent" size="small" onClick={() => void refetchSummary()}>
              Retry
            </Button>
          </div>
        ) : summaryLoading || !summary ? (
          <Skeleton
            animation={reducedMotion ? 'pulse' : 'wave'}
            className={styles.summarySkeletonRow}
            aria-label={`Loading your ${monthLabel} summary`}
          >
            <SkeletonItem className={styles.summarySkeletonBar} />
          </Skeleton>
        ) : (
          <Text as="p" className={styles.summaryLine}>
            {monthLabel} so far:{' '}
            <span className={mergeClasses(styles.summaryStrong, styles.summaryPositive)}>
              {signedCurrency(summary.totalIncome, '+')}
            </span>{' '}
            in,{' '}
            <span className={mergeClasses(styles.summaryStrong, styles.summaryNegative)}>
              {signedCurrency(summary.totalExpenses, '-')}
            </span>{' '}
            out — net{' '}
            <span
              className={mergeClasses(
                styles.summaryStrong,
                summary.netChange < 0 ? styles.summaryNegative : styles.summaryPositive,
              )}
            >
              {signedCurrency(summary.netChange, summary.netChange < 0 ? '-' : '+')}
            </span>
            {summary.pendingCount > 0 ? `, ${summary.pendingCount} pending` : ''}.
          </Text>
        )}
      </section>

      {/* ===== The ledger, deliberately last and deliberately quiet ===== */}
      <section className={styles.recent}>
        <div className={styles.recentHeader}>
          <Text as="h2" className={styles.recentTitle}>
            Recent activity
          </Text>
          <Link to="/history" className={styles.seeAll}>
            See all
          </Link>
        </div>

        {recentLoading && showRecentSkeleton && (
          <TransactionListSkeleton rows={RECENT_PAGE_SIZE} label="Loading recent transactions" />
        )}

        {!recentLoading && recentError !== undefined && (
          <MessageBar intent="error">
            <MessageBarBody>Could not load recent transactions.</MessageBarBody>
            <MessageBarActions>
              <Button appearance="transparent" onClick={() => void refetchRecent()}>
                Retry
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}

        {!recentLoading && !recentError && recentTransactions.length === 0 && (
          <Text className={styles.recentState}>No transactions yet.</Text>
        )}

        {!recentLoading && !recentError && recentTransactions.length > 0 && (
          <div className={styles.recentList}>
            {recentTransactions.map((transaction) => (
              <TransactionItem
                key={transaction.id}
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
        )}
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

export default DirectionA;
