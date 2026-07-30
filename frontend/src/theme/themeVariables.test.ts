import { describe, expect, it } from 'vitest';
import { colors, gradients, lightPalette, shadows, surfaces } from './tokens';
import { darkPalette } from './darkPalette';
import { toCssBlock, toDeclarations } from './cssVariables';
import { readFileSync } from 'node:fs';

/**
 * The seam has three faces — the names, the `var()` references components import, and the checked-in
 * CSS — and only the first two are produced by the same traversal. The stylesheet is a FILE, written
 * once and then living on its own, which is exactly the kind of copy that goes stale silently: a new
 * token would compile, render as an empty custom property, and paint nothing. Browsers do not warn
 * about an undefined `var()`; they just draw the fallback, and the fallback here is "no colour".
 *
 * So the file is asserted against the generator rather than trusted. Every failure mode this can
 * have is one of: a palette gained a value the CSS lacks, the CSS was hand-edited, or the two themes
 * stopped declaring the same names.
 */

// Read from disk rather than imported: Vitest stubs `.css` imports, so `?raw` hands back an empty
// string and every assertion below would pass against nothing at all.
const INDEX_CSS = readFileSync('src/index.css', 'utf8');

describe('theme variables', () => {
  it('is checked in exactly as the palettes generate it', () => {
    // Not "contains the right values" — byte-identical blocks. A looser assertion would pass against
    // a stylesheet that had gained a stray declaration nothing generates.
    expect(INDEX_CSS).toContain(toCssBlock(':root', lightPalette));
    expect(INDEX_CSS).toContain(toCssBlock(":root[data-theme='dark']", darkPalette));
  });

  it('declares the same names in both themes, so no surface is themed in only one', () => {
    const light = toDeclarations(lightPalette).map(([name]) => name);
    const dark = toDeclarations(darkPalette).map(([name]) => name);
    // Sorted rather than order-sensitive: the traversal order is a property of the palette's shape,
    // and the claim here is about coverage.
    expect([...dark].sort()).toEqual([...light].sort());
    expect(light.length).toBeGreaterThan(60);
  });

  it('resolves every reference components import to a declaration that exists', () => {
    // The end-to-end property, and the only one that catches a typo in `toVarRefs`'s prefix: walk
    // what the app actually uses, not what the palette happens to contain.
    const declared = new Set(toDeclarations(lightPalette).map(([name]) => name));
    const referenced = JSON.stringify([colors, surfaces, shadows, gradients])
      .match(/--ab-[a-zA-Z0-9-]+/g)!
      .map((name) => name);

    expect(referenced.length).toBeGreaterThan(60);
    expect(referenced.filter((name) => !declared.has(name))).toEqual([]);
  });

  it('leaves every value in the stylesheet, and none in the imported objects', () => {
    // The direction of the refactor, asserted so it cannot quietly reverse. A component that reads
    // `colors.brand[60]` must receive a reference; the moment one of these is a literal hex again,
    // that value has stopped being theme-aware and will stay light in dark mode.
    expect(colors.brand[60]).toBe('var(--ab-colors-brand-60)');
    expect(surfaces.canvas).toBe('var(--ab-surfaces-canvas)');
    expect(shadows.md).toBe('var(--ab-shadows-md)');
    expect(gradients.brand).toBe('var(--ab-gradients-brand)');
    expect(JSON.stringify([colors, surfaces, shadows, gradients])).not.toMatch(/#[0-9A-Fa-f]{6}/);
  });
});
