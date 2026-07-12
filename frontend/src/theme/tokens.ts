/**
 * Design Tokens for AzureBank
 * Based on design-tokens/tokens.json from Figma export
 */

export const colors = {
  brand: {
    10: '#001D3D',
    20: '#002D5E',
    30: '#003D7F',
    40: '#004DA0', // Primary hover/pressed
    50: '#005DC1',
    60: '#006DE2', // Primary brand - main CTA
    70: '#1A7FE8',
    80: '#4D99ED', // Brand hover light
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

export const typography = {
  fontFamily: {
    base: "'Segoe UI', 'Segoe UI Web (West European)', -apple-system, BlinkMacSystemFont, Roboto, 'Helvetica Neue', sans-serif",
    mono: "Consolas, 'Courier New', Courier, monospace",
  },
  fontSize: {
    xs: '10px',
    sm: '12px',
    base: '14px',
    md: '16px',
    lg: '20px',
    xl: '24px',
    '2xl': '28px',
    '3xl': '32px',
    '4xl': '40px',
    '5xl': '48px',
  },
  fontWeight: {
    regular: 400,
    medium: 500,
    semibold: 600,
    bold: 700,
  },
  lineHeight: {
    tight: 1.2,
    normal: 1.5,
    relaxed: 1.75,
  },
} as const;

export const spacing = {
  0: '0px',
  1: '4px',
  2: '8px',
  3: '12px',
  4: '16px',
  5: '20px',
  6: '24px',
  8: '32px',
  10: '40px',
  12: '48px',
  16: '64px',
} as const;

export const borderRadius = {
  none: '0px',
  sm: '2px',
  md: '4px',
  lg: '6px',
  xl: '8px',
  '2xl': '12px',
  full: '9999px',
} as const;

export const shadows = {
  sm: '0px 1px 2px rgba(0, 0, 0, 0.05)',
  md: '0px 4px 6px -1px rgba(0, 0, 0, 0.1), 0px 2px 4px -1px rgba(0, 0, 0, 0.06)',
  lg: '0px 10px 15px -3px rgba(0, 0, 0, 0.1), 0px 4px 6px -2px rgba(0, 0, 0, 0.05)',
  xl: '0px 20px 25px -5px rgba(0, 0, 0, 0.1), 0px 10px 10px -5px rgba(0, 0, 0, 0.04)',
} as const;

export const breakpoints = {
  mobile: 375,
  tablet: 768,
  desktop: 1024,
  wide: 1440,
} as const;

// ============================================
// COMPONENT SIZE TOKENS
// ============================================

export const componentSizes = {
  // Buttons
  button: {
    small: { height: '36px', heightDesktop: '40px', fontSize: '14px', padding: '0 16px' },
    medium: { height: '44px', heightDesktop: '48px', fontSize: '15px', padding: '0 20px' },
    large: { height: '48px', heightDesktop: '52px', fontSize: '16px', padding: '0 24px' },
  },

  // Inputs
  input: {
    small: { height: '40px', heightDesktop: '44px' },
    medium: { height: '48px', heightDesktop: '52px' },
    large: { height: '56px', heightDesktop: '64px' },
  },

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

  // Cards
  card: {
    maxWidth: {
      mobile: '343px', // 375 - 32px padding
      desktop: '440px',
      wide: '480px',
    },
    padding: {
      none: '0',
      small: { mobile: '16px', desktop: '20px' },
      medium: { mobile: '24px', desktop: '32px' },
      large: { mobile: '24px', desktop: '48px' },
    },
    radius: '16px',
  },
} as const;

// ============================================
// MEDIA QUERY HELPERS
// ============================================

export const mediaQueries = {
  mobile: `(max-width: ${breakpoints.tablet - 1}px)`,
  tablet: `(min-width: ${breakpoints.tablet}px) and (max-width: ${breakpoints.desktop - 1}px)`,
  desktop: `(min-width: ${breakpoints.desktop}px)`,
  touch: `(max-width: ${breakpoints.desktop - 1}px)`,
  wide: `(min-width: ${breakpoints.wide}px)`,
} as const;

// ============================================
// ANIMATION TOKENS
// ============================================

export const transitions = {
  fast: '150ms ease',
  normal: '200ms ease',
  slow: '300ms ease',
  spring: '300ms cubic-bezier(0.34, 1.56, 0.64, 1)',
} as const;

// ============================================
// GRADIENT PRESETS
// ============================================

export const gradients = {
  brand: 'linear-gradient(135deg, #006DE2 0%, #004DA0 100%)',
  success: 'linear-gradient(135deg, #E6F4EA 0%, #D4EDDA 100%)',
  error: 'linear-gradient(135deg, #FCE8E6 0%, #FADBD8 100%)',
  warning: 'linear-gradient(135deg, #FEF3E2 0%, #FDE9CC 100%)',
  info: 'linear-gradient(135deg, #E0F2FE 0%, #D1E4FC 100%)',
  primary: 'linear-gradient(135deg, #E6F0FC 0%, #D1E4FC 100%)',
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
