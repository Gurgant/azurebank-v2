import {
  ArrowSwap24Filled,
  History24Filled,
  History24Regular,
  Home24Filled,
  Home24Regular,
  Settings24Filled,
  Settings24Regular,
  Wallet24Filled,
  Wallet24Regular,
  type FluentIcon,
} from '@fluentui/react-icons';

/**
 * The app's information architecture: the PLACES you can be, and the one ACT you can perform.
 *
 * Sidebar and BottomNav used to each own a list, and the lists disagreed on everything that could
 * be disagreed about — measured in a real browser rather than inferred:
 *
 *  - COUNT: four items plus a footer button, against five tabs.
 *  - ORDER: History before Transfer on one surface, after it on the other.
 *  - LABELS: the same page called "Dashboard" and "Home"; the same page called "Settings" and
 *    "Profile".
 *  - ACTIVE STATE: on `/` nothing lit on either surface, on `/transactions/:id` nothing lit on
 *    either surface, and on `/settings` the bottom nav lit "Profile" while the sidebar lit nothing
 *    at all.
 *
 * That last asymmetry had a cause worth naming, because it is the shape of the whole problem:
 * desktop reached Settings through an `onSettings` CALLBACK while mobile reached it through the
 * table. Only the table could ever learn about the `/profile` alias, so only mobile lit. A shared
 * list removes the second matcher; deleting that callback — which this change also does — removes
 * the second navigation path. Both were needed.
 *
 * This module is the only table and the only matcher. Neither nav surface, nor PageHeader when it
 * arrives, may define, extend or override either.
 */

export interface NavPlace {
  /** The canonical URL this place links to — literally the `<Link>` href. */
  path: string;
  /** Visible text AND accessible name. Deliberately no `aria-label` anywhere. */
  label: string;
  /** Section roots that light this place. The sets are disjoint, so order is irrelevant. */
  matches: string[];
  /**
   * Icon COMPONENTS, not rendered elements, which is what keeps this a `.ts` module.
   *
   * Holding JSX here would force `.tsx`, and a `.tsx` file exporting constants and functions
   * alongside no components trips `react-refresh/only-export-components` — an error in this repo.
   * Component references are also the idiomatic shape: consumers write `<place.icon />` and get a
   * fresh element per render instead of sharing one module-level instance.
   */
  icon: FluentIcon;
  activeIcon: FluentIcon;
}

export const NAV_PLACES: NavPlace[] = [
  {
    path: '/dashboard',
    label: 'Home',
    // `/` renders the same component as `/dashboard` (App.tsx) and is where the `*` fallback lands,
    // so it is an alias rather than a place of its own. Declared as data; never inferred.
    matches: ['/', '/dashboard'],
    icon: Home24Regular,
    activeIcon: Home24Filled,
  },
  {
    path: '/accounts',
    label: 'Accounts',
    matches: ['/accounts'],
    icon: Wallet24Regular,
    activeIcon: Wallet24Filled,
  },
  {
    path: '/history',
    label: 'History',
    // A transaction detail is a page INSIDE history. It used to light nothing, so drilling into a
    // transaction made the navigation forget where you were.
    matches: ['/history', '/transactions'],
    icon: History24Regular,
    activeIcon: History24Filled,
  },
  {
    path: '/settings',
    label: 'Settings',
    // One URL. `/profile` now redirects here, which is why there is no alias to list: the page's
    // own heading already reads "Settings", and it contains a card called "Profile" — naming the
    // page after one of its own cards is how the two surfaces ended up disagreeing.
    matches: ['/settings'],
    icon: Settings24Regular,
    activeIcon: Settings24Filled,
  },
];

/**
 * The ACT, and it is deliberately not a `NavPlace`.
 *
 * Measured: tapping the old "Transfer" tab navigated to `/transfer` and the bar itself vanished —
 * `document.querySelector('nav[aria-label="Main navigation"]')` returned null immediately
 * afterwards, because the transfer wizard renders outside the app shell on purpose. A control that
 * destroys its own container the instant you press it was never a member of that container's list,
 * which is why `isActive('/transfer')` was unreachable code on both surfaces rather than a styling
 * gap.
 *
 * It has no `matches`, it can never be active, it renders as a `<button>` where places render as
 * links, and it carries the filled icon always — there is no inactive variant to swap with.
 *
 * **The invariant this expresses:** the navigation must never be mounted on a route where
 * `keyLive` can be true. Bringing the wizard inside the shell so its item could light would put
 * four always-enabled escape routes beside a `disabled={keyLive}` one and silently void the
 * anti-double-spend guard.
 */
export const TRANSFER_ACTION = {
  path: '/transfer',
  label: 'Transfer',
  icon: ArrowSwap24Filled,
} as const;

export type NavCurrent = 'page' | 'true' | undefined;

/**
 * Which flavour of `aria-current` this place should carry, if any.
 *
 * `'page'` is the page you are on. `'true'` is a page INSIDE the section — today only
 * `/transactions/:id`, where the History control points at `/history` rather than at the
 * transaction being read, so claiming `'page'` would be a small lie. The visual treatment is
 * identical for both; only the attribute differs.
 *
 * Matching is on segment boundaries, not raw `startsWith`: a bare prefix would let a future
 * `/accounts-archive` light Accounts. It is also why `/transfer` can never be caught by History's
 * `/transactions` entry. The `base !== '/'` guard is explicit rather than relying on `'//'` never
 * occurring — a malformed `//foo` would otherwise light Home as a descendant.
 */
export function navCurrent(place: NavPlace, pathname: string): NavCurrent {
  const path = canonicalPathname(pathname);
  if (place.matches.includes(path)) return 'page';
  if (place.matches.some((base) => base !== '/' && path.startsWith(`${base}/`))) return 'true';
  return undefined;
}

/**
 * Trailing slashes off, and the root preserved.
 *
 * Without this, `/accounts/` missed the exact-match branch and was then caught by the DESCENDANT
 * branch — so a bookmark or a hand-written link with a trailing slash lit Accounts as `'true'`
 * instead of `'page'`. React Router does not normalise it (`location.pathname` keeps the slash) and
 * nothing in the hosting setup does either, so it has to happen here.
 *
 * The `|| '/'` is what makes it safe: an unconditional strip maps `'/'` to `''` and drops Home
 * entirely, which is a worse bug than the one being fixed. The `+` handles `//` in one pass.
 */
function canonicalPathname(pathname: string): string {
  return pathname.replace(/\/+$/, '') || '/';
}

/**
 * Everything a nav surface needs about one place at one pathname.
 *
 * The three derivations here — the `aria-current` value, the boolean the classes switch on, and
 * which of the two icons to render — used to sit inline in both Sidebar and BottomNav. That is the
 * same two-copies shape this module exists to remove, one level down: the matcher was shared but
 * its interpretation was not.
 *
 * The icon pairing is the reason this is worth a function rather than a convention. Nothing asserts
 * which icon renders, so a surface could ship `active ? place.icon : place.activeIcon` — inverted —
 * and the whole suite would stay green. Deriving it once makes that unrepresentable instead of
 * merely unlikely.
 *
 * `Icon` is capitalised because it is a component the caller renders as `<Icon />`, not a value.
 */
export function resolveNavPlace(place: NavPlace, pathname: string) {
  const current = navCurrent(place, pathname);
  const active = current !== undefined;
  return { current, active, Icon: active ? place.activeIcon : place.icon };
}

/** The place a pathname belongs to. PageHeader will read its title and back target from this. */
export const findActiveNavPlace = (pathname: string): NavPlace | undefined =>
  NAV_PLACES.find((place) => navCurrent(place, pathname) !== undefined);
