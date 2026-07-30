# ADR-0027: Dark mode through CSS custom properties, decided before the first paint

**Status**: Accepted

**Date**: 2026-07-30

**Decision Makers**: Vladislav Aleshaev

---

## Context

The app had a dark Fluent theme defined and deliberately unwired since U1, and a Settings row that
said "Dark mode — Coming soon". Turning the theme on was never the work. Fluent's `FluentProvider`
themes Fluent's own controls; everything this app draws itself reads a hand-maintained palette —
`colors`, `surfaces`, `shadows`, `gradients` in `theme/tokens.ts` — imported by twenty-five, seventeen,
eight and six files respectively, every value of it light-only.

## Decision

1. **The palette becomes CSS custom properties; components are not touched.** `colors.brand[60]`
   still reads as `colors.brand[60]`; it now resolves to `var(--ab-colors-brand-60)`. Not one of the
   consuming files changed, which is also what keeps the diff reviewable.

2. **A React context was rejected.** It would have meant editing every consumer, re-rendering the
   tree on each switch, and — decisively — it cannot exist early enough. The theme has to be chosen
   *before the first paint* or a dark-mode user sees a white flash on every load, and nothing in the
   module graph runs before the document is parsed. Only an attribute on `<html>` can.

3. **The pre-paint decision is an EXTERNAL script, and that is a CSP requirement rather than a
   preference.** `SecurityHeadersMiddleware` serves `script-src 'self'` with no `unsafe-inline` and
   no nonce. An inline block in `index.html` would have run happily under Vite's dev server and been
   *refused in production* — a bug that only appears where it cannot be debugged. `public/theme-init.js`
   is same-origin, blocking, and in `<head>`.

4. **The literal palettes stay exported, and the stylesheet is DERIVED from them.** This is the part
   that looks redundant and is not. `surfaces.test.ts` reads token values as hex and computes
   luminance; had the tokens simply become `var()` strings, `luminance('var(--ab-surfaces-canvas)')`
   would be `NaN` and the contrast assertions would have gone on passing while measuring nothing.
   One traversal (`cssVariables.ts`) produces the variable names, the references components import,
   and the CSS — so those three cannot drift. The checked-in stylesheet is compared against that
   traversal by a test, because a stale `var()` reference is invisible: browsers do not warn about an
   undefined custom property, they simply paint nothing.

5. **The dark values are derived from Fluent's own dark theme, by role.** `neutral[700]` is "primary
   text", so its dark counterpart is `colorNeutralForeground2`, not the mirror of index 700 on a
   ten-step scale. Anchoring to Fluent means our palette and the component library agree instead of
   disagreeing one control at a time. The relationship U2 fought for is preserved and asserted: the
   canvas sits BELOW the card — `#F3F4F6` under `#FFFFFF` in light, `#141414` under `#292929` in dark.
   A naive inversion gets this backwards and reproduces the flat look U2 had to rescue the app from.

6. **Three preference values, not a boolean.** `system | light | dark`. "Follow the device" is a real
   answer: a user who has never touched the toggle wants their OS honoured, and a user who chose
   light on a dark machine wants that kept. A boolean cannot tell those apart, and the person it
   fails is the one whose machine switches at sunset. While the preference is `system`, a
   `matchMedia` listener follows it; an explicit choice is never overridden.

## Consequences

**Contrast is guaranteed; beauty is not.** `surfaces.test.ts` now runs over BOTH palettes and asserts
4.5:1 for primary text, secondary text and all four transaction chips, plus that no seam has
collapsed into its neighbours. Those are floors, not judgement. Whether the result is handsome needs
a human in a real browser, and U8 is where that belongs — the in-app browser pane cannot be used for
it, since its permanently hidden tabs never fire `requestAnimationFrame` and React 19 freezes
mid-update there.

**The pre-paint script duplicates the storage key, the media query and the default.** It cannot
import them; that is the price of running first. A test reads the file and asserts all three still
match the module, because the failure is silent and specific: change the key on one side and every
load paints light and corrects itself a frame later — exactly the flash the script exists to prevent,
looking like a rendering quirk rather than a bug.

**The attribute is written on mount as well, in a `useLayoutEffect`.** The first draft skipped it,
reasoning that the pre-paint script had already done the job and that repeating the write would make
the script look optional. That traded one idempotent `setAttribute` for a divergence nothing detects:
if the script 404s, if another tab writes the preference between `<head>` and mount, or if no script
ran at all, Fluent takes the stored preference while `<html>` keeps the old attribute and the app
renders the custom properties in one theme and the component library in the other — silently. The
script remains load-bearing for the reason it always was: it runs before the paint, and
`useLayoutEffect` runs before the *next* one, so the correction is never a visible flicker.

**Amended 2026-07-30, after looking at it: the brand ramp could not carry both of its jobs.**
`brand[60]` was simultaneously a colour that sits ON surfaces (links, icons, active labels) and a
surface white text sits on (buttons, the dashboard's Transfer banner). On white those two demands
coincide; on a dark ground they pull apart, and the dark value chosen for readability —
`colorBrandForeground1`, 5.73:1 against the canvas — carried white at **3.22:1**, below AA. Measured
in a real browser, on the most prominent call-to-action in the app.

Every contrast test in the suite asked about text on a CARD, so none of them could see it. The fix is
a `brandFill` role — `rest / hover / pressed`, dark values taken from Fluent's `colorBrandBackground`
where light values keep today's pixels — used by the eight fills that carry
`colorNeutralForegroundOnBrand`, while the ramp keeps the text and stroke role it is actually right
for. Three decorative marks that carry no text (two active-state rails, the PIN dot) stay on the
ramp deliberately: they have to read AGAINST a surface, which is the ramp's job.

**The emitter lowercases hex.** Prettier does that to CSS, and the output is checked in; emitting
uppercase would leave the formatter and the drift test in permanent disagreement, with whichever ran
last winning.

**`"node"` joined the app tsconfig's types.** For the tests, not the app: they read the checked-in
stylesheet and `public/theme-init.js` from disk. Vitest stubs `.css` imports, so `?raw` returns an
empty string whatever the query says — an assertion that would have passed against nothing.
