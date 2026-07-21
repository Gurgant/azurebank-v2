import { useNavigate, useParams } from 'react-router-dom';
import {
  Button,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  mergeClasses,
  Spinner,
  Text,
} from '@fluentui/react-components';
import {
  ChevronLeft24Regular,
  ArrowDownload24Regular,
  ArrowUpload24Regular,
  ArrowSwap24Regular,
  Search24Regular,
} from '@fluentui/react-icons';
import { colors, shadows } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import type { TransactionStatus, TransactionType } from '../api/enums';
import { useGetTransactionQuery } from '../features/api/apiSlice';
import { formatDateHeading, formatCurrency, formatTime, isIncomeType } from '../utils/format';

// ============================================
// STYLES — mobile layout (desktop pass = quality track)
// ============================================

const useStyles = makeStyles({
  container: {
    minHeight: '100vh',
    backgroundColor: colors.neutral[50],
    display: 'flex',
    flexDirection: 'column',
  },

  header: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '12px 16px',
    backgroundColor: '#FFFFFF',
    borderBottom: `1px solid ${colors.neutral[200]}`,
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

  content: {
    flex: 1,
    padding: '16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },

  // ========== HERO ==========
  hero: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    padding: '24px 16px',
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    boxShadow: shadows.sm,
  },

  iconContainer: {
    width: '56px',
    height: '56px',
    borderRadius: '16px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },

  iconIncome: {
    backgroundColor: colors.semantic.success.light,
    color: colors.semantic.success.main,
  },

  iconExpense: {
    backgroundColor: colors.semantic.error.light,
    color: colors.semantic.error.main,
  },

  amount: {
    fontSize: '32px',
    fontWeight: 700,
    fontFamily: 'Consolas, monospace',
  },

  amountPositive: {
    color: colors.semantic.success.main,
  },

  amountNegative: {
    color: colors.neutral[800],
  },

  typeLabel: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
  },

  statusBadge: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    padding: '4px 12px',
    borderRadius: '12px',
  },

  statusDot: {
    width: '8px',
    height: '8px',
    borderRadius: '50%',
  },

  statusLabel: {
    fontSize: '13px',
    fontWeight: 600,
  },

  statusCompleted: {
    backgroundColor: colors.semantic.success.light,
  },
  statusCompletedDot: {
    backgroundColor: colors.semantic.success.main,
  },
  statusCompletedText: {
    color: colors.semantic.success.main,
  },
  statusPending: {
    backgroundColor: '#FEF3E2',
  },
  statusPendingDot: {
    backgroundColor: '#F59E0B',
  },
  statusPendingText: {
    color: '#B45309',
  },
  statusFailed: {
    backgroundColor: colors.semantic.error.light,
  },
  statusFailedDot: {
    backgroundColor: colors.semantic.error.main,
  },
  statusFailedText: {
    color: colors.semantic.error.main,
  },
  statusReversed: {
    backgroundColor: colors.neutral[100],
  },
  statusReversedDot: {
    backgroundColor: colors.neutral[500],
  },
  statusReversedText: {
    color: colors.neutral[600],
  },

  // ========== DETAILS CARD ==========
  detailsCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    padding: '8px 16px',
    boxShadow: shadows.sm,
  },

  sectionTitle: {
    display: 'block',
    fontSize: '14px',
    fontWeight: 600,
    color: colors.neutral[500],
    padding: '12px 0 4px',
  },

  detailRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'baseline',
    gap: '16px',
    padding: '12px 0',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    ':last-child': {
      borderBottom: 'none',
    },
  },

  detailLabel: {
    fontSize: '14px',
    color: colors.neutral[500],
    flexShrink: 0,
  },

  detailValue: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
    textAlign: 'right',
    overflowWrap: 'anywhere',
  },

  detailValueMono: {
    fontFamily: 'Consolas, monospace',
  },

  // ========== STATES ==========
  stateContainer: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '16px',
    padding: '48px 24px',
  },

  stateIcon: {
    width: '80px',
    height: '80px',
    borderRadius: '50%',
    backgroundColor: colors.neutral[100],
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.neutral[400],
  },

  stateTitle: {
    fontSize: '18px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  stateSubtitle: {
    fontSize: '14px',
    color: colors.neutral[500],
    textAlign: 'center',
  },
});

// ============================================
// HELPER FUNCTIONS
// ============================================

function getTransactionIcon(type: TransactionType) {
  switch (type) {
    case 'Deposit':
      return <ArrowDownload24Regular />;
    case 'Withdrawal':
      return <ArrowUpload24Regular />;
    case 'TransferIn':
    case 'TransferOut':
      return <ArrowSwap24Regular />;
  }
}

const TYPE_LABELS: Record<TransactionType, string> = {
  Deposit: 'Deposit',
  Withdrawal: 'Withdrawal',
  TransferIn: 'Transfer received',
  TransferOut: 'Transfer sent',
};

// ============================================
// COMPONENT
// ============================================

export function TransactionDetailPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { id } = useParams<{ id: string }>();

  // T2 — enveloped detail, tagged {Transaction,id}. The route guarantees `id`.
  const { data: transaction, isLoading, error, refetch } = useGetTransactionQuery(id ?? '');
  const problem = error as ApiProblem | undefined;
  const isNotFound = problem?.status === 404;

  // [badge background, dot fill, label text color] — the dot needs a FILL, the
  // label a color only; one shared class painted a block behind the text.
  const statusStyles: Record<TransactionStatus, [string, string, string]> = {
    Completed: [styles.statusCompleted, styles.statusCompletedDot, styles.statusCompletedText],
    Pending: [styles.statusPending, styles.statusPendingDot, styles.statusPendingText],
    Failed: [styles.statusFailed, styles.statusFailedDot, styles.statusFailedText],
    Reversed: [styles.statusReversed, styles.statusReversedDot, styles.statusReversedText],
  };

  return (
    <div className={styles.container}>
      <div className={styles.header}>
        <button
          className={styles.backButton}
          aria-label="Go back"
          onClick={() => void navigate(-1)}
        >
          <ChevronLeft24Regular />
        </button>
        <Text className={styles.headerTitle}>Transaction Details</Text>
      </div>

      {isLoading && (
        <div className={styles.stateContainer}>
          <Spinner size="large" aria-label="Loading transaction" />
        </div>
      )}

      {/* A 404 is a first-class outcome, not an error bar: the link may be stale. */}
      {isNotFound && (
        <div className={styles.stateContainer}>
          <div className={styles.stateIcon}>
            <Search24Regular style={{ width: '32px', height: '32px' }} />
          </div>
          <Text className={styles.stateTitle}>Transaction not found</Text>
          <Text className={styles.stateSubtitle}>
            This transaction doesn&apos;t exist or is not yours to see.
          </Text>
          <Button appearance="primary" onClick={() => void navigate('/history')}>
            Back to history
          </Button>
        </div>
      )}

      {problem && !isNotFound && (
        <div className={styles.content}>
          <MessageBar intent="error">
            <MessageBarBody>
              {problem.detail || 'Could not load the transaction.'}
              {problem.traceId ? ` Support code: ${problem.traceId}` : ''}
            </MessageBarBody>
            <MessageBarActions>
              <Button appearance="transparent" onClick={() => void refetch()}>
                Retry
              </Button>
            </MessageBarActions>
          </MessageBar>
        </div>
      )}

      {transaction && (
        <div className={styles.content}>
          {/* Hero */}
          <div className={styles.hero}>
            <div
              className={mergeClasses(
                styles.iconContainer,
                isIncomeType(transaction.type) ? styles.iconIncome : styles.iconExpense,
              )}
            >
              {getTransactionIcon(transaction.type)}
            </div>
            <Text
              className={mergeClasses(
                styles.amount,
                isIncomeType(transaction.type) ? styles.amountPositive : styles.amountNegative,
              )}
            >
              {isIncomeType(transaction.type) ? '+' : '-'}
              {formatCurrency(Math.abs(transaction.amount))}
            </Text>
            <Text className={styles.typeLabel}>{TYPE_LABELS[transaction.type]}</Text>
            <div className={mergeClasses(styles.statusBadge, statusStyles[transaction.status][0])}>
              <div
                className={mergeClasses(styles.statusDot, statusStyles[transaction.status][1])}
              />
              <Text
                className={mergeClasses(styles.statusLabel, statusStyles[transaction.status][2])}
              >
                {transaction.status}
              </Text>
            </div>
          </div>

          {/* Details — the CONTRACT's fields, nothing fabricated */}
          <div className={styles.detailsCard}>
            <Text className={styles.sectionTitle}>Transaction Information</Text>

            <div className={styles.detailRow}>
              <Text className={styles.detailLabel}>Transaction number</Text>
              <Text className={mergeClasses(styles.detailValue, styles.detailValueMono)}>
                {transaction.transactionNumber}
              </Text>
            </div>

            <div className={styles.detailRow}>
              <Text className={styles.detailLabel}>Date &amp; time</Text>
              <Text className={styles.detailValue}>
                {formatDateHeading(transaction.createdAt)} · {formatTime(transaction.createdAt)}
              </Text>
            </div>

            <div className={styles.detailRow}>
              <Text className={styles.detailLabel}>Type</Text>
              <Text className={styles.detailValue}>{TYPE_LABELS[transaction.type]}</Text>
            </div>

            {transaction.recipientAzureTag && (
              <div className={styles.detailRow}>
                <Text className={styles.detailLabel}>To</Text>
                <Text className={styles.detailValue}>@{transaction.recipientAzureTag}</Text>
              </div>
            )}

            {transaction.senderAzureTag && (
              <div className={styles.detailRow}>
                <Text className={styles.detailLabel}>From</Text>
                <Text className={styles.detailValue}>@{transaction.senderAzureTag}</Text>
              </div>
            )}

            {transaction.description && (
              <div className={styles.detailRow}>
                <Text className={styles.detailLabel}>Description</Text>
                <Text className={styles.detailValue}>{transaction.description}</Text>
              </div>
            )}

            <div className={styles.detailRow}>
              <Text className={styles.detailLabel}>Balance after</Text>
              <Text className={mergeClasses(styles.detailValue, styles.detailValueMono)}>
                {formatCurrency(transaction.balanceAfter)}
              </Text>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

export default TransactionDetailPage;
