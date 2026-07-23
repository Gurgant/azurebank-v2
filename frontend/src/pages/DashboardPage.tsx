import { useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import {
  Button,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Spinner,
  Text,
} from '@fluentui/react-components';
import {
  ArrowDownload24Regular,
  ArrowUpload24Regular,
  ArrowSwap24Regular,
} from '@fluentui/react-icons';
import { format, startOfMonth } from 'date-fns';
import { colors, shadows, gradients } from '../theme/tokens';
import { useAppSelector } from '../app/hooks';
import { selectCurrentUser } from '../features/auth/authSlice';
import type { ApiProblem } from '../api/problemBaseQuery';
import type { AccountResponse, TransactionResponse } from '../features/api/apiSlice';
import {
  useGetAccountsQuery,
  useGetTransactionsQuery,
  useGetTransactionSummaryQuery,
} from '../features/api/apiSlice';
import { formatCurrency, maskAccountNumber } from '../utils/format';
import { TransactionItem } from '../components/shared/TransactionItem';
import { QuickActionButton } from '../components/shared/QuickActionButton';
import { DepositDialog, WithdrawDialog } from '../components';

// The money dialogs take this minimal shape (their RHF rewrite is the next PR).
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
  // ========== CONTAINER ==========
  container: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100vh',
    backgroundColor: colors.neutral[50],
  },

  // ========== MAIN CONTENT ==========
  mainContent: {
    flex: 1,
    padding: '16px',
    '@media (min-width: 1024px)': {
      display: 'flex',
      gap: '32px',
      padding: '32px',
    },
  },

  leftColumn: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
    '@media (min-width: 1024px)': {
      maxWidth: '800px',
      gap: '24px',
    },
  },

  rightColumn: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      flexDirection: 'column',
      width: '360px',
      gap: '24px',
      flexShrink: 0,
    },
  },

  // ========== GREETING ==========
  greetingSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  greetingTitle: {
    fontSize: '24px',
    fontWeight: 600,
    color: colors.neutral[800],
    '@media (min-width: 1024px)': {
      fontSize: '28px',
      fontWeight: 700,
    },
  },

  greetingSubtitle: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'block',
      fontSize: '16px',
      fontWeight: 400,
      color: colors.neutral[500],
    },
  },

  // ========== STATES ==========
  stateContainer: {
    display: 'flex',
    justifyContent: 'center',
    padding: '48px 0',
  },

  sectionState: {
    display: 'flex',
    justifyContent: 'center',
    padding: '24px 0',
  },

  emptyText: {
    fontSize: '14px',
    color: colors.neutral[500],
  },

  emptyStateCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    boxShadow: shadows.md,
    padding: '32px 24px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
  },

  // ========== BALANCE CARDS ==========
  balanceCardsRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    '@media (min-width: 1024px)': {
      flexDirection: 'row',
      gap: '24px',
    },
  },

  balanceCard: {
    flex: 1,
    minHeight: '180px',
    background: gradients.brand,
    borderRadius: '16px',
    boxShadow: '0px 8px 24px rgba(0, 109, 226, 0.3)',
    padding: '24px',
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'space-between',
  },

  accountInfo: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  accountName: {
    fontSize: '14px',
    fontWeight: 400,
    color: 'rgba(255, 255, 255, 0.8)',
  },

  accountNumber: {
    fontSize: '14px',
    fontWeight: 400,
    fontFamily: 'Consolas, monospace',
    color: 'rgba(255, 255, 255, 0.9)',
    letterSpacing: '0.05em',
  },

  balanceInfo: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  balanceLabel: {
    fontSize: '11px',
    fontWeight: 500,
    color: 'rgba(255, 255, 255, 0.7)',
    letterSpacing: '0.1em',
    textTransform: 'uppercase',
  },

  balanceAmount: {
    fontSize: '36px',
    fontWeight: 700,
    color: '#FFFFFF',
    letterSpacing: '-0.02em',
    fontVariantNumeric: 'tabular-nums',
  },

  // Secondary account card (desktop only)
  secondaryCard: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      flex: 1,
      flexDirection: 'column',
      justifyContent: 'space-between',
      minHeight: '180px',
      backgroundColor: '#FFFFFF',
      border: `1px solid ${colors.neutral[200]}`,
      borderRadius: '16px',
      padding: '24px',
    },
  },

  secondaryAccountName: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  secondaryAccountNumber: {
    fontSize: '12px',
    fontWeight: 400,
    fontFamily: 'Consolas, monospace',
    color: colors.neutral[500],
  },

  secondaryBalanceLabel: {
    fontSize: '12px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  secondaryBalanceAmount: {
    fontSize: '32px',
    fontWeight: 700,
    color: colors.neutral[800],
    fontVariantNumeric: 'tabular-nums',
  },

  // ========== QUICK ACTIONS ==========
  quickActions: {
    display: 'flex',
    gap: '12px',
    '@media (min-width: 1024px)': {
      gap: '16px',
    },
  },

  // ========== TRANSACTIONS SECTION ==========
  transactionsSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    '@media (min-width: 1024px)': {
      backgroundColor: '#FFFFFF',
      borderRadius: '16px',
      boxShadow: shadows.md,
      padding: '24px',
      gap: '20px',
    },
  },

  sectionHeader: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
  },

  sectionTitle: {
    fontSize: '18px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  viewAllLink: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.brand[60],
    cursor: 'pointer',
    textDecoration: 'none',
    ':hover': {
      textDecoration: 'underline',
    },
  },

  transactionsList: {
    backgroundColor: '#FFFFFF',
    borderRadius: '12px',
    overflow: 'hidden',
    '@media (min-width: 1024px)': {
      backgroundColor: 'transparent',
      borderRadius: 0,
      display: 'flex',
      flexDirection: 'column',
      gap: '12px',
    },
  },

  // ========== SIDEBAR CARDS (Desktop) ==========
  summaryCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    boxShadow: shadows.md,
    padding: '24px',
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
  },

  summaryTitle: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  summaryRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
  },

  summaryLabel: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  summaryValue: {
    fontSize: '14px',
    fontWeight: 600,
    color: colors.neutral[800],
    fontVariantNumeric: 'tabular-nums',
  },

  summaryValuePositive: {
    color: colors.semantic.success.main,
  },

  summaryValueNegative: {
    color: colors.semantic.error.main,
  },

  summaryDivider: {
    width: '100%',
    height: '1px',
    backgroundColor: colors.neutral[200],
  },

  summaryErrorText: {
    fontSize: '13px',
    color: colors.semantic.error.main,
  },

  helpCard: {
    background: 'linear-gradient(135deg, #F0F6FE 0%, #E6F0FC 100%)',
    borderRadius: '16px',
    padding: '24px',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },

  helpTitle: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  helpText: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
    lineHeight: 1.5,
  },
});

// ============================================
// HELPER FUNCTIONS
// ============================================

function getGreeting(): string {
  const hour = new Date().getHours();
  if (hour < 12) return 'Good morning';
  if (hour < 18) return 'Good afternoon';
  return 'Good evening';
}

/** Transfer rows lead with the counterparty handle; other types fall back to description. */
function counterpartyLabel(transaction: TransactionResponse): string | undefined {
  if (transaction.recipientAzureTag) {
    return `To @${transaction.recipientAzureTag}`;
  }
  if (transaction.senderAzureTag) {
    return `From @${transaction.senderAzureTag}`;
  }
  return undefined;
}

/** Signed EUR for the monthly summary rows: +€x / -€x (net keeps its real sign). */
function signedCurrency(amount: number, positiveSign: '+' | '-'): string {
  return `${positiveSign}${formatCurrency(Math.abs(amount))}`;
}

// ============================================
// COMPONENT
// ============================================

export function DashboardPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const user = useAppSelector(selectCurrentUser);

  // The landing page on REAL data: same D22 posture as AccountsPage — accounts gate the
  // page (loading spinner / error bar / empty CTA), the feed and the monthly summary are
  // SECTIONAL states so a partial failure never blanks the whole dashboard.
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
  } = useGetTransactionsQuery({ page: 1, pageSize: 5 });

  // Month start computed ONCE per mount (local month). toDate is deliberately NOT sent:
  // the server defaults it to "now" per request, so the LIST-tag refetch after a money
  // mutation includes the very transaction that triggered it (a frozen toDate would not).
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

  const primaryAccount = accounts.find((account) => account.isPrimary) ?? accounts[0];
  const secondaryAccount = accounts.find((account) => account.id !== primaryAccount?.id);
  const totalBalance = accounts.reduce((sum, account) => sum + account.balance, 0);

  // Adapter for the money dialogs (RHF rewrite = next PR): masked number, minimal shape.
  const toLegacy = (account: AccountResponse): LegacyDialogAccount => ({
    id: account.id,
    name: account.name,
    accountNumber: maskAccountNumber(account.accountNumber),
    balance: account.balance,
  });
  const legacyAccounts = accounts.map(toLegacy);

  const handleTransactionClick = (id: string) => {
    navigate(`/transactions/${id}`);
  };

  const monthLabel = format(new Date(), 'MMMM');
  const recentTransactions = recent?.data ?? [];

  return (
    <div className={styles.container}>
      {/* Main Content — the app shell (nav/header) is provided by ProtectedShell */}
      <div className={styles.mainContent}>
        {/* Left Column */}
        <div className={styles.leftColumn}>
          {/* Greeting */}
          <div className={styles.greetingSection}>
            <Text className={styles.greetingTitle}>
              {getGreeting()}
              {user ? `, ${user.firstName}` : ''}
            </Text>
            <Text className={styles.greetingSubtitle}>Here's an overview of your accounts</Text>
          </div>

          {/* Page-gating states (D22): spinner / error bar / empty CTA / content */}
          {accountsLoading && (
            <div className={styles.stateContainer}>
              <Spinner size="large" aria-label="Loading your accounts" />
            </div>
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

          {!accountsLoading && !accountsProblem && accounts.length === 0 && (
            <div className={styles.emptyStateCard}>
              <Text className={styles.sectionTitle}>Welcome to AzureBank</Text>
              <Text className={styles.emptyText}>Open your first account to start banking.</Text>
              <Button appearance="primary" onClick={() => navigate('/accounts')}>
                Create your first account
              </Button>
            </div>
          )}

          {!accountsLoading && !accountsProblem && primaryAccount && (
            <>
              {/* Balance Cards */}
              <div className={styles.balanceCardsRow}>
                {/* Primary Account Card */}
                <div className={styles.balanceCard}>
                  <div className={styles.accountInfo}>
                    <Text className={styles.accountName}>{primaryAccount.name}</Text>
                    <Text className={styles.accountNumber}>
                      {maskAccountNumber(primaryAccount.accountNumber)}
                    </Text>
                  </div>
                  <div className={styles.balanceInfo}>
                    <Text className={styles.balanceLabel}>Available Balance</Text>
                    <Text className={styles.balanceAmount}>
                      {formatCurrency(primaryAccount.balance)}
                    </Text>
                  </div>
                </div>

                {/* Secondary Account Card (Desktop Only) */}
                {secondaryAccount && (
                  <div className={styles.secondaryCard}>
                    <div className={styles.accountInfo}>
                      <Text className={styles.secondaryAccountName}>{secondaryAccount.name}</Text>
                      <Text className={styles.secondaryAccountNumber}>
                        {maskAccountNumber(secondaryAccount.accountNumber)}
                      </Text>
                    </div>
                    <div className={styles.balanceInfo}>
                      <Text className={styles.secondaryBalanceLabel}>Available Balance</Text>
                      <Text className={styles.secondaryBalanceAmount}>
                        {formatCurrency(secondaryAccount.balance)}
                      </Text>
                    </div>
                  </div>
                )}
              </div>

              {/* Quick Actions — Deposit/Withdraw open the REAL dialogs over all accounts */}
              <div className={styles.quickActions}>
                <QuickActionButton
                  variant="deposit"
                  label="Deposit"
                  icon={<ArrowDownload24Regular />}
                  onClick={() => setIsDepositOpen(true)}
                />
                <QuickActionButton
                  variant="withdraw"
                  label="Withdraw"
                  icon={<ArrowUpload24Regular />}
                  onClick={() => setIsWithdrawOpen(true)}
                />
                <QuickActionButton
                  variant="transfer"
                  label="Transfer"
                  icon={<ArrowSwap24Regular />}
                  onClick={() => navigate('/transfer')}
                  highlighted
                />
              </div>

              {/* Recent Transactions — sectional state, real ids navigate to the detail */}
              <div className={styles.transactionsSection}>
                <div className={styles.sectionHeader}>
                  <Text className={styles.sectionTitle}>Recent Transactions</Text>
                  <Link to="/history" className={styles.viewAllLink}>
                    See all
                  </Link>
                </div>

                {recentLoading && (
                  <div className={styles.sectionState}>
                    <Spinner size="small" aria-label="Loading recent transactions" />
                  </div>
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
                  <div className={styles.sectionState}>
                    <Text className={styles.emptyText}>No transactions yet.</Text>
                  </div>
                )}

                {!recentLoading && !recentError && recentTransactions.length > 0 && (
                  <div className={styles.transactionsList}>
                    {recentTransactions.map((transaction) => (
                      <TransactionItem
                        key={transaction.id}
                        id={transaction.id}
                        type={transaction.type}
                        description={transaction.description ?? ''}
                        counterparty={counterpartyLabel(transaction)}
                        amount={transaction.amount}
                        date={transaction.createdAt}
                        onClick={handleTransactionClick}
                      />
                    ))}
                  </div>
                )}
              </div>
            </>
          )}
        </div>

        {/* Right Column (Desktop Only) */}
        {!accountsLoading && !accountsProblem && accounts.length > 0 && (
          <div className={styles.rightColumn}>
            {/* Monthly Summary — the server-side aggregate; '—' while loading, inline retry */}
            <div className={styles.summaryCard}>
              <Text className={styles.summaryTitle}>{monthLabel} Summary</Text>
              {summaryError !== undefined ? (
                <>
                  <Text className={styles.summaryErrorText}>Could not load the summary.</Text>
                  <Button appearance="transparent" onClick={() => void refetchSummary()}>
                    Retry
                  </Button>
                </>
              ) : (
                <>
                  <div className={styles.summaryRow}>
                    <span className={styles.summaryLabel}>Total Income</span>
                    <span className={`${styles.summaryValue} ${styles.summaryValuePositive}`}>
                      {summaryLoading || !summary ? '—' : signedCurrency(summary.totalIncome, '+')}
                    </span>
                  </div>
                  <div className={styles.summaryRow}>
                    <span className={styles.summaryLabel}>Total Expenses</span>
                    <span className={`${styles.summaryValue} ${styles.summaryValueNegative}`}>
                      {summaryLoading || !summary
                        ? '—'
                        : signedCurrency(summary.totalExpenses, '-')}
                    </span>
                  </div>
                  <div className={styles.summaryDivider} />
                  <div className={styles.summaryRow}>
                    <span className={styles.summaryLabel}>Net Change</span>
                    <span
                      className={`${styles.summaryValue} ${
                        summary && summary.netChange < 0
                          ? styles.summaryValueNegative
                          : styles.summaryValuePositive
                      }`}
                    >
                      {summaryLoading || !summary
                        ? '—'
                        : signedCurrency(summary.netChange, summary.netChange < 0 ? '-' : '+')}
                    </span>
                  </div>
                </>
              )}
            </div>

            {/* Accounts Overview — real list math + the summary's pending count */}
            <div className={styles.summaryCard}>
              <Text className={styles.summaryTitle}>Accounts Overview</Text>
              <div className={styles.summaryRow}>
                <span className={styles.summaryLabel}>Total Accounts</span>
                <span className={styles.summaryValue}>{accounts.length}</span>
              </div>
              <div className={styles.summaryRow}>
                <span className={styles.summaryLabel}>Total Balance</span>
                <span className={styles.summaryValue}>{formatCurrency(totalBalance)}</span>
              </div>
              <div className={styles.summaryRow}>
                <span className={styles.summaryLabel}>Pending Transactions</span>
                <span className={styles.summaryValue}>
                  {summaryError !== undefined || summaryLoading || !summary
                    ? '—'
                    : summary.pendingCount}
                </span>
              </div>
            </div>

            {/* Help Card — informational copy only (no dead controls) */}
            <div className={styles.helpCard}>
              <Text className={styles.helpTitle}>Need Help?</Text>
              <Text className={styles.helpText}>
                Our support team is available 24/7 to assist you with any questions.
              </Text>
            </div>
          </div>
        )}
      </div>

      {/* Money dialogs — mount-on-open (fresh presence state per open), full account list.
          Their internals are untouched here: the RHF+Zod rewrite is the next, dedicated PR. */}
      {isDepositOpen && (
        <DepositDialog isOpen onClose={() => setIsDepositOpen(false)} accounts={legacyAccounts} />
      )}

      {isWithdrawOpen && (
        <WithdrawDialog isOpen onClose={() => setIsWithdrawOpen(false)} accounts={legacyAccounts} />
      )}
    </div>
  );
}

export default DashboardPage;
