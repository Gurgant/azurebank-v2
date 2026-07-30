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

/**
 * Balanced-brace extraction, and it replaces a regex that was quietly wrong.
 *
 * The first version matched `\{[^}]*\}` — everything up to the FIRST closing brace. That is not a
 * style block whenever one contains a nested brace, and one does today:
 *
 *     ':hover': {
 *       border: `2px dashed ${colors.brand[60]}`,
 *       backgroundColor: colors.brand[140],   // ← past the `}` of the interpolation
 *     }
 *
 * The scan stopped at the template literal and never saw the `backgroundColor` line. Had that value
 * been a ramp STATE shade, the test would have passed while the drift stood — the same "reported a
 * clean number without looking" failure this file was written to end, one layer down. Counting
 * blocks cannot detect it either, since a truncated match is still a match; only reading to the
 * matching brace does.
 */
function blocksAfter(source: string, anchor: RegExp): string[] {
  return [...source.matchAll(anchor)].map((match) => {
    let index = match.index + match[0].length;
    let depth = 1;
    while (index < source.length && depth > 0) {
      if (source[index] === '{') depth += 1;
      else if (source[index] === '}') depth -= 1;
      index += 1;
    }
    return source.slice(match.index, index);
  });
}

/** `':hover': {` / `':active': {` — the opening brace only; `blocksAfter` finds its partner. */
const INTERACTIVE_ANCHOR = /':(?:hover|active)':\s*\{/g;
const RAMP_STATE_SHADE = /backgroundColor:\s*colors\.brand\[(?:40|50|70)\]/;

/** A `makeStyles` entry: a name at two-space indent, which is the shape every style object has. */
const STYLE_ENTRY_ANCHOR = /\n {2}\w+: \{/g;

// Two objects for one pattern, because a `/g` regex carries `lastIndex` between `.test()` calls and
// would answer true, false, true down the list — a self-inflicted version of this file's own theme.
const BRAND_FILL_REST = /backgroundColor:\s*colors\.brandFill\.rest/;
const BRAND_FILL_REST_ALL = new RegExp(BRAND_FILL_REST.source, 'g');

function label(file: string, block: string): string {
  return `${file.slice(file.indexOf('src'))}: ${block.slice(0, 70).replace(/\s+/g, ' ')}…`;
}

describe('brand fill adoption', () => {
  it('reads a style block to its matching brace, not to the first one', () => {
    // The falsifier for the bug above, kept as a fixture rather than pointed at a real file: the
    // corpus can lose its only nested block without anyone noticing, and then the guarantee would
    // silently stop being tested.
    const [block] = blocksAfter(
      "  a: {\n    ':hover': {\n      border: `1px ${x.y}`,\n      backgroundColor: colors.brand[50],\n    },\n  },",
      INTERACTIVE_ANCHOR,
    );

    expect(RAMP_STATE_SHADE.test(block)).toBe(true);
  });

  it('takes every interactive state from brandFill, never from the ramp', () => {
    const blocks = sourceFiles(SRC).flatMap((file) =>
      blocksAfter(readFileSync(file, 'utf8'), INTERACTIVE_ANCHOR).map((block) => ({ file, block })),
    );

    // Non-vacuity, first: a scan that matched nothing would report no offenders just as loudly as a
    // clean one. There are 43 today; the floor only has to prove the extractor still finds blocks.
    expect(blocks.length).toBeGreaterThan(20);

    // Named rather than counted: a failure should say WHICH site regressed, since the whole point
    // is that the last one was invisible to a search that returned a number.
    expect(
      blocks.filter(({ block }) => RAMP_STATE_SHADE.test(block)).map((b) => label(b.file, b.block)),
    ).toEqual([]);
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
    let restSites = 0;
    const fills = sourceFiles(SRC).flatMap((file) => {
      const source = readFileSync(file, 'utf8');
      restSites += (source.match(BRAND_FILL_REST_ALL) ?? []).length;
      return blocksAfter(source, STYLE_ENTRY_ANCHOR)
        .filter((block) => BRAND_FILL_REST.test(block))
        .map((block) => ({ file, block }));
    });

    // Tighter than "found something", and deliberately so: extraction that captured six of eight
    // entries would pass a `> 0` guard while two surfaces went unchecked. Every `brandFill.rest` in
    // the tree must land inside exactly one extracted entry, which stays true as sites are added.
    expect(fills.length).toBe(restSites);

    expect(
      fills
        .filter(
          ({ block }) => block.includes('brandFill.hover') && !block.includes('brandFill.pressed'),
        )
        .map(({ file, block }) => label(file, block)),
    ).toEqual([]);
  });

  it('finds files at all, so an empty pass cannot come from an empty scan', () => {
    // The guard the assertions above need: a broken path would make them vacuously green, which is
    // the same failure mode as the grep this file replaces.
    expect(sourceFiles(SRC).length).toBeGreaterThan(50);
  });
});
