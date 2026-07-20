import { Route, Routes } from 'react-router-dom';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { seedMockSession } from '../../mocks/state';
import { makeTestStore, renderWithProviders, type TestStore } from '../../test/renderWithProviders';
import { apiSlice } from '../../features/api/apiSlice';
import { ProtectedShell } from './ProtectedShell';

/**
 * The shell adoption contract: ONE shared shell around protected pages, fed by the
 * REAL session (never a hardcoded mock user), navigating the REAL information
 * architecture (the dead /transactions path died here), with a logout that actually
 * revokes the server session.
 */

async function bootAuthenticated(): Promise<TestStore> {
  seedMockSession();
  const store = makeTestStore();
  await store.dispatch(apiSlice.endpoints.getMe.initiate()).unwrap();
  return store;
}

function Harness() {
  return (
    <Routes>
      <Route path="/login" element={<div>LOGIN_PAGE</div>} />
      <Route path="/history" element={<div>HISTORY_PAGE</div>} />
      <Route
        path="/dashboard"
        element={
          <ProtectedShell>
            <div>PAGE_CONTENT</div>
          </ProtectedShell>
        }
      />
    </Routes>
  );
}

describe('ProtectedShell (app shell adoption)', () => {
  it('wraps content in the shell with the REAL session user and the real IA', async () => {
    const store = await bootAuthenticated();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });

    expect(screen.getByText('PAGE_CONTENT')).toBeInTheDocument();
    // The session user — not the hardcoded 'John Doe' the old page shells rendered.
    expect(screen.getByText('Demo User')).toBeInTheDocument();
    for (const label of ['Dashboard', 'Accounts', 'History', 'Transfer']) {
      expect(screen.getByRole('button', { name: label })).toBeInTheDocument();
    }
  });

  it('sidebar History navigates to /history — the dead /transactions path is gone', async () => {
    const store = await bootAuthenticated();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });

    await user.click(screen.getByRole('button', { name: 'History' }));

    expect(screen.getByText('HISTORY_PAGE')).toBeInTheDocument();
  });

  it('logout revokes the session server-side and lands on login', async () => {
    const store = await bootAuthenticated();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });

    await user.click(screen.getByRole('button', { name: /logout/i }));

    await waitFor(() => expect(screen.getByText('LOGIN_PAGE')).toBeInTheDocument());
    expect(store.getState().auth.status).toBe('anonymous');
  });
});
