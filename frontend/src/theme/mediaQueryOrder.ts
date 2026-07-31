/**
 * Why the responsive cascade needs a comparator at all.
 *
 * Griffel puts every `@media` rule in its own `<style>` element and decides where to insert it with
 * `renderer.compareMediaQueries`. Its default is a STRING comparison:
 *
 *     const defaultCompareMediaQueries = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
 *     // @griffel/core 1.19.2, renderer/createDOMRenderer
 *
 * Lexicographically, `'(min-width: 1024px)' < '(min-width: 480px)'` — because `'1' < '4'`. So the
 * emitted order of this app's four breakpoints is **lg, xl, sm, md**, not ascending. Media rules
 * carry no extra specificity, so at a width where two of them match, the LATER one wins: at 1024px+
 * the `sm` rule beats the `lg` rule it was meant to override.
 *
 * That was not a theory. Measured in a real browser at 1280px, `AuthLayout`'s form panel computed
 * `padding: 40px 32px` — the `sm` value — while its `lg` rule sat two stylesheets earlier, matching
 * and losing. The `lg` padding had never once applied since it was written.
 *
 * A source scan found exactly ONE such collision in the app today, which is why only one screen was
 * visibly wrong. That number is the reason to fix the ordering rather than the single site: the next
 * two-breakpoint declaration anyone writes inherits the same inversion, and it fails silently — the
 * rule is present, it matches, the browser simply applies the other one.
 */

/** `(min-width: 640px)` → `640`. Only a min-width query has an intrinsic order. */
const MIN_WIDTH = /\(min-width:\s*(\d+)px\)/;

/**
 * Two classes, then a rule inside each — and it has to be shaped that way to be an ORDER.
 *
 * The obvious version compares numerically when both queries carry a min-width and falls back to
 * the string comparison otherwise. That is not transitive, and the cycle is easy to write down:
 * `480px < 1024px` numerically, `1024px < 2rem` as strings, and `2rem < 480px` as strings. A
 * comparator with a cycle has no defined result — the placement would go back to depending on
 * insertion order, which is the bug this file exists to remove, reintroduced one level down.
 * (On the six queries this app actually emits today there is no cycle; a `rem` breakpoint would
 * create one, and "no cycle yet" is not a property worth relying on.)
 *
 * So width queries form one class ordered by width, everything else forms another ordered as
 * before, and the classes never interleave. Transitive by construction.
 *
 * The non-width class sorts FIRST, and that is chosen to preserve today's behaviour rather than to
 * improve on it: the media bucket is shared with Fluent, whose `(forced-colors: active)` block
 * already lands ahead of every `(min-width: …)` sheet under the default comparator, purely because
 * `'f' < 'm'`. Keeping it there means this change moves exactly the sheets it was written to move.
 *
 * Min-width only is not a limitation — `breakpoints.ts` states it as a design decision, so a
 * max-width query would be a departure from that rule, and this is not the place to quietly
 * accommodate one.
 */
export function compareMinWidthMediaQueries(a: string, b: string): number {
  const widthA = MIN_WIDTH.exec(a);
  const widthB = MIN_WIDTH.exec(b);

  if (widthA && widthB) {
    return Number(widthA[1]) - Number(widthB[1]);
  }

  // One of them has a width and the other does not: the classes are separated here, never compared
  // on their text, which is precisely what would let a cycle form.
  if (widthA) return 1;
  if (widthB) return -1;

  return a < b ? -1 : a > b ? 1 : 0;
}
