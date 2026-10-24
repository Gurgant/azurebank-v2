import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { http } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { bffProblem } from '../mocks/problem';
import { server } from '../mocks/server';
import { mockState } from '../mocks/state';
import { COPY } from '../test/outage';
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

/*
 * Each test waits for what the visitor sees, never for the store. RTK hands the session check's
 * answer to React on the next animation frame, so the store says 'authenticated' or 'anonymous'
 * while the check's spinner is still drawn, and a query made then finds no page.
 */

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
    // The link is drawn only in the signed-in view, so the rest is asked of that view.
    expect(await screen.findByRole('link', { name: 'Go to Home' })).toHaveAttribute(
      'href',
      '/dashboard',
    );
    expect(store.getState().auth.status).toBe('authenticated');

    const heading = screen.getByRole('heading', { name: 'Page not found', level: 1 });
    expect(heading).toBeVisible();
    expect(screen.getByText('There is no page at this address.')).toBeVisible();
    // Inside the shell: the app's own navigation is around the page.
    expect(screen.getAllByRole('navigation', { name: 'Main navigation' })).not.toHaveLength(0);
    expect(screen.queryByRole('link', { name: 'Go to sign in' })).not.toBeInTheDocument();
    // The shell's main holds the page, and the page adds no main of its own inside it.
    const mains = screen.getAllByRole('main');
    expect(mains).toHaveLength(1);
    expect(mains[0]).toContainElement(heading);
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
    // The link is drawn only in the signed-out view, so the rest is asked of that view.
    expect(await screen.findByRole('link', { name: 'Go to sign in' })).toHaveAttribute(
      'href',
      '/login',
    );
    expect(store.getState().auth.status).toBe('anonymous');

    const heading = screen.getByRole('heading', { name: 'Page not found', level: 1 });
    expect(heading).toBeVisible();
    expect(screen.getByText('There is no page at this address.')).toBeVisible();
    expect(screen.queryByRole('navigation', { name: 'Main navigation' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Go to Home' })).not.toBeInTheDocument();
    // Alone, the page is the one main landmark.
    const mains = screen.getAllByRole('main');
    expect(mains).toHaveLength(1);
    expect(mains[0]).toContainElement(heading);
    // The session check is the one request, and it is seen, so the list below is not empty
    // because nothing was recorded. Nothing under /api is asked: it needs a session.
    expect(asked).toContain('/bff/auth/me');
    expect(asked.filter((path) => path.startsWith('/api/'))).toEqual([]);
  });

  it('while the session is checked, offers no way back, and then the one that is true', async () => {
    // The check is held, so the page is drawn while the app cannot yet tell a signed-in visitor
    // from a signed-out one. It then answers 401, and the visitor is known as anonymous.
    // The spinner is drawn on the first render, before the request has reached the handler, so
    // the handler hands out its answer through a promise that is awaited before answering.
    let handOver: (answer: (response: Response) => void) => void = () => {};
    const held = new Promise<(response: Response) => void>((resolve) => {
      handOver = resolve;
    });
    server.use(
      http.get(
        '*/bff/auth/me',
        () =>
          new Promise<Response>((resolve) => {
            handOver(resolve);
          }),
      ),
    );
    const store = await openTheAddress();

    expect(await screen.findByLabelText('Checking your session')).toBeInTheDocument();
    expect(store.getState().auth.status).toBe('unknown');
    expect(window.location.pathname).toBe(ADDRESS);
    expect(screen.queryByRole('link', { name: 'Go to sign in' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Go to Home' })).not.toBeInTheDocument();

    const answer = await held;
    answer(bffProblem({ status: 401, title: 'Unauthorized', detail: 'No session.' }));

    await stayedOnTheAddress();
    expect(await screen.findByRole('link', { name: 'Go to sign in' })).toHaveAttribute(
      'href',
      '/login',
    );
    expect(store.getState().auth.status).toBe('anonymous');
    expect(screen.queryByLabelText('Checking your session')).not.toBeInTheDocument();
  });

  it('when the service cannot answer the check, says so instead of offering sign-in', async () => {
    // A 500 is an answer that says nothing about the session, and it is not retried: the status
    // stays 'unknown', over a session the visitor may well still have.
    let down = true;
    server.use(
      http.get('*/bff/auth/me', () =>
        down
          ? bffProblem({ status: 500, title: 'Internal Server Error', detail: 'Broken.' })
          : undefined,
      ),
    );
    const store = await openTheAddress();

    expect(
      await screen.findByRole('heading', { level: 1, name: COPY.unavailableTitle }),
    ).toBeVisible();
    expect(store.getState().auth.status).toBe('unknown');
    expect(window.location.pathname).toBe(ADDRESS);
    expect(screen.getByRole('alert')).toHaveTextContent(COPY.unavailable);
    expect(screen.queryByRole('link', { name: 'Go to sign in' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Page not found' })).not.toBeInTheDocument();

    // Back up: Try again finds the session, and the page is drawn in the shell with the way Home.
    down = false;
    fireEvent.click(screen.getByRole('button', { name: COPY.tryAgain }));

    expect(await screen.findByRole('link', { name: 'Go to Home' })).toHaveAttribute(
      'href',
      '/dashboard',
    );
    await stayedOnTheAddress();
    expect(store.getState().auth.status).toBe('authenticated');
    expect(screen.getAllByRole('navigation', { name: 'Main navigation' })).not.toHaveLength(0);
  });
});
