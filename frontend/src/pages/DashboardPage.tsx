import { useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import { makeStyles, Text, Button, Spinner } from '@fluentui/react-components';
import {
  ArrowDownload24Regular,
  ArrowUpload24Regular,
  ArrowSwap24Regular,
} from '@fluentui/react-icons';
import { colors, shadows, gradients } from '../theme/tokens';
import { useAppSelector } from '../app/hooks';
import { selectCurrentUser } from '../features/auth/authSlice';
import { TransactionItem, type TransactionType } from '../components/shared/TransactionItem';
import { QuickActionButton } from '../components/shared/QuickActionButton';

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  // ========== CONTAINER ==========
  container: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100vh',
    backgroundColor: '#F7F8FA',
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

  // Desktop filters
  filterRow: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      gap: '12px',
    },
  },

  filterButton: {
    height: '36px',
    padding: '0 16px',
    borderRadius: '8px',
    backgroundColor: colors.neutral[100],
    border: 'none',
    cursor: 'pointer',
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
    ':hover': {
      backgroundColor: colors.neutral[200],
    },
  },

  filterButtonActive: {
    backgroundColor: colors.brand[120],
    color: colors.brand[60],
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

  // ========== LOADING STATE ==========
  loadingContainer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '400px',
  },
});

// ============================================
// MOCK DATA (to be replaced with RTK Query)
// ============================================

const mockAccounts = [
  {
    id: '1',
    name: 'Primary Account',
    accountNumber: '**** **** **** 4521',
    balance: 12450.0,
    type: 'checking',
  },
  {
    id: '2',
    name: 'Savings Account',
    accountNumber: '**** **** **** 5678',
    balance: 8320.0,
    type: 'savings',
  },
];

const mockTransactions: Array<{
  id: string;
  type: TransactionType;
  description: string;
  amount: number;
  date: Date;
}> = [
  {
    id: '1',
    type: 'deposit',
    description: 'Salary Deposit',
    amount: 3500.0,
    date: new Date('2025-12-15'),
  },
  {
    id: '2',
    type: 'withdrawal',
    description: 'ATM Withdrawal',
    amount: 200.0,
    date: new Date('2025-12-14'),
  },
  {
    id: '3',
    type: 'transfer-out',
    description: 'Transfer to Savings',
    amount: 500.0,
    date: new Date('2025-12-12'),
  },
];

// ============================================
// HELPER FUNCTIONS
// ============================================

function formatCurrency(amount: number): string {
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
  }).format(amount);
}

function getGreeting(): string {
  const hour = new Date().getHours();
  if (hour < 12) return 'Good morning';
  if (hour < 18) return 'Good afternoon';
  return 'Good evening';
}

// ============================================
// COMPONENT
// ============================================

export function DashboardPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const user = useAppSelector(selectCurrentUser);
  const [activeFilter, setActiveFilter] = useState('all');
  const [isLoading] = useState(false);

  // Handlers
  const handleDeposit = () => {
    // Will open deposit dialog
    console.log('Open deposit');
  };

  const handleWithdraw = () => {
    // Will open withdraw dialog
    console.log('Open withdraw');
  };

  const handleTransfer = () => {
    navigate('/transfer');
  };

  const handleTransactionClick = (id: string) => {
    navigate(`/transactions/${id}`);
  };

  if (isLoading) {
    return (
      <div className={styles.container}>
        <div className={styles.loadingContainer}>
          <Spinner size="large" />
        </div>
      </div>
    );
  }

  const primaryAccount = mockAccounts[0];
  const secondaryAccount = mockAccounts[1];

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

          {/* Balance Cards */}
          <div className={styles.balanceCardsRow}>
            {/* Primary Account Card */}
            <div className={styles.balanceCard}>
              <div className={styles.accountInfo}>
                <Text className={styles.accountName}>{primaryAccount.name}</Text>
                <Text className={styles.accountNumber}>{primaryAccount.accountNumber}</Text>
              </div>
              <div className={styles.balanceInfo}>
                <Text className={styles.balanceLabel}>Available Balance</Text>
                <Text className={styles.balanceAmount}>
                  {formatCurrency(primaryAccount.balance)}
                </Text>
              </div>
            </div>

            {/* Secondary Account Card (Desktop Only) */}
            <div className={styles.secondaryCard}>
              <div className={styles.accountInfo}>
                <Text className={styles.secondaryAccountName}>{secondaryAccount.name}</Text>
                <Text className={styles.secondaryAccountNumber}>
                  {secondaryAccount.accountNumber}
                </Text>
              </div>
              <div className={styles.balanceInfo}>
                <Text className={styles.secondaryBalanceLabel}>Available Balance</Text>
                <Text className={styles.secondaryBalanceAmount}>
                  {formatCurrency(secondaryAccount.balance)}
                </Text>
              </div>
            </div>
          </div>

          {/* Quick Actions */}
          <div className={styles.quickActions}>
            <QuickActionButton
              variant="deposit"
              label="Deposit"
              icon={<ArrowDownload24Regular />}
              onClick={handleDeposit}
            />
            <QuickActionButton
              variant="withdraw"
              label="Withdraw"
              icon={<ArrowUpload24Regular />}
              onClick={handleWithdraw}
            />
            <QuickActionButton
              variant="transfer"
              label="Transfer"
              icon={<ArrowSwap24Regular />}
              onClick={handleTransfer}
              highlighted
            />
          </div>

          {/* Transactions Section */}
          <div className={styles.transactionsSection}>
            <div className={styles.sectionHeader}>
              <Text className={styles.sectionTitle}>Recent Transactions</Text>
              <Link to="/history" className={styles.viewAllLink}>
                See all
              </Link>
            </div>

            {/* Desktop Filter Row */}
            <div className={styles.filterRow}>
              {['all', 'deposits', 'withdrawals', 'transfers'].map((filter) => (
                <button
                  key={filter}
                  className={`${styles.filterButton} ${activeFilter === filter ? styles.filterButtonActive : ''}`}
                  onClick={() => setActiveFilter(filter)}
                >
                  {filter.charAt(0).toUpperCase() + filter.slice(1)}
                </button>
              ))}
            </div>

            {/* Transactions List */}
            <div className={styles.transactionsList}>
              {mockTransactions.map((transaction) => (
                <TransactionItem
                  key={transaction.id}
                  id={transaction.id}
                  type={transaction.type}
                  description={transaction.description}
                  amount={transaction.amount}
                  date={transaction.date}
                  onClick={() => handleTransactionClick(transaction.id)}
                />
              ))}
            </div>
          </div>
        </div>

        {/* Right Column (Desktop Only) */}
        <div className={styles.rightColumn}>
          {/* Monthly Summary */}
          <div className={styles.summaryCard}>
            <Text className={styles.summaryTitle}>Monthly Summary</Text>
            <div className={styles.summaryRow}>
              <span className={styles.summaryLabel}>Total Income</span>
              <span className={`${styles.summaryValue} ${styles.summaryValuePositive}`}>
                +$5,089.99
              </span>
            </div>
            <div className={styles.summaryRow}>
              <span className={styles.summaryLabel}>Total Expenses</span>
              <span className={`${styles.summaryValue} ${styles.summaryValueNegative}`}>
                -$1,234.56
              </span>
            </div>
            <div className={styles.summaryDivider} />
            <div className={styles.summaryRow}>
              <span className={styles.summaryLabel}>Net Change</span>
              <span className={`${styles.summaryValue} ${styles.summaryValuePositive}`}>
                +$3,855.43
              </span>
            </div>
          </div>

          {/* Accounts Overview */}
          <div className={styles.summaryCard}>
            <Text className={styles.summaryTitle}>Accounts Overview</Text>
            <div className={styles.summaryRow}>
              <span className={styles.summaryLabel}>Total Accounts</span>
              <span className={styles.summaryValue}>{mockAccounts.length}</span>
            </div>
            <div className={styles.summaryRow}>
              <span className={styles.summaryLabel}>Total Balance</span>
              <span className={styles.summaryValue}>
                {formatCurrency(mockAccounts.reduce((sum, acc) => sum + acc.balance, 0))}
              </span>
            </div>
            <div className={styles.summaryRow}>
              <span className={styles.summaryLabel}>Pending Transactions</span>
              <span className={styles.summaryValue}>0</span>
            </div>
          </div>

          {/* Help Card */}
          <div className={styles.helpCard}>
            <Text className={styles.helpTitle}>Need Help?</Text>
            <Text className={styles.helpText}>
              Our support team is available 24/7 to assist you with any questions.
            </Text>
            <Button appearance="primary" size="medium">
              Contact Support
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}

export default DashboardPage;
