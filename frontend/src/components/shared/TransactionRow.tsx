import { mergeClasses, Text } from '@fluentui/react-components';
import { format } from 'date-fns';
import type { TransactionResponse } from '../../features/api/apiSlice';
import { formatCurrency, formatTime, isIncomeType } from '../../utils/format';
import {
  isVoided,
  transactionLabel,
  transactionNote,
  useTransactionRowStyles as useStyles,
} from './transactionRowStyles';

/**
 * ONE transaction row, for every screen that shows transactions.
 *
 * There were two, and they disagreed about the same transaction. The dashboard showed a status pill
 * on every row; History showed the status only when it was NOT `Completed`, so a settled payment
 * looked like it had no status at all. The dashboard struck through a reversed entry; History
 * printed it as a plain debit — which says the money left when it came back. The dashboard's
 * subtitle was the time, History's was the transaction number.
 *
 * None of that was a design decision on either side. It was drift, and the only way it stops
 * recurring is that there is nowhere for it to happen: one row, one set of rules.
 *
 * **A `<tr>`, not a list item, and that is deliberate.** Money wants columns — amounts and running
 * balances have to line up vertically to be comparable at a glance, which is what `tabular-nums`
 * in a fixed column buys and what a flex row cannot. History keeps its day grouping; a full-width
 * header row inside `<tbody>` does that without leaving the table.
 *
 * `showBalance` exists for the same reason the dashboard's does: `balanceAfter` is per-account, so
 * a running balance is only a number when the rows come from ONE account. The caller decides
 * because only the caller knows its own scope.
 */

export interface TransactionRowProps {
  transaction: TransactionResponse;
  /** Only pass `true` when the rows are scoped to a single account — see the note above. */
  showBalance?: boolean;
  /** Opens the transaction. Omit to render a row that is not a link. */
  onOpen?: (id: string) => void;
  /** Hides every figure, for the dashboard's privacy toggle. */
  hidden?: boolean;
}

export const TRANSACTION_COLUMNS = ['When', 'Entry', 'Amount', 'Status'] as const;

export function TransactionRow({
  transaction: t,
  showBalance = false,
  onOpen,
  hidden = false,
}: TransactionRowProps) {
  const styles = useStyles();
  const income = isIncomeType(t.type);
  const voided = isVoided(t);
  const money = (n: number) => (hidden ? '••••••' : formatCurrency(n));

  return (
    <tr>
      <td className={mergeClasses(styles.td, styles.when)}>
        {format(new Date(t.createdAt), 'd MMM')}
        <br />
        {formatTime(t.createdAt)}
      </td>

      <td className={styles.td}>
        {onOpen ? (
          <button type="button" className={styles.open} onClick={() => onOpen(t.id)}>
            {transactionLabel(t)}
          </button>
        ) : (
          transactionLabel(t)
        )}
        {transactionNote(t) && <Text className={styles.note}>{transactionNote(t)}</Text>}
      </td>

      <td
        className={mergeClasses(
          styles.td,
          styles.num,
          income ? styles.in : styles.out,
          voided && styles.void,
        )}
      >
        {income ? '+' : '-'}
        {money(t.amount)}
      </td>

      {showBalance && (
        <td className={mergeClasses(styles.td, styles.num, styles.balance)}>
          {money(t.balanceAfter)}
        </td>
      )}

      <td className={styles.td}>
        <span
          className={mergeClasses(
            styles.pill,
            t.status === 'Pending' && styles.pillPending,
            t.status === 'Completed' && styles.pillDone,
          )}
        >
          {t.status}
        </span>
      </td>
    </tr>
  );
}

/**
 * The header row, so the columns and their order cannot drift from the cells beneath them either.
 * `Balance` appears on the same condition the cell does — the pair is the whole point.
 */
export function TransactionHead({ showBalance = false }: { showBalance?: boolean }) {
  const styles = useStyles();
  return (
    <thead>
      <tr>
        <th className={styles.th} style={{ width: '22%' }}>
          When
        </th>
        <th className={styles.th}>Entry</th>
        <th className={mergeClasses(styles.th, styles.num)} style={{ width: '20%' }}>
          Amount
        </th>
        {showBalance && (
          <th
            className={mergeClasses(styles.th, styles.num, styles.balance)}
            style={{ width: '20%' }}
          >
            Balance
          </th>
        )}
        <th className={styles.th} style={{ width: '18%' }}>
          Status
        </th>
      </tr>
    </thead>
  );
}

/** A day's heading, spanning the table so grouping does not cost the columns. */
export function TransactionDayRow({ label, columns }: { label: string; columns: number }) {
  const styles = useStyles();
  return (
    <tr>
      <td className={styles.dayRow} colSpan={columns}>
        <Text>{label}</Text>
      </td>
    </tr>
  );
}

/**
 * The loading row, sized from the same cells as the real one.
 *
 * It lives here rather than in each page for the reason the row does: a skeleton whose height does
 * not match its content produces the layout shift it exists to prevent, and two copies drift.
 */
export function TransactionRowSkeleton({ showBalance = false }: { showBalance?: boolean }) {
  const styles = useStyles();
  const cell = <div className={styles.skeletonBar} />;
  return (
    <tr>
      <td className={styles.td}>{cell}</td>
      <td className={styles.td}>{cell}</td>
      <td className={styles.td}>{cell}</td>
      {showBalance && <td className={mergeClasses(styles.td, styles.balance)}>{cell}</td>}
      <td className={styles.td}>{cell}</td>
    </tr>
  );
}

/**
 * The status, on its own, for the places that show one outside a table — the dashboard's "needs
 * attention" list. Same vocabulary, same colours, same never-colour-alone rule.
 */
export function StatusPill({ status }: { status: TransactionResponse['status'] }) {
  const styles = useStyles();
  return (
    <span
      className={mergeClasses(
        styles.pill,
        status === 'Pending' && styles.pillPending,
        status === 'Completed' && styles.pillDone,
      )}
    >
      {status}
    </span>
  );
}

/** A row that spans the table to say the list is empty, without breaking the column count. */
export function TransactionEmptyRow({
  children,
  columns,
}: {
  children: React.ReactNode;
  columns: number;
}) {
  const styles = useStyles();
  return (
    <tr>
      <td className={mergeClasses(styles.td, styles.muted)} colSpan={columns}>
        {children}
      </td>
    </tr>
  );
}

/** Shared table shell, so the two ledgers cannot disagree about borders or layout either. */
export function TransactionTable({ children }: { children: React.ReactNode }) {
  const styles = useStyles();
  return <table className={styles.table}>{children}</table>;
}

export default TransactionRow;
