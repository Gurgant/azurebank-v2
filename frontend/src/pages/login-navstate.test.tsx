import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Route, Routes } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { MOCK_PASSWORD, MOCK_USER } from '../mocks/state';
import { renderWithProviders } from '../test/renderWithProviders';
import { LoginPage } from './LoginPage';

const EXPIRED_NOTE = 'Your session has expired. Please sign in again.';

function LoginHarness() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/dashboard" element={<div>DASHBOARD</div>} />
      <Route path="/transfer" element={<div>TRANSFER</div>} />
    </Routes>
  );
}

// The prefillEmail validation is exercised via the expiry banner rather than the (RHF-seeded, not
// reliably reflected in jsdom) email input value: a VALID prefillEmail keeps the whole nav state,
// so the reason=expired banner shows; an INVALID prefillEmail invalidates the entire safeParse, so
// the banner disappears even though `reason` itself was valid.
describe('LoginPage navigation-state validation', () => {
  it('keeps a VALID nav state — the expiry banner shows (valid prefillEmail accepted)', async () => {
    renderWithProviders(<LoginHarness />, {
      routerEntries: [
        { pathname: '/login', state: { prefillEmail: 'seed@azurebank.dev', reason: 'expired' } },
      ],
    });
    expect(await screen.findByText(EXPIRED_NOTE)).toBeInTheDocument();
  });

  it('drops a MALFORMED nav state to {} — a bad prefillEmail kills the whole state', async () => {
    renderWithProviders(<LoginHarness />, {
      routerEntries: [
        { pathname: '/login', state: { prefillEmail: 'not-an-email', reason: 'expired' } },
      ],
    });
    // Page is mounted (so "no banner" is meaningful, not just "nothing rendered")...
    await screen.findByLabelText(/email address/i);
    // ...but the expiry note is gone: the invalid email failed the whole safeParse → navState = {}.
    expect(screen.queryByText(EXPIRED_NOTE)).not.toBeInTheDocument();
  });

  it('lands on /dashboard when the returnTo (from) in nav state is malformed', async () => {
    const user = userEvent.setup();
    renderWithProviders(<LoginHarness />, {
      // `from` is not an object → navState falls back to {} → login defaults to /dashboard.
      routerEntries: [{ pathname: '/login', state: { from: 12345 } }],
    });
    await user.type(await screen.findByLabelText(/email address/i), MOCK_USER.email);
    await user.type(screen.getByLabelText(/^password$/i), MOCK_PASSWORD);
    await user.click(screen.getByRole('button', { name: /sign in/i }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
  });
});
