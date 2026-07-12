# ClaudeTeam2Figma - Design Recreation Prompt
## AzureBank Frontend Design to Figma

**Document Version**: 1.0
**Created**: 2025-12-17
**Purpose**: Comprehensive prompt for a second Claude team to recreate the AzureBank UI design in Figma
**Status**: READY FOR USE

---

## How to Use This Document

Copy the content between `=== BEGIN PROMPT ===` and `=== END PROMPT ===` and provide it to Claude with Figma MCP or Figma plugin access.

---

=== BEGIN PROMPT ===

# Figma Design Recreation Task
## AzureBank - Personal Banking Application

You are ClaudeTeam2Figma, tasked with recreating the complete UI design for AzureBank in Figma. You have access to all design specifications and must create pixel-perfect mockups.

---

## 1. PROJECT OVERVIEW

**App Name**: AzureBank
**Type**: Personal Banking Web Application
**Design System**: Based on Microsoft FluentUI v9
**Primary Color**: #006DE2 (Azure Blue)
**Target**: Desktop (1440px) and Mobile (375px)

---

## 2. BRAND IDENTITY

### Logo
- Text-based: "AzureBank" in Segoe UI Bold
- Icon option: Stylized "AB" monogram with rounded square background
- Primary color on white, or white on primary

### Color Palette

#### Primary Colors
| Name | Hex | Usage |
|------|-----|-------|
| Primary Blue | #006DE2 | Primary buttons, links, active states |
| Primary Dark | #005BB5 | Hover states |
| Primary Light | #E6F2FF | Backgrounds, selected states |

#### Neutral Colors
| Name | Hex | Usage |
|------|-----|-------|
| Gray 900 | #1F2937 | Primary text |
| Gray 700 | #374151 | Secondary text |
| Gray 500 | #6B7280 | Placeholder text |
| Gray 300 | #D1D5DB | Borders |
| Gray 100 | #F3F4F6 | Backgrounds |
| White | #FFFFFF | Cards, inputs |

#### Semantic Colors
| Name | Hex | Usage |
|------|-----|-------|
| Success | #34A853 | Deposits, positive amounts |
| Error | #DC2626 | Withdrawals, errors, negative amounts |
| Warning | #F59E0B | Warnings, pending states |
| Info | #0EA5E9 | Information, transfers |

---

## 3. TYPOGRAPHY

**Font Family**: Segoe UI (fallback: system-ui, sans-serif)

| Style | Size | Weight | Line Height | Usage |
|-------|------|--------|-------------|-------|
| Display | 32px | 700 | 40px | Hero balance |
| Title 1 | 28px | 600 | 36px | Page titles |
| Title 2 | 24px | 600 | 32px | Section headers |
| Title 3 | 20px | 600 | 28px | Card titles |
| Body 1 | 16px | 400 | 24px | Body text |
| Body 2 | 14px | 400 | 20px | Secondary text |
| Caption | 12px | 400 | 16px | Labels, hints |
| Button | 14px | 600 | 20px | Button text |

---

## 4. SPACING SYSTEM

| Token | Value | Usage |
|-------|-------|-------|
| xxs | 4px | Tight spacing |
| xs | 8px | Icon gaps |
| sm | 12px | Input padding |
| md | 16px | Card padding |
| lg | 24px | Section gaps |
| xl | 32px | Page padding |
| xxl | 48px | Large sections |

---

## 5. COMPONENT LIBRARY

### 5.1 Buttons

**Primary Button**
- Background: #006DE2
- Text: White, 14px, 600 weight
- Padding: 12px 24px
- Border Radius: 6px
- Height: 44px
- Hover: #005BB5
- Disabled: #9CA3AF with 50% opacity

**Secondary Button**
- Background: Transparent
- Border: 1px solid #D1D5DB
- Text: #374151
- Same dimensions as primary

**Danger Button**
- Background: #DC2626
- Text: White
- Hover: #B91C1C

### 5.2 Input Fields

- Height: 44px
- Border: 1px solid #D1D5DB
- Border Radius: 6px
- Padding: 12px 16px
- Focus: 2px solid #006DE2
- Error: Border #DC2626, error message in red below

### 5.3 Cards

- Background: White
- Border Radius: 12px
- Shadow: 0 1px 3px rgba(0,0,0,0.1)
- Padding: 24px

### 5.4 Balance Card (Hero Component)

```
┌─────────────────────────────────────────────────────────┐
│  [Icon] Savings Account                    ︙ (menu)    │
│  AB-1234-5678-90                                        │
│                                                         │
│  Current Balance                                        │
│  €12,450.00                              (large, bold)  │
│                                                         │
│  ┌─────────┐  ┌─────────┐  ┌─────────┐                │
│  │ Deposit │  │Withdraw │  │Transfer │                 │
│  └─────────┘  └─────────┘  └─────────┘                │
└─────────────────────────────────────────────────────────┘
```

- Background: Gradient from #006DE2 to #0052B3
- Text: White
- Balance: 32px, Bold
- Border Radius: 16px

### 5.5 Transaction Card

```
┌─────────────────────────────────────────────────────────┐
│  [↓] Deposit                              +€500.00     │
│      Salary payment                     Dec 17, 2025   │
└─────────────────────────────────────────────────────────┘
```

- Deposit icon: Green circle with down arrow
- Withdrawal icon: Red circle with up arrow
- Transfer icon: Blue circle with swap arrows
- Amount: Green for +, Red for -

### 5.6 Navigation

**Desktop Sidebar**
- Width: 240px
- Background: #F3F4F6
- Items: 48px height, 16px padding
- Active: #E6F2FF background, #006DE2 text

**Mobile Bottom Nav**
- Height: 64px
- 4 items: Dashboard, Accounts, Transfer, Profile
- Active: #006DE2 icon and label

---

## 6. SCREEN INVENTORY

Create the following screens at both 1440px (Desktop) and 375px (Mobile):

### 6.1 Authentication Screens

**Login Page**
```
┌─────────────────────────────────────────────────────────┐
│                                                         │
│                    [AzureBank Logo]                     │
│                                                         │
│                   Welcome Back                          │
│           Sign in to your account                       │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ Email                                            │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ Password                                    [👁]  │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │              Sign In (Primary Button)            │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│           Don't have an account? Register              │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

**Register Page**
```
┌─────────────────────────────────────────────────────────┐
│                                                         │
│                    [AzureBank Logo]                     │
│                                                         │
│              Create Your Account                        │
│        Join AzureBank in minutes                        │
│                                                         │
│  ┌──────────────────┐  ┌──────────────────┐           │
│  │ First Name       │  │ Surname          │           │
│  └──────────────────┘  └──────────────────┘           │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ @ Choose your AzureTag                          │  │
│  └─────────────────────────────────────────────────┘  │
│  ✓ @johnsmith is available                             │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ Email                                            │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ Password                                    [👁]  │  │
│  └─────────────────────────────────────────────────┘  │
│  Password strength: [████████░░] Strong                │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ Confirm Password                            [👁]  │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │           Create Account (Primary)               │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│         Already have an account? Sign In               │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

### 6.2 Dashboard

**Desktop Dashboard (1440px)**
```
┌─────────────────────────────────────────────────────────────────────────┐
│ ┌─────────┐                                      [Notifications] [👤]  │
│ │AzureBank│  Dashboard                           Welcome, John (@john) │
├─────────────┬───────────────────────────────────────────────────────────┤
│             │                                                           │
│ [Dashboard] │  ┌───────────────────────────────────────────────────┐  │
│ [Accounts]  │  │            BALANCE CARD (Hero)                    │  │
│ [Transfer]  │  │            €12,450.00                              │  │
│ [History]   │  │  [Deposit] [Withdraw] [Transfer]                  │  │
│             │  └───────────────────────────────────────────────────┘  │
│ [Settings]  │                                                           │
│ [Logout]    │  ┌─────────────────────┐  ┌─────────────────────────┐  │
│             │  │  Quick Actions      │  │  Recent Transactions    │  │
│             │  │                     │  │                         │  │
│             │  │  [+] New Account    │  │  ↓ Deposit   +€500.00  │  │
│             │  │  [↓] Deposit        │  │  ↑ Withdraw  -€50.00   │  │
│             │  │  [↑] Withdraw       │  │  → Transfer  -€200.00  │  │
│             │  │  [→] Transfer       │  │                         │  │
│             │  └─────────────────────┘  │  [View All →]           │  │
│             │                            └─────────────────────────┘  │
│             │                                                           │
│             │  ┌───────────────────────────────────────────────────┐  │
│             │  │  My Accounts                                      │  │
│             │  │                                                   │  │
│             │  │  [Savings] €12,450  [Checking] €2,300            │  │
│             │  │                                                   │  │
│             │  │  [+ Create New Account]                          │  │
│             │  └───────────────────────────────────────────────────┘  │
│             │                                                           │
└─────────────┴───────────────────────────────────────────────────────────┘
```

**Mobile Dashboard (375px)**
```
┌─────────────────────────────┐
│ AzureBank        [🔔] [👤]  │
├─────────────────────────────┤
│                             │
│ Welcome, John               │
│ @johnsmith                  │
│                             │
│ ┌─────────────────────────┐│
│ │    BALANCE CARD         ││
│ │    €12,450.00           ││
│ │                         ││
│ │ [Deposit][Withdraw]     ││
│ │ [Transfer]              ││
│ └─────────────────────────┘│
│                             │
│ Quick Actions               │
│ ┌─────┐┌─────┐┌─────┐┌─────┐│
│ │ New ││ Dep ││ With││Trans││
│ └─────┘└─────┘└─────┘└─────┘│
│                             │
│ Recent Transactions         │
│ ┌─────────────────────────┐│
│ │ ↓ Deposit    +€500.00   ││
│ │ ↑ Withdraw   -€50.00    ││
│ │ → Transfer   -€200.00   ││
│ └─────────────────────────┘│
│ [View All]                  │
│                             │
│ My Accounts                 │
│ ┌─────────────────────────┐│
│ │ Savings      €12,450.00 ││
│ │ Checking      €2,300.00 ││
│ └─────────────────────────┘│
│                             │
├─────────────────────────────┤
│ [🏠] [💳] [↔️] [👤]          │
│ Home  Acct  Send  Profile   │
└─────────────────────────────┘
```

### 6.3 Transfer Wizard (4 Steps)

**Step 1: Select Source**
```
┌─────────────────────────────────────────────────────────┐
│                  Transfer Money                    [X]  │
│                                                         │
│  Step 1 of 4: Select Account                           │
│  ●───○───○───○                                         │
│                                                         │
│  Which account do you want to send from?               │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ (●) Savings Account                             │  │
│  │     AB-1234-5678-90                             │  │
│  │     Balance: €12,450.00                         │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ (○) Checking Account                            │  │
│  │     AB-9876-5432-10                             │  │
│  │     Balance: €2,300.00                          │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│               [Cancel]          [Next →]               │
└─────────────────────────────────────────────────────────┘
```

**Step 2: Find Recipient**
```
┌─────────────────────────────────────────────────────────┐
│                  Transfer Money                    [X]  │
│                                                         │
│  Step 2 of 4: Find Recipient                           │
│  ●───●───○───○                                         │
│                                                         │
│  Enter the recipient's AzureTag:                       │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ @                                        [Search]│  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│  ─── Or select from recent recipients ───              │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ ★ @janesmith - Jane S.               [Select]  │  │
│  │ ★ @mikebrown - Mike B.               [Select]  │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │  ✓ Recipient Found                              │  │
│  │                                                 │  │
│  │  👤 Jane Smith                                  │  │
│  │     @janesmith                                  │  │
│  │                                                 │  │
│  │  [ ] Save to recent recipients                  │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│              [← Back]          [Next →]                │
└─────────────────────────────────────────────────────────┘
```

**Note**: v2.0 Privacy Fix - NO account list is shown for recipient. Sender only sees @AzureTag and masked name.

**Step 3: Enter Amount**
```
┌─────────────────────────────────────────────────────────┐
│                  Transfer Money                    [X]  │
│                                                         │
│  Step 3 of 4: Enter Amount                             │
│  ●───●───●───○                                         │
│                                                         │
│  From: Savings Account (€12,450.00)                    │
│  To: Jane Smith (@janesmith)                           │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │ €                                    500.00      │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│  Quick amounts:                                        │
│  [€50] [€100] [€250] [€500] [€1000]                   │
│                                                         │
│  Description (optional):                               │
│  ┌─────────────────────────────────────────────────┐  │
│  │ Lunch money                                      │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│  Your new balance: €11,950.00                          │
│                                                         │
│              [← Back]          [Review →]              │
└─────────────────────────────────────────────────────────┘
```

**Step 4: Confirm**
```
┌─────────────────────────────────────────────────────────┐
│                  Transfer Money                    [X]  │
│                                                         │
│  Step 4 of 4: Confirm Transfer                         │
│  ●───●───●───●                                         │
│                                                         │
│  ┌─────────────────────────────────────────────────┐  │
│  │              REVIEW YOUR TRANSFER               │  │
│  │                                                 │  │
│  │  From:    Savings Account                       │  │
│  │           AB-1234-5678-90                       │  │
│  │                                                 │  │
│  │  To:      Jane Smith                            │  │
│  │           @janesmith                            │  │
│  │                                                 │  │
│  │  Amount:  €500.00                               │  │
│  │  Note:    Lunch money                           │  │
│  │                                                 │  │
│  │  ─────────────────────────────────────────────  │  │
│  │                                                 │  │
│  │  ⚠ Please verify the recipient details before   │  │
│  │    confirming. Transfers cannot be reversed.   │  │
│  └─────────────────────────────────────────────────┘  │
│                                                         │
│              [← Back]     [Confirm Transfer]           │
└─────────────────────────────────────────────────────────┘
```

**Success State**
```
┌─────────────────────────────────────────────────────────┐
│                                                         │
│                         ✓                               │
│                    (green circle)                       │
│                                                         │
│              Transfer Successful!                       │
│                                                         │
│         €500.00 sent to Jane Smith                     │
│                                                         │
│         Reference: TXN-20251217-123456                 │
│                                                         │
│               [Done]  [New Transfer]                   │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

### 6.4 Additional Screens to Create

1. **Accounts List Page** - Grid of account cards
2. **Account Detail Page** - Account info + transaction history
3. **Transaction History Page** - Filtered list with date groups
4. **Deposit Dialog** - Amount input modal
5. **Withdraw Dialog** - Amount input modal
6. **Create Account Dialog** - Name, type, initial deposit
7. **Profile/Settings Page** - User info, AzureTag display
8. **404 Not Found** - Error page
9. **Loading States** - Skeleton screens for all major views
10. **Empty States** - No accounts, no transactions

---

## 7. COMPONENT STATES

For each interactive component, create:

### Buttons
- Default
- Hover
- Active/Pressed
- Disabled
- Loading (with spinner)

### Input Fields
- Default/Empty
- Focused
- Filled
- Error
- Disabled

### Cards
- Default
- Hover
- Selected

---

## 8. RESPONSIVE BEHAVIOR

| Breakpoint | Width | Layout |
|------------|-------|--------|
| Mobile | 375px | Single column, bottom nav |
| Tablet | 768px | 2-column, sidebar collapsed |
| Desktop | 1440px | 3-column, full sidebar |

---

## 9. FIGMA FILE STRUCTURE

Create the following pages in Figma:

1. **Cover** - Project title and version
2. **Design System** - Colors, typography, spacing, icons
3. **Components** - All reusable components with variants
4. **Auth Screens** - Login, Register
5. **Dashboard** - Desktop and Mobile
6. **Accounts** - List, Detail
7. **Transfers** - 4-step wizard
8. **Dialogs** - Deposit, Withdraw, Create Account
9. **States** - Loading, Empty, Error
10. **Prototype** - Interactive flows

---

## 10. DELIVERABLES CHECKLIST

- [ ] Design system page with all tokens
- [ ] Component library with all states
- [ ] Login screen (Desktop + Mobile)
- [ ] Register screen (Desktop + Mobile)
- [ ] Dashboard (Desktop + Mobile)
- [ ] Accounts list (Desktop + Mobile)
- [ ] Account detail (Desktop + Mobile)
- [ ] Transfer wizard - all 4 steps (Desktop + Mobile)
- [ ] Transaction history (Desktop + Mobile)
- [ ] All dialog modals
- [ ] Skeleton loading states
- [ ] Empty states
- [ ] Interactive prototype for main user flows

---

**Note**: All designs must follow accessibility guidelines with 4.5:1 contrast ratio minimum for text and 3:1 for UI elements.

=== END PROMPT ===

---

## Post-Generation Checklist

After ClaudeTeam2Figma completes the task:

- [ ] Verify all screens are created
- [ ] Check color accuracy against specs
- [ ] Verify typography matches
- [ ] Test responsive variants
- [ ] Review component states
- [ ] Validate accessibility contrast
- [ ] Export design tokens if needed
- [ ] Generate developer handoff specs

---

**Document Status**: READY FOR USE
