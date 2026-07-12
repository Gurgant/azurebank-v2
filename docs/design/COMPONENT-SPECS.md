# AzureBank Component Specifications

> **For: Other Claude Code Implementation Session**
> **Generated from**: 27 HTML reference files in `/figma-html/`
> **Target Framework**: React 19 + FluentUI v9 + TypeScript

---

## Table of Contents
1. [Design System Foundations](#1-design-system-foundations)
2. [Shared Components](#2-shared-components)
3. [Auth Screens](#3-auth-screens)
4. [Dashboard](#4-dashboard)
5. [Transfer Wizard](#5-transfer-wizard)
6. [Dialogs](#6-dialogs)
7. [History & Transactions](#7-history--transactions)
8. [Accounts](#8-accounts)
9. [Settings](#9-settings)
10. [Responsive Patterns](#10-responsive-patterns)

---

## 1. Design System Foundations

### Screen Dimensions
| Viewport | Width | Height | Use Case |
|----------|-------|--------|----------|
| Mobile | 375px | 812px | iPhone 13/14 |
| Desktop | 1440px | 900px | Standard desktop |

### Color Palette

#### Brand Colors
```typescript
const brandColors = {
  primary: '#006DE2',      // Main CTA, links, active states
  primaryHover: '#004DA0', // Button hover
  primaryLight: '#E6F0FC', // Selected backgrounds
  primaryLighter: '#F0F6FE', // Info backgrounds
};
```

#### Semantic Colors
```typescript
const semanticColors = {
  success: {
    bg: '#E6F4EA',
    icon: '#34A853',
    text: '#137333',
  },
  error: {
    bg: '#FCE8E6',
    icon: '#EA4335',
    text: '#C5221F',
  },
  warning: {
    bg: '#FEF3E2',
    icon: '#F59E0B',
    text: '#B45309',
  },
  info: {
    bg: '#E0F2FE',
    icon: '#0EA5E9',
    text: '#0369A1',
  },
};
```

#### Neutral Colors
```typescript
const neutralColors = {
  gray900: '#1F2937', // Primary text
  gray700: '#6B7280', // Secondary text
  gray500: '#9CA3AF', // Placeholder, disabled
  gray300: '#D1D5DB', // Borders
  gray200: '#E5E7EB', // Dividers
  gray100: '#F3F4F6', // Backgrounds, badges
  gray50: '#F7F8FA',  // Page background
  white: '#FFFFFF',
};
```

### Typography

```typescript
const typography = {
  fontFamily: "'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif",
  fontMono: "Consolas, 'Courier New', monospace",

  sizes: {
    h1: { size: '48px', weight: 700 },      // Desktop branding
    h2: { size: '32px', weight: 700 },      // Mobile balance
    h3: { size: '28px', weight: 700 },      // Page titles
    h4: { size: '24px', weight: 600 },      // Section titles
    h5: { size: '20px', weight: 600 },      // Dialog titles
    h6: { size: '18px', weight: 600 },      // Card titles
    bodyLg: { size: '16px', weight: 400 },  // Body text
    body: { size: '15px', weight: 400 },    // Default text
    bodySm: { size: '14px', weight: 400 },  // Secondary text
    caption: { size: '13px', weight: 400 }, // Small text
    tiny: { size: '12px', weight: 400 },    // Labels
    micro: { size: '10px', weight: 500 },   // Nav labels
  },
};
```

### Spacing System
```typescript
const spacing = {
  xs: '4px',
  sm: '8px',
  md: '12px',
  lg: '16px',
  xl: '20px',
  '2xl': '24px',
  '3xl': '32px',
  '4xl': '48px',
  '5xl': '64px',
};
```

### Border Radius
```typescript
const borderRadius = {
  sm: '2px',
  md: '8px',
  lg: '12px',
  xl: '16px',
  '2xl': '24px',
  full: '9999px', // Pills, avatars
};
```

### Shadows
```typescript
const shadows = {
  card: '0px 8px 24px rgba(0, 0, 0, 0.1)',
  cardSm: '0px 2px 8px rgba(0, 0, 0, 0.08)',
  cardMd: '0px 4px 6px -1px rgba(0, 0, 0, 0.1)',
  cardLg: '0px 10px 15px -3px rgba(0, 0, 0, 0.1)',
};
```

---

## 2. Shared Components

### 2.1 Button Variants

#### Primary Button
```css
/* Mobile */
.btn-primary {
  width: 100%;
  height: 48px; /* Mobile */
  background: #006DE2;
  border-radius: 8px;
  font-size: 16px;
  font-weight: 600;
  color: #FFFFFF;
}

/* Desktop */
.btn-primary-desktop {
  height: 52px;
}

/* Hover State */
.btn-primary:hover {
  background: #004DA0;
}
```

#### Secondary Button
```css
.btn-secondary {
  height: 48px;
  background: #FFFFFF;
  border: 1px solid #D1D5DB;
  border-radius: 8px;
  font-size: 16px;
  font-weight: 600;
  color: #1F2937;
}
```

#### Danger Button (Logout, Withdraw)
```css
.btn-danger {
  background: #EA4335;
  /* OR outlined: */
  background: transparent;
  border: 1px solid #EA4335;
  color: #EA4335;
}
```

#### Success Button (Deposit)
```css
.btn-success {
  background: #34A853;
}
```

### 2.2 Form Field

```tsx
interface FormFieldProps {
  label: string;
  placeholder?: string;
  type?: 'text' | 'password' | 'email';
  error?: string;
  value: string;
  onChange: (value: string) => void;
}

/* Mobile Specs */
.form-field {
  gap: 6px; /* Label to input */
}

.form-label {
  font-size: 14px;
  font-weight: 500;
  color: #1F2937;
}

.form-input {
  width: 100%;
  height: 48px; /* Mobile: 44-48px */
  background: #FFFFFF;
  border: 1px solid #D1D5DB;
  border-radius: 8px;
  padding: 0 16px;
  font-size: 16px;
}

/* Desktop: 52px height */
/* Focused: border 2px #006DE2 */
/* Error: border 1px #EA4335 */
```

### 2.3 Card Component

```css
.card {
  background: #FFFFFF;
  border-radius: 16px;
  box-shadow: 0px 8px 24px rgba(0, 0, 0, 0.1);
  padding: 24px; /* Mobile: 24px, Desktop: 48px */
}

/* Card widths */
/* Mobile: 343px (375 - 32px padding) */
/* Desktop: 440-480px */
```

### 2.4 Avatar Component

```tsx
interface AvatarProps {
  initials: string;
  size?: 'sm' | 'md' | 'lg' | 'xl';
  variant?: 'primary' | 'success' | 'warning';
}

/* Sizes */
const avatarSizes = {
  sm: '32px',   // Nav, small contexts
  md: '40px',   // User menu
  lg: '48px',   // Recipients, accounts
  xl: '64px',   // Profile section
  '2xl': '72px', // Transaction detail
};

/* Default style */
.avatar {
  background: #E6F0FC;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
}

.avatar-initials {
  font-weight: 600;
  color: #006DE2;
}
```

### 2.5 Badge/Pill Component

```tsx
interface BadgeProps {
  variant: 'primary' | 'success' | 'error' | 'warning' | 'neutral';
  children: React.ReactNode;
}

/* Styles */
.badge {
  padding: 4px 10px;
  border-radius: 12px;
  font-size: 12px;
  font-weight: 500;
}

.badge-success { background: #E6F4EA; color: #137333; }
.badge-error { background: #FCE8E6; color: #C5221F; }
.badge-warning { background: #FEF3E2; color: #B45309; }
.badge-primary { background: #E6F0FC; color: #006DE2; }
.badge-neutral { background: #F3F4F6; color: #6B7280; }
```

### 2.6 Icon Container

```tsx
interface IconContainerProps {
  variant: 'deposit' | 'withdrawal' | 'transfer-out' | 'transfer-in';
  size?: 'sm' | 'md' | 'lg';
}

/* Transaction icon backgrounds */
const iconContainers = {
  deposit: { bg: '#E6F4EA', color: '#34A853' },
  withdrawal: { bg: '#FCE8E6', color: '#EA4335' },
  'transfer-out': { bg: '#FEF3E2', color: '#F59E0B' },
  'transfer-in': { bg: '#E0F2FE', color: '#0EA5E9' },
};

/* Sizes */
const iconSizes = {
  sm: { container: '40px', icon: '20px' },  // Table rows
  md: { container: '44px', icon: '22px' },  // Transaction list
  lg: { container: '72px', icon: '36px' },  // Detail pages
};
```

---

## 3. Auth Screens

### 3.1 LoginPage

**Mobile Layout (375x812)**
```
Screen: bg #F7F8FA, padding 80px 16px 40px
└── LoginCard: 343px, bg white, radius 16px, shadow, padding 40px 24px
    ├── LogoSection: gap 8px
    │   ├── Logo: "AzureBank", 32px bold, #006DE2
    │   └── Tagline: 14px, #6B7280
    ├── HeaderSection: gap 4px
    │   ├── Title: "Welcome Back", 24px semibold
    │   └── Subtitle: 14px, #6B7280
    ├── FormSection: gap 16px
    │   ├── EmailField: label + input (48px height)
    │   ├── PasswordField: label + input + eye icon
    │   └── ForgotPassword: 14px, #006DE2, right-aligned
    └── ActionSection: gap 16px
        ├── SignInButton: 52px height
        ├── Divider: line + "or" + line
        └── RegisterLink: "Don't have an account? Register"
```

**Desktop Layout (1440x900)**
```
Screen: flex row
├── BrandingPanel: 600px, gradient #006DE2→#004DA0, padding 64px
│   └── BrandingContent: max-width 400px, gap 32px
│       ├── Logo: 48px bold, white
│       ├── Tagline: 24px, white 90% opacity
│       └── Features: icon (48x48, 12px radius) + text
└── LoginPanel: flex 1, centered
    └── LoginCard: 440px, radius 16px, shadow, padding 48px
        ├── Title: 28px bold
        ├── Form: inputs 52px height, gap 20px
        └── Actions: gap 20px
```

### 3.2 RegisterPage

**Mobile additions:**
- Name row: FirstName + LastName side-by-side
- Input height: 44px
- Password requirements box: bg #F9FAFB, radius 8px, padding 12px

**Desktop additions:**
- Benefits list with checkmarks (#34A853)
- Card width: 480px
- Terms text below button

---

## 4. Dashboard

### 4.1 DashboardMobile

```
Screen: 375x812, bg #F7F8FA
├── Header: 56px, white, border-bottom
│   ├── MenuIcon: 24px
│   ├── Logo: "AzureBank", 20px semibold
│   └── NotificationIcon: 24px
├── GreetingSection: padding 16px
│   ├── Greeting: "Good Morning,", 14px #6B7280
│   └── UserName: "John", 24px bold
├── BalanceCard: margin 0 16px, gradient, radius 16px
│   ├── Label: "Total Balance", 14px, white 80%
│   ├── Amount: "$12,450.00", 32px bold, white
│   └── AccountInfo: pill badge, masked number
├── QuickActions: row of 3 buttons
│   ├── Deposit: icon #34A853
│   ├── Withdraw: icon #EA4335
│   └── Transfer: icon #006DE2, primary style
├── TransactionsSection:
│   ├── Header: "Recent" + "See All"
│   └── TransactionList: items 16px padding, 12px gap
└── BottomNav: 72px height, 5 items
```

### 4.2 DashboardDesktop

```
Screen: 1440x900, bg #F7F8FA
├── Header: 64px
│   ├── Logo: 24px
│   ├── NavMenu: Dashboard|Accounts|Transactions|Transfers
│   └── UserMenu: avatar + name
├── MainContent: padding 32px, flex
│   ├── LeftColumn: max-width 800px
│   │   ├── GreetingSection: 28px title
│   │   ├── BalanceCardsRow: flex, gap 24px
│   │   │   ├── PrimaryBalanceCard: gradient, 280px
│   │   │   └── SecondaryCards: white, border
│   │   ├── QuickActionsRow: 3 buttons
│   │   └── TransactionsSection: card with table
│   └── RightColumn: 360px sidebar
│       ├── MonthlySummary: donut chart placeholder
│       ├── AccountsOverview: list
│       └── HelpCard: support info
```

### 4.3 Key Components

**BalanceCard (Primary)**
```css
.balance-card {
  background: linear-gradient(135deg, #006DE2 0%, #004DA0 100%);
  border-radius: 16px;
  padding: 24px;
}
```

**QuickActionButton**
```css
.quick-action {
  flex: 1;
  height: 80px; /* or 56px compact */
  background: #FFFFFF;
  border-radius: 12px;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
}

.quick-action-icon {
  width: 40px;
  height: 40px;
  border-radius: 10px;
}
```

**TransactionItem**
```css
.transaction-item {
  display: flex;
  align-items: center;
  padding: 16px;
  gap: 12px;
  border-bottom: 1px solid #F3F4F6;
}
```

---

## 5. Transfer Wizard

### 5.1 Progress Indicator

```tsx
interface TransferProgressProps {
  currentStep: 1 | 2 | 3 | 4;
}

/* Specs */
.progress-indicator {
  height: 60px;
  background: #FFFFFF;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px 24px;
}

.step-circle {
  width: 32px;
  height: 32px;
  border-radius: 50%;
  font-size: 14px;
  font-weight: 600;
}

.step-active { background: #006DE2; color: white; }
.step-completed { background: #006DE2; color: white; }
.step-inactive { background: #E5E7EB; color: #9CA3AF; }

.step-line {
  width: 60px;
  height: 2px;
  background: #E5E7EB;
}
.step-line-completed { background: #006DE2; }
```

### 5.2 Step 1: Source Account Selection

```tsx
interface AccountSelectorProps {
  accounts: Account[];
  selectedId: string;
  onSelect: (id: string) => void;
}

/* AccountCard */
.account-card {
  height: 80px;
  background: #FFFFFF;
  border: 1px solid #E5E7EB;
  border-radius: 12px;
  padding: 16px;
  display: flex;
  align-items: center;
  gap: 12px;
}

.account-card-selected {
  border: 2px solid #006DE2;
  background: #F0F6FE;
}
```

### 5.3 Step 2: Destination Selection

**RecipientCard**
```css
.recipient-card {
  background: #FFFFFF;
  border: 1px solid #E5E7EB;
  border-radius: 12px;
  padding: 16px;
  display: flex;
  align-items: center;
  gap: 12px;
}

.recipient-avatar {
  width: 48px;
  height: 48px;
  border-radius: 50%;
  background: #E6F0FC;
}

.recipient-account {
  font-family: monospace;
  color: #6B7280;
}
```

**NewRecipientCard**
```css
.new-recipient-card {
  border: 1px dashed #D1D5DB;
  /* Plus icon in circle */
}
```

### 5.4 Step 3: Amount Entry

**AmountDisplay**
```css
.amount-display {
  display: flex;
  align-items: baseline;
}

.amount-currency {
  font-size: 48px;
  font-weight: 300;
  color: #1F2937;
}

.amount-value {
  font-size: 48px;
  font-weight: 700;
}

.amount-decimal {
  font-size: 32px;
  color: #6B7280;
}
```

**QuickAmountButton**
```css
.quick-btn {
  width: 70px;
  height: 36px;
  background: #F3F4F6;
  border-radius: 8px;
}

.quick-btn-selected {
  background: #E6F0FC;
  color: #006DE2;
}
```

### 5.5 Step 4: Confirmation

**TransferSummaryCard**
```css
.summary-card {
  background: #FFFFFF;
  border-radius: 16px;
  box-shadow: card shadow;
  padding: 24px;
}

.transfer-flow {
  display: flex;
  align-items: center;
  justify-content: space-between;
  /* From Avatar → Arrow → To Avatar */
}

.flow-arrow {
  width: 40px;
  height: 40px;
  color: #006DE2;
}
```

### 5.6 Success State

```css
.success-icon-container {
  width: 120px;
  height: 120px;
  background: #E6F4EA;
  border-radius: 50%;
  /* Checkmark icon 48px, #34A853 */
}

.success-title {
  font-size: 24px;
  font-weight: 700;
  color: #137333;
}

.reference-number {
  font-family: monospace;
  background: #F3F4F6;
  padding: 8px 16px;
  border-radius: 8px;
}
```

---

## 6. Dialogs

### 6.1 Bottom Sheet (Mobile)

```css
.dialog {
  width: 375px;
  background: #FFFFFF;
  border-radius: 24px 24px 0 0;
  padding: 12px 16px 32px 16px;
}

.dialog-handle {
  width: 40px;
  height: 4px;
  background: #D1D5DB;
  border-radius: 2px;
  margin: 0 auto 8px;
}

.dialog-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.dialog-close {
  width: 32px;
  height: 32px;
  background: #F3F4F6;
  border-radius: 50%;
}
```

### 6.2 Deposit Dialog

```tsx
interface DepositDialogProps {
  account: Account;
  onDeposit: (amount: number, description?: string) => void;
  onClose: () => void;
}

/* Icon */
.deposit-icon-container {
  width: 64px;
  height: 64px;
  background: #E6F4EA;
  /* Arrow down icon, #34A853 */
}

/* Amount input with focus */
.amount-input {
  height: 64px;
  border: 2px solid #006DE2; /* Active state */
  border-radius: 12px;
  font-size: 24px;
  font-weight: 600;
}

/* Quick amounts row */
.quick-amounts {
  display: flex;
  gap: 8px;
}

.quick-amount-btn {
  flex: 1;
  height: 40px;
  background: #F3F4F6;
  border-radius: 8px;
}

/* Primary button - GREEN for deposit */
.btn-deposit {
  background: #34A853;
}
```

### 6.3 Withdraw Dialog

Same as deposit with:
- Icon: Arrow up, bg #FCE8E6, icon #EA4335
- Amount border: 2px solid #EA4335
- Warning notice box
- Primary button: bg #EA4335

**Warning Notice**
```css
.warning-notice {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px;
  background: #FEF3E2;
  border-radius: 8px;
}

.warning-icon { color: #F59E0B; }
.warning-text { color: #B45309; }
```

### 6.4 Account Selector

```css
.account-selector {
  width: 100%;
  height: 64px;
  background: #FFFFFF;
  border: 1px solid #D1D5DB;
  border-radius: 12px;
  padding: 12px 16px;
  display: flex;
  justify-content: space-between;
  align-items: center;
}
```

---

## 7. History & Transactions

### 7.1 HistoryMobile

**FilterTabs**
```css
.filter-tabs {
  display: flex;
  padding: 12px 16px;
  gap: 8px;
  overflow-x: auto;
}

.filter-tab {
  height: 36px;
  padding: 0 16px;
  background: #F3F4F6;
  border-radius: 18px; /* Pill */
}

.filter-tab-active {
  background: #006DE2;
  color: white;
}
```

**SummaryCard**
```css
.summary-card {
  margin: 16px;
  padding: 16px;
  background: linear-gradient(135deg, #006DE2 0%, #004DA0 100%);
  border-radius: 12px;
  display: flex;
  justify-content: space-around;
}

.summary-value-positive { color: #86EFAC; }
.summary-value-negative { color: #FCA5A5; }
```

**DateHeader**
```css
.date-header {
  padding: 16px;
  background: #F7F8FA;
  font-size: 14px;
  font-weight: 600;
  color: #6B7280;
}
```

### 7.2 HistoryDesktop

**PageHeader**
```css
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.page-title { font-size: 28px; font-weight: 700; }
.page-subtitle { font-size: 16px; color: #6B7280; }
```

**SummaryCardsRow**
```css
.summary-cards {
  display: flex;
  gap: 24px;
}

.summary-card {
  flex: 1;
  padding: 24px;
  background: #FFFFFF;
  border-radius: 12px;
}
```

**DataTable**
```css
.table-container {
  background: #FFFFFF;
  border-radius: 12px;
  overflow: hidden;
}

.table-header {
  display: flex;
  padding: 16px 24px;
  background: #F9FAFB;
  border-bottom: 1px solid #E5E7EB;
}

.th {
  font-size: 12px;
  font-weight: 600;
  color: #6B7280;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.table-row {
  display: flex;
  padding: 16px 24px;
  border-bottom: 1px solid #F3F4F6;
}

.table-row:hover {
  background: #F9FAFB;
}
```

**Pagination**
```css
.pagination {
  display: flex;
  justify-content: space-between;
  padding: 16px 24px;
}

.page-btn {
  width: 36px;
  height: 36px;
  border: 1px solid #D1D5DB;
  border-radius: 8px;
}

.page-btn-active {
  background: #006DE2;
  border-color: #006DE2;
  color: white;
}
```

### 7.3 TransactionDetail

**HeaderCard**
```css
.transaction-header {
  background: #FFFFFF;
  border-radius: 16px;
  padding: 24px;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 16px;
}

.transaction-amount {
  font-size: 36px;
  font-weight: 700;
}

.status-badge {
  padding: 8px 16px;
  background: #E6F4EA;
  border-radius: 20px;
  display: flex;
  align-items: center;
  gap: 6px;
}

.status-dot {
  width: 8px;
  height: 8px;
  background: #34A853;
  border-radius: 50%;
}
```

**DetailsCard**
```css
.details-card {
  background: #FFFFFF;
  border-radius: 16px;
}

.details-section {
  padding: 16px;
}

.section-title {
  font-size: 12px;
  font-weight: 600;
  color: #6B7280;
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.detail-row {
  display: flex;
  justify-content: space-between;
  padding: 12px 0;
  border-bottom: 1px solid #F3F4F6;
}

.section-divider {
  height: 8px;
  background: #F7F8FA;
}
```

---

## 8. Accounts

### 8.1 AccountsMobile

**Header with gradient**
```css
.header {
  background: #006DE2;
  padding: 0 16px 24px;
}

.total-value {
  font-size: 32px;
  font-weight: 700;
  color: #FFFFFF;
}
```

**AccountCard**
```css
.account-card {
  background: #FFFFFF;
  border-radius: 16px;
  padding: 20px;
  box-shadow: 0px 2px 8px rgba(0, 0, 0, 0.08);
}

.account-icon-container {
  width: 48px;
  height: 48px;
  border-radius: 12px;
}

/* Type variants */
.checking { background: linear-gradient(135deg, #E6F0FC, #D1E4FC); }
.savings { background: linear-gradient(135deg, #E6F4EA, #D4EDDA); }
.investment { background: linear-gradient(135deg, #FEF3E2, #FDE9CC); }

.account-actions {
  display: flex;
  gap: 8px;
  padding-top: 12px;
  border-top: 1px solid #F3F4F6;
}

.account-action-btn {
  flex: 1;
  height: 40px;
  background: #F7F8FA;
  border-radius: 8px;
}
```

**AddAccountCard**
```css
.add-account-card {
  border: 2px dashed #D1D5DB;
  border-radius: 16px;
  padding: 24px;
  text-align: center;
}

.add-account-card:hover {
  border-color: #006DE2;
  background: #F0F7FF;
}
```

---

## 9. Settings

### 9.1 SettingsMobile

**ProfileSection**
```css
.profile-section {
  background: #FFFFFF;
  border-radius: 16px;
  padding: 20px;
  display: flex;
  align-items: center;
  gap: 16px;
}

.profile-avatar {
  width: 64px;
  height: 64px;
  background: linear-gradient(135deg, #E6F0FC, #D1E4FC);
  border-radius: 50%;
}
```

**SettingsGroup**
```css
.settings-group {
  background: #FFFFFF;
  border-radius: 16px;
  overflow: hidden;
}

.group-title {
  font-size: 12px;
  font-weight: 600;
  color: #6B7280;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  padding: 16px 16px 8px;
}
```

**SettingsItem**
```css
.settings-item {
  display: flex;
  align-items: center;
  padding: 16px;
  border-bottom: 1px solid #F3F4F6;
}

.item-icon-container {
  width: 40px;
  height: 40px;
  border-radius: 10px;
  margin-right: 12px;
}

/* Color variants */
.blue { background: #E6F0FC; color: #006DE2; }
.green { background: #E6F4EA; color: #34A853; }
.purple { background: #F3E8FF; color: #8B5CF6; }
.orange { background: #FEF3E2; color: #F59E0B; }
.red { background: #FCE8E6; color: #EA4335; }
```

**ToggleSwitch**
```css
.toggle-switch {
  width: 48px;
  height: 28px;
  background: #D1D5DB;
  border-radius: 14px;
  position: relative;
}

.toggle-switch.active {
  background: #006DE2;
}

.toggle-knob {
  width: 24px;
  height: 24px;
  background: #FFFFFF;
  border-radius: 50%;
  position: absolute;
  top: 2px;
  left: 2px;
  box-shadow: 0px 2px 4px rgba(0, 0, 0, 0.2);
  transition: transform 0.2s;
}

.toggle-switch.active .toggle-knob {
  transform: translateX(20px);
}
```

**LogoutButton**
```css
.logout-button {
  width: 100%;
  height: 52px;
  background: #FFFFFF;
  border: 1px solid #EA4335;
  border-radius: 12px;
  color: #EA4335;
}

.logout-button:hover {
  background: #FCE8E6;
}
```

---

## 10. Responsive Patterns

### 10.1 Mobile-First Approach

```tsx
// Use FluentUI's useMediaQuery or custom breakpoints
const breakpoints = {
  mobile: '(max-width: 767px)',
  tablet: '(min-width: 768px) and (max-width: 1023px)',
  desktop: '(min-width: 1024px)',
};
```

### 10.2 Component Size Variants

| Component | Mobile | Desktop |
|-----------|--------|---------|
| Card padding | 24px | 48px |
| Input height | 48px | 52px |
| Button height | 48px | 52px |
| Page title | 24px | 28px |
| Card width | 343px (full - 32px) | 440-480px |
| Border radius | 16px | 16px |

### 10.3 Layout Patterns

**Mobile**: Single column, bottom navigation
**Desktop**: Two-column (main + sidebar), top navigation

### 10.4 Navigation

**Mobile BottomNav**
```css
.bottom-nav {
  height: 72px; /* or 83px with safe area */
  background: #FFFFFF;
  border-top: 1px solid #E5E7EB;
  padding-bottom: 20px; /* Safe area */
}

.nav-item {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
}

.nav-icon { width: 24px; height: 24px; }
.nav-label { font-size: 10px; font-weight: 500; }

.nav-item-active {
  .nav-icon { color: #006DE2; }
  .nav-label { color: #006DE2; }
}
```

**Desktop TopNav**
```css
.header {
  height: 64px;
  background: #FFFFFF;
  border-bottom: 1px solid #E5E7EB;
  padding: 0 32px;
}

.nav-menu {
  display: flex;
  gap: 32px;
}

.nav-item {
  font-size: 14px;
  font-weight: 500;
  color: #6B7280;
}

.nav-item-active {
  color: #006DE2;
}
```

---

## FluentUI v9 Component Mapping

| Custom Component | FluentUI Equivalent |
|------------------|---------------------|
| Button Primary | `<Button appearance="primary">` |
| Button Secondary | `<Button appearance="outline">` |
| Input | `<Input>` with custom styles |
| Card | `<Card>` |
| Avatar | `<Avatar>` |
| Badge | `<Badge>` |
| Divider | `<Divider>` |
| Dialog | `<Dialog>` / `<DrawerBody>` |
| Spinner | `<Spinner>` |
| Tab | `<TabList>` / `<Tab>` |
| Table | `<Table>` / `<DataGrid>` |
| Toggle | `<Switch>` |
| Menu | `<Menu>` / `<MenuList>` |

---

**Document Version**: 1.0
**Last Updated**: 2026-01-02
**Generated by**: Claude Code (Figma Session)
