import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { FluentProvider } from '@fluentui/react-components';
import { describe, expect, it } from 'vitest';
// Vite's `?raw` rather than `node:fs`: no Node types to add to a browser-targeted tsconfig, and it
// resolves relative to THIS file instead of to whatever the runner's cwd happens to be.
import appSource from '../../App.tsx?raw';
import { azureBankLightTheme } from '../../theme/fluentTheme';
import { BottomNav } from './BottomNav';
import { Sidebar } from './Sidebar';
import { NAV_PLACES, navCurrent, resolveNavPlace } from './navItems';

/**
 * One information architecture, asserted on BOTH surfaces at once.
 *
 * Eight of the nineteen component cases here already passed against the previous version, and that
 * is worth stating rather than implying otherwise: the failures this change closes were measured in
 * a real browser — on `/` nothing lit anywhere, on `/transactions/:id` nothing lit anywhere, and on
 * `/settings` the bottom bar lit "Profile" while the sidebar lit nothing at all — while the rest of
 * the file pins invariants that already held so they cannot quietly stop holding.
 *
 * **The components are rendered DIRECTLY, never through AppLayout, and that is load-bearing.** The
 * suite's viewport is 1024x768 (test/viewport.ts) — exactly the `lg` breakpoint — so AppLayout
 * always renders its DESKTOP branch here. Every BottomNav assertion routed through it would find no
 * bottom bar and pass by asserting nothing.
 */

const TX_ID = '019f7b3f-0000-7000-8000-000000000b02';

type Surface = 'sidebar' | 'bottom nav';

function renderNav(surface: Surface, pathname: string) {
  const ui =
    surface === 'sidebar' ? <Sidebar userName="Demo User" onLogout={() => {}} /> : <BottomNav />;
  return render(
    <FluentProvider theme={azureBankLightTheme}>
      <MemoryRouter initialEntries={[pathname]}>{ui}</MemoryRouter>
    </FluentProvider>,
  );
}

/** Every element inside the nav landmark that claims to be current, and what it claims. */
function currentItems() {
  const nav = screen.getByRole('navigation', { name: 'Main navigation' });
  return [...nav.querySelectorAll('[aria-current]')].map((el) => ({
    label: el.textContent?.trim(),
    value: el.getAttribute('aria-current'),
  }));
}

const SURFACES: Surface[] = ['sidebar', 'bottom nav'];

/**
 * The contract, in one table. `page` is the page you are on; `true` is a page inside the section,
 * where the control points at the section root rather than at what you are reading.
 */
const EXPECTED: { path: string; label: string; value: 'page' | 'true' }[] = [
  { path: '/dashboard', label: 'Home', value: 'page' },
  { path: '/', label: 'Home', value: 'page' },
  { path: '/accounts', label: 'Accounts', value: 'page' },
  { path: '/history', label: 'History', value: 'page' },
  { path: `/transactions/${TX_ID}`, label: 'History', value: 'true' },
  { path: '/settings', label: 'Settings', value: 'page' },
];

describe.each(SURFACES)('%s', (surface) => {
  it.each(EXPECTED)('lights exactly $label on $path', ({ path, label, value }) => {
    renderNav(surface, path);

    // "Exactly one" is half the assertion: two lit items is as wrong as none, and only one of those
    // two failures is visible in a screenshot.
    expect(currentItems()).toEqual([{ label, value }]);
  });

  it('shows the four places in one order, as links with real hrefs', () => {
    renderNav(surface, '/dashboard');
    const nav = screen.getByRole('navigation', { name: 'Main navigation' });

    // Links, not buttons. The old `<button onClick={navigate}>` gave these no href in the
    // accessibility tree, and no cmd-click, middle-click or status-bar preview either.
    const links = within(nav).getAllByRole('link');
    expect(links.map((el) => el.textContent?.trim())).toEqual([
      'Home',
      'Accounts',
      'History',
      'Settings',
    ]);
    expect(links.map((el) => el.getAttribute('href'))).toEqual([
      '/dashboard',
      '/accounts',
      '/history',
      '/settings',
    ]);
  });

  it('renders Transfer as an act: a button, never lit, on every route', () => {
    for (const { path } of EXPECTED) {
      const { unmount } = renderNav(surface, path);
      const nav = screen.getByRole('navigation', { name: 'Main navigation' });

      const transfer = within(nav).getByRole('button', { name: 'Transfer' });
      expect(transfer).not.toHaveAttribute('aria-current');
      // Never a link: a place is a link, the act is a button, and that is the whole thesis made
      // readable off the DOM instead of asserted in a comment.
      expect(within(nav).queryByRole('link', { name: 'Transfer' })).toBeNull();

      unmount();
    }
  });
});

describe('navCurrent', () => {
  /**
   * A trailing slash used to fall out of the exact-match branch and get caught by the DESCENDANT
   * branch instead, so `/accounts/` announced `'true'` where `/accounts` announced `'page'` — a
   * regression against the old bare-`startsWith` matcher, reachable from any bookmark or
   * hand-written link. React Router keeps the slash in `location.pathname`; nothing upstream
   * normalises it.
   */
  it.each([
    ['/dashboard/', 'Home'],
    ['/accounts/', 'Accounts'],
    ['/history/', 'History'],
    ['/settings/', 'Settings'],
    ['/accounts//', 'Accounts'],
  ])('treats %s as the page itself, not a descendant', (pathname, label) => {
    const place = NAV_PLACES.find((p) => p.label === label)!;
    expect(navCurrent(place, pathname)).toBe('page');
  });

  it('still lights Home at the bare root', () => {
    // The guard against the obvious fix: stripping trailing slashes unconditionally maps '/' to
    // '', which drops Home entirely — a worse bug than the one being fixed.
    expect(navCurrent(NAV_PLACES[0], '/')).toBe('page');
  });

  it('pairs the filled icon with the active state, and only then', () => {
    // Nothing else asserts WHICH icon renders, so before both surfaces derived this from one
    // function an inverted ternary on either of them was invisible to the whole suite.
    for (const place of NAV_PLACES) {
      const onIt = resolveNavPlace(place, place.matches[place.matches.length - 1]);
      expect(onIt.active).toBe(true);
      expect(onIt.Icon).toBe(place.activeIcon);

      const elsewhere = resolveNavPlace(place, '/nowhere');
      expect(elsewhere.active).toBe(false);
      expect(elsewhere.Icon).toBe(place.icon);
      expect(elsewhere.current).toBeUndefined();
    }
  });

  it('refuses prefixes that are not whole segments', () => {
    const [home, accounts, history] = NAV_PLACES;
    expect(navCurrent(accounts, '/accounts-archive')).toBeUndefined();
    expect(navCurrent(history, '/historyexport')).toBeUndefined();
    // The one that matters: the transfer wizard must never be caught by History's section root.
    expect(navCurrent(history, '/transfer')).toBeUndefined();
    expect(navCurrent(home, '/accounts')).toBeUndefined();
  });
});

describe('the two surfaces cannot disagree', () => {
  it('renders the same places, in the same order, with the same labels', () => {
    const read = (surface: Surface) => {
      const { unmount } = renderNav(surface, '/dashboard');
      const nav = screen.getByRole('navigation', { name: 'Main navigation' });
      const shape = within(nav)
        .getAllByRole('link')
        .map((el) => `${el.textContent?.trim()} -> ${el.getAttribute('href')}`);
      unmount();
      return shape;
    };

    // Not "both match a hardcoded list" — both match EACH OTHER, which is the property that broke.
    expect(read('sidebar')).toEqual(read('bottom nav'));
  });
});

describe('Sidebar footer', () => {
  it('keeps Logout an action: outside the nav landmark and never current', () => {
    renderNav('sidebar', '/settings');

    const logout = screen.getByRole('button', { name: /logout/i });
    expect(logout).not.toHaveAttribute('aria-current');
    // Sign-out is not a destination, so it does not belong to the navigation landmark.
    expect(screen.getByRole('navigation', { name: 'Main navigation' })).not.toContainElement(
      logout,
    );
  });

  it('no longer offers a second route to Settings', () => {
    renderNav('sidebar', '/dashboard');

    // Desktop used to reach this page through an `onSettings` callback while mobile reached it
    // through the table, which is precisely why only mobile ever lit it. One route now.
    expect(screen.getAllByRole('link', { name: 'Settings' })).toHaveLength(1);
    expect(screen.queryByRole('button', { name: 'Settings' })).toBeNull();
  });
});

/**
 * The guard against the NEXT drift, rather than this one.
 *
 * Every other assertion in this file compares the nav against a list written by hand in this file —
 * structurally the same shape as the bug, since another file can outgrow it silently. This one
 * reads the routing table itself. Both nav components carry comments memorialising the last time a
 * label and a path disagreed here; drift is the recurring failure mode, so something has to watch
 * for it mechanically.
 */
describe('routing table', () => {
  /** Each `<Route …>` block, paired with whether it renders inside the app shell. */
  const routes = appSource
    .split('<Route')
    .slice(1)
    .map((chunk: string) => ({
      path: /path="([^"]+)"/.exec(chunk)?.[1],
      // `'<ProtectedShell'` without the closing bracket, deliberately. Matching `'<ProtectedShell>'`
      // recognised the shell ONLY when written with zero attributes, so `<ProtectedShell key="t">`
      // read as out-of-shell and BOTH guards below went green with the transfer wizard rendering
      // inside the nav-bearing shell — the exact thing they exist to prevent. `key` needs no prop
      // declaration and is invisible to tsc, so nothing else would have caught it.
      inShell: chunk.includes('<ProtectedShell'),
    }))
    .filter((r): r is { path: string; inShell: boolean } => typeof r.path === 'string');

  it('finds the routes it is supposed to be guarding', () => {
    // Without this the two assertions below would both pass against a parser that found nothing.
    expect(routes.length).toBeGreaterThanOrEqual(8);
    expect(routes.filter((r) => r.inShell).length).toBeGreaterThanOrEqual(6);
  });

  it('gives every in-shell route exactly one nav place', () => {
    for (const route of routes.filter((r) => r.inShell)) {
      const pathname = route.path.replace(/:[^/]+/g, TX_ID);
      const lit = NAV_PLACES.filter((place) => navCurrent(place, pathname) !== undefined);
      expect(
        lit.map((p) => p.label),
        `route ${route.path}`,
      ).toHaveLength(1);
    }
  });

  it('keeps the money wizards OUT of the shell, where no nav can be mounted', () => {
    // The invariant, enforced rather than asserted: the nav must never mount on a route where
    // `keyLive` can be true. Bringing the transfer wizard inside the shell so its item could light
    // would put four always-enabled escape routes beside a `disabled={keyLive}` one and silently
    // void the anti-double-spend guard.
    for (const wizard of ['/transfer', '/transfer/internal', '/pin-setup']) {
      const route = routes.find((r) => r.path === wizard);
      expect(route, `route ${wizard} must exist`).toBeDefined();
      expect(route?.inShell, `route ${wizard} must NOT be in the shell`).toBe(false);
    }
  });
});
