# Architecture Overview
## Bank Account Management System

**Document Version**: 2.0
**Created**: 2025-12-16
**Updated**: 2025-12-17
**Status**: FINALIZED - Phase 1 Complete
**Author**: System Architect

---

## 1. System Context Diagram (with BFF Pattern)

```
┌─────────────────────────────────────────────────────────────────────────┐
│                              USERS                                       │
│                    (Desktop & Mobile Browsers)                           │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    │ HTTPS
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                      FRONTEND APPLICATION                                │
│                     (React 19.2 + TypeScript)                           │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │                    FluentUI v9 Components                          │ │
│  │         (@fluentui/react-components ^9.72.8)                       │ │
│  ├────────────────────────────────────────────────────────────────────┤ │
│  │                    React Router v7                                 │ │
│  │            (react-router-dom ^7.1.1)                               │ │
│  ├────────────────────────────────────────────────────────────────────┤ │
│  │              Redux Toolkit + RTK Query                             │ │
│  │         (@reduxjs/toolkit ^2.11.2)                                 │ │
│  │    ** NO TOKENS STORED - Only user info in Redux **                │ │
│  ├────────────────────────────────────────────────────────────────────┤ │
│  │                  Axios HTTP Client                                 │ │
│  │              (axios ^1.7.9)                                        │ │
│  │        ** Credentials: include (for session cookie) **             │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                              │                                           │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │               MSW (Mock Service Worker)                            │ │
│  │           (Development & Testing Only)                             │ │
│  └────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    │ HTTP Requests + Session Cookie
                                    │ Cookie: .AzureBank.Session
                                    │ (HTTP-only, Secure, SameSite=Strict)
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                      BFF GATEWAY (NEW)                                   │
│                  (.NET 10 + YARP Reverse Proxy)                         │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │              BFF Authentication Controller                         │ │
│  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐               │ │
│  │  │  /bff/auth/  │ │  /bff/auth/  │ │  /bff/auth/  │               │ │
│  │  │    login     │ │    logout    │ │      me      │               │ │
│  │  └──────────────┘ └──────────────┘ └──────────────┘               │ │
│  │                  ┌──────────────┐                                  │ │
│  │                  │  /bff/auth/  │                                  │ │
│  │                  │  verify-pin  │                                  │ │
│  │                  └──────────────┘                                  │ │
│  ├────────────────────────────────────────────────────────────────────┤ │
│  │              Session Management                                    │ │
│  │  ┌──────────────────────────────────────────────────────────┐     │ │
│  │  │  JWT stored SERVER-SIDE in session (Memory or Redis)     │     │ │
│  │  │  Browser NEVER sees the JWT token                        │     │ │
│  │  │  Session tracks: userId, token, authLevel, lastActivity  │     │ │
│  │  └──────────────────────────────────────────────────────────┘     │ │
│  ├────────────────────────────────────────────────────────────────────┤ │
│  │              YARP Reverse Proxy                                    │ │
│  │  ┌──────────────────────────────────────────────────────────┐     │ │
│  │  │  Proxies /api/* requests to Backend API                  │     │ │
│  │  │  BearerTokenTransform: Adds Authorization header         │     │ │
│  │  │  from server-side session before forwarding              │     │ │
│  │  └──────────────────────────────────────────────────────────┘     │ │
│  ├────────────────────────────────────────────────────────────────────┤ │
│  │              Middleware Pipeline                                   │ │
│  │  [Security Headers] → [Session] → [Rate Limit] → [YARP]           │ │
│  └────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    │ REST API / JSON
                                    │ Authorization: Bearer <JWT>
                                    │ (Injected by BFF from session)
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                      BACKEND API                                         │
│                  (.NET 10 LTS + ASP.NET Core)                           │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │              ASP.NET Core Web API                                  │ │
│  │                                                                    │ │
│  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐               │ │
│  │  │     Auth     │ │   Account    │ │ Transaction  │               │ │
│  │  │  Controller  │ │  Controller  │ │  Controller  │               │ │
│  │  └──────────────┘ └──────────────┘ └──────────────┘               │ │
│  ├────────────────────────────────────────────────────────────────────┤ │
│  │              Middleware Pipeline                                   │ │
│  │  [Exception] → [JWT Auth] → [Logging] → [Routing]                 │ │
│  │  ** No CORS needed - only BFF can access **                       │ │
│  ├────────────────────────────────────────────────────────────────────┤ │
│  │              Business Services Layer                               │ │
│  │  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐               │ │
│  │  │ AuthService  │ │AccountService│ │TransactionSvc│               │ │
│  │  └──────────────┘ └──────────────┘ └──────────────┘               │ │
│  ├────────────────────────────────────────────────────────────────────┤ │
│  │           Entity Framework Core 10                                 │ │
│  │              (Code-First + Migrations)                             │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                              │                                           │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │               Scalar API Documentation                             │ │
│  │            (Available at /scalar/v1)                               │ │
│  └────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                                    │ SQL Connection
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                      DATABASE                                            │
│                  Microsoft SQL Server 2022+                              │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │  ┌──────────┐  ┌──────────┐  ┌──────────────┐                     │ │
│  │  │  Users   │  │ Accounts │  │ Transactions │                     │ │
│  │  │          │──│          │──│              │                     │ │
│  │  │ Id (PK)  │  │ Id (PK)  │  │ Id (PK)      │                     │ │
│  │  │ Email    │  │ UserId   │  │ AccountId    │                     │ │
│  │  │ PassHash │  │ AcctNum  │  │ Type         │                     │ │
│  │  │ Pin      │  │ Balance  │  │ Amount       │                     │ │
│  │  └──────────┘  └──────────┘  └──────────────┘                     │ │
│  └────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
```

### 1.1 BFF Authentication Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    BFF AUTHENTICATION FLOW                               │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  1. LOGIN                                                                │
│  ────────                                                                │
│  Browser                    BFF                         Backend API      │
│     │                        │                              │            │
│     │  POST /bff/auth/login  │                              │            │
│     │  {email, password}     │                              │            │
│     │───────────────────────>│                              │            │
│     │                        │  POST /api/auth/login        │            │
│     │                        │──────────────────────────────>            │
│     │                        │  {token, user}               │            │
│     │                        │<──────────────────────────────            │
│     │                        │                              │            │
│     │                        │  Store JWT in session        │            │
│     │                        │  (server-side memory)        │            │
│     │                        │                              │            │
│     │  Set-Cookie:           │                              │            │
│     │  .AzureBank.Session    │                              │            │
│     │  {user} (NO TOKEN!)    │                              │            │
│     │<───────────────────────│                              │            │
│                                                                          │
│  2. PROTECTED REQUEST                                                    │
│  ────────────────────                                                    │
│  Browser                    BFF                         Backend API      │
│     │                        │                              │            │
│     │  GET /api/accounts     │                              │            │
│     │  Cookie: session=...   │                              │            │
│     │───────────────────────>│                              │            │
│     │                        │  Validate session            │            │
│     │                        │  Get JWT from session        │            │
│     │                        │                              │            │
│     │                        │  GET /api/accounts           │            │
│     │                        │  Authorization: Bearer JWT   │            │
│     │                        │──────────────────────────────>            │
│     │                        │  {accounts}                  │            │
│     │                        │<──────────────────────────────            │
│     │  {accounts}            │                              │            │
│     │  (NO TOKEN!)           │                              │            │
│     │<───────────────────────│                              │            │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### 1.2 Security Benefits of BFF Pattern

| Threat | Mitigation |
|--------|------------|
| XSS Token Theft | Tokens never reach browser - nothing to steal |
| CSRF | SameSite=Strict cookies prevent cross-site requests |
| Token in Network | Only session ID visible, not actual JWT |
| Session Hijacking | HTTP-only cookies, secure flag, short expiry |

> See [08-security-design.md](08-security-design.md) for complete security architecture.

---

## 2. Frontend Architecture (Detailed)

### 2.1 Complete Folder Structure

```
frontend/
├── public/
│   ├── mockServiceWorker.js      # MSW service worker
│   └── favicon.ico
│
├── src/
│   ├── main.tsx                  # Application entry point
│   ├── App.tsx                   # Root component with providers
│   ├── vite-env.d.ts            # Vite type declarations
│   │
│   ├── app/                      # Application setup
│   │   ├── store.ts             # Redux store configuration
│   │   ├── hooks.ts             # Typed useAppDispatch, useAppSelector
│   │   ├── router.tsx           # React Router configuration
│   │   └── api.ts               # Base RTK Query API setup
│   │
│   ├── features/                 # Feature-based modules
│   │   ├── auth/
│   │   │   ├── authSlice.ts     # Auth state (token, user, isAuthenticated)
│   │   │   ├── authApi.ts       # RTK Query: login, register
│   │   │   ├── useAuth.ts       # Custom hook for auth operations
│   │   │   └── index.ts         # Feature exports
│   │   │
│   │   ├── account/
│   │   │   ├── accountApi.ts    # RTK Query: getAccount, getBalance
│   │   │   ├── useAccount.ts    # Custom hook for account operations
│   │   │   └── index.ts
│   │   │
│   │   ├── transactions/
│   │   │   ├── transactionApi.ts # RTK Query: deposit, withdraw, getHistory
│   │   │   ├── useTransactions.ts
│   │   │   └── index.ts
│   │   │
│   │   └── transfer/
│   │       ├── transferApi.ts    # RTK Query: transfer
│   │       ├── useTransfer.ts
│   │       └── index.ts
│   │
│   ├── components/               # Reusable components
│   │   ├── common/              # Generic UI components
│   │   │   ├── LoadingSpinner.tsx
│   │   │   ├── ErrorMessage.tsx
│   │   │   ├── ConfirmDialog.tsx
│   │   │   ├── AmountInput.tsx
│   │   │   └── index.ts
│   │   │
│   │   ├── layout/              # Layout components
│   │   │   ├── AppLayout.tsx    # Main app layout with nav
│   │   │   ├── AuthLayout.tsx   # Layout for login/register
│   │   │   ├── Sidebar.tsx      # Desktop sidebar navigation
│   │   │   ├── MobileNav.tsx    # Mobile bottom navigation
│   │   │   ├── Header.tsx       # Top header
│   │   │   └── index.ts
│   │   │
│   │   └── forms/               # Form components
│   │       ├── LoginForm.tsx
│   │       ├── RegisterForm.tsx
│   │       ├── DepositForm.tsx
│   │       ├── WithdrawForm.tsx
│   │       ├── TransferForm.tsx
│   │       └── index.ts
│   │
│   ├── pages/                    # Route page components
│   │   ├── Login/
│   │   │   ├── LoginPage.tsx
│   │   │   └── index.ts
│   │   ├── Register/
│   │   │   ├── RegisterPage.tsx
│   │   │   └── index.ts
│   │   ├── Dashboard/
│   │   │   ├── DashboardPage.tsx
│   │   │   ├── BalanceCard.tsx
│   │   │   ├── QuickActions.tsx
│   │   │   ├── RecentTransactions.tsx
│   │   │   └── index.ts
│   │   ├── Deposit/
│   │   │   ├── DepositPage.tsx
│   │   │   └── index.ts
│   │   ├── Withdraw/
│   │   │   ├── WithdrawPage.tsx
│   │   │   └── index.ts
│   │   ├── Transfer/
│   │   │   ├── TransferPage.tsx
│   │   │   └── index.ts
│   │   ├── History/
│   │   │   ├── HistoryPage.tsx
│   │   │   ├── TransactionList.tsx
│   │   │   ├── TransactionFilters.tsx
│   │   │   └── index.ts
│   │   └── NotFound/
│   │       ├── NotFoundPage.tsx
│   │       └── index.ts
│   │
│   ├── hooks/                    # Custom React hooks
│   │   ├── useResponsive.ts     # Media query hook
│   │   ├── useToast.ts          # Toast notifications
│   │   ├── useDebounce.ts       # Debounce values
│   │   └── index.ts
│   │
│   ├── types/                    # TypeScript type definitions
│   │   ├── auth.types.ts        # User, LoginRequest, etc.
│   │   ├── account.types.ts     # Account, Balance, etc.
│   │   ├── transaction.types.ts # Transaction, TransactionType
│   │   ├── api.types.ts         # ApiError, ApiResponse
│   │   └── index.ts
│   │
│   ├── utils/                    # Utility functions
│   │   ├── formatCurrency.ts    # Currency formatting
│   │   ├── formatDate.ts        # Date formatting
│   │   ├── validation.ts        # Zod schemas
│   │   ├── storage.ts           # Secure storage helpers
│   │   └── index.ts
│   │
│   ├── mocks/                    # MSW mock handlers
│   │   ├── browser.ts           # MSW browser setup
│   │   ├── handlers/
│   │   │   ├── auth.handlers.ts
│   │   │   ├── account.handlers.ts
│   │   │   ├── transaction.handlers.ts
│   │   │   └── index.ts
│   │   ├── data/
│   │   │   ├── users.ts         # Mock user data
│   │   │   ├── accounts.ts      # Mock account data
│   │   │   └── transactions.ts  # Mock transaction data
│   │   └── index.ts
│   │
│   ├── theme/                    # FluentUI theming
│   │   ├── theme.ts             # Custom theme configuration
│   │   ├── tokens.ts            # Custom design tokens
│   │   └── index.ts
│   │
│   └── styles/                   # Global styles
│       └── global.css           # Global CSS (minimal)
│
├── index.html                    # HTML entry point
├── package.json                  # Dependencies
├── tsconfig.json                 # TypeScript config
├── tsconfig.node.json           # Node TypeScript config
├── vite.config.ts               # Vite configuration
├── eslint.config.js             # ESLint configuration
└── .prettierrc                  # Prettier configuration
```

### 2.2 Component Hierarchy

```
App
├── FluentProvider (theme)
│   └── Provider (Redux store)
│       └── BrowserRouter
│           ├── AuthLayout (public routes)
│           │   ├── Route: /login → LoginPage
│           │   │   └── LoginForm
│           │   └── Route: /register → RegisterPage
│           │       └── RegisterForm
│           │
│           └── ProtectedRoute (requires auth)
│               └── AppLayout
│                   ├── Header
│                   │   ├── Logo
│                   │   ├── UserMenu
│                   │   └── ThemeToggle (optional)
│                   │
│                   ├── Sidebar (desktop) / MobileNav (mobile)
│                   │   ├── NavItem: Dashboard
│                   │   ├── NavItem: Deposit
│                   │   ├── NavItem: Withdraw
│                   │   ├── NavItem: Transfer
│                   │   ├── NavItem: History
│                   │   └── NavItem: Logout
│                   │
│                   └── Main Content
│                       ├── Route: / → DashboardPage
│                       │   ├── BalanceCard
│                       │   ├── QuickActions
│                       │   └── RecentTransactions
│                       │
│                       ├── Route: /deposit → DepositPage
│                       │   └── DepositForm
│                       │
│                       ├── Route: /withdraw → WithdrawPage
│                       │   └── WithdrawForm
│                       │
│                       ├── Route: /transfer → TransferPage
│                       │   └── TransferForm
│                       │
│                       └── Route: /history → HistoryPage
│                           ├── TransactionFilters
│                           └── TransactionList
```

---

## 3. Backend Architecture (Detailed)

### 3.1 Complete Solution Structure

```
backend/
├── BankApp.sln                           # Solution file
│
├── src/
│   └── BankApp.API/                      # Main API project
│       ├── BankApp.API.csproj            # Project file
│       │
│       ├── Controllers/                   # API Controllers
│       │   ├── AuthController.cs         # POST /api/auth/register, login
│       │   ├── AccountController.cs      # CRUD for accounts
│       │   ├── TransactionController.cs  # Deposits, withdrawals
│       │   └── TransferController.cs     # Money transfers
│       │
│       ├── Services/                      # Business Logic Layer
│       │   ├── Interfaces/
│       │   │   ├── IAuthService.cs
│       │   │   ├── IAccountService.cs
│       │   │   ├── ITransactionService.cs
│       │   │   ├── ITransferService.cs
│       │   │   └── IPasswordService.cs
│       │   │
│       │   ├── AuthService.cs            # Auth business logic
│       │   ├── AccountService.cs         # Account business logic
│       │   ├── TransactionService.cs     # Transaction logic
│       │   ├── TransferService.cs        # Transfer logic
│       │   ├── PasswordService.cs        # Argon2id hashing
│       │   └── JwtService.cs             # JWT generation/validation
│       │
│       ├── Models/                        # Domain Models
│       │   ├── Entities/                 # Database entities
│       │   │   ├── User.cs
│       │   │   ├── Account.cs
│       │   │   └── Transaction.cs
│       │   │
│       │   ├── DTOs/                     # Data Transfer Objects
│       │   │   ├── Auth/
│       │   │   │   ├── RegisterRequest.cs
│       │   │   │   ├── LoginRequest.cs
│       │   │   │   └── AuthResponse.cs
│       │   │   │
│       │   │   ├── Account/
│       │   │   │   ├── CreateAccountRequest.cs
│       │   │   │   ├── AccountResponse.cs
│       │   │   │   └── BalanceResponse.cs
│       │   │   │
│       │   │   ├── Transaction/
│       │   │   │   ├── DepositRequest.cs
│       │   │   │   ├── WithdrawRequest.cs
│       │   │   │   ├── TransferRequest.cs
│       │   │   │   ├── TransactionResponse.cs
│       │   │   │   └── TransactionHistoryResponse.cs
│       │   │   │
│       │   │   └── Common/
│       │   │       ├── ApiResponse.cs    # Standard response wrapper
│       │   │       └── ErrorResponse.cs  # Error response format
│       │   │
│       │   └── Enums/
│       │       └── TransactionType.cs    # Deposit, Withdrawal, TransferIn, TransferOut
│       │
│       ├── Data/                          # Data Access Layer
│       │   ├── BankDbContext.cs          # EF Core DbContext
│       │   │
│       │   ├── Configurations/           # EF Fluent API configs
│       │   │   ├── UserConfiguration.cs
│       │   │   ├── AccountConfiguration.cs
│       │   │   └── TransactionConfiguration.cs
│       │   │
│       │   └── Migrations/               # EF migrations (auto-generated)
│       │
│       ├── Validators/                    # FluentValidation
│       │   ├── Auth/
│       │   │   ├── RegisterRequestValidator.cs
│       │   │   └── LoginRequestValidator.cs
│       │   │
│       │   ├── Account/
│       │   │   └── CreateAccountRequestValidator.cs
│       │   │
│       │   └── Transaction/
│       │       ├── DepositRequestValidator.cs
│       │       ├── WithdrawRequestValidator.cs
│       │       └── TransferRequestValidator.cs
│       │
│       ├── Middleware/                    # Custom Middleware
│       │   ├── ExceptionMiddleware.cs    # Global exception handling
│       │   └── RequestLoggingMiddleware.cs
│       │
│       ├── Extensions/                    # Extension Methods
│       │   ├── ServiceCollectionExtensions.cs
│       │   ├── ApplicationBuilderExtensions.cs
│       │   └── ClaimsPrincipalExtensions.cs
│       │
│       ├── Configuration/                 # App Configuration
│       │   ├── JwtSettings.cs
│       │   └── Argon2Settings.cs
│       │
│       ├── Program.cs                     # Application entry point
│       ├── appsettings.json              # Default settings
│       └── appsettings.Development.json  # Dev settings
│
└── tests/
    └── BankApp.Tests/                     # Test project
        ├── BankApp.Tests.csproj
        ├── Unit/
        │   ├── Services/
        │   └── Validators/
        └── Integration/
            └── Controllers/
```

### 3.2 Clean Architecture Layers

```
┌─────────────────────────────────────────────────────────────────────────┐
│                        PRESENTATION LAYER                                │
│                         (Controllers)                                    │
│  ┌─────────────────────────────────────────────────────────────────────┐│
│  │  AuthController  │ AccountController │ TransactionController        ││
│  │                  │                   │ TransferController           ││
│  └─────────────────────────────────────────────────────────────────────┘│
│                                  │                                       │
│                                  │ DTOs                                  │
│                                  ▼                                       │
├─────────────────────────────────────────────────────────────────────────┤
│                        APPLICATION LAYER                                 │
│                          (Services)                                      │
│  ┌─────────────────────────────────────────────────────────────────────┐│
│  │ IAuthService      │ IAccountService  │ ITransactionService          ││
│  │ AuthService       │ AccountService   │ TransactionService           ││
│  │ PasswordService   │                  │ TransferService              ││
│  │ JwtService        │                  │                              ││
│  └─────────────────────────────────────────────────────────────────────┘│
│                                  │                                       │
│                                  │ Entities                              │
│                                  ▼                                       │
├─────────────────────────────────────────────────────────────────────────┤
│                         DOMAIN LAYER                                     │
│                      (Entities + Enums)                                  │
│  ┌─────────────────────────────────────────────────────────────────────┐│
│  │  User             │ Account          │ Transaction                  ││
│  │  TransactionType  │                  │                              ││
│  └─────────────────────────────────────────────────────────────────────┘│
│                                  │                                       │
│                                  │                                       │
│                                  ▼                                       │
├─────────────────────────────────────────────────────────────────────────┤
│                      INFRASTRUCTURE LAYER                                │
│                   (EF Core + External Services)                          │
│  ┌─────────────────────────────────────────────────────────────────────┐│
│  │  BankDbContext    │ Entity Configurations │ Migrations              ││
│  └─────────────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Data Flow Diagrams

### 4.1 Authentication Flow

```
┌──────────┐     ┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  User    │     │   Frontend   │     │   Backend    │     │   Database   │
└────┬─────┘     └──────┬───────┘     └──────┬───────┘     └──────┬───────┘
     │                  │                    │                    │
     │ 1. Enter creds   │                    │                    │
     │─────────────────>│                    │                    │
     │                  │                    │                    │
     │                  │ 2. POST /api/auth/login                 │
     │                  │   {email, password}│                    │
     │                  │───────────────────>│                    │
     │                  │                    │                    │
     │                  │                    │ 3. Query user      │
     │                  │                    │───────────────────>│
     │                  │                    │                    │
     │                  │                    │ 4. User data       │
     │                  │                    │<───────────────────│
     │                  │                    │                    │
     │                  │                    │ 5. Verify Argon2   │
     │                  │                    │    hash            │
     │                  │                    │                    │
     │                  │                    │ 6. Generate JWT    │
     │                  │                    │                    │
     │                  │ 7. {token, user}   │                    │
     │                  │<───────────────────│                    │
     │                  │                    │                    │
     │                  │ 8. Store token     │                    │
     │                  │    in Redux        │                    │
     │                  │                    │                    │
     │ 9. Navigate to   │                    │                    │
     │    Dashboard     │                    │                    │
     │<─────────────────│                    │                    │
     │                  │                    │                    │
```

### 4.2 Transaction Flow (Deposit)

```
┌──────────┐     ┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  User    │     │   Frontend   │     │   Backend    │     │   Database   │
└────┬─────┘     └──────┬───────┘     └──────┬───────┘     └──────┬───────┘
     │                  │                    │                    │
     │ 1. Enter amount  │                    │                    │
     │─────────────────>│                    │                    │
     │                  │                    │                    │
     │                  │ 2. Validate input  │                    │
     │                  │    (zod schema)    │                    │
     │                  │                    │                    │
     │                  │ 3. POST /api/transactions/deposit       │
     │                  │    {accountId, amount}                  │
     │                  │    Authorization: Bearer <token>        │
     │                  │───────────────────>│                    │
     │                  │                    │                    │
     │                  │                    │ 4. Validate JWT    │
     │                  │                    │                    │
     │                  │                    │ 5. Validate request│
     │                  │                    │    (FluentValidation)
     │                  │                    │                    │
     │                  │                    │ 6. Begin Transaction
     │                  │                    │                    │
     │                  │                    │ 7. Update balance  │
     │                  │                    │───────────────────>│
     │                  │                    │                    │
     │                  │                    │ 8. Insert txn record
     │                  │                    │───────────────────>│
     │                  │                    │                    │
     │                  │                    │ 9. Commit          │
     │                  │                    │                    │
     │                  │ 10. {transaction,  │                    │
     │                  │     newBalance}    │                    │
     │                  │<───────────────────│                    │
     │                  │                    │                    │
     │                  │ 11. Invalidate     │                    │
     │                  │     RTK Query cache│                    │
     │                  │                    │                    │
     │ 12. Show success │                    │                    │
     │     + new balance│                    │                    │
     │<─────────────────│                    │                    │
```

### 4.3 Transfer Flow

```
┌──────────┐     ┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  User    │     │   Frontend   │     │   Backend    │     │   Database   │
└────┬─────┘     └──────┬───────┘     └──────┬───────┘     └──────┬───────┘
     │                  │                    │                    │
     │ 1. Enter details │                    │                    │
     │   (acct#, amount)│                    │                    │
     │─────────────────>│                    │                    │
     │                  │                    │                    │
     │                  │ 2. Validate        │                    │
     │                  │                    │                    │
     │                  │ 3. POST /api/transfers                  │
     │                  │───────────────────>│                    │
     │                  │                    │                    │
     │                  │                    │ 4. Find recipient  │
     │                  │                    │    account         │
     │                  │                    │───────────────────>│
     │                  │                    │                    │
     │                  │                    │ 5. Verify balance  │
     │                  │                    │    >= amount       │
     │                  │                    │                    │
     │                  │                    │ 6. Begin Transaction
     │                  │                    │                    │
     │                  │                    │ 7. Debit source    │
     │                  │                    │───────────────────>│
     │                  │                    │                    │
     │                  │                    │ 8. Credit dest     │
     │                  │                    │───────────────────>│
     │                  │                    │                    │
     │                  │                    │ 9. Insert 2 txn    │
     │                  │                    │    records (linked)│
     │                  │                    │───────────────────>│
     │                  │                    │                    │
     │                  │                    │ 10. Commit         │
     │                  │                    │                    │
     │                  │ 11. Success        │                    │
     │                  │<───────────────────│                    │
     │                  │                    │                    │
     │ 12. Confirmation │                    │                    │
     │<─────────────────│                    │                    │
```

---

## 5. Security Architecture

### 5.1 Security Layers

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           SECURITY LAYERS                                │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  Layer 1: TRANSPORT SECURITY                                            │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │ • HTTPS/TLS 1.3 for all communications                             │ │
│  │ • HSTS headers in production                                       │ │
│  │ • Certificate validation                                           │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                          │
│  Layer 2: AUTHENTICATION                                                │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │ • JWT Bearer tokens (HS256 or RS256)                               │ │
│  │ • Access token: 15-30 minutes expiration                           │ │
│  │ • Refresh token: Optional, HTTP-only cookie                        │ │
│  │ • Token stored in Redux state (memory only)                        │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                          │
│  Layer 3: AUTHORIZATION                                                 │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │ • User can only access their own accounts                          │ │
│  │ • Claims-based authorization                                       │ │
│  │ • Resource ownership validation                                    │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                          │
│  Layer 4: PASSWORD SECURITY                                             │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │ • Argon2id hashing (OWASP recommended)                             │ │
│  │ • Memory: 64 MB, Iterations: 3, Parallelism: 4                     │ │
│  │ • Salt: 16 bytes random per password                               │ │
│  │ • Never store plaintext passwords                                  │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                          │
│  Layer 5: INPUT VALIDATION                                              │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │ • Frontend: Zod schema validation                                  │ │
│  │ • Backend: FluentValidation                                        │ │
│  │ • Sanitize all user inputs                                         │ │
│  │ • Reject malformed requests                                        │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                          │
│  Layer 6: SQL INJECTION PREVENTION                                      │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │ • Entity Framework Core with parameterized queries                 │ │
│  │ • No raw SQL concatenation                                         │ │
│  │ • Input sanitization                                               │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                          │
│  Layer 7: XSS PREVENTION                                                │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │ • React automatically escapes rendered content                     │ │
│  │ • CSP headers in production                                        │ │
│  │ • No dangerouslySetInnerHTML with user content                     │ │
│  └────────────────────────────────────────────────────────────────────┘ │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### 5.2 JWT Token Structure

```
Header:
{
  "alg": "HS256",
  "typ": "JWT"
}

Payload:
{
  "sub": "user-guid-here",           // User ID
  "email": "user@example.com",       // User email
  "name": "John Doe",                // User name
  "iat": 1702800000,                 // Issued at
  "exp": 1702801800,                 // Expires (30 min)
  "iss": "BankApp",                  // Issuer
  "aud": "BankApp"                   // Audience
}

Signature:
HMACSHA256(
  base64UrlEncode(header) + "." + base64UrlEncode(payload),
  secret
)
```

---

## 6. Error Handling Strategy

### 6.1 Frontend Error Handling

```typescript
// Global error types
interface ApiError {
  type: string;
  message: string;
  errors?: Record<string, string[]>;
  statusCode: number;
}

// RTK Query error handling
const baseQuery = fetchBaseQuery({
  baseUrl: '/api',
  prepareHeaders: (headers, { getState }) => {
    const token = (getState() as RootState).auth.token;
    if (token) {
      headers.set('Authorization', `Bearer ${token}`);
    }
    return headers;
  },
});

// Error display patterns
// - Toast for transient errors (network)
// - Inline for validation errors
// - Full page for critical errors (401, 500)
```

### 6.2 Backend Error Handling

```csharp
// Global exception middleware
app.UseMiddleware<ExceptionMiddleware>();

// Standard error response format
{
  "type": "validation_error",
  "message": "Validation failed",
  "errors": {
    "amount": ["Amount must be greater than 0"],
    "accountId": ["Account not found"]
  },
  "statusCode": 422
}

// HTTP Status Codes
// 200 - Success
// 201 - Created
// 400 - Bad Request
// 401 - Unauthorized
// 403 - Forbidden
// 404 - Not Found
// 422 - Validation Error
// 500 - Internal Server Error
```

---

## 7. Responsive Design Strategy

### 7.1 Breakpoints

```css
/* Mobile-first approach */
/* Mobile: 0 - 479px (default) */
/* Tablet: 480px - 1023px */
/* Desktop: 1024px+ */

@media (min-width: 480px) { /* Tablet */ }
@media (min-width: 1024px) { /* Desktop */ }
```

### 7.2 Layout Behavior

| Breakpoint | Navigation | Layout | Transaction Form |
|------------|------------|--------|------------------|
| Mobile | Bottom nav | Single column | Full width |
| Tablet | Bottom nav | Two columns | Modal or inline |
| Desktop | Side nav | Two/three columns | Side panel |

---

## 8. Key Design Decisions Summary

| Decision | Choice | Rationale |
|----------|--------|-----------|
| State Management | Redux Toolkit + RTK Query | Required by specification, excellent caching |
| UI Library | FluentUI v9 | Required, Microsoft design system |
| ORM | Entity Framework Core 10 | .NET standard, type-safe, migrations |
| Password Hashing | Argon2id | OWASP recommended, memory-hard |
| API Documentation | Scalar | Modern UI, team experience |
| HTTP Client | Axios | Interceptors for auth, better errors |
| Build Tool | Vite | Fast HMR, modern, CRA deprecated |
| Form Validation | Zod + react-hook-form | Type inference, performance |
| Backend Validation | FluentValidation | Clean, testable rules |
| Logging | Serilog | Structured, multiple sinks |

---

## 9. Cross-Cutting Concerns

For detailed implementation specifications, see: [17-cross-cutting-concerns.md](17-cross-cutting-concerns.md)

### 9.1 Error Handling
```
┌─────────────────────────────────────────────────────────────────────────┐
│                    EXCEPTION HIERARCHY                                   │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  AppException (base)                                                    │
│  ├── NotFoundException (404)                                            │
│  ├── BusinessRuleException (400)                                        │
│  │   └── InsufficientFundsException                                     │
│  ├── AuthenticationException (401)                                      │
│  ├── AuthorizationException (403)                                       │
│  ├── ConflictException (409) - e.g., duplicate AzureTag                │
│  └── RateLimitException (429)                                           │
│                                                                          │
│  Global Exception Middleware catches all exceptions and returns:        │
│  - Sanitized error message (external)                                   │
│  - Full stack trace logged (internal)                                   │
│  - Correlation ID in response                                           │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### 9.2 Observability Stack
```
┌─────────────────────────────────────────────────────────────────────────┐
│                    OBSERVABILITY ARCHITECTURE                            │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  ┌──────────────┐     ┌──────────────┐     ┌──────────────┐            │
│  │   Tracing    │     │   Metrics    │     │   Logging    │            │
│  │ OpenTelemetry│     │ OpenTelemetry│     │   Serilog    │            │
│  └──────┬───────┘     └──────┬───────┘     └──────┬───────┘            │
│         │                    │                    │                     │
│         ▼                    ▼                    ▼                     │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │              Correlation ID (X-Correlation-ID)                   │   │
│  │      Links all traces, metrics, and logs for one request        │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│         │                    │                    │                     │
│         ▼                    ▼                    ▼                     │
│  ┌───────────┐       ┌───────────┐       ┌───────────┐                 │
│  │  Jaeger   │       │Prometheus │       │    Seq    │                 │
│  │  /OTLP    │       │   /OTLP   │       │ /Console  │                 │
│  └───────────┘       └───────────┘       └───────────┘                 │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

### 9.3 Object Mapping
```
┌─────────────────────────────────────────────────────────────────────────┐
│                    MAPPERLY OBJECT MAPPING                               │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  Why Mapperly (NOT AutoMapper):                                         │
│  ✓ MIT License (free forever)                                          │
│  ✓ Compile-time source generation (zero reflection)                    │
│  ✓ Full .NET 10 and Native AOT support                                 │
│  ✓ Compile-time type safety                                            │
│                                                                          │
│  [Mapper]                                                               │
│  public partial class AccountMapper                                     │
│  {                                                                       │
│      public partial AccountDto ToDto(Account account);                  │
│      public partial Account ToEntity(CreateAccountRequest request);    │
│  }                                                                       │
│                                                                          │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## 10. Performance Considerations

### 10.1 Frontend
- RTK Query automatic caching
- Lazy loading routes with React.lazy()
- FluentUI tree-shaking
- Vite optimized bundles

### 10.2 Backend
- EF Core query optimization
- Async/await throughout
- Response compression
- Database indexing strategy

---

**Document Status**: FINALIZED
**Phase 1 Complete**: Yes
**Ready for Phase 2**: Yes
