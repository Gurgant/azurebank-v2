import { createElement } from 'react';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { PinInput } from '../components/PinInput';
import { Avatar } from '../components/shared/Avatar';
import { useTransferWizardStyles } from '../pages/transferWizardStyles';
import { toDeclarations } from './cssVariables';
import { lightPalette, type LightPalette } from './tokens';
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
    // 4.5:1, because most of the call sites that take this as `color:` are TEXT — links, active
    // nav labels, the "See all" affordance. An earlier draft asserted 3:1, the non-text
    // threshold, which is right for the icons that also use it and wrong for the majority.
    //
    // Against the CARD rather than the canvas, and that distinction is measured rather than
    // assumed: on the canvas the LIGHT value is 4.42:1, below AA, and on the brand tint it is
    // 4.23:1. Words drawn on those grounds take another shade, and the tests at the end of this
    // file read which one from the components' own style sheets.
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

  /**
   * The card is not the only ground the secondary grey is drawn on, nor the hardest. It is drawn
   * on the canvas, on the wells and the chips, on the hairline grey under a hovered chip, on the
   * brand tints, as on a chosen card, and on the green of a found recipient. In the light theme
   * every one of them is darker than the card.
   */
  it.each([
    ['the canvas', surfaces.canvas],
    ['neutral[50]', colors.neutral[50]],
    ['neutral[100]', colors.neutral[100]],
    ['neutral[200]', colors.neutral[200]],
    ['brand[120]', colors.brand[120]],
    ['brand[130]', colors.brand[130]],
    ['semantic.success.light', colors.semantic.success.light],
  ])('prints secondary text on %s at 4.5:1 or better', (ground, hex) => {
    const ratio = contrast(colors.neutral[500], hex);
    expect(ratio, `neutral[500] on ${ground}: ${ratio.toFixed(2)}`).toBeGreaterThanOrEqual(4.5);
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
/**
 * Brand words as the components draw them, held on the ground each one sits on, in both themes.
 *
 * A pair of tokens cannot see a component go back to `brand[60]`, so these read the shade from
 * the style sheets the components write. jsdom draws nothing, and its `getComputedStyle` does not
 * follow these classes, so the reading is the one `disabledButtonEdge.test.tsx` sets out: of the
 * rules that name one of the element's classes, in the state asked for, and set `color`, the last
 * in the page. It gives a `var(--ab-…)` reference, and each palette's declarations give its hex.
 */
function sheetRules(): CSSStyleRule[] {
  return Array.from(document.querySelectorAll('style'))
    .flatMap((element) => Array.from(element.sheet?.cssRules ?? []))
    .filter((rule): rule is CSSStyleRule => 'selectorText' in rule);
}

/** The variable the sheets draw `element`'s words with in `state` (`':hover'`), or `undefined`. */
function shadeOf(element: Element, state = ''): string | undefined {
  const selectors = new Set(Array.from(element.classList).map((name) => `.${name}${state}`));
  const value = sheetRules()
    .filter(
      (rule) =>
        rule.style.getPropertyValue('color') !== '' &&
        rule.selectorText.split(',').some((selector) => selectors.has(selector.trim())),
    )
    .map((rule) => rule.style.getPropertyValue('color'))
    .at(-1);
  return /^var\((--ab-[\w-]+)\)$/.exec(value ?? '')?.[1];
}

/** The transfer pages' link to each other, with the rule both pages give it. */
function TransferLink() {
  const styles = useTransferWizardStyles();
  return createElement('button', { className: styles.linkBtn }, 'Between your own accounts');
}

function showPin(): HTMLElement {
  render(createElement(PinInput, { value: '', onChange: () => {} }));
  return screen.getByRole('button', { name: 'Show PIN' });
}

const BRAND_WORDS = [
  {
    words: "the avatar's initials",
    draw: () => {
      render(createElement(Avatar, { initials: 'AL' }));
      return screen.getByRole('img', { name: 'AL' });
    },
    state: '',
    grounds: (palette: LightPalette) => ({ 'brand[120]': palette.colors.brand[120] }),
  },
  {
    words: "the transfer pages' link to each other",
    draw: () => {
      render(createElement(TransferLink));
      return screen.getByRole('button', { name: 'Between your own accounts' });
    },
    state: '',
    grounds: (palette: LightPalette) => ({ 'the canvas': palette.surfaces.canvas }),
  },
  {
    // On the canvas on a transfer page, and on the card in a money dialog.
    words: 'Show/Hide under the PIN boxes, under the pointer',
    draw: showPin,
    state: ':hover',
    grounds: (palette: LightPalette, card: string) => ({
      'the canvas': palette.surfaces.canvas,
      'the card': card,
    }),
  },
];

describe('brand words as the components draw them', () => {
  it('reads the sheets as a browser does: Show/Hide at rest is the grey its rule declares', () => {
    // CONTROL: green before this change. If the reading found nothing, or the wrong rule, the
    // tests below could not be believed.
    expect(shadeOf(showPin())).toBe('--ab-colors-neutral-500');
  });

  describe.each([
    ['light', lightPalette, CHROME.light],
    ['dark', darkPalette, CHROME.dark],
  ] as const)('%s', (_name, palette, card) => {
    const hexOf = new Map(toDeclarations(palette));

    it.each(BRAND_WORDS)('prints $words at 4.5:1 or better', ({ draw, state, grounds }) => {
      const shade = shadeOf(draw(), state);
      const hex = hexOf.get(shade ?? '') ?? '';
      expect(hex, `the shade the sheets give: ${shade}`).toMatch(/^#[0-9A-F]{6}$/i);
      for (const [ground, background] of Object.entries(grounds(palette, card))) {
        const ratio = contrast(hex, background);
        expect(ratio, `${shade} on ${ground}: ${ratio.toFixed(2)}`).toBeGreaterThanOrEqual(4.5);
      }
    });
  });
});

describe('the light-theme pairing this replaced', () => {
  it('was a border the colour of what it borders', () => {
    expect(contrast(lightPalette.colors.neutral[100], CHROME.light)).toBeLessThan(1.2);
    expect(contrast(lightPalette.colors.neutral[100], lightPalette.surfaces.canvas)).toBeCloseTo(
      1,
      5,
    );
  });
});
