import { createMemoryRouter, Link, RouterProvider } from 'react-router-dom';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { useMoneyWizard } from './useMoneyWizard';
import type { IdempotentTrigger } from './useIdempotentMutation';

/**
 * The blocker, exercised through the REAL hook.
 *
 * The first draft of this file reimplemented the predicate — a local `useBlocker` with the same
 * shape — and that was a test of react-router rather than of this app: it would have stayed green if
 * `useMoneyWizard` never registered a blocker at all, if `keyLive` were computed wrongly, or if
 * `exitPrompt` were never wired to the dialog. The stated excuse was that reaching a live key means
 * driving RTK Query, an idempotency key and a step-up interceptor. That was simply wrong. The hook
 * takes its `trigger` as a PARAMETER, and `keyLive` is `isSubmitting || keyRetained`, so a trigger
 * whose promise never settles holds a key live with no infrastructure whatsoever.
 *
 * The mode still matters and is not incidental: under a declarative router `useBlocker` throws
 * "useBlocker must be used within a data router" rather than degrading — which is how the migration
 * announced itself, by failing thirty existing tests at once.
 */

type Body = Record<string, never>;
interface Result {
  ok: true;
}

/** A trigger the test drives: `unwrap()` stays pending until `settle` is called. */
function deferredTrigger() {
  let settle: (value: Result) => void = () => {};
  const pending = new Promise<Result>((resolve) => {
    settle = resolve;
  });
  const trigger: IdempotentTrigger<Body, Result> = () => ({ unwrap: () => pending });
  return { trigger, settle: (value: Result) => settle(value) };
}

function Wizard({ trigger }: { trigger: IdempotentTrigger<Body, Result> }) {
  // The real hook, with the real blocker, the real `keyLive` and the real `exitPrompt`.
  const wizard = useMoneyWizard<Body, Result>(trigger, {
    messages: {},
    fallback: 'Something went wrong.',
  });

  return (
    <>
      <p>transfer</p>
      <p>keyLive:{String(wizard.keyLive)}</p>
      <button onClick={() => void wizard.run({} as Body)}>send</button>
      <Link to="/history">go to history</Link>
      <Link to="/login">forced logout</Link>
      {wizard.exitPrompt && (
        <div>
          <p>Leave without finishing?</p>
          <button onClick={wizard.exitPrompt.leave}>Leave anyway</button>
          <button onClick={wizard.exitPrompt.stay}>Stay on this page</button>
        </div>
      )}
    </>
  );
}

function renderAt(initialEntries: string[] = ['/transfer']) {
  const { trigger, settle } = deferredTrigger();
  const router = createMemoryRouter(
    [
      { path: '/transfer', element: <Wizard trigger={trigger} /> },
      { path: '/history', element: <p>history</p> },
      { path: '/login', element: <p>sign in</p> },
    ],
    { initialEntries },
  );
  render(<RouterProvider router={router} />);
  return { router, settle };
}

/** Press Send. The trigger never settles, so the key stays live for the rest of the test. */
async function startSending() {
  await userEvent.click(screen.getByRole('button', { name: 'send' }));
  expect(screen.getByText('keyLive:true')).toBeInTheDocument();
}

describe('leaving a money wizard with a live key', () => {
  it('holds the navigation and asks, rather than letting it through', async () => {
    renderAt();
    await startSending();

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
    renderAt();
    await startSending();
    await userEvent.click(screen.getByRole('link', { name: 'go to history' }));
    await screen.findByText('Leave without finishing?');

    await userEvent.click(screen.getByRole('button', { name: 'Leave anyway' }));

    expect(await screen.findByText('history')).toBeInTheDocument();
  });

  it('stays put when the user says stay', async () => {
    renderAt();
    await startSending();
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
    renderAt();
    await startSending();

    await userEvent.click(screen.getByRole('link', { name: 'forced logout' }));

    expect(await screen.findByText('sign in')).toBeInTheDocument();
    expect(screen.queryByText('Leave without finishing?')).toBeNull();
  });

  it('closes the prompt by itself when the key clears underneath it', async () => {
    // Leave the dialog open, and let the send that is already in flight succeed. The key clears
    // while `blocker.state` is still 'blocked', which without the reset effect leaves "you have
    // unsent money" standing over a COMPLETED transfer.
    const { settle } = renderAt();
    await startSending();
    await userEvent.click(screen.getByRole('link', { name: 'go to history' }));
    await screen.findByText('Leave without finishing?');

    await act(async () => {
      settle({ ok: true });
    });

    await waitFor(() => expect(screen.queryByText('Leave without finishing?')).toBeNull());
    expect(screen.getByText('keyLive:false')).toBeInTheDocument();
    // Reset CANCELS the navigation rather than completing it, so the user is left where they were.
    expect(screen.getByText('transfer')).toBeInTheDocument();
  });

  it('holds a POP too, which is the navigation this whole change exists for', async () => {
    // Every test above clicks a `<Link>`, and a link is a PUSH. Browser Back is a POP, and the
    // router handles the two differently — it cannot pre-empt a POP, so it lets the history move,
    // then rewinds it and replays the move on `proceed()`. A suite that only ever pushed would
    // therefore be silent about the exact navigation the ADR is written about.
    const { router } = renderAt(['/history', '/transfer']);
    await startSending();

    await act(async () => {
      await router.navigate(-1);
    });

    expect(await screen.findByText('Leave without finishing?')).toBeInTheDocument();
    expect(screen.getByText('transfer')).toBeInTheDocument();
    expect(screen.queryByText('history')).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Leave anyway' }));

    expect(await screen.findByText('history')).toBeInTheDocument();
  });

  it('does not interfere at all when no key is live', async () => {
    // The negative control: without it every assertion above would still pass against a blocker
    // that blocked unconditionally. Note there is no `startSending()` here.
    renderAt();
    expect(screen.getByText('keyLive:false')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('link', { name: 'go to history' }));

    expect(await screen.findByText('history')).toBeInTheDocument();
    expect(screen.queryByText('Leave without finishing?')).toBeNull();
  });

  it('lets a POP through untouched when no key is live', async () => {
    const { router } = renderAt(['/history', '/transfer']);

    await act(async () => {
      await router.navigate(-1);
    });

    expect(await screen.findByText('history')).toBeInTheDocument();
    expect(screen.queryByText('Leave without finishing?')).toBeNull();
  });
});
