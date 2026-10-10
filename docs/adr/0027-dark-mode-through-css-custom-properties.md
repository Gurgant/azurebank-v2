# ADR-0027: Dark mode through CSS custom properties, decided before the first paint

**Status:** Accepted · **Date:** 2026-07-30 · **Amended:** 2026-07-30 (decision 7, the brand fill) ·
**Decision Makers:** Vladislav Aleshaev

## Context

The app has a dark Fluent theme, and turning it on is not the work. Fluent's `FluentProvider` themes
Fluent's own controls; everything the app draws itself reads a hand-maintained palette (`colors`,
`surfaces`, `shadows`, `gradients` in `theme/tokens.ts`), imported by twenty-five, seventeen, eight
and six files respectively, and every value of it is light-only. The theme also has to be chosen
before the first paint, or a dark-mode user sees a white flash on every load.

## Decision

1. **The palette is CSS custom properties; components are not touched.** `colors.brand[60]` now
   resolves to `var(--ab-colors-brand-60)`, so no consuming file has to change.
2. **A React context is rejected**, because it means editing every consumer and re-rendering the
   tree on each switch, and it cannot exist early enough: nothing in the module graph runs before
   the document is parsed. Only an attribute on `<html>` can be set before the first paint.
3. **The pre-paint decision is an external script, as a CSP requirement.**
   `SecurityHeadersMiddleware` serves `script-src 'self'` with no `unsafe-inline` and no nonce, so
   an inline block in `index.html` runs under Vite's dev server and is refused in production.
   `public/theme-init.js` is same-origin, blocking, and in `<head>`.
4. **The literal palettes stay exported, and the stylesheet is derived from them.**
   `surfaces.test.ts` reads token values as hex and computes luminance: with tokens that are only
   `var()` strings the luminance is `NaN`, and the contrast assertions pass while measuring nothing.
   One traversal (`cssVariables.ts`) produces the variable names, the references components import
   and the CSS, so the three cannot drift. A test compares the checked-in stylesheet with it,
   because a browser paints nothing for an undefined property and never warns.
5. **The dark values are derived from Fluent's own dark theme, by role**, so that the palette and
   the component library agree. `neutral[700]` is "primary text", so its dark counterpart is
   `colorNeutralForeground2`, not the mirror of index 700 on a ten-step scale. The canvas sits below
   the card in both themes: `#F3F4F6` under `#FFFFFF` in light, `#141414` under `#292929` in dark. A
   naive inversion gets this backwards and flattens the app.
6. **Three preference values, not a boolean**: `system | light | dark`. A user who never touched the
   toggle wants the device honoured, and a user who chose light on a dark machine wants that kept; a
   boolean cannot tell them apart. While the preference is `system` a `matchMedia` listener follows
   the device, and an explicit choice is never overridden.
7. **The brand colour has two roles.** `brand[60]` is both a colour that sits on surfaces (links,
   icons, active labels) and a surface that white text sits on (buttons, the dashboard's Transfer
   banner). On a dark ground the two pull apart: `colorBrandForeground1`, chosen for readability, is
   5.73:1 against the canvas and carries white at 3.22:1, below AA. A `brandFill` role
   (`rest / hover / pressed`, dark values from Fluent's `colorBrandBackground`, light values
   unchanged) serves the eight fills that carry `colorNeutralForegroundOnBrand`. The ramp keeps text
   and strokes, and three marks with no text (two active-state rails, the PIN dot), because those
   have to read against a surface.
8. **The attribute is written on mount as well, in a `useLayoutEffect`.** If the script 404s, if
   another tab writes the preference between `<head>` and mount, or if no script ran, Fluent takes
   the stored preference while `<html>` keeps the old attribute, and the custom properties render in
   one theme and the component library in the other, silently. The script stays load-bearing: it
   runs before the first paint and `useLayoutEffect` before the next, so nothing flickers.

## Rejected

- Rejected: a React context, because it cannot run before the first paint (decision 2).
- Rejected: an inline pre-paint script, because the CSP refuses it in production (decision 3).

## Consequences

- Contrast is guaranteed; beauty is not. `surfaces.test.ts` runs over both palettes and asserts
  4.5:1 for primary text, secondary text and all four transaction chips, and that no seam has
  collapsed into its neighbours. Those are floors: handsome takes a person in a real browser.
- The pre-paint script duplicates the storage key, the media query and the default, because it
  cannot import them. A test holds the script to the module, because the failure is silent: with a
  different key on one side every load paints light and corrects itself a frame later.
- The emitter lowercases hex, because Prettier does that to CSS and the output is checked in:
  uppercase would leave the formatter and the drift test in permanent disagreement.
- `"node"` is in the app tsconfig's types, for the tests: they read the checked-in stylesheet and
  `public/theme-init.js` from disk, because Vitest stubs `.css` imports and `?raw` returns empty.

## Verified by

- `surfaces.test.ts`: contrast and seams on both palettes. `themeVariables.test.ts`: the checked-in
  stylesheet is what the palettes generate. `themePreference.test.ts`: the three values, the
  pre-paint script, the write before paint. `brandFillUsage.test.ts`: fills read `brandFill`.

## Related

ADR-0054.
