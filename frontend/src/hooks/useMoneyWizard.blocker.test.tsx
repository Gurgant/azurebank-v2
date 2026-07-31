import { createMemoryRouter, Link, RouterProvider } from 'react-router-dom';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { useBlocker } from 'react-router-dom';
import { useCallback } from 'react';

/**
 * The blocker contract, exercised on its own.
 *
 * `useMoneyWizard` reaches this through RTK Query, an idempotency key and a step-up interceptor, and
 * driving all of that to get a live key would test those instead. What is new here is smaller and
 * exactly stateable: a POP is held, both answers are offered, and `/login` is exempt. So the
 * predicate is exercised directly, against a real data router, with the same shape the hook uses.
 *
 * The mode matters and is not incidental: under a declarative router `useBlocker` throws
 * "useBlocker must be used within a data router" rather than degrading — which is how the migration
 * announced itself, by failing thirty existing tests at once.
 */

function Wizard({ keyLive }: { keyLive: boolean }) {
  const blocker = useBlocker(
    useCallback(
      ({ nextLocation }: { nextLocation: { pathname: string } }) =>
        keyLive && nextLocation.pathname !== '/login',
      [keyLive],
    ),
  );

  return (
    <>
      <p>transfer</p>
      <Link to="/history">go to history</Link>
      <Link to="/login">forced logout</Link>
      {blocker.state === 'blocked' && (
        <div>
          <p>Leave without finishing?</p>
          <button onClick={() => blocker.proceed?.()}>Leave anyway</button>
          <button onClick={() => blocker.reset?.()}>Stay on this page</button>
        </div>
      )}
    </>
  );
}

function renderAt(keyLive: boolean) {
  const router = createMemoryRouter(
    [
      { path: '/transfer', element: <Wizard keyLive={keyLive} /> },
      { path: '/history', element: <p>history</p> },
      { path: '/login', element: <p>sign in</p> },
    ],
    { initialEntries: ['/transfer'] },
  );
  render(<RouterProvider router={router} />);
  return router;
}

describe('leaving a money wizard with a live key', () => {
  it('holds the navigation and asks, rather than letting it through', async () => {
    renderAt(true);

    await userEvent.click(screen.getByRole('link', { name: 'go to history' }));

    expect(await screen.findByText('Leave without finishing?')).toBeInTheDocument();
    // Still on the wizard: held, not merely warned after the fact.
    expect(screen.getByText('transfer')).toBeInTheDocument();
    expect(screen.queryByText('history')).toBeNull();
  });

  it('lets the user leave anyway, because refusing would be a locked door', async () => {
    // The decision this file exists to pin. Every OTHER exit refuses while a key is live, and
    // matching them here would trap: after a >= 500 the key is retained while `verifyRequired`
    // stays false, so the verify view does not render, Back and Close are disabled, and
    // `requestLeave` no-ops. A blocker with no `proceed()` leaves closing the tab as the only way
    // out of a page the user reached by having a payment fail.
    renderAt(true);
    await userEvent.click(screen.getByRole('link', { name: 'go to history' }));
    await screen.findByText('Leave without finishing?');

    await userEvent.click(screen.getByRole('button', { name: 'Leave anyway' }));

    expect(await screen.findByText('history')).toBeInTheDocument();
  });

  it('stays put when the user says stay', async () => {
    renderAt(true);
    await userEvent.click(screen.getByRole('link', { name: 'go to history' }));
    await screen.findByText('Leave without finishing?');

    await userEvent.click(screen.getByRole('button', { name: 'Stay on this page' }));

    await waitFor(() => expect(screen.queryByText('Leave without finishing?')).toBeNull());
    expect(screen.getByText('transfer')).toBeInTheDocument();
  });

  it('never blocks the way to /login, because that navigation is a forced logout', async () => {
    // `ProtectedRoute` renders `<Navigate to="/login" replace />` the moment auth stops being
    // `authenticated`, and the router consults the blocker on REPLACE too. A bare
    // `useBlocker(keyLive)` would put "are you sure?" in front of a session expiry and hold the
    // user on a page whose credentials are already dead.
    renderAt(true);

    await userEvent.click(screen.getByRole('link', { name: 'forced logout' }));

    expect(await screen.findByText('sign in')).toBeInTheDocument();
    expect(screen.queryByText('Leave without finishing?')).toBeNull();
  });

  it('does not interfere at all when no key is live', async () => {
    // The negative control: without it every assertion above would still pass against a blocker
    // that blocked unconditionally.
    renderAt(false);

    await userEvent.click(screen.getByRole('link', { name: 'go to history' }));

    expect(await screen.findByText('history')).toBeInTheDocument();
    expect(screen.queryByText('Leave without finishing?')).toBeNull();
  });
});
