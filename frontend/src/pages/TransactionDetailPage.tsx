import { useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { makeStyles, Text } from '@fluentui/react-components';
import {
  ChevronLeft24Regular,
  ArrowDownload24Regular,
  ArrowUpload24Regular,
  ArrowSwap24Regular,
  ArrowDownload20Regular,
  ArrowUpload20Regular,
  Share20Regular,
  Print20Regular,
  ArrowRepeatAll20Regular,
  QuestionCircle20Regular,
} from '@fluentui/react-icons';
import { colors, shadows, gradients, transitions } from '../theme/tokens';

// ============================================
// TYPES
// ============================================

type TransactionType = 'deposit' | 'withdrawal' | 'transfer_out' | 'transfer_in';
type TransactionStatus = 'completed' | 'pending' | 'failed';

interface Transaction {
  id: string;
  transactionId: string;
  type: TransactionType;
  amount: number;
  description: string;
  category: string;
  status: TransactionStatus;
  date: string;
  time: string;
  reference: string;
  account: {
    name: string;
    number: string;
    type: string;
  };
  balanceBefore: number;
  balanceAfter: number;
  fee: number;
  recipient?: {
    name: string;
    accountNumber: string;
  };
  notes?: string;
}

// ============================================
// MOCK DATA
// ============================================

const mockTransaction: Transaction = {
  id: '1',
  transactionId: 'TXN-2026010100089',
  type: 'withdrawal',
  amount: 200.0,
  description: 'ATM Withdrawal at Main Branch - Downtown Location',
  category: 'Cash Out',
  status: 'completed',
  date: 'Jan 1, 2026',
  time: '10:23 AM',
  reference: 'REF-8472651',
  account: {
    name: 'Main Account',
    number: '**** **** **** 4521',
    type: 'Checking',
  },
  balanceBefore: 12650.0,
  balanceAfter: 12450.0,
  fee: 0,
};

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  container: {
    minHeight: '100vh',
    backgroundColor: colors.neutral[50],
    display: 'flex',
    flexDirection: 'column',
  },

  // ========== MOBILE HEADER ==========
  mobileHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '12px 16px',
    backgroundColor: '#FFFFFF',
    borderBottom: `1px solid ${colors.neutral[200]}`,
    '@media (min-width: 1024px)': {
      display: 'none',
    },
  },

  headerLeft: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },

  backButton: {
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

  headerAction: {
    width: '40px',
    height: '40px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    borderRadius: '8px',
    color: colors.neutral[500],
    ':hover': {
      backgroundColor: colors.neutral[100],
    },
  },

  // ========== DESKTOP HEADER ==========
  desktopHeader: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between',
      padding: '0 32px',
      height: '64px',
      backgroundColor: '#FFFFFF',
      borderBottom: `1px solid ${colors.neutral[200]}`,
    },
  },

  desktopHeaderLeft: {
    display: 'flex',
    alignItems: 'center',
    gap: '48px',
  },

  logo: {
    fontSize: '24px',
    fontWeight: 700,
    color: colors.brand[60],
    cursor: 'pointer',
  },

  navMenu: {
    display: 'flex',
    gap: '32px',
  },

  navItem: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
    cursor: 'pointer',
    padding: '8px 0',
    borderBottom: '2px solid transparent',
    transition: `all ${transitions.fast}`,
    ':hover': {
      color: colors.neutral[800],
    },
  },

  navItemActive: {
    color: colors.brand[60],
    borderBottomColor: colors.brand[60],
  },

  desktopHeaderRight: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },

  userAvatar: {
    width: '40px',
    height: '40px',
    borderRadius: '50%',
    backgroundColor: colors.brand[130],
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },

  avatarInitials: {
    fontSize: '14px',
    fontWeight: 600,
    color: colors.brand[60],
  },

  userName: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  // ========== MAIN CONTENT ==========
  mainContent: {
    flex: 1,
    padding: '16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    '@media (min-width: 1024px)': {
      padding: '32px',
      gap: '24px',
    },
  },

  // ========== PAGE HEADER (Desktop) ==========
  pageHeader: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      alignItems: 'center',
      gap: '16px',
    },
  },

  desktopBackButton: {
    width: '40px',
    height: '40px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '8px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    color: colors.neutral[500],
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  pageTitle: {
    fontSize: '24px',
    fontWeight: 700,
    color: colors.neutral[800],
  },

  // ========== CONTENT GRID ==========
  contentGrid: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    '@media (min-width: 1024px)': {
      display: 'grid',
      gridTemplateColumns: '1fr 400px',
      gap: '24px',
    },
  },

  // ========== TRANSACTION HEADER (Mobile) ==========
  transactionHeader: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    padding: '24px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '16px',
    '@media (min-width: 1024px)': {
      display: 'none',
    },
  },

  iconContainer: {
    width: '72px',
    height: '72px',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    '@media (min-width: 1024px)': {
      width: '80px',
      height: '80px',
    },
  },

  iconContainerWithdrawal: {
    background: gradients.error,
    color: colors.semantic.error.main,
  },

  iconContainerDeposit: {
    background: gradients.success,
    color: colors.semantic.success.main,
  },

  iconContainerTransfer: {
    background: gradients.primary,
    color: colors.brand[60],
  },

  transactionIcon: {
    width: '36px',
    height: '36px',
    '@media (min-width: 1024px)': {
      width: '40px',
      height: '40px',
    },
  },

  transactionAmount: {
    fontSize: '36px',
    fontWeight: 700,
    '@media (min-width: 1024px)': {
      fontSize: '40px',
    },
  },

  amountNegative: {
    color: colors.semantic.error.main,
  },

  amountPositive: {
    color: colors.semantic.success.main,
  },

  transactionType: {
    fontSize: '16px',
    fontWeight: 500,
    color: colors.neutral[500],
  },

  statusBadge: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    padding: '8px 16px',
    borderRadius: '20px',
  },

  statusBadgeCompleted: {
    backgroundColor: colors.semantic.success.light,
  },

  statusBadgePending: {
    backgroundColor: colors.semantic.warning.light,
  },

  statusBadgeFailed: {
    backgroundColor: colors.semantic.error.light,
  },

  statusDot: {
    width: '8px',
    height: '8px',
    borderRadius: '50%',
  },

  statusDotCompleted: {
    backgroundColor: colors.semantic.success.main,
  },

  statusDotPending: {
    backgroundColor: colors.semantic.warning.main,
  },

  statusDotFailed: {
    backgroundColor: colors.semantic.error.main,
  },

  statusText: {
    fontSize: '14px',
    fontWeight: 600,
  },

  statusTextCompleted: {
    color: '#137333',
  },

  statusTextPending: {
    color: '#B45309',
  },

  statusTextFailed: {
    color: '#B91C1C',
  },

  // ========== MAIN CARD (Desktop) ==========
  mainCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    boxShadow: shadows.sm,
    overflow: 'hidden',
  },

  transactionSummary: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      alignItems: 'center',
      gap: '24px',
      padding: '32px',
      borderBottom: `1px solid ${colors.neutral[200]}`,
    },
  },

  transactionMainInfo: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },

  typeLabel: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
  },

  transactionDescription: {
    fontSize: '16px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  transactionStatus: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: '8px',
  },

  transactionDate: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  // ========== DETAILS SECTIONS ==========
  detailsCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    overflow: 'hidden',
    '@media (min-width: 1024px)': {
      borderRadius: 0,
      boxShadow: 'none',
    },
  },

  detailsSection: {
    padding: '16px',
    '@media (min-width: 1024px)': {
      padding: '24px 32px',
    },
  },

  sectionTitle: {
    fontSize: '12px',
    fontWeight: 600,
    color: colors.neutral[500],
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    marginBottom: '12px',
    '@media (min-width: 1024px)': {
      fontSize: '14px',
      marginBottom: '16px',
    },
  },

  // Mobile: detail rows
  detailRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'flex-start',
    padding: '12px 0',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    ':last-child': {
      borderBottom: 'none',
    },
    '@media (min-width: 1024px)': {
      display: 'none',
    },
  },

  // Desktop: detail grid
  detailsGrid: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'grid',
      gridTemplateColumns: '1fr 1fr',
      gap: '16px 32px',
    },
  },

  detailItem: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  detailLabel: {
    fontSize: '13px',
    fontWeight: 400,
    color: colors.neutral[500],
    '@media (min-width: 1024px)': {
      fontSize: '13px',
    },
  },

  detailValue: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
    textAlign: 'right',
    '@media (min-width: 1024px)': {
      fontSize: '15px',
      textAlign: 'left',
    },
  },

  detailValueMono: {
    fontFamily: 'Consolas, "Courier New", monospace',
  },

  detailValueMultiline: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: '2px',
    '@media (min-width: 1024px)': {
      alignItems: 'flex-start',
    },
  },

  detailSubvalue: {
    fontSize: '12px',
    fontWeight: 400,
    color: colors.neutral[400],
  },

  sectionDivider: {
    height: '8px',
    backgroundColor: colors.neutral[50],
    '@media (min-width: 1024px)': {
      height: '1px',
      backgroundColor: colors.neutral[200],
      margin: '0 32px',
    },
  },

  fullWidth: {
    '@media (min-width: 1024px)': {
      gridColumn: 'span 2',
    },
  },

  // ========== SIDEBAR ==========
  sidebar: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    '@media (min-width: 1024px)': {
      gap: '24px',
    },
  },

  sidebarCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    boxShadow: shadows.sm,
    padding: '24px',
  },

  sidebarTitle: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
    marginBottom: '16px',
  },

  // ========== BALANCE TIMELINE ==========
  balanceTimeline: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      flexDirection: 'column',
      gap: '16px',
    },
  },

  timelineItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
  },

  timelineDot: {
    width: '12px',
    height: '12px',
    borderRadius: '50%',
    backgroundColor: colors.neutral[300],
    flexShrink: 0,
  },

  timelineDotActive: {
    backgroundColor: colors.brand[60],
  },

  timelineContent: {
    flex: 1,
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
  },

  timelineLabel: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  timelineValue: {
    fontSize: '14px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  timelineConnector: {
    width: '2px',
    height: '24px',
    backgroundColor: colors.neutral[200],
    marginLeft: '5px',
  },

  changeIndicator: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '8px',
    padding: '12px',
    borderRadius: '8px',
    marginTop: '8px',
  },

  changeIndicatorNegative: {
    backgroundColor: colors.semantic.error.light,
  },

  changeIndicatorPositive: {
    backgroundColor: colors.semantic.success.light,
  },

  changeText: {
    fontSize: '16px',
    fontWeight: 600,
  },

  changeTextNegative: {
    color: colors.semantic.error.main,
  },

  changeTextPositive: {
    color: colors.semantic.success.main,
  },

  // ========== ACTIONS ==========
  actionsCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    padding: '16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    '@media (min-width: 1024px)': {
      boxShadow: shadows.sm,
      padding: '24px',
    },
  },

  actionButton: {
    width: '100%',
    height: '52px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '12px',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.neutral[50],
      border: `1px solid ${colors.neutral[300]}`,
    },
    '@media (min-width: 1024px)': {
      height: '48px',
      borderRadius: '8px',
      gap: '10px',
    },
  },

  actionButtonPrimary: {
    backgroundColor: colors.brand[60],
    border: `1px solid ${colors.brand[60]}`,
    ':hover': {
      backgroundColor: colors.brand[50],
      border: `1px solid ${colors.brand[50]}`,
    },
  },

  actionButtonIcon: {
    width: '20px',
    height: '20px',
    color: colors.neutral[500],
  },

  actionButtonIconPrimary: {
    color: '#FFFFFF',
  },

  actionButtonText: {
    fontSize: '15px',
    fontWeight: 500,
    color: colors.neutral[800],
    '@media (min-width: 1024px)': {
      fontSize: '14px',
    },
  },

  actionButtonTextPrimary: {
    color: '#FFFFFF',
  },

  // ========== HELP SECTION ==========
  helpLink: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '8px',
    padding: '16px',
    '@media (min-width: 1024px)': {
      display: 'none',
    },
  },

  helpLinkText: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.brand[60],
  },

  helpCard: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      flexDirection: 'column',
      gap: '12px',
      backgroundColor: colors.brand[140],
      border: `1px solid ${colors.brand[120]}`,
      borderRadius: '12px',
      padding: '20px',
    },
  },

  helpHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },

  helpIcon: {
    width: '24px',
    height: '24px',
    color: colors.brand[60],
  },

  helpTitle: {
    fontSize: '15px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  helpText: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
    lineHeight: '1.5',
  },

  helpLinkDesktop: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.brand[60],
    cursor: 'pointer',
    ':hover': {
      textDecoration: 'underline',
    },
  },
});

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

function getTransactionIcon(type: TransactionType) {
  switch (type) {
    case 'deposit':
      return <ArrowDownload24Regular />;
    case 'withdrawal':
      return <ArrowUpload24Regular />;
    case 'transfer_out':
    case 'transfer_in':
      return <ArrowSwap24Regular />;
  }
}

function getTransactionTypeLabel(type: TransactionType): string {
  switch (type) {
    case 'deposit':
      return 'Deposit';
    case 'withdrawal':
      return 'Withdrawal';
    case 'transfer_out':
      return 'Transfer Sent';
    case 'transfer_in':
      return 'Transfer Received';
  }
}

function isPositiveTransaction(type: TransactionType): boolean {
  return type === 'deposit' || type === 'transfer_in';
}

// ============================================
// COMPONENT
// ============================================

export function TransactionDetailPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { id: transactionId } = useParams<{ id: string }>();

  // TODO: In real app, fetch transaction by transactionId from RTK Query
  console.debug('Viewing transaction:', transactionId);
  const [transaction] = useState<Transaction>(mockTransaction);

  const isPositive = isPositiveTransaction(transaction.type);
  const amountDisplay = isPositive
    ? formatCurrency(transaction.amount)
    : `-${formatCurrency(transaction.amount)}`;

  const iconContainerClass = `${styles.iconContainer} ${
    transaction.type === 'withdrawal'
      ? styles.iconContainerWithdrawal
      : transaction.type === 'deposit'
        ? styles.iconContainerDeposit
        : styles.iconContainerTransfer
  }`;

  const statusBadgeClass = `${styles.statusBadge} ${
    transaction.status === 'completed'
      ? styles.statusBadgeCompleted
      : transaction.status === 'pending'
        ? styles.statusBadgePending
        : styles.statusBadgeFailed
  }`;

  const statusDotClass = `${styles.statusDot} ${
    transaction.status === 'completed'
      ? styles.statusDotCompleted
      : transaction.status === 'pending'
        ? styles.statusDotPending
        : styles.statusDotFailed
  }`;

  const statusTextClass = `${styles.statusText} ${
    transaction.status === 'completed'
      ? styles.statusTextCompleted
      : transaction.status === 'pending'
        ? styles.statusTextPending
        : styles.statusTextFailed
  }`;

  const handleBack = () => {
    navigate(-1);
  };

  const handleDownload = () => {
    // TODO: Implement download receipt
    console.log('Download receipt');
  };

  const handleShare = () => {
    // TODO: Implement share
    console.log('Share details');
  };

  const handlePrint = () => {
    // TODO: Implement print
    window.print();
  };

  const handleRepeat = () => {
    // TODO: Navigate to repeat transaction
    console.log('Repeat transaction');
  };

  return (
    <div className={styles.container}>
      {/* Mobile Header */}
      <div className={styles.mobileHeader}>
        <div className={styles.headerLeft}>
          <button className={styles.backButton} onClick={handleBack}>
            <ChevronLeft24Regular />
          </button>
          <Text className={styles.headerTitle}>Transaction Details</Text>
        </div>
        <button className={styles.headerAction} onClick={handleShare}>
          <Share20Regular />
        </button>
      </div>

      {/* Desktop Header */}
      <div className={styles.desktopHeader}>
        <div className={styles.desktopHeaderLeft}>
          <Text className={styles.logo} onClick={() => navigate('/dashboard')}>
            AzureBank
          </Text>
          <div className={styles.navMenu}>
            <Text className={styles.navItem} onClick={() => navigate('/dashboard')}>
              Dashboard
            </Text>
            <Text className={styles.navItem} onClick={() => navigate('/accounts')}>
              Accounts
            </Text>
            <Text className={`${styles.navItem} ${styles.navItemActive}`}>Transactions</Text>
            <Text className={styles.navItem} onClick={() => navigate('/transfer')}>
              Transfers
            </Text>
          </div>
        </div>
        <div className={styles.desktopHeaderRight}>
          <div className={styles.userAvatar}>
            <Text className={styles.avatarInitials}>JD</Text>
          </div>
          <Text className={styles.userName}>John Doe</Text>
        </div>
      </div>

      {/* Main Content */}
      <div className={styles.mainContent}>
        {/* Page Header (Desktop) */}
        <div className={styles.pageHeader}>
          <button className={styles.desktopBackButton} onClick={handleBack}>
            <ChevronLeft24Regular />
          </button>
          <Text className={styles.pageTitle}>Transaction Details</Text>
        </div>

        <div className={styles.contentGrid}>
          {/* Left Column */}
          <div>
            {/* Transaction Header (Mobile) */}
            <div className={styles.transactionHeader}>
              <div className={iconContainerClass}>{getTransactionIcon(transaction.type)}</div>
              <Text
                className={`${styles.transactionAmount} ${
                  isPositive ? styles.amountPositive : styles.amountNegative
                }`}
              >
                {amountDisplay}
              </Text>
              <Text className={styles.transactionType}>
                {getTransactionTypeLabel(transaction.type)}
              </Text>
              <div className={statusBadgeClass}>
                <div className={statusDotClass} />
                <Text className={statusTextClass}>
                  {transaction.status.charAt(0).toUpperCase() + transaction.status.slice(1)}
                </Text>
              </div>
            </div>

            {/* Main Card (Desktop) */}
            <div className={styles.mainCard}>
              {/* Transaction Summary (Desktop) */}
              <div className={styles.transactionSummary}>
                <div className={iconContainerClass}>{getTransactionIcon(transaction.type)}</div>
                <div className={styles.transactionMainInfo}>
                  <Text className={styles.typeLabel}>
                    {getTransactionTypeLabel(transaction.type)}
                  </Text>
                  <Text
                    className={`${styles.transactionAmount} ${
                      isPositive ? styles.amountPositive : styles.amountNegative
                    }`}
                  >
                    {amountDisplay}
                  </Text>
                  <Text className={styles.transactionDescription}>{transaction.description}</Text>
                </div>
                <div className={styles.transactionStatus}>
                  <div className={statusBadgeClass}>
                    <div className={statusDotClass} />
                    <Text className={statusTextClass}>
                      {transaction.status.charAt(0).toUpperCase() + transaction.status.slice(1)}
                    </Text>
                  </div>
                  <Text className={styles.transactionDate}>
                    {transaction.date} at {transaction.time}
                  </Text>
                </div>
              </div>

              {/* Transaction Information */}
              <div className={styles.detailsCard}>
                <div className={styles.detailsSection}>
                  <Text className={styles.sectionTitle}>Transaction Information</Text>

                  {/* Mobile: Detail Rows */}
                  <div className={styles.detailRow}>
                    <Text className={styles.detailLabel}>Transaction ID</Text>
                    <Text className={`${styles.detailValue} ${styles.detailValueMono}`}>
                      {transaction.transactionId}
                    </Text>
                  </div>
                  <div className={styles.detailRow}>
                    <Text className={styles.detailLabel}>Date & Time</Text>
                    <div className={styles.detailValueMultiline}>
                      <Text className={styles.detailValue}>{transaction.date}</Text>
                      <Text className={styles.detailSubvalue}>{transaction.time}</Text>
                    </div>
                  </div>
                  <div className={styles.detailRow}>
                    <Text className={styles.detailLabel}>Type</Text>
                    <Text className={styles.detailValue}>
                      {getTransactionTypeLabel(transaction.type)}
                    </Text>
                  </div>
                  <div className={styles.detailRow}>
                    <Text className={styles.detailLabel}>Category</Text>
                    <Text className={styles.detailValue}>{transaction.category}</Text>
                  </div>

                  {/* Desktop: Detail Grid */}
                  <div className={styles.detailsGrid}>
                    <div className={styles.detailItem}>
                      <Text className={styles.detailLabel}>Transaction ID</Text>
                      <Text className={`${styles.detailValue} ${styles.detailValueMono}`}>
                        {transaction.transactionId}
                      </Text>
                    </div>
                    <div className={styles.detailItem}>
                      <Text className={styles.detailLabel}>Reference Number</Text>
                      <Text className={`${styles.detailValue} ${styles.detailValueMono}`}>
                        {transaction.reference}
                      </Text>
                    </div>
                    <div className={styles.detailItem}>
                      <Text className={styles.detailLabel}>Type</Text>
                      <Text className={styles.detailValue}>
                        {getTransactionTypeLabel(transaction.type)}
                      </Text>
                    </div>
                    <div className={styles.detailItem}>
                      <Text className={styles.detailLabel}>Category</Text>
                      <Text className={styles.detailValue}>{transaction.category}</Text>
                    </div>
                    <div className={styles.detailItem}>
                      <Text className={styles.detailLabel}>Processing Time</Text>
                      <Text className={styles.detailValue}>Instant</Text>
                    </div>
                    <div className={styles.detailItem}>
                      <Text className={styles.detailLabel}>Fee</Text>
                      <Text className={styles.detailValue}>{formatCurrency(transaction.fee)}</Text>
                    </div>
                  </div>
                </div>

                <div className={styles.sectionDivider} />

                {/* Account Details */}
                <div className={styles.detailsSection}>
                  <Text className={styles.sectionTitle}>Account Details</Text>

                  {/* Mobile: Detail Rows */}
                  <div className={styles.detailRow}>
                    <Text className={styles.detailLabel}>From Account</Text>
                    <div className={styles.detailValueMultiline}>
                      <Text className={styles.detailValue}>{transaction.account.name}</Text>
                      <Text className={`${styles.detailSubvalue} ${styles.detailValueMono}`}>
                        {transaction.account.number}
                      </Text>
                    </div>
                  </div>
                  <div className={styles.detailRow}>
                    <Text className={styles.detailLabel}>Balance Before</Text>
                    <Text className={styles.detailValue}>
                      {formatCurrency(transaction.balanceBefore)}
                    </Text>
                  </div>
                  <div className={styles.detailRow}>
                    <Text className={styles.detailLabel}>Balance After</Text>
                    <Text className={styles.detailValue}>
                      {formatCurrency(transaction.balanceAfter)}
                    </Text>
                  </div>

                  {/* Desktop: Detail Grid */}
                  <div className={styles.detailsGrid}>
                    <div className={styles.detailItem}>
                      <Text className={styles.detailLabel}>Account Name</Text>
                      <Text className={styles.detailValue}>{transaction.account.name}</Text>
                    </div>
                    <div className={styles.detailItem}>
                      <Text className={styles.detailLabel}>Account Number</Text>
                      <Text className={`${styles.detailValue} ${styles.detailValueMono}`}>
                        {transaction.account.number}
                      </Text>
                    </div>
                    <div className={styles.detailItem}>
                      <Text className={styles.detailLabel}>Account Type</Text>
                      <Text className={styles.detailValue}>{transaction.account.type}</Text>
                    </div>
                    <div className={styles.detailItem}>
                      <Text className={styles.detailLabel}>Currency</Text>
                      <Text className={styles.detailValue}>USD</Text>
                    </div>
                  </div>
                </div>

                <div className={styles.sectionDivider} />

                {/* Additional Details */}
                <div className={styles.detailsSection}>
                  <Text className={styles.sectionTitle}>Additional Details</Text>

                  {/* Mobile: Detail Rows */}
                  <div className={styles.detailRow}>
                    <Text className={styles.detailLabel}>Description</Text>
                    <Text className={styles.detailValue}>ATM Withdrawal</Text>
                  </div>
                  <div className={styles.detailRow}>
                    <Text className={styles.detailLabel}>Reference</Text>
                    <Text className={`${styles.detailValue} ${styles.detailValueMono}`}>
                      {transaction.reference}
                    </Text>
                  </div>

                  {/* Desktop: Detail Grid */}
                  <div className={styles.detailsGrid}>
                    <div className={`${styles.detailItem} ${styles.fullWidth}`}>
                      <Text className={styles.detailLabel}>Description</Text>
                      <Text className={styles.detailValue}>{transaction.description}</Text>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          {/* Right Column / Sidebar */}
          <div className={styles.sidebar}>
            {/* Balance Impact Card (Desktop) */}
            <div className={styles.sidebarCard}>
              <Text className={styles.sidebarTitle}>Balance Impact</Text>
              <div className={styles.balanceTimeline}>
                <div className={styles.timelineItem}>
                  <div className={styles.timelineDot} />
                  <div className={styles.timelineContent}>
                    <Text className={styles.timelineLabel}>Balance Before</Text>
                    <Text className={styles.timelineValue}>
                      {formatCurrency(transaction.balanceBefore)}
                    </Text>
                  </div>
                </div>
                <div className={styles.timelineConnector} />
                <div className={styles.timelineItem}>
                  <div className={`${styles.timelineDot} ${styles.timelineDotActive}`} />
                  <div className={styles.timelineContent}>
                    <Text className={styles.timelineLabel}>Balance After</Text>
                    <Text className={styles.timelineValue}>
                      {formatCurrency(transaction.balanceAfter)}
                    </Text>
                  </div>
                </div>
              </div>
              <div
                className={`${styles.changeIndicator} ${
                  isPositive ? styles.changeIndicatorPositive : styles.changeIndicatorNegative
                }`}
              >
                {isPositive ? (
                  <ArrowDownload20Regular style={{ color: colors.semantic.success.main }} />
                ) : (
                  <ArrowUpload20Regular style={{ color: colors.semantic.error.main }} />
                )}
                <Text
                  className={`${styles.changeText} ${
                    isPositive ? styles.changeTextPositive : styles.changeTextNegative
                  }`}
                >
                  {amountDisplay}
                </Text>
              </div>
            </div>

            {/* Actions Card */}
            <div className={styles.actionsCard}>
              <Text className={styles.sidebarTitle} style={{ display: 'none' }}>
                Quick Actions
              </Text>
              <button className={styles.actionButton} onClick={handleDownload}>
                <ArrowDownload20Regular className={styles.actionButtonIcon} />
                <Text className={styles.actionButtonText}>Download Receipt</Text>
              </button>
              <button className={styles.actionButton} onClick={handlePrint}>
                <Print20Regular className={styles.actionButtonIcon} />
                <Text className={styles.actionButtonText}>Print Details</Text>
              </button>
              <button className={styles.actionButton} onClick={handleShare}>
                <Share20Regular className={styles.actionButtonIcon} />
                <Text className={styles.actionButtonText}>Share Details</Text>
              </button>
              <button
                className={`${styles.actionButton} ${styles.actionButtonPrimary}`}
                onClick={handleRepeat}
              >
                <ArrowRepeatAll20Regular
                  className={`${styles.actionButtonIcon} ${styles.actionButtonIconPrimary}`}
                />
                <Text className={`${styles.actionButtonText} ${styles.actionButtonTextPrimary}`}>
                  Repeat Transaction
                </Text>
              </button>
            </div>

            {/* Help Link (Mobile) */}
            <div className={styles.helpLink}>
              <QuestionCircle20Regular style={{ color: colors.brand[60] }} />
              <Text className={styles.helpLinkText}>Need help with this transaction?</Text>
            </div>

            {/* Help Card (Desktop) */}
            <div className={styles.helpCard}>
              <div className={styles.helpHeader}>
                <QuestionCircle20Regular className={styles.helpIcon} />
                <Text className={styles.helpTitle}>Need Assistance?</Text>
              </div>
              <Text className={styles.helpText}>
                If you have questions about this transaction or notice any discrepancies, our
                support team is here to help.
              </Text>
              <Text className={styles.helpLinkDesktop}>Contact Support →</Text>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

export default TransactionDetailPage;
