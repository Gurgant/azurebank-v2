import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes, useLocation } from 'react-router-dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../mocks/server';
import { renderWithProviders } from '../test/renderWithProviders';
import { RegisterPage } from './RegisterPage';

// Probe the PIN-setup destination so the test can assert the returnTo the handoff carries, not just
// that the route rendered.
function PinSetupProbe() {
  const returnTo = new URLSearchParams(useLocation().search).get('returnTo') ?? '';
  return (
    <>
      <div>PIN SETUP PAGE</div>
      <div data-testid="returnTo">{returnTo}</div>
    </>
  );
}

function RegisterHarness() {
  return (
    <Routes>
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/pin-setup" element={<PinSetupProbe />} />
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
  it('sends a brand-new (PIN-less) user to /pin-setup with returnTo=/dashboard', async () => {
    const user = userEvent.setup();
    renderWithProviders(<RegisterHarness />, { routerEntries: ['/register'] });

    // The default MSW register handler returns a fresh user with hasPin:false.
    await fillAndSubmit(user, 'fresh@azurebank.dev');

    expect(await screen.findByText('PIN SETUP PAGE')).toBeInTheDocument();
    expect(screen.getByTestId('returnTo')).toHaveTextContent('/dashboard');
    expect(screen.queryByText('DASHBOARD PAGE')).not.toBeInTheDocument();
  });

  it('sends an already-PIN’d registration straight to the dashboard', async () => {
    // The other branch of the handoff: a 201 whose user already has a PIN skips the wizard.
    server.use(
      http.post('*/bff/auth/register', () =>
        HttpResponse.json(
          {
            data: {
              user: {
                id: '019f7b3f-0000-7000-8000-00000000aaaa',
                email: 'pinned@azurebank.dev',
                firstName: 'P',
                lastName: 'U',
                azureTag: 'pinned_user',
                hasPin: true,
              },
              // Far-future + fixed (the repo's mocks avoid Date.now()): a successful-registration
              // fixture must not ship a pre-expired session.
              expiresAt: '2099-01-01T00:00:00.0000000Z',
            },
            message: 'ok',
          },
          { status: 201 },
        ),
      ),
    );
    const user = userEvent.setup();
    renderWithProviders(<RegisterHarness />, { routerEntries: ['/register'] });

    await fillAndSubmit(user, 'pinned@azurebank.dev');

    expect(await screen.findByText('DASHBOARD PAGE')).toBeInTheDocument();
    expect(screen.queryByText('PIN SETUP PAGE')).not.toBeInTheDocument();
  });
});
