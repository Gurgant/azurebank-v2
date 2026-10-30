import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { StatusSlot } from './StatusSlot';

/**
 * The polite slot is in the page from the first render, empty until a sentence is put into it.
 * Measured the same way as the account number's clipped words: out of the flow, still in the
 * accessibility tree.
 */
describe('StatusSlot', () => {
  it('is there from the first render, empty, and out of the flow', () => {
    renderWithProviders(<StatusSlot />);

    const slot = screen.getByRole('status');
    expect(slot).toBeEmptyDOMElement();
    expect(slot).toHaveAttribute('aria-atomic', 'true');
    expect({
      position: getComputedStyle(slot).position,
      width: getComputedStyle(slot).width,
      height: getComputedStyle(slot).height,
      overflow: getComputedStyle(slot).overflow,
    }).toStrictEqual({
      position: 'absolute',
      width: '1px',
      height: '1px',
      overflow: 'hidden',
    });
  });

  it('shows a child', () => {
    renderWithProviders(
      <StatusSlot>
        <span>Your new account &quot;Holiday&quot; is open.</span>
      </StatusSlot>,
    );

    const slot = screen.getByRole('status');
    expect(slot).toHaveTextContent('Your new account "Holiday" is open.');
    expect(getComputedStyle(slot).position).not.toBe('absolute');
  });
});
