# Frontend Components Architecture
## Bank Account Management System

**Document Version**: 5.0
**Created**: 2025-12-16
**Updated**: 2026-01-09
**Author**: Frontend Lead (Virtual Team Member)
**Status**: FINAL - BFF Pattern Aligned + Enhanced Components

**Bank Name**: AzureBank

---

## 1. Overview

This document defines the complete frontend component architecture, including component hierarchy, state management, API integration, custom hooks, and TypeScript interfaces for the BankApp application.

### Architecture Principles
1. **Component-Based**: Small, reusable, single-responsibility components
2. **Type-Safe**: Full TypeScript coverage with strict mode
3. **State Colocation**: Keep state as close to where it's used as possible
4. **Performance-First**: Memoization, lazy loading, optimistic updates
5. **Accessibility-Built-In**: ARIA attributes, keyboard navigation, screen reader support

---

## 2. Project Structure

```
src/
├── app/
│   ├── store.ts                 # Redux store configuration
│   └── hooks.ts                 # Typed Redux hooks
│
├── components/
│   ├── common/                  # Shared/reusable components
│   │   ├── BalanceCard/
│   │   │   ├── BalanceCard.tsx
│   │   │   ├── BalanceCard.styles.ts
│   │   │   └── index.ts
│   │   ├── TransactionCard/
│   │   ├── CurrencyInput/
│   │   ├── PasswordInput/
│   │   ├── FilterBar/
│   │   ├── EmptyState/
│   │   ├── LoadingSpinner/
│   │   ├── ErrorBoundary/
│   │   ├── Skeleton/               # NEW: Skeleton loading components
│   │   │   ├── BalanceCardSkeleton.tsx
│   │   │   ├── TransactionListSkeleton.tsx
│   │   │   ├── AccountCardSkeleton.tsx
│   │   │   └── index.ts
│   │   ├── LoadingButton/          # NEW: Button with loading state
│   │   ├── AnimatedNumber/         # NEW: Animated balance display
│   │   └── ProgressStepper/        # NEW: Wizard step indicator
│   │
│   ├── layout/
│   │   ├── AppLayout/
│   │   ├── Header/
│   │   ├── MobileNav/
│   │   └── Footer/
│   │
│   ├── auth/                    # Authentication components
│   │   ├── LoginForm/
│   │   ├── RegisterForm/
│   │   └── ProtectedRoute/
│   │
│   ├── accounts/                # Account-related components
│   │   ├── AccountList/
│   │   ├── AccountCard/
│   │   ├── CreateAccountDialog/
│   │   └── AccountDetails/
│   │
│   ├── transactions/            # Transaction components
│   │   ├── TransactionList/
│   │   ├── TransactionTable/
│   │   ├── DepositDialog/
│   │   ├── WithdrawDialog/
│   │   └── TransferWizard/      # 4-step external transfer wizard
│   │       ├── TransferWizard.tsx
│   │       ├── TransferSourceStep.tsx
│   │       ├── TransferRecipientStep.tsx
│   │       ├── TransferAmountStep.tsx
│   │       ├── TransferConfirmStep.tsx
│   │       ├── TransferSuccessStep.tsx
│   │       ├── RecipientSearch.tsx
│   │       ├── transferWizardSlice.ts
│   │       └── index.ts
│   │
│   ├── feedback/               # NEW: User feedback components
│   │   ├── SuccessAnimation/       # Success celebration animations
│   │   │   ├── SuccessAnimation.tsx
│   │   │   ├── Confetti.tsx
│   │   │   └── index.ts
│   │   └── index.ts
│   │
│   └── dashboard/               # Dashboard-specific components
│       ├── DashboardSummary/
│       ├── QuickActions/
│       ├── RecentTransactions/
│       └── BalanceTrend/           # NEW: Sparkline balance chart (optional)
│
├── features/                    # Redux Toolkit slices + RTK Query
│   ├── auth/
│   │   ├── authSlice.ts
│   │   └── authApi.ts
│   ├── accounts/
│   │   ├── accountsSlice.ts
│   │   └── accountsApi.ts
│   ├── transactions/
│   │   ├── transactionsSlice.ts
│   │   └── transactionsApi.ts
│   ├── recipients/              # External transfer recipient search
│   │   └── recipientsApi.ts
│   └── transferWizard/          # Transfer wizard state
│       └── transferWizardSlice.ts
│
├── hooks/                       # Custom React hooks
│   ├── useAuth.ts
│   ├── useAccounts.ts
│   ├── useTransactions.ts
│   ├── useRecipientSearch.ts    # Search recipients by AzureTag/Account#
│   ├── useTransferWizard.ts     # Transfer wizard state management
│   ├── useMediaQuery.ts
│   ├── useCurrency.ts
│   └── useDebounce.ts
│
├── pages/                       # Route-level components
│   ├── LoginPage.tsx
│   ├── RegisterPage.tsx
│   ├── DashboardPage.tsx
│   ├── AccountsPage.tsx
│   ├── AccountDetailPage.tsx
│   ├── TransactionsPage.tsx
│   └── NotFoundPage.tsx
│
├── theme/                       # FluentUI theme configuration
│   ├── index.ts
│   ├── brandColors.ts
│   ├── lightTheme.ts
│   ├── semanticColors.ts
│   ├── typography.ts
│   └── layout.ts
│
├── types/                       # TypeScript type definitions
│   ├── auth.types.ts
│   ├── account.types.ts
│   ├── transaction.types.ts
│   └── api.types.ts
│
├── utils/                       # Utility functions
│   ├── formatCurrency.ts
│   ├── formatDate.ts
│   ├── validation.ts
│   └── constants.ts
│
├── App.tsx
├── main.tsx
└── vite-env.d.ts
```

---

## 3. Component Hierarchy

### 3.1 Application Tree

```
<React.StrictMode>
└── <Provider store={store}>                    # Redux Provider
    └── <FluentProvider theme={bankAppTheme}>   # FluentUI Theme
        └── <BrowserRouter>
            └── <App>
                ├── <Toaster />                  # Global toast notifications
                └── <Routes>
                    │
                    │── /login
                    │   └── <LoginPage>
                    │       └── <LoginForm />
                    │
                    │── /register
                    │   └── <RegisterPage>
                    │       └── <RegisterForm />
                    │
                    │── /* (Protected Routes)
                    │   └── <ProtectedRoute>
                    │       └── <AppLayout>
                    │           ├── <Header />
                    │           ├── <MobileNav />     # Conditional
                    │           │
                    │           │── /dashboard
                    │           │   └── <DashboardPage>
                    │           │       ├── <BalanceCard />
                    │           │       ├── <QuickActions />
                    │           │       ├── <RecentTransactions />
                    │           │       └── <AccountList />
                    │           │
                    │           │── /accounts
                    │           │   └── <AccountsPage>
                    │           │       ├── <AccountList />
                    │           │       └── <CreateAccountDialog />
                    │           │
                    │           │── /accounts/:id
                    │           │   └── <AccountDetailPage>
                    │           │       ├── <BalanceCard />
                    │           │       ├── <FilterBar />
                    │           │       ├── <TransactionTable />    # Desktop
                    │           │       ├── <TransactionList />     # Mobile
                    │           │       └── <Pagination />
                    │           │
                    │           │── /transactions
                    │           │   └── <TransactionsPage>
                    │           │       ├── <FilterBar />
                    │           │       ├── <TransactionTable />
                    │           │       └── <Pagination />
                    │           │
                    │           └── <Footer />
                    │
                    └── /*
                        └── <NotFoundPage />
```

### 3.2 Component Breakdown by Feature

#### Authentication Components
```
LoginPage
├── Logo
├── Title "Welcome Back"
├── LoginForm
│   ├── Input (Email)
│   ├── PasswordInput
│   ├── Button (Submit)
│   └── Link (Register)
└── ErrorMessage (conditional)

RegisterPage
├── Logo (AzureBank)
├── Title "Create Your AzureBank Account"
├── RegisterForm
│   ├── Input (First Name)        # Side by side on desktop
│   ├── Input (Surname)           # Stacked on mobile
│   ├── Input (AzureTag)          # @username for transfers (IMMUTABLE)
│   │   └── Helper text "This is how others find you for transfers"
│   ├── Input (Email)
│   ├── PasswordInput
│   ├── PasswordInput (Confirm)
│   ├── Button (Submit)
│   └── Link (Login)
└── ErrorMessage (conditional)
```

#### Dashboard Components
```
DashboardPage
├── PageHeader "Welcome, {firstName}" with @{azureTag}
├── BalanceCard (Primary Account)
│   ├── AccountIcon
│   ├── AccountName
│   ├── AccountNumber (AB-XXXX-XXXX-XX format)
│   ├── BalanceLabel
│   ├── BalanceAmount (€)
│   └── ActionButtons (Deposit, Withdraw, Transfer)
├── QuickActions
│   ├── QuickActionButton (Deposit)
│   ├── QuickActionButton (Withdraw)
│   ├── QuickActionButton (Transfer)  ◄── Opens TransferWizard
│   └── QuickActionButton (New Account)
├── TwoColumnGrid (Desktop) / SingleColumn (Mobile)
│   ├── RecentTransactions
│   │   ├── SectionHeader
│   │   ├── TransactionCard (repeat)
│   │   │   └── Shows @azureTag for transfers
│   │   └── ViewAllLink
│   └── AccountList
│       ├── SectionHeader
│       ├── AccountCard (repeat)
│       └── CreateAccountButton
└── Dialogs (Modal - triggered by actions)
    ├── DepositDialog
    ├── WithdrawDialog
    ├── TransferWizard (4-step)        ◄── UPDATED: Multi-step wizard
    └── CreateAccountDialog
```

#### Account Detail Components
```
AccountDetailPage
├── BackButton
├── BalanceCard
│   └── (same structure as Dashboard)
├── FilterBar
│   ├── SearchInput
│   ├── TypeDropdown
│   ├── DateRangePicker
│   └── ResetButton
├── TransactionTable (Desktop ≥768px)
│   ├── TableHeader
│   └── TableRow (repeat)
│       ├── DateCell
│       ├── DescriptionCell
│       ├── TypeBadge
│       └── AmountCell
├── TransactionList (Mobile <768px)
│   ├── DateGroupHeader
│   └── TransactionCard (repeat)
├── Pagination / LoadMore
└── EmptyState (conditional)
```

#### External Transfer Wizard Components (NEW)
```
TransferWizard (4-step dialog/full-screen on mobile)
├── WizardHeader
│   ├── Title "Transfer Money"
│   ├── Step Indicator "Step X of 4"
│   ├── Progress Bar
│   └── Close Button
│
├── Step 1: TransferSourceStep
│   ├── Title "Select account to send from"
│   ├── AccountRadioList
│   │   ├── RadioOption (Checking)
│   │   │   ├── AccountType
│   │   │   ├── AccountNumber (AB-XXXX-XXXX-XX)
│   │   │   └── Balance
│   │   └── RadioOption (Savings)
│   └── Navigation (Cancel, Next)
│
├── Step 2: TransferRecipientStep
│   ├── Title "Find recipient"
│   ├── RecipientSearch
│   │   ├── AzureTagInput (prefix @)
│   │   ├── SearchButton
│   │   ├── Divider "Or by Account Number"
│   │   └── AccountNumberInput (AB-____-____-__)
│   ├── RecipientCard (when found)
│   │   ├── AzureTag (@username)
│   │   ├── DisplayName (masked: "John S.")
│   │   └── AccountSelector (RadioGroup)
│   ├── ErrorMessage (not found / self transfer)
│   └── Navigation (Back, Next)
│
├── Step 3: TransferAmountStep
│   ├── TransferSummaryHeader
│   │   ├── From: Account + Balance
│   │   └── To: @AzureTag + Account
│   ├── CurrencyInput (€ amount)
│   ├── QuickAmountButtons (€50, €100, €500, €1000)
│   ├── DescriptionInput (optional, max 140 chars)
│   ├── BalancePreview "Your new balance: €X"
│   ├── InsufficientFundsWarning (conditional)
│   └── Navigation (Back, Review)
│
├── Step 4: TransferConfirmStep
│   ├── Title "Confirm Transfer"
│   ├── TransferSummaryCard
│   │   ├── From Section (Account name + number)
│   │   ├── To Section (@AzureTag + masked name + account)
│   │   ├── Amount (€X.XX)
│   │   ├── Note (if provided)
│   │   ├── Divider
│   │   └── Balance After
│   ├── WarningMessage "Please verify all details"
│   └── Navigation (Back, Confirm Transfer)
│
└── TransferSuccessStep
    ├── SuccessIcon (checkmark)
    ├── Title "Transfer Successful!"
    ├── Amount + Recipient
    ├── ReferenceNumber
    └── ActionButtons (Done, New Transfer)
```

**Transfer Wizard State Flow**:
```
User clicks Transfer → Open Wizard → Step 1: Select source account
→ Step 2: Search recipient by @AzureTag or Account#
  → API: GET /api/recipients/search?q=...
  → Display recipient card with masked name
  → Select destination account
→ Step 3: Enter amount + optional description
  → Client-side balance validation
→ Step 4: Review and confirm
  → API: POST /api/transfers/external
→ Success: Show confirmation with reference number
```

---

## 4. State Management Architecture

### 4.1 Redux Store Structure

```typescript
// src/app/store.ts
import { configureStore } from '@reduxjs/toolkit';
import { setupListeners } from '@reduxjs/toolkit/query';
import { authApi } from '../features/auth/authApi';
import { accountsApi } from '../features/accounts/accountsApi';
import { transactionsApi } from '../features/transactions/transactionsApi';
import { recipientsApi } from '../features/recipients/recipientsApi';
import authReducer from '../features/auth/authSlice';
import uiReducer from '../features/ui/uiSlice';
import transferWizardReducer from '../features/transferWizard/transferWizardSlice';

export const store = configureStore({
  reducer: {
    // RTK Query APIs
    [authApi.reducerPath]: authApi.reducer,
    [accountsApi.reducerPath]: accountsApi.reducer,
    [transactionsApi.reducerPath]: transactionsApi.reducer,
    [recipientsApi.reducerPath]: recipientsApi.reducer,

    // Regular slices
    auth: authReducer,
    ui: uiReducer,
    transferWizard: transferWizardReducer,  // Transfer wizard state
  },
  middleware: (getDefaultMiddleware) =>
    getDefaultMiddleware()
      .concat(authApi.middleware)
      .concat(accountsApi.middleware)
      .concat(transactionsApi.middleware)
      .concat(recipientsApi.middleware),
});

setupListeners(store.dispatch);

export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
```

### 4.2 State Shape

```typescript
interface RootState {
  // RTK Query cache (managed automatically)
  authApi: RTKQueryState;
  accountsApi: RTKQueryState;
  transactionsApi: RTKQueryState;
  recipientsApi: RTKQueryState;

  // Auth state (BFF Pattern - NO token storage in browser)
  auth: {
    user: User | null;
    isAuthenticated: boolean;
    isLoading: boolean;
    sessionExpiresAt: string | null;  // For timeout warning UI
    authLevel: 1 | 2;                  // 1=basic, 2=PIN verified (step-up)
  };

  // UI state
  ui: {
    isMobileNavOpen: boolean;
    activeDialog: DialogType | null;
    toasts: Toast[];
    selectedAccountId: string | null;
  };

  // Transfer Wizard state (NEW)
  transferWizard: {
    isOpen: boolean;
    currentStep: 1 | 2 | 3 | 4 | 'success';
    sourceAccountNumber: string | null;
    recipient: {
      azureTag: string;
      displayName: string;
      accountNumber: string;
    } | null;
    amount: number | null;
    description: string;
    transactionReference: string | null;
    error: string | null;
  };
}
```

### 4.3 Auth Slice (BFF Pattern)

> **IMPORTANT**: AzureBank uses the BFF (Backend-for-Frontend) pattern.
> - JWT tokens are stored SERVER-SIDE only (never in browser)
> - Authentication uses HTTP-only session cookies
> - No localStorage token operations
> - See `08-security-design.md` for full BFF architecture

```typescript
// src/features/auth/authSlice.ts
import { createSlice, PayloadAction } from '@reduxjs/toolkit';
import { authApi } from './authApi';

interface AuthState {
  user: User | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  sessionExpiresAt: string | null;  // For timeout warning UI
  authLevel: 1 | 2;                  // 1=basic, 2=PIN verified
}

// BFF Pattern: No token in browser, session managed via HTTP-only cookie
const initialState: AuthState = {
  user: null,
  isAuthenticated: false,  // Will be set by session check on app load
  isLoading: true,
  sessionExpiresAt: null,
  authLevel: 1,
};

const authSlice = createSlice({
  name: 'auth',
  initialState,
  reducers: {
    // BFF Pattern: Only store user info, no token (session cookie handles auth)
    setCredentials: (
      state,
      action: PayloadAction<{
        user: User;
        sessionExpiresAt?: string;
        authLevel?: 1 | 2;
      }>
    ) => {
      state.user = action.payload.user;
      state.isAuthenticated = true;
      state.isLoading = false;
      state.sessionExpiresAt = action.payload.sessionExpiresAt ?? null;
      state.authLevel = action.payload.authLevel ?? 1;
      // NO localStorage - session managed by HTTP-only cookie
    },
    logout: (state) => {
      state.user = null;
      state.isAuthenticated = false;
      state.isLoading = false;
      state.sessionExpiresAt = null;
      state.authLevel = 1;
      // NO localStorage - BFF clears session cookie via /bff/auth/logout
    },
    setLoading: (state, action: PayloadAction<boolean>) => {
      state.isLoading = action.payload;
    },
    updateSessionExpiry: (state, action: PayloadAction<string>) => {
      state.sessionExpiresAt = action.payload;
    },
    setAuthLevel: (state, action: PayloadAction<1 | 2>) => {
      state.authLevel = action.payload;
    },
  },
  extraReducers: (builder) => {
    builder
      // BFF: Login returns user info only (no token)
      .addMatcher(
        authApi.endpoints.login.matchFulfilled,
        (state, { payload }) => {
          state.user = payload.user;
          state.isAuthenticated = true;
          state.sessionExpiresAt = payload.session?.expiresAt ?? null;
          state.authLevel = 1;
          // Session cookie set automatically by browser (HTTP-only)
        }
      )
      // BFF: Session check on app load
      .addMatcher(
        authApi.endpoints.getSession.matchFulfilled,
        (state, { payload }) => {
          state.user = payload.user;
          state.isAuthenticated = true;
          state.isLoading = false;
          state.sessionExpiresAt = payload.session?.expiresAt ?? null;
          state.authLevel = payload.session?.authLevel ?? 1;
        }
      )
      .addMatcher(
        authApi.endpoints.getSession.matchRejected,
        (state) => {
          state.user = null;
          state.isAuthenticated = false;
          state.isLoading = false;
          state.sessionExpiresAt = null;
          state.authLevel = 1;
          // No token to clear - session cookie expired/invalid
        }
      );
  },
});

export const {
  setCredentials,
  logout,
  setLoading,
  updateSessionExpiry,
  setAuthLevel
} = authSlice.actions;
export default authSlice.reducer;
```

---

## 5. RTK Query API Definitions

### 5.1 Base API Configuration (BFF Pattern)

> **BFF Pattern**: All requests go through the BFF gateway.
> - Use `credentials: 'include'` to send session cookie
> - NO Authorization header from browser (BFF injects JWT internally)
> - Session cookie is HTTP-only (JavaScript cannot access)

```typescript
// src/features/api/baseApi.ts
import { createApi, fetchBaseQuery } from '@reduxjs/toolkit/query/react';

// BFF Pattern: Session cookie sent automatically with credentials: 'include'
// NO token header needed - BFF injects JWT on server side
const baseQuery = fetchBaseQuery({
  baseUrl: '/api',  // All requests go through BFF proxy
  credentials: 'include',  // CRITICAL: Send HTTP-only session cookie
  prepareHeaders: (headers) => {
    headers.set('Content-Type', 'application/json');
    // NO Authorization header - BFF handles JWT injection
    return headers;
  },
});

// Wrapper for handling 401 errors (session expired)
const baseQueryWithSessionCheck = async (args, api, extraOptions) => {
  let result = await baseQuery(args, api, extraOptions);

  if (result.error && result.error.status === 401) {
    // Session expired or invalid - redirect to login
    // No token to clear - just update auth state
    api.dispatch({ type: 'auth/logout' });
    // Optional: redirect to login page
    window.location.href = '/login';
  }

  return result;
};

export const baseApi = createApi({
  baseQuery: baseQueryWithSessionCheck,
  tagTypes: ['Account', 'Transaction', 'User', 'Session'],
  endpoints: () => ({}),
});
```

### 5.2 Auth API (BFF Endpoints)

> **BFF Endpoints**: Auth calls go to `/bff/auth/*` endpoints.
> - Login creates server-side session, returns user only (NO token)
> - Session cookie set automatically by browser
> - getSession checks if session is valid on app load

```typescript
// src/features/auth/authApi.ts
import { baseApi } from '../api/baseApi';
import type {
  LoginRequest,
  LoginResponse,
  RegisterRequest,
  SessionResponse,
  User
} from '../../types/auth.types';

// BFF Response types (no token in response)
interface BffLoginResponse {
  data: {
    user: User;
  };
  message: string;
}

interface BffSessionResponse {
  data: {
    user: User;
    session: {
      authLevel: 1 | 2;
      createdAt: string;
      lastActivity: string;
      expiresAt: string;
    };
  };
}

export const authApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    // BFF: Login - returns user only, session cookie set by browser
    login: builder.mutation<BffLoginResponse, LoginRequest>({
      query: (credentials) => ({
        url: '/bff/auth/login',  // BFF endpoint
        method: 'POST',
        body: credentials,
      }),
      invalidatesTags: ['Session'],
    }),

    // BFF: Register - same as login, creates session
    register: builder.mutation<BffLoginResponse, RegisterRequest>({
      query: (userData) => ({
        url: '/bff/auth/register',  // BFF endpoint (or /api/auth/register)
        method: 'POST',
        body: userData,
      }),
    }),

    // BFF: Get current session - checks if session cookie is valid
    getSession: builder.query<BffSessionResponse, void>({
      query: () => '/bff/auth/me',  // BFF endpoint
      providesTags: ['Session', 'User'],
    }),

    // BFF: Logout - clears server session, expires cookie
    logout: builder.mutation<void, void>({
      query: () => ({
        url: '/bff/auth/logout',  // BFF endpoint
        method: 'POST',
      }),
      invalidatesTags: ['Session', 'User', 'Account', 'Transaction'],
    }),

    // BFF: Verify PIN for step-up authentication
    verifyPin: builder.mutation<{ authLevel: 2 }, { pin: string }>({
      query: (data) => ({
        url: '/bff/auth/verify-pin',  // BFF endpoint
        method: 'POST',
        body: data,
      }),
      invalidatesTags: ['Session'],
    }),

    // BFF: Get session timeout status (for warning UI)
    getSessionStatus: builder.query<{
      idleTimeoutAt: string;
      absoluteTimeoutAt: string;
    }, void>({
      query: () => '/bff/auth/session-status',
    }),
  }),
});

export const {
  useLoginMutation,
  useRegisterMutation,
  useGetSessionQuery,
  useLogoutMutation,
  useVerifyPinMutation,
  useGetSessionStatusQuery,
} = authApi;
```

### 5.3 Accounts API

```typescript
// src/features/accounts/accountsApi.ts
import { baseApi } from '../api/baseApi';
import type {
  Account,
  CreateAccountRequest,
  UpdateAccountRequest
} from '../../types/account.types';

export const accountsApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    getAccounts: builder.query<Account[], void>({
      query: () => '/accounts',
      providesTags: (result) =>
        result
          ? [
              ...result.map(({ id }) => ({ type: 'Account' as const, id })),
              { type: 'Account', id: 'LIST' },
            ]
          : [{ type: 'Account', id: 'LIST' }],
    }),

    getAccountById: builder.query<Account, string>({
      query: (id) => `/accounts/${id}`,
      providesTags: (result, error, id) => [{ type: 'Account', id }],
    }),

    createAccount: builder.mutation<Account, CreateAccountRequest>({
      query: (account) => ({
        url: '/accounts',
        method: 'POST',
        body: account,
      }),
      invalidatesTags: [{ type: 'Account', id: 'LIST' }],
    }),

    updateAccount: builder.mutation<Account, UpdateAccountRequest>({
      query: ({ id, ...patch }) => ({
        url: `/accounts/${id}`,
        method: 'PUT',
        body: patch,
      }),
      invalidatesTags: (result, error, { id }) => [
        { type: 'Account', id },
        { type: 'Account', id: 'LIST' },
      ],
    }),

    deleteAccount: builder.mutation<void, string>({
      query: (id) => ({
        url: `/accounts/${id}`,
        method: 'DELETE',
      }),
      invalidatesTags: (result, error, id) => [
        { type: 'Account', id },
        { type: 'Account', id: 'LIST' },
      ],
    }),
  }),
});

export const {
  useGetAccountsQuery,
  useGetAccountByIdQuery,
  useCreateAccountMutation,
  useUpdateAccountMutation,
  useDeleteAccountMutation,
} = accountsApi;
```

### 5.4 Transactions API

```typescript
// src/features/transactions/transactionsApi.ts
import { baseApi } from '../api/baseApi';
import type {
  Transaction,
  TransactionListResponse,
  TransactionFilters,
  DepositRequest,
  WithdrawRequest,
  TransferRequest,
} from '../../types/transaction.types';

export const transactionsApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    getTransactions: builder.query<
      TransactionListResponse,
      { accountId?: string; filters?: TransactionFilters }
    >({
      query: ({ accountId, filters }) => {
        const params = new URLSearchParams();
        if (accountId) params.append('accountId', accountId);
        if (filters?.type) params.append('type', filters.type);
        if (filters?.startDate) params.append('startDate', filters.startDate);
        if (filters?.endDate) params.append('endDate', filters.endDate);
        if (filters?.search) params.append('search', filters.search);
        if (filters?.page) params.append('page', filters.page.toString());
        if (filters?.pageSize) params.append('pageSize', filters.pageSize.toString());

        return `/transactions?${params.toString()}`;
      },
      providesTags: (result) =>
        result
          ? [
              ...result.items.map(({ id }) => ({
                type: 'Transaction' as const,
                id
              })),
              { type: 'Transaction', id: 'LIST' },
            ]
          : [{ type: 'Transaction', id: 'LIST' }],
    }),

    getTransactionById: builder.query<Transaction, string>({
      query: (id) => `/transactions/${id}`,
      providesTags: (result, error, id) => [{ type: 'Transaction', id }],
    }),

    deposit: builder.mutation<Transaction, DepositRequest>({
      query: (data) => ({
        url: '/transactions/deposit',
        method: 'POST',
        body: data,
      }),
      invalidatesTags: (result, error, { accountId }) => [
        { type: 'Transaction', id: 'LIST' },
        { type: 'Account', id: accountId },
        { type: 'Account', id: 'LIST' },
      ],
    }),

    withdraw: builder.mutation<Transaction, WithdrawRequest>({
      query: (data) => ({
        url: '/transactions/withdraw',
        method: 'POST',
        body: data,
      }),
      invalidatesTags: (result, error, { accountId }) => [
        { type: 'Transaction', id: 'LIST' },
        { type: 'Account', id: accountId },
        { type: 'Account', id: 'LIST' },
      ],
    }),

    transfer: builder.mutation<Transaction[], TransferRequest>({
      query: (data) => ({
        url: '/transactions/transfer',
        method: 'POST',
        body: data,
      }),
      invalidatesTags: (result, error, { fromAccountId, toAccountId }) => [
        { type: 'Transaction', id: 'LIST' },
        { type: 'Account', id: fromAccountId },
        { type: 'Account', id: toAccountId },
        { type: 'Account', id: 'LIST' },
      ],
    }),
  }),
});

export const {
  useGetTransactionsQuery,
  useGetTransactionByIdQuery,
  useDepositMutation,
  useWithdrawMutation,
  useTransferMutation,
} = transactionsApi;
```

---

## 6. Custom Hooks

### 6.1 useAuth Hook

```typescript
// src/hooks/useAuth.ts
import { useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAppDispatch, useAppSelector } from '../app/hooks';
import { logout } from '../features/auth/authSlice';
import {
  useLoginMutation,
  useRegisterMutation,
  useGetCurrentUserQuery,
} from '../features/auth/authApi';
import type { LoginRequest, RegisterRequest } from '../types/auth.types';

export function useAuth() {
  const dispatch = useAppDispatch();
  const navigate = useNavigate();
  const { user, token, isAuthenticated, isLoading } = useAppSelector(
    (state) => state.auth
  );

  const [loginMutation, { isLoading: isLoggingIn }] = useLoginMutation();
  const [registerMutation, { isLoading: isRegistering }] = useRegisterMutation();

  // Fetch current user if we have a token but no user
  const { isLoading: isLoadingUser } = useGetCurrentUserQuery(undefined, {
    skip: !token || !!user,
  });

  const login = useCallback(
    async (credentials: LoginRequest) => {
      try {
        await loginMutation(credentials).unwrap();
        navigate('/dashboard');
        return { success: true };
      } catch (error: any) {
        return {
          success: false,
          error: error.data?.message || 'Login failed'
        };
      }
    },
    [loginMutation, navigate]
  );

  const register = useCallback(
    async (userData: RegisterRequest) => {
      try {
        await registerMutation(userData).unwrap();
        navigate('/dashboard');
        return { success: true };
      } catch (error: any) {
        return {
          success: false,
          error: error.data?.message || 'Registration failed'
        };
      }
    },
    [registerMutation, navigate]
  );

  const signOut = useCallback(() => {
    dispatch(logout());
    navigate('/login');
  }, [dispatch, navigate]);

  return {
    user,
    isAuthenticated,
    isLoading: isLoading || isLoadingUser,
    isLoggingIn,
    isRegistering,
    login,
    register,
    logout: signOut,
  };
}
```

### 6.2 useAccounts Hook

```typescript
// src/hooks/useAccounts.ts
import { useMemo } from 'react';
import {
  useGetAccountsQuery,
  useGetAccountByIdQuery,
  useCreateAccountMutation,
  useDeleteAccountMutation,
} from '../features/accounts/accountsApi';
import type { CreateAccountRequest } from '../types/account.types';

export function useAccounts() {
  const { data: accounts, isLoading, error, refetch } = useGetAccountsQuery();
  const [createAccountMutation, { isLoading: isCreating }] = useCreateAccountMutation();
  const [deleteAccountMutation, { isLoading: isDeleting }] = useDeleteAccountMutation();

  const totalBalance = useMemo(() => {
    if (!accounts) return 0;
    return accounts.reduce((sum, account) => sum + account.balance, 0);
  }, [accounts]);

  const primaryAccount = useMemo(() => {
    if (!accounts?.length) return null;
    return accounts[0]; // Could add logic for primary account selection
  }, [accounts]);

  const createAccount = async (data: CreateAccountRequest) => {
    try {
      const result = await createAccountMutation(data).unwrap();
      return { success: true, account: result };
    } catch (error: any) {
      return {
        success: false,
        error: error.data?.message || 'Failed to create account'
      };
    }
  };

  const deleteAccount = async (accountId: string) => {
    try {
      await deleteAccountMutation(accountId).unwrap();
      return { success: true };
    } catch (error: any) {
      return {
        success: false,
        error: error.data?.message || 'Failed to delete account'
      };
    }
  };

  return {
    accounts: accounts || [],
    totalBalance,
    primaryAccount,
    isLoading,
    isCreating,
    isDeleting,
    error,
    refetch,
    createAccount,
    deleteAccount,
  };
}

export function useAccount(accountId: string) {
  const { data: account, isLoading, error } = useGetAccountByIdQuery(accountId, {
    skip: !accountId,
  });

  return {
    account,
    isLoading,
    error,
  };
}
```

### 6.3 useTransactions Hook

```typescript
// src/hooks/useTransactions.ts
import { useState, useCallback } from 'react';
import {
  useGetTransactionsQuery,
  useDepositMutation,
  useWithdrawMutation,
  useTransferMutation,
} from '../features/transactions/transactionsApi';
import type {
  TransactionFilters,
  DepositRequest,
  WithdrawRequest,
  TransferRequest,
} from '../types/transaction.types';

interface UseTransactionsOptions {
  accountId?: string;
  initialFilters?: TransactionFilters;
}

export function useTransactions(options: UseTransactionsOptions = {}) {
  const { accountId, initialFilters } = options;

  const [filters, setFilters] = useState<TransactionFilters>({
    page: 1,
    pageSize: 10,
    ...initialFilters,
  });

  const { data, isLoading, isFetching, error, refetch } = useGetTransactionsQuery({
    accountId,
    filters,
  });

  const [depositMutation, { isLoading: isDepositing }] = useDepositMutation();
  const [withdrawMutation, { isLoading: isWithdrawing }] = useWithdrawMutation();
  const [transferMutation, { isLoading: isTransferring }] = useTransferMutation();

  const updateFilters = useCallback((newFilters: Partial<TransactionFilters>) => {
    setFilters((prev) => ({
      ...prev,
      ...newFilters,
      page: newFilters.page ?? 1, // Reset to page 1 when filters change
    }));
  }, []);

  const resetFilters = useCallback(() => {
    setFilters({
      page: 1,
      pageSize: 10,
    });
  }, []);

  const deposit = async (data: DepositRequest) => {
    try {
      const result = await depositMutation(data).unwrap();
      return { success: true, transaction: result };
    } catch (error: any) {
      return {
        success: false,
        error: error.data?.message || 'Deposit failed'
      };
    }
  };

  const withdraw = async (data: WithdrawRequest) => {
    try {
      const result = await withdrawMutation(data).unwrap();
      return { success: true, transaction: result };
    } catch (error: any) {
      return {
        success: false,
        error: error.data?.message || 'Withdrawal failed'
      };
    }
  };

  const transfer = async (data: TransferRequest) => {
    try {
      const result = await transferMutation(data).unwrap();
      return { success: true, transactions: result };
    } catch (error: any) {
      return {
        success: false,
        error: error.data?.message || 'Transfer failed'
      };
    }
  };

  return {
    transactions: data?.items || [],
    totalCount: data?.totalCount || 0,
    totalPages: data?.totalPages || 0,
    currentPage: filters.page || 1,
    filters,
    isLoading,
    isFetching,
    isDepositing,
    isWithdrawing,
    isTransferring,
    error,
    updateFilters,
    resetFilters,
    refetch,
    deposit,
    withdraw,
    transfer,
  };
}
```

### 6.4 Utility Hooks

```typescript
// src/hooks/useMediaQuery.ts
import { useState, useEffect } from 'react';
import { breakpoints } from '../theme/breakpoints';

export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(false);

  useEffect(() => {
    const media = window.matchMedia(query);
    setMatches(media.matches);

    const listener = (event: MediaQueryListEvent) => {
      setMatches(event.matches);
    };

    media.addEventListener('change', listener);
    return () => media.removeEventListener('change', listener);
  }, [query]);

  return matches;
}

export function useIsMobile() {
  return useMediaQuery(`(max-width: ${breakpoints.tablet})`);
}

export function useIsTablet() {
  return useMediaQuery(
    `(min-width: ${breakpoints.tablet}) and (max-width: ${breakpoints.desktop})`
  );
}

export function useIsDesktop() {
  return useMediaQuery(`(min-width: ${breakpoints.desktop})`);
}
```

```typescript
// src/hooks/useCurrency.ts
import { useCallback } from 'react';

interface CurrencyOptions {
  currency?: string;
  locale?: string;
  showSign?: boolean;
}

export function useCurrency(options: CurrencyOptions = {}) {
  const {
    currency = 'EUR',
    locale = 'en-IE',
    showSign = false,
  } = options;

  const format = useCallback((amount: number): string => {
    const formatted = new Intl.NumberFormat(locale, {
      style: 'currency',
      currency,
    }).format(Math.abs(amount));

    if (showSign && amount !== 0) {
      return amount > 0 ? `+${formatted}` : `-${formatted}`;
    }

    return formatted;
  }, [currency, locale, showSign]);

  const parse = useCallback((value: string): number => {
    // Remove currency symbol and thousand separators
    const cleaned = value.replace(/[^0-9.-]/g, '');
    return parseFloat(cleaned) || 0;
  }, []);

  return { format, parse };
}
```

```typescript
// src/hooks/useDebounce.ts
import { useState, useEffect } from 'react';

export function useDebounce<T>(value: T, delay: number = 300): T {
  const [debouncedValue, setDebouncedValue] = useState<T>(value);

  useEffect(() => {
    const handler = setTimeout(() => {
      setDebouncedValue(value);
    }, delay);

    return () => {
      clearTimeout(handler);
    };
  }, [value, delay]);

  return debouncedValue;
}
```

---

## 7. TypeScript Interfaces

### 7.1 Auth Types

```typescript
// src/types/auth.types.ts

export interface User {
  id: string;                    // Internal ID (NEVER expose to other users)
  azureTag: string;              // Public identifier (@username) - IMMUTABLE
  email: string;
  firstName: string;
  surname: string;
  displayName: string;           // Masked: "John S." for privacy
  createdAt: string;
  updatedAt: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface LoginResponse {
  user: User;
  token: string;
  expiresAt: string;
}

export interface RegisterRequest {
  firstName: string;
  surname: string;
  azureTag: string;              // NEW: @username (3-20 chars, alphanumeric+_)
  email: string;
  password: string;
  confirmPassword: string;
}

// AzureTag validation regex
export const AZURE_TAG_REGEX = /^@[a-zA-Z][a-zA-Z0-9_]{2,19}$/;
```

### 7.2 Account Types

```typescript
// src/types/account.types.ts

export type AccountType = 'Savings' | 'Checking' | 'Investment';

export interface Account {
  id: string;                    // Internal ID (NEVER expose to other users)
  userId: string;                // Internal reference
  name: string;
  accountNumber: string;         // AzureBank format: AB-XXXX-XXXX-XX
  accountType: AccountType;
  balance: number;
  currency: string;              // Default: 'EUR'
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

// Public account info for transfer recipient display
export interface AccountPublicInfo {
  accountNumber: string;         // AB-XXXX-XXXX-XX
  accountType: AccountType;
  // NO balance, NO name - privacy protection
}

export interface CreateAccountRequest {
  name: string;
  accountType: AccountType;
  initialDeposit?: number;
}

export interface UpdateAccountRequest {
  id: string;
  name?: string;
  accountType?: AccountType;
  isActive?: boolean;
}

// Account number format validation
export const ACCOUNT_NUMBER_REGEX = /^AB-\d{4}-\d{4}-\d{2}$/;
```

### 7.3 Transaction Types

```typescript
// src/types/transaction.types.ts

export type TransactionType = 'Deposit' | 'Withdrawal' | 'TransferIn' | 'TransferOut';

export interface Transaction {
  id: string;
  accountId: string;
  type: TransactionType;
  amount: number;
  balance: number;              // Balance after transaction
  description: string;
  referenceNumber: string;
  // For external transfers - public identifiers only
  counterpartyAzureTag?: string;   // @username of the other party
  counterpartyAccountNumber?: string; // AB-XXXX-XXXX-XX
  createdAt: string;
}

export interface TransactionFilters {
  type?: TransactionType;
  startDate?: string;
  endDate?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface TransactionListResponse {
  items: Transaction[];
  totalCount: number;
  totalPages: number;
  currentPage: number;
  pageSize: number;
}

export interface DepositRequest {
  accountNumber: string;       // Use account number, not internal ID
  amount: number;
  description?: string;
}

export interface WithdrawRequest {
  accountNumber: string;       // Use account number, not internal ID
  amount: number;
  description?: string;
}

// Legacy internal transfer (between own accounts)
export interface InternalTransferRequest {
  fromAccountNumber: string;
  toAccountNumber: string;
  amount: number;
  description?: string;
}

// NEW: External transfer to another user
export interface ExternalTransferRequest {
  fromAccountNumber: string;     // Sender's account (AB-XXXX-XXXX-XX)
  toAzureTag: string;            // Recipient's @username
  toAccountNumber: string;       // Recipient's specific account
  amount: number;
  description?: string;          // Optional, max 140 chars
}

export interface ExternalTransferResponse {
  success: boolean;
  transactionReference: string;  // TXN-YYYYMMDDXXXXX
  amount: number;
  fromAccountNumber: string;
  toAzureTag: string;
  toAccountNumber: string;
  newBalance: number;
  timestamp: string;
}
```

### 7.4 Recipient Search Types (NEW)

```typescript
// src/types/recipient.types.ts

export interface RecipientSearchRequest {
  query: string;                 // @azureTag or AB-XXXX-XXXX-XX
}

export interface RecipientPublicProfile {
  azureTag: string;              // @username
  displayName: string;           // "John S." - masked for privacy
  accounts: RecipientAccountInfo[];
}

export interface RecipientAccountInfo {
  accountNumber: string;         // AB-XXXX-XXXX-XX
  accountType: 'Savings' | 'Checking' | 'Investment';
  // NO balance exposed - privacy
}

export interface RecipientSearchResponse {
  found: boolean;
  recipient?: RecipientPublicProfile;
  error?: 'NOT_FOUND' | 'SELF_TRANSFER' | 'RATE_LIMITED' | 'INVALID_FORMAT';
}

// Rate limiting: max 5 searches per minute
export const RECIPIENT_SEARCH_RATE_LIMIT = 5;
```

### 7.5 API Response Types

```typescript
// src/types/api.types.ts

export interface ApiError {
  status: number;
  message: string;
  errors?: Record<string, string[]>;
}

export interface PaginatedResponse<T> {
  items: T[];
  totalCount: number;
  totalPages: number;
  currentPage: number;
  pageSize: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
}

export interface ValidationError {
  field: string;
  message: string;
}
```

---

## 8. FluentUI Component Mapping

### 8.1 Core Components Used

| Feature | FluentUI Component | Custom Wrapper |
|---------|-------------------|----------------|
| Primary Buttons | `<Button appearance="primary">` | - |
| Secondary Buttons | `<Button appearance="outline">` | - |
| Danger Buttons | `<Button>` with custom styles | - |
| Text Input | `<Input>` | - |
| Password Input | `<Input type="password">` | `<PasswordInput>` |
| Currency Input | `<Input>` with prefix | `<CurrencyInput>` |
| Select/Dropdown | `<Dropdown>` | - |
| Date Picker | `<DatePicker>` | - |
| Dialog/Modal | `<Dialog>` | - |
| Toast | `<Toaster>` / `<Toast>` | - |
| Spinner | `<Spinner>` | - |
| Card | `<Card>` | `<BalanceCard>`, `<TransactionCard>` |
| Table | `<Table>` | `<TransactionTable>` |
| Menu | `<Menu>`, `<MenuTrigger>`, `<MenuPopover>` | - |
| Avatar | `<Avatar>` | - |
| Badge | `<Badge>` | - |
| Divider | `<Divider>` | - |
| Skeleton | `<Skeleton>` | - |
| Link | `<Link>` | - |

### 8.2 Custom Component Specifications

#### BalanceCard

```typescript
// src/components/common/BalanceCard/BalanceCard.tsx
interface BalanceCardProps {
  account: Account;
  variant?: 'hero' | 'compact';
  showActions?: boolean;
  onDeposit?: () => void;
  onWithdraw?: () => void;
  onTransfer?: () => void;
  className?: string;
}
```

#### TransactionCard

```typescript
// src/components/common/TransactionCard/TransactionCard.tsx
interface TransactionCardProps {
  transaction: Transaction;
  showAccount?: boolean;
  onClick?: () => void;
  className?: string;
}
```

#### CurrencyInput

```typescript
// src/components/common/CurrencyInput/CurrencyInput.tsx
interface CurrencyInputProps {
  value: number;
  onChange: (value: number) => void;
  currency?: string;
  label?: string;
  error?: string;
  required?: boolean;
  disabled?: boolean;
  min?: number;
  max?: number;
}
```

#### PasswordInput

```typescript
// src/components/common/PasswordInput/PasswordInput.tsx
interface PasswordInputProps {
  value: string;
  onChange: (value: string) => void;
  label?: string;
  error?: string;
  required?: boolean;
  showStrengthIndicator?: boolean;
  placeholder?: string;
}
```

#### FilterBar

```typescript
// src/components/common/FilterBar/FilterBar.tsx
interface FilterBarProps {
  filters: TransactionFilters;
  onFiltersChange: (filters: Partial<TransactionFilters>) => void;
  onReset: () => void;
  showAccountFilter?: boolean;
  accounts?: Account[];
}
```

---

## 9. Routing Configuration

```typescript
// src/App.tsx
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { ProtectedRoute } from './components/auth/ProtectedRoute';
import { AppLayout } from './components/layout/AppLayout';

// Lazy load pages for code splitting
const LoginPage = lazy(() => import('./pages/LoginPage'));
const RegisterPage = lazy(() => import('./pages/RegisterPage'));
const DashboardPage = lazy(() => import('./pages/DashboardPage'));
const AccountsPage = lazy(() => import('./pages/AccountsPage'));
const AccountDetailPage = lazy(() => import('./pages/AccountDetailPage'));
const TransactionsPage = lazy(() => import('./pages/TransactionsPage'));
const NotFoundPage = lazy(() => import('./pages/NotFoundPage'));

function App() {
  return (
    <BrowserRouter>
      <Suspense fallback={<LoadingSpinner />}>
        <Routes>
          {/* Public Routes */}
          <Route path="/login" element={<LoginPage />} />
          <Route path="/register" element={<RegisterPage />} />

          {/* Protected Routes */}
          <Route element={<ProtectedRoute />}>
            <Route element={<AppLayout />}>
              <Route path="/dashboard" element={<DashboardPage />} />
              <Route path="/accounts" element={<AccountsPage />} />
              <Route path="/accounts/:id" element={<AccountDetailPage />} />
              <Route path="/transactions" element={<TransactionsPage />} />
            </Route>
          </Route>

          {/* Redirects */}
          <Route path="/" element={<Navigate to="/dashboard" replace />} />

          {/* 404 */}
          <Route path="*" element={<NotFoundPage />} />
        </Routes>
      </Suspense>
    </BrowserRouter>
  );
}
```

---

## 10. Technical Feasibility Assessment

### 10.1 FluentUI v9 Capabilities

| Requirement | FluentUI Support | Notes |
|-------------|-----------------|-------|
| Custom Theming | ✅ Full | Brand colors, tokens fully customizable |
| Responsive Design | ✅ Full | Built-in responsive utilities |
| Accessibility | ✅ Excellent | WCAG 2.1 AA compliant out of box |
| Dark Mode | ✅ Full | `createDarkTheme()` available |
| RTL Support | ✅ Full | Built-in |
| Form Validation | ⚠️ Partial | Needs react-hook-form integration |
| Data Tables | ✅ Full | Table + DataGrid components |
| Modals/Dialogs | ✅ Full | Dialog component |
| Toast Notifications | ✅ Full | Toaster component |
| Date Picker | ✅ Full | DatePicker component |
| Icons | ✅ Full | @fluentui/react-icons |

### 10.2 Implementation Complexity

| Component | Complexity | Effort Estimate | Risk |
|-----------|-----------|-----------------|------|
| Auth Flow | Medium | Standard | Low |
| Dashboard | Medium | Standard | Low |
| Account CRUD | Low | Simple | Low |
| Transactions | Medium | Standard | Low |
| Transfer Flow | Medium | Standard | Medium |
| Filter/Search | Medium | Standard | Low |
| Responsive Layout | Low | Standard | Low |
| Theme Setup | Low | Simple | Low |

### 10.3 Third-Party Dependencies

```json
{
  "dependencies": {
    // Core
    "react": "^19.2.3",
    "react-dom": "^19.2.3",
    "react-router-dom": "^7.1.0",

    // State Management
    "@reduxjs/toolkit": "^2.5.0",
    "react-redux": "^9.2.0",

    // UI Framework
    "@fluentui/react-components": "^9.56.9",
    "@fluentui/react-icons": "^2.0.265",

    // Form Handling
    "react-hook-form": "^7.54.2",
    "@hookform/resolvers": "^3.9.1",
    "zod": "^3.24.1"
  },
  "devDependencies": {
    // Build Tools
    "vite": "^6.0.5",
    "@vitejs/plugin-react": "^4.3.4",
    "typescript": "~5.7.2",

    // Testing
    "@testing-library/react": "^16.1.0",
    "vitest": "^2.1.8",
    "msw": "^2.7.0"
  }
}
```

### 10.4 Performance Considerations

| Area | Strategy | Implementation |
|------|----------|---------------|
| Bundle Size | Code splitting | `React.lazy()` for routes |
| API Calls | Caching | RTK Query cache |
| Re-renders | Memoization | `useMemo`, `useCallback`, `React.memo` |
| Lists | Virtualization | Consider for large transaction lists |
| Images | Lazy loading | Native `loading="lazy"` |
| State | Normalization | RTK Query handles automatically |

### 10.5 Testing Strategy

```
Unit Tests
├── Utils (formatCurrency, formatDate, validation)
├── Custom Hooks (useAuth, useAccounts, useTransactions)
└── Redux Slices (authSlice)

Component Tests
├── Common Components (BalanceCard, TransactionCard)
├── Form Components (LoginForm, RegisterForm)
└── Dialog Components (DepositDialog, TransferDialog)

Integration Tests
├── Auth Flow (Login → Dashboard)
├── Account Management (Create → View → Delete)
└── Transaction Flow (Deposit → View in list)

E2E Tests (Playwright)
├── Complete user journey
├── Error handling scenarios
└── Mobile responsiveness
```

---

## 11. Component Implementation Priority

### Phase 1: Core Infrastructure
1. Theme setup
2. Redux store configuration
3. Base API setup
4. Protected route
5. App layout

### Phase 2: Authentication
6. Login page/form
7. Register page/form
8. Auth state management

### Phase 3: Dashboard
9. Dashboard page
10. BalanceCard component
11. QuickActions component
12. RecentTransactions component

### Phase 4: Accounts
13. AccountList component
14. AccountCard component
15. CreateAccountDialog
16. AccountDetailPage

### Phase 5: Transactions
17. TransactionCard component
18. TransactionTable component
19. FilterBar component
20. Deposit/Withdraw dialogs

### Phase 6: External Transfers (CRITICAL)
21. Recipients API integration
22. TransferWizard (4-step)
23. RecipientSearch component
24. TransferSourceStep
25. TransferRecipientStep
26. TransferAmountStep
27. TransferConfirmStep
28. TransferSuccessStep

### Phase 7: Industry Standard Enhancements (NEW)
29. Skeleton loading components (BalanceCardSkeleton, TransactionListSkeleton)
30. LoadingButton component
31. AnimatedNumber component (balance animation)
32. ProgressStepper component (wizard indicator)
33. SuccessAnimation component (celebration feedback)
34. Transaction date grouping

### Phase 8: Polish
35. Error handling refinements
36. Empty states
37. Mobile optimizations
38. Touch target refinements
39. Accessibility audit

---

## 12. Enhanced Components (Industry Standards)

Based on industry research (see 04m-industry-standards-analysis.md), the following components have been added to align with 2025 enterprise fintech standards.

### 12.1 Skeleton Loading Components

```typescript
// src/components/common/Skeleton/BalanceCardSkeleton.tsx
import { makeStyles, shorthands } from '@fluentui/react-components';

const useStyles = makeStyles({
  skeleton: {
    background: 'linear-gradient(90deg, #E5E7EB 0%, #F3F4F6 50%, #E5E7EB 100%)',
    backgroundSize: '200% 100%',
    animationName: {
      '0%': { backgroundPosition: '-200% 0' },
      '100%': { backgroundPosition: '200% 0' },
    },
    animationDuration: '1.5s',
    animationIterationCount: 'infinite',
    ...shorthands.borderRadius('4px'),
  },
  card: {
    minHeight: '200px',
    ...shorthands.padding('24px'),
    ...shorthands.borderRadius('12px'),
    backgroundColor: '#E5E7EB',
  },
});

export const BalanceCardSkeleton: React.FC = () => {
  const styles = useStyles();
  return (
    <div className={styles.card}>
      <div className={styles.skeleton} style={{ height: 20, width: '40%', marginBottom: 8 }} />
      <div className={styles.skeleton} style={{ height: 14, width: '30%', marginBottom: 24 }} />
      <div className={styles.skeleton} style={{ height: 48, width: '60%', marginBottom: 24 }} />
      <div style={{ display: 'flex', gap: 12 }}>
        <div className={styles.skeleton} style={{ height: 32, width: 80 }} />
        <div className={styles.skeleton} style={{ height: 32, width: 80 }} />
        <div className={styles.skeleton} style={{ height: 32, width: 80 }} />
      </div>
    </div>
  );
};
```

### 12.2 LoadingButton Component

```typescript
// src/components/common/LoadingButton/LoadingButton.tsx
import { Button, ButtonProps, Spinner } from '@fluentui/react-components';

interface LoadingButtonProps extends ButtonProps {
  isLoading?: boolean;
  loadingText?: string;
}

export const LoadingButton: React.FC<LoadingButtonProps> = ({
  isLoading,
  loadingText = 'Processing...',
  children,
  disabled,
  style,
  ...props
}) => {
  return (
    <Button
      {...props}
      disabled={disabled || isLoading}
      style={{
        ...style,
        minWidth: 'fit-content', // Prevent layout shift
      }}
    >
      {isLoading ? (
        <>
          <Spinner size="tiny" style={{ marginRight: 8 }} />
          {loadingText}
        </>
      ) : (
        children
      )}
    </Button>
  );
};
```

### 12.3 AnimatedNumber Component

```typescript
// src/components/common/AnimatedNumber/AnimatedNumber.tsx
import { useEffect, useState, useRef, useMemo } from 'react';

interface AnimatedNumberProps {
  value: number;
  duration?: number;
  prefix?: string;
  suffix?: string;
  decimals?: number;
  locale?: string;
}

export const AnimatedNumber: React.FC<AnimatedNumberProps> = ({
  value,
  duration = 800,
  prefix = '',
  suffix = '',
  decimals = 2,
  locale = 'en-IE',
}) => {
  const [displayValue, setDisplayValue] = useState(0);
  const previousValue = useRef(0);
  const animationRef = useRef<number>();

  // Check for reduced motion preference
  const prefersReducedMotion = useMemo(
    () => window.matchMedia('(prefers-reduced-motion: reduce)').matches,
    []
  );

  useEffect(() => {
    if (prefersReducedMotion) {
      setDisplayValue(value);
      return;
    }

    const startValue = previousValue.current;
    const startTime = performance.now();

    const animate = (currentTime: number) => {
      const elapsed = currentTime - startTime;
      const progress = Math.min(elapsed / duration, 1);

      // Ease out cubic
      const easeOut = 1 - Math.pow(1 - progress, 3);
      const current = startValue + (value - startValue) * easeOut;

      setDisplayValue(current);

      if (progress < 1) {
        animationRef.current = requestAnimationFrame(animate);
      } else {
        previousValue.current = value;
      }
    };

    animationRef.current = requestAnimationFrame(animate);

    return () => {
      if (animationRef.current) {
        cancelAnimationFrame(animationRef.current);
      }
    };
  }, [value, duration, prefersReducedMotion]);

  const formattedValue = new Intl.NumberFormat(locale, {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  }).format(displayValue);

  return (
    <span aria-label={`${prefix}${formattedValue}${suffix}`}>
      {prefix}{formattedValue}{suffix}
    </span>
  );
};

// Custom hook for use in other components
export const useAnimatedNumber = (value: number, duration = 800) => {
  const [displayValue, setDisplayValue] = useState(value);
  // ... same animation logic
  return displayValue;
};
```

### 12.4 ProgressStepper Component

```typescript
// src/components/common/ProgressStepper/ProgressStepper.tsx
import { makeStyles, tokens, mergeClasses } from '@fluentui/react-components';
import { CheckmarkFilled } from '@fluentui/react-icons';

interface Step {
  id: string;
  label: string;
}

interface ProgressStepperProps {
  steps: Step[];
  currentStepIndex: number;
  completedSteps?: string[];
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '0',
  },
  stepWrapper: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
  },
  step: {
    width: '32px',
    height: '32px',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '14px',
    fontWeight: 600,
    transition: 'all 200ms ease',
  },
  stepCompleted: {
    backgroundColor: '#34A853',
    color: 'white',
  },
  stepCurrent: {
    backgroundColor: '#006DE2',
    color: 'white',
    boxShadow: '0 0 0 4px rgba(0, 109, 226, 0.2)',
  },
  stepFuture: {
    backgroundColor: '#E5E7EB',
    color: '#6B7280',
  },
  connector: {
    width: '48px',
    height: '2px',
    marginTop: '-16px', // Align with center of circles
  },
  connectorCompleted: {
    backgroundColor: '#34A853',
  },
  connectorFuture: {
    backgroundColor: '#D1D5DB',
  },
  label: {
    marginTop: '8px',
    fontSize: '12px',
    fontWeight: 500,
  },
  labelActive: {
    color: '#1F2937',
  },
  labelInactive: {
    color: '#6B7280',
  },
});

export const ProgressStepper: React.FC<ProgressStepperProps> = ({
  steps,
  currentStepIndex,
  completedSteps = [],
}) => {
  const styles = useStyles();

  const isCompleted = (stepIndex: number) =>
    stepIndex < currentStepIndex || completedSteps.includes(steps[stepIndex].id);

  return (
    <div className={styles.container} role="progressbar" aria-valuenow={currentStepIndex + 1} aria-valuemin={1} aria-valuemax={steps.length}>
      {steps.map((step, index) => (
        <React.Fragment key={step.id}>
          <div className={styles.stepWrapper}>
            <div
              className={mergeClasses(
                styles.step,
                isCompleted(index)
                  ? styles.stepCompleted
                  : index === currentStepIndex
                  ? styles.stepCurrent
                  : styles.stepFuture
              )}
              aria-current={index === currentStepIndex ? 'step' : undefined}
            >
              {isCompleted(index) ? (
                <CheckmarkFilled fontSize={16} />
              ) : (
                index + 1
              )}
            </div>
            <span
              className={mergeClasses(
                styles.label,
                index <= currentStepIndex ? styles.labelActive : styles.labelInactive
              )}
            >
              {step.label}
            </span>
          </div>
          {index < steps.length - 1 && (
            <div
              className={mergeClasses(
                styles.connector,
                isCompleted(index) ? styles.connectorCompleted : styles.connectorFuture
              )}
            />
          )}
        </React.Fragment>
      ))}
    </div>
  );
};
```

### 12.5 SuccessAnimation Component

```typescript
// src/components/feedback/SuccessAnimation/SuccessAnimation.tsx
import { useEffect, useState, useMemo } from 'react';
import { makeStyles, tokens } from '@fluentui/react-components';
import { CheckmarkCircleFilled } from '@fluentui/react-icons';

type AnimationLevel = 'subtle' | 'moderate' | 'celebration';

interface SuccessAnimationProps {
  level?: AnimationLevel;
  message?: string;
  subMessage?: string;
  onComplete?: () => void;
  autoHideDelay?: number; // ms, 0 to disable
}

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '32px',
    textAlign: 'center',
  },
  icon: {
    color: '#34A853',
    transform: 'scale(0)',
    animation: 'scaleIn 300ms ease-out forwards',
  },
  iconSubtle: {
    fontSize: '48px',
  },
  iconModerate: {
    fontSize: '64px',
  },
  iconCelebration: {
    fontSize: '80px',
  },
  ring: {
    position: 'absolute',
    borderRadius: '50%',
    border: '2px solid #34A853',
    animation: 'ringExpand 500ms ease-out forwards',
  },
  message: {
    marginTop: '16px',
    fontSize: '20px',
    fontWeight: 600,
    color: '#1F2937',
  },
  subMessage: {
    marginTop: '8px',
    fontSize: '14px',
    color: '#6B7280',
  },
  // Keyframes defined in global CSS
});

export const SuccessAnimation: React.FC<SuccessAnimationProps> = ({
  level = 'moderate',
  message = 'Success!',
  subMessage,
  onComplete,
  autoHideDelay = 0,
}) => {
  const styles = useStyles();
  const [showConfetti, setShowConfetti] = useState(false);

  const prefersReducedMotion = useMemo(
    () => window.matchMedia('(prefers-reduced-motion: reduce)').matches,
    []
  );

  useEffect(() => {
    if (level === 'celebration' && !prefersReducedMotion) {
      setShowConfetti(true);
      const timer = setTimeout(() => setShowConfetti(false), 1500);
      return () => clearTimeout(timer);
    }
  }, [level, prefersReducedMotion]);

  useEffect(() => {
    if (autoHideDelay > 0 && onComplete) {
      const timer = setTimeout(onComplete, autoHideDelay);
      return () => clearTimeout(timer);
    }
  }, [autoHideDelay, onComplete]);

  const iconClass = mergeClasses(
    styles.icon,
    level === 'subtle' && styles.iconSubtle,
    level === 'moderate' && styles.iconModerate,
    level === 'celebration' && styles.iconCelebration
  );

  return (
    <div className={styles.container}>
      {showConfetti && <Confetti />}
      <div style={{ position: 'relative' }}>
        <CheckmarkCircleFilled className={iconClass} />
        {level !== 'subtle' && !prefersReducedMotion && (
          <div className={styles.ring} />
        )}
      </div>
      <div className={styles.message}>{message}</div>
      {subMessage && <div className={styles.subMessage}>{subMessage}</div>}
    </div>
  );
};

// Simple confetti component
const Confetti: React.FC = () => {
  // Generate 20 confetti pieces with random positions/colors
  const pieces = useMemo(() =>
    Array.from({ length: 20 }, (_, i) => ({
      id: i,
      left: Math.random() * 100,
      delay: Math.random() * 500,
      color: ['#34A853', '#006DE2', '#F59E0B', '#0EA5E9'][Math.floor(Math.random() * 4)],
    })),
    []
  );

  return (
    <div style={{ position: 'absolute', inset: 0, overflow: 'hidden', pointerEvents: 'none' }}>
      {pieces.map((piece) => (
        <div
          key={piece.id}
          style={{
            position: 'absolute',
            left: `${piece.left}%`,
            top: '-10px',
            width: '8px',
            height: '8px',
            backgroundColor: piece.color,
            borderRadius: '2px',
            animation: `confettiFall 1.5s ease-out ${piece.delay}ms forwards`,
          }}
        />
      ))}
    </div>
  );
};
```

### 12.6 Transaction Date Grouping Utility

```typescript
// src/utils/groupTransactionsByDate.ts
import { Transaction } from '../types/transaction.types';

export interface GroupedTransactions {
  [date: string]: Transaction[];
}

export function groupTransactionsByDate(transactions: Transaction[]): GroupedTransactions {
  const now = new Date();
  const today = now.toDateString();
  const yesterday = new Date(now.setDate(now.getDate() - 1)).toDateString();
  const weekAgo = new Date(now.setDate(now.getDate() - 6)).getTime();

  return transactions.reduce((groups, transaction) => {
    const txDate = new Date(transaction.createdAt);
    const txDateString = txDate.toDateString();

    let groupKey: string;
    if (txDateString === today) {
      groupKey = 'Today';
    } else if (txDateString === yesterday) {
      groupKey = 'Yesterday';
    } else if (txDate.getTime() >= weekAgo) {
      groupKey = 'This Week';
    } else if (txDate.getFullYear() === new Date().getFullYear()) {
      groupKey = txDate.toLocaleDateString('en-GB', { month: 'short', day: 'numeric' });
    } else {
      groupKey = txDate.toLocaleDateString('en-GB', { month: 'short', day: 'numeric', year: 'numeric' });
    }

    if (!groups[groupKey]) {
      groups[groupKey] = [];
    }
    groups[groupKey].push(transaction);

    return groups;
  }, {} as GroupedTransactions);
}

// Usage in TransactionList component:
// const groupedTransactions = useMemo(
//   () => groupTransactionsByDate(transactions),
//   [transactions]
// );
```

### 12.7 Global Animation Keyframes

```css
/* src/styles/animations.css - Add to global styles */

@keyframes scaleIn {
  from {
    transform: scale(0.5);
    opacity: 0;
  }
  to {
    transform: scale(1);
    opacity: 1;
  }
}

@keyframes ringExpand {
  from {
    width: 64px;
    height: 64px;
    opacity: 1;
  }
  to {
    width: 96px;
    height: 96px;
    opacity: 0;
  }
}

@keyframes confettiFall {
  from {
    transform: translateY(0) rotate(0deg);
    opacity: 1;
  }
  to {
    transform: translateY(300px) rotate(720deg);
    opacity: 0;
  }
}

@keyframes shimmer {
  0% {
    background-position: -200% 0;
  }
  100% {
    background-position: 200% 0;
  }
}

/* Respect reduced motion preference */
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

---

## 12. Enhanced UX Components (v5.0)

> These components implement industry-standard UX patterns from the 04m-industry-standards-analysis.md.

### 12.1 Skeleton Loading Components

#### BalanceCardSkeleton

| Element | Animation | Dimensions |
|---------|-----------|------------|
| Balance area | Shimmer pulse | 200px x 48px |
| Label area | Shimmer pulse | 120px x 16px |
| Action buttons | Shimmer pulse | 3 x 80px circles |

```typescript
interface SkeletonProps {
  width?: string | number;
  height?: string | number;
  variant?: 'text' | 'circular' | 'rectangular';
  animation?: 'pulse' | 'wave';
}

// BalanceCardSkeleton usage
<BalanceCardSkeleton /> // Shows while account data loads
```

#### TransactionListSkeleton

| Element | Count | Dimensions |
|---------|-------|------------|
| Transaction row | 5 items | 100% x 72px |
| Icon placeholder | Per row | 40px circle |
| Text lines | 2 per row | 60% / 40% width |
| Amount | Per row | 80px x 20px |

#### AccountCardSkeleton

| Element | Animation | Dimensions |
|---------|-----------|------------|
| Card container | None | 100% x 140px |
| Account name | Shimmer | 70% x 20px |
| Balance | Shimmer | 50% x 32px |
| Account number | Shimmer | 40% x 14px |

### 12.2 AnimatedNumber Component

> Provides count-up animation for balance reveals, inspired by Stripe/Revolut.

| Prop | Type | Default | Description |
|------|------|---------|-------------|
| value | number | required | Target value to animate to |
| duration | number | 800 | Animation duration in ms |
| decimals | number | 2 | Decimal places to show |
| prefix | string | '' | Currency symbol (e.g., '€') |
| easing | string | 'easeOutCubic' | Easing function |

**Animation Behavior**:

| Scenario | Behavior |
|----------|----------|
| Initial mount | Animate from 0 to value |
| Value increase | Animate from old to new |
| Value decrease | Animate from old to new (same timing) |
| Rapid updates | Cancel previous, start new |

**Easing Functions**:

| Name | Curve | Use Case |
|------|-------|----------|
| easeOutCubic | cubic-bezier(0.33, 1, 0.68, 1) | Default - satisfying deceleration |
| easeOutExpo | cubic-bezier(0.16, 1, 0.3, 1) | Dramatic reveals |
| linear | linear | Continuous counters |

### 12.3 Date Grouping (TransactionList)

> Groups transactions by date with sticky headers.

**Group Labels**:

| Condition | Label |
|-----------|-------|
| Today | "Today" |
| Yesterday | "Yesterday" |
| Same week | Day name (e.g., "Monday") |
| This year | "Month Day" (e.g., "January 5") |
| Previous years | "Month Day, Year" (e.g., "December 15, 2025") |

**Visual Treatment**:

| Element | Style |
|---------|-------|
| Group header | 12px, semibold, neutral foreground 2 |
| Sticky behavior | Stick to top on scroll |
| Padding | 12px top, 8px bottom |
| Divider | 1px border below (subtle) |

### 12.4 CopyButton Component

> One-click copy for AzureTag and Account Number.

| Prop | Type | Default | Description |
|------|------|---------|-------------|
| value | string | required | Text to copy |
| label | string | 'Copy' | Tooltip text |
| size | 'small' \| 'medium' | 'small' | Button size |
| showToast | boolean | true | Show confirmation toast |

**States**:

| State | Icon | Duration |
|-------|------|----------|
| Default | CopyRegular | - |
| Copying | Spinner | 100ms |
| Copied | CheckmarkRegular | 2000ms then reset |
| Error | DismissRegular | 2000ms then reset |

**Toast Messages**:

| State | Message | Type |
|-------|---------|------|
| Success | "Copied to clipboard" | info |
| Error | "Failed to copy" | error |

**Usage Locations**:

| Location | Value Copied |
|----------|--------------|
| Profile header | @AzureTag |
| Account card | Account Number |
| Transaction detail | Transaction ID |

---

**Document Status**: FINAL - BFF Pattern Aligned + Enhanced Components

**Change Log**:
| Version | Date | Changes |
|---------|------|---------|
| 3.0 | 2025-12-17 | External Transfers (AzureTag system) |
| 4.0 | 2025-12-17 | Industry standard enhancements (skeletons, animations, progress stepper) |
| 5.0 | 2026-01-09 | BFF pattern alignment + enhanced UX component specs |
