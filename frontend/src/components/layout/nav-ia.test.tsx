import { fireEvent, render, screen, within } from '@testing-library/react';
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

/**
 * Reveals whatever a surface keeps behind a disclosure, so the two can be compared on equal terms.
 *
 * The sidebar is vertical and shows all five places at once. The bottom bar has four cells and puts
 * the utility group behind "More". Comparing them without opening it would compare a full list
 * against a truncated one and call the difference a bug.
 */
function revealAll() {
  const more = screen.queryByRole('button', { name: 'More' });
  if (more && more.getAttribute('aria-expanded') === 'false') fireEvent.click(more);
}

function renderNav(surface: Surface, pathname: string) {
  const ui =
    surface === 'sidebar' ? (
      <Sidebar userName="Demo User" onLogout={() => {}} />
    ) : (
      <BottomNav onLogout={() => {}} />
    );
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
    // Utility places live behind "More" on the bottom bar. Open it, or this asserts that a closed
    // drawer contains nothing — which is true and says nothing.
    revealAll();

    // "Exactly one" is half the assertion: two lit items is as wrong as none, and only one of those
    // two failures is visible in a screenshot.
    expect(currentItems()).toEqual([{ label, value }]);
  });

  it('shows every place in one order, as links with real hrefs', () => {
    renderNav(surface, '/dashboard');
    revealAll();
    const nav = screen.getByRole('navigation', { name: 'Main navigation' });

    // Links, not buttons. The old `<button onClick={navigate}>` gave these no href in the
    // accessibility tree, and no cmd-click, middle-click or status-bar preview either.
    const links = within(nav).getAllByRole('link');
    expect(links.map((el) => el.textContent?.trim())).toEqual([
      'Home',
      'Accounts',
      'History',
      'Settings',
      'Contact',
    ]);
    expect(links.map((el) => el.getAttribute('href'))).toEqual([
      '/dashboard',
      '/accounts',
      '/history',
      '/settings',
      '/about',
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
      revealAll();
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
  it('keeps sign-out an action: a button, never current, never a link', () => {
    renderNav('sidebar', '/settings');

    const signOut = screen.getByRole('button', { name: /sign out/i });
    expect(signOut).not.toHaveAttribute('aria-current');
    expect(screen.queryByRole('link', { name: /sign out/i })).toBeNull();
    // In the sidebar it sits below the landmark entirely — there is room, and it is not a place.
    expect(screen.getByRole('navigation', { name: 'Main navigation' })).not.toContainElement(
      signOut,
    );
  });

  it('offers sign-out on the phone, where it used to take two levels of Settings', () => {
    // The invariant loosened here, deliberately, and this records why. The landmark used to hold
    // exactly ONE non-place control (Transfer); it now holds two, because the bottom bar is the
    // only mobile surface and sign-out was reachable only by opening Settings and scrolling — on
    // the surface where a shared or lost phone makes signing out matter most.
    //
    // What did NOT loosen is what an act IS: a button, never a link, never carrying aria-current.
    renderNav('bottom nav', '/dashboard');
    fireEvent.click(screen.getByRole('button', { name: 'More' }));

    const nav = screen.getByRole('navigation', { name: 'Main navigation' });
    const signOut = within(nav).getByRole('button', { name: /sign out/i });
    expect(signOut).not.toHaveAttribute('aria-current');
    expect(within(nav).queryByRole('link', { name: /sign out/i })).toBeNull();
  });

  it('tells you the utility row is a disclosure, and which way it goes', () => {
    renderNav('bottom nav', '/dashboard');
    const more = screen.getByRole('button', { name: 'More' });

    expect(more).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('link', { name: 'Contact' })).toBeNull();

    fireEvent.click(more);
    expect(more).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('link', { name: 'Contact' })).toBeInTheDocument();
  });

  it('lights the trigger when the page you are on is hidden behind it', () => {
    // Closed on /settings, the bar would otherwise be entirely dark — which is the defect PR #47
    // fixed on that exact route, reintroduced one level down.
    renderNav('bottom nav', '/settings');
    expect(screen.getByRole('button', { name: 'More' })).toHaveAttribute('aria-current', 'true');
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
  /**
   * COMMENTS STRIPPED BEFORE ANY OF THIS IS SCANNED.
   *
   * `App.tsx` explains its own routing at length, so a scan for the thing the prose is ABOUT
   * matches the prose. The gate test below was written, run green, and then falsified by replacing
   * `import.meta.env.DEV &&` with `true &&` — it stayed green, because the sentence explaining why
   * the gate exists still contained the words. It found a comment, not a guard.
   *
   * Same shape as the `useNavigate` scan in `page-header.test.tsx`, which failed for the same
   * reason a few hours earlier. Stripping is also strictly safer for `<ProtectedShell` below: a
   * comment mentioning the shell would otherwise count as rendering inside it.
   */
  const code = appSource.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

  const chunks = code.split('<Route');

  const routes = chunks
    .slice(1)
    .map((chunk: string, i: number) => ({
      path: /path="([^"]+)"/.exec(chunk)?.[1],
      // The text between the PREVIOUS `<Route` and this one — where a wrapping conditional lands
      // once the source is split this way. Exact, not a character-window guess.
      preamble: chunks[i],
      // `'<ProtectedShell'` without the closing bracket, deliberately. Matching `'<ProtectedShell>'`
      // recognised the shell ONLY when written with zero attributes, so `<ProtectedShell key="t">`
      // read as out-of-shell and BOTH guards below went green with the transfer wizard rendering
      // inside the nav-bearing shell — the exact thing they exist to prevent. `key` needs no prop
      // declaration and is invisible to tsc, so nothing else would have caught it.
      inShell: chunk.includes('<ProtectedShell'),
    }))
    .filter(
      (r): r is { path: string; preamble: string; inShell: boolean } => typeof r.path === 'string',
    );

  /**
   * Scratch routes under `/dev/`, exempt from the nav-place rule and from nothing else.
   *
   * They are in the shell on purpose — a design being judged has to sit next to the real sidebar,
   * because the sidebar's 240px is part of what the layout has to survive — but giving one a nav
   * place would put scratch work in the product's navigation. The exemption is narrow (`/dev/`
   * prefix only) and self-deleting: it goes when the gallery does.
   *
   * The price of the exemption is the test below, which is stricter than what it excuses.
   */
  const isScratch = (path: string) => path.startsWith('/dev/');

  it('finds the routes it is supposed to be guarding', () => {
    // Without this the two assertions below would both pass against a parser that found nothing.
    expect(routes.length).toBeGreaterThanOrEqual(8);
    expect(routes.filter((r) => r.inShell).length).toBeGreaterThanOrEqual(6);
  });

  it('keeps every scratch route behind a build-time gate', () => {
    // `import.meta.env.DEV` is replaced with `false` when Vite builds, so Rollup drops the branch
    // and then the module — which is what keeps a scratch route out of production. That is a
    // property of the SOURCE, so it is checked here rather than left to somebody remembering to
    // grep `dist/`. This repo deleted `DEV_BYPASS_AUTH` in A3 because a dev-only door that nobody
    // re-checks quietly stops being dev-only.
    //
    // Passes vacuously once the gallery is deleted and no `/dev/` route exists — which is correct:
    // the rule is "if a scratch route exists it must be gated", not "a scratch route must exist".
    for (const route of routes.filter((r) => isScratch(r.path))) {
      expect(
        route.preamble,
        `scratch route ${route.path} must sit inside import.meta.env.DEV`,
      ).toContain('import.meta.env.DEV');
    }
  });

  it('gives every in-shell route exactly one nav place', () => {
    for (const route of routes.filter((r) => r.inShell && !isScratch(r.path))) {
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
