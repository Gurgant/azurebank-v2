# AzureBank Architecture Overview

This document provides a comprehensive overview of the AzureBank backend architecture.

---

## Table of Contents

- [System Context](#system-context)
- [Container View](#container-view)
- [Component Views](#component-views)
- [Data Flow](#data-flow)
- [Security Architecture](#security-architecture)
- [Database Design](#database-design)

---

## System Context

The AzureBank system provides banking services to customers through web and mobile applications.

```mermaid
flowchart LR
    subgraph External["External Systems"]
        Email["📧 Email System<br/><i>Sends notifications</i>"]
        IdP["🔐 Identity Provider<br/><i>Optional OAuth2</i>"]
    end

    Customer["👤 Bank Customer<br/><i>End user with accounts</i>"]

    subgraph AzureBank["🏦 AzureBank System"]
        System["Banking Platform<br/><i>Manage accounts, transactions,<br/>and transfers</i>"]
    end

    Customer -->|"HTTPS"| System
    System -->|"SMTP"| Email
    System -.->|"OAuth2"| IdP

    style Customer fill:#08427b,color:#fff
    style System fill:#1168bd,color:#fff
    style Email fill:#999,color:#fff
    style IdP fill:#999,color:#fff
```

### External Actors

| Actor | Description |
|-------|-------------|
| **Bank Customer** | End user accessing banking services |
| **Email System** | External email provider for notifications |
| **Identity Provider** | Optional external authentication (future) |

---

## Container View

The system is composed of multiple containers (deployable units):

```mermaid
flowchart TB
    Customer["👤 Bank Customer"]

    subgraph Frontend["Frontend Layer"]
        SPA["⚛️ Single Page App<br/><i>React / TypeScript</i>"]
    end

    subgraph Backend["Backend Layer"]
        BFF["🔀 BFF Gateway<br/><i>ASP.NET Core + YARP</i><br/>Port 7216"]
        API["⚙️ REST API<br/><i>ASP.NET Core</i><br/>Port 7215"]
    end

    subgraph Data["Data Layer"]
        DB[("🗄️ SQL Server<br/><i>Accounts, Users,<br/>Transactions</i>")]
        Session[("💾 Session Store<br/><i>In-Memory / Redis</i>")]
    end

    Customer -->|"HTTPS"| SPA
    SPA -->|"HTTPS + Cookie"| BFF
    BFF -->|"HTTPS + Bearer"| API
    BFF <-->|"Sessions"| Session
    API -->|"EF Core"| DB

    style Customer fill:#08427b,color:#fff
    style SPA fill:#438dd5,color:#fff
    style BFF fill:#1168bd,color:#fff
    style API fill:#1168bd,color:#fff
    style DB fill:#438dd5,color:#fff
    style Session fill:#438dd5,color:#fff
```

### Container Descriptions

| Container | Technology | Responsibility |
|-----------|------------|----------------|
| **SPA** | React, TypeScript | User interface (future) |
| **BFF Gateway** | ASP.NET Core, YARP 2.3.0 | Session management, rate limiting, security headers, reverse proxy |
| **REST API** | ASP.NET Core 10.0 | Business logic, validation, authentication, data access |
| **Database** | SQL Server 2022 | Persistent storage for all domain data |
| **Session Store** | In-Memory (Redis capable) | User session and token storage |

---

## Component Views

### API Components

| Layer | Components | Purpose |
|-------|------------|---------|
| **Controllers** | AuthController, AccountController, TransactionController, TransferController, UserController | HTTP endpoints |
| **Services** | AuthService, AccountService, TransactionService, TransferService, UserService, JwtService | Business logic |
| **Validation** | FluentValidation validators | Request validation |
| **Data Access** | AzureBankDbContext, Mapperly mappers | Database operations |

```mermaid
graph TD
    A[Controllers] --> B[Services]
    A --> C[Validators]
    B --> D[DbContext]
    D --> E[(SQL Server)]
```

### BFF Components

| Layer | Components | Purpose |
|-------|------------|---------|
| **Middleware** | SecurityHeaders, RateLimiter, SessionActivity, AuthLevel | Request pipeline |
| **Controllers** | BffAuthController | Session endpoints |
| **Services** | SessionService, TokenStore, CleanupService | Session management |
| **Proxy** | YARP ReverseProxy, BearerTokenTransform | API forwarding |

```mermaid
graph TD
    A[Client] --> B[Middleware]
    B --> C[YARP Proxy]
    B --> D[BffAuthController]
    C --> E[Backend API]
    D --> F[SessionService]
```

---

## Data Flow

### Authentication Flow

**Registration:**
1. Client → BFF: `POST /bff/auth/register`
2. BFF → API: Forward request
3. API: Hash password (Argon2id), create user + account
4. API → BFF: Return token + user data
5. BFF: Store token in session
6. BFF → Client: Set-Cookie + user data

**Login:**
1. Client → BFF: `POST /bff/auth/login`
2. BFF → API: Forward credentials
3. API: Verify password, generate JWT
4. BFF: Create session, store token
5. BFF → Client: Set-Cookie + user data

```mermaid
graph LR
    Client -->|Cookie| BFF
    BFF -->|Bearer JWT| API
    API --> DB[(Database)]
```

### Transaction Flow

**Deposit/Withdraw:**
1. Client → BFF: `POST /api/transactions/deposit`
2. BFF: Get JWT from session, attach as Bearer
3. BFF → API: Forward with Bearer token
4. API: Validate, update balance, create record
5. API → BFF → Client: Transaction result

```mermaid
graph LR
    C[Client] -->|1. Request| BFF
    BFF -->|2. Add Bearer| API
    API -->|3. Update| DB[(Database)]
    DB -->|4. Result| API
    API -->|5. Response| BFF
    BFF -->|6. Response| C
```

### Transfer Flow (with Step-Up Auth)

**Step 1: PIN Verification Required**
1. Client → BFF: `POST /api/transfers`
2. BFF: Check AuthLevel = 1 (insufficient)
3. BFF → Client: `403 STEP_UP_REQUIRED`

**Step 2: Verify PIN**
4. Client → BFF: `POST /bff/auth/verify-pin`
5. BFF → API: Verify PIN hash
6. BFF: Upgrade session to AuthLevel 2
7. BFF → Client: `{authLevel: 2}`

**Step 3: Execute Transfer**
8. Client → BFF: `POST /api/transfers` (retry)
9. BFF: AuthLevel = 2 ✓, forward with Bearer
10. API: Debit sender, credit recipient
11. API → BFF → Client: Transfer result

```mermaid
graph TD
    A[Transfer Request] --> B{AuthLevel?}
    B -->|Level 1| C[403 Step-Up Required]
    C --> D[Verify PIN]
    D --> E[Upgrade to Level 2]
    E --> A
    B -->|Level 2| F[Execute Transfer]
    F --> G[Success]
```

---

## Security Architecture

### Defense in Depth

```mermaid
graph TD
    L1[Layer 1: Network] --> L2[Layer 2: Gateway]
    L2 --> L3[Layer 3: Authentication]
    L3 --> L4[Layer 4: Authorization]
    L4 --> L5[Layer 5: Data]
```

| Layer | Features |
|-------|----------|
| **1. Network** | HTTPS/TLS 1.3, CORS Policy |
| **2. Gateway** | Rate Limiting (100 req/min), Security Headers, HTTP-Only Cookies |
| **3. Authentication** | JWT Validation, Session Management, Step-Up Auth (PIN) |
| **4. Authorization** | Role-Based Access, Resource Ownership, Auth Level Check |
| **5. Data** | Argon2id Passwords, Data Encryption, Immutable Audit Trail |

### Security Features by Layer

| Layer | Feature | Implementation |
|-------|---------|----------------|
| **Network** | TLS | HTTPS enforced |
| **Network** | CORS | Origin whitelist |
| **Gateway** | Rate Limiting | 100 req/min fixed window |
| **Gateway** | Security Headers | OWASP recommended |
| **Gateway** | Cookie Security | HTTP-only, Secure, SameSite=Strict |
| **Auth** | Token Management | JWT (15 min) + Refresh (60 min) |
| **Auth** | Session | Server-side with crypto ID |
| **Auth** | Step-Up | PIN for sensitive operations |
| **Authz** | Access Control | User owns resources |
| **Data** | Passwords | Argon2id hashing |
| **Data** | Audit | Immutable transactions |

---

## Database Design

### Entity Relationship Diagram

```mermaid
erDiagram
    AspNetUsers ||--o{ Accounts : "owns"
    AspNetUsers ||--o{ RefreshTokens : "has"
    Accounts ||--o{ Transactions : "contains"

    AspNetUsers {
        uniqueidentifier Id PK
        nvarchar Email UK
        nvarchar AzureTag UK
        nvarchar PasswordHash
        nvarchar PinHash
        nvarchar FirstName
        nvarchar LastName
        datetime2 CreatedAt
        datetime2 UpdatedAt
    }

    Accounts {
        uniqueidentifier Id PK
        uniqueidentifier UserId FK
        nvarchar AccountNumber UK
        nvarchar Name
        int Type
        decimal Balance
        bit IsPrimary
        bit IsDeleted
        rowversion RowVersion
        datetime2 CreatedAt
        datetime2 UpdatedAt
    }

    Transactions {
        uniqueidentifier Id PK
        uniqueidentifier AccountId FK
        int Type
        decimal Amount
        int Status
        nvarchar Description
        datetime2 CreatedAt
    }

    RefreshTokens {
        uniqueidentifier Id PK
        uniqueidentifier UserId FK
        nvarchar TokenHash
        datetime2 ExpiresAt
        datetime2 RevokedAt
        datetime2 CreatedAt
    }
```

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **UUID v7 for IDs** | Time-ordered, globally unique, no coordination |
| **Soft Delete** | Preserve account history, regulatory compliance |
| **Optimistic Concurrency** | RowVersion prevents lost updates |
| **Immutable Transactions** | Audit trail integrity |
| **Decimal Precision** | Balance (19,4), Amount (18,2) for currency |

---

## Related Documentation

- [ADR-0001: BFF Pattern](../adr/0001-bff-pattern.md)
- [ADR-0002: YARP Proxy](../adr/0002-yarp-proxy.md)
- [ADR-0003: Argon2id Hashing](../adr/0003-argon2id-password-hashing.md)
- [Database Schema](./database-schema.md)
- [API Reference](https://localhost:7215/scalar/v1)
