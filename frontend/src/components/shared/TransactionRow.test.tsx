import { fireEvent, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { TransactionResponse } from '../../features/api/apiSlice';
import { renderWithProviders } from '../../test/renderWithProviders';
import {
  StatusPill,
  TransactionBody,
  TransactionDayRow,
  TransactionEmptyRow,
  TransactionFoot,
  TransactionHead,
  TransactionRow,
  TransactionRowSkeleton,
  TransactionTable,
} from './TransactionRow';

/**
 * The row exists so that two screens cannot disagree about one transaction. This file pins the
 * weaker version of that promise that the component itself broke: the row inlined its own copy of
 * the status pill, byte-identical to `StatusPill` a hundred lines below it, in a file whose entire
 * docblock is about not keeping two copies of one rule.
 *
 * It rendered correctly, which is exactly why nothing caught it. What follows compares the two
 * renderings structurally rather than by eye, so a re-inlined copy fails the moment it differs.
 */

const STATUSES: TransactionResponse['status'][] = ['Completed', 'Pending', 'Reversed', 'Failed'];

function transaction(status: TransactionResponse['status']): TransactionResponse {
  return {
    id: `019f7b3f-0000-7000-8000-0000000000${STATUSES.indexOf(status)}1`,
    transactionNumber: 'TXN-20260720-000999',
    type: 'Deposit',
    amount: 10,
    balanceAfter: 100,
    description: 'A row',
    recipientAzureTag: null,
    senderAzureTag: null,
    status,
    createdAt: '2026-07-20T09:15:00.0000000Z',
  };
}

describe('the status pill has exactly one implementation', () => {
  it.each(STATUSES)('renders %s identically inside a row and on its own', (status) => {
    const inRow = renderWithProviders(
      <TransactionTable>
        <tbody>
          <TransactionRow transaction={transaction(status)} />
        </tbody>
      </TransactionTable>,
    );
    const rowPill = screen.getByText(status);
    const rowClasses = rowPill.className;
    inRow.unmount();

    renderWithProviders(<StatusPill status={status} />);
    const standalone = screen.getByText(status);

    // Same element, same Griffel classes. Not "looks the same" — the same set of rules.
    expect(standalone.tagName).toBe(rowPill.tagName);
    expect(standalone.className.split(' ').sort()).toEqual(rowClasses.split(' ').sort());
  });

  it('colours Pending and Completed differently, so the comparison above is not vacuous', () => {
    const pending = renderWithProviders(<StatusPill status="Pending" />);
    const pendingClasses = screen.getByText('Pending').className;
    pending.unmount();

    renderWithProviders(<StatusPill status="Completed" />);
    expect(screen.getByText('Completed').className).not.toBe(pendingClasses);
  });
});

/**
 * THE LEDGER STAYS A TABLE, HOWEVER IT IS DRAWN.
 *
 * Under 480 px of its own container a row is laid out on two lines by a grid, the head is not
 * drawn, and no element is a table's row or cell to the layout any more
 * (`transactionRowStyles.ts`). An element whose `display` is no longer the table's own can lose
 * the role the browser gave it, so each one carries its role as an attribute, and that is what
 * this block holds. jsdom applies no container query and lays nothing out: where the two lines
 * are drawn is held in a browser, by `e2e/deposit.spec.ts`.
 *
 * The role is read as an ATTRIBUTE on purpose. `getByRole('row')` finds a `<tr>` that says nothing
 * about itself, by the role HTML implies for it, and would pass on a table that had lost it.
 */
function role(element: Element): string | null {
  return element.getAttribute('role');
}

describe('the ledger stays a table to assistive technology', () => {
  it('names every element of the table by a role of its own', () => {
    const { container } = renderWithProviders(
      <TransactionTable>
        <TransactionHead />
        <TransactionBody>
          <TransactionDayRow label="July 20, 2026" columns={4} />
          <TransactionRow transaction={transaction('Completed')} onOpen={() => {}} />
          <TransactionRowSkeleton />
          <TransactionEmptyRow columns={4}>Nothing yet.</TransactionEmptyRow>
        </TransactionBody>
        <TransactionFoot label="July so far" total="+€10.00" />
      </TransactionTable>,
    );
    const roles = (selector: string) => [...container.querySelectorAll(selector)].map(role);

    expect(roles('table')).toEqual(['table']);
    expect(roles('thead, tbody, tfoot')).toEqual(['rowgroup', 'rowgroup', 'rowgroup']);
    // The head's, the day's, the transaction's, the loading one, the empty one and the foot's.
    expect(roles('tr')).toEqual(['row', 'row', 'row', 'row', 'row', 'row']);
    expect(roles('th')).toEqual(['columnheader', 'columnheader', 'columnheader', 'columnheader']);
    // 1 for the day, 4 and 4 for the two rows, 1 for the empty one, 3 for the foot.
    const cells = roles('td');
    expect(cells).toHaveLength(13);
    expect(new Set(cells)).toEqual(new Set(['cell']));
  });

  it('puts the four cells of a row under the four headings, in their order', () => {
    renderWithProviders(
      <TransactionTable>
        <TransactionHead />
        <TransactionBody>
          <TransactionRow transaction={transaction('Completed')} onOpen={() => {}} />
        </TransactionBody>
      </TransactionTable>,
    );
    expect(screen.getAllByRole('columnheader').map((heading) => heading.textContent)).toEqual([
      'When',
      'Entry',
      'Amount',
      'Status',
    ]);

    const row = screen.getByRole('button', { name: 'A row' }).closest('tr')!;
    const cells = [...row.children];
    expect(cells.map(role)).toEqual(['cell', 'cell', 'cell', 'cell']);
    // The date and the time with a mark between them, for the line they share on a phone. The
    // day and the hour are the reader's own zone's, so only their shape is held.
    expect(cells[0].textContent).toMatch(/^\d{1,2} Jul · \d{1,2}:\d{2} [AP]M$/);
    expect(cells[1]).toHaveTextContent('A row');
    expect(cells[2]).toHaveTextContent('+€10.00');
    expect(cells[3]).toHaveTextContent('Completed');
  });

  it('keeps the Balance heading and its cell together, fourth of five', () => {
    renderWithProviders(
      <TransactionTable>
        <TransactionHead showBalance />
        <TransactionBody>
          <TransactionRow transaction={transaction('Completed')} showBalance />
        </TransactionBody>
        <TransactionFoot label="July so far" total="+€10.00" showBalance />
      </TransactionTable>,
    );
    // `hidden`, because jsdom reads no media query: there the Balance column is never shown.
    const headings = screen.getAllByRole('columnheader', { hidden: true });
    expect(headings.map((heading) => heading.textContent)).toEqual([
      'When',
      'Entry',
      'Amount',
      'Balance',
      'Status',
    ]);
    expect(headings.map(role)).toEqual(Array(5).fill('columnheader'));

    const [, row, foot] = document.querySelectorAll('tr');
    expect([...row.children].map(role)).toEqual(Array(5).fill('cell'));
    expect(row.children[3]).toHaveTextContent('€100.00');
    // The foot's label spans When and Entry, so its four cells cover the same five columns.
    expect([...foot.children].map(role)).toEqual(Array(4).fill('cell'));
    expect(foot.children[0]).toHaveAttribute('colspan', '2');
  });

  it('says the foot in a row of the table: its label, then its total', () => {
    renderWithProviders(
      <TransactionTable>
        <TransactionFoot label="July so far · 1 pending" total="+€1,475.50" />
      </TransactionTable>,
    );
    const foot = document.querySelector('tfoot')!;
    const cells = within(foot).getAllByRole('cell');
    expect(cells.map((cell) => cell.textContent)).toEqual([
      'July so far · 1 pending',
      '+€1,475.50',
      '',
    ]);
  });
});

/**
 * NOTHING IN A ROW WAITS FOR A POINTER. A phone has none to hover with, so a row keeps nothing
 * in a `title`, which only a hovering pointer shows, and its entry is a button: what a tap opens
 * is the transaction's own page, where everything about it is whole.
 */
describe('a row asks nothing of a pointer that hovers', () => {
  it('carries no title attribute, on any status', () => {
    const { container } = renderWithProviders(
      <TransactionTable>
        <TransactionHead showBalance />
        <TransactionBody>
          {STATUSES.map((status) => (
            <TransactionRow
              key={status}
              transaction={transaction(status)}
              showBalance
              onOpen={() => {}}
            />
          ))}
        </TransactionBody>
        <TransactionFoot label="July so far" total="+€10.00" showBalance />
      </TransactionTable>,
    );
    // The rows are there, each with its button: a table with nothing in it has no title either.
    expect(screen.getAllByRole('button', { name: 'A row' })).toHaveLength(STATUSES.length);
    expect(container.querySelectorAll('[title]')).toHaveLength(0);
  });

  it('opens the transaction from its entry, which is a button', () => {
    const onOpen = vi.fn();
    const shown = transaction('Completed');
    renderWithProviders(
      <TransactionTable>
        <TransactionBody>
          <TransactionRow transaction={shown} onOpen={onOpen} />
        </TransactionBody>
      </TransactionTable>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'A row' }));
    expect(onOpen).toHaveBeenCalledTimes(1);
    expect(onOpen).toHaveBeenCalledWith(shown.id);
  });
});
