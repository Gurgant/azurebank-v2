import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { TransactionResponse } from '../../features/api/apiSlice';
import { renderWithProviders } from '../../test/renderWithProviders';
import { StatusPill, TransactionRow, TransactionTable } from './TransactionRow';

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
