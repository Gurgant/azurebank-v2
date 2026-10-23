import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../mocks/server';
import { mockState } from '../mocks/state';
// Imported while the file is collected, which no time limit bounds: the first import of App
// transforms every module of the app, and on a loaded machine that took longer than a hook's 60 s.
// A test's own import, after the registry is emptied, then only evaluates the modules again.
import '../App';

/**
 * An address that names no page, opened as a visitor opens it: in the whole app, through the
 * route table App.tsx declares and not a copy of it.
 *
 * App.tsx builds its router once, when the module is evaluated, from the address the window holds
 * then, and does not export it. So each test puts the address in the window, empties the module
 * registry and imports App again: the router, the store and the routes are the ones the app runs
 * with, and the store is read from the same registry.
 */

const ADDRESS = '/no-such-page';

/** Opens the app at `ADDRESS` and hands back the store it renders with. */
async function openTheAddress() {
  window.history.replaceState(null, '', ADDRESS);
  vi.resetModules();
  const [{ default: App }, { store }] = await Promise.all([
    import('../App'),
    import('../app/store'),
  ]);
  render(<App />);
  return store;
}

/** Where the visitor is and what the tab says, once the route has been drawn. */
async function stayedOnTheAddress() {
  await waitFor(() =>
    expect({ address: window.location.pathname, title: document.title }).toEqual({
      address: ADDRESS,
      title: 'Page not found · AzureBank',
    }),
  );
}

afterEach(() => {
  cleanup();
  server.events.removeAllListeners();
  window.history.replaceState(null, '', '/');
});

describe('an address that names no page', () => {
  it('signed in, says so inside the app and offers the way Home', async () => {
    const store = await openTheAddress();

    await stayedOnTheAddress();
    await waitFor(() => expect(store.getState().auth.status).toBe('authenticated'));

    expect(screen.getByRole('heading', { name: 'Page not found', level: 1 })).toBeVisible();
    expect(screen.getByText('There is no page at this address.')).toBeVisible();
    // Inside the shell: the app's own navigation is around the page.
    expect(screen.getAllByRole('navigation', { name: 'Main navigation' })).not.toHaveLength(0);
    expect(screen.getByRole('link', { name: 'Go to Home' })).toHaveAttribute('href', '/dashboard');
    expect(screen.queryByRole('link', { name: 'Go to sign in' })).not.toBeInTheDocument();
  });

  it('signed out, stands alone, asks nothing that needs a session and offers sign-in', async () => {
    // The setup signs every test in, because the guarded pages need it. This visitor has no
    // session, so the check at start-up is answered 401 and the app knows the visitor as anonymous.
    mockState.session = null;
    const asked: string[] = [];
    server.events.on('request:start', ({ request }) => {
      asked.push(new URL(request.url).pathname);
    });
    const store = await openTheAddress();

    await stayedOnTheAddress();
    await waitFor(() => expect(store.getState().auth.status).toBe('anonymous'));

    expect(screen.getByRole('heading', { name: 'Page not found', level: 1 })).toBeVisible();
    expect(screen.getByText('There is no page at this address.')).toBeVisible();
    expect(screen.queryByRole('navigation', { name: 'Main navigation' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Go to sign in' })).toHaveAttribute('href', '/login');
    expect(screen.queryByRole('link', { name: 'Go to Home' })).not.toBeInTheDocument();
    // The session check is the one request, and it is seen, so the list below is not empty
    // because nothing was recorded. Nothing under /api is asked: it needs a session.
    expect(asked).toContain('/bff/auth/me');
    expect(asked.filter((path) => path.startsWith('/api/'))).toEqual([]);
  });
});
