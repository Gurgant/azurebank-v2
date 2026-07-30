import type { LightPalette } from './tokens';

/**
 * The dark palette — derived by rule, not picked by eye.
 *
 * Every value here answers one question: **what plays this role when the ground is dark?** Roles, not
 * numbers: `neutral[700]` is "primary text", and its dark counterpart is whatever reads as primary
 * text on a dark card, not `neutral[200]` because 700 mirrors to 200 on a ten-step scale.
 *
 * The anchors are **Fluent's own dark theme**, measured from `azureBankDarkTheme` rather than
 * guessed, so the palette and the component library agree instead of fighting each other one control
 * at a time. The ones that mattered:
 *
 * | Fluent dark token              | value     | what it anchors here          |
 * | ------------------------------ | --------- | ----------------------------- |
 * | `colorNeutralBackground3`      | `#141414` | the canvas, BELOW the card    |
 * | `colorNeutralBackground1`      | `#292929` | the card itself               |
 * | `colorNeutralStroke3`          | `#3D3D3D` | the hairline between surfaces |
 * | `colorNeutralForeground2`      | `#D6D6D6` | primary text                  |
 * | `colorNeutralForeground3`      | `#ADADAD` | secondary text                |
 * | `colorNeutralForeground4`      | `#999999` | placeholder                   |
 * | `colorBrandForeground1`        | `#4C95D1` | the brand where it must READ  |
 * | `colorBrandBackground`         | `#0068A1` | the brand where it must FILL  |
 *
 * The relationship that had to survive is the one U2 fought for: **the canvas sits below the card.**
 * In light that is `#F3F4F6` under `#FFFFFF`; here it is `#141414` under `#292929`. A dark theme that
 * inverts naively gets this backwards — it makes the ground lighter than what stands on it — and the
 * result is the flat look the light theme already had to be rescued from.
 *
 * **What is deliberately NOT claimed.** These values are contrast-checked, not art-directed. The
 * numbers guarantee text is readable and that no seam has collapsed into its neighbours; they do not
 * guarantee the result is handsome. That judgement needs a human in a real browser, and U8 is where
 * it belongs.
 */
export const darkPalette: LightPalette = {
  colors: {
    /**
     * The brand ramp REVERSES at the ends and holds in the middle.
     *
     * In light, 10–30 are deep blues that carry text on pale tints, and 110–160 are near-white tints
     * that act as surfaces. On a dark ground both jobs swap hands: text must be pale, surfaces must
     * be deep. The middle (60–70) stays recognisably the brand, because a brand that changes hue with
     * the theme is not a brand — it moves only far enough to hold contrast, following Fluent's own
     * dark brand tokens.
     */
    brand: {
      10: '#EAF4FB', // was the deepest — now the palest, because it carries text
      20: '#D5E9F7',
      30: '#BEDCF2',
      40: '#9FCAEA', // pressed, seen against a dark fill
      50: '#7DB6E0',
      60: '#4C95D1', // the brand where it must READ — Fluent's colorBrandForeground1
      70: '#67A4DB', // hover: lighter, matching light mode's direction of travel
      80: '#2E7FBF',
      90: '#1B6CA8',
      100: '#0F5B90',
      110: '#0B4B78', // the deep end is now the surface end
      120: '#083D63',
      130: '#06304F',
      140: '#04243C',
      150: '#03192A',
      160: '#021019',
    },
    /**
     * Taken straight from Fluent's dark `colorBrandBackground`, which is what a brand FILL is for.
     * White on `#0068A1` measures 6.01:1; white on the ramp's `#4C95D1` measures 3.22:1 — which is
     * what shipped, and what a real browser showed on the dashboard's Transfer banner. `hover` goes
     * LIGHTER here, the direction that reads on a dark ground, and white still clears AA at 4.87:1.
     */
    brandFill: {
      rest: '#0068A1',
      hover: '#0077B6',
      pressed: '#004C77',
    },
    // Identical to light, and that is the finding rather than an oversight: both grounds it sits on
    // are the same in both themes — `gradients.brand` is byte-identical, and `brandFill.rest` moves
    // only from #0077B6 to #0068A1. A white veil over the same blue needs no second value. It is
    // declared anyway because the palette's contract is that every name exists in both themes, and
    // that contract is what makes a missing dark value a compile error rather than a dark screen.
    onBrandPlate: 'rgba(255, 255, 255, 0.15)',
    /**
     * Neutrals are anchored to Fluent's dark foregrounds and strokes, so a heading in our palette and
     * a Fluent control's label are the same grey rather than two greys that nearly match.
     */
    neutral: {
      // A well is INSET, so it belongs between the ground and the card — in light that is #F9FAFB
      // between #F3F4F6 and #FFFFFF. The first draft here read "lighter than the card, as it was
      // darker in light" and put it at #333333, ABOVE the card: a well that reads as raised, which
      // is the one thing it must not do. Fluent's dark colorNeutralBackground2 is exactly the step
      // wanted. Now asserted from both sides.
      50: '#1F1F1F',
      100: '#141414', // the canvas itself, mirroring light's neutral[100] === surfaces.canvas
      200: '#3D3D3D', // border light — colorNeutralStroke3
      300: '#525252', // border default — colorNeutralStroke2
      400: '#999999', // placeholder — colorNeutralForeground4
      500: '#ADADAD', // secondary text — colorNeutralForeground3
      600: '#C2C2C2',
      700: '#D6D6D6', // primary text — colorNeutralForeground2
      800: '#EBEBEB', // heading
      900: '#FFFFFF', // the lightest, where light had the darkest
    },
    /**
     * Semantic hues keep their identity and swap their light/dark roles: `light` was a pale wash
     * behind a badge and is now a deep wash; `dark` was the readable text colour on that wash and is
     * now a pale one. `main` moves least — it is the icon colour, and an icon reads on both grounds.
     */
    semantic: {
      success: { light: '#12301C', main: '#4CB86A', dark: '#8FE0A6' },
      error: { light: '#3A1512', main: '#F2685C', dark: '#FFA9A1' },
      warning: { light: '#332008', main: '#F5A623', dark: '#FFD08A' },
      info: { light: '#0A2A3D', main: '#38B6F0', dark: '#8FD7F7' },
      /**
       * Unchanged, and that is the point: these are defined AGAINST the brand gradient, which is
       * dark in both themes. Re-tinting them for the page's theme would break the only ground they
       * were ever measured on.
       */
      onBrand: { positive: '#E5FCEE', negative: '#FFF4F4' },
    },
    /** Row chips. `background` is the deep wash, `foreground` the pale text on it, `icon` the hue. */
    transaction: {
      deposit: { background: '#12301C', foreground: '#8FE0A6', icon: '#4CB86A' },
      withdrawal: { background: '#3A1512', foreground: '#FFA9A1', icon: '#F2685C' },
      transferOut: { background: '#332008', foreground: '#FFD08A', icon: '#F5A623' },
      transferIn: { background: '#0A2A3D', foreground: '#8FD7F7', icon: '#38B6F0' },
    },
  },
  surfaces: {
    canvas: '#141414', // colorNeutralBackground3 — BELOW the card, as in light
    border: '#3D3D3D', // colorNeutralStroke3
  },
  /**
   * Shadows carry more alpha and stay black.
   *
   * A shadow is an absence of light, so tinting it lighter to "show up" is backwards — what actually
   * fails on a dark ground is the ALPHA: 0.05 black over `#141414` is a change of about one value
   * step and simply is not there. Doubling the opacity restores the separation without inventing a
   * glow, which is the other common dark-mode mistake (a light halo reads as emission, not elevation).
   */
  shadows: {
    sm: '0px 1px 2px rgba(0, 0, 0, 0.4)',
    md: '0px 4px 6px -1px rgba(0, 0, 0, 0.5), 0px 2px 4px -1px rgba(0, 0, 0, 0.4)',
    xl: '0px 20px 25px -5px rgba(0, 0, 0, 0.6), 0px 10px 10px -5px rgba(0, 0, 0, 0.4)',
    brand: '0px 8px 24px rgba(0, 0, 0, 0.55)',
  },
  /**
   * The brand gradient is UNCHANGED, and every other gradient inverts.
   *
   * `brand` was already dark-to-dark so white text could sit on it; it needs nothing, and changing it
   * would break the two `onBrand` values measured against it. The status washes are the opposite
   * case: they are pale by construction, and a pale wash on a `#141414` canvas does not read as a
   * tint, it reads as a lamp.
   */
  gradients: {
    brand: 'linear-gradient(135deg, #0077B6 0%, #003F64 100%)',
    success: 'linear-gradient(135deg, #12301C 0%, #0C2415 100%)',
    error: 'linear-gradient(135deg, #3A1512 0%, #2B0F0D 100%)',
    warning: 'linear-gradient(135deg, #332008 0%, #261806 100%)',
    info: 'linear-gradient(135deg, #0A2A3D 0%, #07202E 100%)',
    primary: 'linear-gradient(135deg, #0B2338 0%, #081A2A 100%)',
    brandTint: 'linear-gradient(135deg, #0E2233 0%, #0A1A28 100%)',
  },
};
