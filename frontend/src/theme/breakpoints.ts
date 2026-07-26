/**
 * The single source of truth for viewport breakpoints.
 *
 * Min-width only, and that is a design decision rather than a style preference: mixing min- and
 * max-width queries makes two rules fight over the same viewport at the boundary, and which one
 * wins depends on source order rather than on intent. One direction means the cascade reads as a
 * progression — the base case is the narrow one, and each breakpoint only ever adds.
 *
 * `base` is therefore not a query. It is the floor the unqualified styles must work at.
 */
export const breakpoints = {
  base: 320,
  sm: 480,
  md: 640,
  lg: 1024,
  xl: 1366,
} as const;

export type Breakpoint = keyof typeof breakpoints;

/**
 * Query strings for Griffel and for `useMediaQuery`.
 *
 * Griffel wants these as object keys (`[media.lg]: { ... }`), which is why they are pre-formatted
 * strings rather than a function call — a template literal in a key position would be evaluated per
 * render and defeat Griffel's static extraction.
 */
export const media = {
  sm: `(min-width: ${breakpoints.sm}px)`,
  md: `(min-width: ${breakpoints.md}px)`,
  lg: `(min-width: ${breakpoints.lg}px)`,
  xl: `(min-width: ${breakpoints.xl}px)`,
} as const;
