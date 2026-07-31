import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Button } from '@fluentui/react-components';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { renderWithProviders } from './renderWithProviders';

describe('renderWithProviders', () => {
  it('renders under theme + store + router', () => {
    renderWithProviders(
      <div>
        <Button>Themed button</Button>
        <Link to="/accounts">Accounts</Link>
      </div>,
    );

    expect(screen.getByRole('button', { name: 'Themed button' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Accounts' })).toHaveAttribute('href', '/accounts');
  });

  it('gives every test a FRESH store', () => {
    const { store } = renderWithProviders(<div />);
    const { store: another } = renderWithProviders(<div />);

    expect(store).not.toBe(another);
    expect(store.getState()).toHaveProperty('auth');
    expect(store.getState()).toHaveProperty('api');
  });
});

/**
 * The helper's own router, pinned.
 *
 * It builds a DATA router now (ADR-0028), and a data router owns its history rather than delegating
 * to a component's ref. Building it inside the wrapper — the obvious placement, and the one this
 * exists to prevent going back to — throws that history away on every render.
 *
 * The trap is that the obvious assertion does not catch it. Navigate, `rerender`, check the
 * location: PASSES against the bug, because `RouterProvider` seeds its state with a lazy `useState`
 * and keeps rendering the FIRST router's frozen location while `useNavigate` drives the newly built
 * one. Two probes written that way both went green before a third, moving backward through a
 * divergent history, showed the two had been out of sync the whole time.
 */
function HistoryProbe({ label }: { label: string }) {
  const location = useLocation();
  const navigate = useNavigate();

  return (
    <>
      <p>label:{label}</p>
      <p>at:{location.pathname}</p>
      <button onClick={() => void navigate('/c')}>forward</button>
      <button onClick={() => void navigate(-1)}>back</button>
    </>
  );
}

describe('renderWithProviders keeps ONE router per render call', () => {
  it('keeps the history a rerender should not have touched', async () => {
    const { rerender } = renderWithProviders(<HistoryProbe label="first" />, {
      routerEntries: ['/a', '/b'],
    });
    expect(screen.getByText('at:/b')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'forward' }));
    expect(screen.getByText('at:/c')).toBeInTheDocument(); // history: /a /b /c, sitting on /c

    rerender(<HistoryProbe label="second" />);
    await userEvent.click(screen.getByRole('button', { name: 'back' }));

    // Preserved: one entry back from /c. A router rebuilt from `initialEntries` sits on /b with no
    // /c behind it, so ITS "back" lands on /a — which is exactly what this saw before the router
    // moved out of the wrapper.
    expect(screen.getByText(/^at:/)).toHaveTextContent('at:/b');
  });

  it('still renders a subject the rerender swapped in', async () => {
    // The router being stable must not freeze the SUBJECT too — it reaches the route element
    // through context precisely so this keeps working.
    const { rerender } = renderWithProviders(<HistoryProbe label="first" />, {
      routerEntries: ['/a'],
    });
    expect(screen.getByText('label:first')).toBeInTheDocument();

    rerender(<HistoryProbe label="second" />);

    expect(screen.getByText('label:second')).toBeInTheDocument();
    expect(screen.queryByText('label:first')).toBeNull();
  });
});
