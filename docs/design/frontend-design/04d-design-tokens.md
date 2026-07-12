# Design Tokens
## Bank Account Management System

**Document Version**: 3.0
**Created**: 2025-12-16
**Updated**: 2025-12-17
**Author**: Web Designer (Virtual Team Member)
**Status**: UPDATED - AzureBank Branding

**Bank Name**: AzureBank

---

## 1. Overview

This document defines FluentUI design tokens and custom theme configuration for the AzureBank application. We leverage FluentUI v9's built-in token system while defining custom semantic tokens for banking-specific use cases.

### Design Philosophy
- **FluentUI First**: Use built-in tokens wherever possible
- **Semantic Naming**: Custom tokens describe purpose, not appearance
- **Accessibility**: All colors meet WCAG 2.1 AA contrast requirements
- **Consistency**: Single source of truth for all design values

---

## 2. Color Palette

### 2.1 Brand Colors (Custom Theme)

We use a professional blue-based brand color scheme appropriate for financial applications.

```typescript
// src/theme/brandColors.ts
export const bankAppBrandRamp = {
  10: '#001D3D',  // Darkest
  20: '#002D5E',
  30: '#003D7F',
  40: '#004DA0',
  50: '#005DC1',
  60: '#006DE2',  // Primary brand
  70: '#1A7FE8',
  80: '#4D99ED',  // Brand hover
  90: '#80B3F2',
  100: '#B3CDF7',
  110: '#CCE0FA',
  120: '#E6F0FC',
  130: '#F0F6FE',
  140: '#F5FAFF',
  150: '#FAFCFF',
  160: '#FFFFFF', // Lightest
};
```

### 2.2 Semantic Colors

```typescript
// src/theme/semanticColors.ts
export const semanticColors = {
  // Transaction Types
  deposit: {
    background: '#E6F4EA',     // Light green
    foreground: '#137333',     // Dark green
    icon: '#34A853',           // Green
  },
  withdrawal: {
    background: '#FCE8E6',     // Light red
    foreground: '#C5221F',     // Dark red
    icon: '#EA4335',           // Red
  },
  transferOut: {
    background: '#FEF3E2',     // Light orange
    foreground: '#B45309',     // Dark orange
    icon: '#F59E0B',           // Orange
  },
  transferIn: {
    background: '#E0F2FE',     // Light blue
    foreground: '#0369A1',     // Dark blue
    icon: '#0EA5E9',           // Blue
  },

  // Status Colors
  success: {
    background: '#E6F4EA',
    foreground: '#137333',
    border: '#34A853',
  },
  warning: {
    background: '#FEF3E2',
    foreground: '#B45309',
    border: '#F59E0B',
  },
  error: {
    background: '#FCE8E6',
    foreground: '#C5221F',
    border: '#EA4335',
  },
  info: {
    background: '#E0F2FE',
    foreground: '#0369A1',
    border: '#0EA5E9',
  },

  // Balance Display
  balance: {
    positive: '#137333',
    negative: '#C5221F',
    neutral: '#1F2937',
  },
};
```

### 2.3 FluentUI Token Mapping

| Purpose | FluentUI Token | Custom Override |
|---------|---------------|-----------------|
| Primary Action | `colorBrandBackground` | `#006DE2` |
| Primary Hover | `colorBrandBackgroundHover` | `#004DA0` |
| Primary Text | `colorBrandForeground1` | `#006DE2` |
| Background | `colorNeutralBackground1` | Default |
| Card Background | `colorNeutralBackground2` | Default |
| Border | `colorNeutralStroke1` | Default |
| Text Primary | `colorNeutralForeground1` | Default |
| Text Secondary | `colorNeutralForeground2` | Default |
| Error | `colorPaletteRedForeground1` | Default |
| Success | `colorPaletteGreenForeground1` | Default |

---

## 3. Typography

### 3.1 Font Family

```typescript
// FluentUI Default Font Stack
fontFamilyBase: "'Segoe UI', 'Segoe UI Web (West European)', -apple-system, BlinkMacSystemFont, Roboto, 'Helvetica Neue', sans-serif"

// Monospace (for account numbers)
fontFamilyMonospace: "Consolas, 'Courier New', Courier, monospace"
```

### 3.2 Font Sizes (FluentUI Tokens)

| Token | Size | Usage |
|-------|------|-------|
| `fontSizeBase100` | 10px | Micro labels |
| `fontSizeBase200` | 12px | Captions, helper text |
| `fontSizeBase300` | 14px | Body text (default) |
| `fontSizeBase400` | 16px | Emphasized body |
| `fontSizeBase500` | 20px | Card titles |
| `fontSizeBase600` | 24px | Page titles |
| `fontSizeHero700` | 28px | Section headers |
| `fontSizeHero800` | 32px | Large displays |
| `fontSizeHero900` | 40px | Hero text |
| `fontSizeHero1000` | 68px | (Not used) |

### 3.3 Custom Typography Scale

```typescript
// src/theme/typography.ts
export const typography = {
  // Balance Display (Hero)
  balanceDisplay: {
    fontSize: '48px',           // Custom - between Hero900 and Hero1000
    fontWeight: 700,
    lineHeight: '56px',
    letterSpacing: '-0.02em',
  },

  // Account Number
  accountNumber: {
    fontSize: '14px',
    fontWeight: 400,
    fontFamily: 'monospace',
    letterSpacing: '0.05em',
  },

  // Transaction Amount
  transactionAmount: {
    fontSize: '16px',
    fontWeight: 600,
    fontFamily: 'monospace',
  },

  // Form Labels
  formLabel: {
    fontSize: '14px',
    fontWeight: 500,
    lineHeight: '20px',
  },
};
```

### 3.4 Font Weights (FluentUI Tokens)

| Token | Weight | Usage |
|-------|--------|-------|
| `fontWeightRegular` | 400 | Body text |
| `fontWeightMedium` | 500 | Labels, captions |
| `fontWeightSemibold` | 600 | Buttons, headings |
| `fontWeightBold` | 700 | Balance display |

---

## 4. Spacing

### 4.1 Spacing Scale (FluentUI Tokens)

| Token | Value | Usage |
|-------|-------|-------|
| `spacingHorizontalNone` | 0 | Reset |
| `spacingHorizontalXXS` | 2px | Micro spacing |
| `spacingHorizontalXS` | 4px | Icon margins |
| `spacingHorizontalSNudge` | 6px | Tight spacing |
| `spacingHorizontalS` | 8px | Compact elements |
| `spacingHorizontalMNudge` | 10px | - |
| `spacingHorizontalM` | 12px | Default gap |
| `spacingHorizontalL` | 16px | Card padding |
| `spacingHorizontalXL` | 20px | Section spacing |
| `spacingHorizontalXXL` | 24px | Large sections |
| `spacingHorizontalXXXL` | 32px | Page margins |

### 4.2 Layout Spacing

```typescript
// src/theme/layout.ts
export const layout = {
  // Page Layout
  page: {
    maxWidth: '1200px',
    paddingDesktop: '32px',
    paddingMobile: '16px',
  },

  // Content Area
  content: {
    maxWidth: '800px',  // Dashboard content
    gap: '24px',        // Between cards
  },

  // Cards
  card: {
    padding: '24px',
    paddingMobile: '16px',
    borderRadius: '8px',
    gap: '16px',
  },

  // Forms
  form: {
    fieldGap: '16px',
    labelGap: '4px',
  },

  // Header
  header: {
    height: '64px',
    heightMobile: '56px',
  },

  // Dialog
  dialog: {
    width: '480px',
    widthMobile: '100%',
    padding: '24px',
  },
};
```

---

## 5. Shadows

### 5.1 FluentUI Shadow Tokens

| Token | Usage |
|-------|-------|
| `shadow2` | Subtle elevation (cards at rest) |
| `shadow4` | Light elevation (hover states) |
| `shadow8` | Medium elevation (dropdowns) |
| `shadow16` | High elevation (dialogs) |
| `shadow28` | Highest elevation (tooltips) |
| `shadow64` | (Not commonly used) |

### 5.2 Shadow Application

```typescript
// Component Shadow Mapping
export const shadowMap = {
  card: 'shadow4',           // Balance card, transaction cards
  cardHover: 'shadow8',      // Card hover state
  dropdown: 'shadow8',       // Filter dropdowns
  dialog: 'shadow16',        // Modal dialogs
  toast: 'shadow16',         // Toast notifications
  tooltip: 'shadow28',       // Tooltips
};
```

---

## 6. Border Radius

### 6.1 FluentUI Border Radius Tokens

| Token | Value | Usage |
|-------|-------|-------|
| `borderRadiusNone` | 0 | Sharp corners |
| `borderRadiusSmall` | 2px | Small elements |
| `borderRadiusMedium` | 4px | Buttons, inputs |
| `borderRadiusLarge` | 6px | Cards |
| `borderRadiusXLarge` | 8px | Dialogs |
| `borderRadiusCircular` | 50% | Avatars, icons |

### 6.2 Application

```typescript
export const borderRadius = {
  button: 'borderRadiusMedium',      // 4px
  input: 'borderRadiusMedium',       // 4px
  card: 'borderRadiusLarge',         // 6px
  dialog: 'borderRadiusXLarge',      // 8px
  badge: 'borderRadiusCircular',     // Pills
  avatar: 'borderRadiusCircular',    // User avatars
};
```

---

## 7. Breakpoints

### 7.1 Responsive Breakpoints

```typescript
// src/theme/breakpoints.ts
export const breakpoints = {
  // Mobile First
  mobile: '0px',      // Default (0 - 479px)
  tablet: '480px',    // Small tablets (480 - 767px)
  desktop: '768px',   // Large tablets/small desktop (768 - 1023px)
  wide: '1024px',     // Desktop (1024px+)
  ultraWide: '1440px', // Large screens
};

// Media Query Helpers
export const mediaQueries = {
  mobile: '@media (max-width: 479px)',
  tablet: '@media (min-width: 480px) and (max-width: 767px)',
  desktop: '@media (min-width: 768px)',
  wide: '@media (min-width: 1024px)',
};
```

### 7.2 Responsive Behavior

| Breakpoint | Navigation | Cards | Dialog |
|------------|-----------|-------|--------|
| Mobile (< 480px) | Hamburger | Full width | Full screen |
| Tablet (480-767px) | Hamburger | 2 columns | Centered modal |
| Desktop (768px+) | Inline | 2 columns | Centered modal |
| Wide (1024px+) | Inline | 3 columns | Centered modal |

---

## 8. Icons

### 8.1 FluentUI Icons Used

```typescript
// From @fluentui/react-icons
import {
  // Navigation
  PersonRegular,
  SignOutRegular,
  ChevronDownRegular,
  ChevronLeftRegular,
  DismissRegular,

  // Actions
  AddRegular,
  ArrowDownloadRegular,    // Deposit
  ArrowUploadRegular,      // Withdrawal
  ArrowSwapRegular,        // Transfer
  CopyRegular,
  EyeRegular,
  EyeOffRegular,

  // Status
  CheckmarkCircleRegular,
  ErrorCircleRegular,
  WarningRegular,
  InfoRegular,

  // Transaction Types
  ArrowDownRegular,        // Money in
  ArrowUpRegular,          // Money out
  ArrowRightRegular,       // Transfer out
  ArrowLeftRegular,        // Transfer in

  // UI
  FilterRegular,
  CalendarRegular,
  SearchRegular,
  MoreHorizontalRegular,
} from '@fluentui/react-icons';
```

### 8.2 Icon Sizes

| Size Token | Value | Usage |
|-----------|-------|-------|
| `fontSize16` | 16px | Inline icons (buttons, inputs) |
| `fontSize20` | 20px | Default icons |
| `fontSize24` | 24px | Card icons, navigation |
| `fontSize32` | 32px | Hero icons |
| `fontSize48` | 48px | Empty states |

---

## 9. Theme Configuration

### 9.1 Light Theme (Default)

```typescript
// src/theme/lightTheme.ts
import { createLightTheme, BrandVariants } from '@fluentui/react-components';
import { bankAppBrandRamp } from './brandColors';

const bankAppBrand: BrandVariants = {
  10: bankAppBrandRamp[10],
  20: bankAppBrandRamp[20],
  30: bankAppBrandRamp[30],
  40: bankAppBrandRamp[40],
  50: bankAppBrandRamp[50],
  60: bankAppBrandRamp[60],
  70: bankAppBrandRamp[70],
  80: bankAppBrandRamp[80],
  90: bankAppBrandRamp[90],
  100: bankAppBrandRamp[100],
  110: bankAppBrandRamp[110],
  120: bankAppBrandRamp[120],
  130: bankAppBrandRamp[130],
  140: bankAppBrandRamp[140],
  150: bankAppBrandRamp[150],
  160: bankAppBrandRamp[160],
};

export const bankAppLightTheme = createLightTheme(bankAppBrand);

// Custom overrides if needed
export const customLightTheme = {
  ...bankAppLightTheme,
  // Add custom token overrides here
};
```

### 9.2 Dark Theme (Optional - Future Enhancement)

```typescript
// src/theme/darkTheme.ts
import { createDarkTheme } from '@fluentui/react-components';
import { bankAppBrand } from './lightTheme';

export const bankAppDarkTheme = createDarkTheme(bankAppBrand);

// Custom overrides for dark mode
export const customDarkTheme = {
  ...bankAppDarkTheme,
  // Dark mode specific overrides
};
```

### 9.3 Theme Provider Setup

```typescript
// src/App.tsx
import { FluentProvider } from '@fluentui/react-components';
import { customLightTheme } from './theme/lightTheme';

function App() {
  return (
    <FluentProvider theme={customLightTheme}>
      {/* Application components */}
    </FluentProvider>
  );
}
```

---

## 10. Animation & Transitions

### 10.1 Duration Tokens

| Token | Duration | Usage |
|-------|----------|-------|
| `durationUltraFast` | 50ms | Micro-interactions |
| `durationFaster` | 100ms | Fast feedback |
| `durationFast` | 150ms | Button states |
| `durationNormal` | 200ms | Default transitions |
| `durationSlow` | 300ms | Dialogs, modals |
| `durationSlower` | 400ms | Complex animations |

### 10.2 Easing Functions

| Token | Function | Usage |
|-------|----------|-------|
| `curveEasyEase` | ease | General transitions |
| `curveEasyEaseMax` | ease-in-out | Emphasis |
| `curveDecelerateMin` | ease-out | Enter animations |
| `curveAccelerateMin` | ease-in | Exit animations |

### 10.3 Common Animations

```typescript
export const animations = {
  // Button hover
  buttonHover: {
    transition: 'background-color 150ms ease, transform 150ms ease',
  },

  // Card hover
  cardHover: {
    transition: 'box-shadow 200ms ease, transform 200ms ease',
    transform: 'translateY(-2px)',
  },

  // Dialog enter
  dialogEnter: {
    animation: 'fadeIn 200ms ease-out, slideUp 200ms ease-out',
  },

  // Toast enter
  toastEnter: {
    animation: 'slideIn 300ms ease-out',
  },

  // Skeleton pulse
  skeletonPulse: {
    animation: 'pulse 1.5s ease-in-out infinite',
  },
};
```

---

## 11. Z-Index Scale

```typescript
export const zIndex = {
  base: 0,
  dropdown: 1000,
  sticky: 1020,
  fixed: 1030,
  modalBackdrop: 1040,
  modal: 1050,
  popover: 1060,
  tooltip: 1070,
  toast: 1080,
};
```

---

## 12. Design Token Export

### 12.1 Complete Theme Export

```typescript
// src/theme/index.ts
export { customLightTheme, customDarkTheme } from './themes';
export { semanticColors } from './semanticColors';
export { typography } from './typography';
export { layout } from './layout';
export { breakpoints, mediaQueries } from './breakpoints';
export { animations } from './animations';
export { zIndex } from './zIndex';
```

---

**Document Status**: COMPLETE - Ready for Phase 2 Review
