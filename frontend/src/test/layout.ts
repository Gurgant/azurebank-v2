/**
 * The layout jsdom does not have, supplied only as far as something depends on it.
 *
 * jsdom parses CSS but never lays anything out, so every element reports `offsetParent === null`
 * and a 0x0 `getBoundingClientRect()`. Those are not neutral answers — they are the answers a real
 * browser gives for an element that is not rendered at all, and libraries that ask read them as
 * exactly that.
 *
 * ─── WHAT THIS FIXES, and it is not a style problem ────────────────────────────────────────────
 *
 * Fluent's Dialog traps focus through tabster, and tabster decides what is focusable with
 * `isDisplayNone()`, which begins (tabster/dist/index.js:1576):
 *
 *     if (element.offsetParent === null && document.body !== element && style.position !== 'fixed')
 *         return true;
 *
 * Under jsdom that is true of every element inside an open dialog, so tabster concludes the dialog
 * contains nothing focusable. Fluent's `useFocusFirstElement` then falls through its whole ladder —
 * `focusFirst`, `focusDefault` — to `resetFocus(modalizerRoot)`, which focuses the SURFACE itself
 * via `focus(container, true, true)`. That second argument is `noFocusedProgrammaticallyFlag`: the
 * focus deliberately does NOT carry the programmatic marker, and `ModalizerAPI._onFocus` only calls
 * `setActive()` for programmatic focus. So the modalizer never becomes active.
 *
 * 250ms later — `hiddenUpdate()` is a debounced `setTimeout(…, 250)` at index.js:5354 — the pass
 * runs, finds that the open dialog's modalizer is not `activeId`, and puts it in `hiddenElements`:
 *
 *     ModalizerAPI._hiddenUpdate → toggle → augmentAttribute → setAttribute('aria-hidden', 'true')
 *
 * on the OPEN dialog's own `fui-DialogSurface`. Nothing ever removes it. Everything inside the
 * dialog leaves the accessibility tree permanently, so `getByRole` stops finding it while
 * `getByText` — which never consults that tree — still does.
 *
 * That is the whole flake. Four tests across three files (`account-reveal`, `stepup-interceptor`,
 * `settings` twice) failed intermittently with `Unable to find role="button" and name "…"`, always
 * on a `findByRole(role, { name })` issued while a Fluent dialog was open. It was a race against a
 * 250ms timer: a query that landed inside the window passed, one that landed after it could never
 * pass, and CPU contention pushed almost everything past it. Measured on main 8c1c521 — 4 of 6 full
 * `vitest run` passes red, and 2 of 2 with eight busy cores.
 *
 * ─── WHY THIS RATHER THAN A LOOSER QUERY ───────────────────────────────────────────────────────
 *
 * `{ hidden: true }` or a text query would have made the red go away in one line, and both would
 * have been lies: they would assert that a control is reachable while the DOM says it is not, on a
 * PIN prompt whose whole job is to be operable. The accessibility fact was never wrong — it was
 * FABRICATED by jsdom telling tabster that a visible dialog was not rendered. Fixing the input
 * removes the false fact instead of agreeing not to look at it.
 *
 * The check is falsifiable rather than assumed: with this module, focus lands on the PIN input
 * (`aria-label="Digit 1 of 6"`) exactly as a real browser puts it, and the surface's `aria-hidden`
 * stays absent. Without it, focus lands on the surface and `aria-hidden="true"` appears.
 * `tabster-modalizer.test.tsx` pins both halves.
 *
 * ─── SCOPE, deliberately the minimum that works ────────────────────────────────────────────────
 *
 * Only `offsetParent` and the two client-rect methods. Measured under load: stubbing
 * `offsetWidth`/`offsetHeight`/`clientWidth`/`clientHeight` INSTEAD of the rects still failed 2/2,
 * and the rects alone passed 2/2 — so the size properties are not part of the fix and are left
 * alone. Nothing here invents layout for tests to assert on: every element gets the same nominal
 * box, which is enough to answer "is this rendered at all" and useless for anything geometric. A
 * test that wants real geometry needs a real browser (`e2e/`), and this module cannot give it one.
 */

/** The nominal box of a rendered element. One size for everything — see the scope note above. */
const RENDERED_BOX = { width: 100, height: 20 } as const;

function makeRect(width: number, height: number): DOMRect {
  return {
    x: 0,
    y: 0,
    top: 0,
    left: 0,
    right: width,
    bottom: height,
    width,
    height,
    toJSON: () => ({ x: 0, y: 0, top: 0, left: 0, right: width, bottom: height, width, height }),
  };
}

const BOX_RECT = makeRect(RENDERED_BOX.width, RENDERED_BOX.height);
const EMPTY_RECT = makeRect(0, 0);

/**
 * `display: none` anywhere up the chain means no box at all — the one distinction worth keeping.
 *
 * `visibility: hidden` deliberately does NOT count: a hidden element still occupies its box in real
 * CSS, and tabster tests visibility separately in the very next branch of `isDisplayNone`. Blurring
 * the two here would make a closed `ConfirmDialog` (which hides with `visibility`, not `display`)
 * look unrendered and defeat the tests that check it is inert.
 */
function isRendered(element: Element): boolean {
  if (!element.isConnected) return false;
  for (let node: Element | null = element; node; node = node.parentElement) {
    if (window.getComputedStyle(node).display === 'none') return false;
  }
  return true;
}

/**
 * Install the stubs. Idempotent, and safe to call from a setup file that may be evaluated once per
 * test file — each definition simply replaces the previous one.
 */
export function installLayoutStubs(): void {
  Object.defineProperty(HTMLElement.prototype, 'offsetParent', {
    configurable: true,
    get(this: HTMLElement): Element | null {
      // The spec's own answers, in the order the spec gives them: nothing for an unrendered
      // element, nothing for the body, and nothing for a fixed element. tabster's first branch
      // excludes `position: fixed` explicitly for exactly this reason, so returning null there is
      // what keeps the dialog SURFACE (which is fixed) reading as rendered.
      if (!isRendered(this) || this === document.body) return null;
      if (window.getComputedStyle(this).position === 'fixed') return null;

      for (let parent = this.parentElement; parent; parent = parent.parentElement) {
        if (parent === document.body) return parent;
        if (window.getComputedStyle(parent).position !== 'static') return parent;
      }
      return null;
    },
  });

  Element.prototype.getBoundingClientRect = function (this: Element): DOMRect {
    return isRendered(this) ? BOX_RECT : EMPTY_RECT;
  };

  Element.prototype.getClientRects = function (this: Element): DOMRectList {
    // A box-less element has NO rects, not one empty rect — `getClientRects().length === 0` is how
    // callers ask "is this rendered", so an empty list is the meaningful answer.
    const rects = isRendered(this) ? [BOX_RECT] : [];
    return Object.assign(rects, {
      item: (index: number) => rects[index] ?? null,
    }) as unknown as DOMRectList;
  };
}
