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
 * Bare media conditions, for `window.matchMedia`.
 *
 * These are NOT valid Griffel keys — see `atMedia` below. The comment here used to claim they were
 * ("Griffel wants these as object keys"), and nothing had ever tried it: a Griffel key must be the
 * whole at-rule, so `[media.lg]: { … }` produces a rule that is silently dropped and a component
 * that simply has no responsive behaviour. Two of them shipped that way for an afternoon.
 */
export const media = {
  sm: `(min-width: ${breakpoints.sm}px)`,
  md: `(min-width: ${breakpoints.md}px)`,
  lg: `(min-width: ${breakpoints.lg}px)`,
  xl: `(min-width: ${breakpoints.xl}px)`,
} as const;

/**
 * The same conditions as at-rules, for Griffel: `[atMedia.lg]: { … }`.
 *
 * Pre-formatted strings rather than a function call because a template literal in a key position
 * is evaluated per render and defeats Griffel's static extraction.
 */
export const atMedia = {
  sm: `@media ${media.sm}`,
  md: `@media ${media.md}`,
  lg: `@media ${media.lg}`,
  xl: `@media ${media.xl}`,
} as const;
