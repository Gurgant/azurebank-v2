import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '../test/renderWithProviders';
import { RegisterPage } from './RegisterPage';

function RegisterHarness() {
  return (
    <Routes>
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/pin-setup" element={<div>PIN SETUP PAGE</div>} />
      <Route path="/dashboard" element={<div>DASHBOARD PAGE</div>} />
    </Routes>
  );
}

async function fillAndSubmit(user: ReturnType<typeof userEvent.setup>, email: string) {
  await user.type(screen.getByLabelText(/first name/i), 'Test');
  await user.type(screen.getByLabelText(/last name/i), 'User');
  await user.type(screen.getByLabelText(/azuretag/i), 'test_user');
  await user.type(screen.getByLabelText(/^email$/i), email);
  await user.type(screen.getByLabelText(/^password$/i), 'Password1!');
  await user.type(screen.getByLabelText(/confirm password/i), 'Password1!');
  await user.click(screen.getByRole('button', { name: /create account/i }));
}

describe('register → PIN setup handoff', () => {
  it('sends a brand-new (PIN-less) user to /pin-setup, not straight to the dashboard', async () => {
    const user = userEvent.setup();
    renderWithProviders(<RegisterHarness />, { routerEntries: ['/register'] });

    // The MSW register handler returns a fresh user with hasPin:false.
    await fillAndSubmit(user, 'fresh@azurebank.dev');

    expect(await screen.findByText('PIN SETUP PAGE')).toBeInTheDocument();
    expect(screen.queryByText('DASHBOARD PAGE')).not.toBeInTheDocument();
  });
});
