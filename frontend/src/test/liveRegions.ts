import { expect } from 'vitest';

/**
 * The two roles this app announces with. `role="alert"` carries an implicit
 * `aria-live="assertive"` + `aria-atomic="true"`; `role="status"` is the polite equivalent. Both
 * ARE live regions — the attribute never has to be spelled out for the region to exist, which is
 * exactly why nesting one inside the other is easy to do by accident.
 */
const LIVE_REGION = '[role="alert"],[role="status"],[aria-live]';

/**
 * Assert that no live region sits inside another live region.
 *
 * Nesting is not a tidiness question. Both roles imply `aria-atomic="true"`, so one message ends up
 * with two announceable ancestors and a screen reader may read it twice — and the outer, atomic one
 * merges genuinely separate banners (an error AND a lock countdown) into a single utterance.
 *
 * This dialog grew exactly that way: it had hand-rolled `<div role="alert">` wrappers from before
 * `MessageBar` carried its own role, so adding the role to the banner — the app-wide pattern set by
 * `LoginPage` — quietly produced a pair. A wrapper keeps its `id` for `aria-describedby`; what it
 * must not keep is a role.
 *
 * Scoped to `document.body` rather than a render container because Fluent PORTALS dialogs and
 * popovers: `renderWithProviders(...).container` does not contain the banners this is about.
 */
export function expectNoNestedLiveRegions(scope: HTMLElement = document.body) {
  const nested = Array.from(scope.querySelectorAll<HTMLElement>(LIVE_REGION))
    .filter((region) => region.parentElement?.closest(LIVE_REGION) !== null)
    .map((region) => `${region.getAttribute('role')}: ${region.textContent?.trim().slice(0, 60)}`);

  expect(nested).toEqual([]);
}
