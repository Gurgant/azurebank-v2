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
import { act, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ThemeProvider } from '../../theme/ThemeProvider';
import { expectNoNestedLiveRegions } from '../../test/liveRegions';
import { ROUTE_ANNOUNCE_DELAY_MS } from './pageTitle';
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
        {/* The same title on another path, as / and /dashboard are both "Home". */}
        <Route path="/b2" handle={{ title: 'Beta' }} element={<Page name="Beta" />} />
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

/** The announcement is written a task after the change, so every read of it waits. */
const announced = (text: string) => waitFor(() => expect(region()).toHaveTextContent(text));

/** Long enough for an announcement to have been written, had one been due. */
const pastTheDelay = () =>
  act(async () => {
    await new Promise((resolve) => setTimeout(resolve, ROUTE_ANNOUNCE_DELAY_MS + 50));
  });

/** Every text the region has held since the call, in order. */
function recordRegion() {
  const texts: string[] = [];
  const observer = new MutationObserver(() => texts.push(region().textContent ?? ''));
  observer.observe(region(), { childList: true, characterData: true, subtree: true });
  return { texts, stop: () => observer.disconnect() };
}

describe('RouteAnnouncer', () => {
  it('titles a load, and neither announces it nor moves focus', async () => {
    mount('/a');
    await pastTheDelay();

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
    // Focus moves at once; the announcement follows in a task of its own.
    const main = screen.getByRole('main');
    expect(document.activeElement).toBe(main);
    await announced('Beta page loaded');
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

    await announced('Alpha page loaded');
    expect(document.activeElement).toBe(screen.getByRole('main'));
  });

  it('focuses the h1 where there is no main', async () => {
    const { go } = mount('/a');

    await go('/wizard');

    await announced('Wizard page loaded');
    expect(document.activeElement).toBe(screen.getByRole('heading', { name: 'Wizard' }));
  });

  it('leaves focus where the new page put it, with or without a main', async () => {
    const { go } = mount('/a');

    await go('/auto');
    await announced('Auto page loaded');
    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: 'Box' }));

    await go('/auto-main');
    await announced('Auto in main page loaded');
    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: 'Field in main' }));
  });

  it('announces where a redirect lands, not the route that redirected', async () => {
    const { go } = mount('/a');

    await go('/old');

    expect(document.title).toBe('Beta · AzureBank');
    await announced('Beta page loaded');
  });

  it('announces a redirect during a load when the route that redirected is titled', async () => {
    mount('/guarded');

    // The first titled path is the load; landing somewhere else from it is a change.
    await act(async () => {});

    expect(document.title).toBe('Beta · AzureBank');
    await announced('Beta page loaded');
  });

  /*
    Two routes can share a title: / and /dashboard are both "Home". Writing the text the region
    already holds changes nothing in the DOM, and a screen reader announces changes, so the second
    arrival was silent. The region is emptied at the change and written a task later, which is a
    change whatever it held.
  */
  it('announces a title again when the region already holds it', async () => {
    const { go } = mount('/a');
    await go('/b');
    await announced('Beta page loaded');
    const { texts, stop } = recordRegion();

    await go('/b2');
    expect(region()).toHaveTextContent('');
    await announced('Beta page loaded');
    stop();

    expect(texts).toEqual(['', 'Beta page loaded']);
  });

  it('drops an announcement its route has already left', async () => {
    const { go } = mount('/a');
    const { texts, stop } = recordRegion();

    await go('/b');
    await go('/wizard');
    await announced('Wizard page loaded');
    await pastTheDelay();
    stop();

    expect(texts).not.toContain('Beta page loaded');
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

    await announced('Beta page loaded');
    expect(document.activeElement).toBe(inModal);
  });

  it('waits for an aria-hidden page to be exposed before announcing into it', async () => {
    const { go } = mount('/a');
    // What Tabster leaves on the app root for up to 250ms after a modal closes.
    const root = screen.getByTestId('app-root');
    root.setAttribute('aria-hidden', 'true');

    await go('/b');
    await pastTheDelay();

    expect(region()).toHaveTextContent('');
    expect(document.activeElement).toBe(document.body);

    await act(async () => {
      root.removeAttribute('aria-hidden');
      // MutationObserver callbacks are microtasks.
      await Promise.resolve();
    });

    expect(document.activeElement).toBe(screen.getByRole('main'));
    await announced('Beta page loaded');
  });

  /*
    StepUpModal and SessionExpiryWarning live above the router and can stay open across a Back
    navigation, for as long as the user takes. The wait used to give up after a second and write
    into the hidden region; when the modal then closed nothing changed, so nothing was announced.
  */
  it('waits as long as the page stays hidden, not for a second', async () => {
    const { go } = mount('/a');
    const root = screen.getByTestId('app-root');
    root.setAttribute('aria-hidden', 'true');

    await go('/b');
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 1200));
    });

    expect(region()).toHaveTextContent('');
    expect(document.activeElement).toBe(document.body);

    await act(async () => {
      root.removeAttribute('aria-hidden');
      await Promise.resolve();
    });
    expect(document.activeElement).toBe(screen.getByRole('main'));
    await announced('Beta page loaded');
  });

  it('drops a route left while the page was still hidden, wait and all', async () => {
    const { go } = mount('/a');
    const root = screen.getByTestId('app-root');
    root.setAttribute('aria-hidden', 'true');
    const { texts, stop } = recordRegion();

    await go('/b');
    await go('/wizard');
    await act(async () => {
      root.removeAttribute('aria-hidden');
      await Promise.resolve();
    });
    await announced('Wizard page loaded');
    await pastTheDelay();
    stop();

    // /b's wait ended when /b was left; only the page that is showing speaks.
    expect(texts).not.toContain('Beta page loaded');
    expect(document.activeElement).toBe(screen.getByRole('heading', { name: 'Wizard' }));
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
