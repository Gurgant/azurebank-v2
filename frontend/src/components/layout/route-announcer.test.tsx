import { readFileSync } from 'node:fs';
import { StrictMode } from 'react';
import {
  createMemoryRouter,
  createRoutesFromElements,
  Link,
  Navigate,
  Route,
  RouterProvider,
} from 'react-router-dom';
import { act, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ThemeProvider } from '../../theme/ThemeProvider';
import { expectNoNestedLiveRegions } from '../../test/liveRegions';
import { RouteAnnouncer } from './RouteAnnouncer';

/**
 * `RouteAnnouncer` on a router of its own, in StrictMode as `main.tsx` renders the app. Each page
 * has its nav OUTSIDE `<main>`, as the shell does, so a link that stays mounted after it is pressed
 * is the case the focus rule has to handle.
 */
function Page({ name }: { name: string }) {
  return (
    <>
      <nav>
        <Link to="/a">Alpha link</Link>
        <Link to="/b">Beta link</Link>
      </nav>
      <main>
        <h1>{name}</h1>
      </main>
    </>
  );
}

function mount(initial = '/a') {
  const router = createMemoryRouter(
    createRoutesFromElements(
      <Route element={<RouteAnnouncer />}>
        <Route path="/a" handle={{ title: 'Alpha' }} element={<Page name="Alpha" />} />
        <Route path="/b" handle={{ title: 'Beta' }} element={<Page name="Beta" />} />
        <Route
          path="/wizard"
          handle={{ title: 'Wizard' }}
          element={
            <>
              <button type="button">Back</button>
              <h1>Wizard</h1>
            </>
          }
        />
        {/* Each has somewhere the rule could move focus to, so leaving it alone is a choice. */}
        <Route
          path="/auto"
          handle={{ title: 'Auto' }}
          element={
            <>
              <h1>Auto</h1>
              <input aria-label="Box" autoFocus />
            </>
          }
        />
        <Route
          path="/auto-main"
          handle={{ title: 'Auto in main' }}
          element={
            <main>
              <h1>Auto in main</h1>
              <input aria-label="Field in main" autoFocus />
            </main>
          }
        />
        <Route path="/old" element={<Navigate to="/b" replace />} />
        {/* Titled and redirecting, as ProtectedRoute makes /dashboard for a signed-out load. */}
        <Route
          path="/guarded"
          handle={{ title: 'Guarded' }}
          element={<Navigate to="/b" replace />}
        />
      </Route>,
    ),
    { initialEntries: [initial] },
  );
  const utils = render(
    <StrictMode>
      <ThemeProvider>
        <div data-testid="app-root">
          <RouterProvider router={router} />
        </div>
        {/* Above the router, as StepUpModal and SessionExpiryWarning are. */}
        <div role="dialog" aria-modal="true" aria-label="Modal above the router">
          <button type="button">In the modal</button>
        </div>
      </ThemeProvider>
    </StrictMode>,
  );
  const go = (to: string) =>
    act(async () => {
      await router.navigate(to);
    });
  return { ...utils, router, go };
}

const region = () => document.querySelector<HTMLElement>('[data-route-announcer]')!;

describe('RouteAnnouncer', () => {
  it('titles a load, and neither announces it nor moves focus', () => {
    mount('/a');

    expect(document.title).toBe('Alpha · AzureBank');
    expect(region()).toHaveTextContent('');
    expect(region()).toHaveAttribute('role', 'status');
    expect(document.activeElement).toBe(document.body);
  });

  it('announces a change and moves focus into main, off a link that stayed mounted', async () => {
    const { go } = mount('/a');
    screen.getByRole('link', { name: 'Beta link' }).focus();

    await go('/b');

    expect(document.title).toBe('Beta · AzureBank');
    expect(region()).toHaveTextContent('Beta page loaded');
    const main = screen.getByRole('main');
    expect(document.activeElement).toBe(main);
    // Focusable for this one focus only.
    expect(main).toHaveAttribute('tabindex', '-1');
    expectNoNestedLiveRegions();

    act(() => main.blur());
    expect(main).not.toHaveAttribute('tabindex');
    expect(main).not.toHaveAttribute('data-route-focus');
  });

  it('announces a return to the page that was loaded first', async () => {
    const { go } = mount('/a');
    await go('/b');

    await go('/a');

    expect(region()).toHaveTextContent('Alpha page loaded');
    expect(document.activeElement).toBe(screen.getByRole('main'));
  });

  it('focuses the h1 where there is no main', async () => {
    const { go } = mount('/a');

    await go('/wizard');

    expect(region()).toHaveTextContent('Wizard page loaded');
    expect(document.activeElement).toBe(screen.getByRole('heading', { name: 'Wizard' }));
  });

  it('leaves focus where the new page put it, with or without a main', async () => {
    const { go } = mount('/a');

    await go('/auto');
    expect(region()).toHaveTextContent('Auto page loaded');
    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: 'Box' }));

    await go('/auto-main');
    expect(region()).toHaveTextContent('Auto in main page loaded');
    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: 'Field in main' }));
  });

  it('announces where a redirect lands, not the route that redirected', async () => {
    const { go } = mount('/a');

    await go('/old');

    expect(document.title).toBe('Beta · AzureBank');
    expect(region()).toHaveTextContent('Beta page loaded');
  });

  it('announces a redirect during a load when the route that redirected is titled', async () => {
    mount('/guarded');

    // The first titled path is the load; landing somewhere else from it is a change.
    await act(async () => {});

    expect(document.title).toBe('Beta · AzureBank');
    expect(region()).toHaveTextContent('Beta page loaded');
  });

  it('does not move focus for a change of search alone', async () => {
    const { go } = mount('/a');
    await go('/b');
    const link = screen.getByRole('link', { name: 'Alpha link' });
    act(() => link.focus());

    await go('/b?x=1');

    expect(document.activeElement).toBe(link);
  });

  it('never takes focus out of a modal above the router', async () => {
    const { go } = mount('/a');
    const inModal = screen.getByRole('button', { name: 'In the modal' });
    act(() => inModal.focus());

    await go('/b');

    expect(region()).toHaveTextContent('Beta page loaded');
    expect(document.activeElement).toBe(inModal);
  });

  it('waits for an aria-hidden page to be exposed before announcing into it', async () => {
    const { go } = mount('/a');
    // What Tabster leaves on the app root for up to 250ms after a modal closes.
    const root = screen.getByTestId('app-root');
    root.setAttribute('aria-hidden', 'true');

    await go('/b');

    expect(region()).toHaveTextContent('');
    expect(document.activeElement).toBe(document.body);

    await act(async () => {
      root.removeAttribute('aria-hidden');
      // MutationObserver callbacks are microtasks.
      await Promise.resolve();
    });

    expect(region()).toHaveTextContent('Beta page loaded');
    expect(document.activeElement).toBe(screen.getByRole('main'));
  });

  it('is wired into App, and every route that renders a page names its title', () => {
    // Against the source, as RouteError.test.tsx does: App's router is module-scope and unexported.
    // Comments are stripped first, because App.tsx explains its routing in prose that names routes.
    const app = readFileSync('src/App.tsx', 'utf8')
      .replace(/\/\*[\s\S]*?\*\//g, '')
      .replace(/^\s*\/\/.*$/gm, '')
      .replace(/\{\/\*[\s\S]*?\*\/\}/g, '');

    expect(app).toContain('<Route element={<RouteAnnouncer />} errorElement={<RouteError />}>');

    const pages = app
      .split('<Route')
      .filter((chunk) => /path="[^"]+"/.test(chunk) && !chunk.includes('<Navigate'));
    expect(pages.map((chunk) => /path="([^"]+)"/.exec(chunk)?.[1])).toEqual([
      '/login',
      '/register',
      '/',
      '/dashboard',
      '/accounts',
      '/history',
      '/transactions/:id',
      '/settings',
      '/about',
      '/transfer',
      '/transfer/internal',
      '/pin-setup',
    ]);
    for (const chunk of pages) {
      expect(chunk, chunk.slice(0, 40)).toMatch(/handle=\{\{ title: '[^']+' \}\}/);
    }
  });
});
