import type { BrandVariants } from '@fluentui/react-components';

/**
 * The Fluent brand ramp, seeded on #0077b6.
 *
 * Generated with Microsoft's own theme-designer algorithm rather than by hand: two quadratic
 * Béziers through CIE Lab (black → dark control → key → light control → white), arc-length sampled
 * into 16 lightness stops, chroma snapped into sRGB gamut. Hue holds to 2.5° across the whole ramp
 * in CIE LCh.
 *
 * **#0077b6 sits at index 80, and that is the entire point.** Fluent's `createLightTheme` reads
 * index 80 as the primary — 14 alias tokens derive from it, including `colorBrandForeground1`,
 * `colorBrandBackground`, `colorBrandStroke1` and `colorCompoundBrandStroke`. The previous ramp put
 * the brand at index 60, which only 8 secondary tokens read, so index 80 fell to a pale #4D99ED and
 * `colorBrandForeground1` shipped at **2.96:1 on white** — a WCAG AA failure live on the login and
 * register pages. Twenty light-theme tokens go from failing to passing with this ramp.
 *
 * Because the generated values are correct on their own, `fluentTheme.ts` needs no brand overrides
 * at all: `colorBrandBackground` resolves to 4.87:1 with a white label, and hover and pressed darken
 * to 6.01:1 and 11.08:1 without help.
 *
 * Two residuals, recorded rather than hidden:
 * - #0077b6 is 4.42:1 on `colorNeutralBackground3` (#F3F4F6), just under the 4.5:1 body-text bar. It
 *   clears the 3:1 large-text and UI bar. Where brand text must sit on that surface, use index 70
 *   (#0068A1, 5.46:1).
 * - Three dark-theme *pressed* tokens land at 3.69:1. Microsoft's own shipped `brandWeb` ramp fails
 *   the identical tokens at 3.85:1, so this is Fluent-default parity, not a regression introduced
 *   here. Revisit at U7 when dark mode is switched on.
 */
export const brandRamp: BrandVariants = {
  10: '#001A2D',
  20: '#00263E',
  30: '#003251',
  40: '#003F64',
  50: '#004C77',
  60: '#005A8C',
  70: '#0068A1',
  80: '#0077B6', // the brand — Fluent's primary index
  90: '#2D86C5',
  100: '#4C95D1',
  110: '#67A4DB',
  120: '#81B3E4',
  130: '#9BC2EB',
  140: '#B5D2F2',
  150: '#CEE1F7',
  160: '#E8F1FC',
};
