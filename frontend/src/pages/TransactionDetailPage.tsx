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
  tokens,
} from '@fluentui/react-components';
import {
  ArrowDownload24Regular,
  ArrowUpload24Regular,
  ArrowSwap24Regular,
  Search24Regular,
} from '@fluentui/react-icons';
import { atMedia } from '../theme/breakpoints';
import { colors, shadows } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import { PageHeader } from '../components/layout/PageHeader';
import type { TransactionStatus, TransactionType } from '../api/enums';
import { useGetTransactionQuery } from '../features/api/apiSlice';
import { formatDateHeading, formatCurrency, formatTime, isIncomeType } from '../utils/format';

// ============================================
// STYLES — mobile layout (desktop pass = quality track)
// ============================================

const useStyles = makeStyles({
  /**
   * The canvas belongs to the shell (AppLayout); the height is CLAIMED from it with `flex: 1`.
   *
   * The loading spinner and the "Transaction not found" state below centre themselves with
   * `flex: 1`, so this container has to be taller than its content for either to land in the
   * middle of the page rather than under the header.
   */
  container: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
  },

  /**
   * A capped, centred measure — not the two columns the plan called for.
   *
   * That line was written when this page carried several sections; H5 pruned it to a hero and one
   * card, and two columns for one card is a column with nothing beside it. What was actually broken
   * is that the page had NO media query at all, so on a 1440px screen a receipt with eight
   * key-value rows stretched the full width and its labels ended a screen away from their values.
   */
  content: {
    flex: 1,
    padding: '16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    width: '100%',
    maxWidth: '640px',
    marginLeft: 'auto',
    marginRight: 'auto',

    [atMedia.md]: {
      padding: '32px 24px',
      gap: '24px',
    },
  },

  // ========== HERO ==========
  hero: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    padding: '24px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
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
    backgroundColor: colors.semantic.warning.light,
  },
  statusPendingDot: {
    backgroundColor: colors.semantic.warning.main,
  },
  statusPendingText: {
    color: colors.semantic.warning.dark,
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
    backgroundColor: tokens.colorNeutralBackground1,
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
      {/* A leaf inside History, so back goes to the SECTION ROOT — deterministically, not
          `navigate(-1)`. Opened from the dashboard's recent feed, the old back returned to the
          dashboard while the navigation insisted History was the current place: a header and a nav
          contradicting each other on one screen. */}
      <PageHeader title="Transaction Details" onBack={() => navigate('/history')} />

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
