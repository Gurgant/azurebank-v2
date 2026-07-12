# Constraints Validation Document
## Bank Account Management System - Dev4Side Technical Test

**Document Version**: 1.0
**Created**: 2025-12-16
**Author**: System Architect
**Status**: VALIDATED

---

## 1. HARD CONSTRAINTS (Non-Negotiable)

### 1.1 Backend Constraints

| Constraint | Specification | Source | Validation Status |
|------------|---------------|--------|-------------------|
| Runtime | .NET 10 | Technical Test | VALIDATED |
| Database | Microsoft SQL Server (Latest) | Technical Test | VALIDATED |
| Authentication | JWT | Technical Test | VALIDATED |
| Password Storage | Secure hashing (bcrypt/Argon2) | Technical Test | VALIDATED |
| API Style | REST with JSON | Technical Test | VALIDATED |

### 1.2 Frontend Constraints

| Constraint | Specification | Source | Validation Status |
|------------|---------------|--------|-------------------|
| Framework | React 19.x | Technical Test | VALIDATED |
| Language | TypeScript (Strict Mode) | Technical Test | VALIDATED |
| UI Library | @fluentui/react-components (Latest) | Prompt Requirement | VALIDATED |
| Global State | Redux Toolkit + RTK Query | Prompt Requirement | VALIDATED |
| Local State | React Hooks (Functional Only) | Technical Test | VALIDATED |
| Components | Functional Components Only | Technical Test | VALIDATED |
| Responsive | Mobile + Desktop (Mobile-first) | Prompt Requirement | VALIDATED |

### 1.3 Documentation Constraints

| Constraint | Specification | Source | Validation Status |
|------------|---------------|--------|-------------------|
| README.md | Required - Build, Setup, Run instructions | Technical Test | VALIDATED |

---

## 2. FUNCTIONAL REQUIREMENTS

### 2.1 Authentication & Security
| Requirement | Description | Priority |
|-------------|-------------|----------|
| User Registration | New user account creation | P0 - Must Have |
| User Login | JWT-based authentication | P0 - Must Have |
| Protected Routes | All APIs except register/login | P0 - Must Have |
| Secure Password Storage | Hash passwords safely | P0 - Must Have |

### 2.2 Bank Account Management
| Requirement | Description | Priority |
|-------------|-------------|----------|
| Account Creation | Open new account with initial balance (can be zero) | P0 - Must Have |
| Unique Account Number | Each account has unique identifier | P0 - Must Have |
| Account Ownership | Each account has an owner | P0 - Must Have |

### 2.3 Balance Operations
| Requirement | Description | Priority |
|-------------|-------------|----------|
| View Current Balance | Display current account balance | P0 - Must Have |
| View Historical Balance | Balance at specific datetime | P1 - Should Have |
| Deposits | Add money to account | P0 - Must Have |
| Withdrawals | Remove money with overdraw validation | P0 - Must Have |

### 2.4 Transactions
| Requirement | Description | Priority |
|-------------|-------------|----------|
| Transaction History | View by time window (30, 90 days, etc.) | P0 - Must Have |
| Money Transfer | Transfer to another account by account number | P0 - Must Have |

### 2.5 Frontend Views
| Requirement | Description | Priority |
|-------------|-------------|----------|
| Login Page | User authentication | P0 - Must Have |
| Registration Page | New user signup | P0 - Must Have |
| Dashboard | Current balance display | P0 - Must Have |
| Transaction History | Last 10-20 transactions | P0 - Must Have |
| Transaction Form | Deposit/Withdrawal/Transfer | P0 - Must Have |

---

## 3. IMPLICIT CONSTRAINTS (Derived from Best Practices)

### 3.1 Security
- OWASP Top 10 compliance
- HTTPS for all communications (production)
- Input validation on all endpoints
- SQL injection prevention via parameterized queries
- XSS prevention in frontend
- CSRF protection

### 3.2 Code Quality
- Clean code principles
- Separation of concerns
- Proper error handling
- Unit testable architecture

### 3.3 Performance
- Efficient database queries
- Proper indexing
- Pagination for transaction history

---

## 4. EXPLICIT NON-REQUIREMENTS

| Item | Statement | Source |
|------|-----------|--------|
| Complex State Management | Not needed (but we use Redux per prompt) | Technical Test |
| Responsive Design | Desktop-only is fine (but we do mobile-first per prompt) | Technical Test |
| Complete All Features | NOT mandatory | Technical Test |

**Note**: The prompt requirements OVERRIDE the "not needed" statements. We WILL implement:
- Redux Toolkit + RTK Query for state management
- Mobile-first responsive design

---

## 5. TECHNOLOGY STACK SUMMARY

```
FRONTEND
├── React 19.x
├── TypeScript (strict)
├── @fluentui/react-components
├── Redux Toolkit
├── RTK Query
├── MSW (Mock Service Worker) - for frontend-first development
└── React Router v6+

BACKEND
├── .NET 10
├── ASP.NET Core Web API
├── Entity Framework Core
├── JWT Bearer Authentication
├── Argon2/bcrypt for password hashing
└── Microsoft SQL Server

DEVELOPMENT TOOLS
├── MSW for API mocking
├── Swagger/OpenAPI for documentation
└── Git for version control
```

---

## 6. ARCHITECTURE DECISIONS PREVIEW

### 6.1 State Management Strategy
```
REDUX STORE (Global State)
├── authSlice
│   ├── accessToken
│   ├── user
│   └── isAuthenticated
│
└── RTK Query APIs
    ├── accountApi (getAccount, getBalance, getBalanceAtTime)
    ├── transactionApi (getTransactions, deposit, withdraw)
    └── transferApi (transfer)

LOCAL STATE (React Hooks)
├── Form inputs (useState)
├── Modal visibility (useState)
└── UI toggles (useState)
```

### 6.2 Frontend-First Development
```
Phase 1: Frontend + MSW Mocks
[React] -> [RTK Query] -> [MSW Handlers] -> [Mock Data]

Phase 2: Connect Real Backend
[React] -> [RTK Query] -> [.NET API] -> [SQL Server]
```

---

## 7. CONSTRAINT CONFLICTS & RESOLUTIONS

| Conflict | Resolution | Rationale |
|----------|------------|-----------|
| Test says "no complex state" vs Prompt requires Redux | Use Redux Toolkit | Prompt requirements take precedence |
| Test says "desktop-only fine" vs Prompt requires mobile | Implement mobile-first | Prompt requirements take precedence |
| Quality over quantity vs all features | Prioritize P0 features first | Align with test philosophy |

---

## 8. VALIDATION CHECKLIST

- [x] All hard constraints documented
- [x] All functional requirements extracted
- [x] Technology stack validated against constraints
- [x] Conflicts identified and resolved
- [x] Priority levels assigned
- [x] Implicit requirements documented
- [x] Non-requirements clarified

---

## 9. SIGN-OFF

| Role | Status | Date |
|------|--------|------|
| System Architect | APPROVED | 2025-12-16 |
| Backend Lead | PENDING | - |
| Frontend Lead | PENDING | - |
| Security Specialist | PENDING | - |
| Database Engineer | PENDING | - |

---

**Document Status**: COMPLETE - Ready for Phase 1
