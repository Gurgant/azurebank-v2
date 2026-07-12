import { useState, useMemo } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import {
  makeStyles,
  Text,
} from '@fluentui/react-components';
import {
  ChevronLeft24Regular,
  Filter24Regular,
  Home24Regular,
  Wallet24Regular,
  ArrowSwap24Regular,
  History24Filled,
  ArrowDownload24Regular,
  ArrowUpload24Regular,
  ArrowRight24Regular,
  ArrowLeft24Regular,
} from '@fluentui/react-icons';
import { colors, gradients, transitions } from '../theme/tokens';

// ============================================
// TYPES
// ============================================

type TransactionType = 'deposit' | 'withdrawal' | 'transfer_out' | 'transfer_in';
type FilterType = 'all' | 'deposits' | 'withdrawals' | 'transfers';

interface Transaction {
  id: string;
  type: TransactionType;
  description: string;
  account: string;
  amount: number;
  date: string;
  time: string;
}

interface GroupedTransactions {
  date: string;
  transactions: Transaction[];
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100vh',
    backgroundColor: '#F7F8FA',
  },

  // ========== HEADER ==========
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    height: '56px',
    padding: '0 16px',
    backgroundColor: '#FFFFFF',
    borderBottom: `1px solid ${colors.neutral[200]}`,
  },

  headerButton: {
    width: '40px',
    height: '40px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    borderRadius: '8px',
    color: colors.neutral[800],
    ':hover': {
      backgroundColor: colors.neutral[100],
    },
  },

  headerTitle: {
    fontSize: '18px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  filterButton: {
    color: colors.brand[60],
  },

  // ========== CONTENT ==========
  content: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
    paddingBottom: '72px', // Bottom nav space
  },

  // ========== FILTER TABS ==========
  filterTabs: {
    display: 'flex',
    padding: '12px 16px',
    gap: '8px',
    backgroundColor: '#FFFFFF',
    borderBottom: `1px solid ${colors.neutral[200]}`,
    overflowX: 'auto',
    flexShrink: 0,
  },

  filterTab: {
    height: '36px',
    padding: '0 16px',
    backgroundColor: colors.neutral[100],
    borderRadius: '18px',
    border: 'none',
    cursor: 'pointer',
    whiteSpace: 'nowrap',
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.neutral[200],
    },
  },

  filterTabActive: {
    backgroundColor: colors.brand[60],
    color: '#FFFFFF',
    ':hover': {
      backgroundColor: colors.brand[40],
    },
  },

  // ========== SUMMARY CARD ==========
  summaryCard: {
    margin: '16px',
    padding: '16px',
    background: gradients.brand,
    borderRadius: '12px',
    display: 'flex',
    justifyContent: 'space-around',
    flexShrink: 0,
  },

  summaryItem: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '4px',
  },

  summaryLabel: {
    fontSize: '12px',
    fontWeight: 400,
    color: 'rgba(255, 255, 255, 0.8)',
  },

  summaryValue: {
    fontSize: '16px',
    fontWeight: 700,
    color: '#FFFFFF',
  },

  summaryValuePositive: {
    color: '#86EFAC',
  },

  summaryValueNegative: {
    color: '#FCA5A5',
  },

  // ========== TRANSACTION LIST ==========
  transactionList: {
    flex: 1,
    backgroundColor: '#FFFFFF',
    overflowY: 'auto',
  },

  // ========== DATE HEADER ==========
  dateHeader: {
    padding: '16px',
    backgroundColor: '#F7F8FA',
  },

  dateText: {
    fontSize: '14px',
    fontWeight: 600,
    color: colors.neutral[500],
  },

  // ========== TRANSACTION ITEM ==========
  transactionItem: {
    display: 'flex',
    alignItems: 'center',
    padding: '16px',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    gap: '12px',
    cursor: 'pointer',
    transition: `background ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  transactionIcon: {
    width: '44px',
    height: '44px',
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
  },

  iconDeposit: {
    backgroundColor: colors.semantic.success.light,
    color: colors.semantic.success.main,
  },

  iconWithdrawal: {
    backgroundColor: colors.semantic.error.light,
    color: colors.semantic.error.main,
  },

  iconTransferOut: {
    backgroundColor: '#FEF3E2',
    color: '#F59E0B',
  },

  iconTransferIn: {
    backgroundColor: '#E0F2FE',
    color: '#0EA5E9',
  },

  transactionDetails: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    minWidth: 0,
  },

  transactionTitle: {
    fontSize: '15px',
    fontWeight: 500,
    color: colors.neutral[800],
    whiteSpace: 'nowrap',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },

  transactionSubtitle: {
    fontSize: '13px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  transactionRight: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: '4px',
  },

  transactionAmount: {
    fontSize: '16px',
    fontWeight: 600,
    fontFamily: 'Consolas, monospace',
  },

  amountPositive: {
    color: colors.semantic.success.main,
  },

  amountNegative: {
    color: colors.semantic.error.main,
  },

  amountTransfer: {
    color: '#B45309',
  },

  transactionTime: {
    fontSize: '12px',
    fontWeight: 400,
    color: colors.neutral[400],
  },

  // ========== EMPTY STATE ==========
  emptyState: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '48px 24px',
    gap: '16px',
  },

  emptyIcon: {
    width: '80px',
    height: '80px',
    borderRadius: '50%',
    backgroundColor: colors.neutral[100],
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.neutral[400],
  },

  emptyTitle: {
    fontSize: '18px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  emptySubtitle: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
    textAlign: 'center',
  },

  // ========== BOTTOM NAV ==========
  bottomNav: {
    position: 'fixed',
    bottom: 0,
    left: 0,
    right: 0,
    height: '72px',
    backgroundColor: '#FFFFFF',
    borderTop: `1px solid ${colors.neutral[200]}`,
    boxShadow: '0px -2px 4px rgba(0, 0, 0, 0.05)',
    display: 'flex',
    justifyContent: 'space-around',
    alignItems: 'center',
    paddingBottom: '20px',
  },

  navButton: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '4px',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    padding: '8px 16px',
    textDecoration: 'none',
  },

  navIconActive: {
    color: colors.brand[60],
  },

  navIconInactive: {
    color: colors.neutral[500],
  },

  navLabelText: {
    fontSize: '12px',
    fontWeight: 500,
  },

  navLabelActive: {
    color: colors.brand[60],
  },

  navLabelInactive: {
    color: colors.neutral[500],
  },
});

// ============================================
// MOCK DATA
// ============================================

const mockTransactions: Transaction[] = [
  { id: '1', type: 'deposit', description: 'Salary Deposit', account: 'Main Account', amount: 5000, date: 'January 1, 2026', time: '10:30 AM' },
  { id: '2', type: 'withdrawal', description: 'ATM Withdrawal', account: 'Main Account', amount: -200, date: 'January 1, 2026', time: '2:15 PM' },
  { id: '3', type: 'transfer_out', description: 'Transfer to Sarah', account: 'Main Account', amount: -350, date: 'January 1, 2026', time: '4:45 PM' },
  { id: '4', type: 'transfer_in', description: 'Transfer from Mike', account: 'Main Account', amount: 89.99, date: 'December 31, 2025', time: '11:20 AM' },
  { id: '5', type: 'withdrawal', description: 'Online Purchase', account: 'Main Account', amount: -156.50, date: 'December 31, 2025', time: '3:30 PM' },
  { id: '6', type: 'deposit', description: 'Refund - Amazon', account: 'Main Account', amount: 45, date: 'December 31, 2025', time: '5:00 PM' },
  { id: '7', type: 'deposit', description: 'Freelance Payment', account: 'Main Account', amount: 1250, date: 'December 30, 2025', time: '9:15 AM' },
  { id: '8', type: 'transfer_out', description: 'Rent Payment', account: 'Main Account', amount: -1500, date: 'December 30, 2025', time: '10:00 AM' },
];

// ============================================
// HELPER FUNCTIONS
// ============================================

function formatCurrency(amount: number): string {
  const prefix = amount >= 0 ? '+' : '';
  return prefix + new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
  }).format(Math.abs(amount));
}

function getTransactionIcon(type: TransactionType) {
  switch (type) {
    case 'deposit':
      return ArrowDownload24Regular;
    case 'withdrawal':
      return ArrowUpload24Regular;
    case 'transfer_out':
      return ArrowRight24Regular;
    case 'transfer_in':
      return ArrowLeft24Regular;
  }
}

function groupTransactionsByDate(transactions: Transaction[]): GroupedTransactions[] {
  const groups: { [key: string]: Transaction[] } = {};

  transactions.forEach((transaction) => {
    if (!groups[transaction.date]) {
      groups[transaction.date] = [];
    }
    groups[transaction.date].push(transaction);
  });

  return Object.keys(groups).map((date) => ({
    date,
    transactions: groups[date],
  }));
}

// ============================================
// COMPONENT
// ============================================

export function HistoryPage() {
  const styles = useStyles();
  const navigate = useNavigate();

  const [activeFilter, setActiveFilter] = useState<FilterType>('all');

  // Filter transactions
  const filteredTransactions = useMemo(() => {
    switch (activeFilter) {
      case 'deposits':
        return mockTransactions.filter((t) => t.type === 'deposit');
      case 'withdrawals':
        return mockTransactions.filter((t) => t.type === 'withdrawal');
      case 'transfers':
        return mockTransactions.filter((t) => t.type === 'transfer_out' || t.type === 'transfer_in');
      default:
        return mockTransactions;
    }
  }, [activeFilter]);

  // Group by date
  const groupedTransactions = useMemo(
    () => groupTransactionsByDate(filteredTransactions),
    [filteredTransactions]
  );

  // Calculate summary
  const summary = useMemo(() => {
    const income = mockTransactions.filter((t) => t.amount > 0).reduce((sum, t) => sum + t.amount, 0);
    const expenses = mockTransactions.filter((t) => t.amount < 0).reduce((sum, t) => sum + Math.abs(t.amount), 0);
    return { income, expenses, net: income - expenses };
  }, []);

  const handleTransactionClick = (id: string) => {
    navigate(`/transactions/${id}`);
  };

  const handleBack = () => {
    navigate(-1);
  };

  const getIconStyle = (type: TransactionType) => {
    switch (type) {
      case 'deposit':
        return styles.iconDeposit;
      case 'withdrawal':
        return styles.iconWithdrawal;
      case 'transfer_out':
        return styles.iconTransferOut;
      case 'transfer_in':
        return styles.iconTransferIn;
    }
  };

  const getAmountStyle = (type: TransactionType, amount: number) => {
    if (type === 'transfer_out') return styles.amountTransfer;
    return amount >= 0 ? styles.amountPositive : styles.amountNegative;
  };

  return (
    <div className={styles.container}>
      {/* Header */}
      <div className={styles.header}>
        <button className={styles.headerButton} onClick={handleBack}>
          <ChevronLeft24Regular />
        </button>
        <Text className={styles.headerTitle}>Transaction History</Text>
        <button className={`${styles.headerButton} ${styles.filterButton}`}>
          <Filter24Regular />
        </button>
      </div>

      {/* Content */}
      <div className={styles.content}>
        {/* Filter Tabs */}
        <div className={styles.filterTabs}>
          {(['all', 'deposits', 'withdrawals', 'transfers'] as FilterType[]).map((filter) => (
            <button
              key={filter}
              className={`${styles.filterTab} ${activeFilter === filter ? styles.filterTabActive : ''}`}
              onClick={() => setActiveFilter(filter)}
            >
              {filter.charAt(0).toUpperCase() + filter.slice(1)}
            </button>
          ))}
        </div>

        {/* Summary Card */}
        <div className={styles.summaryCard}>
          <div className={styles.summaryItem}>
            <span className={styles.summaryLabel}>Income</span>
            <span className={`${styles.summaryValue} ${styles.summaryValuePositive}`}>
              +${summary.income.toLocaleString()}
            </span>
          </div>
          <div className={styles.summaryItem}>
            <span className={styles.summaryLabel}>Expenses</span>
            <span className={`${styles.summaryValue} ${styles.summaryValueNegative}`}>
              -${summary.expenses.toLocaleString()}
            </span>
          </div>
          <div className={styles.summaryItem}>
            <span className={styles.summaryLabel}>Net</span>
            <span className={styles.summaryValue}>
              {summary.net >= 0 ? '+' : '-'}${Math.abs(summary.net).toLocaleString()}
            </span>
          </div>
        </div>

        {/* Transaction List */}
        {filteredTransactions.length > 0 ? (
          <div className={styles.transactionList}>
            {groupedTransactions.map((group) => (
              <div key={group.date}>
                {/* Date Header */}
                <div className={styles.dateHeader}>
                  <span className={styles.dateText}>{group.date}</span>
                </div>

                {/* Transactions */}
                {group.transactions.map((transaction) => {
                  const IconComponent = getTransactionIcon(transaction.type);
                  return (
                    <div
                      key={transaction.id}
                      className={styles.transactionItem}
                      onClick={() => handleTransactionClick(transaction.id)}
                      role="button"
                      tabIndex={0}
                    >
                      <div className={`${styles.transactionIcon} ${getIconStyle(transaction.type)}`}>
                        <IconComponent />
                      </div>
                      <div className={styles.transactionDetails}>
                        <Text className={styles.transactionTitle}>{transaction.description}</Text>
                        <Text className={styles.transactionSubtitle}>{transaction.account}</Text>
                      </div>
                      <div className={styles.transactionRight}>
                        <Text className={`${styles.transactionAmount} ${getAmountStyle(transaction.type, transaction.amount)}`}>
                          {formatCurrency(transaction.amount)}
                        </Text>
                        <Text className={styles.transactionTime}>{transaction.time}</Text>
                      </div>
                    </div>
                  );
                })}
              </div>
            ))}
          </div>
        ) : (
          <div className={styles.emptyState}>
            <div className={styles.emptyIcon}>
              <History24Filled style={{ width: '32px', height: '32px' }} />
            </div>
            <Text className={styles.emptyTitle}>No Transactions</Text>
            <Text className={styles.emptySubtitle}>
              No transactions found for the selected filter.
            </Text>
          </div>
        )}
      </div>

      {/* Bottom Navigation */}
      <div className={styles.bottomNav}>
        <Link to="/" className={styles.navButton}>
          <Home24Regular className={styles.navIconInactive} />
          <span className={`${styles.navLabelText} ${styles.navLabelInactive}`}>Home</span>
        </Link>
        <Link to="/accounts" className={styles.navButton}>
          <Wallet24Regular className={styles.navIconInactive} />
          <span className={`${styles.navLabelText} ${styles.navLabelInactive}`}>Accounts</span>
        </Link>
        <Link to="/transfer" className={styles.navButton}>
          <ArrowSwap24Regular className={styles.navIconInactive} />
          <span className={`${styles.navLabelText} ${styles.navLabelInactive}`}>Transfer</span>
        </Link>
        <Link to="/history" className={styles.navButton}>
          <History24Filled className={styles.navIconActive} />
          <span className={`${styles.navLabelText} ${styles.navLabelActive}`}>History</span>
        </Link>
      </div>
    </div>
  );
}

export default HistoryPage;
