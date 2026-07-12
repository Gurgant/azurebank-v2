# Implementation Handoff Report
## AzureBank - Bank Account Management System

**Document Version**: 1.0
**Created**: 2026-01-09
**Purpose**: Handoff for implementation phase (AI or human developer)
**Status**: READY FOR IMPLEMENTATION

---

## 1. Project Overview

### What is AzureBank?
A personal banking web application that allows users to:
- Register with unique AzureTag (@username)
- Create multiple bank accounts (Checking, Savings, Investment)
- Deposit and withdraw funds
- Transfer money internally (between own accounts) and externally (to other users via AzureTag)
- View transaction history with filtering
- Query historical balance at any point in time

### Project Type
- **Full-stack web application**
- **Technical assessment** for Dev4Side
- **Design-first methodology** - All design documentation complete

### Current State
- **Design Phase**: 100% COMPLETE (9 phases)
- **Implementation Phase**: NOT STARTED
- **Documentation**: ~35,000+ lines across 45+ documents

---

## 2. Technology Stack (Finalized)

### Frontend
| Package | Version | Purpose |
|---------|---------|---------|
| react | ^19.2.3 | UI framework |
| typescript | ^5.7.2 | Type safety (strict mode) |
| @fluentui/react-components | ^9.72.8 | UI component library |
| @fluentui/react-icons | ^2.0.270 | Icon library |
| @reduxjs/toolkit | ^2.11.2 | State management + RTK Query |
| react-redux | ^9.2.0 | React bindings |
| react-router-dom | ^7.1.1 | Routing |
| axios | ^1.7.9 | HTTP client |
| react-hook-form | ^7.54.2 | Form handling |
| zod | ^3.24.1 | Validation |
| date-fns | ^4.1.0 | Date utilities |
| sweetalert2 | ^11.15.2 | Confirmation dialogs |
| msw | ^2.7.0 | API mocking (dev only) |
| vite | ^6.0.5 | Build tool |

### Backend
| Package | Version | Purpose |
|---------|---------|---------|
| .NET SDK | 10.0 | Runtime |
| Microsoft.EntityFrameworkCore | 10.0.0 | ORM |
| Microsoft.EntityFrameworkCore.SqlServer | 10.0.0 | SQL Server provider |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.0 | User management |
| Microsoft.IdentityModel.JsonWebTokens | 8.15.0 | JWT handling |
| Yarp.ReverseProxy | 2.x | BFF reverse proxy |
| Scalar.AspNetCore | 2.11.5 | API documentation |
| FluentValidation | 11.11.0 | Request validation |
| Serilog | 4.2.0 | Structured logging |
| Riok.Mapperly | 4.0.1 | Object mapping (NOT AutoMapper) |

### Package Manager
| Tool | Command | Purpose |
|------|---------|---------|
| Bun | 1.x | Package manager (17x faster than npm) |

---

## 3. Architecture Summary

### System Architecture (BFF Pattern)

```
┌─────────────────────────────────────────────────────┐
│                  BROWSER                             │
│            React 19 + FluentUI + Redux               │
│         ** NO JWT TOKENS STORED HERE **              │
└─────────────────────────────────────────────────────┘
                        │
                        │ HTTP-only Session Cookie
                        ▼
┌─────────────────────────────────────────────────────┐
│                  BFF GATEWAY                         │
│              .NET 10 + YARP Proxy                   │
│   - Stores JWT in server-side session               │
│   - Injects Authorization header to API             │
│   - Handles /bff/auth/* endpoints                   │
└─────────────────────────────────────────────────────┘
                        │
                        │ Authorization: Bearer JWT
                        ▼
┌─────────────────────────────────────────────────────┐
│                  BACKEND API                         │
│              .NET 10 + EF Core 10                   │
│   - Business logic and validation                   │
│   - Only accessible from BFF (no CORS)              │
└─────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────┐
│                  SQL SERVER                          │
│   Tables: Users, Accounts, Transactions, RefreshTokens │
└─────────────────────────────────────────────────────┘
```

### Why BFF Pattern?
- **XSS Protection**: JWT never reaches browser - nothing to steal
- **CSRF Protection**: SameSite=Strict cookies
- **Token Security**: Only session ID visible in network traffic
- **Server Control**: Can revoke sessions instantly

---

## 4. Key Design Decisions (MUST FOLLOW)

### 4.1 Authentication (CRITICAL)

```typescript
// CORRECT: Frontend Redux auth state
interface AuthState {
  user: User | null;           // User info only
  session: SessionInfo | null; // Session metadata
  isAuthenticated: boolean;
  isInitialized: boolean;
  // NO TOKEN FIELD - tokens are server-side only
}

// CORRECT: RTK Query API configuration
const baseQuery = fetchBaseQuery({
  baseUrl: '/bff',  // All requests go through BFF
  credentials: 'include',  // CRITICAL: Include session cookie
});
```

### 4.2 API Request Pattern

```typescript
// CORRECT: All API calls include credentials
fetch('/api/accounts', {
  credentials: 'include',  // Required for session cookie
});

// axios equivalent
axios.defaults.withCredentials = true;
```

### 4.3 Session Cookie Configuration

```csharp
// Backend BFF session configuration
options.Cookie.Name = ".AzureBank.Session";
options.Cookie.HttpOnly = true;
options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
options.Cookie.SameSite = SameSiteMode.Strict;
options.IdleTimeout = TimeSpan.FromMinutes(30);
```

### 4.4 Database Entities

```
Users
├── Id (GUID, PK)
├── AzureTag (UNIQUE, IMMUTABLE) - e.g., "johnsmith"
├── Email (UNIQUE)
├── PasswordHash (Argon2id)
├── FirstName, LastName
└── CreatedAt, UpdatedAt, IsDeleted

Accounts
├── Id (GUID, PK)
├── UserId (FK → Users)
├── AccountNumber (UNIQUE) - Format: AB-XXXX-XXXX-XX
├── Name, Type (checking/savings/investment)
├── Balance (DECIMAL 19,4)
├── IsPrimary (BIT) - Only one per user
└── CreatedAt, UpdatedAt, IsDeleted

Transactions
├── Id (GUID, PK)
├── TransactionNumber - Format: TXN-YYYYMMDD-XXXXXX
├── AccountId (FK → Accounts)
├── Type (deposit/withdrawal/transfer_in/transfer_out)
├── Amount, BalanceBefore, BalanceAfter
├── Description
├── RelatedTransactionId (FK → Transactions) - For transfer pairs
├── RecipientAzureTag, SenderAzureTag
└── CreatedAt
```

---

## 5. Implementation Priority

### Phase 1: Project Setup (FIRST)

```bash
# 1. Create frontend
bun create vite frontend --template react-ts
cd frontend
bun install

# 2. Install dependencies (from package.json in 16-package-manifest.md)
bun add @fluentui/react-components @fluentui/react-icons
bun add @reduxjs/toolkit react-redux react-router-dom
bun add axios react-hook-form @hookform/resolvers zod
bun add date-fns clsx sweetalert2 sweetalert2-react-content use-debounce
bun add -d msw

# 3. Initialize MSW
bunx msw init public/ --save

# 4. Configure TypeScript strict mode
# See tsconfig.json in 10-implementation-guide-frontend.md
```

### Phase 2: Core Infrastructure

| Priority | Task | Reference Document |
|----------|------|-------------------|
| 1 | FluentUI theme setup | 10-implementation-guide-frontend.md Section 9 |
| 2 | Redux store + typed hooks | 10-implementation-guide-frontend.md Section 6 |
| 3 | RTK Query base API | 10-implementation-guide-frontend.md Section 7 |
| 4 | Auth slice (BFF-aware) | 09-redux-architecture.md Section 4 |
| 5 | Router configuration | 10-implementation-guide-frontend.md Section 11 |

### Phase 3: MSW Mock Handlers

| Priority | Handler Group | Reference Document |
|----------|--------------|-------------------|
| 1 | Mock database + session store | 10-implementation-guide-frontend.md Section 8.5-8.6 |
| 2 | BFF auth handlers | 10-implementation-guide-frontend.md Section 8.7 |
| 3 | Account handlers | 07-msw-mock-handlers.md |
| 4 | Transaction handlers | 07-msw-mock-handlers.md |
| 5 | Transfer handlers | 07-msw-mock-handlers.md |

### Phase 4: UI Components

| Priority | Component | Reference Document |
|----------|-----------|-------------------|
| 1 | AppLayout, Header, MobileNav | 10-implementation-guide-frontend.md Section 11.4-11.5 |
| 2 | LoginPage, RegisterPage | 10-implementation-guide-frontend.md Section 11.6-11.7 |
| 3 | DashboardPage | 10-implementation-guide-frontend.md Section 11.8 |
| 4 | BalanceCard, TransactionCard | 10-implementation-guide-frontend.md Section 10.2-10.3 |
| 5 | AccountsPage, AccountDetailPage | 10-implementation-guide-frontend.md Section 11.9-11.10 |
| 6 | Transfer wizard components | 04l-external-transfers-design.md |

### Phase 5: Backend

| Priority | Task | Reference Document |
|----------|------|-------------------|
| 1 | .NET solution structure | 11-implementation-guide-backend.md Section 2 |
| 2 | AzureBank.Shared library | 11-implementation-guide-backend.md Section 4 |
| 3 | EF Core DbContext | 11-implementation-guide-backend.md Section 5 |
| 4 | AzureBank.Api controllers/services | 11-implementation-guide-backend.md Section 6 |
| 5 | AzureBank.Bff YARP + session | 11-implementation-guide-backend.md Section 7 |

### Phase 6: Integration & Deployment

| Priority | Task | Reference Document |
|----------|------|-------------------|
| 1 | Connect frontend to BFF | Remove MSW, update base URL |
| 2 | Database migrations | 05-database-schema.md SQL DDL |
| 3 | End-to-end testing | 10-implementation-guide-frontend.md Section 12.13 |
| 4 | Production build | docs/DEPLOYMENT.md |

---

## 6. Document Reference Map

### Where to Find What

| I Need To... | Go To Document |
|--------------|----------------|
| Set up the project | 10-implementation-guide-frontend.md Section 5 |
| Configure FluentUI theme | 10-implementation-guide-frontend.md Section 9 |
| Set up Redux store | 09-redux-architecture.md |
| Create RTK Query APIs | 10-implementation-guide-frontend.md Section 7 |
| Implement MSW handlers | 10-implementation-guide-frontend.md Section 8 |
| Build UI components | 10-implementation-guide-frontend.md Section 10 |
| Create page routes | 10-implementation-guide-frontend.md Section 11 |
| Set up .NET backend | 11-implementation-guide-backend.md |
| Understand database schema | 05-database-schema.md |
| Know API endpoints | 06-api-contracts.md or docs/API-QUICK-REFERENCE.md |
| Understand security | 08-security-design.md |
| Deploy to production | docs/DEPLOYMENT.md |

---

## 7. Test Accounts (MSW Development)

| Email | Password | AzureTag | Has Accounts |
|-------|----------|----------|--------------|
| john@example.com | Test123! | @johnsmith | Yes (2 accounts) |
| jane@example.com | Test123! | @janesmith | Yes (1 account) |
| mike@example.com | Test123! | @mikebrown | Yes (1 account) |

---

## 8. API Endpoint Quick Reference

### BFF Authentication
```
POST /bff/auth/login      # Login, returns session cookie
POST /bff/auth/logout     # Logout, clears session
GET  /bff/auth/me         # Get current user + session info
POST /bff/auth/verify-pin # Step-up auth for sensitive ops
GET  /bff/auth/session-status # Check session validity
```

### Accounts (proxied through BFF)
```
GET  /api/accounts        # Get user's accounts
POST /api/accounts        # Create new account
GET  /api/accounts/{id}   # Get account by ID
POST /api/accounts/{id}/set-primary  # Set as primary
GET  /api/accounts/{id}/balance?at={datetime}  # Historical balance
```

### Transactions
```
GET  /api/transactions?accountId=&from=&to=&type=&page=&pageSize=
POST /api/transactions/deposit
POST /api/transactions/withdraw
```

### Transfers
```
POST /api/transfers          # External transfer (by AzureTag)
POST /api/transfers/internal # Internal transfer (own accounts)
GET  /api/recipients/search?q=  # Search recipients
GET  /api/recipients/validate/{azureTag}  # Validate recipient
```

---

## 9. Common Pitfalls to Avoid

### Frontend

| Pitfall | Correct Approach |
|---------|------------------|
| Storing JWT in localStorage/Redux | Don't store tokens - BFF handles them |
| Forgetting `credentials: 'include'` | Always include for session cookie |
| Using `any` types | Use strict TypeScript, define all interfaces |
| Not handling 401 errors | Global 401 handler dispatches clearAuth |
| Hardcoding API URLs | Use environment variables |

### Backend

| Pitfall | Correct Approach |
|---------|------------------|
| CORS on backend API | No CORS needed - only BFF accesses API |
| Sending JWT to browser | Store in server-side session |
| Raw SQL queries | Use EF Core parameterized queries |
| Weak password hashing | Use Argon2id with Identity |
| Missing validation | Use FluentValidation on all requests |

---

## 10. Getting Started Checklist

```
□ Install Bun: powershell -c "irm bun.sh/install.ps1 | iex"
□ Install .NET 10 SDK
□ Install SQL Server (or use Docker)
□ Clone/create repository
□ Read 10-implementation-guide-frontend.md (primary frontend reference)
□ Read 11-implementation-guide-backend.md (primary backend reference)
□ Create Vite project with React + TypeScript
□ Install dependencies from 16-package-manifest.md
□ Start with Phase 1 tasks above
```

---

## 11. Success Criteria

The implementation is complete when:

- [ ] User can register with unique AzureTag
- [ ] User can login/logout with session persistence
- [ ] User can create multiple accounts
- [ ] User can deposit/withdraw funds
- [ ] User can transfer internally between own accounts
- [ ] User can transfer externally via AzureTag
- [ ] User can view transaction history with filters
- [ ] User can query historical balance
- [ ] All operations work on mobile and desktop
- [ ] No JWT tokens exposed to browser (BFF pattern)
- [ ] All data validated on backend
- [ ] Error messages are user-friendly

---

**Document Status**: READY FOR IMPLEMENTATION
**Last Updated**: 2026-01-09
