import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
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
  ChevronLeft24Regular,
  History24Filled,
  ArrowDownload24Regular,
  ArrowUpload24Regular,
  ArrowRight24Regular,
  ArrowLeft24Regular,
} from '@fluentui/react-icons';
import { colors, gradients, transitions } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import type { TransactionType } from '../api/enums';
import type { TransactionResponse } from '../features/api/apiSlice';
import { useGetTransactionHistoryInfiniteQuery } from '../features/api/apiSlice';
import {
  formatCurrency,
  formatDateHeading,
  formatTime,
  formatTransactionAmount,
  isIncomeType,
} from '../utils/format';

// ============================================
// TYPES
// ============================================

type FilterType = 'all' | 'deposits' | 'withdrawals' | 'transfers';

interface GroupedTransactions {
  date: string;
  transactions: TransactionResponse[];
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

  headerSpacer: {
    width: '40px',
  },

  // ========== CONTENT ==========
  content: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
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
    width: '100%',
    textAlign: 'left',
    backgroundColor: 'transparent',
    border: 'none',
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

  transactionTime: {
    fontSize: '12px',
    fontWeight: 400,
    color: colors.neutral[400],
  },

  statusText: {
    fontSize: '12px',
    fontWeight: 600,
    color: colors.semantic.warning.main,
  },

  // ========== STATES (D22) ==========
  stateContainer: {
    flex: 1,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '48px 0',
  },

  errorContainer: {
    padding: '16px',
  },

  loadMoreContainer: {
    display: 'flex',
    justifyContent: 'center',
    padding: '16px',
    backgroundColor: '#FFFFFF',
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
});

// ============================================
// HELPER FUNCTIONS
// ============================================

function getTransactionIcon(type: TransactionType) {
  switch (type) {
    case 'Deposit':
      return ArrowDownload24Regular;
    case 'Withdrawal':
      return ArrowUpload24Regular;
    case 'TransferOut':
      return ArrowRight24Regular;
    case 'TransferIn':
      return ArrowLeft24Regular;
  }
}

/** The row's headline: counterparty first, then description, then the bare type. */
function getRowTitle(transaction: TransactionResponse): string {
  if (transaction.type === 'TransferOut' && transaction.recipientAzureTag) {
    return `To @${transaction.recipientAzureTag}`;
  }
  if (transaction.type === 'TransferIn' && transaction.senderAzureTag) {
    return `From @${transaction.senderAzureTag}`;
  }
  return transaction.description ?? transaction.type;
}

const FILTER_TYPES: Record<Exclude<FilterType, 'all'>, TransactionType[]> = {
  deposits: ['Deposit'],
  withdrawals: ['Withdrawal'],
  transfers: ['TransferIn', 'TransferOut'],
};

function groupByDate(transactions: TransactionResponse[]): GroupedTransactions[] {
  const groups = new Map<string, TransactionResponse[]>();
  for (const transaction of transactions) {
    const heading = formatDateHeading(transaction.createdAt);
    const group = groups.get(heading);
    if (group) {
      group.push(transaction);
    } else {
      groups.set(heading, [transaction]);
    }
  }
  return [...groups.entries()].map(([date, items]) => ({ date, transactions: items }));
}

// ============================================
// COMPONENT
// ============================================

export function HistoryPage() {
  const styles = useStyles();
  const navigate = useNavigate();

  const [activeFilter, setActiveFilter] = useState<FilterType>('all');

  // T1 — the infinite feed. Pages accumulate; every money mutation invalidates
  // {Transaction,'LIST'} and refetches all loaded pages (accepted D7 cost).
  const { data, isLoading, error, refetch, fetchNextPage, hasNextPage, isFetchingNextPage } =
    useGetTransactionHistoryInfiniteQuery({});
  const problem = error as ApiProblem | undefined;

  const transactions = useMemo(() => (data?.pages ?? []).flatMap((p) => p.data ?? []), [data]);

  // Client-side tabs over the LOADED pages — the API has no type filter, and the
  // honest alternative to filtering what we have would be lying about totals.
  const filteredTransactions = useMemo(() => {
    if (activeFilter === 'all') {
      return transactions;
    }
    const types = FILTER_TYPES[activeFilter];
    return transactions.filter((t) => types.includes(t.type));
  }, [transactions, activeFilter]);

  const groupedTransactions = useMemo(
    () => groupByDate(filteredTransactions),
    [filteredTransactions],
  );

  // The mock-signed trap, killed: amounts are UNSIGNED — direction comes from the
  // TYPE. Failed/Reversed money never (or no longer) moved, so it stays out.
  const summary = useMemo(() => {
    const settled = transactions.filter((t) => t.status !== 'Failed' && t.status !== 'Reversed');
    const income = settled
      .filter((t) => isIncomeType(t.type))
      .reduce((sum, t) => sum + t.amount, 0);
    const expenses = settled
      .filter((t) => !isIncomeType(t.type))
      .reduce((sum, t) => sum + t.amount, 0);
    return { income, expenses, net: income - expenses };
  }, [transactions]);

  const getIconStyle = (type: TransactionType) => {
    switch (type) {
      case 'Deposit':
        return styles.iconDeposit;
      case 'Withdrawal':
        return styles.iconWithdrawal;
      case 'TransferOut':
        return styles.iconTransferOut;
      case 'TransferIn':
        return styles.iconTransferIn;
    }
  };

  return (
    <div className={styles.container}>
      {/* Header */}
      <div className={styles.header}>
        <button
          className={styles.headerButton}
          aria-label="Go back"
          onClick={() => void navigate(-1)}
        >
          <ChevronLeft24Regular />
        </button>
        <Text className={styles.headerTitle}>Transaction History</Text>
        <div className={styles.headerSpacer} />
      </div>

      {/* Content */}
      <div className={styles.content}>
        {isLoading && (
          <div className={styles.stateContainer}>
            <Spinner size="large" aria-label="Loading transactions" />
          </div>
        )}

        {problem && (
          <div className={styles.errorContainer}>
            <MessageBar intent="error">
              <MessageBarBody>
                {problem.detail || 'Could not load your transactions.'}
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

        {!isLoading && !problem && (
          <>
            {/* Filter Tabs — client-side, over the loaded pages */}
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

            {/* Summary Card — totals over the loaded, settled transactions */}
            <div className={styles.summaryCard}>
              <div className={styles.summaryItem}>
                <span className={styles.summaryLabel}>Income</span>
                <span className={`${styles.summaryValue} ${styles.summaryValuePositive}`}>
                  +{formatCurrency(summary.income)}
                </span>
              </div>
              <div className={styles.summaryItem}>
                <span className={styles.summaryLabel}>Expenses</span>
                <span className={`${styles.summaryValue} ${styles.summaryValueNegative}`}>
                  -{formatCurrency(summary.expenses)}
                </span>
              </div>
              <div className={styles.summaryItem}>
                <span className={styles.summaryLabel}>Net</span>
                <span className={styles.summaryValue}>
                  {summary.net >= 0 ? '+' : '-'}
                  {formatCurrency(Math.abs(summary.net))}
                </span>
              </div>
            </div>

            {/* Transaction List */}
            {filteredTransactions.length > 0 ? (
              <div className={styles.transactionList}>
                {groupedTransactions.map((group) => (
                  <div key={group.date}>
                    <div className={styles.dateHeader}>
                      <span className={styles.dateText}>{group.date}</span>
                    </div>

                    {group.transactions.map((transaction) => {
                      const IconComponent = getTransactionIcon(transaction.type);
                      return (
                        <button
                          key={transaction.id}
                          className={styles.transactionItem}
                          onClick={() => void navigate(`/transactions/${transaction.id}`)}
                        >
                          <div
                            className={`${styles.transactionIcon} ${getIconStyle(transaction.type)}`}
                          >
                            <IconComponent />
                          </div>
                          <div className={styles.transactionDetails}>
                            <Text className={styles.transactionTitle}>
                              {getRowTitle(transaction)}
                            </Text>
                            <Text className={styles.transactionSubtitle}>
                              {transaction.transactionNumber}
                            </Text>
                          </div>
                          <div className={styles.transactionRight}>
                            <Text
                              className={`${styles.transactionAmount} ${
                                isIncomeType(transaction.type)
                                  ? styles.amountPositive
                                  : styles.amountNegative
                              }`}
                            >
                              {formatTransactionAmount(transaction.amount, transaction.type)}
                            </Text>
                            {transaction.status === 'Completed' ? (
                              <Text className={styles.transactionTime}>
                                {formatTime(transaction.createdAt)}
                              </Text>
                            ) : (
                              <Text className={styles.statusText}>{transaction.status}</Text>
                            )}
                          </div>
                        </button>
                      );
                    })}
                  </div>
                ))}

                {hasNextPage && (
                  <div className={styles.loadMoreContainer}>
                    <Button
                      appearance="secondary"
                      disabled={isFetchingNextPage}
                      onClick={() => void fetchNextPage()}
                    >
                      {isFetchingNextPage ? <Spinner size="tiny" /> : 'Load more'}
                    </Button>
                  </div>
                )}
              </div>
            ) : (
              <div className={styles.emptyState}>
                <div className={styles.emptyIcon}>
                  <History24Filled style={{ width: '32px', height: '32px' }} />
                </div>
                <Text className={styles.emptyTitle}>No Transactions</Text>
                <Text className={styles.emptySubtitle}>
                  {activeFilter === 'all'
                    ? 'Your transactions will appear here.'
                    : 'No transactions found for the selected filter.'}
                </Text>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}

export default HistoryPage;
