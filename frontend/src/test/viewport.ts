/**
 * A `matchMedia` stub that can actually change, and the API tests use to change it.
 *
 * jsdom has no `matchMedia` at all, so something has to stand in. The first stand-in computed
 * `matches` once at construction and made `addEventListener` a no-op — enough for a suite that only
 * ever renders at one width, which is precisely what this suite was. `setup.ts` even claimed a test
 * "can say so by setting this and firing a resize"; it could not, and nothing said so.
 *
 * `useMediaQuery` reads through `useSyncExternalStore`: the snapshot is `mql.matches` and the
 * subscription is `addEventListener('change', …)`. A frozen property defeats the first half and a
 * no-op listener defeats the second, so a test that set `window.innerWidth` and expected a
 * re-render would have quietly asserted against the old layout — a green test proving nothing.
 *
 * Two changes fix it. `matches` is a **getter**, so the snapshot is always live. And every
 * MediaQueryList handed out is retained, so {@link setViewportWidth} can fire `change` on exactly
 * the ones whose answer moved.
 */

/** 1024 is exactly the `lg` breakpoint, so this is the desktop tree — stated, not inherited. */
export const TEST_VIEWPORT_WIDTH = 1024;
export const TEST_VIEWPORT_HEIGHT = 768;

type ChangeListener = (event: MediaQueryListEvent) => void;

interface StubMediaQueryList extends MediaQueryList {
  readonly __listeners: Set<ChangeListener>;
}

/**
 * Every list ever handed out, because a change has to reach the ones already mounted.
 *
 * `useMediaQuery` memoises one MediaQueryList per query for the life of the component, so the
 * object a mounted hook holds is the only route to it — there is no re-calling `matchMedia` to
 * pick up a new value.
 */
const live = new Set<StubMediaQueryList>();

/**
 * Resolve a width condition against the current viewport.
 *
 * Only min-width and max-width are handled, which is enough: the app's breakpoint module is
 * min-width only by design. Anything else falls through to "no match", the jsdom-neutral answer.
 */
function matchesWidth(query: string): boolean {
  const conditions = [...query.matchAll(/\((min|max)-width:\s*(\d+(?:\.\d+)?)px\)/g)];
  if (conditions.length === 0) return false;
  // Multiple conditions in one query string are ANDed — the same way CSS reads `and`.
  return conditions.every(([, kind, px]) =>
    kind === 'min' ? window.innerWidth >= Number(px) : window.innerWidth <= Number(px),
  );
}

function evaluate(query: string): boolean {
  // Strictly the REDUCE query — a bare 'prefers-reduced-motion' would also match
  // '(prefers-reduced-motion: no-preference)' and lie to both-mode checks.
  return query.includes('prefers-reduced-motion: reduce') || matchesWidth(query);
}

export function matchMediaStub(query: string): MediaQueryList {
  const listeners = new Set<ChangeListener>();
  const mql = {
    media: query,
    onchange: null,
    get matches() {
      return evaluate(query);
    },
    // Both the modern and the deprecated listener APIs, because Fluent still uses addListener.
    addListener: (fn: ChangeListener) => listeners.add(fn),
    removeListener: (fn: ChangeListener) => listeners.delete(fn),
    addEventListener: (_type: string, fn: ChangeListener) => listeners.add(fn),
    removeEventListener: (_type: string, fn: ChangeListener) => listeners.delete(fn),
    dispatchEvent: () => false,
    __listeners: listeners,
  } as unknown as StubMediaQueryList;

  live.add(mql);
  return mql;
}

/**
 * Resize the simulated viewport and notify only the queries whose answer changed.
 *
 * Call it inside `act()` when a component is mounted — the notification lands in a React
 * `useSyncExternalStore` subscription and schedules a render.
 *
 * Notifying only the movers is not an optimisation: a `change` event on a query that did not change
 * would let a broken hook look correct, because any re-render would appear to be a reaction.
 */
export function setViewportWidth(width: number): void {
  const before = new Map([...live].map((q) => [q, q.matches]));
  window.innerWidth = width;

  for (const mql of live) {
    if (mql.matches === before.get(mql)) continue;
    const event = Object.assign(new Event('change'), {
      matches: mql.matches,
      media: mql.media,
    }) as unknown as MediaQueryListEvent;
    for (const listener of [...mql.__listeners]) listener(event);
  }

  // Anything still reading window size directly rather than through a query.
  window.dispatchEvent(new Event('resize'));
}

/** Back to the desktop default, so one test's viewport never decides the next one's. */
export function resetViewport(): void {
  setViewportWidth(TEST_VIEWPORT_WIDTH);
  // Lists from an unmounted tree cannot be reached again and would otherwise accumulate for the
  // whole file — a leak that only ever costs time, but costs it on every subsequent resize.
  live.clear();
}
