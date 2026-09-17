import { useEffect, useRef } from 'react';
import { Outlet, useLocation, useMatches } from 'react-router-dom';
import { makeStyles } from '@fluentui/react-components';
import { pageTitle } from './pageTitle';

/**
 * What a route change tells someone who cannot see it happen.
 *
 * A client-side navigation swaps the page without a load, so a screen reader hears nothing and
 * keyboard focus stays on the link that was pressed, or falls to `<body>` when that link unmounts.
 * This sits at the top of the route tree and, for every route that names a title in its `handle`:
 * - sets `document.title` (WCAG 2.4.2), on a load as well as on a change;
 * - on a CHANGE only, writes "<title> page loaded" into a polite status region, the announcement
 *   `docs/design/frontend-design/04a-ux-user-flows.md` specifies;
 * - on a change only, moves focus to the page's `<main>`, or to its `<h1>` where there is no main
 *   (the full-screen wizards), unless focus is already somewhere the new page put it.
 *
 * Focus goes to `<main>` rather than to the heading the UX doc asks for, because the dashboard's
 * `<h1>` is the balance figure. A container that is not focusable gets `tabindex="-1"` for the one
 * focus and loses it on blur, and `index.css` draws no outline for it: the focus is for the reader
 * and the keyboard, and the page looks exactly as it did.
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
 *   wait until the region is exposed again, for at most `EXPOSE_WAIT_MS`.
 */

/** Tabster's hidden-update timer is 250ms; this is the most the announcement waits past it. */
const EXPOSE_WAIT_MS = 1000;

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

  useEffect(() => {
    // An untitled route is a redirect on its way somewhere titled (/profile, the * fallback).
    if (!title) return;
    document.title = pageTitle(title);

    const isLoad = settled.current === null || settled.current === pathname;
    settled.current = pathname;
    const el = region.current;
    if (isLoad || !el) return;

    return whenExposed(el, () => {
      el.textContent = `${title} page loaded`;
      focusTheNewPage();
    });
  }, [pathname, title]);

  return (
    <>
      <div ref={region} role="status" data-route-announcer="" className={styles.region} />
      <Outlet />
    </>
  );
}

/** Runs `then` once no ancestor of `el` is aria-hidden, or after `EXPOSE_WAIT_MS` regardless. */
function whenExposed(el: HTMLElement, then: () => void): () => void {
  if (!el.closest('[aria-hidden="true"]')) {
    then();
    return () => {};
  }
  const stop = () => {
    observer.disconnect();
    window.clearTimeout(timer);
  };
  const observer = new MutationObserver(() => {
    if (!el.closest('[aria-hidden="true"]')) {
      stop();
      then();
    }
  });
  const timer = window.setTimeout(() => {
    stop();
    then();
  }, EXPOSE_WAIT_MS);
  observer.observe(document.body, {
    attributes: true,
    attributeFilter: ['aria-hidden'],
    subtree: true,
  });
  return stop;
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
