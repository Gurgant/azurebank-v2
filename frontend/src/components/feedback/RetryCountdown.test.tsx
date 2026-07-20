import { StrictMode } from 'react';
import { screen, waitFor } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { RetryCountdown } from './RetryCountdown';

it('renders the remaining time (seconds under a minute, m:ss above)', () => {
  renderWithProviders(<RetryCountdown deadline={Date.now() + 45_000} />);
  expect(screen.getByRole('timer')).toHaveTextContent(/^Try again in 4[45]s$/);
});

it('formats minute-scale lockouts as m:ss', () => {
  renderWithProviders(<RetryCountdown deadline={Date.now() + 90_000} />);
  expect(screen.getByRole('timer')).toHaveTextContent(/^Try again in 1:[23][0-9]$/);
});

it('renders nothing and fires onElapsed once for an already-elapsed deadline (StrictMode)', async () => {
  const onElapsed = vi.fn();
  // StrictMode double-invokes effects: the pre-fix sync branch fired onElapsed twice.
  renderWithProviders(
    <StrictMode>
      <RetryCountdown deadline={Date.now() - 1000} onElapsed={onElapsed} />
    </StrictMode>,
  );
  await waitFor(() => expect(onElapsed).toHaveBeenCalled());
  await new Promise((resolve) => setTimeout(resolve, 25));
  expect(onElapsed).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole('timer')).not.toBeInTheDocument();
});
