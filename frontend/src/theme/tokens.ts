/**
 * Design Tokens for AzureBank
 * Based on design-tokens/tokens.json from Figma export
 */

export const colors = {
  /**
   * The legacy brand palette, read directly by component code.
   *
   * This is NOT the Fluent ramp in `brandRamp.ts`, and the two must not be conflated. Fluent indexes
   * for its own token generator; this one is indexed by the original Figma export and its shades are
   * consumed as literal values. Swapping one for the other by index is destructive, and measurably
   * so: the Fluent ramp's index 120 is #81B3E4 at luminance 0.425, where this palette's 120 is a
   * near-white #E6F0FC at 0.862 — a drop of 0.44 under the dark text that sits on it at nine call
   * sites (the active sidebar item, Avatar, IconContainer, QuickActionButton, ConfirmDialog).
   *
   * Only the three shades that mean "the brand" and its hover and pressed states have been recoloured
   * onto #0077b6, so the app does not show two different blues at once. The pale end keeps its
   * values: the new ramp has no equivalent that light, and those call sites migrate to Fluent's
   * neutral and brand-tint tokens later in U1 rather than to a wrong index now.
   */
  brand: {
    10: '#001D3D',
    20: '#002D5E',
    30: '#003D7F',
    40: '#003F64', // pressed — from the Fluent ramp's index 40
    50: '#004C77',
    60: '#0077B6', // the brand. Fluent calls this index 80; this palette has always called it 60.
    70: '#0068A1', // hover — from the Fluent ramp's index 70
    80: '#4D99ED',
    90: '#80B3F2',
    100: '#B3CDF7',
    110: '#CCE0FA',
    120: '#E6F0FC',
    130: '#F0F6FE',
    140: '#F5FAFF',
    150: '#FAFCFF',
    160: '#FFFFFF',
  },
  neutral: {
    50: '#F9FAFB', // Background lightest
    100: '#F3F4F6', // Background light
    200: '#E5E7EB', // Border light
    300: '#D1D5DB', // Border default
    400: '#9CA3AF', // Placeholder text
    500: '#6B7280', // Secondary text
    600: '#4B5563', // Tertiary text
    700: '#374151', // Primary text
    800: '#1F2937', // Heading text
    900: '#111827', // Darkest text
  },
  semantic: {
    success: {
      light: '#E6F4EA',
      main: '#34A853',
      dark: '#137333',
    },
    error: {
      light: '#FCE8E6',
      main: '#EA4335',
      dark: '#C5221F',
    },
    warning: {
      light: '#FEF3E2',
      main: '#F59E0B',
      dark: '#B45309',
    },
    info: {
      light: '#E0F2FE',
      main: '#0EA5E9',
      dark: '#0369A1',
    },

    /**
     * Positive and negative values rendered ON a brand surface.
     *
     * `success.main` and `error.main` are tuned for a white ground and all but vanish against the
     * brand panel, so the history summary invented these two tints inline. That was a real gap in
     * the palette rather than carelessness — naming it here is what stops the next dark surface
     * inventing its own.
     */
    onBrand: {
      positive: '#86EFAC',
      negative: '#FCA5A5',
    },
  },
  transaction: {
    deposit: {
      background: '#E6F4EA',
      foreground: '#137333',
      icon: '#34A853',
    },
    withdrawal: {
      background: '#FCE8E6',
      foreground: '#C5221F',
      icon: '#EA4335',
    },
    transferOut: {
      background: '#FEF3E2',
      foreground: '#B45309',
      icon: '#F59E0B',
    },
    transferIn: {
      background: '#E0F2FE',
      foreground: '#0369A1',
      icon: '#0EA5E9',
    },
  },
} as const;

export const shadows = {
  sm: '0px 1px 2px rgba(0, 0, 0, 0.05)',
  md: '0px 4px 6px -1px rgba(0, 0, 0, 0.1), 0px 2px 4px -1px rgba(0, 0, 0, 0.06)',
  xl: '0px 20px 25px -5px rgba(0, 0, 0, 0.1), 0px 10px 10px -5px rgba(0, 0, 0, 0.04)',
} as const;

/**
 * Superseded by `theme/breakpoints.ts`, and kept alive only by `AppLayout`.
 *
 * The new scale is min-width only; this one is read by AppLayout's `max-width: ${tablet - 1}px`
 * rule, which cannot be expressed there by design. That rule disappears when AppLayout moves to
 * CSS-first layout — it already has the `hiddenMobile`/`hiddenDesktop` classes for it, written and
 * never applied — and this block goes with it. `mobile` and `wide` had no readers and are gone.
 */
export const breakpoints = {
  tablet: 768,
  desktop: 1024,
} as const;

// ============================================
// COMPONENT SIZE TOKENS
// ============================================

export const componentSizes = {
  // Avatars
  avatar: {
    sm: { size: '32px', fontSize: '12px' },
    md: { size: '40px', fontSize: '14px' },
    lg: { size: '48px', fontSize: '18px' },
    xl: { size: '64px', fontSize: '24px' },
    '2xl': { size: '72px', fontSize: '28px' },
  },

  // Icon containers (for transaction types, etc.)
  iconContainer: {
    sm: { size: '40px', iconSize: '20px', radius: '10px' },
    md: { size: '44px', iconSize: '22px', radius: '11px' },
    lg: { size: '72px', iconSize: '36px', radius: '50%' },
  },

  // Navigation
  header: {
    height: '56px',
    heightDesktop: '64px',
    paddingX: '16px',
    paddingXDesktop: '24px',
  },
  bottomNav: {
    height: '72px',
    heightWithSafeArea: '92px', // 72px + 20px safe area
    paddingX: '8px',
  },
  sidebar: {
    width: '240px',
    collapsedWidth: '72px',
  },
} as const;

// ============================================
// ANIMATION TOKENS
// ============================================

/**
 * Two durations and one easing, which is the whole measured surface.
 *
 * `slow` (300ms) went with BottomSheet, its only consumer. `spring` was declared and never used —
 * a second easing that no screen ever asked for. Motion here is deliberately narrow: a token set
 * wider than its call sites invites inconsistency rather than preventing it.
 */
export const transitions = {
  fast: '150ms ease',
  normal: '200ms ease',
} as const;

// ============================================
// GRADIENT PRESETS
// ============================================

export const gradients = {
  // Dark-to-dark on purpose: white text sits on this, and the logo's cyan #39b8db would drop it to
  // 2.4:1. Both stops come from the brand ramp — index 80 into index 40.
  brand: 'linear-gradient(135deg, #0077B6 0%, #003F64 100%)',
  success: 'linear-gradient(135deg, #E6F4EA 0%, #D4EDDA 100%)',
  error: 'linear-gradient(135deg, #FCE8E6 0%, #FADBD8 100%)',
  warning: 'linear-gradient(135deg, #FEF3E2 0%, #FDE9CC 100%)',
  info: 'linear-gradient(135deg, #E0F2FE 0%, #D1E4FC 100%)',
  primary: 'linear-gradient(135deg, #E6F0FC 0%, #D1E4FC 100%)',
  // The palest brand tint, for a surface that must recede rather than compete. Spelled out at the
  // Dashboard help card before it had a name.
  brandTint: 'linear-gradient(135deg, #F0F6FE 0%, #E6F0FC 100%)',
} as const;

// ============================================
// Z-INDEX SCALE
// ============================================

export const zIndex = {
  base: 0,
  dropdown: 1000,
  sticky: 1100,
  header: 1200,
  bottomNav: 1200,
  modal: 1300,
  popover: 1400,
  tooltip: 1500,
  toast: 1600,
} as const;
