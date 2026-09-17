import { createKeyborg } from 'keyborg';

/**
 * Keyborg's programmatic-focus marker, installed before user-event can hide it.
 *
 * ─── THE CHAIN ─────────────────────────────────────────────────────────────────────────────────
 *
 * Fluent's Dialog traps focus through tabster, and tabster asks keyborg whether a focus move was
 * programmatic. Keyborg finds out by wrapping `HTMLElement.prototype.focus` with a plain
 * assignment (keyborg 2.6.0, dist/index.js:96): the wrapper records the element, and the next
 * `focusin` on it is reported with `isFocusedProgrammatically: true`.
 *
 * `userEvent.setup()` patches the same method (user-event 14.6.1, `document/patchFocus.js`) with
 * `Object.defineProperties`, as a GETTER with no setter. A test that calls `setup()` before it
 * renders, as `settings.test.tsx` does, has done that before Fluent creates tabster and tabster
 * creates keyborg. Keyborg first checks that it can replace `focus` by assigning a probe and
 * focusing a button (`canOverrideNativeFocus`, dist/index.js:66). Under the getter that assignment
 * is ignored without an error, the probe never runs, and keyborg stops marking any focus as
 * programmatic: the dialog's own first focus, `useFocusFirstElement` focusing the input, arrives as
 * `isFocusedProgrammatically: undefined`, measured with a listener on `keyborg:focusin`.
 *
 * Tabster reads that as a user moving focus into a modalizer that is not the active one, so instead
 * of activating the dialog's modalizer it schedules `_restoreModalizerFocus` 100ms later (tabster
 * 8.7.0, dist/index.js:5283). That pass finds no active modalizer to pull focus into and focuses
 * the last focusable element before the dialog's portal — on the settings page, "Log out" — and the
 * dialog, never active, is aria-hidden by the 250ms `hiddenUpdate` that `test/layout.ts` describes.
 * A role query that lands before that passes; one that lands after cannot. Measured with a stack on
 * every focusin: the move to "Log out" came from `ModalizerAPI._restoreModalizerFocus` on a timer,
 * in a PASSING run as well, so the only thing that varied was how late the query arrived.
 * Whether a real browser can reach that timer was not measured: user-event exists only in tests.
 *
 * ─── THE FIX ───────────────────────────────────────────────────────────────────────────────────
 *
 * Create one keyborg for the test window here, before any test can call `userEvent.setup()`, and
 * never dispose it. Keyborg keeps one core per window (`window.__keyborg`), shared by every
 * instance and disposed with the last of them, so this instance keeps the focus wrapper for the
 * whole file and tabster's instances reuse it. user-event, patching later, keeps keyborg's wrapper
 * as the `focus` it calls. The same `keyborg:focusin` listener then reports the dialog's first
 * focus as programmatic, the modalizer activates, and focus stays in the dialog.
 *
 * `package.json` declares keyborg at 2.6.0, the exact version tabster 8.7.0 pins, because this file
 * imports it directly rather than through Fluent.
 *
 * One consequence, read from the source and not measured: user-event moves focus by calling
 * `element.focus()`, so a CLICK in a test is also reported as programmatic, where a real mouse
 * click is not. Tabster treats a programmatic focus outside the active modalizer as a deliberate
 * move and follows it, so a test that clicks outside an open dialog and expects focus to be pulled
 * back would not see that here.
 *
 * ─── MEASURED, 2026-09-17 on main 9dc371d ──────────────────────────────────────────────────────
 *
 * `keyborg.test.tsx` is the guard. With the call in `setup.ts` commented out, both of its tests
 * failed in 4 of 4 runs: a keyborg started after `userEvent.setup()` reported a `focus()` call as
 * `[undefined]`, and in the 3 runs that printed it, 500ms after the settings dialog opened, focus
 * was on "Log out", outside the dialog, with `aria-hidden="true"` on the surface. With the call,
 * both passed 4 of 4.
 *
 * Under eight busy cores, the whole of `settings.test.tsx` failed 2 of 10 runs without this install
 * and 0 of 30 with it; at a 2-in-10 rate, 30 clean runs by chance is about 1 in 800. The guard
 * above is the stronger evidence: without the install it fails every time.
 */
export function installKeyborgBeforeUserEvent(): void {
  createKeyborg(window);
}
