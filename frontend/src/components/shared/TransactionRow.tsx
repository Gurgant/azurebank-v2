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
 * Since 2026-10-06 that `<tr>` is drawn on two lines where the table's own box is under 480 px,
 * which is a phone held upright: four columns do not fit one, and an amount ran under the status
 * beside it. The measurements and the layout are with `NARROW` in `transactionRowStyles.ts`. It
 * is the same `<tr>` of the same `<table>` at every width: the amounts still end on one edge, in
 * tabular figures, and every element says its role in the markup, because a browser may stop
 * treating as a row a `<tr>` that is not displayed as one. Read that day in Chromium's
 * accessibility tree at a 375 px screen: one table, its four column headers, and four cells to
 * each transaction's row, on the dashboard and on History.
 *
 * **Nothing in a row waits for a pointer that hovers.** A phone has none: no `title`, and no
 * ellipsis, whose hidden half only a tooltip could show. What does not fit a line wraps, and
 * the entry is a button that opens the transaction's own page.
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
    <tr role="row" className={styles.row}>
      <td
        role="cell"
        className={mergeClasses(styles.td, styles.when, styles.cell, styles.cellWhen)}
      >
        {format(new Date(t.createdAt), 'd MMM')}
        <br className={styles.whenBreak} />
        <span className={styles.whenJoin}>{' · '}</span>
        {formatTime(t.createdAt)}
      </td>

      <td role="cell" className={mergeClasses(styles.td, styles.cell, styles.cellEntry)}>
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
        role="cell"
        className={mergeClasses(
          styles.td,
          styles.num,
          income ? styles.in : styles.out,
          voided && styles.void,
          styles.cell,
          styles.cellAmount,
        )}
      >
        {income ? '+' : '-'}
        {money(t.amount)}
      </td>

      {showBalance && (
        <td role="cell" className={mergeClasses(styles.td, styles.num, styles.balance)}>
          {money(t.balanceAfter)}
        </td>
      )}

      <td role="cell" className={mergeClasses(styles.td, styles.cell, styles.cellStatus)}>
        <StatusPill status={t.status} />
      </td>
    </tr>
  );
}

/**
 * The header row, so the columns and their order cannot drift from the cells beneath them either.
 * `Balance` appears on the same condition the cell does — the pair is the whole point.
 *
 * Where a row is two lines the headings are not drawn and are still read (`head` in the styles).
 */
export function TransactionHead({ showBalance = false }: { showBalance?: boolean }) {
  const styles = useStyles();
  return (
    <thead role="rowgroup" className={styles.head}>
      <tr role="row">
        <th role="columnheader" className={styles.th} style={{ width: '22%' }}>
          When
        </th>
        <th role="columnheader" className={styles.th}>
          Entry
        </th>
        <th
          role="columnheader"
          className={mergeClasses(styles.th, styles.num)}
          style={{ width: '20%' }}
        >
          Amount
        </th>
        {showBalance && (
          <th
            role="columnheader"
            className={mergeClasses(styles.th, styles.num, styles.balance)}
            style={{ width: '20%' }}
          >
            Balance
          </th>
        )}
        <th role="columnheader" className={mergeClasses(styles.th, styles.statusHead)}>
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
    <tr role="row" className={styles.block}>
      <td role="cell" className={mergeClasses(styles.dayRow, styles.block)} colSpan={columns}>
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
 *
 * Where a row is two lines so is this one, its four bars where the four cells will be.
 */
export function TransactionRowSkeleton({ showBalance = false }: { showBalance?: boolean }) {
  const styles = useStyles();
  const cell = <div className={styles.skeletonBar} />;
  return (
    <tr role="row" className={styles.row}>
      <td
        role="cell"
        className={mergeClasses(styles.td, styles.cell, styles.cellWhen, styles.skeletonCell)}
      >
        {cell}
      </td>
      <td
        role="cell"
        className={mergeClasses(styles.td, styles.cell, styles.cellEntry, styles.skeletonCell)}
      >
        {cell}
      </td>
      <td
        role="cell"
        className={mergeClasses(styles.td, styles.cell, styles.cellAmount, styles.skeletonCell)}
      >
        {cell}
      </td>
      {showBalance && (
        <td role="cell" className={mergeClasses(styles.td, styles.balance)}>
          {cell}
        </td>
      )}
      <td
        role="cell"
        className={mergeClasses(styles.td, styles.cell, styles.cellStatus, styles.skeletonCell)}
      >
        {cell}
      </td>
    </tr>
  );
}

/**
 * The status pill — every one of them, in the row above and in the places that show one outside a
 * table (the dashboard's "needs attention" list).
 *
 * The row used to inline its own copy of this. Byte-identical logic, and still wrong: a file whose
 * whole subject is that two copies of one rule drift apart had two copies of the status rule in it.
 * Caught in review, which is the point — the version that renders correctly and the version that is
 * structurally safe are not the same thing, and only the second one survives the next edit.
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
    <tr role="row" className={styles.block}>
      <td
        role="cell"
        className={mergeClasses(styles.td, styles.muted, styles.block)}
        colSpan={columns}
      >
        {children}
      </td>
    </tr>
  );
}

/**
 * Shared table shell, so the two ledgers cannot disagree about borders or layout either.
 *
 * The `div` is the box the table's width is measured against: under 480 px of it a row is two
 * lines (`NARROW` in the styles).
 */
export function TransactionTable({ children }: { children: React.ReactNode }) {
  const styles = useStyles();
  return (
    <div className={styles.frame}>
      <table role="table" className={styles.table}>
        {children}
      </table>
    </div>
  );
}

/**
 * The rows' group. A component, though it is one element, so that a page cannot write a
 * `<tbody>` without its role and its class: where a row is two lines the group is a block, and
 * says by its role what it still is.
 */
export function TransactionBody({ children }: { children: React.ReactNode }) {
  const styles = useStyles();
  return (
    <tbody role="rowgroup" className={styles.block}>
      {children}
    </tbody>
  );
}

/**
 * The foot: one row under the others, a label and a total, for the dashboard's month so far.
 *
 * Until 2026-10-06 the dashboard wrote this row itself, with the cells' classes handed to it
 * by a hook of the styles module so that its Balance placeholder came and went with the column.
 * It is here now for the reason the row is: the foot has to be laid out on the same condition as
 * the rows above it, in columns or on a line, and a second copy of that condition is the copy
 * that drifts.
 *
 * Its label spans When and Entry and its total stands under Amount; the cell under Status is
 * empty, and so is the one under Balance when that column is shown.
 */
export function TransactionFoot({
  label,
  total,
  showBalance = false,
}: {
  label: React.ReactNode;
  total: React.ReactNode;
  showBalance?: boolean;
}) {
  const styles = useStyles();
  return (
    <tfoot role="rowgroup" className={styles.block}>
      <tr role="row" className={styles.footRow}>
        <td role="cell" className={styles.foot} colSpan={2}>
          {label}
        </td>
        <td role="cell" className={mergeClasses(styles.foot, styles.num)}>
          {total}
        </td>
        {showBalance && <td role="cell" className={mergeClasses(styles.foot, styles.balance)} />}
        <td role="cell" className={mergeClasses(styles.foot, styles.footSpare)} />
      </tr>
    </tfoot>
  );
}

export default TransactionRow;
