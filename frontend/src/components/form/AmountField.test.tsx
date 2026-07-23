import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { withdrawFormSchema, type WithdrawFormValues } from '../../forms/moneySchemas';
import { AmountField } from './AmountField';

const classNames = { wrapper: 'w', currency: 'c', input: 'i', hint: 'h' };

/**
 * G2/G3 spike harness: a real RHF form with the real withdraw schema (balance €830), proving
 * (1) the sanitizer runs through the Controller, (2) the hint shows the schema's exact legacy
 * message only once something is typed, (3) RHF's focus-on-first-error reaches the native
 * input through the Controller ref — the pattern every money form relies on.
 */
function Harness({ onBodyEdit }: { onBodyEdit?: () => void }) {
  const { control, handleSubmit } = useForm<WithdrawFormValues>({
    resolver: zodResolver(withdrawFormSchema(830)),
    mode: 'onChange',
    defaultValues: { accountId: 'a1', amount: '', description: '' },
  });
  return (
    <form onSubmit={(e) => void handleSubmit(() => {})(e)}>
      <AmountField
        control={control}
        name="amount"
        ariaLabel="Withdraw amount"
        onBodyEdit={onBodyEdit}
        classNames={classNames}
      />
      <button type="submit">Go</button>
    </form>
  );
}

describe('AmountField (G2/G3 spike)', () => {
  it('sanitizes through the Controller: multiple dots collapse, 2-decimal clamp', () => {
    render(<Harness />);
    const input = screen.getByLabelText('Withdraw amount');
    fireEvent.change(input, { target: { value: '1.2.3' } });
    expect(input).toHaveValue('1.23');
    fireEvent.change(input, { target: { value: '9.999' } });
    expect(input).toHaveValue('9.99');
  });

  it('shows the exact legacy balance message only once something is typed', async () => {
    render(<Harness />);
    const input = screen.getByLabelText('Withdraw amount');
    // Empty → no hint (the CTA-disabled path is silent, like the legacy forms).
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    fireEvent.change(input, { target: { value: '900' } });
    expect(await screen.findByText('Exceeds available balance of €830.00.')).toBeInTheDocument();
    // Clearing back to empty silences the hint again.
    fireEvent.change(input, { target: { value: '' } });
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('focus-on-first-error reaches the native input through the Controller ref (G3)', async () => {
    render(<Harness />);
    fireEvent.click(screen.getByRole('button', { name: 'Go' }));
    const input = screen.getByLabelText('Withdraw amount');
    await vi.waitFor(() => expect(input).toHaveFocus());
  });

  it('calls onBodyEdit on every change (the key-rotation invariant)', () => {
    const onBodyEdit = vi.fn();
    render(<Harness onBodyEdit={onBodyEdit} />);
    fireEvent.change(screen.getByLabelText('Withdraw amount'), { target: { value: '5' } });
    fireEvent.change(screen.getByLabelText('Withdraw amount'), { target: { value: '50' } });
    expect(onBodyEdit).toHaveBeenCalledTimes(2);
  });
});
