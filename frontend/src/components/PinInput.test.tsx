import { useState } from 'react';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '../test/renderWithProviders';
import { PinInput } from './PinInput';

/**
 * PR-10 — the reusable 6-box PIN entry. Pins the controlled compact-value contract:
 * per-box typing, paste distribution, backspace splice, and the onComplete latch.
 */

function Harness({ length = 6 }: { length?: number }) {
  const [value, setValue] = useState('');
  const [completed, setCompleted] = useState('');
  return (
    <div>
      <PinInput
        value={value}
        onChange={setValue}
        onComplete={setCompleted}
        length={length}
        ariaLabel="PIN"
      />
      <div data-testid="value">{value}</div>
      <div data-testid="completed">{completed}</div>
    </div>
  );
}

describe('PinInput (PR-10)', () => {
  it('renders one box per digit', () => {
    renderWithProviders(<Harness />);
    expect(screen.getAllByLabelText(/Digit \d of 6/)).toHaveLength(6);
  });

  it('distributes a pasted code across the boxes and fires onComplete', async () => {
    renderWithProviders(<Harness />);
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123456');

    expect(screen.getByLabelText('Digit 1 of 6')).toHaveValue('1');
    expect(screen.getByLabelText('Digit 6 of 6')).toHaveValue('6');
    expect(screen.getByTestId('value')).toHaveTextContent('123456');
    expect(screen.getByTestId('completed')).toHaveTextContent('123456');
  });

  it('accepts per-box typing and auto-advances', async () => {
    renderWithProviders(<Harness />);
    for (let i = 1; i <= 6; i++) {
      await userEvent.type(screen.getByLabelText(`Digit ${i} of 6`), String(i));
    }
    expect(screen.getByTestId('value')).toHaveTextContent('123456');
  });

  it('strips non-digits from a paste', async () => {
    renderWithProviders(<Harness />);
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('12ab34');
    expect(screen.getByTestId('value')).toHaveTextContent('1234');
    expect(screen.getByTestId('completed')).toHaveTextContent('');
  });

  it('backspace removes the last digit', async () => {
    renderWithProviders(<Harness />);
    await userEvent.click(screen.getByLabelText('Digit 1 of 6'));
    await userEvent.paste('123');
    await userEvent.type(screen.getByLabelText('Digit 3 of 6'), '{backspace}');
    expect(screen.getByTestId('value')).toHaveTextContent('12');
  });

  it('never drops a keystroke typed into an empty out-of-range box (append, not ignore)', async () => {
    // Regression: with the value empty, typing into box 4 must register (append to the
    // first empty slot) rather than being silently discarded — the wrong-PIN retry path.
    renderWithProviders(<Harness />);
    await userEvent.type(screen.getByLabelText('Digit 4 of 6'), '7');
    expect(screen.getByTestId('value')).toHaveTextContent('7');
  });

  it('masks digits by default and reveals them on the toggle', async () => {
    renderWithProviders(<Harness />);
    expect(screen.getByLabelText('Digit 1 of 6')).toHaveAttribute('type', 'password');
    await userEvent.click(screen.getByRole('button', { name: 'Show PIN' }));
    expect(screen.getByLabelText('Digit 1 of 6')).toHaveAttribute('type', 'text');
  });
});
