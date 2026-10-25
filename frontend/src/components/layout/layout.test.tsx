import { useState } from 'react';
import { Route, Routes } from 'react-router-dom';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { server } from '../../mocks/server';
import { problem } from '../../mocks/problem';
import { AppToaster } from '../feedback';
import { seedMockSession } from '../../mocks/state';
import { makeTestStore, renderWithProviders, type TestStore } from '../../test/renderWithProviders';
import { setViewportWidth } from '../../test/viewport';
import { apiSlice } from '../../features/api/apiSlice';
import { AppLayout } from './AppLayout';
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
    <>
      {/* The toast outlet — logout failures surface here (the useProblemToast pipeline). */}
      <AppToaster />
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
    </>
  );
}

describe('ProtectedShell (app shell adoption)', () => {
  it('wraps content in the shell with the REAL session user and the real IA', async () => {
    const store = await bootAuthenticated();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });

    expect(screen.getByText('PAGE_CONTENT')).toBeInTheDocument();
    // The session user — not the hardcoded 'John Doe' the old page shells rendered.
    expect(screen.getByText('Demo User')).toBeInTheDocument();

    // The four PLACES are links with real hrefs. They used to be buttons calling navigate(), which
    // put no href in the accessibility tree — see nav-ia.test.tsx for the full contract.
    for (const [label, href] of [
      ['Home', '/dashboard'],
      ['Accounts', '/accounts'],
      ['History', '/history'],
      ['Settings', '/settings'],
    ]) {
      expect(screen.getByRole('link', { name: label })).toHaveAttribute('href', href);
    }
    // Transfer is the one ACT, so it stays reachable from the shell — as a button.
    expect(screen.getByRole('button', { name: 'Transfer' })).toBeInTheDocument();
  });

  it('sidebar History navigates to /history — the dead /transactions path is gone', async () => {
    const store = await bootAuthenticated();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });

    await user.click(screen.getByRole('link', { name: 'History' }));

    expect(screen.getByText('HISTORY_PAGE')).toBeInTheDocument();
  });

  it('logout revokes the session server-side and lands on login', async () => {
    const store = await bootAuthenticated();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });

    await user.click(screen.getByRole('button', { name: /sign out/i }));

    await waitFor(() => expect(screen.getByText('LOGIN_PAGE')).toBeInTheDocument());
    expect(store.getState().auth.status).toBe('anonymous');
  });

  it('a FAILED logout never masquerades as a logout: stays authenticated, no redirect', async () => {
    server.use(
      http.post('*/bff/auth/logout', () =>
        problem({ status: 503, title: 'Service Unavailable', detail: 'Try again shortly.' }),
      ),
    );
    const store = await bootAuthenticated();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });

    await user.click(screen.getByRole('button', { name: /sign out/i }));

    // The error surfaces via the toast pipeline; the session cookie is still alive,
    // so the UI must stay authenticated on the page — not fake a login screen.
    await waitFor(() => expect(screen.getByText(/service unavailable/i)).toBeInTheDocument());
    expect(screen.queryByText('LOGIN_PAGE')).not.toBeInTheDocument();
    expect(store.getState().auth.status).toBe('authenticated');
  });
});

/** A field that holds what was typed in React state, as a dialog's amount does. */
function TypedField() {
  const [value, setValue] = useState('');
  return (
    <input aria-label="Amount" value={value} onChange={(event) => setValue(event.target.value)} />
  );
}

describe('AppLayout across the 1024 px breakpoint', () => {
  /**
   * A tablet turned, or a window resized or split, moves the shell from the sidebar to the bottom
   * bar and back. A page that remounted there would drop an open dialog, what was typed in it and
   * where focus was; only the navigation may change places.
   */
  it('keeps the page, what was typed in it and its focus while the layout changes', () => {
    setViewportWidth(1100);
    renderWithProviders(
      <AppLayout>
        <TypedField />
      </AppLayout>,
    );
    const field = screen.getByRole('textbox', { name: 'Amount' });
    field.focus();
    fireEvent.change(field, { target: { value: '7' } });

    const kept = () => {
      const now = screen.getByRole<HTMLInputElement>('textbox', { name: 'Amount' });
      return { sameNode: now === field, value: now.value, focused: now === document.activeElement };
    };

    act(() => setViewportWidth(1000));
    // Non-vacuity: the sidebar (an `aside`) is gone, so the phone layout was drawn.
    expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
    expect(kept()).toEqual({ sameNode: true, value: '7', focused: true });

    act(() => setViewportWidth(1100));
    expect(screen.getByRole('complementary')).toBeInTheDocument();
    expect(kept()).toEqual({ sameNode: true, value: '7', focused: true });
  });
});

/**
 * On desktop the sidebar comes before the page, so a keyboard visitor used to Tab through seven
 * stops of navigation on every page before its first control (measured in Chromium at 1280x900 on
 * /dashboard and /accounts). The shell's first stop is now a skip link to the page.
 */
describe('AppLayout skip link', () => {
  it('is the first Tab stop on desktop, and Enter puts focus in main for that one focus', async () => {
    const store = await bootAuthenticated();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });
    const main = screen.getByRole('main');
    // RouteAnnouncer gives main a tabindex only where it has none, and draws its ring through the
    // attribute it adds with it: a tabindex left on main would turn both off.
    expect(main).not.toHaveAttribute('tabindex');

    await user.tab();
    const skip = screen.getByRole('link', { name: 'Skip to main content' });
    expect(skip).toHaveFocus();

    await user.keyboard('{Enter}');
    expect(main).toHaveFocus();
    expect(main).toHaveAttribute('tabindex', '-1');
    expect(main).toHaveAttribute('data-route-focus');

    // The next Tab is the page's own: main is not a stop of its own, and gives both back.
    await user.tab();
    expect(main).not.toHaveFocus();
    expect(main).not.toHaveAttribute('tabindex');
    expect(main).not.toHaveAttribute('data-route-focus');
  });

  it('is not drawn on the phone, where the bar comes after the page', () => {
    setViewportWidth(390);
    renderWithProviders(
      <AppLayout onLogout={() => {}}>
        <p>Page</p>
      </AppLayout>,
    );

    expect(screen.queryByRole('link', { name: 'Skip to main content' })).toBeNull();
  });
});

describe('ProtectedShell while the sign-out is under way', () => {
  it('asks the server once however often Sign out is pressed, then lands on login', async () => {
    let answer: () => void = () => {};
    let asked = 0;
    server.use(
      http.post('*/bff/auth/logout', async () => {
        asked += 1;
        await new Promise<void>((resolve) => {
          answer = resolve;
        });
        return HttpResponse.json({ message: 'Logged out successfully' });
      }),
    );
    const store = await bootAuthenticated();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });
    const signOut = screen.getByRole('button', { name: 'Sign out' });

    await user.click(signOut);
    await waitFor(() => expect(signOut).toHaveAttribute('aria-busy', 'true'));
    await user.click(signOut);
    await user.click(signOut);

    expect(asked).toBe(1);
    expect(signOut).toHaveFocus();
    act(() => answer());
    await waitFor(() => expect(screen.getByText('LOGIN_PAGE')).toBeInTheDocument());
    expect(asked).toBe(1);
  });

  it('after a failed sign-out, Sign out is usable again and still focused', async () => {
    server.use(
      http.post('*/bff/auth/logout', () =>
        problem({ status: 503, title: 'Service Unavailable', detail: 'Try again shortly.' }),
      ),
    );
    const store = await bootAuthenticated();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });
    const signOut = screen.getByRole('button', { name: 'Sign out' });

    await user.click(signOut);

    await waitFor(() => expect(screen.getByText(/service unavailable/i)).toBeInTheDocument());
    expect(signOut).not.toHaveAttribute('aria-busy');
    expect(signOut).not.toHaveAttribute('aria-disabled');
    expect(signOut).toHaveFocus();
    expect(store.getState().auth.status).toBe('authenticated');
  });
});

/**
 * The same pending state reaches the phone's bar, where Sign out sits in the row behind More: the
 * row stays open from the press to the answer, so the busy tile, and after a failure the usable
 * one, are seen where the visitor pressed.
 */
describe('ProtectedShell on the phone while the sign-out is under way', () => {
  it('keeps the More row open with Sign out busy, asks once, then lands on login', async () => {
    let answer: () => void = () => {};
    let asked = 0;
    server.use(
      http.post('*/bff/auth/logout', async () => {
        asked += 1;
        await new Promise<void>((resolve) => {
          answer = resolve;
        });
        return HttpResponse.json({ message: 'Logged out successfully' });
      }),
    );
    setViewportWidth(390);
    const store = await bootAuthenticated();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });
    const more = screen.getByRole('button', { name: 'More' });
    await user.click(more);
    const signOut = screen.getByRole('button', { name: 'Sign out' });

    await user.click(signOut);
    await waitFor(() => expect(signOut).toHaveAttribute('aria-busy', 'true'));
    expect(more).toHaveAttribute('aria-expanded', 'true');
    expect(signOut).toHaveFocus();
    await user.click(signOut);

    expect(asked).toBe(1);
    act(() => answer());
    await waitFor(() => expect(screen.getByText('LOGIN_PAGE')).toBeInTheDocument());
    expect(asked).toBe(1);
  });

  it('after a failed sign-out the row is still open, and Sign out is usable again and focused', async () => {
    server.use(
      http.post('*/bff/auth/logout', () =>
        problem({ status: 503, title: 'Service Unavailable', detail: 'Try again shortly.' }),
      ),
    );
    setViewportWidth(390);
    const store = await bootAuthenticated();
    const user = userEvent.setup();
    renderWithProviders(<Harness />, { store, routerEntries: ['/dashboard'] });
    const more = screen.getByRole('button', { name: 'More' });
    await user.click(more);
    const signOut = screen.getByRole('button', { name: 'Sign out' });

    await user.click(signOut);

    await waitFor(() => expect(screen.getByText(/service unavailable/i)).toBeInTheDocument());
    expect(more).toHaveAttribute('aria-expanded', 'true');
    expect(signOut).not.toHaveAttribute('aria-busy');
    expect(signOut).not.toHaveAttribute('aria-disabled');
    expect(signOut).toHaveFocus();
    expect(store.getState().auth.status).toBe('authenticated');
  });
});
