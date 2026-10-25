/**
 * Puts focus on a page container that is not focusable, for that one focus.
 *
 * A container with no `tabindex` gets `tabindex="-1"` and `data-route-focus` until it loses focus:
 * a permanent `tabindex` would make it a stop of its own for a mouse click inside it, and would
 * leave this function nothing to add the next time. `index.css` draws the focus ring through
 * `data-route-focus` under `:focus-visible`, so a keyboard visitor sees where focus went and a
 * mouse click draws nothing. A container that already has a `tabindex` keeps it, and no ring is
 * drawn for it here.
 *
 * Shared by `RouteAnnouncer`, which focuses the new page after a route change, and the shell's
 * skip link, which focuses the page on request: one ring and one clean-up for both.
 */
export function focusContainer(target: HTMLElement) {
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
