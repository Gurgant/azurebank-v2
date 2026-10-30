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

// The prefillEmail validation is exercised via the expiry banner, which shows that the whole nav
// state was kept: a VALID prefillEmail keeps it, so the reason=expired banner shows; an INVALID
// prefillEmail invalidates the entire safeParse, so the banner disappears even though `reason`
// itself was valid. What the email field shows has its own tests below.
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

  it('the email handed over stays in the field when the page is drawn again, and is what is sent', async () => {
    const user = userEvent.setup();
    renderWithProviders(<LoginHarness />, {
      routerEntries: [{ pathname: '/login', state: { prefillEmail: MOCK_USER.email } }],
    });
    const email = await screen.findByLabelText(/email address/i);
    // The form writes the value on the node when it registers it, and Fluent's Input, which keeps
    // a value of its own, wipes it at its next commit. The eye button is such a commit.
    await user.click(screen.getByRole('button', { name: 'Show password' }));
    expect(email).toHaveValue(MOCK_USER.email);

    await user.type(screen.getByLabelText(/^password$/i), MOCK_PASSWORD);
    await user.click(screen.getByRole('button', { name: /sign in/i }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
  });

  it('an email handed over can be changed, and the change is what is sent', async () => {
    const user = userEvent.setup();
    renderWithProviders(<LoginHarness />, {
      routerEntries: [{ pathname: '/login', state: { prefillEmail: 'seed@azurebank.dev' } }],
    });
    const email = await screen.findByLabelText(/email address/i);
    await user.clear(email);
    await user.type(email, MOCK_USER.email);
    await user.type(screen.getByLabelText(/^password$/i), MOCK_PASSWORD);
    await user.click(screen.getByRole('button', { name: /sign in/i }));
    expect(await screen.findByText('DASHBOARD')).toBeInTheDocument();
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
