# Visual Specifications
## Bank Account Management System

**Document Version**: 5.0
**Created**: 2025-12-16
**Updated**: 2026-01-09
**Author**: Web Designer (Virtual Team Member)
**Status**: FINAL - Complete Microinteraction Specs

**Bank Name**: AzureBank

---

## 1. Overview

This document defines detailed visual design specifications for all UI components in the AzureBank application. All specifications align with FluentUI v9 design system and the design tokens defined in `04d-design-tokens.md`.

### Design Principles
1. **Consistency**: Uniform spacing, colors, and typography across all components
2. **Clarity**: Clear visual hierarchy guiding users through tasks
3. **Accessibility**: WCAG 2.1 AA compliance minimum
4. **Responsiveness**: Mobile-first approach with graceful desktop enhancement

---

## 2. Component Visual Specifications

### 2.1 Buttons

#### Primary Button
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Primary Button                                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Default State:                                                 │
│  ┌─────────────────────┐                                       │
│  │   Create Account    │  Background: #006DE2 (colorBrandBackground)
│  └─────────────────────┘  Text: #FFFFFF                        │
│                           Font: 14px/600 (fontSizeBase300/Semibold)
│                           Padding: 8px 16px                    │
│                           Border Radius: 4px (borderRadiusMedium)
│                           Min Width: 96px                       │
│                           Height: 32px                          │
│                                                                 │
│  Hover State:                                                   │
│  ┌─────────────────────┐                                       │
│  │   Create Account    │  Background: #004DA0 (colorBrandBackgroundHover)
│  └─────────────────────┘  Cursor: pointer                      │
│                           Transition: 150ms ease                │
│                                                                 │
│  Pressed State:                                                 │
│  ┌─────────────────────┐                                       │
│  │   Create Account    │  Background: #003D7F (colorBrandBackgroundPressed)
│  └─────────────────────┘  Transform: scale(0.98)               │
│                                                                 │
│  Disabled State:                                                │
│  ┌─────────────────────┐                                       │
│  │   Create Account    │  Background: #E0E0E0                  │
│  └─────────────────────┘  Text: #A0A0A0                        │
│                           Cursor: not-allowed                   │
│                                                                 │
│  Focus State:                                                   │
│  ┌─────────────────────┐                                       │
│  │   Create Account    │  Outline: 2px solid #000000           │
│  └─────────────────────┘  Outline Offset: 2px                  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Secondary Button (Outline)
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Secondary Button                                     │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Default State:                                                 │
│  ┌─────────────────────┐                                       │
│  │      Cancel         │  Background: transparent              │
│  └─────────────────────┘  Text: #006DE2 (colorBrandForeground1)│
│                           Border: 1px solid #006DE2            │
│                           Padding: 8px 16px                    │
│                           Border Radius: 4px                   │
│                                                                 │
│  Hover State:                                                   │
│  ┌─────────────────────┐                                       │
│  │      Cancel         │  Background: #F0F6FE (brand[130])     │
│  └─────────────────────┘                                       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Danger Button
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Danger Button                                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Default State:                                                 │
│  ┌─────────────────────┐                                       │
│  │   Delete Account    │  Background: #C5221F                  │
│  └─────────────────────┘  Text: #FFFFFF                        │
│                                                                 │
│  Hover State:                                                   │
│  ┌─────────────────────┐                                       │
│  │   Delete Account    │  Background: #A31B19                  │
│  └─────────────────────┘                                       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Icon Button
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Icon Button                                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Default:        Hover:          With Text:                     │
│  ┌─────┐        ┌─────┐         ┌───────────────┐              │
│  │  +  │        │  +  │         │  +  Add New   │              │
│  └─────┘        └─────┘         └───────────────┘              │
│  32x32px        Bg: #F5F5F5     Gap: 8px                       │
│  Icon: 20px     Border Radius   between icon                   │
│                 50% for circle  and text                        │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Button Sizes
```
┌─────────────────────────────────────────────────────────────────┐
│ BUTTON SIZES                                                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Small:                                                         │
│  ┌──────────┐   Height: 24px, Padding: 4px 8px                 │
│  │  Small   │   Font: 12px                                     │
│  └──────────┘                                                   │
│                                                                 │
│  Medium (Default):                                              │
│  ┌─────────────┐  Height: 32px, Padding: 8px 16px              │
│  │   Medium    │  Font: 14px                                   │
│  └─────────────┘                                                │
│                                                                 │
│  Large:                                                         │
│  ┌────────────────┐  Height: 40px, Padding: 12px 24px          │
│  │     Large      │  Font: 16px                                │
│  └────────────────┘                                             │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

### 2.2 Forms

#### Text Input Field
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Text Input                                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Label + Input (Default):                                       │
│  ┌─────────────────────────────────────────────────┐           │
│  │  Email Address *                                │           │
│  │  ┌─────────────────────────────────────────────┐│           │
│  │  │ john.doe@example.com                        ││           │
│  │  └─────────────────────────────────────────────┘│           │
│  │  Helper text or description                     │           │
│  └─────────────────────────────────────────────────┘           │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Label:                                                    │  │
│  │   Font: 14px/500 (fontSizeBase300/Medium)                │  │
│  │   Color: #1F2937 (colorNeutralForeground1)               │  │
│  │   Margin Bottom: 4px                                      │  │
│  │   Required Indicator: " *" in #C5221F                    │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Input Box:                                                │  │
│  │   Height: 32px                                            │  │
│  │   Padding: 8px 12px                                       │  │
│  │   Border: 1px solid #D1D5DB (colorNeutralStroke1)        │  │
│  │   Border Radius: 4px                                      │  │
│  │   Font: 14px/400                                          │  │
│  │   Background: #FFFFFF                                     │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Helper Text:                                              │  │
│  │   Font: 12px/400 (fontSizeBase200)                       │  │
│  │   Color: #6B7280 (colorNeutralForeground2)               │  │
│  │   Margin Top: 4px                                         │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                 │
│  States:                                                        │
│                                                                 │
│  Focus:                                                         │
│  ┌─────────────────────────────────────────────┐               │
│  │                                             │  Border: 2px  │
│  │  |                                          │  #006DE2      │
│  └─────────────────────────────────────────────┘               │
│                                                                 │
│  Error:                                                         │
│  ┌─────────────────────────────────────────────┐               │
│  │ invalid-email                               │  Border: 1px  │
│  └─────────────────────────────────────────────┘  #C5221F      │
│  ⚠ Please enter a valid email address            Error text:   │
│                                                   #C5221F      │
│                                                                 │
│  Disabled:                                                      │
│  ┌─────────────────────────────────────────────┐               │
│  │ disabled@example.com                        │  Background:  │
│  └─────────────────────────────────────────────┘  #F3F4F6      │
│                                                   Cursor: not  │
│                                                   allowed      │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Password Input
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Password Input                                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Password *                                                     │
│  ┌─────────────────────────────────────────────┬─────┐         │
│  │ ●●●●●●●●●●●●                               │ 👁  │         │
│  └─────────────────────────────────────────────┴─────┘         │
│  Minimum 8 characters                                           │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Eye icon toggle: 20px, right padding 12px                   │
│  - Icon button area: 32px width                                │
│  - Revealed state shows actual text                            │
│                                                                 │
│  Password Strength Indicator:                                   │
│  ┌──────────────────────────────────────────────┐              │
│  │ ████████░░░░░░░░░░░░░░░░░░░░  Weak          │              │
│  │ ██████████████░░░░░░░░░░░░░░  Medium        │              │
│  │ ██████████████████████████░░  Strong        │              │
│  │ ██████████████████████████████  Very Strong │              │
│  └──────────────────────────────────────────────┘              │
│  Colors: Weak=#C5221F, Medium=#F59E0B,                         │
│          Strong=#34A853, Very Strong=#137333                   │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Currency Input (Amount Field)
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Currency Input                                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Amount *                                                       │
│  ┌───────┬──────────────────────────────────────┐              │
│  │   €   │  1,500.00                            │              │
│  └───────┴──────────────────────────────────────┘              │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Currency prefix: 40px width, #F3F4F6 background            │
│  - Input: Right-aligned, monospace font                        │
│  - Decimal places: Always show 2                               │
│  - Thousand separator: comma (locale-dependent)                │
│  - Font: 16px monospace for amount                            │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Dropdown / Select
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Dropdown Select                                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Account Type *                                                 │
│  ┌─────────────────────────────────────────────┬─────┐         │
│  │ Savings Account                             │  ▼  │         │
│  └─────────────────────────────────────────────┴─────┘         │
│                                                                 │
│  Expanded (with shadow8):                                       │
│  ┌─────────────────────────────────────────────┬─────┐         │
│  │ Savings Account                             │  ▲  │         │
│  ├─────────────────────────────────────────────┴─────┤         │
│  │ ✓ Savings Account                                 │         │
│  │   Checking Account                                │         │
│  │   Investment Account                              │         │
│  └───────────────────────────────────────────────────┘         │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Chevron icon: 16px                                          │
│  - Dropdown shadow: shadow8                                    │
│  - Option padding: 8px 12px                                    │
│  - Option hover: #F5F5F5 background                            │
│  - Selected indicator: ✓ checkmark                             │
│  - Max height: 300px with scroll                               │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Form Layout
```
┌─────────────────────────────────────────────────────────────────┐
│ FORM LAYOUT SPECIFICATIONS                                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Single Column Form:                                            │
│  ┌─────────────────────────────────────────────────┐           │
│  │  Field 1                                        │           │
│  │  ┌───────────────────────────────────────────┐ │           │
│  │  │                                           │ │           │
│  │  └───────────────────────────────────────────┘ │           │
│  │                          ↕ 16px gap            │           │
│  │  Field 2                                        │           │
│  │  ┌───────────────────────────────────────────┐ │           │
│  │  │                                           │ │           │
│  │  └───────────────────────────────────────────┘ │           │
│  │                          ↕ 16px gap            │           │
│  │  Field 3                                        │           │
│  │  ┌───────────────────────────────────────────┐ │           │
│  │  │                                           │ │           │
│  │  └───────────────────────────────────────────┘ │           │
│  └─────────────────────────────────────────────────┘           │
│                                                                 │
│  Two Column Form (Desktop):                                     │
│  ┌─────────────────────┐  ┌─────────────────────┐              │
│  │ First Name *        │  │ Surname *           │              │
│  │ ┌─────────────────┐ │  │ ┌─────────────────┐ │              │
│  │ │                 │ │  │ │                 │ │              │
│  │ └─────────────────┘ │  │ └─────────────────┘ │              │
│  └─────────────────────┘  └─────────────────────┘              │
│         50%        ↔ 16px gap       50%                        │
│                                                                 │
│  Form Actions:                                                  │
│  ┌─────────────────────────────────────────────────┐           │
│  │                                                 │           │
│  │              ┌──────────┐  ┌──────────────┐    │           │
│  │              │  Cancel  │  │    Submit    │    │           │
│  │              └──────────┘  └──────────────┘    │           │
│  │                   Secondary      Primary       │           │
│  │              ↔ 12px gap between buttons        │           │
│  │              Right-aligned on desktop          │           │
│  └─────────────────────────────────────────────────┘           │
│                                                                 │
│  Form Actions (Mobile): Full width, stacked                    │
│  ┌─────────────────────────────────────────────────┐           │
│  │  ┌─────────────────────────────────────────┐   │           │
│  │  │              Submit                      │   │           │
│  │  └─────────────────────────────────────────┘   │           │
│  │  ┌─────────────────────────────────────────┐   │           │
│  │  │              Cancel                      │   │           │
│  │  └─────────────────────────────────────────┘   │           │
│  │              ↕ 8px gap between buttons         │           │
│  └─────────────────────────────────────────────────┘           │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

### 2.3 Cards

#### Balance Card (Hero)
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Balance Card                                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │                                                         │   │
│  │   💳 My Savings Account                                 │   │
│  │   ****1234                                              │   │
│  │                                                         │   │
│  │   Available Balance                                     │   │
│  │   €12,450.00                                           │   │
│  │                                                         │   │
│  │   ┌──────────┐  ┌──────────┐  ┌──────────┐            │   │
│  │   │ Deposit  │  │ Withdraw │  │ Transfer │            │   │
│  │   └──────────┘  └──────────┘  └──────────┘            │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Card:                                                     │  │
│  │   Background: Linear gradient                            │  │
│  │     from #006DE2 to #004DA0 (135deg)                     │  │
│  │   Border Radius: 12px                                    │  │
│  │   Padding: 24px                                          │  │
│  │   Shadow: shadow8                                        │  │
│  │   Min Height: 200px                                      │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Account Name:                                             │  │
│  │   Font: 16px/500 white                                   │  │
│  │   Icon: 20px, margin-right 8px                          │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Account Number:                                           │  │
│  │   Font: 14px monospace, rgba(255,255,255,0.8)           │  │
│  │   Letter spacing: 0.05em                                 │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Balance Label:                                            │  │
│  │   Font: 14px/400, rgba(255,255,255,0.9)                 │  │
│  │   Margin top: 24px                                       │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Balance Amount:                                           │  │
│  │   Font: 48px/700 white                                   │  │
│  │   Letter spacing: -0.02em                                │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Action Buttons:                                           │  │
│  │   Background: rgba(255,255,255,0.2)                      │  │
│  │   Text: white                                            │  │
│  │   Border: 1px solid rgba(255,255,255,0.3)               │  │
│  │   Hover: rgba(255,255,255,0.3)                          │  │
│  │   Gap: 12px between buttons                             │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Account List Card
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Account List Card                                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  💳  Savings Account                         €12,450.00 │   │
│  │      ****1234                                      ▸    │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Background: #FFFFFF                                         │
│  - Border: 1px solid #E5E7EB                                  │
│  - Border Radius: 8px                                          │
│  - Padding: 16px                                               │
│  - Shadow: shadow4                                             │
│  - Hover: shadow8, border-color: #006DE2                      │
│  - Gap between cards: 12px                                     │
│                                                                 │
│  Content Layout:                                                │
│  - Left icon: 24px, color based on account type               │
│  - Title: 14px/600, color: #1F2937                            │
│  - Subtitle: 12px monospace, color: #6B7280                   │
│  - Amount: 16px/600 monospace, right-aligned                  │
│  - Chevron: 16px, color: #9CA3AF                              │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Transaction Card
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Transaction Card                                     │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Deposit Transaction:                                           │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ┌───┐                                                  │   │
│  │  │ ↓ │  Salary Payment                      +€2,500.00  │   │
│  │  └───┘  Dec 15, 2025 • 09:30                           │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  Withdrawal Transaction:                                        │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ┌───┐                                                  │   │
│  │  │ ↑ │  ATM Withdrawal                       -€200.00   │   │
│  │  └───┘  Dec 14, 2025 • 14:22                           │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  Transfer Out:                                                  │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ┌───┐                                                  │   │
│  │  │ → │  Transfer to Checking                 -€500.00   │   │
│  │  └───┘  Dec 13, 2025 • 11:45                           │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  Transfer In:                                                   │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ┌───┐                                                  │   │
│  │  │ ← │  Transfer from Savings                +€500.00   │   │
│  │  └───┘  Dec 13, 2025 • 11:45                           │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Card:                                                     │  │
│  │   Background: #FFFFFF                                    │  │
│  │   Border: 1px solid #E5E7EB                             │  │
│  │   Border Radius: 8px                                     │  │
│  │   Padding: 12px 16px                                     │  │
│  │   Margin Bottom: 8px                                     │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Icon Container:                                           │  │
│  │   Size: 40px x 40px                                      │  │
│  │   Border Radius: 8px                                     │  │
│  │   Icon Size: 20px                                        │  │
│  │                                                           │  │
│  │   Deposit:    Bg: #E6F4EA, Icon: #34A853 (↓)            │  │
│  │   Withdrawal: Bg: #FCE8E6, Icon: #EA4335 (↑)            │  │
│  │   Transfer Out: Bg: #FEF3E2, Icon: #F59E0B (→)          │  │
│  │   Transfer In:  Bg: #E0F2FE, Icon: #0EA5E9 (←)          │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Description:                                              │  │
│  │   Font: 14px/500, Color: #1F2937                        │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Date/Time:                                                │  │
│  │   Font: 12px/400, Color: #6B7280                        │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Amount:                                                   │  │
│  │   Font: 16px/600 monospace                              │  │
│  │   Positive: #137333                                      │  │
│  │   Negative: #C5221F                                      │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Empty State Card
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Empty State                                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │                                                         │   │
│  │                        📄                               │   │
│  │                    (48px icon)                          │   │
│  │                                                         │   │
│  │                No transactions yet                      │   │
│  │       Your transactions will appear here once          │   │
│  │           you make your first deposit.                  │   │
│  │                                                         │   │
│  │               ┌─────────────────────┐                  │   │
│  │               │   Make a Deposit    │                  │   │
│  │               └─────────────────────┘                  │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Icon: 48px, color: #9CA3AF                                  │
│  - Title: 18px/600, color: #1F2937, margin-top: 16px          │
│  - Description: 14px/400, color: #6B7280, max-width: 300px    │
│  - Text-align: center                                          │
│  - CTA Button: margin-top: 24px                                │
│  - Padding: 48px                                               │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

### 2.4 Data Display

#### Transaction Table (Desktop)
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Transaction Table                                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │ Date        │ Description          │ Type     │ Amount  │   │
│  ├─────────────────────────────────────────────────────────┤   │
│  │ Dec 15      │ Salary Payment       │ Deposit  │+€2,500  │   │
│  │ Dec 14      │ ATM Withdrawal       │ Withdraw │ -€200   │   │
│  │ Dec 13      │ Transfer to Checking │ Transfer │ -€500   │   │
│  │ Dec 12      │ Online Purchase      │ Withdraw │  -€89   │   │
│  │ Dec 10      │ Interest Credit      │ Deposit  │  +€12   │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Table Container:                                          │  │
│  │   Background: #FFFFFF                                    │  │
│  │   Border: 1px solid #E5E7EB                             │  │
│  │   Border Radius: 8px                                     │  │
│  │   Overflow: hidden                                       │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Header Row:                                               │  │
│  │   Background: #F9FAFB                                    │  │
│  │   Font: 12px/600 uppercase                              │  │
│  │   Color: #6B7280                                         │  │
│  │   Padding: 12px 16px                                     │  │
│  │   Letter spacing: 0.05em                                 │  │
│  │   Border Bottom: 1px solid #E5E7EB                      │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Data Row:                                                 │  │
│  │   Padding: 12px 16px                                     │  │
│  │   Border Bottom: 1px solid #F3F4F6                      │  │
│  │   Font: 14px/400                                         │  │
│  │   Hover: Background #F9FAFB                             │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Amount Column:                                            │  │
│  │   Font: 14px/600 monospace                              │  │
│  │   Text-align: right                                      │  │
│  │   Positive: #137333, Negative: #C5221F                  │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Type Badge:                                               │  │
│  │   Font: 12px/500                                         │  │
│  │   Padding: 2px 8px                                       │  │
│  │   Border Radius: 12px (pill)                            │  │
│  │   Deposit: Bg #E6F4EA, Text #137333                     │  │
│  │   Withdrawal: Bg #FCE8E6, Text #C5221F                  │  │
│  │   Transfer: Bg #E0F2FE, Text #0369A1                    │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Filter Bar
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Filter Bar                                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Desktop Layout:                                                │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │ 🔍 Search transactions...  │ Type ▼ │ Date Range ▼ │🔄│   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  Mobile Layout:                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │ 🔍 Search transactions...                           │ ⚙ │   │
│  └─────────────────────────────────────────────────────────┘   │
│  (Filter icon opens bottom sheet with filter options)          │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Container: Flex, gap 12px, padding 16px                     │
│  - Search input: Flex-grow, min-width 200px                    │
│  - Filter dropdowns: min-width 120px                           │
│  - Reset button: Icon only, 32x32px                            │
│  - Background: #FFFFFF                                         │
│  - Border: 1px solid #E5E7EB                                   │
│  - Border Radius: 8px                                          │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Pagination
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Pagination                                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Showing 1-10 of 156        ‹ │ 1 │ 2 │ 3 │ ... │ 16 │ › │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Info text: 14px/400, color #6B7280                          │
│  - Page buttons: 32x32px, border-radius 4px                    │
│  - Current page: Background #006DE2, text white                │
│  - Other pages: Background transparent, text #1F2937           │
│  - Hover: Background #F5F5F5                                   │
│  - Nav arrows: 32x32px, disabled: opacity 0.5                  │
│  - Gap between buttons: 4px                                    │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

### 2.5 Navigation

#### Header / App Bar
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Header                                               │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Desktop (768px+):                                              │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  🏦 AzureBank        Dashboard  Accounts  Transactions    │   │
│  │                                        Welcome, John ▼  │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  Mobile (<768px):                                               │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ☰    🏦 AzureBank                              👤         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Container:                                                │  │
│  │   Height: 64px (desktop), 56px (mobile)                  │  │
│  │   Background: #FFFFFF                                    │  │
│  │   Border Bottom: 1px solid #E5E7EB                      │  │
│  │   Box Shadow: 0 1px 3px rgba(0,0,0,0.1)                 │  │
│  │   Position: sticky, top: 0                              │  │
│  │   Z-index: 1030                                          │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Logo:                                                     │  │
│  │   Icon: 24px                                             │  │
│  │   Text: 18px/600, color #006DE2                         │  │
│  │   Gap: 8px                                               │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Nav Items:                                                │  │
│  │   Font: 14px/500                                         │  │
│  │   Color: #4B5563 (default), #006DE2 (active)            │  │
│  │   Padding: 8px 16px                                      │  │
│  │   Gap: 8px                                               │  │
│  │   Active indicator: 2px bottom border #006DE2           │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ User Menu:                                                │  │
│  │   Font: 14px/500                                         │  │
│  │   Avatar: 32px circle (if using avatar)                 │  │
│  │   Chevron: 16px                                          │  │
│  │   Dropdown shadow: shadow8                               │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Mobile Navigation Drawer
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Mobile Nav Drawer                                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌───────────────────────────────┬─────────────────────────┐   │
│  │                               │                         │   │
│  │   🏦 AzureBank              ✕   │                         │   │
│  │   ─────────────────────────   │      (Overlay)          │   │
│  │                               │      rgba(0,0,0,0.5)    │   │
│  │   👤 John Smith               │                         │   │
│  │   john@example.com            │                         │   │
│  │   ─────────────────────────   │                         │   │
│  │                               │                         │   │
│  │   🏠 Dashboard                │                         │   │
│  │   💳 Accounts                 │                         │   │
│  │   📋 Transactions             │                         │   │
│  │   ─────────────────────────   │                         │   │
│  │                               │                         │   │
│  │   🚪 Sign Out                 │                         │   │
│  │                               │                         │   │
│  └───────────────────────────────┴─────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Drawer width: 280px (max 80vw)                              │
│  - Background: #FFFFFF                                         │
│  - Animation: slide from left, 200ms ease-out                  │
│  - Overlay: rgba(0,0,0,0.5), click to close                    │
│  - Header padding: 16px                                        │
│  - Nav item: 48px height, padding 12px 16px                    │
│  - Nav item hover: Background #F5F5F5                          │
│  - Active nav: Background #E0F2FE, text #006DE2                │
│  - Divider: 1px solid #E5E7EB, margin 8px 0                    │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### User Dropdown Menu
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: User Dropdown                                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│                                    Welcome, John ▼              │
│                                    ┌──────────────────────┐    │
│                                    │  👤 Profile Settings │    │
│                                    │  ─────────────────── │    │
│                                    │  🚪 Sign Out         │    │
│                                    └──────────────────────┘    │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Min width: 180px                                            │
│  - Background: #FFFFFF                                         │
│  - Border: 1px solid #E5E7EB                                   │
│  - Border Radius: 8px                                          │
│  - Shadow: shadow8                                             │
│  - Padding: 8px 0                                              │
│  - Item padding: 8px 16px                                      │
│  - Item hover: Background #F5F5F5                              │
│  - Icon: 16px, margin-right 8px                               │
│  - Divider: 1px solid #E5E7EB, margin 4px 0                    │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

### 2.6 Modals & Dialogs

#### Standard Dialog
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Dialog                                               │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │                                                         │   │
│  │  Create New Account                                 ✕   │   │
│  │  ─────────────────────────────────────────────────────  │   │
│  │                                                         │   │
│  │  Account Name *                                         │   │
│  │  ┌─────────────────────────────────────────────────┐   │   │
│  │  │                                                 │   │   │
│  │  └─────────────────────────────────────────────────┘   │   │
│  │                                                         │   │
│  │  Account Type *                                         │   │
│  │  ┌─────────────────────────────────────────────┬───┐   │   │
│  │  │ Select account type                         │ ▼ │   │   │
│  │  └─────────────────────────────────────────────┴───┘   │   │
│  │                                                         │   │
│  │  Initial Deposit                                        │   │
│  │  ┌─────┬───────────────────────────────────────────┐   │   │
│  │  │  €  │ 0.00                                      │   │   │
│  │  └─────┴───────────────────────────────────────────┘   │   │
│  │                                                         │   │
│  │  ─────────────────────────────────────────────────────  │   │
│  │                      ┌──────────┐  ┌──────────────┐    │   │
│  │                      │  Cancel  │  │    Create    │    │   │
│  │                      └──────────┘  └──────────────┘    │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │ Overlay:                                                  │  │
│  │   Background: rgba(0, 0, 0, 0.4)                         │  │
│  │   Z-index: 1040                                          │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Dialog:                                                   │  │
│  │   Width: 480px (max 90vw)                                │  │
│  │   Background: #FFFFFF                                    │  │
│  │   Border Radius: 8px                                     │  │
│  │   Shadow: shadow16                                       │  │
│  │   Z-index: 1050                                          │  │
│  │   Animation: fadeIn + scaleUp 200ms ease-out            │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Header:                                                   │  │
│  │   Padding: 16px 24px                                     │  │
│  │   Border Bottom: 1px solid #E5E7EB                      │  │
│  │   Title: 18px/600                                        │  │
│  │   Close button: 32x32px, position absolute right        │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Body:                                                     │  │
│  │   Padding: 24px                                          │  │
│  │   Max Height: 60vh with overflow scroll                 │  │
│  ├──────────────────────────────────────────────────────────┤  │
│  │ Footer:                                                   │  │
│  │   Padding: 16px 24px                                     │  │
│  │   Border Top: 1px solid #E5E7EB                         │  │
│  │   Buttons: right-aligned, gap 12px                      │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Confirmation Dialog
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Confirmation Dialog                                  │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │                                                         │   │
│  │                         ⚠️                               │   │
│  │                                                         │   │
│  │                  Delete Account?                        │   │
│  │                                                         │   │
│  │     Are you sure you want to delete "Savings"?         │   │
│  │     This action cannot be undone.                       │   │
│  │                                                         │   │
│  │         ┌──────────┐  ┌────────────────┐               │   │
│  │         │  Cancel  │  │ Delete Account │               │   │
│  │         └──────────┘  └────────────────┘               │   │
│  │                         (Danger Button)                 │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Width: 400px max                                            │
│  - Icon: 48px, centered, color based on action type            │
│  - Title: 20px/600, centered, margin-top 16px                  │
│  - Message: 14px/400, centered, color #6B7280                  │
│  - Buttons: centered, gap 12px                                  │
│  - Danger action: Use danger button style                       │
│  - Padding: 32px                                               │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Mobile Full-Screen Dialog
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Mobile Full-Screen Dialog                            │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ←  Create Account                              Save    │   │
│  │  ─────────────────────────────────────────────────────  │   │
│  │                                                         │   │
│  │  Account Name *                                         │   │
│  │  ┌─────────────────────────────────────────────────┐   │   │
│  │  │                                                 │   │   │
│  │  └─────────────────────────────────────────────────┘   │   │
│  │                                                         │   │
│  │  Account Type *                                         │   │
│  │  ┌─────────────────────────────────────────────┬───┐   │   │
│  │  │ Select account type                         │ ▼ │   │   │
│  │  └─────────────────────────────────────────────┴───┘   │   │
│  │                                                         │   │
│  │  ...                                                    │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS (Mobile < 480px):                              │
│  - Takes full viewport                                         │
│  - Header: 56px, sticky                                        │
│  - Back arrow: replaces close X                                │
│  - Primary action in header (text button)                      │
│  - Body: scrollable, padding 16px                              │
│  - No footer - action in header                                │
│  - Animation: slide up from bottom                             │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

### 2.7 Feedback Components

#### Toast Notifications
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Toast Notifications                                  │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Success Toast:                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ✓  Account created successfully                    ✕   │   │
│  └─────────────────────────────────────────────────────────┘   │
│  Background: #E6F4EA, Border-left: 4px solid #34A853           │
│                                                                 │
│  Error Toast:                                                   │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ✕  Failed to process transaction                   ✕   │   │
│  └─────────────────────────────────────────────────────────┘   │
│  Background: #FCE8E6, Border-left: 4px solid #EA4335           │
│                                                                 │
│  Warning Toast:                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ⚠  Low balance warning                             ✕   │   │
│  └─────────────────────────────────────────────────────────┘   │
│  Background: #FEF3E2, Border-left: 4px solid #F59E0B           │
│                                                                 │
│  Info Toast:                                                    │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ℹ  Processing your request...                      ✕   │   │
│  └─────────────────────────────────────────────────────────┘   │
│  Background: #E0F2FE, Border-left: 4px solid #0EA5E9           │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Position: top-right, 24px from edges                        │
│  - Width: 360px max (100% - 32px on mobile)                    │
│  - Padding: 12px 16px                                          │
│  - Border Radius: 8px                                          │
│  - Shadow: shadow16                                            │
│  - Icon: 20px                                                  │
│  - Text: 14px/500                                              │
│  - Close button: 16px                                          │
│  - Auto dismiss: 5 seconds                                     │
│  - Animation: slide in from right, 300ms                       │
│  - Stack: max 3 visible, 8px gap                              │
│  - Z-index: 1080                                               │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Loading States
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Loading States                                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Spinner (Inline):                                              │
│  ○◔◑◕●  Size: 20px (small), 32px (medium), 48px (large)        │
│         Color: #006DE2 (brand)                                  │
│         Animation: rotate 1s linear infinite                    │
│                                                                 │
│  Button Loading:                                                │
│  ┌─────────────────────┐                                       │
│  │  ○  Processing...   │  Spinner replaces icon/left of text   │
│  └─────────────────────┘  Button disabled during load          │
│                                                                 │
│  Skeleton Card:                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░                            │   │
│  │  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░              │   │
│  │  ▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░                      │   │
│  └─────────────────────────────────────────────────────────┘   │
│  Background: #E5E7EB                                           │
│  Animation: pulse (opacity 0.6 to 1), 1.5s ease-in-out         │
│                                                                 │
│  Full Page Loading:                                             │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │                                                         │   │
│  │                         ○                               │   │
│  │                   Loading...                            │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│  Centered in viewport, background: rgba(255,255,255,0.9)       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Alert / Banner
```
┌─────────────────────────────────────────────────────────────────┐
│ COMPONENT: Alert Banner                                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ⚠  Your session will expire in 5 minutes. Save your   │   │
│  │     work or extend your session.        [Extend] [✕]   │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Full width, typically below header                          │
│  - Padding: 12px 24px                                          │
│  - Background/Border based on type (same as toast)             │
│  - Icon: 20px                                                  │
│  - Text: 14px/400                                              │
│  - Actions: right-aligned buttons                              │
│  - Dismissible: optional close button                          │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## 3. Page Layouts

### 3.1 Desktop Layouts

#### Authentication Pages (Login/Register)
```
┌─────────────────────────────────────────────────────────────────┐
│ LAYOUT: Authentication Page (Desktop)                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │                                                         │   │
│  │                     🏦 AzureBank                          │   │
│  │                                                         │   │
│  │               ┌─────────────────────┐                  │   │
│  │               │                     │                  │   │
│  │               │     Login Form      │                  │   │
│  │               │                     │                  │   │
│  │               │    Width: 400px     │                  │   │
│  │               │                     │                  │   │
│  │               └─────────────────────┘                  │   │
│  │                                                         │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Centered vertically and horizontally                        │
│  - Min height: 100vh                                           │
│  - Background: #F9FAFB or subtle pattern                       │
│  - Card: 400px width, shadow4, border-radius 8px               │
│  - Card padding: 32px                                          │
│  - Logo: centered above card, margin-bottom 24px               │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Dashboard Layout
```
┌─────────────────────────────────────────────────────────────────┐
│ LAYOUT: Dashboard (Desktop)                                     │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Header (64px)                                          │   │
│  ├─────────────────────────────────────────────────────────┤   │
│  │                                                         │   │
│  │  ┌───────────────────────────────────────────────────┐ │   │
│  │  │  Balance Card (Hero)                              │ │   │
│  │  │  max-width: 800px, centered                       │ │   │
│  │  └───────────────────────────────────────────────────┘ │   │
│  │                          ↕ 24px                         │   │
│  │  ┌───────────────────────────────────────────────────┐ │   │
│  │  │  Quick Actions Row                                │ │   │
│  │  │  ┌─────────┐ ┌─────────┐ ┌─────────┐ ┌─────────┐ │ │   │
│  │  │  │ Deposit │ │Withdraw │ │Transfer │ │ NewAcct │ │ │   │
│  │  │  └─────────┘ └─────────┘ └─────────┘ └─────────┘ │ │   │
│  │  └───────────────────────────────────────────────────┘ │   │
│  │                          ↕ 24px                         │   │
│  │  ┌────────────────────┐  ┌────────────────────────┐   │   │
│  │  │ Recent Trans (50%) │  │  My Accounts (50%)     │   │   │
│  │  │                    │  │                        │   │   │
│  │  │                    │  │                        │   │   │
│  │  └────────────────────┘  └────────────────────────┘   │   │
│  │       ↔ 24px gap between columns                       │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Content max-width: 1200px, centered                         │
│  - Page padding: 32px                                          │
│  - Section gap: 24px                                           │
│  - Two-column grid at 768px+                                   │
│  - Background: #F9FAFB                                         │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Account Detail Layout
```
┌─────────────────────────────────────────────────────────────────┐
│ LAYOUT: Account Detail (Desktop)                                │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Header (64px)                                          │   │
│  ├─────────────────────────────────────────────────────────┤   │
│  │                                                         │   │
│  │  ← Back to Dashboard                                    │   │
│  │                          ↕ 16px                         │   │
│  │  ┌───────────────────────────────────────────────────┐ │   │
│  │  │  Account Balance Card (Hero)                      │ │   │
│  │  │  Full width of content area                       │ │   │
│  │  └───────────────────────────────────────────────────┘ │   │
│  │                          ↕ 24px                         │   │
│  │  ┌───────────────────────────────────────────────────┐ │   │
│  │  │  Filter Bar                                       │ │   │
│  │  │  🔍 Search... │ Type ▼ │ Date ▼ │ Reset          │ │   │
│  │  └───────────────────────────────────────────────────┘ │   │
│  │                          ↕ 16px                         │   │
│  │  ┌───────────────────────────────────────────────────┐ │   │
│  │  │  Transaction Table / List                         │ │   │
│  │  │                                                   │ │   │
│  │  │  Date    │ Description │ Type    │ Amount        │ │   │
│  │  │  --------|-------------|---------|--------       │ │   │
│  │  │  ...     │ ...         │ ...     │ ...           │ │   │
│  │  │                                                   │ │   │
│  │  └───────────────────────────────────────────────────┘ │   │
│  │                          ↕ 16px                         │   │
│  │  ┌───────────────────────────────────────────────────┐ │   │
│  │  │  Pagination                                       │ │   │
│  │  │  Showing 1-10 of 156      ‹ 1 2 3 ... 16 ›       │ │   │
│  │  └───────────────────────────────────────────────────┘ │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Content max-width: 1000px, centered                         │
│  - Back link: 14px, icon + text                               │
│  - Single column layout                                        │
│  - Table on desktop, cards on mobile                           │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

### 3.2 Mobile Layouts

#### Authentication Pages (Mobile)
```
┌─────────────────────────────────────────────────────────────────┐
│ LAYOUT: Authentication Page (Mobile)                            │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────────────┐                                          │
│  │                  │                                          │
│  │   🏦 AzureBank     │   Logo at top, not centered vertically   │
│  │                  │                                          │
│  │  Welcome Back    │                                          │
│  │                  │                                          │
│  │  Email           │                                          │
│  │  ┌────────────┐  │                                          │
│  │  │            │  │  Full width inputs                       │
│  │  └────────────┘  │                                          │
│  │                  │                                          │
│  │  Password        │                                          │
│  │  ┌────────────┐  │                                          │
│  │  │            │  │                                          │
│  │  └────────────┘  │                                          │
│  │                  │                                          │
│  │  ┌────────────┐  │                                          │
│  │  │   Log In   │  │  Full width button                       │
│  │  └────────────┘  │                                          │
│  │                  │                                          │
│  │  Don't have...   │                                          │
│  │                  │                                          │
│  └──────────────────┘                                          │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Padding: 24px                                               │
│  - No card wrapper - direct on background                      │
│  - Logo: top, margin-bottom 32px                               │
│  - Inputs: full width                                          │
│  - Button: full width                                          │
│  - Keyboard-aware: content scrolls when keyboard open          │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Dashboard Layout (Mobile)
```
┌─────────────────────────────────────────────────────────────────┐
│ LAYOUT: Dashboard (Mobile)                                      │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────────────┐                                          │
│  │ ☰  BankApp   👤  │  56px header                             │
│  ├──────────────────┤                                          │
│  │                  │                                          │
│  │ ┌──────────────┐ │                                          │
│  │ │ Balance Card │ │  Full width, horizontal scroll           │
│  │ │ (swipeable)  │ │  for multiple accounts                   │
│  │ └──────────────┘ │                                          │
│  │   ○ ○ ● ○        │  Pagination dots                        │
│  │                  │                                          │
│  │ ┌──┐ ┌──┐ ┌──┐   │                                          │
│  │ │+$│ │-$│ │↔ │   │  Quick action buttons                    │
│  │ └──┘ └──┘ └──┘   │  3 columns, icon + label below          │
│  │ Dep  Wth  Trf    │                                          │
│  │                  │                                          │
│  │ Recent Trans     │                                          │
│  │ ┌──────────────┐ │                                          │
│  │ │ Trans Card 1 │ │  Stacked transaction cards               │
│  │ └──────────────┘ │                                          │
│  │ ┌──────────────┐ │                                          │
│  │ │ Trans Card 2 │ │                                          │
│  │ └──────────────┘ │                                          │
│  │ ┌──────────────┐ │                                          │
│  │ │ Trans Card 3 │ │                                          │
│  │ └──────────────┘ │                                          │
│  │                  │                                          │
│  │  View All →      │                                          │
│  │                  │                                          │
│  └──────────────────┘                                          │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Padding: 16px                                               │
│  - Balance card: swipeable carousel if multiple accounts       │
│  - Quick actions: 3-column grid, 48px icons                    │
│  - Transactions: card layout (not table)                       │
│  - "View All" link at bottom of section                       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

#### Account Detail (Mobile)
```
┌─────────────────────────────────────────────────────────────────┐
│ LAYOUT: Account Detail (Mobile)                                 │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────────────┐                                          │
│  │ ←  Savings    ⚙  │  56px header with back arrow             │
│  ├──────────────────┤                                          │
│  │                  │                                          │
│  │ ┌──────────────┐ │                                          │
│  │ │              │ │                                          │
│  │ │  €12,450.00  │ │  Compact balance display                 │
│  │ │              │ │                                          │
│  │ │ [+] [-] [↔]  │ │  Action buttons inline                  │
│  │ └──────────────┘ │                                          │
│  │                  │                                          │
│  │ 🔍 Search...  ⚙  │  Search + filter button                  │
│  │                  │                                          │
│  │ Today            │  Date grouping                           │
│  │ ┌──────────────┐ │                                          │
│  │ │ Trans Card   │ │                                          │
│  │ └──────────────┘ │                                          │
│  │ ┌──────────────┐ │                                          │
│  │ │ Trans Card   │ │                                          │
│  │ └──────────────┘ │                                          │
│  │                  │                                          │
│  │ Yesterday        │                                          │
│  │ ┌──────────────┐ │                                          │
│  │ │ Trans Card   │ │                                          │
│  │ └──────────────┘ │                                          │
│  │                  │                                          │
│  │   Load More ↓    │  Infinite scroll or load more           │
│  │                  │                                          │
│  └──────────────────┘                                          │
│                                                                 │
│  SPECIFICATIONS:                                                │
│  - Transactions grouped by date                                │
│  - Infinite scroll instead of pagination                       │
│  - Filter opens bottom sheet                                   │
│  - Settings (⚙) opens account options bottom sheet            │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

---

## 4. Responsive Breakpoint Behaviors

### 4.1 Component Adaptations

| Component | Mobile (<480px) | Tablet (480-767px) | Desktop (768px+) |
|-----------|-----------------|-------------------|------------------|
| Header | Hamburger menu | Hamburger menu | Inline nav |
| Balance Card | Full width | Full width | Max 800px |
| Quick Actions | 3-column grid | 4-column grid | Horizontal row |
| Transactions | Cards | Cards | Table |
| Dialogs | Full screen | Centered modal | Centered modal |
| Forms | Single column | Single column | Two column option |
| Pagination | Load more | Simplified | Full pagination |
| Filters | Bottom sheet | Inline dropdown | Inline dropdown |

### 4.2 Touch Targets

| Device | Minimum Touch Target | Recommended |
|--------|---------------------|-------------|
| Mobile | 44px x 44px | 48px x 48px |
| Tablet | 44px x 44px | 44px x 44px |
| Desktop | 32px x 32px | 32px x 32px |

---

## 5. Accessibility Specifications

### 5.1 Color Contrast

| Element | Foreground | Background | Contrast Ratio | WCAG Level |
|---------|-----------|------------|----------------|------------|
| Body Text | #1F2937 | #FFFFFF | 14.7:1 | AAA |
| Secondary Text | #6B7280 | #FFFFFF | 5.4:1 | AA |
| Primary Button | #FFFFFF | #006DE2 | 5.2:1 | AA |
| Link Text | #006DE2 | #FFFFFF | 4.5:1 | AA |
| Error Text | #C5221F | #FFFFFF | 5.9:1 | AA |
| Success Text | #137333 | #FFFFFF | 7.1:1 | AAA |

### 5.2 Focus Indicators

```
All interactive elements:
- Focus outline: 2px solid #000000
- Outline offset: 2px
- Visible on keyboard navigation
- Not visible on mouse click (focus-visible)
```

### 5.3 Screen Reader Considerations

| Feature | Implementation |
|---------|---------------|
| Form Labels | Associated via `htmlFor` |
| Error Messages | `aria-describedby` |
| Loading States | `aria-live="polite"` |
| Dialogs | `role="dialog"`, `aria-modal="true"` |
| Toasts | `role="alert"`, `aria-live="assertive"` |
| Navigation | `role="navigation"`, `aria-label` |
| Balance Display | `aria-label` with full currency format |

---

## 6. Implementation Notes

### 6.1 FluentUI Component Mapping

| Design Component | FluentUI Component |
|-----------------|-------------------|
| Primary Button | `<Button appearance="primary">` |
| Secondary Button | `<Button appearance="outline">` |
| Text Input | `<Input>` |
| Select | `<Dropdown>` or `<Combobox>` |
| Dialog | `<Dialog>` |
| Toast | `<Toaster>` / `<Toast>` |
| Spinner | `<Spinner>` |
| Card | `<Card>` |
| Table | `<Table>` (DataGrid for complex) |

### 6.2 Custom Components Needed

1. **BalanceCard** - Custom hero card with gradient
2. **TransactionCard** - Custom transaction display
3. **CurrencyInput** - Input with currency prefix
4. **PasswordInput** - Input with visibility toggle
5. **FilterBar** - Combined search and filter component
6. **MobileNav** - Drawer navigation for mobile

---

---

## 7. Enhanced Interaction Specifications (Industry Standards)

Based on industry research (see 04m-industry-standards-analysis.md), the following enhancements align with 2025 enterprise fintech standards.

### 7.1 Microinteraction Specifications

#### Button Press Feedback
```
┌─────────────────────────────────────────────────────────────────┐
│ BUTTON INTERACTION STATES                                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  REST STATE:                                                     │
│  ┌─────────────────────┐                                        │
│  │   Create Account    │  transform: scale(1)                   │
│  └─────────────────────┘  transition: transform 100ms ease-out  │
│                                                                  │
│  HOVER STATE:                                                    │
│  ┌─────────────────────┐                                        │
│  │   Create Account    │  transform: scale(1.02)                │
│  └─────────────────────┘  box-shadow: shadow8                   │
│                           cursor: pointer                        │
│                                                                  │
│  PRESS/ACTIVE STATE:                                             │
│  ┌─────────────────────┐                                        │
│  │   Create Account    │  transform: scale(0.97)                │
│  └─────────────────────┘  transition: transform 100ms ease-out  │
│                                                                  │
│  LOADING STATE:                                                  │
│  ┌─────────────────────┐                                        │
│  │ ○  Processing...    │  Spinner: 16px, left of text           │
│  └─────────────────────┘  Button disabled, min-width preserved  │
│                           opacity: 0.8                           │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 7.2 Skeleton Loading Specifications

```
┌─────────────────────────────────────────────────────────────────┐
│ SKELETON LOADING COMPONENT                                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  SPECIFICATIONS:                                                 │
│  - Background: linear-gradient(90deg,                           │
│      #E5E7EB 0%,                                                 │
│      #F3F4F6 50%,                                                │
│      #E5E7EB 100%)                                               │
│  - Background-size: 200% 100%                                    │
│  - Animation: shimmer 1.5s infinite                              │
│  - Border-radius: matches actual component                       │
│                                                                  │
│  BALANCE CARD SKELETON:                                          │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░   │   │
│  │  ░░░░░░░░░░░░░░  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░   │   │
│  │                                                         │   │
│  │  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░         │   │
│  │                                                         │   │
│  │  ░░░░░░░░░░  ░░░░░░░░░░  ░░░░░░░░░░                   │   │
│  └─────────────────────────────────────────────────────────┘   │
│  Height: matches Balance Card (200px min)                       │
│  Border-radius: 12px                                             │
│                                                                  │
│  TRANSACTION CARD SKELETON:                                      │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  ░░░░░  ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  ░░░░░░░░░     │   │
│  │  ░░░░░  ░░░░░░░░░░░░░░░░░░░░                           │   │
│  └─────────────────────────────────────────────────────────┘   │
│  Height: 64px                                                    │
│  Border-radius: 8px                                              │
│                                                                  │
│  KEYFRAMES:                                                      │
│  @keyframes shimmer {                                            │
│    0% { background-position: -200% 0; }                         │
│    100% { background-position: 200% 0; }                        │
│  }                                                               │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 7.3 Progress Stepper Specifications

```
┌─────────────────────────────────────────────────────────────────┐
│ WIZARD PROGRESS STEPPER                                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  VISUAL DESIGN:                                                  │
│                                                                  │
│  ●━━━━━━━━●━━━━━━━━○━━━━━━━━○                                   │
│  Source  Recipient  Amount  Confirm                              │
│                                                                  │
│  STEP STATES:                                                    │
│                                                                  │
│  Completed Step (✓):                                             │
│  ┌───┐                                                          │
│  │ ✓ │  Background: #34A853 (success green)                     │
│  └───┘  Icon: white checkmark                                   │
│         Size: 32px circle                                        │
│                                                                  │
│  Current Step (●):                                               │
│  ┌───┐                                                          │
│  │ 2 │  Background: #006DE2 (brand blue)                        │
│  └───┘  Text: white, step number                                │
│         Size: 32px circle                                        │
│         Ring: 4px solid rgba(0,109,226,0.2)                     │
│                                                                  │
│  Future Step (○):                                                │
│  ┌───┐                                                          │
│  │ 3 │  Background: #E5E7EB (neutral)                           │
│  └───┘  Text: #6B7280                                           │
│         Size: 32px circle                                        │
│                                                                  │
│  CONNECTING LINE:                                                │
│  ━━━━━━━━  Completed: #34A853 (solid)                           │
│  ─ ─ ─ ─  Future: #D1D5DB (dashed or lighter)                   │
│  Width: 2px                                                      │
│                                                                  │
│  STEP LABEL:                                                     │
│  Font: 12px/500                                                  │
│  Color: #6B7280 (future), #1F2937 (current/completed)           │
│  Margin-top: 8px                                                 │
│                                                                  │
│  MOBILE ADAPTATION:                                              │
│  - Horizontal scrollable if needed                              │
│  - Labels hidden, only circles visible                          │
│  - Current step label shown below                               │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 7.4 Animated Balance Display

```
┌─────────────────────────────────────────────────────────────────┐
│ ANIMATED BALANCE NUMBER                                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ANIMATION SPECIFICATIONS:                                       │
│  - Duration: 800ms                                               │
│  - Easing: cubic-bezier(0.33, 1, 0.68, 1) (ease-out-cubic)     │
│  - Trigger: On mount, on value change                           │
│  - Direction: Count up from 0 (or previous value)               │
│                                                                  │
│  VISUAL EXAMPLE:                                                 │
│                                                                  │
│  Frame 0ms:    €0.00                                             │
│  Frame 200ms:  €4,150.00                                         │
│  Frame 400ms:  €9,180.00                                         │
│  Frame 600ms:  €11,670.00                                        │
│  Frame 800ms:  €12,450.00                                        │
│                                                                  │
│  ACCESSIBILITY:                                                  │
│  - Respects prefers-reduced-motion                               │
│  - If reduced motion: show final value immediately              │
│  - aria-label: "Balance: €12,450.00"                            │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 7.5 Success Celebration Animations

```
┌─────────────────────────────────────────────────────────────────┐
│ SUCCESS ANIMATION LEVELS                                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  LEVEL 1: SUBTLE (Small actions)                                │
│  ─────────────────────────────────────────────────────────────  │
│  Usage: Form saves, small deposits, settings changes             │
│                                                                  │
│  ┌─────┐                                                        │
│  │  ✓  │  Checkmark scales in from 0.5 to 1                     │
│  └─────┘  Duration: 300ms                                       │
│           Color: #34A853                                         │
│           Easing: spring(1, 100, 10, 0)                         │
│                                                                  │
│  LEVEL 2: MODERATE (Standard transfers)                         │
│  ─────────────────────────────────────────────────────────────  │
│  Usage: Transfers, account creation, profile updates             │
│                                                                  │
│  ┌─────┐                                                        │
│  │  ✓  │  Checkmark with expanding ring                         │
│  └─────┘  Ring: scales from 1 to 1.5, fades out                 │
│           Duration: 500ms                                        │
│           Optional: subtle sparkle particles                     │
│                                                                  │
│  LEVEL 3: CELEBRATION (Milestones)                              │
│  ─────────────────────────────────────────────────────────────  │
│  Usage: First transfer, savings goal reached, achievements       │
│                                                                  │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │                    🎉  🎊  ✨                             │   │
│  │                       ✓                                  │   │
│  │              Transfer Successful!                        │   │
│  │           €500.00 sent to Jane Smith                     │   │
│  │                                                          │   │
│  │               [ View Receipt ]                           │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                  │
│  - Confetti particles (15-20 pieces)                            │
│  - Large checkmark animation                                     │
│  - Duration: 1500ms                                              │
│  - Auto-dismiss: 3 seconds (or manual)                          │
│                                                                  │
│  REDUCED MOTION:                                                 │
│  - Skip animations, show final state immediately                 │
│  - Use static success icon + message                             │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 7.6 Transaction Date Grouping

```
┌─────────────────────────────────────────────────────────────────┐
│ TRANSACTION LIST WITH DATE HEADERS                               │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  DATE HEADER COMPONENT:                                          │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  Today                                                   │   │
│  └─────────────────────────────────────────────────────────┘   │
│  Font: 12px/600 uppercase                                       │
│  Color: #6B7280                                                  │
│  Letter-spacing: 0.05em                                          │
│  Padding: 8px 0                                                  │
│  Margin-top: 16px (between groups)                              │
│  Border-bottom: 1px solid #E5E7EB (optional)                    │
│                                                                  │
│  DATE GROUPING LOGIC:                                            │
│  - "Today" - current date                                        │
│  - "Yesterday" - previous date                                   │
│  - "This Week" - same week, 2+ days ago                         │
│  - "Dec 15" - older than 1 week, same year                      │
│  - "Dec 15, 2024" - different year                              │
│                                                                  │
│  FULL EXAMPLE:                                                   │
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  TODAY                                                   │   │
│  │  ┌───────────────────────────────────────────────────┐  │   │
│  │  │ ↓  Salary Payment                    +€2,500.00   │  │   │
│  │  └───────────────────────────────────────────────────┘  │   │
│  │  ┌───────────────────────────────────────────────────┐  │   │
│  │  │ ↑  Coffee Shop                         -€4.50     │  │   │
│  │  └───────────────────────────────────────────────────┘  │   │
│  │                                                          │   │
│  │  YESTERDAY                                               │   │
│  │  ┌───────────────────────────────────────────────────┐  │   │
│  │  │ →  Transfer to @janesmith              -€500.00   │  │   │
│  │  └───────────────────────────────────────────────────┘  │   │
│  │                                                          │   │
│  │  THIS WEEK                                               │   │
│  │  ┌───────────────────────────────────────────────────┐  │   │
│  │  │ ↓  Refund from Amazon                  +€29.99    │  │   │
│  │  └───────────────────────────────────────────────────┘  │   │
│  └─────────────────────────────────────────────────────────┘   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 8. Balance Trend Visualization (Optional Enhancement)

```
┌─────────────────────────────────────────────────────────────────┐
│ BALANCE TREND SPARKLINE                                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  PLACEMENT: Below balance in Balance Card                        │
│                                                                  │
│  €12,450.00                                                      │
│         ╱─╲                              ↑ 8.2%                  │
│        ╱   ╲    ╱╲                       vs last month          │
│       ╱     ╲  ╱  ╲___╱╲                                        │
│      ╱       ╲╱          ╲___●                                  │
│  ───────────────────────────────                                 │
│  30d                       Today                                 │
│                                                                  │
│  SPECIFICATIONS:                                                 │
│  - SVG-based sparkline (no heavy chart library)                 │
│  - Width: 100% of card                                          │
│  - Height: 48px                                                  │
│  - Line color: rgba(255,255,255,0.8) (on gradient card)         │
│  - Current dot: 6px solid white                                 │
│  - Trend indicator: ↑/↓ with % change                           │
│  - Trend color: white (on card), green/red (standalone)         │
│                                                                  │
│  DATA POINTS:                                                    │
│  - 30 days of daily closing balances                             │
│  - Fetched with dashboard load                                   │
│  - Cached for 1 hour                                             │
│                                                                  │
│  ACCESSIBILITY:                                                  │
│  - aria-label: "Balance trend over 30 days, up 8.2%"            │
│  - Data table alternative available on click                    │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 9. Microinteraction Timing Specifications (v5.0)

> Complete animation and interaction timing for a polished, professional feel.

### 9.1 Animation Categories

| Category | Duration Range | Use Cases |
|----------|----------------|-----------|
| Micro | 50-150ms | Button press, focus ring |
| Fast | 150-250ms | Hover effects, tooltips, dropdowns |
| Medium | 250-500ms | Dialogs, page transitions, success states |
| Slow | 500-1000ms | Number count-up, balance reveal |
| Celebration | 1000-2000ms | Transfer success, confetti |

### 9.2 Button Interactions

| State | Duration | Easing | Transform/Effect |
|-------|----------|--------|------------------|
| Rest | - | - | scale(1.0) |
| Hover | 150ms | ease-out | scale(1.02), slight shadow lift |
| Press/Active | 100ms | ease-in | scale(0.97) |
| Release | 200ms | ease-out | scale(1.0) |
| Focus | 0ms | - | outline ring 2px offset 2px |
| Loading | infinite | linear | spinner rotation 1s |
| Disabled | - | - | opacity 0.4, cursor not-allowed |

```css
/* Button animation example */
.button {
  transition: transform 150ms ease-out, box-shadow 150ms ease-out;
}
.button:hover {
  transform: scale(1.02);
}
.button:active {
  transform: scale(0.97);
  transition-duration: 100ms;
}
```

### 9.3 Card Interactions

| State | Duration | Easing | Effect |
|-------|----------|--------|--------|
| Rest | - | - | elevation level 1 |
| Hover | 200ms | ease-out | elevation +1, translateY(-2px) |
| Press | 100ms | ease-in | elevation -1, translateY(0) |
| Focus | 0ms | - | outline ring 2px |

### 9.4 Dialog Animations

| Action | Duration | Easing | Effect |
|--------|----------|--------|--------|
| Backdrop appear | 200ms | ease-out | opacity 0 → 0.5 |
| Dialog enter | 250ms | cubic-bezier(0.4, 0, 0.2, 1) | scale(0.95, 1) + opacity 0 → 1 |
| Dialog exit | 200ms | cubic-bezier(0.4, 0, 1, 1) | scale(1, 0.95) + opacity 1 → 0 |
| Backdrop exit | 150ms | ease-in | opacity 0.5 → 0 |

### 9.5 Toast Notifications

| Action | Duration | Easing | Effect |
|--------|----------|--------|--------|
| Enter | 300ms | cubic-bezier(0.4, 0, 0.2, 1) | translateX(100%) → 0 |
| Stay | 4000ms | - | visible |
| Exit | 200ms | ease-in | opacity 1 → 0 |

### 9.6 Success Animations

| Level | Use Case | Animation | Duration |
|-------|----------|-----------|----------|
| Subtle | Form save, profile update | Checkmark fade-in | 300ms |
| Moderate | Deposit, withdrawal | Checkmark + pulse ring | 500ms |
| Celebration | Transfer complete | Confetti burst + check | 1500ms |

**Success Checkmark Animation**:
1. Circle draws (stroke-dashoffset animation) - 300ms
2. Checkmark draws - 200ms
3. Optional pulse ring - 300ms

### 9.7 Number Animation (Balance Reveal)

| Property | Value |
|----------|-------|
| Duration | 800ms |
| Easing | easeOutCubic - cubic-bezier(0.33, 1, 0.68, 1) |
| Start | 0 or previous value |
| End | Current balance |
| Decimals | Animate to 2 decimal places |

### 9.8 Loading States

| Component | Animation | Duration |
|-----------|-----------|----------|
| Button spinner | Rotation | 1000ms infinite |
| Skeleton pulse | Opacity 0.4 ↔ 0.7 | 1500ms infinite |
| Skeleton wave | Gradient sweep | 1500ms infinite |
| Page loader | Dots pulse | 1200ms infinite |

### 9.9 Page Transitions

| Transition | Duration | Effect |
|------------|----------|--------|
| Navigate forward | 200ms | Fade + slight slide right |
| Navigate back | 200ms | Fade + slight slide left |
| Tab switch | 150ms | Fade only |

### 9.10 Reduced Motion

```css
/* Always respect user preference */
@media (prefers-reduced-motion: reduce) {
  *,
  *::before,
  *::after {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
  }
}
```

**Fallback Behaviors**:
- Animations → Instant state change
- Number count-up → Direct value display
- Confetti → Simple checkmark only
- Skeleton wave → Static gray

---

**Document Status**: FINAL - Complete Microinteraction Specs

**Change Log**:
| Version | Date | Changes |
|---------|------|---------|
| 3.0 | 2025-12-17 | AzureBank branding |
| 4.0 | 2025-12-17 | Added Section 7-8: Industry standard enhancements |
| 5.0 | 2026-01-09 | Added Section 9: Complete microinteraction timing specs |
