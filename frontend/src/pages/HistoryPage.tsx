import { Fragment, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Button,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { History24Filled } from '@fluentui/react-icons';
import { colors, gradients } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import { PageHeader } from '../components/layout/PageHeader';
import { SegmentedFilter } from '../components/shared/SegmentedFilter';
import {
  TransactionDayRow,
  TransactionHead,
  TransactionRow,
  TransactionTable,
} from '../components/shared/TransactionRow';
import type { TransactionType } from '../api/enums';
import type { TransactionResponse } from '../features/api/apiSlice';
import { useGetTransactionHistoryInfiniteQuery } from '../features/api/apiSlice';
import { formatCurrency, formatDateHeading, isIncomeType } from '../utils/format';

// ============================================
// TYPES
// ============================================

/** The filters, and their labels, in one place rather than derived from the value by capitalising. */
const TRANSACTION_FILTERS = [
  { value: 'all', label: 'All' },
  { value: 'deposits', label: 'Deposits' },
  { value: 'withdrawals', label: 'Withdrawals' },
  { value: 'transfers', label: 'Transfers' },
] as const satisfies readonly { value: string; label: string }[];

type FilterType = 'all' | 'deposits' | 'withdrawals' | 'transfers';

interface GroupedTransactions {
  date: string;
  transactions: TransactionResponse[];
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  /**
   * The canvas belongs to the shell (AppLayout); the height is CLAIMED from it.
   *
   * `flex: 1` is not decoration. This page centres its loading, empty and not-found states with
   * `flex: 1` further down, and those need a parent that is taller than its content. That used to
   * come from `minHeight: 100vh` right here — which also cost 48px of phantom scroll on every
   * page — and now comes from the shell's content box, which is a column flex container for
   * exactly this reason. Drop this line and the empty state stops centring.
   */
  container: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
  },

  // ========== HEADER ==========

  // ========== CONTENT ==========
  content: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
  },

  // ========== FILTER TABS ==========

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
    // Was rgba(255,255,255,0.8), which is 3.69:1 on this card's lighter gradient stop — a second
    // failure on the same surface, found while measuring the one the bot flagged. Dimming white to
    // suggest hierarchy is the reflex; here it bought 1.18 of contrast away from 12px text. The
    // hierarchy is already carried by size and weight against the 16px/700 value beside it.
    color: tokens.colorNeutralForegroundOnBrand,
  },

  summaryValue: {
    fontSize: '16px',
    fontWeight: 700,
    color: tokens.colorNeutralForegroundOnBrand,
  },

  summaryValuePositive: {
    color: colors.semantic.onBrand.positive,
  },

  summaryValueNegative: {
    color: colors.semantic.onBrand.negative,
  },

  // ========== TRANSACTION LIST ==========
  transactionList: {
    flex: 1,
    backgroundColor: tokens.colorNeutralBackground1,
    overflowY: 'auto',
  },

  // ========== DATE HEADER ==========

  // ========== TRANSACTION ITEM ==========

  iconFallback: {
    backgroundColor: colors.neutral[100],
    color: colors.neutral[500],
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
    backgroundColor: tokens.colorNeutralBackground1,
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

  /**
   * How many of the loaded pages are SHOWN. Pages are never dropped from the cache — hiding them
   * costs nothing and re-expanding repeats no request — so this is a view cap, not a data change.
   */
  const [visiblePages, setVisiblePages] = useState(1);
  const loadedPages = data?.pages?.length ?? 0;

  /** Reveal an already-loaded page if there is one, otherwise fetch the next. */
  const showMore = async () => {
    if (visiblePages < loadedPages) {
      setVisiblePages((n) => n + 1);
      return;
    }
    await fetchNextPage();
    setVisiblePages((n) => n + 1);
  };

  const transactions = useMemo(
    () => (data?.pages ?? []).slice(0, visiblePages).flatMap((p) => p.data ?? []),
    [data, visiblePages],
  );

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

  return (
    <div className={styles.container}>
      {/* A nav destination, so no back affordance: a tab has no parent, and the navigation that
          got you here is on screen and lit. The title comes from the same table the nav reads —
          "History" — rather than being re-typed here as "Transaction History". */}
      <PageHeader />

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
            <SegmentedFilter
              label="Filter transactions by type"
              options={TRANSACTION_FILTERS}
              value={activeFilter}
              onChange={setActiveFilter}
            />

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
                <TransactionTable>
                  <TransactionHead />
                  <tbody>
                    {groupedTransactions.map((group) => (
                      <Fragment key={group.date}>
                        <TransactionDayRow label={group.date} columns={4} />
                        {group.transactions.map((transaction) => (
                          <TransactionRow
                            key={transaction.id}
                            transaction={transaction}
                            onOpen={(id) => navigate(`/transactions/${id}`)}
                          />
                        ))}
                      </Fragment>
                    ))}
                  </tbody>
                </TransactionTable>

                {/* Expanding used to be one-way: every press added a page and nothing took one
                    away, so a long history left you with hundreds of rows and only a reload to get
                    back. "Show less" hides pages rather than dropping them — the cache keeps them,
                    so re-expanding costs nothing and no request is repeated. */}
                <div className={styles.loadMoreContainer}>
                  {(hasNextPage || visiblePages < loadedPages) && (
                    <Button
                      appearance="secondary"
                      disabled={isFetchingNextPage}
                      onClick={() => void showMore()}
                    >
                      {isFetchingNextPage ? <Spinner size="tiny" /> : 'Load more'}
                    </Button>
                  )}
                  {visiblePages > 1 && (
                    <Button appearance="transparent" onClick={() => setVisiblePages(1)}>
                      Show less
                    </Button>
                  )}
                </div>
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
