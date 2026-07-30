import { describe, expect, it } from 'vitest';
import { lightPalette } from './tokens';
import { darkPalette } from './darkPalette';

/**
 * One rule, and it is the rule that actually broke: **a seam must be distinguishable from both
 * surfaces it separates.**
 *
 * The sidebar's right border was `neutral[100]` on a white sidebar, which measures 1.10:1 — not a
 * boundary anyone can see. Moving the canvas onto that same `neutral[100]` would have made the
 * other side 1.00:1: the border would have become literally invisible, in the change meant to fix
 * visibility. Nothing in the codebase could have caught that, because a border and the thing it
 * borders being the same colour is perfectly valid CSS.
 *
 * The thresholds below are deliberately modest. This is a hairline between two near-white
 * surfaces, not text, and WCAG's 3:1 for non-text contrast applies to controls you must identify
 * to operate — not to a decorative rule whose job is done mostly by the fill change either side of
 * it. Asserting 3:1 here would be a number chosen to sound rigorous and would force a heavy grey
 * line no banking product uses. What these numbers do assert is that the seam has NOT collapsed
 * into either neighbour, which is the failure that happened.
 */

/**
 * The card each theme's canvas sits beside — Fluent's `colorNeutralBackground1`, measured from the
 * two themes rather than assumed: white in light, `#292929` in dark.
 */
const CHROME = { light: '#FFFFFF', dark: '#292929' } as const;
const WHITE = CHROME.light;

/** WCAG 2.1 relative luminance. */
function luminance(hex: string): number {
  const channels = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

describe.each([
  ['light', lightPalette, CHROME.light],
  ['dark', darkPalette, CHROME.dark],
] as const)('surface tokens — %s', (_name, palette, chrome) => {
  const { colors, surfaces } = palette;

  it('reproduces a known ratio, so the helper itself is trustworthy', () => {
    // Black on white is 21:1 by definition. Without this the two assertions below could both pass
    // against a broken formula.
    expect(contrast('#000000', WHITE)).toBeCloseTo(21, 5);
    expect(contrast(WHITE, WHITE)).toBeCloseTo(1, 5);
  });

  it('separates the canvas from the chrome it sits beside', () => {
    expect(contrast(surfaces.canvas, chrome)).toBeGreaterThan(1.05);
  });

  it('keeps the seam visible against BOTH surfaces it separates', () => {
    expect(contrast(surfaces.border, chrome)).toBeGreaterThan(1.2);
    expect(contrast(surfaces.border, surfaces.canvas)).toBeGreaterThan(1.1);
  });

  /**
   * The relationship U2 fought for, and the one a naive dark theme inverts: the ground is DARKER
   * than the card in light, so in dark it must be darker still — not lighter. Stated as a direction
   * relative to the chrome rather than as an absolute luminance, which is what makes it one rule
   * both themes can be held to.
   */
  it('keeps the canvas on the far side of the chrome from the wells recessed into it', () => {
    const groundIsBelow = luminance(surfaces.canvas) < luminance(chrome);
    expect(groundIsBelow).toBe(true);
    // A well is INSET: it belongs strictly between the ground and the card, on the card's side of
    // the ground but never past the card itself. Only the lower half of this was asserted at first,
    // and the dark palette duly shipped a well LIGHTER than its card — a recess that reads as a
    // bump. One-sided bounds are how a mirrored palette passes a test written for one direction.
    const well = luminance(colors.neutral[50]);
    const ground = luminance(surfaces.canvas);
    const raised = luminance(chrome);
    expect(Math.min(ground, raised)).toBeLessThan(well);
    expect(well).toBeLessThan(Math.max(ground, raised));
  });

  /**
   * The test that was missing, and the shape of the hole it left.
   *
   * Every contrast assertion here asked about text on a CARD. Nothing asked about text on a BRAND
   * FILL — so `brand[60]` could be given a dark value tuned to read AS text (5.73:1 on the canvas)
   * while white sat on it at 3.22:1, and the suite stayed green. A real browser showed it on the
   * dashboard's Transfer banner.
   *
   * `colorNeutralForegroundOnBrand` is `#ffffff` in BOTH Fluent themes — measured against the live
   * library, not assumed — so white is the fixed side of this ratio and the fill is the side that
   * has to move.
   */
  it.each(['rest', 'hover', 'pressed'] as const)(
    'carries white on the brand fill at %s, at 4.5:1 or better',
    (state) => {
      expect(contrast(WHITE, colors.brandFill[state])).toBeGreaterThanOrEqual(4.5);
    },
  );

  /**
   * The other half of the split, and the reason one token could not do both: the fill has to be
   * dark enough for white, while the ramp has to be light enough to READ on the ground. Asserting
   * both directions is what makes the separation load-bearing rather than cosmetic.
   */
  /**
   * Contrast with white is not the only thing a fill owes.
   *
   * A first attempt to falsify the split moved the dark hover DARKER — the light-mode direction —
   * and every assertion stayed green, because white reads fine on a darker blue. The mutation was
   * expressing the wrong property: the failure there is not legibility, it is that hover stops
   * being VISIBLE AS a state change. A hover indistinguishable from rest is a dead affordance, and
   * nothing else in this suite would notice.
   */
  it.each(['hover', 'pressed'] as const)(
    'makes %s perceptibly different from rest, so the state reads as a state',
    (state) => {
      expect(contrast(colors.brandFill[state], colors.brandFill.rest)).toBeGreaterThan(1.15);
    },
  );

  it('keeps the brand READABLE as text on the card, which is the ramp’s job', () => {
    // 4.5:1, because 26 call sites use this as `color:` on TEXT — links, active nav labels, the
    // "See all" affordance. An earlier draft asserted 3:1, the non-text threshold, which is right
    // for the icons that also use it and wrong for the majority.
    //
    // Against the CARD rather than the canvas, and that distinction is measured rather than
    // assumed: brand text lives inside cards, and on the canvas the LIGHT value is 4.42:1 — below
    // AA. Asserting against the canvas would fail today, which is the useful thing to know: if
    // brand-coloured text is ever placed directly on the ground, it needs its own value. No such
    // placement was found; finding one is U8 work, not a blind fix here.
    expect(contrast(colors.brand[60], chrome)).toBeGreaterThanOrEqual(4.5);
  });
  /** Body text on the card it is printed on. WCAG 1.4.3 AA for normal text. */
  it('prints primary text on the card at 4.5:1 or better', () => {
    expect(contrast(colors.neutral[700], chrome)).toBeGreaterThanOrEqual(4.5);
  });

  /** Secondary text is still text: same threshold, and the one most often quietly failed. */
  it('prints secondary text on the card at 4.5:1 or better', () => {
    expect(contrast(colors.neutral[500], chrome)).toBeGreaterThanOrEqual(4.5);
  });

  /** Every status chip: pale-on-deep in dark, deep-on-pale in light — the ratio is what must hold. */
  it.each(['deposit', 'withdrawal', 'transferOut', 'transferIn'] as const)(
    'prints the %s chip at 4.5:1 on its own background',
    (kind) => {
      const chip = colors.transaction[kind];
      expect(contrast(chip.foreground, chip.background)).toBeGreaterThanOrEqual(4.5);
    },
  );
});

/**
 * Deliberately NOT run over both palettes.
 *
 * This records a specific defect in the LIGHT theme — `neutral[100]` was the border and also the
 * value the canvas was about to take, so the two would have measured 1.00:1 — and "under 1.2:1" is
 * the evidence it was invisible, not a rule any theme should satisfy. Held against dark it fails at
 * 1.27:1, which is the palette being CORRECT: there `neutral[100]` is the canvas and the card stands
 * a visible step above it. A historical fact parameterised over a second theme stops being a fact.
 */
describe('the light-theme pairing this replaced', () => {
  it('was a border the colour of what it borders', () => {
    expect(contrast(lightPalette.colors.neutral[100], CHROME.light)).toBeLessThan(1.2);
    expect(contrast(lightPalette.colors.neutral[100], lightPalette.surfaces.canvas)).toBeCloseTo(
      1,
      5,
    );
  });
});
