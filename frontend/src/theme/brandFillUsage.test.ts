import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * A SOURCE scan, and the reason it exists is that my own check was not one.
 *
 * The brand-fill split migrated eight `backgroundColor` sites off the ramp. To confirm none were
 * missed I grepped for `':hover'` and `colors.brand[40]` — on the SAME LINE. Every real occurrence
 * spans several lines, so the grep reported "none surviving" while `ConfirmDialog` still hovered to
 * the pressed shade, and the PR body went out claiming all four were done. A reviewer found it.
 *
 * The invariant is about CALL SITES, not rendered values, so no render test can hold it: an
 * interactive brand surface must take every state from `brandFill`. The ramp shades that used to
 * play those states — 40 (pressed), 50 and 70 (hover) — must not appear as an interactive
 * `backgroundColor` anywhere. They remain perfectly valid as text, strokes and static tints, which
 * is why the check is scoped to hover/active blocks rather than to the shades themselves.
 */

// Plain relative, like the other file-reading tests here: Vitest runs from the frontend root. An
// `import.meta.url` round trip produced `C:\src` on Windows — which the vacuity guard below caught
// on the first run, rather than letting the scan pass over nothing.
const SRC = 'src';

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return sourceFiles(path);
    if (!/\.tsx?$/.test(entry.name) || entry.name.includes('.test.')) return [];
    return [path];
  });
}

/** `':hover': { … }` / `':active': { … }` — non-greedy to the first closing brace. */
const INTERACTIVE_BLOCK = /':(?:hover|active)':\s*\{[^}]*\}/g;
const RAMP_STATE_SHADE = /backgroundColor:\s*colors\.brand\[(?:40|50|70)\]/;

/**
 * A style object that paints a brand fill, from its `rest` to the closing brace of its outermost
 * block. Greedy to the two-space `},` that ends a `makeStyles` entry, which is the shape every
 * style object in this codebase has.
 */
const BRAND_FILL_BLOCK = /backgroundColor:\s*colors\.brandFill\.rest[\s\S]*?\n {2}\},/g;

describe('brand fill adoption', () => {
  it('takes every interactive state from brandFill, never from the ramp', () => {
    const offenders = sourceFiles(SRC).flatMap((file) => {
      const blocks = readFileSync(file, 'utf8').match(INTERACTIVE_BLOCK) ?? [];
      return blocks
        .filter((block) => RAMP_STATE_SHADE.test(block))
        .map((block) => `${file.slice(file.indexOf('src'))}: ${block.replace(/\s+/g, ' ')}`);
    });

    // Named rather than counted: a failure should say WHICH site regressed, since the whole point
    // is that the last one was invisible to a search that returned a number.
    expect(offenders).toEqual([]);
  });

  /**
   * Declaring hover obliges you to declare pressed.
   *
   * Keyed on HOVER rather than on the fill, and that distinction is load-bearing: two of the eight
   * `brandFill.rest` sites are not controls at all — the recipient avatar on the transfer page and
   * the disc inside the bottom-nav tab. A rule that said "every brand fill needs three states"
   * would have put a hover on an avatar. The presence of `:hover` is what marks a surface as
   * interactive, so that is what the rule keys on.
   *
   * The gap this closes was real and wider than it looked: seven of eight sites were incomplete,
   * and `Sidebar` was the only control with all three. Hover was unified in this PR because three
   * shades for one state is drift; one control with a pressed state and five without is the same
   * drift, and the argument that stopped at hover was inconsistent.
   */
  it('gives every interactive brand fill a pressed state, not just a hover', () => {
    const offenders = sourceFiles(SRC).flatMap((file) => {
      const source = readFileSync(file, 'utf8');
      const blocks = source.match(BRAND_FILL_BLOCK) ?? [];
      return blocks
        .filter(
          (block) => block.includes('brandFill.hover') && !block.includes('brandFill.pressed'),
        )
        .map(
          (block) =>
            `${file.slice(file.indexOf('src'))}: ${block.slice(0, 60).replace(/\s+/g, ' ')}…`,
        );
    });

    expect(offenders).toEqual([]);
  });

  it('finds files at all, so an empty pass cannot come from an empty scan', () => {
    // The guard the assertion above needs: a broken path would make it vacuously green, which is
    // the same failure mode as the grep it replaces.
    expect(sourceFiles(SRC).length).toBeGreaterThan(50);
  });
});
