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

it('renders nothing and fires onElapsed once for an already-elapsed deadline', async () => {
  const onElapsed = vi.fn();
  renderWithProviders(<RetryCountdown deadline={Date.now() - 1000} onElapsed={onElapsed} />);
  await waitFor(() => expect(onElapsed).toHaveBeenCalledTimes(1));
  expect(screen.queryByRole('timer')).not.toBeInTheDocument();
});
