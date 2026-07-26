import { describe, expect, it } from 'vitest';
import { colors, surfaces } from './tokens';

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

const WHITE = '#FFFFFF';

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

describe('surface tokens', () => {
  it('reproduces a known ratio, so the helper itself is trustworthy', () => {
    // Black on white is 21:1 by definition. Without this the two assertions below could both pass
    // against a broken formula.
    expect(contrast('#000000', WHITE)).toBeCloseTo(21, 5);
    expect(contrast(WHITE, WHITE)).toBeCloseTo(1, 5);
  });

  it('separates the canvas from the chrome it sits beside', () => {
    // Chrome is Fluent's colorNeutralBackground1 — white in the light theme.
    expect(contrast(surfaces.canvas, WHITE)).toBeGreaterThan(1.05);
  });

  it('keeps the seam visible against BOTH surfaces it separates', () => {
    expect(contrast(surfaces.border, WHITE)).toBeGreaterThan(1.2);
    expect(contrast(surfaces.border, surfaces.canvas)).toBeGreaterThan(1.1);
  });

  it('rejects the arrangement that shipped: a border the colour of what it borders', () => {
    // The old pairing, asserted directly rather than described. `neutral[100]` was the border AND
    // the value the canvas was about to take.
    expect(contrast(colors.neutral[100], WHITE)).toBeLessThan(1.2);
    expect(contrast(colors.neutral[100], surfaces.canvas)).toBeCloseTo(1, 5);
  });

  it('keeps the canvas darker than the wells recessed into the cards above it', () => {
    // A well inside a raised card must stay lighter than the ground that card stands on, or the
    // card reads as perforated rather than raised. `neutral[50]` keeps that role.
    expect(luminance(colors.neutral[50])).toBeGreaterThan(luminance(surfaces.canvas));
  });
});
