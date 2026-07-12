# AzureBank - Figma Screen Master Plan

**Document Version**: 1.0
**Created**: 2026-01-01
**Status**: EXECUTION READY
**Author**: Claude Code

---

## Executive Summary

This document provides a comprehensive, systematic plan to complete all missing Figma screens for the AzureBank application. Based on thorough analysis of the architecture documents, existing HTML files, and design tokens, we've identified **13 missing screens** that need to be created to achieve full design coverage.

---

## 1. Current State Analysis

### 1.1 Existing HTML Files (13 total)

| # | File | Platform | Type | Status |
|---|------|----------|------|--------|
| 1 | login-mobile.html | Mobile (375px) | Auth | ✅ Complete |
| 2 | login-desktop.html | Desktop (1440px) | Auth | ✅ Complete |
| 3 | register-mobile.html | Mobile (375px) | Auth | ✅ Complete |
| 4 | register-desktop.html | Desktop (1440px) | Auth | ✅ Complete |
| 5 | dashboard-mobile.html | Mobile (375px) | Main | ✅ Complete |
| 6 | dashboard-desktop.html | Desktop (1440px) | Main | ✅ Complete |
| 7 | dialog-deposit.html | Mobile (375px) | Dialog | ✅ Complete |
| 8 | dialog-withdraw.html | Mobile (375px) | Dialog | ✅ Complete |
| 9 | transfer-wizard-step1.html | Mobile (375px) | Wizard | ✅ Complete |
| 10 | transfer-wizard-step2-destination.html | Mobile (375px) | Wizard | ✅ Complete |
| 11 | transfer-wizard-step3-amount.html | Mobile (375px) | Wizard | ✅ Complete |
| 12 | transfer-wizard-step4-confirm.html | Mobile (375px) | Wizard | ✅ Complete |
| 13 | transfer-success.html | Mobile (375px) | Wizard | ✅ Complete |

### 1.2 Missing Screens Identified

#### MOBILE (375x812) - Missing 4 screens:
| # | Screen | Priority | Complexity |
|---|--------|----------|------------|
| 1 | Transaction History (Full Page) | HIGH | Medium |
| 2 | Transaction Detail | MEDIUM | Low |
| 3 | Account Selection / Accounts List | MEDIUM | Low |
| 4 | Settings / Profile | LOW | Medium |

#### DESKTOP (1440x900) - Missing 9 screens:
| # | Screen | Priority | Complexity |
|---|--------|----------|------------|
| 1 | Deposit Dialog/Modal | HIGH | Low |
| 2 | Withdraw Dialog/Modal | HIGH | Low |
| 3 | Transfer Step 1 (Select Account) | HIGH | Medium |
| 4 | Transfer Step 2 (Destination) | HIGH | Medium |
| 5 | Transfer Step 3 (Amount) | HIGH | Medium |
| 6 | Transfer Step 4 (Confirm) | HIGH | Medium |
| 7 | Transfer Success | HIGH | Low |
| 8 | Transaction History (Full Page) | HIGH | Medium |
| 9 | Settings / Profile | LOW | Medium |

---

## 2. Design System Reference

### 2.1 Core Design Tokens

```
COLORS:
├── Primary Blue: #006DE2 (brand.60)
├── Primary Hover: #004DA0 (brand.40)
├── Primary Light: #E6F0FC (brand.120)
├── Background: #F7F8FA
├── Card Background: #FFFFFF
├── Border Default: #D1D5DB (neutral.300)
├── Border Light: #E5E7EB (neutral.200)
├── Text Primary: #1F2937 (neutral.800)
├── Text Secondary: #6B7280 (neutral.500)
├── Text Placeholder: #9CA3AF (neutral.400)
├── Success: #34A853 / Background: #E6F4EA
├── Error: #EA4335 / Background: #FCE8E6
├── Warning: #F59E0B / Background: #FEF3E2
└── Info: #0EA5E9 / Background: #E0F2FE

TYPOGRAPHY:
├── Font Family: 'Segoe UI', -apple-system, BlinkMacSystemFont, sans-serif
├── Mono: Consolas, 'Courier New', monospace
├── Heading XL: 28px/700
├── Heading LG: 24px/600
├── Heading MD: 20px/600
├── Body: 16px/400
├── Body SM: 14px/400
└── Caption: 12px/400

SPACING:
├── xs: 4px
├── sm: 8px
├── md: 12px
├── base: 16px
├── lg: 20px
├── xl: 24px
└── 2xl: 32px

BORDER RADIUS:
├── Inputs/Buttons: 8px
├── Cards: 12px
├── Large Cards: 16px
└── Dialogs: 24px (top corners for mobile sheets)

SHADOWS:
├── sm: 0px 1px 2px rgba(0,0,0,0.05)
├── md: 0px 4px 6px rgba(0,0,0,0.1)
└── lg: 0px 8px 24px rgba(0,0,0,0.1)
```

### 2.2 Platform-Specific Patterns

#### Mobile (375x812):
- Header: 56px height, logo left, avatar right
- Bottom Nav: 72px height with safe area padding
- Content padding: 16px horizontal
- Cards: Full-width with 16px margins
- Dialogs: Bottom sheet style with 24px top radius

#### Desktop (1440x900):
- Header: 64px height with horizontal nav
- Main content: Dual column layout (800px + 360px sidebar)
- Content padding: 32px
- Dialogs: Centered modals with overlay
- Cards: Variable width based on grid

---

## 3. Execution Plan

### PHASE 1: Desktop Priority Screens (HIGH IMPACT)
**Estimated Files: 7 | Focus: Critical user flows**

#### 1.1 Desktop Dialogs (deposit & withdraw)
```
File: dialog-deposit-desktop.html
File: dialog-withdraw-desktop.html

Pattern: Centered modal overlay
- Overlay: rgba(0,0,0,0.5) full screen
- Modal: 480px width, white, 16px radius
- Header: Title + X close button
- Icon: 64px circle with gradient
- Form: Account selector, amount input, quick amounts
- Actions: Primary button (full width), Cancel link
```

#### 1.2 Desktop Transfer Wizard (4 steps + success)
```
File: transfer-desktop-step1.html
File: transfer-desktop-step2-destination.html
File: transfer-desktop-step3-amount.html
File: transfer-desktop-step4-confirm.html
File: transfer-desktop-success.html

Pattern: Full page with header + progress + content
- Header: Same as dashboard desktop
- Progress: Horizontal stepper (centered)
- Content: Centered card (600px width)
- Step 1: Account selection cards
- Step 2: Recipient input + recent contacts
- Step 3: Amount input + summary
- Step 4: Full review + confirm
- Success: Checkmark animation + details + actions
```

### PHASE 2: Transaction History (Both Platforms)
**Estimated Files: 2 | Focus: Data display**

#### 2.1 Mobile Transaction History
```
File: history-mobile.html

Pattern: Full page list with filters
- Header: Back arrow, title, filter icon
- Filter tabs: All, Deposits, Withdrawals, Transfers
- Date range picker (optional)
- Transaction list: Virtualized scroll
- Each item: Icon, description, date, amount
- Empty state: Illustration + message
```

#### 2.2 Desktop Transaction History
```
File: history-desktop.html

Pattern: Dashboard layout with history focus
- Header: Same as dashboard
- Main content: Full width (no sidebar)
- Filters: Date range pickers + type dropdown
- Table view: Columns (Date, Description, Type, Amount, Balance)
- Pagination: Bottom with page numbers
- Export button: Top right
```

### PHASE 3: Transaction Detail
**Estimated Files: 2 | Focus: Single item view**

#### 3.1 Mobile Transaction Detail
```
File: transaction-detail-mobile.html

Pattern: Full page detail view
- Header: Back arrow + "Transaction Details"
- Hero: Large amount with type icon
- Status badge: Completed/Pending
- Details card:
  - Date & Time
  - Transaction ID
  - From/To account
  - Description
  - Reference number
- Actions: Share, Download receipt
```

#### 3.2 Desktop Transaction Detail
```
File: transaction-detail-desktop.html

Pattern: Modal or slide-over panel
- Panel: 480px width from right
- Header: Title + close
- Same content as mobile
- Print/Download options
```

### PHASE 4: Account Screens
**Estimated Files: 2 | Focus: Account management**

#### 4.1 Mobile Accounts List
```
File: accounts-mobile.html

Pattern: List of account cards
- Header: "My Accounts" + Add button
- Account cards: Similar to dashboard but larger
- Each card:
  - Account type icon
  - Name + last 4 digits
  - Balance
  - Chevron for detail
- Total balance summary at top
```

#### 4.2 Desktop Accounts (Optional - part of dashboard)
```
Could be integrated into dashboard sidebar
or as expanded view when clicking "Accounts" nav
```

### PHASE 5: Settings/Profile (LOW PRIORITY)
**Estimated Files: 2 | Focus: User settings**

#### 5.1 Mobile Settings
```
File: settings-mobile.html

Pattern: List of settings sections
- Header: Back + "Settings"
- Profile section: Avatar, name, email, edit
- Preferences: Notifications, theme
- Security: Change password, biometrics
- Support: Help, Contact, FAQ
- Logout button (bottom, destructive)
```

#### 5.2 Desktop Settings
```
File: settings-desktop.html

Pattern: Dashboard layout with settings panel
- Left: Settings navigation
- Right: Settings content area
- Tabs: Profile, Security, Notifications, etc.
```

---

## 4. Detailed Task Breakdown

### PHASE 1: Desktop Dialogs & Transfer (7 files)

| Task | File | Est. Lines | Dependencies |
|------|------|------------|--------------|
| 1.1.1 | dialog-deposit-desktop.html | ~300 | tokens.json |
| 1.1.2 | dialog-withdraw-desktop.html | ~300 | tokens.json |
| 1.2.1 | transfer-desktop-step1.html | ~350 | dashboard-desktop pattern |
| 1.2.2 | transfer-desktop-step2-destination.html | ~400 | step1 pattern |
| 1.2.3 | transfer-desktop-step3-amount.html | ~400 | step2 pattern |
| 1.2.4 | transfer-desktop-step4-confirm.html | ~450 | step3 pattern |
| 1.2.5 | transfer-desktop-success.html | ~300 | step4 pattern |

### PHASE 2: Transaction History (2 files)

| Task | File | Est. Lines | Dependencies |
|------|------|------------|--------------|
| 2.1 | history-mobile.html | ~400 | dashboard-mobile pattern |
| 2.2 | history-desktop.html | ~500 | dashboard-desktop pattern |

### PHASE 3: Transaction Detail (2 files)

| Task | File | Est. Lines | Dependencies |
|------|------|------------|--------------|
| 3.1 | transaction-detail-mobile.html | ~300 | history-mobile |
| 3.2 | transaction-detail-desktop.html | ~350 | history-desktop |

### PHASE 4: Accounts (1-2 files)

| Task | File | Est. Lines | Dependencies |
|------|------|------------|--------------|
| 4.1 | accounts-mobile.html | ~350 | dashboard-mobile pattern |

### PHASE 5: Settings (2 files)

| Task | File | Est. Lines | Dependencies |
|------|------|------------|--------------|
| 5.1 | settings-mobile.html | ~400 | accounts-mobile pattern |
| 5.2 | settings-desktop.html | ~500 | dashboard-desktop pattern |

---

## 5. Execution Workflow

### Step-by-Step Process for Each File:

```
1. CREATE HTML FILE
   └── Use design tokens from tokens.json
   └── Follow platform pattern (mobile/desktop)
   └── Include all data-name attributes for Figma
   └── Use semantic CSS class names
   └── Inline all styles (no external CSS)

2. VALIDATE HTML
   └── Check all colors match tokens
   └── Verify typography scale
   └── Confirm spacing consistency
   └── Test in browser at correct viewport

3. IMPORT TO FIGMA
   └── Use html-to-design MCP tool
   └── Specify correct name (no .html suffix)
   └── Verify import succeeded

4. VERIFY IN FIGMA
   └── Check fonts are Segoe UI
   └── Verify colors match design system
   └── Confirm spacing and sizing
   └── Test responsiveness if applicable

5. RENAME SECTION
   └── Remove timestamp from name (manual in Figma)
   └── Organize in correct position on canvas
```

---

## 6. Best Practices

### 6.1 HTML Structure Best Practices

```html
<!-- Always include viewport meta -->
<meta name="viewport" content="width=[375|1440], initial-scale=1.0">

<!-- Always use design system font -->
font-family: 'Segoe UI', 'Segoe UI Web (West European)',
             -apple-system, BlinkMacSystemFont, Roboto,
             'Helvetica Neue', sans-serif;

<!-- Always use data-name for Figma layer names -->
<div class="component" data-name="ComponentName">

<!-- Use semantic color variables from tokens -->
background: #006DE2; /* brand.60 - Primary */
color: #1F2937;      /* neutral.800 - Text Primary */
border: 1px solid #D1D5DB; /* neutral.300 - Border */
```

### 6.2 Naming Conventions

```
Files:
├── [feature]-[platform].html
├── [feature]-[variant]-[platform].html
└── dialog-[action]-[platform].html

Examples:
├── history-mobile.html
├── transfer-desktop-step1.html
└── dialog-deposit-desktop.html

Figma Sections:
├── Dashboard - Mobile
├── Dashboard - Desktop
├── Transfer Step 1 - Mobile
├── Transfer Step 1 - Desktop
└── Deposit Dialog - Desktop
```

### 6.3 Component Patterns

```
BUTTONS:
├── Primary: bg #006DE2, text #FFFFFF, radius 8px, height 48-52px
├── Secondary: bg transparent, text #006DE2, border 1px solid
└── Destructive: bg #EA4335, text #FFFFFF

INPUTS:
├── Default: border #D1D5DB, radius 8px, height 48-52px
├── Focus: border #006DE2 (2px)
├── Error: border #EA4335
└── Disabled: bg #F3F4F6

CARDS:
├── Elevated: bg #FFFFFF, shadow-md, radius 12-16px
├── Outlined: bg #FFFFFF, border 1px solid #E5E7EB
└── Gradient: linear-gradient(135deg, #006DE2, #004DA0)

DIALOGS:
├── Mobile: Bottom sheet, radius 24px top corners
├── Desktop: Centered modal, radius 16px, max-width 480-600px
└── Overlay: bg rgba(0,0,0,0.5)
```

### 6.4 Accessibility Considerations

```
- Maintain minimum contrast ratios (4.5:1 for text)
- Use semantic HTML elements where possible
- Include proper heading hierarchy
- Ensure touch targets are minimum 44x44px
- Use clear focus states for interactive elements
```

---

## 7. Priority Matrix

| Priority | Screens | Reason |
|----------|---------|--------|
| P0 - Critical | Desktop Transfer Wizard (5) | Core user flow, no alternatives |
| P0 - Critical | Desktop Deposit/Withdraw (2) | Core actions from dashboard |
| P1 - High | Transaction History (2) | Listed in bottom nav, expected |
| P2 - Medium | Transaction Detail (2) | Linked from history |
| P2 - Medium | Accounts List Mobile (1) | Listed in bottom nav |
| P3 - Low | Settings/Profile (2) | Can be deferred |

---

## 8. Final Checklist

### Before Starting Each Screen:
- [ ] Review existing similar screen for patterns
- [ ] Open tokens.json for color/typography reference
- [ ] Determine exact viewport dimensions
- [ ] List all required components/states

### After Creating Each Screen:
- [ ] HTML validates without errors
- [ ] Renders correctly at target viewport
- [ ] All colors match design tokens
- [ ] Typography follows scale
- [ ] Import to Figma successful
- [ ] Fonts render as Segoe UI
- [ ] Section renamed properly

### Phase Completion:
- [ ] All files in phase created
- [ ] All imported to Figma
- [ ] Visual QA passed
- [ ] Patterns consistent across screens

---

## 9. Summary

**Total New Screens to Create: 13-15**
- Desktop: 9 screens (HIGH PRIORITY)
- Mobile: 4-6 screens (MEDIUM PRIORITY)

**Execution Order:**
1. Desktop Deposit/Withdraw Dialogs (2)
2. Desktop Transfer Wizard (5)
3. Transaction History (2)
4. Transaction Detail (2)
5. Accounts Mobile (1)
6. Settings (2) - if time permits

**Estimated Total HTML Lines: ~4,500-5,000**

---

*This plan provides a systematic, prioritized approach to completing all missing Figma screens while maintaining design consistency and following established patterns.*
