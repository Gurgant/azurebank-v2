/**
 * The seam that makes the palette theme-aware without touching a single consumer.
 *
 * **Why variables rather than a React context.** Twenty-five files import `colors`, seventeen
 * `surfaces`, and none of them should have to know a theme exists — a context would mean editing all
 * of them and re-rendering the tree on every switch. More decisively, a context cannot exist before
 * React does: the theme has to be decided BEFORE the first paint or the user sees a flash of the
 * wrong one, and only a CSS custom property set on `<html>` can be in place that early.
 *
 * **Why the literals stay.** The token objects below become `var(--ab-…)` references, which is
 * exactly what a stylesheet wants and exactly what a CONTRAST TEST cannot use: `surfaces.test.ts`
 * reads these values as hex and computes luminance, and `luminance('var(--ab-surface-canvas)')` is
 * `NaN` — an assertion that passes by accident. So the literal palettes remain exported, the CSS is
 * DERIVED from them by the same traversal that names the variables, and the contrast rules that
 * guarded light now guard dark too.
 *
 * One traversal produces the names, the references and the CSS, so the three cannot drift apart:
 * there is no second list to keep in step.
 */

/** A palette is a tree of colour strings, nested however the palette finds it useful. */
export type PaletteTree = { readonly [key: string]: string | PaletteTree };

/**
 * The same tree with every leaf widened to `string`.
 *
 * Two callers need exactly this and for the same reason. `toVarRefs` returns it because a `var()`
 * reference is a string, not the literal hex it replaces; and `LightPalette` is it because `as const`
 * would otherwise demand that the dark palette repeat the light hexes to satisfy the type. Widened,
 * the type still enforces the one thing worth enforcing: the SAME KEYS, so a forgotten dark value is
 * a compile error rather than an undefined custom property found by looking at the screen.
 */
export type WidenedTree<T> = {
  readonly [K in keyof T]: T[K] extends string ? string : WidenedTree<T[K]>;
};

/**
 * `['colors','brand','60']` → `--ab-colors-brand-60`.
 *
 * The `ab-` prefix is not decoration: custom properties live in one flat global namespace shared
 * with every library on the page, and `--surface-canvas` is a name someone else may already own.
 */
export function cssVarName(path: readonly string[]): string {
  return `--ab-${path.join('-')}`;
}

/** Replace every leaf with `var(--ab-…)`, preserving the shape so call sites read identically. */
export function toVarRefs<T extends PaletteTree>(
  tree: T,
  prefix: readonly string[] = [],
): WidenedTree<T> {
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(tree)) {
    const path = [...prefix, key];
    out[key] =
      typeof value === 'string'
        ? `var(${cssVarName(path)})`
        : toVarRefs(value as PaletteTree, path);
  }
  return out as WidenedTree<T>;
}

/** Flatten a palette to the `--ab-… : value` pairs it declares, in traversal order. */
export function toDeclarations(
  tree: PaletteTree,
  prefix: readonly string[] = [],
): [name: string, value: string][] {
  return Object.entries(tree).flatMap(([key, value]) => {
    const path = [...prefix, key];
    return typeof value === 'string'
      ? [[cssVarName(path), value] as [string, string]]
      : toDeclarations(value as PaletteTree, path);
  });
}

/**
 * Render one selector's block.
 *
 * Emitted rather than hand-written so `index.css` can be checked in — a static stylesheet is what
 * lets the variables exist before the first frame — while still having exactly one source of truth.
 * A test compares the file against this output, so editing either alone fails.
 */
export function toCssBlock(selector: string, tree: PaletteTree): string {
  const body = toDeclarations(tree)
    .map(([name, value]) => `  ${name}: ${lowercaseHex(value)};`)
    .join('\n');
  return `${selector} {\n${body}\n}`;
}

/**
 * Prettier lowercases hex in CSS, and this function's output is checked in.
 *
 * Emitting uppercase would put the formatter and the drift test in permanent disagreement: prettier
 * rewrites the block, the test then finds the file no longer matches the generator, and whichever
 * ran last wins. Lowercasing HERE keeps prettier authoritative over the whole stylesheet while the
 * palettes keep the casing their own comments quote.
 */
function lowercaseHex(value: string): string {
  return value.replace(/#[0-9a-fA-F]{3,8}\b/g, (hex) => hex.toLowerCase());
}
