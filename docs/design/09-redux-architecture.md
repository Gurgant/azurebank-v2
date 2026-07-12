# Redux Architecture
## AzureBank - Bank Account Management System

**Document Version**: 3.0
**Created**: 2025-12-16
**Updated**: 2026-01-09
**Author**: Frontend Lead
**Status**: UPDATED - Phase 6 Complete (BFF Architecture)

---

> ⚠️ **IMPORTANT UPDATE (Phase 5+)**
>
> This document has been updated to reflect the **BFF (Backend-for-Frontend) architecture**
> implemented in Phase 5. Key changes:
> - **NO JWT tokens stored on client** - Authentication is session-based
> - **Cookie-based authentication** - HTTP-only cookies managed by BFF
> - **No Authorization headers** - All requests use `credentials: 'include'`
>
> For the complete, authoritative frontend implementation, see:
> **[10-implementation-guide-frontend.md](10-implementation-guide-frontend.md)** (Section 6)

---

## Changelog

| Version | Date | Changes |
|---------|------|---------|
| 3.0 | 2026-01-09 | Major update: BFF architecture, no client-side tokens, UI slice, Transfer Wizard |
| 2.0 | 2025-12-17 | Added RTK Query patterns, cache invalidation |
| 1.0 | 2025-12-16 | Initial document |

---

## 1. State Architecture Overview

### 1.1 State Management Strategy (BFF-Aware)

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    STATE MANAGEMENT (BFF Architecture)                   │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  REDUX STORE (Global State)                                             │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │                                                                    │ │
│  │  AUTH SLICE (BFF Session-Based - NO TOKENS)                       │ │
│  │  ├── user: User | null                                            │ │
│  │  ├── session: SessionInfo | null                                  │ │
│  │  ├── isAuthenticated: boolean                                     │ │
│  │  ├── isInitialized: boolean      ◀── App startup check complete   │ │
│  │  └── isLoading: boolean          ◀── Auth operation in progress   │ │
│  │                                                                    │ │
│  │  UI SLICE (Global UI State)                                       │ │
│  │  ├── toasts: Toast[]             ◀── Notification queue           │ │
│  │  ├── activeDialogs: DialogId[]   ◀── Open dialogs                 │ │
│  │  ├── isMobileNavOpen: boolean    ◀── Mobile navigation            │ │
│  │  └── sessionTimeoutWarning       ◀── Timeout warning state        │ │
│  │                                                                    │ │
│  │  TRANSFER WIZARD SLICE (Multi-step Form State)                    │ │
│  │  ├── currentStep: TransferWizardStep                              │ │
│  │  ├── transferType: 'internal' | 'external' | null                 │ │
│  │  ├── sourceAccountId, recipient, amount, description              │ │
│  │  └── completedTransfer: TransferResponse | null                   │ │
│  │                                                                    │ │
│  │  RTK QUERY API (Single Base API with injectEndpoints)             │ │
│  │  └── baseApi.reducer (manages all API slices)                     │ │
│  │      ├── Auth endpoints (login, register, logout, getMe)          │ │
│  │      ├── Account endpoints (getAccounts, createAccount, etc.)     │ │
│  │      ├── Transaction endpoints (deposit, withdraw, history)       │ │
│  │      ├── Transfer endpoints (internal, external)                  │ │
│  │      └── Recipient endpoints (search, validate)                   │ │
│  │                                                                    │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                          │
│  LOCAL STATE (React Hooks - Component Level)                            │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │  useState for:                                                     │ │
│  │  ├── Form inputs (managed by react-hook-form)                     │ │
│  │  ├── Dropdown open/closed                                         │ │
│  │  ├── Component-specific toggles                                   │ │
│  │  └── Ephemeral UI state                                           │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### 1.2 BFF Authentication Flow

```
┌──────────────────────────────────────────────────────────────────────────┐
│                     BFF AUTHENTICATION FLOW                               │
├──────────────────────────────────────────────────────────────────────────┤
│                                                                           │
│  Browser                BFF Gateway              Backend API              │
│    │                       │                         │                    │
│    │ POST /bff/auth/login  │                         │                    │
│    │ {email, password}     │                         │                    │
│    │──────────────────────▶│                         │                    │
│    │                       │ POST /api/auth/login    │                    │
│    │                       │────────────────────────▶│                    │
│    │                       │                         │                    │
│    │                       │◀──── JWT Token ─────────│                    │
│    │                       │                         │                    │
│    │                       │ Store JWT in session    │                    │
│    │                       │ (server-side)           │                    │
│    │                       │                         │                    │
│    │◀── Set-Cookie ────────│                         │                    │
│    │    (HTTP-only)        │                         │                    │
│    │                       │                         │                    │
│    │ ✅ NO TOKEN returned  │                         │                    │
│    │    to browser!        │                         │                    │
│    │                       │                         │                    │
│  ──────────────────────────────────────────────────────────────────────  │
│                                                                           │
│  KEY POINTS:                                                              │
│  • JWT is NEVER sent to browser                                          │
│  • Session cookie is HTTP-only (JavaScript cannot access)                │
│  • All subsequent requests automatically include cookie                   │
│  • BFF injects JWT into backend requests                                 │
│                                                                           │
└──────────────────────────────────────────────────────────────────────────┘
```

### 1.3 When to Use What

| State Type | Use For | Example |
|------------|---------|---------|
| **Redux (authSlice)** | Auth state synchronized from server | User profile, session info |
| **Redux (uiSlice)** | Global UI state | Toasts, dialogs, mobile nav |
| **Redux (transferWizardSlice)** | Multi-step wizard state | Transfer wizard form data |
| **RTK Query** | Server data with caching | Accounts, transactions, balances |
| **useState** | Ephemeral local UI state | Form focus, dropdown open |
| **react-hook-form** | Form state management | All form inputs, validation |

---

## 2. Store Configuration

### 2.1 store.ts (BFF-Aware)

```typescript
// src/app/store.ts
import { configureStore } from '@reduxjs/toolkit';
import { setupListeners } from '@reduxjs/toolkit/query';

// Feature slices
import authReducer from '@features/auth/authSlice';
import uiReducer from '@features/ui/uiSlice';
import transferWizardReducer from '@features/transferWizard/transferWizardSlice';

// Base API (all endpoints injected into this)
import { baseApi } from '@features/api/baseApi';

/**
 * Redux store configuration
 *
 * Architecture:
 * - BFF-aware (no token storage)
 * - Single base API with injected endpoints
 * - Feature-based slices for local state
 */
export const store = configureStore({
  reducer: {
    // Feature slices
    auth: authReducer,
    ui: uiReducer,
    transferWizard: transferWizardReducer,

    // RTK Query API (single reducer for all endpoints)
    [baseApi.reducerPath]: baseApi.reducer,
  },
  middleware: (getDefaultMiddleware) =>
    getDefaultMiddleware({
      // Allow non-serializable values for certain actions if needed
      serializableCheck: {
        ignoredActions: ['ui/addToast'],
      },
    }).concat(baseApi.middleware),

  // Enable Redux DevTools in development only
  devTools: import.meta.env.DEV,
});

// Enable refetchOnFocus/refetchOnReconnect behaviors
setupListeners(store.dispatch);

// Type exports
export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
export type AppStore = typeof store;
```

### 2.2 hooks.ts

```typescript
// src/app/hooks.ts
import { useDispatch, useSelector, useStore } from 'react-redux';
import type { RootState, AppDispatch, AppStore } from './store';

/**
 * Typed Redux hooks
 * Always use these instead of plain useDispatch/useSelector
 */
export const useAppDispatch = useDispatch.withTypes<AppDispatch>();
export const useAppSelector = useSelector.withTypes<RootState>();
export const useAppStore = useStore.withTypes<AppStore>();
```

---

## 3. Auth Slice (BFF-Aware - NO TOKENS)

### 3.1 Types

```typescript
// src/types/auth.types.ts

/**
 * User information returned from BFF
 * Note: azureTag is the unique public identifier
 */
export interface User {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  azureTag: string;        // Public identifier (e.g., "johndoe")
  createdAt: string;
}

/**
 * Session information from BFF
 * Note: NO tokens - session is managed server-side
 */
export interface SessionInfo {
  authLevel: 1 | 2;        // 1 = logged in, 2 = PIN verified
  createdAt: string;
  lastActivity: string;
}

/**
 * Auth slice state
 * BFF-aware: NO token storage
 */
export interface AuthState {
  user: User | null;
  session: SessionInfo | null;
  isAuthenticated: boolean;
  isInitialized: boolean;  // True after initial session check
  isLoading: boolean;      // True during auth operations
}

/**
 * Login request payload
 */
export interface LoginRequest {
  email: string;
  password: string;
}

/**
 * Register request payload
 */
export interface RegisterRequest {
  email: string;
  password: string;
  confirmPassword: string;
  firstName: string;
  lastName: string;
  azureTag: string;
}

/**
 * Login/Register response from BFF
 * Note: NO token returned - cookie set automatically
 */
export interface AuthResponse {
  user: User;
  // NO token field - BFF manages JWT server-side
}

/**
 * GetMe response from BFF
 */
export interface GetMeResponse {
  user: User;
  session: SessionInfo;
}
```

### 3.2 Auth Slice Implementation (BFF-Aware)

```typescript
// src/features/auth/authSlice.ts
import { createSlice } from '@reduxjs/toolkit';
import type { PayloadAction } from '@reduxjs/toolkit';
import type { RootState } from '@/app/store';
import type { AuthState, User, SessionInfo } from '@/types/auth.types';

/**
 * Initial auth state
 * - isInitialized: false until we check session on app load
 * - isAuthenticated: false until BFF confirms session is valid
 */
const initialState: AuthState = {
  user: null,
  session: null,
  isAuthenticated: false,
  isInitialized: false,
  isLoading: false,
};

/**
 * Auth slice for BFF-based authentication
 *
 * IMPORTANT: No token storage!
 * - Authentication state is derived from successful API calls
 * - Session validity is checked via /bff/auth/me on app startup
 * - Cookies are managed automatically by browser
 */
const authSlice = createSlice({
  name: 'auth',
  initialState,
  reducers: {
    /**
     * Set authenticated state after successful login or session check
     * Called when BFF confirms user is authenticated
     */
    setAuthenticated: (
      state,
      action: PayloadAction<{ user: User; session?: SessionInfo }>
    ) => {
      state.user = action.payload.user;
      state.session = action.payload.session ?? null;
      state.isAuthenticated = true;
      state.isInitialized = true;
      state.isLoading = false;
    },

    /**
     * Clear auth state on logout or session expiry
     * Called when user logs out or BFF returns 401
     */
    clearAuth: (state) => {
      state.user = null;
      state.session = null;
      state.isAuthenticated = false;
      state.isLoading = false;
      // Keep isInitialized true - we know auth state
    },

    /**
     * Mark auth as initialized (after checking session)
     * Called when session check fails (not logged in)
     */
    setInitialized: (state) => {
      state.isInitialized = true;
      state.isLoading = false;
    },

    /**
     * Set loading state during auth operations
     */
    setLoading: (state, action: PayloadAction<boolean>) => {
      state.isLoading = action.payload;
    },

    /**
     * Update session info (e.g., after PIN verification)
     */
    updateSession: (state, action: PayloadAction<Partial<SessionInfo>>) => {
      if (state.session) {
        state.session = { ...state.session, ...action.payload };
      }
    },

    /**
     * Update user profile
     */
    updateUser: (state, action: PayloadAction<Partial<User>>) => {
      if (state.user) {
        state.user = { ...state.user, ...action.payload };
      }
    },
  },
});

// Export actions
export const {
  setAuthenticated,
  clearAuth,
  setInitialized,
  setLoading,
  updateSession,
  updateUser,
} = authSlice.actions;

// ============================================
// SELECTORS
// ============================================

/**
 * Select current user
 */
export const selectUser = (state: RootState) => state.auth.user;

/**
 * Select session info
 */
export const selectSession = (state: RootState) => state.auth.session;

/**
 * Check if user is authenticated
 */
export const selectIsAuthenticated = (state: RootState) =>
  state.auth.isAuthenticated;

/**
 * Check if auth has been initialized (session checked)
 */
export const selectIsAuthInitialized = (state: RootState) =>
  state.auth.isInitialized;

/**
 * Check if auth operation is in progress
 */
export const selectIsAuthLoading = (state: RootState) => state.auth.isLoading;

/**
 * Get user's display name
 */
export const selectUserDisplayName = (state: RootState) => {
  const user = state.auth.user;
  if (!user) return null;
  return `${user.firstName} ${user.lastName}`;
};

/**
 * Get user's AzureTag (with @ prefix for display)
 */
export const selectUserAzureTag = (state: RootState) => {
  const user = state.auth.user;
  if (!user) return null;
  return `@${user.azureTag}`;
};

/**
 * Get user's initials for avatar
 */
export const selectUserInitials = (state: RootState) => {
  const user = state.auth.user;
  if (!user) return null;
  return `${user.firstName[0]}${user.lastName[0]}`.toUpperCase();
};

/**
 * Check if user has elevated auth (PIN verified)
 */
export const selectHasElevatedAuth = (state: RootState) =>
  state.auth.session?.authLevel === 2;

// Export reducer
export default authSlice.reducer;
```

---

## 4. RTK Query Base Configuration (BFF-Aware)

### 4.1 Base API with Cookie Authentication

```typescript
// src/features/api/baseApi.ts
import { createApi, fetchBaseQuery } from '@reduxjs/toolkit/query/react';
import type {
  BaseQueryFn,
  FetchArgs,
  FetchBaseQueryError,
} from '@reduxjs/toolkit/query';
import { clearAuth } from '@features/auth/authSlice';

/**
 * BFF API base URL
 * All requests go through BFF, which handles JWT injection
 */
const BFF_BASE_URL = '/';  // BFF is same-origin

/**
 * Custom base query with:
 * - Cookie credentials (session-based auth)
 * - Correlation ID headers
 * - Automatic retry with backoff
 * - 401 session expiry handling
 */
const baseQueryWithAuth = fetchBaseQuery({
  baseUrl: BFF_BASE_URL,
  // CRITICAL: Include cookies in all requests
  credentials: 'include',
  prepareHeaders: (headers) => {
    // Add correlation ID for request tracking
    headers.set('X-Correlation-ID', crypto.randomUUID());
    return headers;
  },
});

/**
 * Enhanced base query with retry logic and auth handling
 */
const baseQueryWithReauth: BaseQueryFn<
  string | FetchArgs,
  unknown,
  FetchBaseQueryError
> = async (args, api, extraOptions) => {
  let result = await baseQueryWithAuth(args, api, extraOptions);

  // Handle 401 - session expired
  if (result.error && result.error.status === 401) {
    // Clear auth state - user needs to log in again
    api.dispatch(clearAuth());
  }

  return result;
};

/**
 * Base API configuration
 *
 * All feature APIs inject endpoints into this base API.
 * This provides:
 * - Single middleware
 * - Unified cache
 * - Shared tag types
 */
export const baseApi = createApi({
  reducerPath: 'api',
  baseQuery: baseQueryWithReauth,

  // Tag types for cache invalidation
  tagTypes: [
    'Account',
    'Balance',
    'Transaction',
    'Recipient',
    'User',
    'Session',
  ],

  // Empty endpoints - features inject their own
  endpoints: () => ({}),

  // Cache configuration
  keepUnusedDataFor: 60,        // 60 seconds
  refetchOnMountOrArgChange: 30, // Refetch if older than 30s
  refetchOnFocus: true,          // Refetch when window focused
  refetchOnReconnect: true,      // Refetch when network reconnects
});
```

### 4.2 API Exports

```typescript
// src/features/api/index.ts
export { baseApi } from './baseApi';

// Re-export all API hooks from feature modules
export * from '@features/auth/authApi';
export * from '@features/accounts/accountsApi';
export * from '@features/transactions/transactionsApi';
export * from '@features/transfers/transfersApi';
export * from '@features/recipients/recipientsApi';
```

---

## 5. Auth API (RTK Query - BFF Endpoints)

```typescript
// src/features/auth/authApi.ts
import { baseApi } from '@features/api/baseApi';
import type {
  LoginRequest,
  RegisterRequest,
  AuthResponse,
  GetMeResponse,
} from '@/types/auth.types';
import type { ApiResponse } from '@/types/api.types';

/**
 * Auth API endpoints
 *
 * These hit the BFF, NOT the backend directly.
 * BFF manages JWT server-side; browser only sees cookies.
 */
export const authApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    /**
     * Login user
     * POST /bff/auth/login
     *
     * Sets HTTP-only session cookie on success.
     * NO token returned to browser.
     */
    login: builder.mutation<ApiResponse<AuthResponse>, LoginRequest>({
      query: (credentials) => ({
        url: '/bff/auth/login',
        method: 'POST',
        body: credentials,
      }),
      // Invalidate all cached data on login
      invalidatesTags: ['User', 'Account', 'Transaction', 'Session'],
    }),

    /**
     * Register new user
     * POST /api/auth/register (through BFF proxy)
     */
    register: builder.mutation<ApiResponse<AuthResponse>, RegisterRequest>({
      query: (userData) => ({
        url: '/api/auth/register',
        method: 'POST',
        body: userData,
      }),
    }),

    /**
     * Logout user
     * POST /bff/auth/logout
     *
     * Clears server-side session and cookie.
     */
    logout: builder.mutation<void, void>({
      query: () => ({
        url: '/bff/auth/logout',
        method: 'POST',
      }),
      // Clear all cached data on logout
      invalidatesTags: ['User', 'Account', 'Transaction', 'Balance', 'Session'],
    }),

    /**
     * Get current user
     * GET /bff/auth/me
     *
     * Used to check session validity on app startup.
     */
    getMe: builder.query<ApiResponse<GetMeResponse>, void>({
      query: () => '/bff/auth/me',
      providesTags: ['User', 'Session'],
    }),

    /**
     * Get session status
     * GET /bff/auth/session-status
     *
     * Returns timeout info for session warning UI.
     */
    sessionStatus: builder.query<
      ApiResponse<{
        authLevel: number;
        inactivityExpiresIn: number;
        absoluteExpiresIn: number;
      }>,
      void
    >({
      query: () => '/bff/auth/session-status',
      providesTags: ['Session'],
    }),

    /**
     * Verify PIN for elevated auth
     * POST /bff/auth/verify-pin
     */
    verifyPin: builder.mutation<
      ApiResponse<{ verified: boolean; authLevel: number; expiresAt: string }>,
      { pin: string }
    >({
      query: (body) => ({
        url: '/bff/auth/verify-pin',
        method: 'POST',
        body,
      }),
      invalidatesTags: ['Session'],
    }),
  }),
  overrideExisting: false,
});

// Export hooks
export const {
  useLoginMutation,
  useRegisterMutation,
  useLogoutMutation,
  useGetMeQuery,
  useLazyGetMeQuery,
  useSessionStatusQuery,
  useVerifyPinMutation,
} = authApi;
```

---

## 6. Account API (RTK Query)

```typescript
// src/features/accounts/accountsApi.ts
import { baseApi } from '@features/api/baseApi';
import type {
  Account,
  BalanceResponse,
  CreateAccountRequest,
} from '@/types/account.types';
import type { ApiResponse } from '@/types/api.types';

/**
 * Accounts API endpoints
 */
export const accountsApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    /**
     * Get all user accounts
     * GET /api/accounts
     */
    getAccounts: builder.query<Account[], void>({
      query: () => '/api/accounts',
      transformResponse: (response: ApiResponse<Account[]>) => response.data,
      providesTags: (result) =>
        result
          ? [
              ...result.map(({ id }) => ({ type: 'Account' as const, id })),
              { type: 'Account', id: 'LIST' },
            ]
          : [{ type: 'Account', id: 'LIST' }],
    }),

    /**
     * Get single account by ID
     * GET /api/accounts/:id
     */
    getAccount: builder.query<Account, string>({
      query: (id) => `/api/accounts/${id}`,
      transformResponse: (response: ApiResponse<Account>) => response.data,
      providesTags: (result, error, id) => [{ type: 'Account', id }],
    }),

    /**
     * Create new account
     * POST /api/accounts
     */
    createAccount: builder.mutation<Account, CreateAccountRequest>({
      query: (data) => ({
        url: '/api/accounts',
        method: 'POST',
        body: data,
      }),
      transformResponse: (response: ApiResponse<Account>) => response.data,
      invalidatesTags: [{ type: 'Account', id: 'LIST' }],
    }),

    /**
     * Get account balance
     * GET /api/accounts/:id/balance
     *
     * Optional: ?at=ISO_DATE for historical balance
     */
    getBalance: builder.query<BalanceResponse, string>({
      query: (accountId) => `/api/accounts/${accountId}/balance`,
      transformResponse: (response: ApiResponse<BalanceResponse>) =>
        response.data,
      providesTags: (result, error, accountId) => [
        { type: 'Balance', id: accountId },
      ],
    }),

    /**
     * Get historical balance at specific time
     * GET /api/accounts/:id/balance?at=ISO_DATE
     */
    getBalanceAtTime: builder.query<
      BalanceResponse,
      { accountId: string; at: string }
    >({
      query: ({ accountId, at }) => ({
        url: `/api/accounts/${accountId}/balance`,
        params: { at },
      }),
      transformResponse: (response: ApiResponse<BalanceResponse>) =>
        response.data,
      // Don't cache historical queries as tags
    }),

    /**
     * Set account as primary
     * PATCH /api/accounts/:id/primary
     */
    setAsPrimary: builder.mutation<Account, string>({
      query: (accountId) => ({
        url: `/api/accounts/${accountId}/primary`,
        method: 'PATCH',
      }),
      transformResponse: (response: ApiResponse<Account>) => response.data,
      invalidatesTags: [{ type: 'Account', id: 'LIST' }],
    }),
  }),
  overrideExisting: false,
});

// Export hooks
export const {
  useGetAccountsQuery,
  useGetAccountQuery,
  useCreateAccountMutation,
  useGetBalanceQuery,
  useGetBalanceAtTimeQuery,
  useSetAsPrimaryMutation,
} = accountsApi;
```

---

## 7. Transaction API (RTK Query)

```typescript
// src/features/transactions/transactionsApi.ts
import { baseApi } from '@features/api/baseApi';
import type {
  Transaction,
  TransactionHistoryParams,
  TransactionHistoryResponse,
  DepositRequest,
  DepositResponse,
  WithdrawRequest,
  WithdrawResponse,
} from '@/types/transaction.types';
import type { ApiResponse } from '@/types/api.types';

/**
 * Transactions API endpoints
 */
export const transactionsApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    /**
     * Get transaction history
     * GET /api/transactions
     */
    getTransactions: builder.query<
      TransactionHistoryResponse,
      TransactionHistoryParams
    >({
      query: (params) => ({
        url: '/api/transactions',
        params: {
          accountId: params.accountId,
          from: params.from,
          to: params.to,
          type: params.type,
          page: params.page ?? 1,
          pageSize: params.pageSize ?? 20,
        },
      }),
      transformResponse: (response: ApiResponse<TransactionHistoryResponse>) =>
        response.data,
      providesTags: (result, error, params) => [
        { type: 'Transaction', id: `LIST-${params.accountId}` },
        { type: 'Transaction', id: 'LIST-all' },
      ],
    }),

    /**
     * Deposit money
     * POST /api/transactions/deposit
     */
    deposit: builder.mutation<DepositResponse, DepositRequest>({
      query: (data) => ({
        url: '/api/transactions/deposit',
        method: 'POST',
        body: data,
      }),
      transformResponse: (response: ApiResponse<DepositResponse>) =>
        response.data,
      invalidatesTags: (result, error, { accountId }) => [
        { type: 'Transaction', id: `LIST-${accountId}` },
        { type: 'Transaction', id: 'LIST-all' },
        { type: 'Balance', id: accountId },
        { type: 'Account', id: accountId },
        { type: 'Account', id: 'LIST' },
      ],
    }),

    /**
     * Withdraw money
     * POST /api/transactions/withdraw
     */
    withdraw: builder.mutation<WithdrawResponse, WithdrawRequest>({
      query: (data) => ({
        url: '/api/transactions/withdraw',
        method: 'POST',
        body: data,
      }),
      transformResponse: (response: ApiResponse<WithdrawResponse>) =>
        response.data,
      invalidatesTags: (result, error, { accountId }) => [
        { type: 'Transaction', id: `LIST-${accountId}` },
        { type: 'Transaction', id: 'LIST-all' },
        { type: 'Balance', id: accountId },
        { type: 'Account', id: accountId },
        { type: 'Account', id: 'LIST' },
      ],
    }),
  }),
  overrideExisting: false,
});

// Export hooks
export const {
  useGetTransactionsQuery,
  useDepositMutation,
  useWithdrawMutation,
} = transactionsApi;
```

---

## 8. Transfer API (RTK Query)

```typescript
// src/features/transfers/transfersApi.ts
import { baseApi } from '@features/api/baseApi';
import type {
  TransferRequest,
  TransferResponse,
  InternalTransferRequest,
  InternalTransferResponse,
} from '@/types/transfer.types';
import type { ApiResponse } from '@/types/api.types';

/**
 * Transfers API endpoints
 */
export const transfersApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    /**
     * External transfer (to another user via AzureTag)
     * POST /api/transfers
     */
    transfer: builder.mutation<TransferResponse, TransferRequest>({
      query: (data) => ({
        url: '/api/transfers',
        method: 'POST',
        body: data,
      }),
      transformResponse: (response: ApiResponse<TransferResponse>) =>
        response.data,
      invalidatesTags: (result, error, { fromAccountId }) => [
        { type: 'Balance', id: fromAccountId },
        { type: 'Transaction', id: `LIST-${fromAccountId}` },
        { type: 'Transaction', id: 'LIST-all' },
        { type: 'Account', id: fromAccountId },
        { type: 'Account', id: 'LIST' },
      ],
    }),

    /**
     * Internal transfer (between own accounts)
     * POST /api/transfers/internal
     */
    internalTransfer: builder.mutation<
      InternalTransferResponse,
      InternalTransferRequest
    >({
      query: (data) => ({
        url: '/api/transfers/internal',
        method: 'POST',
        body: data,
      }),
      transformResponse: (response: ApiResponse<InternalTransferResponse>) =>
        response.data,
      invalidatesTags: (result, error, { fromAccountId, toAccountId }) => [
        { type: 'Balance', id: fromAccountId },
        { type: 'Balance', id: toAccountId },
        { type: 'Transaction', id: `LIST-${fromAccountId}` },
        { type: 'Transaction', id: `LIST-${toAccountId}` },
        { type: 'Transaction', id: 'LIST-all' },
        { type: 'Account', id: fromAccountId },
        { type: 'Account', id: toAccountId },
      ],
    }),
  }),
  overrideExisting: false,
});

// Export hooks
export const { useTransferMutation, useInternalTransferMutation } =
  transfersApi;
```

---

## 9. UI Slice

```typescript
// src/features/ui/uiSlice.ts
import { createSlice } from '@reduxjs/toolkit';
import type { PayloadAction } from '@reduxjs/toolkit';
import type { RootState } from '@/app/store';

/**
 * Toast notification
 */
export interface Toast {
  id: string;
  intent: 'success' | 'error' | 'warning' | 'info';
  title: string;
  message?: string;
  duration?: number;
}

/**
 * Dialog identifiers
 */
export type DialogId =
  | 'create-account'
  | 'deposit'
  | 'withdraw'
  | 'transfer'
  | 'confirm-action'
  | 'session-timeout';

/**
 * UI slice state
 */
interface UIState {
  toasts: Toast[];
  activeDialogs: DialogId[];
  dialogData: Record<string, unknown>;
  isSidebarCollapsed: boolean;
  isMobileNavOpen: boolean;
  isGlobalLoading: boolean;
  globalLoadingMessage: string | null;
  sessionTimeoutWarningVisible: boolean;
  sessionExpiresAt: string | null;
}

const initialState: UIState = {
  toasts: [],
  activeDialogs: [],
  dialogData: {},
  isSidebarCollapsed: false,
  isMobileNavOpen: false,
  isGlobalLoading: false,
  globalLoadingMessage: null,
  sessionTimeoutWarningVisible: false,
  sessionExpiresAt: null,
};

const uiSlice = createSlice({
  name: 'ui',
  initialState,
  reducers: {
    // Toast management
    addToast: (state, action: PayloadAction<Omit<Toast, 'id'>>) => {
      const id = crypto.randomUUID();
      state.toasts.push({ ...action.payload, id });
    },
    removeToast: (state, action: PayloadAction<string>) => {
      state.toasts = state.toasts.filter((t) => t.id !== action.payload);
    },
    clearAllToasts: (state) => {
      state.toasts = [];
    },

    // Dialog management
    openDialog: (
      state,
      action: PayloadAction<{ dialogId: DialogId; data?: unknown }>
    ) => {
      if (!state.activeDialogs.includes(action.payload.dialogId)) {
        state.activeDialogs.push(action.payload.dialogId);
      }
      if (action.payload.data !== undefined) {
        state.dialogData[action.payload.dialogId] = action.payload.data;
      }
    },
    closeDialog: (state, action: PayloadAction<DialogId>) => {
      state.activeDialogs = state.activeDialogs.filter(
        (id) => id !== action.payload
      );
      delete state.dialogData[action.payload];
    },
    closeAllDialogs: (state) => {
      state.activeDialogs = [];
      state.dialogData = {};
    },

    // Navigation
    toggleMobileNav: (state) => {
      state.isMobileNavOpen = !state.isMobileNavOpen;
    },
    setMobileNavOpen: (state, action: PayloadAction<boolean>) => {
      state.isMobileNavOpen = action.payload;
    },
    toggleSidebar: (state) => {
      state.isSidebarCollapsed = !state.isSidebarCollapsed;
    },

    // Global loading
    showGlobalLoading: (state, action: PayloadAction<string | undefined>) => {
      state.isGlobalLoading = true;
      state.globalLoadingMessage = action.payload ?? null;
    },
    hideGlobalLoading: (state) => {
      state.isGlobalLoading = false;
      state.globalLoadingMessage = null;
    },

    // Session timeout
    showSessionWarning: (state, action: PayloadAction<string>) => {
      state.sessionTimeoutWarningVisible = true;
      state.sessionExpiresAt = action.payload;
    },
    hideSessionWarning: (state) => {
      state.sessionTimeoutWarningVisible = false;
      state.sessionExpiresAt = null;
    },
  },
});

// Export actions
export const {
  addToast,
  removeToast,
  clearAllToasts,
  openDialog,
  closeDialog,
  closeAllDialogs,
  toggleMobileNav,
  setMobileNavOpen,
  toggleSidebar,
  showGlobalLoading,
  hideGlobalLoading,
  showSessionWarning,
  hideSessionWarning,
} = uiSlice.actions;

// Selectors
export const selectToasts = (state: RootState) => state.ui.toasts;
export const selectIsDialogOpen = (dialogId: DialogId) => (state: RootState) =>
  state.ui.activeDialogs.includes(dialogId);
export const selectDialogData =
  <T = unknown>(dialogId: DialogId) =>
  (state: RootState): T | undefined =>
    state.ui.dialogData[dialogId] as T | undefined;
export const selectIsMobileNavOpen = (state: RootState) =>
  state.ui.isMobileNavOpen;
export const selectIsSidebarCollapsed = (state: RootState) =>
  state.ui.isSidebarCollapsed;
export const selectIsGlobalLoading = (state: RootState) =>
  state.ui.isGlobalLoading;
export const selectSessionWarningVisible = (state: RootState) =>
  state.ui.sessionTimeoutWarningVisible;

// Export reducer
export default uiSlice.reducer;
```

---

## 10. Transfer Wizard Slice

```typescript
// src/features/transferWizard/transferWizardSlice.ts
import { createSlice } from '@reduxjs/toolkit';
import type { PayloadAction } from '@reduxjs/toolkit';
import type { RootState } from '@/app/store';
import type { Recipient, TransferResponse } from '@/types/transfer.types';

/**
 * Transfer wizard steps
 */
export type TransferWizardStep =
  | 'source'
  | 'recipient'
  | 'amount'
  | 'confirm'
  | 'success';

/**
 * Transfer wizard state
 */
interface TransferWizardState {
  currentStep: TransferWizardStep;
  isOpen: boolean;
  transferType: 'internal' | 'external' | null;

  // Step data
  sourceAccountId: string | null;
  recipient: Recipient | null;
  destinationAccountId: string | null;
  amount: number | null;
  description: string;

  // Result
  completedTransfer: TransferResponse | null;

  // UI state
  isSubmitting: boolean;
  error: string | null;
}

const initialState: TransferWizardState = {
  currentStep: 'source',
  isOpen: false,
  transferType: null,
  sourceAccountId: null,
  recipient: null,
  destinationAccountId: null,
  amount: null,
  description: '',
  completedTransfer: null,
  isSubmitting: false,
  error: null,
};

const STEP_ORDER: TransferWizardStep[] = [
  'source',
  'recipient',
  'amount',
  'confirm',
  'success',
];

const transferWizardSlice = createSlice({
  name: 'transferWizard',
  initialState,
  reducers: {
    // Lifecycle
    openExternalTransfer: (state, action: PayloadAction<string | undefined>) => {
      state.isOpen = true;
      state.transferType = 'external';
      state.currentStep = 'source';
      state.sourceAccountId = action.payload ?? null;
      state.recipient = null;
      state.destinationAccountId = null;
      state.amount = null;
      state.description = '';
      state.completedTransfer = null;
      state.isSubmitting = false;
      state.error = null;
    },
    openInternalTransfer: (state, action: PayloadAction<string | undefined>) => {
      state.isOpen = true;
      state.transferType = 'internal';
      state.currentStep = 'source';
      state.sourceAccountId = action.payload ?? null;
      state.recipient = null;
      state.destinationAccountId = null;
      state.amount = null;
      state.description = '';
      state.completedTransfer = null;
      state.isSubmitting = false;
      state.error = null;
    },
    closeWizard: () => initialState,

    // Navigation
    goToStep: (state, action: PayloadAction<TransferWizardStep>) => {
      state.currentStep = action.payload;
      state.error = null;
    },
    nextStep: (state) => {
      const currentIndex = STEP_ORDER.indexOf(state.currentStep);
      if (currentIndex < STEP_ORDER.length - 1) {
        state.currentStep = STEP_ORDER[currentIndex + 1]!;
        state.error = null;
      }
    },
    previousStep: (state) => {
      const currentIndex = STEP_ORDER.indexOf(state.currentStep);
      if (currentIndex > 0) {
        state.currentStep = STEP_ORDER[currentIndex - 1]!;
        state.error = null;
      }
    },

    // Data setters
    setSourceAccount: (state, action: PayloadAction<string>) => {
      state.sourceAccountId = action.payload;
    },
    setRecipient: (state, action: PayloadAction<Recipient>) => {
      state.recipient = action.payload;
      state.destinationAccountId = null;
    },
    setDestinationAccount: (state, action: PayloadAction<string>) => {
      state.destinationAccountId = action.payload;
      state.recipient = null;
    },
    setAmount: (state, action: PayloadAction<number>) => {
      state.amount = action.payload;
    },
    setDescription: (state, action: PayloadAction<string>) => {
      state.description = action.payload;
    },

    // Submission
    startSubmission: (state) => {
      state.isSubmitting = true;
      state.error = null;
    },
    transferSuccess: (state, action: PayloadAction<TransferResponse>) => {
      state.isSubmitting = false;
      state.completedTransfer = action.payload;
      state.currentStep = 'success';
      state.error = null;
    },
    transferFailure: (state, action: PayloadAction<string>) => {
      state.isSubmitting = false;
      state.error = action.payload;
    },
  },
});

// Export actions
export const {
  openExternalTransfer,
  openInternalTransfer,
  closeWizard,
  goToStep,
  nextStep,
  previousStep,
  setSourceAccount,
  setRecipient,
  setDestinationAccount,
  setAmount,
  setDescription,
  startSubmission,
  transferSuccess,
  transferFailure,
} = transferWizardSlice.actions;

// Selectors
export const selectWizardIsOpen = (state: RootState) =>
  state.transferWizard.isOpen;
export const selectCurrentStep = (state: RootState) =>
  state.transferWizard.currentStep;
export const selectTransferType = (state: RootState) =>
  state.transferWizard.transferType;
export const selectSourceAccountId = (state: RootState) =>
  state.transferWizard.sourceAccountId;
export const selectRecipient = (state: RootState) =>
  state.transferWizard.recipient;
export const selectDestinationAccountId = (state: RootState) =>
  state.transferWizard.destinationAccountId;
export const selectAmount = (state: RootState) => state.transferWizard.amount;
export const selectDescription = (state: RootState) =>
  state.transferWizard.description;
export const selectCompletedTransfer = (state: RootState) =>
  state.transferWizard.completedTransfer;
export const selectIsSubmitting = (state: RootState) =>
  state.transferWizard.isSubmitting;
export const selectWizardError = (state: RootState) =>
  state.transferWizard.error;

// Export reducer
export default transferWizardSlice.reducer;
```

---

## 11. TypeScript Types (Updated)

### 11.1 Account Types

```typescript
// src/types/account.types.ts
export type AccountType = 'checking' | 'savings' | 'investment';

export interface Account {
  id: string;
  userId: string;
  accountNumber: string;
  name: string;
  type: AccountType;
  balance: number;
  isPrimary: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface BalanceResponse {
  accountId: string;
  balance: number;
  asOf: string;
}

export interface CreateAccountRequest {
  name: string;
  type: AccountType;
}

export interface AccountSummary {
  totalBalance: number;
  accountCount: number;
  primaryAccount: Account | null;
}
```

### 11.2 Transaction Types

```typescript
// src/types/transaction.types.ts
export type TransactionType =
  | 'deposit'
  | 'withdrawal'
  | 'transfer_in'
  | 'transfer_out';

export type TransactionStatus = 'pending' | 'completed' | 'failed' | 'reversed';

export interface Transaction {
  id: string;
  transactionNumber: string;
  accountId: string;
  type: TransactionType;
  amount: number;
  balanceBefore: number;
  balanceAfter: number;
  description: string | null;
  recipientAzureTag: string | null;
  senderAzureTag: string | null;
  status: TransactionStatus;
  createdAt: string;
}

export interface TransactionHistoryParams {
  accountId?: string;
  from?: string;
  to?: string;
  type?: TransactionType;
  page?: number;
  pageSize?: number;
}

export interface TransactionHistoryResponse {
  transactions: Transaction[];
  pagination: {
    page: number;
    pageSize: number;
    totalItems: number;
    totalPages: number;
  };
}

export interface DepositRequest {
  accountId: string;
  amount: number;
  description?: string;
}

export interface DepositResponse {
  transaction: Transaction;
  newBalance: number;
}

export interface WithdrawRequest {
  accountId: string;
  amount: number;
  description?: string;
}

export interface WithdrawResponse {
  transaction: Transaction;
  newBalance: number;
}
```

### 11.3 Transfer Types

```typescript
// src/types/transfer.types.ts
export interface Recipient {
  azureTag: string;
  displayName: string;
  maskedName: string;  // "John S." for privacy
}

export interface TransferRequest {
  fromAccountId: string;
  recipientAzureTag: string;
  amount: number;
  description?: string;
}

export interface TransferResponse {
  transaction: Transaction;
  newBalance: number;
  recipientAzureTag: string;
}

export interface InternalTransferRequest {
  fromAccountId: string;
  toAccountId: string;
  amount: number;
  description?: string;
}

export interface InternalTransferResponse {
  fromTransaction: Transaction;
  toTransaction: Transaction;
  fromNewBalance: number;
  toNewBalance: number;
}
```

### 11.4 API Types

```typescript
// src/types/api.types.ts
export interface ApiResponse<T> {
  data: T;
  message?: string;
}

export interface ApiError {
  type: string;
  message: string;
  correlationId: string;
  statusCode: number;
  errors?: Record<string, string[]>;
}

/**
 * Type guard for API errors
 */
export function isApiError(error: unknown): error is { data: ApiError } {
  return (
    typeof error === 'object' &&
    error !== null &&
    'data' in error &&
    typeof (error as { data: unknown }).data === 'object'
  );
}

/**
 * Extract error message from API error
 */
export function getErrorMessage(error: unknown): string {
  if (isApiError(error)) {
    return error.data.message || 'An error occurred';
  }
  if (error instanceof Error) {
    return error.message;
  }
  return 'An unexpected error occurred';
}
```

---

## 12. App Provider Setup (Updated)

```typescript
// src/main.tsx
import React from 'react';
import ReactDOM from 'react-dom/client';
import { Provider } from 'react-redux';
import { RouterProvider } from 'react-router-dom';
import { FluentProvider } from '@fluentui/react-components';
import { store } from '@/app/store';
import { router } from '@/app/router';
import { customLightTheme } from '@/theme';
import '@/index.css';

// MSW initialization (development only)
async function enableMocking() {
  if (import.meta.env.DEV && import.meta.env.VITE_ENABLE_MSW === 'true') {
    const { worker } = await import('@/mocks/browser');
    return worker.start({
      onUnhandledRequest: 'bypass',
    });
  }
  return Promise.resolve();
}

enableMocking().then(() => {
  ReactDOM.createRoot(document.getElementById('root')!).render(
    <React.StrictMode>
      <Provider store={store}>
        <FluentProvider theme={customLightTheme}>
          <RouterProvider router={router} />
        </FluentProvider>
      </Provider>
    </React.StrictMode>
  );
});
```

---

## 13. Cache Invalidation Matrix

| Mutation | Invalidated Tags | Effect |
|----------|-----------------|--------|
| **login** | User, Account, Transaction, Session | Fresh data after login |
| **logout** | User, Account, Transaction, Balance, Session | Clear all cached data |
| **deposit** | Transaction LIST, Balance, Account | Update balance and history |
| **withdraw** | Transaction LIST, Balance, Account | Update balance and history |
| **transfer** | Transaction LIST, Balance, Account | Update sender's data |
| **internalTransfer** | Both accounts: Transaction, Balance, Account | Update both accounts |
| **createAccount** | Account LIST | Show new account |
| **setAsPrimary** | Account LIST | Reflect primary change |
| **verifyPin** | Session | Update auth level |

---

## 14. State Shape Summary (BFF Architecture)

```typescript
// Complete RootState shape
interface RootState {
  auth: {
    user: User | null;
    session: SessionInfo | null;
    isAuthenticated: boolean;
    isInitialized: boolean;
    isLoading: boolean;
    // NO TOKEN - BFF manages JWT server-side
  };

  ui: {
    toasts: Toast[];
    activeDialogs: DialogId[];
    dialogData: Record<string, unknown>;
    isSidebarCollapsed: boolean;
    isMobileNavOpen: boolean;
    isGlobalLoading: boolean;
    globalLoadingMessage: string | null;
    sessionTimeoutWarningVisible: boolean;
    sessionExpiresAt: string | null;
  };

  transferWizard: {
    currentStep: TransferWizardStep;
    isOpen: boolean;
    transferType: 'internal' | 'external' | null;
    sourceAccountId: string | null;
    recipient: Recipient | null;
    destinationAccountId: string | null;
    amount: number | null;
    description: string;
    completedTransfer: TransferResponse | null;
    isSubmitting: boolean;
    error: string | null;
  };

  api: {
    queries: { /* RTK Query managed */ };
    mutations: { /* RTK Query managed */ };
    provided: { /* Tag tracking */ };
    subscriptions: { /* Active subscriptions */ };
  };
}
```

---

## 15. Best Practices

### 15.1 BFF-Specific Practices

1. **NEVER store tokens client-side** - Auth is session-based via HTTP-only cookies
2. **Always use `credentials: 'include'`** - Required for cookie-based auth
3. **Handle 401 globally** - Clear auth state and redirect to login
4. **Check session on app startup** - Use `getMe` to validate session

### 15.2 RTK Query Practices

1. **Use single base API** - All endpoints injected via `injectEndpoints`
2. **Transform responses** - Extract `data` from `ApiResponse<T>`
3. **Tag everything** - Enables automatic cache invalidation
4. **Use lazy queries** - For on-demand fetching (e.g., search)

### 15.3 Slice Practices

1. **Keep slices focused** - One responsibility per slice
2. **Export selectors** - Memoized state access
3. **Use PayloadAction** - Type-safe action payloads
4. **Reset state properly** - Handle logout by returning `initialState`

### 15.4 Component Integration

1. **Use typed hooks** - `useAppSelector`, `useAppDispatch`
2. **Create custom hooks** - Abstract Redux logic from components
3. **Handle loading states** - Use RTK Query's `isLoading`
4. **Handle errors** - Use `isError` and show user-friendly messages

---

## 16. References

- **Complete Implementation**: [10-implementation-guide-frontend.md](10-implementation-guide-frontend.md)
- **Security Architecture**: [08-security-design.md](08-security-design.md)
- **API Contracts**: [06-api-contracts.md](06-api-contracts.md)
- **MSW Handlers**: [07-msw-mock-handlers.md](07-msw-mock-handlers.md)

---

**Document Status**: UPDATED - Phase 6 Complete
**BFF Architecture**: Yes (Phase 5+)
**Ready for Implementation**: Yes (use 10-implementation-guide-frontend.md as primary reference)
**Last Updated**: 2026-01-09 by Claude (Frontend Lead)
