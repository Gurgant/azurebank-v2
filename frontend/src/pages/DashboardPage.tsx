import { useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  Button,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowDownload24Regular,
  ArrowSwap24Regular,
  ArrowUpload24Regular,
  Eye20Regular,
  EyeOff20Regular,
  ChevronRight16Regular,
  Headset24Regular,
} from '@fluentui/react-icons';
import { format, startOfMonth } from 'date-fns';
import { atMedia } from '../theme/breakpoints';
import { colors, shadows, surfaces } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import type { AccountResponse, TransactionResponse } from '../features/api/apiSlice';
import {
  useGetAccountsQuery,
  useGetTransactionsQuery,
  useGetTransactionSummaryQuery,
} from '../features/api/apiSlice';
import { formatCurrency, maskAccountNumber } from '../utils/format';
import { QuickActionButton } from '../components/shared/QuickActionButton';
import {
  TransactionHead,
  TransactionRow,
  TransactionTable,
  TransactionRowSkeleton,
  TransactionEmptyRow,
  StatusPill,
} from '../components/shared/TransactionRow';
import {
  transactionLabel,
  useTransactionCellStyles,
} from '../components/shared/transactionRowStyles';
import { DepositDialog, WithdrawDialog } from '../components';

/**
 * The dashboard.
 *
 * **The scope selector is the page's one control, and everything obeys it.**
 *
 * E is not a fifth aesthetic. It is the answer to a specific defect that A, B, C, D, the current
 * dashboard and the externally-generated mockup all gesture at and none of them close: a running
 * balance column beside a cross-account total **cannot reconcile**, because `balanceAfter` is
 * per-account and the hero is a sum. On screen today that reads as €2,200.50 on the newest row
 * under a €2,080.50 heading — two numbers contradicting each other on a banking home page.
 *
 * So the scope stops being decoration:
 *
 *  - **All accounts** → hero is the sum, the feed is cross-account, and the Balance column is
 *    NOT rendered, because a running balance across two ledgers is not a number.
 *  - **One account** → hero is that account, the feed is refetched with `AccountId`, and the
 *    Balance column appears — and now it reconciles, because it is one ledger.
 *
 * That is also why the chips are not merely a breakdown: selecting one changes what the page is
 * about, which is the only honest justification for giving it the most prominent control on screen.
 *
 * ## No greeting line
 *
 * "Good morning, {name}" was here and is deliberately gone. It is computed from the browser's
 * clock, so it can be wrong; but the real problem is that it earns nothing — the sidebar already
 * names the account holder and the hero already answers the question the page exists to answer.
 * It was the same defect as the "Here's an overview of your accounts" subtitle removed alongside
 * it, and keeping one while deleting the other was inconsistent rather than principled.
 *
 * ## What each source contributed
 *
 *  - **The current dashboard** — its four zones and its D22 state posture (accounts gate the page;
 *    feed and summary fail sectionally). It was right about the skeleton; what was wrong was the
 *    weighting and the density.
 *  - **A** — the chips as breakdown-and-switcher in one, and refusing a second row of cards.
 *  - **B** — transfer as the page's dominant verb rather than one tile among three.
 *  - **C** — the running balance and the column totals, now placed where they are true.
 *  - **The generated mockup** — the balanced two-column grid that fills the right rail all the way
 *    down (D left it empty below the fold), the explicit status pills, the "Across N accounts"
 *    line, the privacy toggle, and "Pending attention" as a task rather than a statistic.
 *
 * ## What was rejected, and why — each of these is a promise the code cannot keep
 *
 *  - **A Transfer CTA in the header, with the sidebar's removed.** `nav-ia.test.tsx` pins Transfer
 *    as an act — a button, never lit — on BOTH nav surfaces, on every route. Removing it from the
 *    sidebar breaks that, and leaves the bottom nav without it on a phone.
 *  - **A notification bell.** There is no notification system; `notification` appears in the app
 *    only as a Settings preference. A red dot over nothing is the same defect as a "Stay signed in"
 *    button that cannot extend the session.
 *  - **A month picker.** The summary query takes `{ fromDate, toDate }` and nothing else. A picker
 *    is a feature, not a finish.
 *  - **"Updated moments ago".** Needs a freshness story this app does not have.
 *  - **Clickable "Send again" chips.** `TransferPage` reads no incoming state, so they would open
 *    an empty form under a label promising the opposite. Recent recipients appear as INFORMATION
 *    inside the transfer card instead — true today, and one line in `TransferPage` away from
 *    becoming an action.
 *
 * ## The one thing the scope cannot reach, stated rather than hidden
 *
 * `getTransactionSummary` accepts `{ fromDate, toDate }` — there is no `AccountId`. So the month
 * summary stays all-accounts even when a single account is selected, and it SAYS SO. Making it
 * obey the scope needs a backend parameter; that is the follow-up that completes this design.
 */

const RECENT_PAGE_SIZE = 5;

/** `'all'` is a state, not an id, so a stale selection can only ever fall back to the total. */
type Scope = 'all' | string;

const useStyles = makeStyles({
  // ===== Layout =====
  // One column everywhere, becoming two only at xl. Deliberately NOT at lg: the shell hands the
  // page 705px at a 1024 viewport (1009 client - 240 sidebar - 64 padding), and splitting that
  // gives a ledger 460px wide next to a 225px rail — which is how the current dashboard ends up
  // overflowing its box by 86px between 1024 and 1094. There is no fixed-width track here at any
  // width; both are `minmax(0, fr)`.
  page: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
    padding: '16px',
    maxWidth: '1280px',

    [atMedia.md]: {
      padding: '24px',
    },
  },

  pageGrid: {
    [atMedia.xl]: {
      display: 'grid',
      gridTemplateColumns: 'minmax(0, 2fr) minmax(0, 1fr)',
      gridTemplateAreas: '"hero summary" "actions attention" "ledger support"',
      alignItems: 'start',
      columnGap: '20px',
    },
  },

  areaHero: { [atMedia.xl]: { gridArea: 'hero' } },
  areaSummary: { [atMedia.xl]: { gridArea: 'summary' } },
  areaActions: { [atMedia.xl]: { gridArea: 'actions' } },
  areaAttention: { [atMedia.xl]: { gridArea: 'attention' } },
  areaLedger: { [atMedia.xl]: { gridArea: 'ledger' } },
  areaSupport: { [atMedia.xl]: { gridArea: 'support' } },

  card: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${surfaces.border}`,
    borderRadius: '14px',
    padding: '20px',
    boxShadow: shadows.sm,
  },

  // ===== Zone 1: the balance, and the control that owns the page =====
  heroTop: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
  },

  heroLabel: {
    display: 'block',
    fontSize: '12px',
    fontWeight: 600,
    letterSpacing: '0.06em',
    textTransform: 'uppercase',
    color: colors.neutral[500],
  },

  // `clamp` rather than a breakpoint ramp: one declaration, no media query, and therefore no
  // exposure to Griffel emitting its buckets out of order (measured: 1024, 1366, 480, 640 — so an
  // `sm` rule can beat an `lg` one, which is why AuthLayout's lg padding has never applied).
  heroAmount: {
    display: 'block',
    fontSize: 'clamp(2.25rem, 1.6rem + 3.2vw, 3.25rem)',
    lineHeight: 1.05,
    fontWeight: 700,
    color: colors.neutral[900],
    fontVariantNumeric: 'tabular-nums',
    marginTop: '4px',
  },

  heroMeta: {
    display: 'block',
    marginTop: '6px',
    fontSize: '13px',
    color: colors.neutral[500],
  },

  // `subtle` rendered this as a nearly invisible glyph in the corner. A bordered target reads as
  // a control at a glance, which is what a control has to do.
  eyeButton: {
    minWidth: '40px',
    height: '40px',
    borderRadius: '10px',
    border: `1px solid ${surfaces.border}`,
    color: colors.neutral[600],
  },

  scopeRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '8px',
    marginTop: '16px',
  },

  scopeChip: {
    flex: '1 1 150px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    padding: '8px 12px',
    borderRadius: '10px',
    border: `1px solid ${surfaces.border}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    textAlign: 'left',
    ':hover': { backgroundColor: colors.neutral[50] },
    ':focus-visible': { outline: `2px solid ${colors.brand[60]}`, outlineOffset: '2px' },
  },

  scopeChipOn: {
    // `border`, not `borderColor`: Griffel rejects the four-sided shorthand (it types it as
    // `undefined`), which is the same class of rule that makes `margin` vs `marginTop` matter.
    border: `1px solid ${colors.brand[60]}`,
    backgroundColor: colors.semantic.info.light,
  },

  // One line, ellipsised. It was wrapping between the masked number and "· PRIMARY", which put a
  // dangling separator at the end of a line.
  scopeChipName: {
    fontSize: '12px',
    color: colors.neutral[600],
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    maxWidth: '100%',
  },

  attentionLink: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '4px',
    background: 'none',
    border: 'none',
    padding: 0,
    font: 'inherit',
    color: colors.brand[60],
    cursor: 'pointer',
    ':hover': { textDecoration: 'underline' },
    ':focus-visible': { outline: `2px solid ${colors.brand[60]}`, outlineOffset: '2px' },
  },

  // Fluent's `Text` renders inline regardless of its `as` prop, and the class is merged LAST —
  // which is the only reason this wins. Without it the heading and the link ran together as
  // "Need help?Get in touch".
  supportTitle: {
    display: 'block',
    width: '100%',
    marginBottom: '4px',
  },

  supportRow: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '12px',
  },

  supportIcon: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: '40px',
    height: '40px',
    flexShrink: 0,
    borderRadius: '50%',
    backgroundColor: colors.semantic.info.light,
    color: colors.brand[60],
  },

  supportLink: {
    display: 'inline-flex',
    width: 'fit-content',
    alignItems: 'center',
    gap: '4px',
    marginTop: '2px',
    fontSize: '14px',
    fontWeight: 600,
    color: colors.brand[60],
    textDecoration: 'none',
    ':hover': { textDecoration: 'underline' },
  },

  scopeChipAmount: {
    fontSize: '15px',
    fontWeight: 600,
    color: colors.neutral[800],
    fontVariantNumeric: 'tabular-nums',
  },

  primaryTag: {
    fontSize: '10px',
    fontWeight: 700,
    letterSpacing: '0.06em',
    color: colors.brand[60],
  },

  // ===== Zone 2: actions =====
  transferCard: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '16px',
    width: '100%',
    padding: '18px 20px',
    borderRadius: '14px',
    border: 'none',
    backgroundColor: colors.brandFill.rest,
    color: tokens.colorNeutralForegroundOnBrand,
    cursor: 'pointer',
    textAlign: 'left',
    ':hover': { backgroundColor: colors.brandFill.hover },
    ':focus-visible': { outline: `2px solid ${colors.neutral[900]}`, outlineOffset: '2px' },
  },

  transferTitle: {
    display: 'block',
    fontSize: '18px',
    fontWeight: 700,
    color: 'inherit',
  },

  transferSub: {
    display: 'block',
    fontSize: '13px',
    color: 'inherit',
    opacity: 0.85,
  },

  // `1fr 1fr` gave each tile half the measure — ~340px of card for an icon and one word. Capped,
  // so they stay hand-sized next to a transfer target that is meant to dominate.
  tileRow: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(120px, 200px))',
    gap: '12px',
    marginTop: '12px',
  },

  // ===== Zone 3: the ledger =====
  sectionHead: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '12px',
    marginBottom: '4px',
  },

  sectionTitle: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  thNum: { textAlign: 'right' },

  entryLink: {
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

  amountIn: { color: colors.semantic.success.dark, fontWeight: 600 },
  amountOut: { color: colors.neutral[800], fontWeight: 600 },

  // A reversed entry did not stand. Showing its amount as a plain debit asserts money left the
  // account when it came back; the strike-through says otherwise without inventing a sign.
  amountVoid: { textDecoration: 'line-through', color: colors.neutral[500] },

  // Never colour alone: the pill carries the word.
  pillDone: { backgroundColor: colors.semantic.success.light, color: colors.semantic.success.dark },

  // The running balance exists only when the scope is one account (see the file docblock) AND only
  // where there is room for a fifth column.

  tfootCell: {
    padding: '12px 8px',
    fontSize: '13px',
    fontWeight: 600,
    color: colors.neutral[700],
    borderTop: `2px solid ${surfaces.border}`,
  },

  // ===== Rail =====
  railRow: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '12px',
    padding: '8px 0',
  },

  railLabel: { fontSize: '14px', color: colors.neutral[600] },
  railValue: { fontSize: '14px', fontWeight: 600, fontVariantNumeric: 'tabular-nums' },
  railRule: { borderTop: `1px solid ${surfaces.border}`, marginTop: '4px', paddingTop: '4px' },

  muted: { fontSize: '13px', color: colors.neutral[500] },

  skeletonBar: {
    height: '1em',
    borderRadius: '6px',
    backgroundColor: colors.neutral[100],
  },
});

// ============================================
// Helpers
// ============================================

function recentRecipients(items: TransactionResponse[], limit: number): string[] {
  const out: string[] = [];
  for (const t of items) {
    if (t.type !== 'TransferOut' || !t.recipientAzureTag) continue;
    if (!out.includes(t.recipientAzureTag)) out.push(t.recipientAzureTag);
    if (out.length === limit) break;
  }
  return out;
}

// ============================================
// Component
// ============================================

export function DashboardPage() {
  const styles = useStyles();
  const cells = useTransactionCellStyles();
  const navigate = useNavigate();

  const [scope, setScope] = useState<Scope>('all');
  const [hidden, setHidden] = useState(false);
  const [depositOpen, setDepositOpen] = useState(false);
  const [withdrawOpen, setWithdrawOpen] = useState(false);

  const {
    data: accounts = [],
    isLoading: accountsLoading,
    error: accountsError,
    refetch: refetchAccounts,
  } = useGetAccountsQuery();
  const accountsProblem = accountsError as ApiProblem | undefined;

  // THE SPINE: the scope is a query argument, not a display filter. Selecting an account refetches
  // the feed with `AccountId`, which is what makes the running balance below a real ledger rather
  // than five rows borrowed from two accounts.
  const scopedAccountId = scope === 'all' ? undefined : scope;
  const {
    data: recent,
    isLoading: recentLoading,
    error: recentError,
    refetch: refetchRecent,
  } = useGetTransactionsQuery({
    page: 1,
    pageSize: RECENT_PAGE_SIZE,
    accountId: scopedAccountId,
  });

  const [monthWindow] = useState(() => ({ fromDate: startOfMonth(new Date()).toISOString() }));
  const {
    data: summary,
    isLoading: summaryLoading,
    error: summaryError,
    refetch: refetchSummary,
  } = useGetTransactionSummaryQuery(monthWindow);

  // A lone account is implicitly the scope. With one account "all accounts" IS that account, its
  // ledger is the only ledger, and the running balance therefore reconciles — so withholding the
  // Balance column would be the thesis refusing to apply itself in the one case where it is
  // trivially true. There are no chips to select in that case (there is nothing to choose between),
  // so nothing else would ever set it.
  const selected = accounts.length === 1 ? accounts[0] : accounts.find((a) => a.id === scope);
  const total = accounts.reduce((sum, a) => sum + a.balance, 0);
  const shownBalance = selected ? selected.balance : total;
  const entries = useMemo(() => recent?.data ?? [], [recent]);
  const recipients = useMemo(() => recentRecipients(entries, 3), [entries]);
  const pending = entries.filter((t) => t.status === 'Pending');
  const monthLabel = format(new Date(), 'MMMM');

  const legacyAccounts = accounts.map((a: AccountResponse) => ({
    id: a.id,
    name: a.name,
    accountNumber: maskAccountNumber(a.accountNumber),
    balance: a.balance,
  }));

  const money = (n: number) => (hidden ? '••••••' : formatCurrency(n));

  // ---- Page-gating states (D22), unchanged from the current dashboard ----
  if (accountsProblem) {
    return (
      <div className={styles.page}>
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
    );
  }

  if (!accountsLoading && accounts.length === 0) {
    return (
      <div className={styles.page}>
        <div className={styles.card}>
          <Text as="h1" className={styles.sectionTitle}>
            Welcome to AzureBank
          </Text>
          <Text className={styles.muted}>Open your first account to start banking.</Text>
          <div style={{ marginTop: 12 }}>
            <Button appearance="primary" onClick={() => navigate('/accounts')}>
              Create your first account
            </Button>
          </div>
        </div>
      </div>
    );
  }

  const scopeLabel = selected ? selected.name : `all ${accounts.length} accounts`;

  return (
    <div className={styles.page}>
      <div className={mergeClasses(styles.page, styles.pageGrid)} style={{ padding: 0 }}>
        {/* ===== Zone 1 — balance + the scope control ===== */}
        <section className={mergeClasses(styles.card, styles.areaHero)}>
          <div className={styles.heroTop}>
            <div>
              <Text className={styles.heroLabel}>Available balance</Text>
              {accountsLoading ? (
                <div className={styles.skeletonBar} style={{ width: 220, height: '2.6rem' }} />
              ) : (
                <Text as="h1" className={styles.heroAmount}>
                  {money(shownBalance)}
                </Text>
              )}
              <Text className={styles.heroMeta}>
                {selected
                  ? `${selected.name} · ${maskAccountNumber(selected.accountNumber)}`
                  : `Across ${accounts.length} account${accounts.length === 1 ? '' : 's'}`}
              </Text>
            </div>
            <Button
              className={styles.eyeButton}
              appearance="transparent"
              icon={hidden ? <EyeOff20Regular /> : <Eye20Regular />}
              aria-label={hidden ? 'Show balances' : 'Hide balances'}
              aria-pressed={hidden}
              onClick={() => setHidden((v) => !v)}
            />
          </div>

          {/* One account needs no selector: there is nothing to choose between. */}
          {accounts.length > 1 && (
            <div className={styles.scopeRow} role="group" aria-label="Account scope">
              <button
                type="button"
                className={mergeClasses(styles.scopeChip, scope === 'all' && styles.scopeChipOn)}
                aria-pressed={scope === 'all'}
                onClick={() => setScope('all')}
              >
                <span className={styles.scopeChipName}>All accounts</span>
                <span className={styles.scopeChipAmount}>{money(total)}</span>
              </button>
              {accounts.map((a) => (
                <button
                  key={a.id}
                  type="button"
                  className={mergeClasses(styles.scopeChip, scope === a.id && styles.scopeChipOn)}
                  aria-pressed={scope === a.id}
                  onClick={() => setScope(a.id)}
                >
                  <span className={styles.scopeChipName}>
                    {a.name} {maskAccountNumber(a.accountNumber)}
                    {a.isPrimary ? <span className={styles.primaryTag}> · PRIMARY</span> : null}
                  </span>
                  <span className={styles.scopeChipAmount}>{money(a.balance)}</span>
                </button>
              ))}
            </div>
          )}
        </section>

        {/* ===== Month summary — all-accounts, and it says so ===== */}
        <section className={mergeClasses(styles.card, styles.areaSummary)}>
          <Text className={styles.sectionTitle}>{monthLabel} so far</Text>
          {summaryError ? (
            <MessageBar intent="error">
              <MessageBarBody>Could not load this month.</MessageBarBody>
              <MessageBarActions>
                <Button appearance="transparent" onClick={() => void refetchSummary()}>
                  Retry
                </Button>
              </MessageBarActions>
            </MessageBar>
          ) : summaryLoading ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12, marginTop: 12 }}>
              <div className={styles.skeletonBar} />
              <div className={styles.skeletonBar} />
              <div className={styles.skeletonBar} />
            </div>
          ) : (
            <>
              <div className={styles.railRow}>
                <span className={styles.railLabel}>Money in</span>
                <span className={mergeClasses(styles.railValue, styles.amountIn)}>
                  +{money(summary?.totalIncome ?? 0)}
                </span>
              </div>
              <div className={styles.railRow}>
                <span className={styles.railLabel}>Money out</span>
                <span className={styles.railValue}>-{money(summary?.totalExpenses ?? 0)}</span>
              </div>
              <div className={mergeClasses(styles.railRow, styles.railRule)}>
                <span className={styles.railLabel}>Net change</span>
                <span className={styles.railValue}>
                  {(summary?.netChange ?? 0) >= 0 ? '+' : '-'}
                  {money(Math.abs(summary?.netChange ?? 0))}
                </span>
              </div>
              {/* The one place the scope cannot reach. Saying so beats a number that quietly
                  disagrees with the ledger beside it. */}
              {selected && (
                <Text className={styles.muted}>
                  Across all accounts — this month cannot yet be filtered to one.
                </Text>
              )}
            </>
          )}
        </section>

        {/* ===== Zone 2 — actions ===== */}
        <section className={styles.areaActions}>
          <button
            type="button"
            className={styles.transferCard}
            onClick={() => navigate('/transfer')}
          >
            <span>
              <Text className={styles.transferTitle}>Transfer</Text>
              <Text className={styles.transferSub}>
                {recipients.length > 0
                  ? `Recently sent to ${recipients.map((r) => `@${r}`).join(', ')}`
                  : 'Send to any @handle, or between your own accounts'}
              </Text>
            </span>
            <ArrowSwap24Regular />
          </button>
          <div className={styles.tileRow}>
            <QuickActionButton
              variant="deposit"
              label="Deposit"
              icon={<ArrowDownload24Regular />}
              onClick={() => setDepositOpen(true)}
            />
            <QuickActionButton
              variant="withdraw"
              label="Withdraw"
              icon={<ArrowUpload24Regular />}
              onClick={() => setWithdrawOpen(true)}
            />
          </div>
        </section>

        {/* ===== Pending attention — a task, not a statistic. Absent when there is nothing. ===== */}
        <section className={mergeClasses(styles.card, styles.areaAttention)}>
          <Text className={styles.sectionTitle}>Needs attention</Text>
          {pending.length === 0 ? (
            // Rendered rather than hidden. In a bank "nothing is pending" IS information — it is
            // reassurance — and a section that vanishes makes the page reshuffle itself between
            // one day and the next.
            <Text className={styles.muted}>Nothing needs your attention.</Text>
          ) : (
            pending.map((t) => (
              <div key={t.id} className={styles.railRow}>
                {/* A chevron and link colour, because an affordance that works but does not
                    advertise itself is worth almost nothing — and on touch there is no hover to
                    reveal it. */}
                <button
                  type="button"
                  className={styles.attentionLink}
                  onClick={() => navigate(`/transactions/${t.id}`)}
                >
                  {transactionLabel(t)} <ChevronRight16Regular />
                </button>
                <StatusPill status="Pending" />
              </div>
            ))
          )}
        </section>

        {/* ===== Zone 3 — the ledger ===== */}
        <section className={mergeClasses(styles.card, styles.areaLedger)}>
          <div className={styles.sectionHead}>
            <Text className={styles.sectionTitle}>
              Recent activity{selected ? ` · ${selected.name}` : ''}
            </Text>
            <Button appearance="transparent" onClick={() => navigate('/history')}>
              See all
            </Button>
          </div>

          {recentError ? (
            <MessageBar intent="error">
              <MessageBarBody>Could not load recent activity.</MessageBarBody>
              <MessageBarActions>
                <Button appearance="transparent" onClick={() => void refetchRecent()}>
                  Retry
                </Button>
              </MessageBarActions>
            </MessageBar>
          ) : (
            <TransactionTable>
              <TransactionHead showBalance={!!selected} />
              <tbody>
                {recentLoading
                  ? Array.from({ length: RECENT_PAGE_SIZE }, (_, i) => (
                      <TransactionRowSkeleton key={`sk-${i}`} showBalance={!!selected} />
                    ))
                  : entries.map((t) => (
                      <TransactionRow
                        key={t.id}
                        transaction={t}
                        showBalance={!!selected}
                        hidden={hidden}
                        onOpen={(id) => navigate(`/transactions/${id}`)}
                      />
                    ))}
                {!recentLoading && entries.length === 0 && (
                  <TransactionEmptyRow columns={selected ? 5 : 4}>
                    No transactions yet for {scopeLabel}.
                  </TransactionEmptyRow>
                )}
              </tbody>
              {!recentLoading && !summaryLoading && !summaryError && entries.length > 0 && (
                <tfoot>
                  <tr>
                    <td className={styles.tfootCell} colSpan={2}>
                      {monthLabel} so far
                      {pending.length > 0 ? ` · ${pending.length} pending` : ''}
                    </td>
                    <td className={mergeClasses(styles.tfootCell, cells.number)}>
                      {(summary?.netChange ?? 0) >= 0 ? '+' : '-'}
                      {money(Math.abs(summary?.netChange ?? 0))}
                    </td>
                    {selected && (
                      <td className={mergeClasses(styles.tfootCell, cells.balanceOnly)} />
                    )}
                    <td className={styles.tfootCell} />
                  </tr>
                </tfoot>
              )}
            </TransactionTable>
          )}
        </section>

        {/* ===== Support ===== */}
        <section className={mergeClasses(styles.card, styles.areaSupport)}>
          <div className={styles.supportRow}>
            <span className={styles.supportIcon} aria-hidden>
              <Headset24Regular />
            </span>
            <div>
              <Text className={mergeClasses(styles.sectionTitle, styles.supportTitle)}>
                Need help?
              </Text>
              {/* NOT "our support team is available 24/7". There is no team: this is a portfolio
                  project, and a line promising staffed support is the same defect as a button that
                  cannot extend a session. The link goes somewhere real and says what it is. */}
              <Link to="/about" className={styles.supportLink}>
                Get in touch <ChevronRight16Regular />
              </Link>
            </div>
          </div>
        </section>
      </div>

      {/* Mounted only while open, as the current dashboard does: these forms hold money state and
          a hidden mounted instance is a hidden mounted form. There is no `defaultAccountId` prop
          today, so the scope does NOT yet reach the dialogs — the one gap between this design and
          "everything obeys the scope", recorded rather than glossed. */}
      {depositOpen && (
        <DepositDialog
          isOpen={depositOpen}
          onClose={() => setDepositOpen(false)}
          accounts={legacyAccounts}
        />
      )}
      {withdrawOpen && (
        <WithdrawDialog
          isOpen={withdrawOpen}
          onClose={() => setWithdrawOpen(false)}
          accounts={legacyAccounts}
        />
      )}
    </div>
  );
}

export default DashboardPage;
