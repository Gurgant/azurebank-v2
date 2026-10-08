import { useCallback, useEffect, useRef } from 'react';
import { Outlet, useLocation, useMatches } from 'react-router-dom';
import { makeStyles } from '@fluentui/react-components';
import { pageTitle, ROUTE_ANNOUNCE_DELAY_MS, TitleOverride } from './pageTitle';

/**
 * What a route change tells someone who cannot see it happen.
 *
 * A client-side navigation swaps the page without a load, so a screen reader hears nothing and
 * keyboard focus stays on the link that was pressed, or falls to `<body>` when that link unmounts.
 * This sits at the top of the route tree and, for every route that names a title in its `handle`:
 * - sets `document.title` (WCAG 2.4.2), on a load as well as on a change;
 * - on a CHANGE only, writes "<title> page loaded" into a polite status region. The region is
 *   emptied at the change and written `ROUTE_ANNOUNCE_DELAY_MS` later: / and /dashboard are both
 *   "Home", and text the region already holds is no change for a screen reader to announce. A
 *   change that is left again inside that delay is never announced;
 * - on a change only, moves focus to the page's `<main>`, or to its `<h1>` where there is no main
 *   (the full-screen wizards), unless focus is already somewhere the new page put it.
 *
 * Focus goes to `<main>` rather than to the page's heading, because the dashboard's
 * `<h1>` is the balance figure. A container that is not focusable gets `tabindex="-1"` for the one
 * focus and loses it on blur, and `index.css` gives it a focus ring under `:focus-visible`, so a
 * keyboard user who just left a ringed nav link can see where focus went; after a mouse click the
 * browser does not match `:focus-visible` and nothing is drawn.
 *
 * Deliberate, and each pinned in `route-announcer.test.tsx`:
 * - A load announces nothing and moves nothing: the browser and the screen reader already treat a
 *   load as a new page. StrictMode's second effect run on mount is the same path and the same
 *   answer.
 * - A redirect DURING a load is a change. A signed-out load of /dashboard lands on /login through
 *   `ProtectedRoute`'s `<Navigate replace>`, and hears "Sign in page loaded" — which is where it
 *   is, not where it asked to go. Telling that redirect apart from the one after a real sign-in
 *   would mean guessing at intent.
 * - Focus inside a dialog is never taken. `StepUpModal` and `SessionExpiryWarning` render ABOVE
 *   the router and trap focus; pulling it into `<main>` would put it behind their modal.
 * - While a Fluent modal is open, Tabster sets `aria-hidden` on everything outside it, and lifts it
 *   on a 250ms timer after the modal closes. A dialog that navigates (deposit, withdraw, closing
 *   an account) would otherwise announce into a hidden region and focus a hidden `<main>`, so both
 *   wait until the region is exposed again, however long that takes: the two modals above the
 *   router can stay open across a Back navigation for as long as the user leaves them, and text
 *   written into a hidden region is never announced, not even when the region is exposed later.
 *   Leaving the route, or unmounting, ends the wait.
 * - A page that stands in for the route — the outage page at start-up — puts its own title in
 *   place of the route's through `TitleOverride`, and a route change under it keeps that title
 *   and announces it. The stand-in is read when the change is handled, not when it is rendered:
 *   by then a guard that is leaving with the route has already given the title back.
 */

const useStyles = makeStyles({
  // Visually hidden, still read: the region takes no space and draws nothing.
  region: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    margin: '-1px',
    padding: 0,
    border: 0,
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  },
});

type TitleHandle = { title?: string } | undefined;

export function RouteAnnouncer() {
  const styles = useStyles();
  const { pathname } = useLocation();
  const matches = useMatches();
  const title = matches
    .map((match) => (match.handle as TitleHandle)?.title)
    .filter(Boolean)
    .at(-1);
  const region = useRef<HTMLDivElement>(null);
  // The path last titled. Null until the first titled render, which is the load.
  const settled = useRef<string | null>(null);
  // The route's own title, and the title of a page standing in for it, if any.
  const routeTitle = useRef<string | undefined>(undefined);
  const standIn = useRef<string | null>(null);

  // A stand-in that comes or goes without a route change titles the page there and then.
  const setStandIn = useCallback((next: string | null) => {
    standIn.current = next;
    const shown = next ?? routeTitle.current;
    if (shown) document.title = pageTitle(shown);
  }, []);

  useEffect(() => {
    // An untitled route is a redirect on its way somewhere titled (/profile, the * fallback).
    if (!title) return;
    routeTitle.current = title;
    const shown = standIn.current ?? title;
    document.title = pageTitle(shown);

    const isLoad = settled.current === null || settled.current === pathname;
    settled.current = pathname;
    const el = region.current;
    if (isLoad || !el) return;

    el.textContent = '';
    let announcement: number | undefined;
    const stopWaiting = whenExposed(el, () => {
      focusTheNewPage();
      announcement = window.setTimeout(() => {
        el.textContent = `${shown} page loaded`;
      }, ROUTE_ANNOUNCE_DELAY_MS);
    });
    return () => {
      stopWaiting();
      window.clearTimeout(announcement);
    };
  }, [pathname, title]);

  return (
    <>
      <div ref={region} role="status" data-route-announcer="" className={styles.region} />
      <TitleOverride value={setStandIn}>
        <Outlet />
      </TitleOverride>
    </>
  );
}

/** Runs `then` once no ancestor of `el` is aria-hidden. The returned function ends the wait. */
function whenExposed(el: HTMLElement, then: () => void): () => void {
  if (!el.closest('[aria-hidden="true"]')) {
    then();
    return () => {};
  }
  const observer = new MutationObserver(() => {
    if (!el.closest('[aria-hidden="true"]')) {
      observer.disconnect();
      then();
    }
  });
  observer.observe(document.body, {
    attributes: true,
    attributeFilter: ['aria-hidden'],
    subtree: true,
  });
  return () => observer.disconnect();
}

function focusTheNewPage() {
  const active = document.activeElement;
  if (active?.closest('[aria-modal="true"],[role="dialog"],[role="alertdialog"]')) return;

  const main = document.querySelector('main');
  // Already inside the new page (an autoFocus field in main), or, with no main, anywhere but body
  // (PinSetupPage's first digit box): the page chose, so leave it.
  if (main ? main.contains(active) : active !== null && active !== document.body) return;

  const target = main ?? document.querySelector('h1');
  if (!(target instanceof HTMLElement)) return;
  if (!target.hasAttribute('tabindex')) {
    target.setAttribute('tabindex', '-1');
    target.setAttribute('data-route-focus', '');
    target.addEventListener(
      'blur',
      () => {
        target.removeAttribute('tabindex');
        target.removeAttribute('data-route-focus');
      },
      { once: true },
    );
  }
  target.focus({ preventScroll: true });
}
