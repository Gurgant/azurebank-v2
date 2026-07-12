# AzureBank Reusable Component Patterns

> **Purpose**: Detailed implementation guide for shared components
> **Framework**: React 19 + FluentUI v9 + TypeScript
> **Generated from**: Figma HTML references & COMPONENT-SPECS.md

---

## Table of Contents
1. [Design Token System](#1-design-token-system)
2. [Button Component](#2-button-component)
3. [Card Component](#3-card-component)
4. [FormField Component](#4-formfield-component)
5. [Avatar Component](#5-avatar-component)
6. [Badge Component](#6-badge-component)
7. [IconContainer Component](#7-iconcontainer-component)
8. [Divider Component](#8-divider-component)
9. [BottomSheet Component](#9-bottomsheet-component)
10. [TransactionItem Component](#10-transactionitem-component)
11. [AmountInput Component](#11-amountinput-component)
12. [QuickActionButton Component](#12-quickactionbutton-component)
13. [Layout Components](#13-layout-components)
14. [Responsive Utilities](#14-responsive-utilities)

---

## 1. Design Token System

### 1.1 File: `src/theme/tokens.ts`

```typescript
// ============================================
// AZUREBANK DESIGN TOKENS
// ============================================

// BRAND COLORS
export const brandColors = {
  primary: '#006DE2',       // Main CTA, links, active states
  primaryHover: '#004DA0',  // Button hover
  primaryLight: '#E6F0FC',  // Selected backgrounds
  primaryLighter: '#F0F6FE', // Info backgrounds
  primaryGradient: 'linear-gradient(135deg, #006DE2 0%, #004DA0 100%)',
} as const;

// SEMANTIC COLORS
export const semanticColors = {
  success: {
    bg: '#E6F4EA',
    bgHover: '#D4EDDA',
    icon: '#34A853',
    text: '#137333',
    gradient: 'linear-gradient(135deg, #E6F4EA 0%, #D4EDDA 100%)',
  },
  error: {
    bg: '#FCE8E6',
    bgHover: '#FADBD8',
    icon: '#EA4335',
    text: '#C5221F',
    gradient: 'linear-gradient(135deg, #FCE8E6 0%, #FADBD8 100%)',
  },
  warning: {
    bg: '#FEF3E2',
    bgHover: '#FDE9CC',
    icon: '#F59E0B',
    text: '#B45309',
    gradient: 'linear-gradient(135deg, #FEF3E2 0%, #FDE9CC 100%)',
  },
  info: {
    bg: '#E0F2FE',
    bgHover: '#D1E4FC',
    icon: '#0EA5E9',
    text: '#0369A1',
    gradient: 'linear-gradient(135deg, #E0F2FE 0%, #D1E4FC 100%)',
  },
} as const;

// NEUTRAL COLORS
export const neutralColors = {
  gray900: '#1F2937', // Primary text
  gray700: '#6B7280', // Secondary text
  gray500: '#9CA3AF', // Placeholder, disabled
  gray300: '#D1D5DB', // Borders
  gray200: '#E5E7EB', // Dividers
  gray100: '#F3F4F6', // Backgrounds, badges
  gray50: '#F7F8FA',  // Page background
  white: '#FFFFFF',
} as const;

// SPACING (4px increments)
export const spacing = {
  xs: '4px',
  sm: '8px',
  md: '12px',
  lg: '16px',
  xl: '20px',
  '2xl': '24px',
  '3xl': '32px',
  '4xl': '48px',
  '5xl': '64px',
} as const;

// BORDER RADIUS
export const borderRadius = {
  sm: '2px',
  md: '8px',
  lg: '12px',
  xl: '16px',
  '2xl': '24px',
  full: '9999px',
} as const;

// SHADOWS
export const shadows = {
  card: '0px 8px 24px rgba(0, 0, 0, 0.1)',
  cardSm: '0px 2px 8px rgba(0, 0, 0, 0.08)',
  cardMd: '0px 4px 6px -1px rgba(0, 0, 0, 0.1)',
  cardLg: '0px 10px 15px -3px rgba(0, 0, 0, 0.1)',
  toggle: '0px 2px 4px rgba(0, 0, 0, 0.2)',
} as const;

// TYPOGRAPHY
export const typography = {
  fontFamily: "'Segoe UI', 'Segoe UI Web (West European)', -apple-system, BlinkMacSystemFont, Roboto, 'Helvetica Neue', sans-serif",
  fontMono: "Consolas, 'Courier New', monospace",
} as const;

export const fontSizes = {
  h1: '48px',      // Desktop branding
  h2: '36px',      // Transaction amounts
  h3: '32px',      // Balance display
  h4: '28px',      // Page titles (desktop)
  h5: '24px',      // Page titles (mobile), section headers
  h6: '20px',      // Card titles (desktop)
  bodyXl: '18px',  // Card titles (mobile)
  bodyLg: '16px',  // Body text, buttons
  body: '15px',    // Default text
  bodySm: '14px',  // Secondary text, labels
  caption: '13px', // Small text
  tiny: '12px',    // Labels, section headers
  micro: '10px',   // Nav labels
} as const;

export const fontWeights = {
  regular: 400,
  medium: 500,
  semibold: 600,
  bold: 700,
} as const;

// COMPONENT SIZES
export const componentSizes = {
  // Buttons
  buttonHeightMobile: '48px',
  buttonHeightDesktop: '52px',

  // Inputs
  inputHeightMobile: '48px',
  inputHeightDesktop: '52px',

  // Avatars
  avatarSm: '32px',
  avatarMd: '40px',
  avatarLg: '48px',
  avatarXl: '64px',
  avatar2xl: '72px',

  // Icons
  iconSm: '16px',
  iconMd: '20px',
  iconLg: '24px',
  iconXl: '36px',

  // Icon Containers
  iconContainerSm: '40px',
  iconContainerMd: '44px',
  iconContainerLg: '72px',

  // Navigation
  headerHeight: '56px',
  headerHeightDesktop: '64px',
  bottomNavHeight: '72px',
  bottomNavHeightSafe: '83px', // With safe area

  // Cards
  cardMaxWidthMobile: '343px',
  cardMaxWidthDesktop: '440px',
} as const;

// BREAKPOINTS
export const breakpoints = {
  mobile: '375px',
  tablet: '768px',
  desktop: '1024px',
  wide: '1440px',
} as const;

export const mediaQueries = {
  mobile: '(max-width: 767px)',
  tablet: '(min-width: 768px) and (max-width: 1023px)',
  desktop: '(min-width: 1024px)',
} as const;

// TRANSITIONS
export const transitions = {
  fast: '150ms ease',
  normal: '200ms ease',
  slow: '300ms ease',
} as const;
```

---

## 2. Button Component

### 2.1 Interface

```typescript
// src/components/common/Button.types.ts
export type ButtonVariant = 'primary' | 'secondary' | 'success' | 'danger' | 'ghost';
export type ButtonSize = 'small' | 'medium' | 'large';

export interface ButtonProps {
  variant?: ButtonVariant;
  size?: ButtonSize;
  fullWidth?: boolean;
  loading?: boolean;
  disabled?: boolean;
  icon?: React.ReactNode;
  iconPosition?: 'start' | 'end';
  children: React.ReactNode;
  onClick?: () => void;
  type?: 'button' | 'submit' | 'reset';
  className?: string;
}
```

### 2.2 Specifications

| Variant | Background | Text | Border | Hover |
|---------|------------|------|--------|-------|
| primary | `#006DE2` | `white` | none | `#004DA0` |
| secondary | `white` | `#1F2937` | `1px #D1D5DB` | `#F9FAFB` |
| success | `#34A853` | `white` | none | `#2D9248` |
| danger | `#EA4335` | `white` | none | `#D93025` |
| ghost | `transparent` | `#006DE2` | none | `#E6F0FC` |

| Size | Height (Mobile) | Height (Desktop) | Font Size | Padding |
|------|-----------------|------------------|-----------|---------|
| small | 36px | 40px | 14px | 0 16px |
| medium | 44px | 48px | 15px | 0 20px |
| large | 48px | 52px | 16px | 0 24px |

### 2.3 Usage Examples

```tsx
// Primary CTA
<Button variant="primary" size="large" fullWidth>
  Sign In
</Button>

// Secondary with icon
<Button variant="secondary" icon={<Download24Regular />}>
  Download Receipt
</Button>

// Danger outline
<Button variant="danger" size="medium">
  Logout
</Button>

// Loading state
<Button variant="primary" loading>
  Processing...
</Button>
```

---

## 3. Card Component

### 3.1 Interface

```typescript
// src/components/common/Card.types.ts
export type CardVariant = 'default' | 'elevated' | 'outlined' | 'gradient';
export type CardPadding = 'none' | 'small' | 'medium' | 'large';

export interface CardProps {
  variant?: CardVariant;
  padding?: CardPadding;
  fullWidth?: boolean;
  className?: string;
  children: React.ReactNode;
  onClick?: () => void;
}
```

### 3.2 Specifications

| Variant | Background | Border | Shadow |
|---------|------------|--------|--------|
| default | `white` | none | `0px 8px 24px rgba(0,0,0,0.1)` |
| elevated | `white` | none | `0px 10px 15px rgba(0,0,0,0.1)` |
| outlined | `white` | `1px #E5E7EB` | none |
| gradient | `linear-gradient(135deg, #006DE2, #004DA0)` | none | `0px 8px 24px rgba(0,109,226,0.3)` |

| Padding | Mobile | Desktop |
|---------|--------|---------|
| none | 0 | 0 |
| small | 16px | 20px |
| medium | 24px | 32px |
| large | 24px | 48px |

### 3.3 Usage Examples

```tsx
// Default card with medium padding
<Card padding="medium">
  <CardHeader>Account Summary</CardHeader>
  <CardContent>...</CardContent>
</Card>

// Gradient card for balance display
<Card variant="gradient" padding="large">
  <Text style={{ color: 'white' }}>Total Balance</Text>
  <Text size="900">$12,450.00</Text>
</Card>

// Outlined card for selection
<Card variant="outlined" onClick={handleSelect}>
  Select Account
</Card>
```

---

## 4. FormField Component

### 4.1 Interface

```typescript
// src/components/common/FormField.types.ts
export interface FormFieldProps {
  label: string;
  name: string;
  type?: 'text' | 'email' | 'password' | 'number' | 'tel';
  placeholder?: string;
  value?: string;
  onChange?: (value: string) => void;
  error?: string;
  hint?: string;
  required?: boolean;
  disabled?: boolean;
  icon?: React.ReactNode;
  iconPosition?: 'start' | 'end';
  endContent?: React.ReactNode; // For password toggle, etc.
  size?: 'small' | 'medium' | 'large';
}
```

### 4.2 Specifications

**Layout:**
```
┌─────────────────────────────────────┐
│ Label *                              │ ← 14px medium, #1F2937
├─────────────────────────────────────┤
│                                      │
│ [Icon] Placeholder text      [End]  │ ← 48px height (mobile)
│                                      │
└─────────────────────────────────────┘
  Error message                         ← 12px, #C5221F
```

**States:**
| State | Border | Background |
|-------|--------|------------|
| default | `1px #D1D5DB` | `white` |
| hover | `1px #9CA3AF` | `white` |
| focus | `2px #006DE2` | `white` |
| error | `1px #EA4335` | `#FCE8E6` |
| disabled | `1px #E5E7EB` | `#F7F8FA` |

### 4.3 Usage Examples

```tsx
// Basic email field
<FormField
  label="Email address"
  name="email"
  type="email"
  placeholder="name@example.com"
  error={errors.email?.message}
/>

// Password with toggle
<FormField
  label="Password"
  name="password"
  type={showPassword ? 'text' : 'password'}
  placeholder="Enter your password"
  endContent={
    <IconButton onClick={togglePassword}>
      {showPassword ? <EyeOff24Regular /> : <Eye24Regular />}
    </IconButton>
  }
/>

// Amount input with currency
<FormField
  label="Amount"
  name="amount"
  type="number"
  icon={<Text>$</Text>}
  placeholder="0.00"
/>
```

---

## 5. Avatar Component

### 5.1 Interface

```typescript
// src/components/common/Avatar.types.ts
export type AvatarSize = 'sm' | 'md' | 'lg' | 'xl' | '2xl';
export type AvatarVariant = 'primary' | 'success' | 'warning' | 'info' | 'neutral';

export interface AvatarProps {
  initials?: string;
  name?: string; // Will extract initials from name
  src?: string;  // Image URL
  size?: AvatarSize;
  variant?: AvatarVariant;
  className?: string;
}
```

### 5.2 Specifications

| Size | Dimensions | Font Size | Use Case |
|------|------------|-----------|----------|
| sm | 32px | 12px | Navigation, small contexts |
| md | 40px | 14px | User menu, lists |
| lg | 48px | 18px | Recipients, accounts |
| xl | 64px | 24px | Profile section |
| 2xl | 72px | 28px | Transaction detail |

| Variant | Background | Text Color |
|---------|------------|------------|
| primary | `#E6F0FC` | `#006DE2` |
| success | `#E6F4EA` | `#34A853` |
| warning | `#FEF3E2` | `#F59E0B` |
| info | `#E0F2FE` | `#0EA5E9` |
| neutral | `#F3F4F6` | `#6B7280` |

### 5.3 Usage Examples

```tsx
// From name
<Avatar name="John Doe" size="lg" />  // Shows "JD"

// With image
<Avatar src="/avatar.jpg" name="John Doe" size="xl" />

// Colored variant
<Avatar initials="AB" variant="success" size="md" />
```

---

## 6. Badge Component

### 6.1 Interface

```typescript
// src/components/common/Badge.types.ts
export type BadgeVariant = 'primary' | 'success' | 'error' | 'warning' | 'info' | 'neutral';
export type BadgeSize = 'small' | 'medium';

export interface BadgeProps {
  variant?: BadgeVariant;
  size?: BadgeSize;
  dot?: boolean;      // Show status dot
  icon?: React.ReactNode;
  children: React.ReactNode;
}
```

### 6.2 Specifications

| Variant | Background | Text Color |
|---------|------------|------------|
| primary | `#E6F0FC` | `#006DE2` |
| success | `#E6F4EA` | `#137333` |
| error | `#FCE8E6` | `#C5221F` |
| warning | `#FEF3E2` | `#B45309` |
| info | `#E0F2FE` | `#0369A1` |
| neutral | `#F3F4F6` | `#6B7280` |

| Size | Padding | Font Size | Border Radius |
|------|---------|-----------|---------------|
| small | 4px 8px | 11px | 10px |
| medium | 4px 10px | 12px | 12px |

### 6.3 Usage Examples

```tsx
// Status badge with dot
<Badge variant="success" dot>Completed</Badge>

// Transaction type badge
<Badge variant="error">Withdrawal</Badge>

// Pending status
<Badge variant="warning" icon={<Clock16Regular />}>
  Pending
</Badge>
```

---

## 7. IconContainer Component

### 7.1 Interface

```typescript
// src/components/common/IconContainer.types.ts
export type IconContainerVariant = 'deposit' | 'withdrawal' | 'transfer-out' | 'transfer-in' | 'primary' | 'neutral';
export type IconContainerSize = 'sm' | 'md' | 'lg';

export interface IconContainerProps {
  variant: IconContainerVariant;
  size?: IconContainerSize;
  icon: React.ReactNode;
  gradient?: boolean;
  className?: string;
}
```

### 7.2 Specifications

| Variant | Background | Icon Color | Use Case |
|---------|------------|------------|----------|
| deposit | `#E6F4EA` (gradient optional) | `#34A853` | Deposit transactions |
| withdrawal | `#FCE8E6` (gradient optional) | `#EA4335` | Withdrawal transactions |
| transfer-out | `#FEF3E2` | `#F59E0B` | Outgoing transfers |
| transfer-in | `#E0F2FE` | `#0EA5E9` | Incoming transfers |
| primary | `#E6F0FC` | `#006DE2` | General actions |
| neutral | `#F3F4F6` | `#6B7280` | Settings, misc |

| Size | Container | Icon | Border Radius |
|------|-----------|------|---------------|
| sm | 40px | 20px | 10px |
| md | 44px | 22px | 11px |
| lg | 72px | 36px | 50% (circle) |

### 7.3 Usage Examples

```tsx
// Transaction list item
<IconContainer variant="deposit" size="md">
  <ArrowDown24Regular />
</IconContainer>

// Detail page (large)
<IconContainer variant="withdrawal" size="lg" gradient>
  <ArrowUp24Regular />
</IconContainer>

// Quick action
<IconContainer variant="primary" size="md">
  <Send24Regular />
</IconContainer>
```

---

## 8. Divider Component

### 8.1 Interface

```typescript
// src/components/common/Divider.types.ts
export interface DividerProps {
  text?: string;
  spacing?: 'small' | 'medium' | 'large';
  thickness?: 'thin' | 'thick';
  color?: 'light' | 'medium' | 'dark';
}
```

### 8.2 Specifications

| Spacing | Margin (vertical) |
|---------|------------------|
| small | 16px |
| medium | 24px |
| large | 32px |

| Thickness | Height |
|-----------|--------|
| thin | 1px |
| thick | 8px (for section dividers) |

### 8.3 Usage Examples

```tsx
// Simple line
<Divider />

// With text
<Divider text="or" spacing="medium" />

// Section divider (thick)
<Divider thickness="thick" color="light" />
```

---

## 9. BottomSheet Component

### 9.1 Interface

```typescript
// src/components/common/BottomSheet.types.ts
export interface BottomSheetProps {
  isOpen: boolean;
  onClose: () => void;
  title?: string;
  subtitle?: string;
  showHandle?: boolean;
  showCloseButton?: boolean;
  children: React.ReactNode;
  footer?: React.ReactNode;
}
```

### 9.2 Specifications

```
┌────────────────────────────────────────────┐
│            ────────                         │ ← Handle: 40×4px, #D1D5DB
├────────────────────────────────────────────┤
│ Dialog Title                           ✕   │ ← Header: 56px
│ Optional subtitle                           │
├────────────────────────────────────────────┤
│                                             │
│              Content Area                   │ ← Scrollable
│                                             │
├────────────────────────────────────────────┤
│           [ Primary Button ]                │ ← Footer: fixed
│           [ Secondary Button ]              │
└────────────────────────────────────────────┘

Container:
- Width: 100% (375px viewport)
- Border radius: 24px 24px 0 0
- Padding: 12px 16px 32px
- Background: white
```

### 9.3 Usage Examples

```tsx
<BottomSheet
  isOpen={isDepositOpen}
  onClose={() => setIsDepositOpen(false)}
  title="Deposit Funds"
  subtitle="Add money to your account"
>
  <DepositForm onSubmit={handleDeposit} />
</BottomSheet>
```

---

## 10. TransactionItem Component

### 10.1 Interface

```typescript
// src/components/common/TransactionItem.types.ts
export type TransactionType = 'deposit' | 'withdrawal' | 'transfer-in' | 'transfer-out';

export interface TransactionItemProps {
  id: string;
  type: TransactionType;
  amount: number;
  description: string;
  date: Date;
  recipient?: string;
  showArrow?: boolean;
  onClick?: (id: string) => void;
}
```

### 10.2 Specifications

```
┌────────────────────────────────────────────────────────────┐
│ ┌──────┐                                                   │
│ │ Icon │  Description              +$500.00   >            │
│ │      │  Dec 28, 2025            Deposit                  │
│ └──────┘                                                   │
└────────────────────────────────────────────────────────────┘

Layout:
- Height: auto (typically 72px)
- Padding: 16px
- Icon container: 44×44px, 10px radius
- Gap between icon and text: 12px
- Border bottom: 1px #F3F4F6

Amount colors:
- Positive (deposit, transfer-in): #34A853
- Negative (withdrawal, transfer-out): #EA4335
```

### 10.3 Usage Examples

```tsx
<TransactionItem
  id="txn-001"
  type="deposit"
  amount={500}
  description="Salary Payment"
  date={new Date('2025-12-28')}
  onClick={handleViewDetail}
/>
```

---

## 11. AmountInput Component

### 11.1 Interface

```typescript
// src/components/common/AmountInput.types.ts
export interface AmountInputProps {
  value: number;
  onChange: (value: number) => void;
  currency?: string;
  max?: number;
  min?: number;
  error?: string;
  quickAmounts?: number[];
  size?: 'medium' | 'large';
  accentColor?: 'primary' | 'success' | 'danger';
}
```

### 11.2 Specifications

**Large Display Mode:**
```
           $500.00
           ↑
    48px font, 700 weight
    Currency: 48px light
    Decimals: 32px gray
```

**Quick Amount Buttons:**
```
┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐
│  $25   │ │  $50   │ │  $100  │ │  $200  │
└────────┘ └────────┘ └────────┘ └────────┘
  70×36px, 8px radius, #F3F4F6 bg
  Selected: #E6F0FC bg, #006DE2 text
```

### 11.3 Usage Examples

```tsx
<AmountInput
  value={amount}
  onChange={setAmount}
  currency="$"
  max={availableBalance}
  quickAmounts={[25, 50, 100, 200]}
  accentColor="success"
/>
```

---

## 12. QuickActionButton Component

### 12.1 Interface

```typescript
// src/components/common/QuickActionButton.types.ts
export type QuickActionVariant = 'deposit' | 'withdraw' | 'transfer' | 'default';

export interface QuickActionButtonProps {
  variant: QuickActionVariant;
  label: string;
  icon: React.ReactNode;
  onClick: () => void;
  highlighted?: boolean;
}
```

### 12.2 Specifications

```
┌────────────────────────┐
│       ┌──────┐         │
│       │ Icon │         │  ← 40×40px, 10px radius
│       └──────┘         │
│        Label           │  ← 12px medium
└────────────────────────┘
  Height: 80px
  Background: white
  Border radius: 12px
  Shadow: card shadow

Highlighted (primary action):
  Background: #006DE2
  Icon/text: white
```

### 12.3 Usage Examples

```tsx
<div className={styles.quickActions}>
  <QuickActionButton
    variant="deposit"
    label="Deposit"
    icon={<ArrowDown24Regular />}
    onClick={handleDeposit}
  />
  <QuickActionButton
    variant="withdraw"
    label="Withdraw"
    icon={<ArrowUp24Regular />}
    onClick={handleWithdraw}
  />
  <QuickActionButton
    variant="transfer"
    label="Transfer"
    icon={<Send24Regular />}
    onClick={handleTransfer}
    highlighted
  />
</div>
```

---

## 13. Layout Components

### 13.1 AppLayout

**Structure:**
```
┌─────────────────────────────────────────────────────────────┐
│                         Header                               │ 56px mobile / 64px desktop
├─────────────────────────────────────────────────────────────┤
│                                                              │
│                      Main Content                            │
│                    (children prop)                           │
│                                                              │
│                                                              │
├─────────────────────────────────────────────────────────────┤
│                    BottomNav (mobile)                        │ 72px + safe area
└─────────────────────────────────────────────────────────────┘

Desktop: Sidebar (240px) + Main Content (flex 1)
```

### 13.2 Header

**Mobile (56px):**
```
┌─────────────────────────────────────────┐
│ ☰  AzureBank                        🔔  │
└─────────────────────────────────────────┘
```

**Desktop (64px):**
```
┌─────────────────────────────────────────────────────────────────┐
│ [Logo] AzureBank    Dashboard | Accounts | History    [Avatar] │
└─────────────────────────────────────────────────────────────────┘
```

### 13.3 BottomNav

```
┌────────────────────────────────────────────────────────────────┐
│   🏠       📊       💸       📋       ⚙️                        │
│  Home   Accounts  Transfer History  Settings                   │
└────────────────────────────────────────────────────────────────┘

Item specs:
- Icon: 24×24px
- Label: 10px medium
- Active: #006DE2
- Inactive: #6B7280
- Height: 72px (+ 20px safe area padding)
```

---

## 14. Responsive Utilities

### 14.1 useResponsive Hook

```typescript
// src/hooks/useResponsive.ts
export function useResponsive() {
  const isMobile = useMediaQuery('(max-width: 767px)');
  const isTablet = useMediaQuery('(min-width: 768px) and (max-width: 1023px)');
  const isDesktop = useMediaQuery('(min-width: 1024px)');

  return {
    isMobile,
    isTablet,
    isDesktop,
    isTouch: isMobile || isTablet,
  };
}
```

### 14.2 makeStyles Pattern

```typescript
import { makeStyles, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  container: {
    padding: '16px',
    '@media (min-width: 1024px)': {
      padding: '32px',
    },
  },

  card: {
    width: '100%',
    '@media (min-width: 1024px)': {
      maxWidth: '440px',
    },
  },
});
```

### 14.3 Component Size Variants

```typescript
// In component props
interface ComponentProps {
  size?: 'mobile' | 'desktop'; // Or use useResponsive internally
}

// Internal implementation
const height = useResponsive().isDesktop
  ? componentSizes.buttonHeightDesktop
  : componentSizes.buttonHeightMobile;
```

---

## Implementation Checklist

### Core Components
- [ ] `tokens.ts` - Design token constants
- [ ] `Button.tsx` - All variants, sizes, states
- [ ] `Card.tsx` - All variants, padding options
- [ ] `FormField.tsx` - With FluentUI Field wrapper
- [ ] `Avatar.tsx` - Initials extraction, image fallback
- [ ] `Badge.tsx` - Status indicators
- [ ] `IconContainer.tsx` - Transaction type icons
- [ ] `Divider.tsx` - Simple and with text

### Layout Components
- [ ] `AppLayout.tsx` - Main wrapper
- [ ] `Header.tsx` - Mobile and desktop variants
- [ ] `BottomNav.tsx` - Mobile navigation
- [ ] `Sidebar.tsx` - Desktop navigation

### Feature Components
- [ ] `TransactionItem.tsx` - List item
- [ ] `TransactionList.tsx` - Grouped by date
- [ ] `BottomSheet.tsx` - Modal dialogs
- [ ] `AmountInput.tsx` - Currency input with quick amounts
- [ ] `QuickActionButton.tsx` - Dashboard actions
- [ ] `AccountCard.tsx` - Account display

### Hooks
- [ ] `useResponsive.ts` - Media query hook
- [ ] `useThemeTokens.ts` - Access design tokens

---

**Document Version**: 1.0
**Last Updated**: 2026-01-02
**Status**: Implementation Ready
