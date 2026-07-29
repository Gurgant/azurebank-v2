import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { ResultUnknownView } from './ResultUnknownView';

/**
 * `transfer-behaviour.test.tsx` already pins this screen through BOTH transfer flows. This pins the
 * component itself, which is a different obligation: the flows prove the screen appears with no way
 * out, and these prove the component cannot grow one.
 *
 * The distinction matters because the absence of exits here is the safety property. A future
 * `onClose` prop, or a `PageHeader` that starts defaulting to a Back button, would pass every
 * flow-level test that asserts the two forward actions exist while quietly letting a user walk away
 * from a transfer that may or may not have taken their money.
 */
describe('ResultUnknownView', () => {
  const render = (overrides: Partial<Parameters<typeof ResultUnknownView>[0]> = {}) =>
    renderWithProviders(
      <ResultUnknownView
        title="Send Money"
        repeatWarning="send twice"
        onCheckTransactions={() => {}}
        onStartOver={() => {}}
        {...overrides}
      />,
    );

  it('offers exactly two actions and no way to leave', () => {
    render();

    const buttons = screen.getAllByRole('button');
    expect(buttons.map((b) => b.textContent)).toEqual([
      'Check recent transactions',
      "It didn't go through — start over",
    ]);
    // Named individually too: the array above would still pass if a Back button were added with an
    // icon and no text content.
    expect(screen.queryByRole('button', { name: 'Back' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Close' })).not.toBeInTheDocument();
  });

  it('never navigates or resets on its own — both actions are the caller’s', async () => {
    // The invariant PageHeader and MoneyDialogShell also state. `onStartOver` has to re-arm the
    // idempotency intent, which only the owning flow's wizard can do; a component that called
    // navigate() here would strand the flow with a latched verifyRequired.
    const onCheckTransactions = vi.fn();
    const onStartOver = vi.fn();
    render({ onCheckTransactions, onStartOver });

    await userEvent.click(screen.getByRole('button', { name: 'Check recent transactions' }));
    expect(onCheckTransactions).toHaveBeenCalledTimes(1);

    await userEvent.click(screen.getByRole('button', { name: /didn't go through/ }));
    expect(onStartOver).toHaveBeenCalledTimes(1);
  });

  it('says the caller’s words, so neither flow inherits the other’s copy', () => {
    // Passed as words rather than a flow discriminator: the sentence is assembled once and neither
    // flow can grow a branch the other cannot see.
    render({ title: 'Move Money', repeatWarning: 'move the money twice' });

    expect(screen.getByText(/retrying blindly could move the money twice/)).toBeInTheDocument();
    expect(screen.queryByText(/could send twice/)).not.toBeInTheDocument();
  });
});
