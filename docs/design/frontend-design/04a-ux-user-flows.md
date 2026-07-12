# UX User Flows
## Bank Account Management System

**Document Version**: 4.0
**Created**: 2025-12-16
**Updated**: 2026-01-09
**Author**: UX/UI Expert (Virtual Team Member)
**Status**: FINAL - BFF Pattern Aligned

**Bank Name**: AzureBank

---

## 1. Overview

This document defines all user flows for the banking application, covering authentication, account management, transactions, and error handling patterns.

### Design Principles
1. **Minimal Friction** - Reduce steps to complete any action
2. **Clear Feedback** - Always show operation status
3. **Error Prevention** - Validate before submission
4. **Recovery Support** - Clear paths from error states
5. **Accessibility First** - WCAG 2.1 AA compliance

---

## 2. User Flows

### 2.1 Registration Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                        REGISTRATION FLOW                             │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────┐                                                    │
│  │   Landing    │                                                    │
│  │    Page      │                                                    │
│  └──────┬───────┘                                                    │
│         │                                                            │
│         │ Click "Create Account"                                     │
│         ▼                                                            │
│  ┌──────────────┐                                                    │
│  │ Registration │                                                    │
│  │    Form      │                                                    │
│  │              │                                                    │
│  │ ┌──────────┐ ┌──────────┐                                        │
│  │ │FirstName │ │ Surname  │ ◄── Side by side on desktop            │
│  │ └──────────┘ └──────────┘     Stacked on mobile                  │
│  │ ┌──────────────────────┐                                         │
│  │ │@ AzureTag            │  ◄── Public identifier (@username)      │
│  │ └──────────────────────┘      - 3-20 chars, alphanumeric + _     │
│  │ ┌──────────────────────┐      - Uniqueness check (debounced)     │
│  │ │Email                 │  ◄── Real-time validation               │
│  │ └──────────────────────┘      - Format check                     │
│  │ ┌──────────────────────┐      - Uniqueness check (debounced)     │
│  │ │Password          [👁]│                                         │
│  │ └──────────────────────┘  ◄── Password strength indicator        │
│  │ ┌──────────────────────┐      - Min 8 chars                      │
│  │ │Confirm Password  [👁]│      - 1 uppercase, 1 lowercase         │
│  │ └──────────────────────┘      - 1 number                         │
│  │              │                                                    │
│  │ [Create Account]                                                  │
│  └──────┬───────┘                                                    │
│         │                                                            │
│         ├─────────────────────────────────────────┐                  │
│         │ Success                                 │ Error            │
│         ▼                                         ▼                  │
│  ┌──────────────┐                          ┌──────────────┐          │
│  │   Success    │                          │ Error Toast  │          │
│  │   Message    │                          │              │          │
│  │              │                          │ - Email taken│          │
│  │ "Welcome,    │                          │ - Server err │          │
│  │  [FirstName]!│                          │              │          │
│  │  Account     │                          │ [Try Again]  │          │
│  │  created!"   │                          └──────┬───────┘          │
│  └──────┬───────┘                                 │                  │
│         │                                         │                  │
│         │ Auto-redirect (2s)                      │ Return to form   │
│         │ or click "Go to Login"                  │                  │
│         ▼                                         │                  │
│  ┌──────────────┐                                 │                  │
│  │  Login Page  │◄────────────────────────────────┘                  │
│  └──────────────┘                                                    │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

**Flow Steps**:
1. User lands on login page or navigates to registration
2. User enters first name and surname
3. User chooses their AzureTag (public @username for receiving transfers)
4. User enters email and password (with confirmation)
5. Real-time validation provides immediate feedback
6. On submit: show loading state on button
7. Success: Show personalized message with first name and AzureTag, auto-redirect to login
8. Error: Show inline error, keep form data, allow retry

**Validation Rules**:
- First Name: Required, 2-50 characters, letters only (with spaces, hyphens, apostrophes)
- Surname: Required, 2-50 characters, letters only (with spaces, hyphens, apostrophes)
- **AzureTag**: Required, 3-20 characters, starts with letter, alphanumeric + underscore only, unique (API check), IMMUTABLE after creation
- Email: Valid format, unique (API check)
- Password: Min 8 chars, 1 upper, 1 lower, 1 number
- Confirm Password: Must match password

**Form Field Order**:
1. First Name + Surname (side by side on desktop, stacked on mobile)
2. AzureTag (with @ prefix shown)
3. Email
4. Password
5. Confirm Password
6. Create Account button

**AzureTag Guidelines** (shown to user):
- This is your public identifier for receiving money transfers
- Others will find you by searching for your AzureTag
- Choose carefully - it cannot be changed after registration
- Example: @johnsmith, @maria_garcia, @alex2025

---

### 2.2 Login Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                           LOGIN FLOW                                 │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────┐                                                    │
│  │  Login Page  │                                                    │
│  │              │                                                    │
│  │ ┌──────────┐ │                                                    │
│  │ │Email     │ │                                                    │
│  │ └──────────┘ │                                                    │
│  │ ┌──────────┐ │                                                    │
│  │ │Password  │ │  ◄── Show/Hide toggle                             │
│  │ └──────────┘ │                                                    │
│  │              │                                                    │
│  │ [Sign In]    │                                                    │
│  │              │                                                    │
│  │ "No account? │                                                    │
│  │  Register"   │                                                    │
│  └──────┬───────┘                                                    │
│         │                                                            │
│         │ Submit                                                     │
│         ▼                                                            │
│  ┌──────────────┐                                                    │
│  │   Loading    │  ◄── Button shows spinner                         │
│  │    State     │      Inputs disabled                              │
│  └──────┬───────┘                                                    │
│         │                                                            │
│         ├─────────────────────────────────────────┐                  │
│         │ Success                                 │ Error            │
│         ▼                                         ▼                  │
│  ┌──────────────┐                          ┌──────────────┐          │
│  │ Session      │                          │ Error State  │          │
│  │ Established  │                          │              │          │
│  │              │                          │ "Invalid     │          │
│  │ Store User   │                          │  credentials"│          │
│  │ in Redux     │                          │              │          │
│  └──────┬───────┘                          │ [Try Again]  │          │
│         │                                  └──────┬───────┘          │
│         │                                         │                  │
│         │                                         │ Clear password   │
│         │                                         │ Focus email      │
│         ▼                                         ▼                  │
│  ┌──────────────┐                          ┌──────────────┐          │
│  │  Dashboard   │                          │  Login Form  │          │
│  │              │                          │  (Reset)     │          │
│  └──────────────┘                          └──────────────┘          │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

**Flow Steps** (BFF Pattern):
1. User enters email and password
2. Click "Sign In" or press Enter
3. Show loading spinner on button
4. Success: BFF creates session, sets HTTP-only cookie, returns user info → Store user in Redux, redirect to Dashboard
5. Error: Show error message, clear password, focus email field

**Security Considerations** (BFF Pattern):
- No "remember me" checkbox (session managed server-side)
- HTTP-only session cookie (JavaScript cannot access)
- No JWT in browser (maximum XSS protection)
- Generic error message (don't reveal if email exists)
- Rate limiting handled by backend

---

### 2.3 Dashboard Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                         DASHBOARD FLOW                               │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                         HEADER                                │   │
│  │  [Logo]                              [User Menu ▼] [Logout]   │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │                       NO ACCOUNT STATE                         │ │
│  │                                                                 │ │
│  │     ┌─────────────────────────────────────────────────┐        │ │
│  │     │                                                  │        │ │
│  │     │  "Welcome! You don't have a bank account yet."  │        │ │
│  │     │                                                  │        │ │
│  │     │              [Create Account]                    │        │ │
│  │     │                                                  │        │ │
│  │     └─────────────────────────────────────────────────┘        │ │
│  │                                                                 │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                              │                                       │
│                              │ Click "Create Account"                │
│                              ▼                                       │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │                    CREATE ACCOUNT DIALOG                       │ │
│  │                                                                 │ │
│  │     Initial Balance: [    0.00    ] (optional)                 │ │
│  │                                                                 │ │
│  │     [Cancel]                          [Create]                  │ │
│  │                                                                 │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                              │                                       │
│                              │ Success                               │
│                              ▼                                       │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │                     MAIN DASHBOARD                              │ │
│  │                                                                 │ │
│  │  ┌─────────────────────────────────────────────────────────┐   │ │
│  │  │              BALANCE CARD                                │   │ │
│  │  │                                                          │   │ │
│  │  │  Account: #1234567890                                    │   │ │
│  │  │                                                          │   │ │
│  │  │  Current Balance                                         │   │ │
│  │  │  $12,345.67                                              │   │ │
│  │  │                                                          │   │ │
│  │  │  [Deposit]  [Withdraw]  [Transfer]                       │   │ │
│  │  │                                                          │   │ │
│  │  └─────────────────────────────────────────────────────────┘   │ │
│  │                                                                 │ │
│  │  ┌─────────────────────────────────────────────────────────┐   │ │
│  │  │           RECENT TRANSACTIONS                            │   │ │
│  │  │                                                          │   │ │
│  │  │  Date        Description          Amount                 │   │ │
│  │  │  ─────────────────────────────────────────────           │   │ │
│  │  │  Dec 17      Deposit              +$500.00               │   │ │
│  │  │  Dec 16      Transfer to #9876    -$200.00               │   │ │
│  │  │  Dec 15      Withdrawal           -$100.00               │   │ │
│  │  │  ...                                                     │   │ │
│  │  │                                                          │   │ │
│  │  │  [View All Transactions]                                 │   │ │
│  │  └─────────────────────────────────────────────────────────┘   │ │
│  │                                                                 │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

**Flow Branches**:
1. **New User (No Account)**: Show "Create Account" CTA
2. **Existing User**: Show balance card and recent transactions
3. **Quick Actions**: Deposit, Withdraw, Transfer buttons

---

### 2.4 Deposit Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                          DEPOSIT FLOW                                │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────┐                                                    │
│  │  Dashboard   │                                                    │
│  │              │                                                    │
│  │  [Deposit]   │◄── Click                                          │
│  └──────┬───────┘                                                    │
│         │                                                            │
│         ▼                                                            │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                      DEPOSIT DIALOG                           │   │
│  │                                                               │   │
│  │     Current Balance: $12,345.67                               │   │
│  │                                                               │   │
│  │     Amount to Deposit:                                        │   │
│  │     ┌─────────────────────────────────────────┐               │   │
│  │     │ $  [           ]                        │               │   │
│  │     └─────────────────────────────────────────┘               │   │
│  │                                                               │   │
│  │     Quick amounts: [$50] [$100] [$500] [$1000]                │   │
│  │                                                               │   │
│  │     New Balance: $12,845.67  ◄── Live preview                 │   │
│  │                                                               │   │
│  │     [Cancel]                              [Deposit]           │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│         │                                                            │
│         ├─────────────────────────────────────────┐                  │
│         │ Success                                 │ Error            │
│         ▼                                         ▼                  │
│  ┌──────────────────────┐                 ┌──────────────────────┐   │
│  │   SUCCESS STATE      │                 │   ERROR STATE        │   │
│  │                      │                 │                      │   │
│  │   [Checkmark Icon]   │                 │   [Error Icon]       │   │
│  │                      │                 │                      │   │
│  │   "Successfully      │                 │   "Deposit failed"   │   │
│  │    deposited         │                 │                      │   │
│  │    $500.00"          │                 │   [Try Again]        │   │
│  │                      │                 │                      │   │
│  │   New Balance:       │                 └──────────────────────┘   │
│  │   $12,845.67         │                                            │
│  │                      │                                            │
│  │   [Done]             │                                            │
│  └──────────┬───────────┘                                            │
│             │                                                        │
│             │ Click "Done" or auto-close (3s)                        │
│             ▼                                                        │
│  ┌──────────────────────┐                                            │
│  │   Dashboard          │                                            │
│  │   (Balance Updated)  │                                            │
│  │   (Transaction Added)│                                            │
│  └──────────────────────┘                                            │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

**Flow Steps**:
1. Click "Deposit" button on dashboard
2. Dialog opens with current balance displayed
3. Enter amount (or use quick amount buttons)
4. Live preview shows new balance
5. Click "Deposit" to confirm
6. Success: Show confirmation, update balance
7. Auto-close dialog after 3 seconds

**Validation**:
- Amount must be > 0
- Amount must be a valid number
- Max 2 decimal places

---

### 2.5 Withdrawal Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                        WITHDRAWAL FLOW                               │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────┐                                                    │
│  │  Dashboard   │                                                    │
│  │              │                                                    │
│  │  [Withdraw]  │◄── Click                                          │
│  └──────┬───────┘                                                    │
│         │                                                            │
│         ▼                                                            │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                     WITHDRAW DIALOG                           │   │
│  │                                                               │   │
│  │     Current Balance: $12,345.67                               │   │
│  │     Available: $12,345.67  ◄── Same as balance (no overdraft) │   │
│  │                                                               │   │
│  │     Amount to Withdraw:                                       │   │
│  │     ┌─────────────────────────────────────────┐               │   │
│  │     │ $  [           ]                        │               │   │
│  │     └─────────────────────────────────────────┘               │   │
│  │                                                               │   │
│  │     [Withdraw All: $12,345.67]  ◄── Quick action              │   │
│  │                                                               │   │
│  │     New Balance: $11,845.67  ◄── Live preview                 │   │
│  │                                                               │   │
│  │     ⚠️ "Insufficient funds" (if amount > balance)             │   │
│  │                                                               │   │
│  │     [Cancel]                              [Withdraw]          │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│         │                                                            │
│         ├──────────────────────────────────────────┬─────────────┐   │
│         │ Success                                  │ Insufficient│   │
│         ▼                                          │ Funds       │   │
│  ┌──────────────────────┐                          ▼             │   │
│  │   SUCCESS STATE      │                 ┌──────────────────┐   │   │
│  │                      │                 │  VALIDATION      │   │   │
│  │   [Checkmark Icon]   │                 │  ERROR           │   │   │
│  │                      │                 │                  │   │   │
│  │   "Successfully      │                 │  "Cannot withdraw│   │   │
│  │    withdrawn         │                 │   more than      │   │   │
│  │    $500.00"          │                 │   available      │   │   │
│  │                      │                 │   balance"       │   │   │
│  │   [Done]             │                 │                  │   │   │
│  └──────────────────────┘                 │  [Adjust Amount] │   │   │
│                                           └──────────────────┘   │   │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

**Flow Steps**:
1. Click "Withdraw" button on dashboard
2. Dialog opens showing current and available balance
3. Enter amount (or use "Withdraw All" button)
4. Real-time validation prevents overdraft
5. If amount > balance: Show inline warning, disable button
6. Click "Withdraw" to confirm
7. Success: Show confirmation, update balance

**Validation**:
- Amount must be > 0
- Amount must be <= current balance (NO OVERDRAFT)
- Show real-time feedback if amount exceeds balance

---

### 2.6 External Transfer Flow

The external transfer flow allows users to send money to other AzureBank users.
This is a **4-step process** designed for security and clarity.

**Security Design**:
- Internal database IDs are NEVER exposed to the frontend
- Users are identified by public AzureTag (@username) or Account Number
- Recipient display shows masked name ("John S.") for privacy
- Confirmation step is ALWAYS required

```
┌─────────────────────────────────────────────────────────────────────┐
│                    EXTERNAL TRANSFER FLOW (4 Steps)                  │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────┐                                                    │
│  │  Dashboard   │                                                    │
│  │              │                                                    │
│  │  [Transfer]  │◄── Click                                          │
│  └──────┬───────┘                                                    │
│         │                                                            │
│         ▼                                                            │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │              STEP 1: SELECT SOURCE ACCOUNT                    │   │
│  │                                                               │   │
│  │     Select Account to Transfer From:                          │   │
│  │     ┌─────────────────────────────────────────────────────┐   │   │
│  │     │  ◉ Checking Account                                 │   │   │
│  │     │    AB-1234-5678-90                                  │   │   │
│  │     │    Balance: $12,345.67                              │   │   │
│  │     ├─────────────────────────────────────────────────────┤   │   │
│  │     │  ○ Savings Account                                  │   │   │
│  │     │    AB-9876-5432-10                                  │   │   │
│  │     │    Balance: $5,000.00                               │   │   │
│  │     └─────────────────────────────────────────────────────┘   │   │
│  │                                                               │   │
│  │     [Cancel]                                    [Next →]      │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│         │                                                            │
│         ▼                                                            │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │              STEP 2: FIND RECIPIENT                           │   │
│  │                                                               │   │
│  │     Find recipient by AzureTag or Account Number:             │   │
│  │     ┌─────────────────────────────────────────┐               │   │
│  │     │ @johnsmith                              │ [Search]      │   │
│  │     └─────────────────────────────────────────┘               │   │
│  │                                                               │   │
│  │     ── OR search by Account Number ──                         │   │
│  │     ┌─────────────────────────────────────────┐               │   │
│  │     │ AB-____-____-__                         │ [Search]      │   │
│  │     └─────────────────────────────────────────┘               │   │
│  │                                                               │   │
│  │     ┌─────────────────────────────────────────────────────┐   │   │
│  │     │  ✓ RECIPIENT FOUND                                  │   │   │
│  │     │                                                     │   │   │
│  │     │  @johnsmith                                         │   │   │
│  │     │  John S.  ◄── Masked name for privacy               │   │   │
│  │     │                                                     │   │   │
│  │     │  Select destination account:                        │   │   │
│  │     │  ┌─────────────────────────────────────────────┐    │   │   │
│  │     │  │ ◉ Checking (AB-2222-3333-44)               │    │   │   │
│  │     │  │ ○ Savings  (AB-5555-6666-77)               │    │   │   │
│  │     │  └─────────────────────────────────────────────┘    │   │   │
│  │     └─────────────────────────────────────────────────────┘   │   │
│  │                                                               │   │
│  │     ⚠️ Cannot transfer to your own accounts                   │   │
│  │                                                               │   │
│  │     [← Back]                                    [Next →]      │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│         │                                                            │
│         ▼                                                            │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │              STEP 3: ENTER AMOUNT                             │   │
│  │                                                               │   │
│  │     From: Your Checking (AB-1234-5678-90)                     │   │
│  │     Available Balance: $12,345.67                             │   │
│  │                                                               │   │
│  │     To: @johnsmith - John S.                                  │   │
│  │         Checking (AB-2222-3333-44)                            │   │
│  │                                                               │   │
│  │     Amount to Transfer:                                       │   │
│  │     ┌─────────────────────────────────────────┐               │   │
│  │     │ €  [           500.00                  ]│               │   │
│  │     └─────────────────────────────────────────┘               │   │
│  │                                                               │   │
│  │     Quick amounts: [€50] [€100] [€500] [€1000]                │   │
│  │                                                               │   │
│  │     Description (optional):                                   │   │
│  │     ┌─────────────────────────────────────────┐               │   │
│  │     │ [Dinner split                          ]│               │   │
│  │     └─────────────────────────────────────────┘               │   │
│  │                                                               │   │
│  │     Your New Balance: €11,845.67  ◄── Live preview            │   │
│  │                                                               │   │
│  │     ⚠️ Insufficient funds (if amount > balance)               │   │
│  │                                                               │   │
│  │     [← Back]                                   [Review →]     │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│         │                                                            │
│         ▼                                                            │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │              STEP 4: CONFIRM TRANSFER                         │   │
│  │                                                               │   │
│  │     ┌─────────────────────────────────────────────────────┐   │   │
│  │     │                 TRANSFER SUMMARY                    │   │   │
│  │     │                                                     │   │   │
│  │     │  From:     Your Checking Account                    │   │   │
│  │     │            AB-1234-5678-90                          │   │   │
│  │     │                                                     │   │   │
│  │     │  To:       @johnsmith (John S.)                     │   │   │
│  │     │            AB-2222-3333-44                          │   │   │
│  │     │                                                     │   │   │
│  │     │  Amount:   €500.00                                  │   │   │
│  │     │                                                     │   │   │
│  │     │  Note:     "Dinner split"                           │   │   │
│  │     │                                                     │   │   │
│  │     │  ─────────────────────────────────────────────────  │   │   │
│  │     │                                                     │   │   │
│  │     │  Your balance after: €11,845.67                     │   │   │
│  │     │                                                     │   │   │
│  │     └─────────────────────────────────────────────────────┘   │   │
│  │                                                               │   │
│  │     ⚠️ Please verify all details before confirming            │   │
│  │                                                               │   │
│  │     [← Back]                          [Confirm Transfer]      │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│         │                                                            │
│         ├────────────────────────────────────────────┬───────────┐   │
│         │ Success                                    │ Errors    │   │
│         ▼                                            ▼           │   │
│  ┌──────────────────────┐                 ┌──────────────────┐   │   │
│  │   SUCCESS STATE      │                 │  POSSIBLE ERRORS │   │   │
│  │                      │                 │                  │   │   │
│  │   [Checkmark Icon]   │                 │  - Recipient not │   │   │
│  │                      │                 │    found         │   │   │
│  │   "Transfer          │                 │                  │   │   │
│  │    Successful!"      │                 │  - Insufficient  │   │   │
│  │                      │                 │    funds         │   │   │
│  │   €500.00 sent to    │                 │                  │   │   │
│  │   @johnsmith         │                 │  - Cannot send   │   │   │
│  │                      │                 │    to self       │   │   │
│  │   Reference: TXN-123 │                 │                  │   │   │
│  │                      │                 │  - Daily limit   │   │   │
│  │   [Done]             │                 │    exceeded      │   │   │
│  │   [New Transfer]     │                 │                  │   │   │
│  └──────────────────────┘                 │  - Server error  │   │   │
│                                           └──────────────────┘   │   │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

**4-Step Flow Summary**:

| Step | Purpose | Actions |
|------|---------|---------|
| 1. Select Source | Choose which account to send from | Select from user's accounts |
| 2. Find Recipient | Locate the recipient securely | Search by @AzureTag or Account# |
| 3. Enter Amount | Specify transfer details | Amount + optional description |
| 4. Confirm | Final review before sending | Verify and confirm |

**Flow Steps (Detailed)**:
1. User clicks "Transfer" button on dashboard
2. **Step 1**: User selects which of their accounts to transfer FROM
3. **Step 2**: User searches for recipient by @AzureTag or Account Number
   - System shows masked name ("John S.") for privacy
   - User selects recipient's destination account
   - System validates: not user's own account
4. **Step 3**: User enters amount and optional description
   - Real-time balance preview
   - Quick amount buttons available
5. **Step 4**: User reviews complete transfer summary
   - All details displayed for verification
   - User must explicitly confirm
6. On success: Show confirmation with transaction reference
7. On error: Show specific error, allow retry

**Validation Rules**:
- Recipient: Must exist, cannot be self
- Amount: > 0, <= available balance, max 2 decimal places
- Description: Optional, max 140 characters
- Rate limit: Max 5 recipient searches per minute

**Recipient Search Behavior**:
```
Search Input          │ API Call                    │ Response
──────────────────────┼─────────────────────────────┼────────────────────
@johnsmith            │ GET /recipients?tag=john... │ { found: true, ... }
AB-2222-3333-44       │ GET /recipients?acct=AB-... │ { found: true, ... }
(invalid format)      │ (no API call)               │ Frontend validation
(not found)           │ GET /recipients?...         │ { found: false }
(own AzureTag)        │ GET /recipients?...         │ { error: "SELF" }
```

**Privacy & Security**:
- Internal user IDs are NEVER exposed to frontend
- Only public identifiers used: AzureTag, Account Number
- Recipient name is masked: "FirstName LastInitial." format
- Confirmation step prevents accidental transfers
- Rate limiting prevents enumeration attacks

---

### 2.7 Transaction History Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                    TRANSACTION HISTORY FLOW                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────┐                                                    │
│  │  Dashboard   │                                                    │
│  │              │                                                    │
│  │  [View All   │◄── Click                                          │
│  │   Trans...]  │                                                    │
│  └──────┬───────┘                                                    │
│         │                                                            │
│         ▼                                                            │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                  TRANSACTION HISTORY PAGE                     │   │
│  │                                                               │   │
│  │  ┌─────────────────────────────────────────────────────────┐ │   │
│  │  │                     FILTERS                              │ │   │
│  │  │                                                          │ │   │
│  │  │  Time Range: [Last 30 Days ▼]                            │ │   │
│  │  │              - Last 7 Days                               │ │   │
│  │  │              - Last 30 Days                              │ │   │
│  │  │              - Last 90 Days                              │ │   │
│  │  │              - All Time                                  │ │   │
│  │  │              - Custom Range...                           │ │   │
│  │  │                                                          │ │   │
│  │  │  Type: [All ▼]                                           │ │   │
│  │  │        - All                                             │ │   │
│  │  │        - Deposits                                        │ │   │
│  │  │        - Withdrawals                                     │ │   │
│  │  │        - Transfers In                                    │ │   │
│  │  │        - Transfers Out                                   │ │   │
│  │  │                                                          │ │   │
│  │  └─────────────────────────────────────────────────────────┘ │   │
│  │                                                               │   │
│  │  ┌─────────────────────────────────────────────────────────┐ │   │
│  │  │                  TRANSACTIONS LIST                       │ │   │
│  │  │                                                          │ │   │
│  │  │  ┌─────────────────────────────────────────────────────┐│ │   │
│  │  │  │ Dec 17, 2025 10:30 AM                               ││ │   │
│  │  │  │ DEPOSIT                                             ││ │   │
│  │  │  │                                          +$500.00   ││ │   │
│  │  │  │ Balance after: $12,345.67                           ││ │   │
│  │  │  └─────────────────────────────────────────────────────┘│ │   │
│  │  │                                                          │ │   │
│  │  │  ┌─────────────────────────────────────────────────────┐│ │   │
│  │  │  │ Dec 16, 2025 2:15 PM                                ││ │   │
│  │  │  │ TRANSFER OUT to @johnsmith                          ││ │   │
│  │  │  │ "Dinner split"                                      ││ │   │
│  │  │  │                                          -€200.00   ││ │   │
│  │  │  │ Balance after: €11,845.67                           ││ │   │
│  │  │  └─────────────────────────────────────────────────────┘│ │   │
│  │  │                                                          │ │   │
│  │  │  ... more transactions ...                               │ │   │
│  │  │                                                          │ │   │
│  │  │  [Load More] or infinite scroll                          │ │   │
│  │  │                                                          │ │   │
│  │  └─────────────────────────────────────────────────────────┘ │   │
│  │                                                               │   │
│  │  ┌─────────────────────────────────────────────────────────┐ │   │
│  │  │              EMPTY STATE (No Transactions)               │ │   │
│  │  │                                                          │ │   │
│  │  │     [Empty Illustration]                                 │ │   │
│  │  │                                                          │ │   │
│  │  │     "No transactions found for this period."             │ │   │
│  │  │                                                          │ │   │
│  │  │     [Make a Deposit]                                     │ │   │
│  │  │                                                          │ │   │
│  │  └─────────────────────────────────────────────────────────┘ │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

**Flow Steps**:
1. Click "View All Transactions" from dashboard
2. Navigate to Transaction History page
3. Default: Last 30 days, all types
4. User can filter by time range and type
5. Transactions load with pagination (or infinite scroll)
6. Each transaction shows: date, type, amount, balance after

**Filter Options**:
- Time: 7 days, 30 days, 90 days, all time, custom
- Type: All, Deposits, Withdrawals, Transfers In, Transfers Out

---

## 3. Error States

### 3.1 Error State Patterns

```
┌─────────────────────────────────────────────────────────────────────┐
│                        ERROR STATE PATTERNS                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                    INLINE FIELD ERROR                         │   │
│  │                                                               │   │
│  │     Email                                                     │   │
│  │     ┌─────────────────────────────────────────┐               │   │
│  │     │ invalid-email                           │  ◄── Red      │   │
│  │     └─────────────────────────────────────────┘     border    │   │
│  │     ⚠️ Please enter a valid email address      ◄── Red text   │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                      TOAST ERROR                              │   │
│  │                                                               │   │
│  │     ┌────────────────────────────────────────────────┐        │   │
│  │     │ ⚠️  Something went wrong                    [X]│        │   │
│  │     │                                                │        │   │
│  │     │  Unable to complete transaction.               │        │   │
│  │     │  Please try again.                             │        │   │
│  │     │                                                │        │   │
│  │     │  [Retry]  [Dismiss]                            │        │   │
│  │     └────────────────────────────────────────────────┘        │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                    FULL PAGE ERROR                            │   │
│  │                                                               │   │
│  │                    [Error Illustration]                       │   │
│  │                                                               │   │
│  │                  "Something went wrong"                       │   │
│  │                                                               │   │
│  │        We couldn't load your account information.             │   │
│  │                                                               │   │
│  │                      [Try Again]                              │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                 NETWORK/OFFLINE ERROR                         │   │
│  │                                                               │   │
│  │     ┌────────────────────────────────────────────────┐        │   │
│  │     │ 📡 No internet connection                      │        │   │
│  │     │                                                │        │   │
│  │     │ Check your connection and try again.           │        │   │
│  │     │                                                │        │   │
│  │     └────────────────────────────────────────────────┘        │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### 3.2 Error Message Guidelines

| Error Type | Display Method | Auto-Dismiss | User Action |
|------------|---------------|--------------|-------------|
| Validation | Inline under field | No | Fix input |
| Auth failure | Form-level message | No | Re-enter credentials |
| API error | Toast notification | 5 seconds | Retry available |
| Network error | Banner | No | Wait for connection |
| 404 | Full page | No | Navigate away |
| 500 | Full page | No | Retry |

---

## 4. Loading States

### 4.1 Loading State Patterns

```
┌─────────────────────────────────────────────────────────────────────┐
│                       LOADING STATE PATTERNS                         │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                    BUTTON LOADING                             │   │
│  │                                                               │   │
│  │     ┌─────────────────────────────────────┐                   │   │
│  │     │  [Spinner] Processing...            │  ◄── Disabled     │   │
│  │     └─────────────────────────────────────┘                   │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                  SKELETON LOADING (Cards)                     │   │
│  │                                                               │   │
│  │     ┌─────────────────────────────────────────┐               │   │
│  │     │ ████████████████████████████            │               │   │
│  │     │                                          │               │   │
│  │     │ ██████████████                           │               │   │
│  │     │ ████████████████████                     │               │   │
│  │     │                                          │               │   │
│  │     │ ██████    ██████    ██████               │               │   │
│  │     └─────────────────────────────────────────┘               │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                 SKELETON LOADING (Table)                      │   │
│  │                                                               │   │
│  │     ┌─────────────────────────────────────────────────────┐   │   │
│  │     │ ████████████   ██████████████   ████████████████    │   │   │
│  │     │ ████████████   ██████████████   ████████████████    │   │   │
│  │     │ ████████████   ██████████████   ████████████████    │   │   │
│  │     │ ████████████   ██████████████   ████████████████    │   │   │
│  │     │ ████████████   ██████████████   ████████████████    │   │   │
│  │     └─────────────────────────────────────────────────────┘   │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │                   FULL PAGE LOADING                           │   │
│  │                                                               │   │
│  │                                                               │   │
│  │                        [Spinner]                              │   │
│  │                                                               │   │
│  │                   Loading your account...                     │   │
│  │                                                               │   │
│  │                                                               │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### 4.2 Loading State Usage

| Context | Loading Type | Duration Threshold |
|---------|-------------|-------------------|
| Form submission | Button spinner | Immediate |
| Page load | Skeleton | > 200ms |
| Data refresh | Skeleton overlay | > 500ms |
| Initial app load | Full page | Immediate |
| Infinite scroll | Inline spinner | Immediate |

---

## 5. Navigation Structure

```
┌─────────────────────────────────────────────────────────────────────┐
│                      NAVIGATION STRUCTURE                            │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  PUBLIC ROUTES (Unauthenticated)                                     │
│  ├── /login                    Login page                            │
│  └── /register                 Registration page                     │
│                                                                      │
│  PROTECTED ROUTES (Authenticated)                                    │
│  ├── /dashboard                Main dashboard (default)              │
│  ├── /transactions             Transaction history                   │
│  └── /account                  Account settings (future)             │
│                                                                      │
│  REDIRECTS                                                           │
│  ├── / → /login (if not auth) or /dashboard (if auth)               │
│  ├── /login → /dashboard (if already auth)                          │
│  └── Any protected → /login (if not auth)                           │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 6. Accessibility Considerations

### 6.1 Keyboard Navigation

| Action | Keyboard Shortcut |
|--------|------------------|
| Submit form | Enter |
| Close dialog | Escape |
| Navigate form fields | Tab / Shift+Tab |
| Select dropdown option | Arrow keys + Enter |
| Toggle password visibility | Space on icon |

### 6.2 Screen Reader Announcements

| Event | Announcement |
|-------|-------------|
| Form validation error | "Error: [field name] - [error message]" |
| Transaction success | "Success: [amount] has been [deposited/withdrawn/transferred]" |
| Loading state | "Loading..." |
| Page navigation | "[Page name] page loaded" |

### 6.3 Focus Management

- After dialog opens: Focus first interactive element
- After dialog closes: Return focus to trigger element
- After form error: Focus first field with error
- After page navigation: Focus main content heading

---

**Document Status**: COMPLETE - Updated with External Transfers (AzureTag system) - Ready for Review
