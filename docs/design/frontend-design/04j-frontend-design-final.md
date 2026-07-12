# Final Frontend Design Document
## Bank Account Management System

**Document Version**: 5.0
**Created**: 2025-12-16
**Updated**: 2026-01-09
**Authors**: Claude Team (UX/UI Expert, Web Designer, Frontend Lead)
**Status**: FINAL - BFF Pattern Aligned + Enhanced UX

**Bank Name**: AzureBank

---

## 1. Executive Summary

This document represents the **final consolidated frontend design** for the BankApp - a Bank Account Management System being developed as a technical assessment for Dev4Side. The design has been developed through a rigorous multi-phase process involving:

1. **UX/UI Expert**: User flows, wireframes, and interaction design
2. **Web Designer**: Visual specifications, design tokens, and component styling
3. **Frontend Lead**: Component architecture, state management, and technical specifications
4. **Internal Confrontation**: Cross-review between team roles to ensure consistency and identify gaps

### Key Design Decisions
- **Bank Identity**: AzureBank with custom brand colors (#006DE2 primary blue)
- **UI Framework**: FluentUI v9 with custom banking-appropriate theming
- **Architecture**: Feature-based folder structure with RTK Query for API state management
- **Responsiveness**: Mobile-first approach with breakpoints at 480px and 768px
- **Accessibility**: WCAG 2.1 AA compliant with verified color contrast and keyboard navigation
- **Account Identification**: AzureTag (@username) + Account Number (AB-XXXX-XXXX-XX) system

### Scope
The application enables users to:
- Register with unique AzureTag (@username) and authenticate (BFF session-based)
- Manage multiple bank accounts (Savings, Checking, Investment)
- Perform transactions (Deposit, Withdraw)
- **Send money to other AzureBank users** via External Transfers (CORE FEATURE)
- View transaction history with filtering and pagination

> **Security Note**: AzureBank uses the BFF (Backend-for-Frontend) pattern for maximum XSS protection. JWT tokens are stored server-side only; the browser uses HTTP-only session cookies. See `08-security-design.md` for details.

### Documents Included
| Document | Purpose |
|----------|---------|
| 04a-ux-user-flows.md | All user flows and validation rules |
| 04b-ux-wireframes.md | Desktop and mobile wireframes |
| 04c-design-visual-specs.md | Component visual specifications (v4.0) |
| 04d-design-tokens.md | FluentUI theme and tokens |
| 04e-frontend-components.md | Component architecture and code (v4.0) |
| 04h-internal-confrontation.md | Internal team review |
| 04g-gemini-review-prompt.md | External review prompt |
| **04i-cross-ai-confrontation.md** | **Claude vs Gemini confrontation (COMPLETE)** |
| **04l-external-transfers-design.md** | **External transfers system design (v2.0 - Privacy Fixed)** |
| **04m-industry-standards-analysis.md** | **Industry standards research & enhancement plan** |

### Post-Gemini Review Changes (v4.1)

Based on Gemini's external review, the following changes were accepted:

| Change | Impact | Status |
|--------|--------|--------|
| **Remove recipient account selection** | Sender no longer sees recipient's account list | APPLIED |
| **Add primary account routing** | Backend routes to recipient's primary account | APPLIED |
| **Add wizard state cleanup** | Reset transfer wizard on close/unmount | APPLIED |
| **Add copy-to-clipboard** | Copy buttons for AzureTag/Account numbers | APPLIED |
| **Add global error middleware** | Centralized 401/500 handling | APPLIED |
| **Add RTK Query retry logic** | Retry GET requests 3x with backoff | APPLIED |

See `04i-cross-ai-confrontation.md` for full analysis.

---

## 2. Final UX Specifications

### 2.1 User Journey Map

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        USER JOURNEY: NEW USER                               │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  1. ONBOARD          2. SETUP            3. USE              4. GROW       │
│  ┌─────────┐        ┌─────────┐        ┌─────────┐        ┌─────────┐     │
│  │Register │───────▸│ Create  │───────▸│ Deposit │───────▸│ Create  │     │
│  │  Form   │        │ Account │        │  Funds  │        │ More    │     │
│  └─────────┘        └─────────┘        └─────────┘        │Accounts │     │
│       │                  │                  │              └─────────┘     │
│       │                  │                  │                   │          │
│       ▼                  ▼                  ▼                   ▼          │
│  Auto-login         First account     View balance        Transfer funds   │
│  → Dashboard        → Dashboard       → Transactions      → Between accts  │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 2.2 Complete User Flows

#### Authentication Flows (BFF Pattern)

> **BFF Pattern**: Browser uses session cookies (HTTP-only). No JWT storage in browser.

| Flow | Entry | Steps | Exit |
|------|-------|-------|------|
| Register | /register | Enter FirstName + Surname + **@AzureTag** + Email + Password + Confirm → Submit | Session established → /dashboard |
| Login | /login | Enter Email + Password → Submit | Session established → /dashboard |
| Logout | Header menu | Click "Sign Out" | Session ended → /login |
| Session Timeout | Any protected page | Warning at 5 min → Extend or Logout | Extended: stay, Expired: /login |

**BFF Session Flow**:
1. User submits credentials to `/bff/auth/login`
2. BFF authenticates with backend, receives JWT
3. BFF stores JWT in server-side session
4. BFF sets HTTP-only session cookie in browser
5. Frontend stores user info in Redux (NO token)
6. All subsequent requests include session cookie automatically (`credentials: 'include'`)

**Note**: AzureTag is the user's public identifier (@username format) for receiving transfers. It is IMMUTABLE after registration.

#### Account Flows
| Flow | Entry | Steps | Exit |
|------|-------|-------|------|
| Create Account | Dashboard/Accounts | Open dialog → Enter Name + Type + Initial Deposit → Submit | New account in list |
| View Account | Account list | Click account card | /accounts/:id with transactions |
| Delete Account | Account detail | Confirm dialog → Delete | Removed from list |

#### Transaction Flows
| Flow | Entry | Steps | Exit |
|------|-------|-------|------|
| Deposit | Dashboard quick action | Select account → Enter amount + description → Submit | Balance updated + Toast |
| Withdraw | Dashboard quick action | Select account → Enter amount (≤ balance) + description → Submit | Balance updated + Toast |

#### External Transfer Flow (4-Step Wizard) - CORE FEATURE
| Step | Purpose | Actions |
|------|---------|---------|
| 1. Select Source | Choose sender account | Radio select from user's accounts with balance display |
| 2. Find Recipient | Locate transfer recipient | Search by @AzureTag or Account# → Shows masked name + accounts |
| 3. Enter Amount | Specify transfer details | Enter € amount + optional description (140 chars max) |
| 4. Confirm | Verify before sending | Review summary → Confirm Transfer → Success with reference |

**Security**: Internal database IDs are NEVER exposed. Only public identifiers (AzureTag, Account Number) are used.

### 2.3 Validation Rules Summary

| Field | Rules |
|-------|-------|
| First Name | Required, 2-50 chars, letters/spaces/hyphens/apostrophes only |
| Surname | Required, 2-50 chars, letters/spaces/hyphens/apostrophes only |
| **AzureTag** | Required, 3-20 chars, starts with letter, alphanumeric + underscore, unique, IMMUTABLE |
| Email | Required, valid format, must be unique (API check) |
| Password | Required, 8+ chars, 1 uppercase, 1 lowercase, 1 number |
| Confirm Password | Must match password |
| Account Name | Required, 1-100 chars |
| Amount | Required, > 0.01, max 2 decimal places |
| Withdraw Amount | Must not exceed account balance |
| External Transfer | Recipient must exist, cannot be self, amount ≤ balance, rate limit: 5 searches/min |

### 2.4 Error Handling Strategy

| Error Type | Display Method | User Action |
|------------|---------------|-------------|
| Validation errors | Inline below field (red text) | Fix and retry |
| API errors (4xx) | Toast notification (error style) | Retry or contact support |
| Network errors | Toast with "Retry" button | Click retry |
| Session expired | Full-page redirect with message | Re-login |
| 404 Not Found | Not Found page with home link | Navigate home |

### 2.5 Empty States

| Context | Message | CTA |
|---------|---------|-----|
| No accounts (new user) | "Welcome! Create your first account to get started." | "Create Account" button |
| No transactions (new account) | "No transactions yet. Make your first deposit!" | "Deposit" button |
| Filter returns empty | "No transactions match your filters." | "Clear Filters" button |

---

## 3. Final Visual Specifications

### 3.1 Brand Colors

```
Brand Blue Ramp (Primary)
────────────────────────────────────────────────────────────
10   #001D3D  ████  Darkest - Not commonly used
20   #002D5E  ████
30   #003D7F  ████  Pressed state
40   #004DA0  ████  Hover state / Gradient end
50   #005DC1  ████
60   #006DE2  ████  PRIMARY - Brand color
70   #1A7FE8  ████
80   #4D99ED  ████  Links hover
90   #80B3F2  ████
100  #B3CDF7  ████  Light accents
110  #CCE0FA  ████
120  #E6F0FC  ████  Very light backgrounds
130  #F0F6FE  ████  Hover backgrounds
140  #F5FAFF  ████
150  #FAFCFF  ████
160  #FFFFFF  ████  Lightest
────────────────────────────────────────────────────────────
```

### 3.2 Semantic Colors

| Purpose | Background | Foreground | Icon/Border |
|---------|-----------|------------|-------------|
| Deposit/Success | #E6F4EA | #137333 | #34A853 |
| Withdrawal/Error | #FCE8E6 | #C5221F | #EA4335 |
| Transfer Out/Warning | #FEF3E2 | #B45309 | #F59E0B |
| Transfer In/Info | #E0F2FE | #0369A1 | #0EA5E9 |

### 3.3 Typography Scale

| Token | Size | Weight | Usage |
|-------|------|--------|-------|
| Hero Balance | 48px | 700 | Account balance display |
| Page Title | 24px | 600 | Page headers |
| Card Title | 20px | 600 | Section/card headers |
| Body | 14px | 400 | Default text |
| Caption | 12px | 400 | Helper text, timestamps |
| Monospace | 14-16px | 400-600 | Amounts, account numbers |

### 3.4 Spacing System

| Token | Value | Usage |
|-------|-------|-------|
| Page Padding (Desktop) | 32px | Main content area |
| Page Padding (Mobile) | 16px | Main content area |
| Card Padding | 24px | Inside card components |
| Card Gap | 24px | Between cards |
| Form Field Gap | 16px | Between form fields |
| Button Gap | 12px | Between action buttons |
| Inline Gap | 8px | Icon to text, small elements |

### 3.5 Component Specifications

#### Buttons
| Variant | Background | Text | Border | Usage |
|---------|-----------|------|--------|-------|
| Primary | #006DE2 | White | None | Main actions |
| Secondary | Transparent | #006DE2 | 1px #006DE2 | Cancel, secondary actions |
| Danger | #C5221F | White | None | Delete, destructive |
| Subtle | Transparent | #1F2937 | None | Tertiary actions |

#### Cards
| Property | Value |
|----------|-------|
| Background | #FFFFFF |
| Border | 1px solid #E5E7EB |
| Border Radius | 8px |
| Shadow (rest) | shadow4 |
| Shadow (hover) | shadow8 |

#### Balance Card (Hero)
| Property | Value |
|----------|-------|
| Background | Linear gradient 135deg (#006DE2 → #004DA0) |
| Border Radius | 12px |
| Shadow | shadow8 |
| Text Color | White |
| Action Button BG | rgba(255,255,255,0.2) |

---

## 4. Final Technical Specifications

### 4.1 Project Structure

```
src/
├── app/
│   ├── store.ts              # Redux store with RTK Query
│   └── hooks.ts              # Typed useAppDispatch, useAppSelector
│
├── components/
│   ├── common/               # Shared components
│   │   ├── BalanceCard/
│   │   ├── TransactionCard/
│   │   ├── CurrencyInput/
│   │   ├── PasswordInput/
│   │   ├── FilterBar/
│   │   ├── EmptyState/
│   │   ├── LoadingSpinner/
│   │   └── ErrorBoundary/
│   ├── layout/               # App shell
│   │   ├── AppLayout/
│   │   ├── Header/
│   │   └── MobileNav/
│   ├── auth/                 # Auth components
│   │   ├── LoginForm/
│   │   ├── RegisterForm/
│   │   └── ProtectedRoute/
│   ├── accounts/             # Account components
│   │   ├── AccountList/
│   │   ├── AccountCard/
│   │   └── CreateAccountDialog/
│   ├── transactions/         # Transaction components
│   │   ├── TransactionList/
│   │   ├── TransactionTable/
│   │   ├── DepositDialog/
│   │   ├── WithdrawDialog/
│   │   └── TransferDialog/
│   └── dashboard/            # Dashboard-specific
│       ├── QuickActions/
│       └── RecentTransactions/
│
├── features/                 # Redux slices + RTK Query
│   ├── api/
│   │   └── baseApi.ts        # Base RTK Query config
│   ├── auth/
│   │   ├── authSlice.ts      # Auth state
│   │   └── authApi.ts        # Auth endpoints
│   ├── accounts/
│   │   └── accountsApi.ts    # Account endpoints
│   └── transactions/
│       └── transactionsApi.ts # Transaction endpoints
│
├── hooks/                    # Custom hooks
│   ├── useAuth.ts
│   ├── useAccounts.ts
│   ├── useTransactions.ts
│   ├── useMediaQuery.ts
│   ├── useCurrency.ts
│   └── useDebounce.ts
│
├── pages/                    # Route components
│   ├── LoginPage.tsx
│   ├── RegisterPage.tsx
│   ├── DashboardPage.tsx
│   ├── AccountsPage.tsx
│   ├── AccountDetailPage.tsx
│   └── NotFoundPage.tsx
│
├── theme/                    # FluentUI theme
│   ├── index.ts
│   ├── brandColors.ts
│   ├── lightTheme.ts
│   └── semanticColors.ts
│
├── types/                    # TypeScript types
│   ├── auth.types.ts
│   ├── account.types.ts
│   ├── transaction.types.ts
│   └── api.types.ts
│
├── utils/                    # Utilities
│   ├── formatCurrency.ts
│   ├── formatDate.ts
│   └── validation.ts
│
├── App.tsx
└── main.tsx
```

### 4.2 Core TypeScript Interfaces

```typescript
// User & Auth
interface User {
  id: string;
  email: string;
  firstName: string;
  surname: string;
  createdAt: string;
}

interface LoginRequest {
  email: string;
  password: string;
}

interface RegisterRequest {
  firstName: string;
  surname: string;
  email: string;
  password: string;
  confirmPassword: string;
}

// Account
type AccountType = 'Savings' | 'Checking' | 'Investment';

interface Account {
  id: string;
  userId: string;
  name: string;
  accountNumber: string;
  accountType: AccountType;
  balance: number;
  currency: string;
  isActive: boolean;
  createdAt: string;
}

interface CreateAccountRequest {
  name: string;
  accountType: AccountType;
  initialDeposit?: number;
}

// Transaction
type TransactionType = 'Deposit' | 'Withdrawal' | 'TransferIn' | 'TransferOut';

interface Transaction {
  id: string;
  accountId: string;
  type: TransactionType;
  amount: number;
  balance: number;
  description: string;
  referenceNumber: string;
  relatedAccountId?: string;
  createdAt: string;
}

interface DepositRequest {
  accountId: string;
  amount: number;
  description: string;
}

interface WithdrawRequest {
  accountId: string;
  amount: number;
  description: string;
}

interface TransferRequest {
  fromAccountId: string;
  toAccountId: string;
  amount: number;
  description: string;
}
```

### 4.3 RTK Query API Configuration (BFF Pattern)

> **BFF Pattern**: No Authorization header from browser. Session cookie sent automatically.

```typescript
// BFF Pattern - Session cookie handles authentication
const baseQuery = fetchBaseQuery({
  baseUrl: '/api',
  credentials: 'include',  // CRITICAL: Send HTTP-only session cookie
  // NO Authorization header - BFF injects JWT internally
});

// Tag types for cache invalidation
tagTypes: ['Account', 'Transaction', 'User', 'Session']

// Key endpoints (via BFF)
- authApi: login, register, getSession, logout, verifyPin
- accountsApi: getAccounts, getAccountById, createAccount, deleteAccount
- transactionsApi: getTransactions, deposit, withdraw, transfer
```

### 4.4 Route Configuration

| Path | Component | Auth Required | Notes |
|------|-----------|---------------|-------|
| /login | LoginPage | No | Redirect to /dashboard if authenticated |
| /register | RegisterPage | No | Redirect to /dashboard if authenticated |
| /dashboard | DashboardPage | Yes | Default protected route |
| /accounts | AccountsPage | Yes | All accounts list |
| /accounts/:id | AccountDetailPage | Yes | Single account + transactions |
| /* | NotFoundPage | No | 404 fallback |

---

## 5. Responsive Design Specifications

### 5.1 Breakpoints

| Breakpoint | Width | Layout Changes |
|------------|-------|----------------|
| Mobile | 0 - 479px | Hamburger nav, stacked layouts, full-width dialogs |
| Tablet | 480 - 767px | Hamburger nav, 2-column where appropriate |
| Desktop | 768px+ | Inline nav, tables, centered dialogs |

### 5.2 Component Adaptations

| Component | Mobile | Desktop |
|-----------|--------|---------|
| Navigation | Hamburger → Drawer | Inline horizontal |
| Balance Card | Full width, swipeable | Max 800px, centered |
| Transaction List | Cards | Table with columns |
| Dialogs | Full screen, slide up | 480px modal, centered |
| Forms | Single column | Two column where logical |
| Pagination | "Load More" button | Full page numbers |
| Filters | Bottom sheet | Inline dropdowns |

### 5.3 Touch Targets

| Element | Minimum Size | Notes |
|---------|-------------|-------|
| Buttons | 44px height | WCAG 2.5.5 compliance |
| Icon buttons | 44x44px | Even if icon is smaller |
| List items | 48px height | For comfortable tapping |
| Form inputs | 44px height | Matches button height |

---

## 6. Accessibility Checklist

### 6.1 Color & Contrast
- [x] All text meets WCAG AA (4.5:1 for normal, 3:1 for large)
- [x] Interactive elements meet 3:1 against background
- [x] Color is not the only indicator (icons + labels)
- [x] Focus indicators are visible (2px solid black)

### 6.2 Keyboard Navigation
- [x] All interactive elements are focusable
- [x] Tab order follows logical reading order
- [x] Focus is trapped in modal dialogs
- [x] Escape closes dialogs and menus
- [x] Enter/Space activates buttons

### 6.3 Screen Readers
- [x] All images have alt text (or role="presentation")
- [x] Form inputs have associated labels
- [x] Error messages use aria-describedby
- [x] Loading states use aria-live
- [x] Dialogs use role="dialog" and aria-modal
- [x] Page regions have landmark roles

### 6.4 Motion & Timing
- [x] Respects prefers-reduced-motion
- [x] Toast auto-dismiss can be disabled
- [x] Session timeout has warning + extend option

---

## 7. Implementation Priority

### Phase 1: Core Infrastructure (Week 1)
| Priority | Task | Complexity |
|----------|------|------------|
| 1 | Project setup (Vite, FluentUI, Redux) | Low |
| 2 | Theme configuration | Low |
| 3 | Base API setup (RTK Query) | Medium |
| 4 | App layout + routing | Medium |
| 5 | Protected route component | Low |

### Phase 2: Authentication (Week 1-2)
| Priority | Task | Complexity |
|----------|------|------------|
| 6 | Login page + form | Medium |
| 7 | Register page + form | Medium |
| 8 | Auth state management | Medium |
| 9 | JWT handling + persistence | Low |

### Phase 3: Dashboard & Accounts (Week 2)
| Priority | Task | Complexity |
|----------|------|------------|
| 10 | Dashboard page | Medium |
| 11 | Balance card component | Medium |
| 12 | Account list component | Low |
| 13 | Create account dialog | Medium |
| 14 | Account detail page | Medium |

### Phase 4: Transactions (Week 3)
| Priority | Task | Complexity |
|----------|------|------------|
| 15 | Transaction card component | Low |
| 16 | Transaction table | Medium |
| 17 | Deposit dialog | Medium |
| 18 | Withdraw dialog | Medium |
| 19 | Transfer dialog | Medium |
| 20 | Filter bar | Medium |

### Phase 5: Polish (Week 3-4)
| Priority | Task | Complexity |
|----------|------|------------|
| 21 | Loading states + skeletons | Low |
| 22 | Error handling | Medium |
| 23 | Empty states | Low |
| 24 | Mobile nav drawer | Medium |
| 25 | Toast notifications | Low |
| 26 | Testing | High |

---

## 8. Testing Strategy

### 8.1 Unit Tests
- Utility functions (formatCurrency, formatDate, validation)
- Redux slices (authSlice reducers)
- Custom hooks (mocked API calls)

### 8.2 Component Tests
- Form validation (LoginForm, RegisterForm)
- Dialog behavior (open/close, form submission)
- Conditional rendering (loading, error, empty states)

### 8.3 Integration Tests
- Auth flow (register → login → dashboard)
- Account creation flow
- Transaction flows (deposit, withdraw, transfer)

### 8.4 Accessibility Tests
- Automated: axe-core integration
- Manual: Keyboard-only navigation
- Screen reader: VoiceOver/NVDA testing

---

## 9. Known Limitations & Future Work

### 9.1 Deferred to Phase 2
| Feature | Reason | Priority |
|---------|--------|----------|
| Password Reset | Out of MVP scope | High |
| Dark Mode | FluentUI supports, easy to add | Medium |
| Multi-currency | EUR only for MVP | Low |
| Account Sharing | Complex auth model | Low |
| Transaction Categories | Nice to have | Low |
| Export to CSV | Nice to have | Low |

### 9.2 Technical Debt
| Item | Notes |
|------|-------|
| E2E Tests | Add Playwright tests |
| Performance Profiling | React DevTools audit |
| Bundle Analysis | Check for optimization |
| Error Logging | Add Sentry or similar |

---

## 10. Document Cross-References

| Reference | Document | Description |
|-----------|----------|-------------|
| User Flows | 04a-ux-user-flows.md | Complete flow diagrams |
| Wireframes | 04b-ux-wireframes.md | ASCII wireframes |
| Visual Specs | 04c-design-visual-specs.md | Component styles |
| Design Tokens | 04d-design-tokens.md | FluentUI theme config |
| Architecture | 04e-frontend-components.md | Code architecture |
| Internal Review | 04h-internal-confrontation.md | Team review |
| Gemini Prompt | 04g-gemini-review-prompt.md | External review |
| **External Transfers** | **04l-external-transfers-design.md** | **AzureTag system & transfer flows** |

---

## 11. Sign-Off

| Role | Name | Status | Date |
|------|------|--------|------|
| UX/UI Expert | Claude (Virtual) | APPROVED | 2025-12-17 |
| Web Designer | Claude (Virtual) | APPROVED | 2025-12-17 |
| Frontend Lead | Claude (Virtual) | APPROVED | 2025-12-17 |
| External Review | Gemini | PENDING | - |
| Human Approver | [User] | PENDING | - |

---

## 12. Change Log

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2025-12-16 | Initial document structure |
| 2.0 | 2025-12-17 | Complete Phase 2 content |
| 3.0 | 2025-12-17 | Added External Transfers feature (AzureTag system, 4-step wizard) |
| 4.0 | 2025-12-17 | Industry standards alignment (microinteractions, skeletons, animations) |

---

**Document Status**: ENHANCED - Industry Standards Aligned - Ready for External Review

**Major Features**:
1. ✅ User authentication (register/login/logout)
2. ✅ Account management (create/view/delete)
3. ✅ Deposit and Withdraw transactions
4. ✅ **External Transfers to other AzureBank users** (CORE FEATURE)
5. ✅ Transaction history with filters

**Industry Standard Enhancements (v4.0)**:
6. ✅ Skeleton loading states (BalanceCardSkeleton, TransactionListSkeleton)
7. ✅ Microinteraction feedback (button press, hover effects)
8. ✅ Animated balance display (count-up animation)
9. ✅ Visual progress stepper for wizard flows
10. ✅ Success celebration animations (3 levels: subtle/moderate/celebration)
11. ✅ Transaction date grouping (Today, Yesterday, This Week)

**Next Steps**:
1. Send 04g-gemini-review-prompt.md content to Google Gemini
2. Gemini reviews and creates files in project-docs-gemini/
3. Document Gemini's feedback in 04i-cross-ai-confrontation.md
4. Resolve any confrontations
5. Update this document with merged recommendations
6. Obtain human approval
7. Proceed to Phase 3: Implementation
