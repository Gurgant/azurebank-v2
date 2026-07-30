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
 * Ascending by min-width; everything else keeps Griffel's own ordering.
 *
 * The fallback is deliberate and is the reason this is a comparator rather than a numeric sort.
 * The media bucket is shared with Fluent, which emits `(forced-colors: active)` and
 * `screen and (prefers-reduced-motion: reduce)` into it. Those have no width to compare and no
 * relationship to ours, so imposing an order on them would be inventing a cascade for rules whose
 * current order is already correct. Delegating to the default keeps this change scoped to the one
 * thing that is provably wrong.
 *
 * Min-width only is not a limitation here — `breakpoints.ts` states it as a design decision, so a
 * max-width query would be a departure from that rule, and this comparator is not the place to
 * quietly accommodate one.
 */
export function compareMinWidthMediaQueries(a: string, b: string): number {
  const widthA = MIN_WIDTH.exec(a);
  const widthB = MIN_WIDTH.exec(b);

  if (widthA && widthB) {
    return Number(widthA[1]) - Number(widthB[1]);
  }

  return a < b ? -1 : a > b ? 1 : 0;
}
