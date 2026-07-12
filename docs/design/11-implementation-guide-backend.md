# Backend Implementation Guide
## AzureBank - Bank Account Management System

**Document Version**: 2.1
**Created**: 2025-12-16
**Updated**: 2026-01-08
**Author**: Backend Lead / Integrator
**Status**: UPDATED - Added ASP.NET Core Identity (Phase 7 + Identity Update)

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Project Setup](#2-project-setup)
3. [Solution Structure](#3-solution-structure)
4. [Shared Library (AzureBank.Shared)](#4-shared-library-azurebankshared)
5. [EF Core Configuration](#5-ef-core-configuration)
6. [Backend API (AzureBank.Api)](#6-backend-api-azurebankapi)
7. [BFF Gateway (AzureBank.Bff)](#7-bff-gateway-azurebankbff)
8. [Middleware & Cross-Cutting Concerns](#8-middleware--cross-cutting-concerns)
9. [Configuration Files](#9-configuration-files)
10. [Implementation Checklist](#10-implementation-checklist)

---

## 1. Prerequisites

### 1.1 Required Software

| Software | Version | Purpose |
|----------|---------|---------|
| .NET SDK | 10.0+ | Runtime and build tools |
| Visual Studio 2025 / VS Code | Latest | IDE |
| SQL Server | 2022+ | Database (or LocalDB for dev) |
| Docker Desktop | Latest | Optional - for containerized development |
| Git | 2.40+ | Version control |

### 1.2 Verify Installation

```powershell
# Check .NET version
dotnet --version
# Expected: 10.0.100 or higher

# Check SQL Server (if using LocalDB)
sqllocaldb info
# Should list MSSQLLocalDB

# Check available SDKs
dotnet --list-sdks
```

### 1.3 Development Tools

| Tool | Purpose | Installation |
|------|---------|--------------|
| EF Core CLI | Database migrations | `dotnet tool install --global dotnet-ef` |
| Scalar CLI | API documentation | Built into project |
| SQLite Browser | Optional - view database | Download from sqlitebrowser.org |

---

## 2. Project Setup

### 2.1 Create Solution

```powershell
# Navigate to project root
cd "c:\Users\ellen\OneDrive\Desktop\React Projects\BankApp"

# Create backend folder structure
mkdir backend
cd backend

# Create solution file
dotnet new sln -n AzureBank

# Create projects
# 1. Shared Library (Class Library)
dotnet new classlib -n AzureBank.Shared -o src/AzureBank.Shared -f net10.0

# 2. Backend API (Web API)
dotnet new webapi -n AzureBank.Api -o src/AzureBank.Api -f net10.0 --no-https false

# 3. BFF Gateway (Web API with YARP)
dotnet new webapi -n AzureBank.Bff -o src/AzureBank.Bff -f net10.0 --no-https false

# 4. Test Project (xUnit)
dotnet new xunit -n AzureBank.Tests -o tests/AzureBank.Tests -f net10.0

# Add projects to solution
dotnet sln add src/AzureBank.Shared/AzureBank.Shared.csproj
dotnet sln add src/AzureBank.Api/AzureBank.Api.csproj
dotnet sln add src/AzureBank.Bff/AzureBank.Bff.csproj
dotnet sln add tests/AzureBank.Tests/AzureBank.Tests.csproj

# Add project references
# API references Shared
dotnet add src/AzureBank.Api/AzureBank.Api.csproj reference src/AzureBank.Shared/AzureBank.Shared.csproj

# BFF references Shared
dotnet add src/AzureBank.Bff/AzureBank.Bff.csproj reference src/AzureBank.Shared/AzureBank.Shared.csproj

# Tests reference all projects
dotnet add tests/AzureBank.Tests/AzureBank.Tests.csproj reference src/AzureBank.Shared/AzureBank.Shared.csproj
dotnet add tests/AzureBank.Tests/AzureBank.Tests.csproj reference src/AzureBank.Api/AzureBank.Api.csproj
dotnet add tests/AzureBank.Tests/AzureBank.Tests.csproj reference src/AzureBank.Bff/AzureBank.Bff.csproj
```

### 2.2 Install NuGet Packages

#### AzureBank.Shared (Class Library)

```powershell
cd src/AzureBank.Shared

# No external packages needed - just models, DTOs, constants
# This keeps the shared library lightweight
```

#### AzureBank.Api (Backend API)

```powershell
cd src/AzureBank.Api

# Entity Framework Core
dotnet add package Microsoft.EntityFrameworkCore --version 10.*
dotnet add package Microsoft.EntityFrameworkCore.SqlServer --version 10.*
dotnet add package Microsoft.EntityFrameworkCore.Design --version 10.*

# ASP.NET Core Identity & Authentication
dotnet add package Microsoft.AspNetCore.Identity.EntityFrameworkCore --version 10.*
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer --version 10.*

# Validation
dotnet add package FluentValidation.AspNetCore --version 11.*

# API Documentation (Scalar, NOT Swagger)
dotnet add package Scalar.AspNetCore --version 1.*

# Logging & Observability
dotnet add package Serilog.AspNetCore --version 8.*
dotnet add package Serilog.Formatting.Compact --version 2.*
dotnet add package Serilog.Enrichers.Environment --version 2.*

# OpenTelemetry
dotnet add package OpenTelemetry.Extensions.Hosting --version 1.*
dotnet add package OpenTelemetry.Instrumentation.AspNetCore --version 1.*
dotnet add package OpenTelemetry.Instrumentation.Http --version 1.*
dotnet add package OpenTelemetry.Instrumentation.SqlClient --version 1.*
dotnet add package OpenTelemetry.Instrumentation.EntityFrameworkCore --version 1.*
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol --version 1.*

# Object Mapping
dotnet add package Riok.Mapperly --version 4.*
```

#### AzureBank.Bff (BFF Gateway)

```powershell
cd src/AzureBank.Bff

# YARP Reverse Proxy
dotnet add package Yarp.ReverseProxy --version 2.*

# Session Management
dotnet add package Microsoft.AspNetCore.DataProtection --version 10.*

# Rate Limiting (built-in .NET 8+)
# No package needed - part of Microsoft.AspNetCore.RateLimiting

# HTTP Client (for backend communication)
dotnet add package Microsoft.Extensions.Http.Polly --version 8.*

# Logging (same as API)
dotnet add package Serilog.AspNetCore --version 8.*
dotnet add package Serilog.Formatting.Compact --version 2.*

# OpenTelemetry (same as API)
dotnet add package OpenTelemetry.Extensions.Hosting --version 1.*
dotnet add package OpenTelemetry.Instrumentation.AspNetCore --version 1.*
dotnet add package OpenTelemetry.Instrumentation.Http --version 1.*
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol --version 1.*
```

#### AzureBank.Tests (Test Project)

```powershell
cd tests/AzureBank.Tests

# Testing
dotnet add package Microsoft.NET.Test.Sdk --version 17.*
dotnet add package xunit --version 2.*
dotnet add package xunit.runner.visualstudio --version 2.*
dotnet add package Moq --version 4.*
dotnet add package FluentAssertions --version 6.*

# Integration Testing
dotnet add package Microsoft.AspNetCore.Mvc.Testing --version 10.*
dotnet add package Microsoft.EntityFrameworkCore.InMemory --version 10.*
```

---

## 3. Solution Structure

### 3.1 Complete Directory Layout

```
backend/
├── AzureBank.sln                           # Solution file
│
├── src/
│   │
│   ├── AzureBank.Shared/                   # Shared Class Library
│   │   ├── AzureBank.Shared.csproj
│   │   │
│   │   ├── Entities/                       # Domain Entities (EF Core)
│   │   │   ├── User.cs
│   │   │   ├── Account.cs
│   │   │   ├── Transaction.cs
│   │   │   ├── RefreshToken.cs
│   │   │   └── BaseEntity.cs               # Common entity properties
│   │   │
│   │   ├── DTOs/                           # Data Transfer Objects
│   │   │   ├── Auth/
│   │   │   │   ├── LoginRequest.cs
│   │   │   │   ├── LoginResponse.cs
│   │   │   │   ├── RegisterRequest.cs
│   │   │   │   └── VerifyPinRequest.cs
│   │   │   │
│   │   │   ├── Account/
│   │   │   │   ├── AccountDto.cs
│   │   │   │   ├── CreateAccountRequest.cs
│   │   │   │   ├── UpdateAccountRequest.cs
│   │   │   │   └── AccountSummaryDto.cs
│   │   │   │
│   │   │   ├── Transaction/
│   │   │   │   ├── TransactionDto.cs
│   │   │   │   ├── DepositRequest.cs
│   │   │   │   ├── WithdrawRequest.cs
│   │   │   │   └── TransactionFilterDto.cs
│   │   │   │
│   │   │   ├── Transfer/
│   │   │   │   ├── TransferRequest.cs
│   │   │   │   ├── InternalTransferRequest.cs
│   │   │   │   └── TransferResponse.cs
│   │   │   │
│   │   │   ├── User/
│   │   │   │   ├── UserDto.cs
│   │   │   │   ├── RecipientDto.cs
│   │   │   │   └── UserSearchRequest.cs
│   │   │   │
│   │   │   └── Common/
│   │   │       ├── ApiResponse.cs          # Wrapper: { data, message }
│   │   │       ├── ErrorResponse.cs        # Error format
│   │   │       └── PaginatedResponse.cs    # Pagination wrapper
│   │   │
│   │   ├── Constants/                      # Application constants
│   │   │   ├── TransactionTypes.cs
│   │   │   ├── AccountTypes.cs
│   │   │   ├── ErrorCodes.cs
│   │   │   └── ValidationRules.cs
│   │   │
│   │   ├── Enums/                          # Strongly-typed enums
│   │   │   ├── TransactionType.cs
│   │   │   ├── AccountType.cs
│   │   │   └── TransactionStatus.cs
│   │   │
│   │   └── Exceptions/                     # Custom exceptions
│   │       ├── AppException.cs             # Base exception
│   │       ├── NotFoundException.cs
│   │       ├── BusinessRuleException.cs
│   │       ├── InsufficientFundsException.cs
│   │       ├── AuthenticationException.cs
│   │       ├── AuthorizationException.cs
│   │       └── ConflictException.cs
│   │
│   │
│   ├── AzureBank.Api/                      # Backend REST API
│   │   ├── AzureBank.Api.csproj
│   │   ├── Program.cs                      # Application entry point
│   │   │
│   │   ├── Controllers/                    # API Controllers
│   │   │   ├── AuthController.cs           # POST /api/auth/login, register
│   │   │   ├── AccountsController.cs       # CRUD for accounts
│   │   │   ├── TransactionsController.cs   # Deposits, withdrawals, history
│   │   │   ├── TransfersController.cs      # External & internal transfers
│   │   │   └── UsersController.cs          # User search (for transfers)
│   │   │
│   │   ├── Services/                       # Business Logic
│   │   │   ├── Interfaces/
│   │   │   │   ├── IAuthService.cs
│   │   │   │   ├── IAccountService.cs
│   │   │   │   ├── ITransactionService.cs
│   │   │   │   ├── ITransferService.cs
│   │   │   │   ├── IUserService.cs
│   │   │   │   └── IPasswordHasher.cs
│   │   │   │
│   │   │   └── Implementations/
│   │   │       ├── AuthService.cs
│   │   │       ├── AccountService.cs
│   │   │       ├── TransactionService.cs
│   │   │       ├── TransferService.cs
│   │   │       ├── UserService.cs
│   │   │       ├── PasswordHasher.cs       # Argon2id implementation
│   │   │       └── JwtService.cs           # JWT generation
│   │   │
│   │   ├── Data/                           # Database Layer
│   │   │   ├── AzureBankDbContext.cs       # EF Core DbContext
│   │   │   ├── Configurations/             # Fluent API configurations
│   │   │   │   ├── UserConfiguration.cs
│   │   │   │   ├── AccountConfiguration.cs
│   │   │   │   └── TransactionConfiguration.cs
│   │   │   │
│   │   │   ├── Repositories/               # Repository pattern (optional)
│   │   │   │   ├── IRepository.cs
│   │   │   │   └── Repository.cs
│   │   │   │
│   │   │   └── Seed/
│   │   │       └── DatabaseSeeder.cs       # Development seed data
│   │   │
│   │   ├── Validators/                     # FluentValidation validators
│   │   │   ├── Auth/
│   │   │   │   ├── LoginRequestValidator.cs
│   │   │   │   └── RegisterRequestValidator.cs
│   │   │   │
│   │   │   ├── Account/
│   │   │   │   └── CreateAccountValidator.cs
│   │   │   │
│   │   │   ├── Transaction/
│   │   │   │   ├── DepositRequestValidator.cs
│   │   │   │   └── WithdrawRequestValidator.cs
│   │   │   │
│   │   │   └── Transfer/
│   │   │       ├── TransferRequestValidator.cs
│   │   │       └── InternalTransferValidator.cs
│   │   │
│   │   ├── Mappers/                        # Mapperly mappers
│   │   │   ├── UserMapper.cs
│   │   │   ├── AccountMapper.cs
│   │   │   └── TransactionMapper.cs
│   │   │
│   │   ├── Middleware/                     # Custom middleware
│   │   │   ├── GlobalExceptionMiddleware.cs
│   │   │   ├── CorrelationIdMiddleware.cs
│   │   │   └── RequestLoggingMiddleware.cs
│   │   │
│   │   ├── Extensions/                     # Service extensions
│   │   │   ├── ServiceCollectionExtensions.cs
│   │   │   └── ApplicationBuilderExtensions.cs
│   │   │
│   │   ├── appsettings.json               # Production settings
│   │   └── appsettings.Development.json   # Development settings
│   │
│   │
│   └── AzureBank.Bff/                      # BFF Gateway
│       ├── AzureBank.Bff.csproj
│       ├── Program.cs                      # BFF entry point
│       │
│       ├── Controllers/                    # BFF-specific controllers
│       │   └── BffAuthController.cs        # /bff/auth/* endpoints
│       │
│       ├── Services/                       # BFF services
│       │   ├── Interfaces/
│       │   │   ├── ISessionService.cs
│       │   │   └── ITokenStoreService.cs
│       │   │
│       │   └── Implementations/
│       │       ├── SessionService.cs       # Session management
│       │       └── InMemoryTokenStore.cs   # MVP: in-memory token storage
│       │
│       ├── Transforms/                     # YARP transforms
│       │   └── BearerTokenTransform.cs     # Add JWT to proxied requests
│       │
│       ├── Middleware/                     # BFF middleware
│       │   ├── SessionValidationMiddleware.cs
│       │   └── SecurityHeadersMiddleware.cs
│       │
│       ├── appsettings.json               # BFF production settings
│       └── appsettings.Development.json   # BFF development settings
│
│
└── tests/
    └── AzureBank.Tests/                    # Test Project
        ├── AzureBank.Tests.csproj
        │
        ├── Unit/                           # Unit tests
        │   ├── Services/
        │   │   ├── AccountServiceTests.cs
        │   │   ├── TransactionServiceTests.cs
        │   │   └── PasswordHasherTests.cs
        │   │
        │   └── Validators/
        │       ├── LoginRequestValidatorTests.cs
        │       └── TransferRequestValidatorTests.cs
        │
        ├── Integration/                    # Integration tests
        │   ├── Controllers/
        │   │   ├── AuthControllerTests.cs
        │   │   └── AccountsControllerTests.cs
        │   │
        │   └── TestWebApplicationFactory.cs
        │
        └── TestData/
            └── TestDataBuilder.cs          # Test data helpers
```

### 3.2 Project Dependencies Diagram

```
                    ┌─────────────────────┐
                    │  AzureBank.Tests    │
                    │    (Test Project)   │
                    └─────────┬───────────┘
                              │ references
           ┌──────────────────┼──────────────────┐
           │                  │                  │
           ▼                  ▼                  ▼
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│  AzureBank.Bff  │  │  AzureBank.Api  │  │ AzureBank.Shared│
│   (BFF Gateway) │  │   (REST API)    │  │ (Class Library) │
└────────┬────────┘  └────────┬────────┘  └─────────────────┘
         │                    │                    ▲
         │                    │                    │
         └────────────────────┴────────────────────┘
                    references
```

### 3.3 Runtime Communication

```
┌─────────────────────────────────────────────────────────────────────┐
│                       RUNTIME ARCHITECTURE                           │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│   Browser                    Port 5001              Port 5002       │
│  (React App)                                                        │
│     │                                                               │
│     │  HTTP Request          ┌─────────────────┐                    │
│     │  Cookie: Session       │                 │                    │
│     └───────────────────────▶│  AzureBank.Bff  │                    │
│                              │  (BFF Gateway)  │                    │
│                              │                 │                    │
│                              └────────┬────────┘                    │
│                                       │                             │
│                                       │ HTTP + Bearer Token         │
│                                       │ (YARP Proxy)               │
│                                       │                             │
│                                       ▼                             │
│                              ┌─────────────────┐                    │
│                              │                 │                    │
│                              │  AzureBank.Api  │                    │
│                              │  (REST API)     │                    │
│                              │                 │                    │
│                              └────────┬────────┘                    │
│                                       │                             │
│                                       │ EF Core                     │
│                                       │                             │
│                                       ▼                             │
│                              ┌─────────────────┐                    │
│                              │   SQL Server    │                    │
│                              │   (Database)    │                    │
│                              └─────────────────┘                    │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
```

### 3.4 Port Configuration

| Project | Development Port | Purpose |
|---------|-----------------|---------|
| AzureBank.Bff | 5001 (HTTPS), 5000 (HTTP) | Frontend communication |
| AzureBank.Api | 5002 (HTTPS), 5003 (HTTP) | BFF communication (internal) |
| Frontend (Vite) | 5173 | React development server |

---

## 4. Shared Library (AzureBank.Shared)

The shared library contains all entities, DTOs, constants, enums, and exceptions that are used by both the API and BFF projects. This keeps the codebase DRY and ensures consistency.

### 4.1 Base Entity

```csharp
// AzureBank.Shared/Entities/BaseEntity.cs
namespace AzureBank.Shared.Entities;

/// <summary>
/// Base class for all entities with common audit properties
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}
```

### 4.2 ApplicationUser Entity (Extends IdentityUser)

```csharp
// AzureBank.Shared/Entities/ApplicationUser.cs
using Microsoft.AspNetCore.Identity;

namespace AzureBank.Shared.Entities;

/// <summary>
/// Custom user entity extending IdentityUser
/// IdentityUser provides: Id, UserName, Email, PasswordHash, PhoneNumber, LockoutEnd, AccessFailedCount, etc.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>
    /// Public username for transfers (e.g., "@johnsmith")
    /// Stored lowercase, IMMUTABLE after registration
    /// Note: Maps to UserName in Identity
    /// </summary>
    public string AzureTag { get; set; } = null!;

    /// <summary>
    /// 6-digit PIN hash for step-up authentication (transfers, sensitive operations)
    /// </summary>
    public string? PinHash { get; set; }

    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;

    /// <summary>
    /// Computed full name
    /// </summary>
    public string FullName => $"{FirstName} {LastName}";

    /// <summary>
    /// Audit: created timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: last modified timestamp
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
}

// Note: IdentityUser<Guid> provides these properties automatically:
// - Id (Guid)
// - UserName (string)
// - NormalizedUserName (string)
// - Email (string)
// - NormalizedEmail (string)
// - EmailConfirmed (bool)
// - PasswordHash (string) - handled by Identity's password hasher
// - SecurityStamp (string)
// - ConcurrencyStamp (string)
// - PhoneNumber (string)
// - PhoneNumberConfirmed (bool)
// - TwoFactorEnabled (bool)
// - LockoutEnd (DateTimeOffset?)
// - LockoutEnabled (bool)
// - AccessFailedCount (int)
```

### 4.2.1 ApplicationRole Entity (Optional Custom Role)

```csharp
// AzureBank.Shared/Entities/ApplicationRole.cs
using Microsoft.AspNetCore.Identity;

namespace AzureBank.Shared.Entities;

/// <summary>
/// Custom role entity (optional - can use IdentityRole<Guid> directly)
/// Use this if you need to add custom properties to roles
/// </summary>
public class ApplicationRole : IdentityRole<Guid>
{
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

### 4.3 Account Entity

```csharp
// AzureBank.Shared/Entities/Account.cs
using AzureBank.Shared.Enums;

namespace AzureBank.Shared.Entities;

/// <summary>
/// Bank account entity
/// </summary>
public class Account : BaseEntity
{
    /// <summary>
    /// Foreign key to account owner
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Human-readable account number: AB-XXXX-XXXX-XX
    /// </summary>
    public string AccountNumber { get; set; } = null!;

    /// <summary>
    /// User-defined account name (e.g., "Main Savings")
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Account type: Checking, Savings, Investment
    /// </summary>
    public AccountType Type { get; set; }

    /// <summary>
    /// Current balance - DECIMAL(19,4)
    /// </summary>
    public decimal Balance { get; set; }

    /// <summary>
    /// Whether this is the primary account for receiving external transfers
    /// Only one account per user can be primary
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Optimistic concurrency token
    /// </summary>
    public byte[] RowVersion { get; set; } = null!;

    // Navigation properties
    public ApplicationUser User { get; set; } = null!;
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
```

### 4.4 Transaction Entity

```csharp
// AzureBank.Shared/Entities/Transaction.cs
using AzureBank.Shared.Enums;

namespace AzureBank.Shared.Entities;

/// <summary>
/// Transaction entity for all account movements
/// </summary>
public class Transaction
{
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable transaction ID: TXN-YYYYMMDD-XXXXXX
    /// </summary>
    public string TransactionNumber { get; set; } = null!;

    /// <summary>
    /// Foreign key to the account
    /// </summary>
    public Guid AccountId { get; set; }

    /// <summary>
    /// Transaction type: Deposit, Withdrawal, TransferIn, TransferOut
    /// </summary>
    public TransactionType Type { get; set; }

    /// <summary>
    /// Transaction amount (always positive)
    /// </summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Balance before this transaction
    /// </summary>
    public decimal BalanceBefore { get; set; }

    /// <summary>
    /// Balance after this transaction
    /// </summary>
    public decimal BalanceAfter { get; set; }

    /// <summary>
    /// User-provided description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// For transfers: links to the paired transaction
    /// </summary>
    public Guid? RelatedTransactionId { get; set; }

    /// <summary>
    /// For outgoing transfers: recipient's AzureTag
    /// </summary>
    public string? RecipientAzureTag { get; set; }

    /// <summary>
    /// For incoming transfers: sender's AzureTag
    /// </summary>
    public string? SenderAzureTag { get; set; }

    /// <summary>
    /// Transaction status
    /// </summary>
    public TransactionStatus Status { get; set; } = TransactionStatus.Completed;

    /// <summary>
    /// Transaction timestamp (UTC)
    /// </summary>
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public Account Account { get; set; } = null!;
    public Transaction? RelatedTransaction { get; set; }
}
```

### 4.5 RefreshToken Entity (Post-MVP)

```csharp
// AzureBank.Shared/Entities/RefreshToken.cs
namespace AzureBank.Shared.Entities;

/// <summary>
/// Refresh token entity for JWT token rotation (Post-MVP)
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Token { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }

    // Navigation
    public User User { get; set; } = null!;
    public RefreshToken? ReplacedByToken { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsActive => !IsRevoked && !IsExpired;
}
```

### 4.6 Enums

```csharp
// AzureBank.Shared/Enums/AccountType.cs
namespace AzureBank.Shared.Enums;

/// <summary>
/// Bank account types
/// </summary>
public enum AccountType
{
    Checking,
    Savings,
    Investment
}
```

```csharp
// AzureBank.Shared/Enums/TransactionType.cs
namespace AzureBank.Shared.Enums;

/// <summary>
/// Types of transactions
/// </summary>
public enum TransactionType
{
    Deposit,
    Withdrawal,
    TransferIn,
    TransferOut
}
```

```csharp
// AzureBank.Shared/Enums/TransactionStatus.cs
namespace AzureBank.Shared.Enums;

/// <summary>
/// Transaction processing status
/// </summary>
public enum TransactionStatus
{
    Pending,
    Completed,
    Failed,
    Reversed
}
```

### 4.7 Constants

```csharp
// AzureBank.Shared/Constants/ValidationRules.cs
namespace AzureBank.Shared.Constants;

/// <summary>
/// Validation constants used across the application
/// </summary>
public static class ValidationRules
{
    // Password
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;

    // AzureTag
    public const int AzureTagMinLength = 3;
    public const int AzureTagMaxLength = 20;
    public const string AzureTagPattern = @"^[a-z0-9_]+$";

    // PIN
    public const int PinLength = 6;
    public const string PinPattern = @"^\d{6}$";

    // Account
    public const int AccountNameMinLength = 1;
    public const int AccountNameMaxLength = 100;

    // Money
    public const decimal MinTransactionAmount = 0.01m;
    public const decimal MaxTransactionAmount = 100_000.00m;
    public const int MoneyDecimalPlaces = 2;

    // Description
    public const int DescriptionMaxLength = 500;

    // Pagination
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}
```

```csharp
// AzureBank.Shared/Constants/ErrorCodes.cs
namespace AzureBank.Shared.Constants;

/// <summary>
/// Standardized error codes for API responses
/// </summary>
public static class ErrorCodes
{
    // Authentication
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string PinRequired = "PIN_REQUIRED";
    public const string InvalidPin = "INVALID_PIN";

    // Authorization
    public const string AccessDenied = "ACCESS_DENIED";
    public const string InsufficientPermissions = "INSUFFICIENT_PERMISSIONS";

    // Validation
    public const string ValidationError = "VALIDATION_ERROR";
    public const string InvalidRequest = "INVALID_REQUEST";

    // Business Rules
    public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
    public const string AccountNotFound = "ACCOUNT_NOT_FOUND";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string TransactionNotFound = "TRANSACTION_NOT_FOUND";
    public const string DuplicateAzureTag = "DUPLICATE_AZURE_TAG";
    public const string DuplicateEmail = "DUPLICATE_EMAIL";
    public const string SelfTransferNotAllowed = "SELF_TRANSFER_NOT_ALLOWED";
    public const string SameAccountTransfer = "SAME_ACCOUNT_TRANSFER";

    // System
    public const string InternalError = "INTERNAL_ERROR";
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
}
```

### 4.8 DTOs - Common

```csharp
// AzureBank.Shared/DTOs/Common/ApiResponse.cs
namespace AzureBank.Shared.DTOs.Common;

/// <summary>
/// Standard API response wrapper
/// </summary>
public class ApiResponse<T>
{
    public T? Data { get; set; }
    public string? Message { get; set; }

    public static ApiResponse<T> Success(T data, string? message = null)
        => new() { Data = data, Message = message };
}

/// <summary>
/// Non-generic version for responses without data
/// </summary>
public class ApiResponse
{
    public string? Message { get; set; }

    public static ApiResponse Success(string message)
        => new() { Message = message };
}
```

```csharp
// AzureBank.Shared/DTOs/Common/ErrorResponse.cs
namespace AzureBank.Shared.DTOs.Common;

/// <summary>
/// Standard error response format
/// </summary>
public class ErrorResponse
{
    /// <summary>
    /// Error code (e.g., "VALIDATION_ERROR", "INSUFFICIENT_FUNDS")
    /// </summary>
    public string Type { get; set; } = null!;

    /// <summary>
    /// Human-readable error message
    /// </summary>
    public string Message { get; set; } = null!;

    /// <summary>
    /// Request correlation ID for support
    /// </summary>
    public string CorrelationId { get; set; } = null!;

    /// <summary>
    /// HTTP status code
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Field-level validation errors (for 422 responses)
    /// </summary>
    public Dictionary<string, string[]>? Errors { get; set; }

    /// <summary>
    /// Additional error details (e.g., available balance for INSUFFICIENT_FUNDS)
    /// </summary>
    public Dictionary<string, object>? Details { get; set; }
}
```

```csharp
// AzureBank.Shared/DTOs/Common/PaginatedResponse.cs
namespace AzureBank.Shared.DTOs.Common;

/// <summary>
/// Paginated response wrapper
/// </summary>
public class PaginatedResponse<T>
{
    public List<T> Data { get; set; } = new();
    public PaginationMetadata Pagination { get; set; } = new();
}

/// <summary>
/// Pagination metadata
/// </summary>
public class PaginationMetadata
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}
```

### 4.9 DTOs - Auth

```csharp
// AzureBank.Shared/DTOs/Auth/LoginRequest.cs
namespace AzureBank.Shared.DTOs.Auth;

public class LoginRequest
{
    public string Email { get; set; } = null!;
    public string Password { get; set; } = null!;
}
```

```csharp
// AzureBank.Shared/DTOs/Auth/LoginResponse.cs
namespace AzureBank.Shared.DTOs.Auth;

public class LoginResponse
{
    /// <summary>
    /// JWT access token (only returned to BFF, never to browser)
    /// </summary>
    public string Token { get; set; } = null!;

    /// <summary>
    /// Token expiration time
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// User information
    /// </summary>
    public UserInfo User { get; set; } = null!;
}

public class UserInfo
{
    public Guid Id { get; set; }
    public string AzureTag { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;
    public bool HasPin { get; set; }
}
```

```csharp
// AzureBank.Shared/DTOs/Auth/RegisterRequest.cs
namespace AzureBank.Shared.DTOs.Auth;

public class RegisterRequest
{
    public string AzureTag { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string Password { get; set; } = null!;
    public string ConfirmPassword { get; set; } = null!;
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;
}
```

```csharp
// AzureBank.Shared/DTOs/Auth/VerifyPinRequest.cs
namespace AzureBank.Shared.DTOs.Auth;

public class VerifyPinRequest
{
    public string Pin { get; set; } = null!;
}

public class SetPinRequest
{
    public string Pin { get; set; } = null!;
    public string ConfirmPin { get; set; } = null!;
}
```

### 4.10 DTOs - Account

```csharp
// AzureBank.Shared/DTOs/Account/AccountDto.cs
using AzureBank.Shared.Enums;

namespace AzureBank.Shared.DTOs.Account;

public class AccountDto
{
    public Guid Id { get; set; }
    public string AccountNumber { get; set; } = null!;

    /// <summary>
    /// Masked account number for display: AB-****-****-90
    /// </summary>
    public string MaskedAccountNumber { get; set; } = null!;

    public string Name { get; set; } = null!;
    public AccountType Type { get; set; }
    public decimal Balance { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AccountSummaryDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string MaskedAccountNumber { get; set; } = null!;
    public AccountType Type { get; set; }
    public decimal Balance { get; set; }
}
```

```csharp
// AzureBank.Shared/DTOs/Account/CreateAccountRequest.cs
using AzureBank.Shared.Enums;

namespace AzureBank.Shared.DTOs.Account;

public class CreateAccountRequest
{
    public string Name { get; set; } = null!;
    public AccountType Type { get; set; }
}
```

```csharp
// AzureBank.Shared/DTOs/Account/UpdateAccountRequest.cs
namespace AzureBank.Shared.DTOs.Account;

public class UpdateAccountRequest
{
    public string Name { get; set; } = null!;
}

public class SetPrimaryAccountRequest
{
    public Guid AccountId { get; set; }
}
```

### 4.11 DTOs - Transaction

```csharp
// AzureBank.Shared/DTOs/Transaction/TransactionDto.cs
using AzureBank.Shared.Enums;

namespace AzureBank.Shared.DTOs.Transaction;

public class TransactionDto
{
    public Guid Id { get; set; }
    public string TransactionNumber { get; set; } = null!;
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? Description { get; set; }
    public string? RecipientAzureTag { get; set; }
    public string? SenderAzureTag { get; set; }
    public TransactionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

```csharp
// AzureBank.Shared/DTOs/Transaction/DepositRequest.cs
namespace AzureBank.Shared.DTOs.Transaction;

public class DepositRequest
{
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}
```

```csharp
// AzureBank.Shared/DTOs/Transaction/WithdrawRequest.cs
namespace AzureBank.Shared.DTOs.Transaction;

public class WithdrawRequest
{
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}
```

```csharp
// AzureBank.Shared/DTOs/Transaction/TransactionFilterDto.cs
namespace AzureBank.Shared.DTOs.Transaction;

public class TransactionFilterDto
{
    public Guid? AccountId { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
```

### 4.12 DTOs - Transfer

```csharp
// AzureBank.Shared/DTOs/Transfer/TransferRequest.cs
namespace AzureBank.Shared.DTOs.Transfer;

/// <summary>
/// External transfer to another user via AzureTag
/// </summary>
public class TransferRequest
{
    public Guid FromAccountId { get; set; }
    public string RecipientAzureTag { get; set; } = null!;
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}
```

```csharp
// AzureBank.Shared/DTOs/Transfer/InternalTransferRequest.cs
namespace AzureBank.Shared.DTOs.Transfer;

/// <summary>
/// Internal transfer between user's own accounts
/// </summary>
public class InternalTransferRequest
{
    public Guid FromAccountId { get; set; }
    public Guid ToAccountId { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}
```

```csharp
// AzureBank.Shared/DTOs/Transfer/TransferResponse.cs
namespace AzureBank.Shared.DTOs.Transfer;

public class TransferResponse
{
    public string TransactionNumber { get; set; } = null!;
    public decimal Amount { get; set; }
    public decimal NewBalance { get; set; }
    public string? RecipientName { get; set; }
    public DateTime ProcessedAt { get; set; }
}
```

### 4.13 DTOs - User

```csharp
// AzureBank.Shared/DTOs/User/UserDto.cs
namespace AzureBank.Shared.DTOs.User;

public class UserDto
{
    public Guid Id { get; set; }
    public string AzureTag { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
```

```csharp
// AzureBank.Shared/DTOs/User/RecipientDto.cs
namespace AzureBank.Shared.DTOs.User;

/// <summary>
/// Search result for transfer recipients (privacy-masked)
/// </summary>
public class RecipientDto
{
    public string AzureTag { get; set; } = null!;

    /// <summary>
    /// Privacy-masked name: "John S."
    /// </summary>
    public string DisplayName { get; set; } = null!;
}
```

### 4.14 Custom Exceptions

```csharp
// AzureBank.Shared/Exceptions/AppException.cs
namespace AzureBank.Shared.Exceptions;

/// <summary>
/// Base exception for all application exceptions
/// </summary>
public abstract class AppException : Exception
{
    public string ErrorCode { get; }
    public int StatusCode { get; }
    public Dictionary<string, object>? Details { get; protected set; }

    protected AppException(string message, string errorCode, int statusCode)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}
```

```csharp
// AzureBank.Shared/Exceptions/NotFoundException.cs
namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 404 - Resource not found
/// </summary>
public class NotFoundException : AppException
{
    public NotFoundException(string resource, string identifier)
        : base($"{resource} with identifier '{identifier}' was not found.",
               Constants.ErrorCodes.AccountNotFound, 404) { }

    public NotFoundException(string message, string errorCode)
        : base(message, errorCode, 404) { }
}
```

```csharp
// AzureBank.Shared/Exceptions/BusinessRuleException.cs
namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 400 - Business rule violation
/// </summary>
public class BusinessRuleException : AppException
{
    public BusinessRuleException(string message, string errorCode = "BUSINESS_RULE_VIOLATION")
        : base(message, errorCode, 400) { }
}
```

```csharp
// AzureBank.Shared/Exceptions/InsufficientFundsException.cs
using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 400 - Insufficient funds for transaction
/// </summary>
public class InsufficientFundsException : BusinessRuleException
{
    public InsufficientFundsException(decimal available, decimal requested)
        : base($"Insufficient funds. Available: {available:C}, Requested: {requested:C}",
               ErrorCodes.InsufficientFunds)
    {
        Details = new Dictionary<string, object>
        {
            { "available", available },
            { "requested", requested }
        };
    }
}
```

```csharp
// AzureBank.Shared/Exceptions/AuthenticationException.cs
using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 401 - Authentication failed
/// </summary>
public class AuthenticationException : AppException
{
    public AuthenticationException(string message = "Authentication failed")
        : base(message, ErrorCodes.InvalidCredentials, 401) { }

    public AuthenticationException(string message, string errorCode)
        : base(message, errorCode, 401) { }
}
```

```csharp
// AzureBank.Shared/Exceptions/AuthorizationException.cs
using AzureBank.Shared.Constants;

namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 403 - Authorization failed
/// </summary>
public class AuthorizationException : AppException
{
    public AuthorizationException(string message = "Access denied")
        : base(message, ErrorCodes.AccessDenied, 403) { }
}
```

```csharp
// AzureBank.Shared/Exceptions/ConflictException.cs
namespace AzureBank.Shared.Exceptions;

/// <summary>
/// 409 - Conflict (e.g., duplicate AzureTag, email)
/// </summary>
public class ConflictException : AppException
{
    public ConflictException(string message, string errorCode = "CONFLICT")
        : base(message, errorCode, 409) { }
}
```

### 4.15 AzureBank.Shared.csproj

```xml
<!-- AzureBank.Shared/AzureBank.Shared.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <!-- Required for ApplicationUser extending IdentityUser -->
    <PackageReference Include="Microsoft.Extensions.Identity.Stores" Version="10.0.0" />
  </ItemGroup>

</Project>
```

---

## 5. EF Core Configuration

### 5.1 DbContext (IdentityDbContext)

```csharp
// AzureBank.Api/Data/AzureBankDbContext.cs
using AzureBank.Shared.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Data;

/// <summary>
/// Entity Framework Core database context for AzureBank
/// Extends IdentityDbContext to include ASP.NET Core Identity tables
/// </summary>
public class AzureBankDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    public AzureBankDbContext(DbContextOptions<AzureBankDbContext> options)
        : base(options)
    {
    }

    // Note: Users are accessed via Set<ApplicationUser>() inherited from IdentityDbContext
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // IMPORTANT: Call base FIRST to apply Identity configurations
        base.OnModelCreating(modelBuilder);

        // Apply all configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AzureBankDbContext).Assembly);

        // Configure ApplicationUser additional properties
        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(e => e.AzureTag)
                .HasMaxLength(21) // @username format
                .IsRequired();

            entity.HasIndex(e => e.AzureTag)
                .IsUnique();

            entity.Property(e => e.FirstName).HasMaxLength(50);
            entity.Property(e => e.LastName).HasMaxLength(50);
            entity.Property(e => e.PinHash).HasMaxLength(200);
        });

        // Global query filter for soft deletes
        modelBuilder.Entity<Account>().HasQueryFilter(e => !e.IsDeleted);
    }

    public override int SaveChanges()
    {
        UpdateTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Automatically update CreatedAt and UpdatedAt timestamps
    /// </summary>
    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries<BaseEntity>();

        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = DateTime.UtcNow;
                    entry.Entity.UpdatedAt = DateTime.UtcNow;
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = DateTime.UtcNow;
                    break;
            }
        }

        // Handle Transaction entity (doesn't inherit BaseEntity)
        var transactionEntries = ChangeTracker.Entries<Transaction>()
            .Where(e => e.State == EntityState.Added);

        foreach (var entry in transactionEntries)
        {
            entry.Entity.CreatedAt = DateTime.UtcNow;
        }
    }
}
```

### 5.2 User Configuration

```csharp
// AzureBank.Api/Data/Configurations/UserConfiguration.cs
using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AzureBank.Api.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        // Primary Key
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id)
            .HasDefaultValueSql("NEWID()");

        // AzureTag - IMMUTABLE, lowercase, unique
        builder.Property(u => u.AzureTag)
            .IsRequired()
            .HasMaxLength(20);

        builder.HasIndex(u => u.AzureTag)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0");

        // Email - unique, normalized lowercase
        builder.Property(u => u.Email)
            .IsRequired()
            .HasMaxLength(255);

        builder.HasIndex(u => u.Email)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0");

        // Password Hash
        builder.Property(u => u.PasswordHash)
            .IsRequired()
            .HasMaxLength(255);

        // PIN Hash (optional)
        builder.Property(u => u.PinHash)
            .HasMaxLength(255);

        // Name fields
        builder.Property(u => u.FirstName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(u => u.LastName)
            .IsRequired()
            .HasMaxLength(100);

        // Security fields
        builder.Property(u => u.FailedLoginAttempts)
            .HasDefaultValue(0);

        // Timestamps
        builder.Property(u => u.CreatedAt)
            .IsRequired()
            .HasDefaultValueSql("GETUTCDATE()");

        builder.Property(u => u.UpdatedAt)
            .IsRequired()
            .HasDefaultValueSql("GETUTCDATE()");

        // Soft delete
        builder.Property(u => u.IsDeleted)
            .IsRequired()
            .HasDefaultValue(false);

        // Relationships
        builder.HasMany(u => u.Accounts)
            .WithOne(a => a.User)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(u => u.RefreshTokens)
            .WithOne(r => r.User)
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

### 5.3 Account Configuration

```csharp
// AzureBank.Api/Data/Configurations/AccountConfiguration.cs
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AzureBank.Api.Data.Configurations;

public class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("Accounts");

        // Primary Key
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id)
            .HasDefaultValueSql("NEWID()");

        // Account Number - unique, format: AB-XXXX-XXXX-XX
        builder.Property(a => a.AccountNumber)
            .IsRequired()
            .HasMaxLength(14);

        builder.HasIndex(a => a.AccountNumber)
            .IsUnique()
            .HasFilter("[IsDeleted] = 0");

        // Name
        builder.Property(a => a.Name)
            .IsRequired()
            .HasMaxLength(100);

        // Type - stored as string
        builder.Property(a => a.Type)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        // Balance - DECIMAL(19,4)
        builder.Property(a => a.Balance)
            .HasPrecision(19, 4)
            .HasDefaultValue(0m);

        // IsPrimary - only one per user can be true
        builder.Property(a => a.IsPrimary)
            .IsRequired()
            .HasDefaultValue(false);

        // Filtered unique index for IsPrimary = true per user
        builder.HasIndex(a => new { a.UserId, a.IsPrimary })
            .IsUnique()
            .HasFilter("[IsPrimary] = 1 AND [IsDeleted] = 0")
            .HasDatabaseName("UX_Accounts_UserId_Primary");

        // Row Version for optimistic concurrency
        builder.Property(a => a.RowVersion)
            .IsRowVersion();

        // Timestamps
        builder.Property(a => a.CreatedAt)
            .IsRequired()
            .HasDefaultValueSql("GETUTCDATE()");

        builder.Property(a => a.UpdatedAt)
            .IsRequired()
            .HasDefaultValueSql("GETUTCDATE()");

        // Soft delete
        builder.Property(a => a.IsDeleted)
            .IsRequired()
            .HasDefaultValue(false);

        // Relationships
        builder.HasMany(a => a.Transactions)
            .WithOne(t => t.Account)
            .HasForeignKey(t => t.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Index for user lookups
        builder.HasIndex(a => a.UserId)
            .HasFilter("[IsDeleted] = 0");
    }
}
```

### 5.4 Transaction Configuration

```csharp
// AzureBank.Api/Data/Configurations/TransactionConfiguration.cs
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AzureBank.Api.Data.Configurations;

public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("Transactions");

        // Primary Key
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasDefaultValueSql("NEWID()");

        // Transaction Number - unique, format: TXN-YYYYMMDD-XXXXXX
        builder.Property(t => t.TransactionNumber)
            .IsRequired()
            .HasMaxLength(20);

        builder.HasIndex(t => t.TransactionNumber)
            .IsUnique();

        // Type - stored as string
        builder.Property(t => t.Type)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20);

        // Money fields - DECIMAL(19,4)
        builder.Property(t => t.Amount)
            .HasPrecision(19, 4)
            .IsRequired();

        builder.Property(t => t.BalanceBefore)
            .HasPrecision(19, 4)
            .IsRequired();

        builder.Property(t => t.BalanceAfter)
            .HasPrecision(19, 4)
            .IsRequired();

        // Description
        builder.Property(t => t.Description)
            .HasMaxLength(500);

        // Transfer-related fields
        builder.Property(t => t.RecipientAzureTag)
            .HasMaxLength(20);

        builder.Property(t => t.SenderAzureTag)
            .HasMaxLength(20);

        // Status
        builder.Property(t => t.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(TransactionStatus.Completed);

        // Timestamp
        builder.Property(t => t.CreatedAt)
            .IsRequired()
            .HasDefaultValueSql("GETUTCDATE()");

        // Self-referencing relationship for transfers
        builder.HasOne(t => t.RelatedTransaction)
            .WithOne()
            .HasForeignKey<Transaction>(t => t.RelatedTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes for performance
        builder.HasIndex(t => t.AccountId);

        builder.HasIndex(t => t.CreatedAt)
            .IsDescending();

        builder.HasIndex(t => new { t.AccountId, t.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_Transactions_AccountId_CreatedAt");

        builder.HasIndex(t => t.RelatedTransactionId)
            .HasFilter("[RelatedTransactionId] IS NOT NULL");
    }
}
```

### 5.5 RefreshToken Configuration

```csharp
// AzureBank.Api/Data/Configurations/RefreshTokenConfiguration.cs
using AzureBank.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AzureBank.Api.Data.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");

        // Primary Key
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id)
            .HasDefaultValueSql("NEWID()");

        // Token
        builder.Property(r => r.Token)
            .IsRequired()
            .HasMaxLength(500);

        builder.HasIndex(r => r.Token);

        // Expiration
        builder.Property(r => r.ExpiresAt)
            .IsRequired();

        builder.HasIndex(r => r.ExpiresAt);

        // Timestamps
        builder.Property(r => r.CreatedAt)
            .IsRequired()
            .HasDefaultValueSql("GETUTCDATE()");

        // Self-referencing for token rotation
        builder.HasOne(r => r.ReplacedByToken)
            .WithOne()
            .HasForeignKey<RefreshToken>(r => r.ReplacedByTokenId)
            .OnDelete(DeleteBehavior.Restrict);

        // Index for user lookups
        builder.HasIndex(r => r.UserId);
    }
}
```

### 5.6 Database Seeder

```csharp
// AzureBank.Api/Data/Seed/DatabaseSeeder.cs
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Data.Seed;

public class DatabaseSeeder
{
    private readonly AzureBankDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        AzureBankDbContext context,
        IPasswordHasher passwordHasher,
        ILogger<DatabaseSeeder> logger)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    public async Task SeedAsync()
    {
        if (await _context.Users.AnyAsync())
        {
            _logger.LogInformation("Database already seeded. Skipping...");
            return;
        }

        _logger.LogInformation("Seeding database with test data...");

        await using var transaction = await _context.Database.BeginTransactionAsync();

        try
        {
            // Create test users
            var users = CreateUsers();
            await _context.Users.AddRangeAsync(users);
            await _context.SaveChangesAsync();

            // Create accounts for each user
            var accounts = CreateAccounts(users);
            await _context.Accounts.AddRangeAsync(accounts);
            await _context.SaveChangesAsync();

            // Create sample transactions
            var transactions = CreateTransactions(accounts);
            await _context.Transactions.AddRangeAsync(transactions);
            await _context.SaveChangesAsync();

            await transaction.CommitAsync();
            _logger.LogInformation("Database seeded successfully with {UserCount} users, {AccountCount} accounts, {TransactionCount} transactions",
                users.Count, accounts.Count, transactions.Count);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Error seeding database");
            throw;
        }
    }

    private List<User> CreateUsers()
    {
        var passwordHash = _passwordHasher.HashPassword("Test123!");
        var pinHash = _passwordHasher.HashPassword("123456");

        return new List<User>
        {
            new()
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                AzureTag = "johnsmith",
                Email = "john@example.com",
                PasswordHash = passwordHash,
                PinHash = pinHash,
                FirstName = "John",
                LastName = "Smith"
            },
            new()
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                AzureTag = "janesmith",
                Email = "jane@example.com",
                PasswordHash = passwordHash,
                PinHash = pinHash,
                FirstName = "Jane",
                LastName = "Smith"
            },
            new()
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                AzureTag = "mikebrown",
                Email = "mike@example.com",
                PasswordHash = passwordHash,
                PinHash = pinHash,
                FirstName = "Mike",
                LastName = "Brown"
            }
        };
    }

    private List<Account> CreateAccounts(List<User> users)
    {
        var accounts = new List<Account>();

        // John's accounts
        accounts.Add(new Account
        {
            Id = Guid.Parse("AAAA1111-1111-1111-1111-111111111111"),
            UserId = users[0].Id,
            AccountNumber = "AB-1234-5678-90",
            Name = "Main Savings",
            Type = AccountType.Savings,
            Balance = 12450.00m,
            IsPrimary = true
        });

        accounts.Add(new Account
        {
            Id = Guid.Parse("AAAA2222-2222-2222-2222-222222222222"),
            UserId = users[0].Id,
            AccountNumber = "AB-1234-5678-91",
            Name = "Checking",
            Type = AccountType.Checking,
            Balance = 2300.00m,
            IsPrimary = false
        });

        // Jane's accounts
        accounts.Add(new Account
        {
            Id = Guid.Parse("BBBB1111-1111-1111-1111-111111111111"),
            UserId = users[1].Id,
            AccountNumber = "AB-2345-6789-01",
            Name = "Personal Savings",
            Type = AccountType.Savings,
            Balance = 8500.00m,
            IsPrimary = true
        });

        // Mike's accounts
        accounts.Add(new Account
        {
            Id = Guid.Parse("CCCC1111-1111-1111-1111-111111111111"),
            UserId = users[2].Id,
            AccountNumber = "AB-3456-7890-12",
            Name = "Investment Account",
            Type = AccountType.Investment,
            Balance = 25000.00m,
            IsPrimary = true
        });

        return accounts;
    }

    private List<Transaction> CreateTransactions(List<Account> accounts)
    {
        var transactions = new List<Transaction>();
        var johnSavings = accounts[0];

        // John's deposit
        transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionNumber = $"TXN-{DateTime.UtcNow.AddDays(-3):yyyyMMdd}-000001",
            AccountId = johnSavings.Id,
            Type = TransactionType.Deposit,
            Amount = 5000.00m,
            BalanceBefore = 7450.00m,
            BalanceAfter = 12450.00m,
            Description = "Salary deposit",
            CreatedAt = DateTime.UtcNow.AddDays(-3)
        });

        // John's withdrawal
        transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionNumber = $"TXN-{DateTime.UtcNow.AddDays(-2):yyyyMMdd}-000002",
            AccountId = johnSavings.Id,
            Type = TransactionType.Withdrawal,
            Amount = 500.00m,
            BalanceBefore = 12450.00m,
            BalanceAfter = 11950.00m,
            Description = "ATM withdrawal",
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        });

        // Another deposit
        transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionNumber = $"TXN-{DateTime.UtcNow.AddDays(-1):yyyyMMdd}-000003",
            AccountId = johnSavings.Id,
            Type = TransactionType.Deposit,
            Amount = 500.00m,
            BalanceBefore = 11950.00m,
            BalanceAfter = 12450.00m,
            Description = "Refund",
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        });

        return transactions;
    }
}
```

### 5.7 Migrations Setup

```powershell
# Navigate to API project
cd src/AzureBank.Api

# Create initial migration
dotnet ef migrations add InitialCreate --context AzureBankDbContext

# Apply migration to database
dotnet ef database update

# Generate SQL script (for production deployment)
dotnet ef migrations script --idempotent -o Migrations/InitialCreate.sql
```

### 5.8 Connection String Configuration

```json
// AzureBank.Api/appsettings.json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=AzureBank;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
  }
}
```

```json
// AzureBank.Api/appsettings.Development.json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\MSSQLLocalDB;Database=AzureBank_Dev;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
  }
}
```

### 5.9 DbContext Registration

```csharp
// In Program.cs or ServiceCollectionExtensions.cs
builder.Services.AddDbContext<AzureBankDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null);

            sqlOptions.CommandTimeout(30);
        });

    // Development: Enable sensitive data logging
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});
```

---

## 6. Backend API (AzureBank.Api)

### 6.1 Service Interfaces

```csharp
// AzureBank.Api/Services/Interfaces/IPasswordHasher.cs
namespace AzureBank.Api.Services.Interfaces;

public interface IPasswordHasher
{
    string HashPassword(string password);
    bool VerifyPassword(string hash, string password);
}
```

```csharp
// AzureBank.Api/Services/Interfaces/IJwtService.cs
using AzureBank.Shared.Entities;

namespace AzureBank.Api.Services.Interfaces;

public interface IJwtService
{
    string GenerateToken(User user);
    (bool IsValid, Guid UserId) ValidateToken(string token);
}
```

```csharp
// AzureBank.Api/Services/Interfaces/IAuthService.cs
using AzureBank.Shared.DTOs.Auth;

namespace AzureBank.Api.Services.Interfaces;

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request);
    Task<LoginResponse> RegisterAsync(RegisterRequest request);
    Task<bool> VerifyPinAsync(Guid userId, string pin);
    Task SetPinAsync(Guid userId, SetPinRequest request);
}
```

```csharp
// AzureBank.Api/Services/Interfaces/IAccountService.cs
using AzureBank.Shared.DTOs.Account;

namespace AzureBank.Api.Services.Interfaces;

public interface IAccountService
{
    Task<List<AccountDto>> GetUserAccountsAsync(Guid userId);
    Task<AccountDto> GetAccountByIdAsync(Guid accountId, Guid userId);
    Task<AccountDto> CreateAccountAsync(Guid userId, CreateAccountRequest request);
    Task<AccountDto> UpdateAccountAsync(Guid accountId, Guid userId, UpdateAccountRequest request);
    Task SetPrimaryAccountAsync(Guid userId, Guid accountId);
    Task DeleteAccountAsync(Guid accountId, Guid userId);
    Task<decimal> GetBalanceAtTimeAsync(Guid accountId, Guid userId, DateTime atTime);
}
```

```csharp
// AzureBank.Api/Services/Interfaces/ITransactionService.cs
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;

namespace AzureBank.Api.Services.Interfaces;

public interface ITransactionService
{
    Task<TransactionDto> DepositAsync(Guid userId, DepositRequest request);
    Task<TransactionDto> WithdrawAsync(Guid userId, WithdrawRequest request);
    Task<PaginatedResponse<TransactionDto>> GetTransactionsAsync(Guid userId, TransactionFilterDto filter);
    Task<TransactionDto> GetTransactionByIdAsync(Guid transactionId, Guid userId);
}
```

```csharp
// AzureBank.Api/Services/Interfaces/ITransferService.cs
using AzureBank.Shared.DTOs.Transfer;

namespace AzureBank.Api.Services.Interfaces;

public interface ITransferService
{
    Task<TransferResponse> TransferAsync(Guid userId, TransferRequest request);
    Task<TransferResponse> InternalTransferAsync(Guid userId, InternalTransferRequest request);
}
```

```csharp
// AzureBank.Api/Services/Interfaces/IUserService.cs
using AzureBank.Shared.DTOs.User;

namespace AzureBank.Api.Services.Interfaces;

public interface IUserService
{
    Task<UserDto> GetUserByIdAsync(Guid userId);
    Task<List<RecipientDto>> SearchUsersAsync(string azureTagQuery, Guid excludeUserId);
}
```

### 6.2 Password Hasher (Argon2id)

```csharp
// AzureBank.Api/Services/Implementations/PasswordHasher.cs
using System.Security.Cryptography;
using System.Text;
using AzureBank.Api.Services.Interfaces;
using Konscious.Security.Cryptography;

namespace AzureBank.Api.Services.Implementations;

public class PasswordHasher : IPasswordHasher
{
    // OWASP recommended parameters for Argon2id
    private const int MemorySize = 65536;  // 64 MB
    private const int Iterations = 3;       // Time cost
    private const int Parallelism = 4;      // Parallel threads
    private const int SaltLength = 16;      // 128 bits
    private const int HashLength = 32;      // 256 bits

    public string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = MemorySize,
            Iterations = Iterations,
            DegreeOfParallelism = Parallelism
        };

        var hash = argon2.GetBytes(HashLength);

        // Format: $argon2id$v=19$m=65536,t=3,p=4$<salt_base64>$<hash_base64>
        return $"$argon2id$v=19$m={MemorySize},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string hash, string password)
    {
        try
        {
            var parts = hash.Split('$');
            if (parts.Length != 6 || parts[1] != "argon2id")
                return false;

            // Parse parameters
            var paramParts = parts[3].Split(',');
            var memory = int.Parse(paramParts[0][2..]);
            var iterations = int.Parse(paramParts[1][2..]);
            var parallelism = int.Parse(paramParts[2][2..]);

            var salt = Convert.FromBase64String(parts[4]);
            var storedHash = Convert.FromBase64String(parts[5]);

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
            {
                Salt = salt,
                MemorySize = memory,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };

            var computedHash = argon2.GetBytes(storedHash.Length);

            // Constant-time comparison
            return CryptographicOperations.FixedTimeEquals(storedHash, computedHash);
        }
        catch
        {
            return false;
        }
    }
}
```

### 6.3 JWT Service

```csharp
// AzureBank.Api/Services/Implementations/JwtService.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AzureBank.Api.Services.Implementations;

public class JwtSettings
{
    public string Secret { get; set; } = null!;
    public string Issuer { get; set; } = null!;
    public string Audience { get; set; } = null!;
    public int ExpirationMinutes { get; set; } = 15;
}

public class JwtService : IJwtService
{
    private readonly JwtSettings _settings;
    private readonly byte[] _key;

    public JwtService(IOptions<JwtSettings> settings)
    {
        _settings = settings.Value;
        _key = Encoding.UTF8.GetBytes(_settings.Secret);
    }

    public string GenerateToken(User user)
    {
        var tokenHandler = new JwtSecurityTokenHandler();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("azure_tag", user.AzureTag),
            new(ClaimTypes.Role, "User"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_settings.ExpirationMinutes),
            Issuer = _settings.Issuer,
            Audience = _settings.Audience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(_key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public (bool IsValid, Guid UserId) ValidateToken(string token)
    {
        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _settings.Issuer,
                ValidAudience = _settings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(_key),
                ClockSkew = TimeSpan.Zero
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);
            var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "");

            return (true, userId);
        }
        catch
        {
            return (false, Guid.Empty);
        }
    }
}
```

### 6.4 Account Service

```csharp
// AzureBank.Api/Services/Implementations/AccountService.cs
using AzureBank.Api.Data;
using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services.Implementations;

public class AccountService : IAccountService
{
    private readonly AzureBankDbContext _context;
    private readonly AccountMapper _mapper;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        AzureBankDbContext context,
        AccountMapper mapper,
        ILogger<AccountService> logger)
    {
        _context = context;
        _mapper = mapper;
        _logger = logger;
    }

    public async Task<List<AccountDto>> GetUserAccountsAsync(Guid userId)
    {
        var accounts = await _context.Accounts
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.IsPrimary)
            .ThenBy(a => a.CreatedAt)
            .ToListAsync();

        return _mapper.ToDtos(accounts).ToList();
    }

    public async Task<AccountDto> GetAccountByIdAsync(Guid accountId, Guid userId)
    {
        var account = await _context.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null)
            throw new NotFoundException("Account", accountId.ToString());

        if (account.UserId != userId)
            throw new AuthorizationException("You don't have access to this account");

        return _mapper.ToDto(account);
    }

    public async Task<AccountDto> CreateAccountAsync(Guid userId, CreateAccountRequest request)
    {
        // Check if user exists
        var userExists = await _context.Users.AnyAsync(u => u.Id == userId);
        if (!userExists)
            throw new NotFoundException("User", userId.ToString());

        // Check if this is the first account (make it primary)
        var hasAccounts = await _context.Accounts.AnyAsync(a => a.UserId == userId);

        var account = new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountNumber = GenerateAccountNumber(),
            Name = request.Name,
            Type = request.Type,
            Balance = 0m,
            IsPrimary = !hasAccounts  // First account is primary
        };

        _context.Accounts.Add(account);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Account created. UserId: {UserId}, AccountId: {AccountId}, Type: {Type}",
            userId, account.Id, account.Type);

        return _mapper.ToDto(account);
    }

    public async Task<AccountDto> UpdateAccountAsync(Guid accountId, Guid userId, UpdateAccountRequest request)
    {
        var account = await _context.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null)
            throw new NotFoundException("Account", accountId.ToString());

        if (account.UserId != userId)
            throw new AuthorizationException("You don't have access to this account");

        account.Name = request.Name;
        await _context.SaveChangesAsync();

        return _mapper.ToDto(account);
    }

    public async Task SetPrimaryAccountAsync(Guid userId, Guid accountId)
    {
        var accounts = await _context.Accounts
            .Where(a => a.UserId == userId)
            .ToListAsync();

        var targetAccount = accounts.FirstOrDefault(a => a.Id == accountId);
        if (targetAccount == null)
            throw new NotFoundException("Account", accountId.ToString());

        // Remove primary from all accounts
        foreach (var account in accounts)
        {
            account.IsPrimary = account.Id == accountId;
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Primary account changed. UserId: {UserId}, NewPrimaryAccountId: {AccountId}",
            userId, accountId);
    }

    public async Task DeleteAccountAsync(Guid accountId, Guid userId)
    {
        var account = await _context.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null)
            throw new NotFoundException("Account", accountId.ToString());

        if (account.UserId != userId)
            throw new AuthorizationException("You don't have access to this account");

        if (account.Balance > 0)
            throw new BusinessRuleException("Cannot delete account with positive balance");

        // Soft delete
        account.IsDeleted = true;
        account.DeletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Account deleted. UserId: {UserId}, AccountId: {AccountId}",
            userId, accountId);
    }

    public async Task<decimal> GetBalanceAtTimeAsync(Guid accountId, Guid userId, DateTime atTime)
    {
        var account = await _context.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null)
            throw new NotFoundException("Account", accountId.ToString());

        if (account.UserId != userId)
            throw new AuthorizationException("You don't have access to this account");

        // Get the balance after the last transaction before or at atTime
        var transaction = await _context.Transactions
            .Where(t => t.AccountId == accountId && t.CreatedAt <= atTime)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        return transaction?.BalanceAfter ?? 0m;
    }

    private static string GenerateAccountNumber()
    {
        var random = new Random();
        var digits = random.Next(10000000, 99999999).ToString("D8");

        // Calculate check digits (simplified)
        var sum = digits.Sum(c => c - '0');
        var checkDigits = (sum % 100).ToString("D2");

        return $"AB-{digits[..4]}-{digits[4..]}-{checkDigits}";
    }
}
```

### 6.5 Transaction Service

```csharp
// AzureBank.Api/Services/Implementations/TransactionService.cs
using AzureBank.Api.Data;
using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services.Implementations;

public class TransactionService : ITransactionService
{
    private readonly AzureBankDbContext _context;
    private readonly TransactionMapper _mapper;
    private readonly ILogger<TransactionService> _logger;

    public TransactionService(
        AzureBankDbContext context,
        TransactionMapper mapper,
        ILogger<TransactionService> logger)
    {
        _context = context;
        _mapper = mapper;
        _logger = logger;
    }

    public async Task<TransactionDto> DepositAsync(Guid userId, DepositRequest request)
    {
        var account = await GetAccountWithAuthCheckAsync(request.AccountId, userId);

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionNumber = GenerateTransactionNumber(),
            AccountId = account.Id,
            Type = TransactionType.Deposit,
            Amount = request.Amount,
            BalanceBefore = account.Balance,
            BalanceAfter = account.Balance + request.Amount,
            Description = request.Description,
            Status = TransactionStatus.Completed
        };

        account.Balance = transaction.BalanceAfter;

        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Deposit completed. AccountId: {AccountId}, Amount: {Amount:C}, NewBalance: {NewBalance:C}",
            account.Id, request.Amount, account.Balance);

        return _mapper.ToDto(transaction);
    }

    public async Task<TransactionDto> WithdrawAsync(Guid userId, WithdrawRequest request)
    {
        var account = await GetAccountWithAuthCheckAsync(request.AccountId, userId);

        if (account.Balance < request.Amount)
            throw new InsufficientFundsException(account.Balance, request.Amount);

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            TransactionNumber = GenerateTransactionNumber(),
            AccountId = account.Id,
            Type = TransactionType.Withdrawal,
            Amount = request.Amount,
            BalanceBefore = account.Balance,
            BalanceAfter = account.Balance - request.Amount,
            Description = request.Description,
            Status = TransactionStatus.Completed
        };

        account.Balance = transaction.BalanceAfter;

        _context.Transactions.Add(transaction);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Withdrawal completed. AccountId: {AccountId}, Amount: {Amount:C}, NewBalance: {NewBalance:C}",
            account.Id, request.Amount, account.Balance);

        return _mapper.ToDto(transaction);
    }

    public async Task<PaginatedResponse<TransactionDto>> GetTransactionsAsync(
        Guid userId,
        TransactionFilterDto filter)
    {
        // Validate account belongs to user if specified
        if (filter.AccountId.HasValue)
        {
            await GetAccountWithAuthCheckAsync(filter.AccountId.Value, userId);
        }

        var query = _context.Transactions
            .Include(t => t.Account)
            .Where(t => t.Account.UserId == userId);

        // Apply filters
        if (filter.AccountId.HasValue)
            query = query.Where(t => t.AccountId == filter.AccountId.Value);

        if (filter.FromDate.HasValue)
            query = query.Where(t => t.CreatedAt >= filter.FromDate.Value);

        if (filter.ToDate.HasValue)
            query = query.Where(t => t.CreatedAt <= filter.ToDate.Value);

        // Get total count
        var totalItems = await query.CountAsync();

        // Apply pagination
        var pageSize = Math.Min(filter.PageSize, ValidationRules.MaxPageSize);
        var page = Math.Max(1, filter.Page);

        var transactions = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PaginatedResponse<TransactionDto>
        {
            Data = _mapper.ToDtos(transactions).ToList(),
            Pagination = new PaginationMetadata
            {
                Page = page,
                PageSize = pageSize,
                TotalItems = totalItems,
                TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
            }
        };
    }

    public async Task<TransactionDto> GetTransactionByIdAsync(Guid transactionId, Guid userId)
    {
        var transaction = await _context.Transactions
            .Include(t => t.Account)
            .FirstOrDefaultAsync(t => t.Id == transactionId);

        if (transaction == null)
            throw new NotFoundException("Transaction", transactionId.ToString());

        if (transaction.Account.UserId != userId)
            throw new AuthorizationException("You don't have access to this transaction");

        return _mapper.ToDto(transaction);
    }

    private async Task<Account> GetAccountWithAuthCheckAsync(Guid accountId, Guid userId)
    {
        var account = await _context.Accounts
            .FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null)
            throw new NotFoundException("Account", accountId.ToString());

        if (account.UserId != userId)
            throw new AuthorizationException("You don't have access to this account");

        return account;
    }

    private static string GenerateTransactionNumber()
    {
        var datePart = DateTime.UtcNow.ToString("yyyyMMdd");
        var randomPart = Random.Shared.Next(100000, 999999).ToString();
        return $"TXN-{datePart}-{randomPart}";
    }
}
```

### 6.6 FluentValidation Validators

```csharp
// AzureBank.Api/Validators/Auth/LoginRequestValidator.cs
using AzureBank.Shared.DTOs.Auth;
using FluentValidation;

namespace AzureBank.Api.Validators.Auth;

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required")
            .EmailAddress().WithMessage("Invalid email format");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required");
    }
}
```

```csharp
// AzureBank.Api/Validators/Auth/RegisterRequestValidator.cs
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using FluentValidation;

namespace AzureBank.Api.Validators.Auth;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.AzureTag)
            .NotEmpty().WithMessage("AzureTag is required")
            .MinimumLength(ValidationRules.AzureTagMinLength)
                .WithMessage($"AzureTag must be at least {ValidationRules.AzureTagMinLength} characters")
            .MaximumLength(ValidationRules.AzureTagMaxLength)
                .WithMessage($"AzureTag cannot exceed {ValidationRules.AzureTagMaxLength} characters")
            .Matches(ValidationRules.AzureTagPattern)
                .WithMessage("AzureTag can only contain lowercase letters, numbers, and underscores");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required")
            .EmailAddress().WithMessage("Invalid email format")
            .MaximumLength(254).WithMessage("Email cannot exceed 254 characters");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required")
            .MinimumLength(ValidationRules.PasswordMinLength)
                .WithMessage($"Password must be at least {ValidationRules.PasswordMinLength} characters")
            .MaximumLength(ValidationRules.PasswordMaxLength)
                .WithMessage($"Password cannot exceed {ValidationRules.PasswordMaxLength} characters")
            .Matches("[A-Z]").WithMessage("Password must contain at least one uppercase letter")
            .Matches("[a-z]").WithMessage("Password must contain at least one lowercase letter")
            .Matches("[0-9]").WithMessage("Password must contain at least one digit");

        RuleFor(x => x.ConfirmPassword)
            .Equal(x => x.Password).WithMessage("Passwords do not match");

        RuleFor(x => x.FirstName)
            .NotEmpty().WithMessage("First name is required")
            .MaximumLength(100).WithMessage("First name cannot exceed 100 characters");

        RuleFor(x => x.LastName)
            .NotEmpty().WithMessage("Last name is required")
            .MaximumLength(100).WithMessage("Last name cannot exceed 100 characters");
    }
}
```

```csharp
// AzureBank.Api/Validators/Transaction/DepositRequestValidator.cs
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transaction;
using FluentValidation;

namespace AzureBank.Api.Validators.Transaction;

public class DepositRequestValidator : AbstractValidator<DepositRequest>
{
    public DepositRequestValidator()
    {
        RuleFor(x => x.AccountId)
            .NotEmpty().WithMessage("Account ID is required");

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be positive")
            .LessThanOrEqualTo(ValidationRules.MaxTransactionAmount)
                .WithMessage($"Amount cannot exceed {ValidationRules.MaxTransactionAmount:C}")
            .PrecisionScale(19, 2, ignoreTrailingZeros: true)
                .WithMessage("Amount can have at most 2 decimal places");

        RuleFor(x => x.Description)
            .MaximumLength(ValidationRules.DescriptionMaxLength)
                .WithMessage($"Description cannot exceed {ValidationRules.DescriptionMaxLength} characters");
    }
}
```

```csharp
// AzureBank.Api/Validators/Transfer/TransferRequestValidator.cs
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Transfer;
using FluentValidation;

namespace AzureBank.Api.Validators.Transfer;

public class TransferRequestValidator : AbstractValidator<TransferRequest>
{
    public TransferRequestValidator()
    {
        RuleFor(x => x.FromAccountId)
            .NotEmpty().WithMessage("Source account is required");

        RuleFor(x => x.RecipientAzureTag)
            .NotEmpty().WithMessage("Recipient AzureTag is required")
            .Matches(ValidationRules.AzureTagPattern)
                .WithMessage("Invalid AzureTag format");

        RuleFor(x => x.Amount)
            .GreaterThan(0).WithMessage("Amount must be positive")
            .LessThanOrEqualTo(ValidationRules.MaxTransactionAmount)
                .WithMessage($"Maximum transfer amount is {ValidationRules.MaxTransactionAmount:C}")
            .PrecisionScale(19, 2, ignoreTrailingZeros: true)
                .WithMessage("Amount can have at most 2 decimal places");

        RuleFor(x => x.Description)
            .MaximumLength(ValidationRules.DescriptionMaxLength)
                .WithMessage($"Description cannot exceed {ValidationRules.DescriptionMaxLength} characters");
    }
}
```

### 6.7 Mapperly Mappers

```csharp
// AzureBank.Api/Mappers/AccountMapper.cs
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.Entities;
using Riok.Mapperly.Abstractions;

namespace AzureBank.Api.Mappers;

[Mapper]
public partial class AccountMapper
{
    public partial AccountSummaryDto ToSummaryDto(Account account);
    public partial IEnumerable<AccountDto> ToDtos(IEnumerable<Account> accounts);

    public AccountDto ToDto(Account account)
    {
        return new AccountDto
        {
            Id = account.Id,
            AccountNumber = account.AccountNumber,
            MaskedAccountNumber = MaskAccountNumber(account.AccountNumber),
            Name = account.Name,
            Type = account.Type,
            Balance = account.Balance,
            IsPrimary = account.IsPrimary,
            CreatedAt = account.CreatedAt
        };
    }

    private static string MaskAccountNumber(string accountNumber)
    {
        // AB-1234-5678-90 → AB-****-****-90
        if (accountNumber.Length < 6) return "****";
        return $"{accountNumber[..3]}****-****-{accountNumber[^2..]}";
    }
}
```

```csharp
// AzureBank.Api/Mappers/TransactionMapper.cs
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.Entities;
using Riok.Mapperly.Abstractions;

namespace AzureBank.Api.Mappers;

[Mapper]
public partial class TransactionMapper
{
    public partial TransactionDto ToDto(Transaction transaction);
    public partial IEnumerable<TransactionDto> ToDtos(IEnumerable<Transaction> transactions);
}
```

```csharp
// AzureBank.Api/Mappers/UserMapper.cs
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Entities;
using Riok.Mapperly.Abstractions;

namespace AzureBank.Api.Mappers;

[Mapper]
public partial class UserMapper
{
    [MapperIgnoreSource(nameof(User.PasswordHash))]
    [MapperIgnoreSource(nameof(User.PinHash))]
    public partial UserDto ToDto(User user);

    public UserInfo ToUserInfo(User user)
    {
        return new UserInfo
        {
            Id = user.Id,
            AzureTag = user.AzureTag,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            HasPin = !string.IsNullOrEmpty(user.PinHash)
        };
    }

    public RecipientDto ToRecipientDto(User user)
    {
        return new RecipientDto
        {
            AzureTag = user.AzureTag,
            DisplayName = $"{user.FirstName} {user.LastName[0]}."  // "John S."
        };
    }
}
```

### 6.8 Controllers

```csharp
// AzureBank.Api/Controllers/AuthController.cs
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    /// <summary>
    /// Register a new user
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<LoginResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var response = await _authService.RegisterAsync(request);
        return CreatedAtAction(nameof(Register), ApiResponse<LoginResponse>.Success(response, "Registration successful"));
    }

    /// <summary>
    /// Login with email and password
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<LoginResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var response = await _authService.LoginAsync(request);
        return Ok(ApiResponse<LoginResponse>.Success(response));
    }
}
```

```csharp
// AzureBank.Api/Controllers/AccountsController.cs
using System.Security.Claims;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly IAccountService _accountService;

    public AccountsController(IAccountService accountService)
    {
        _accountService = accountService;
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Get all accounts for the current user
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<List<AccountDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAccounts()
    {
        var accounts = await _accountService.GetUserAccountsAsync(GetUserId());
        return Ok(ApiResponse<List<AccountDto>>.Success(accounts));
    }

    /// <summary>
    /// Get a specific account by ID
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AccountDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccount(Guid id)
    {
        var account = await _accountService.GetAccountByIdAsync(id, GetUserId());
        return Ok(ApiResponse<AccountDto>.Success(account));
    }

    /// <summary>
    /// Create a new account
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<AccountDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateAccount([FromBody] CreateAccountRequest request)
    {
        var account = await _accountService.CreateAccountAsync(GetUserId(), request);
        return CreatedAtAction(nameof(GetAccount), new { id = account.Id },
            ApiResponse<AccountDto>.Success(account, "Account created successfully"));
    }

    /// <summary>
    /// Update account name
    /// </summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AccountDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAccount(Guid id, [FromBody] UpdateAccountRequest request)
    {
        var account = await _accountService.UpdateAccountAsync(id, GetUserId(), request);
        return Ok(ApiResponse<AccountDto>.Success(account, "Account updated successfully"));
    }

    /// <summary>
    /// Get balance at a specific point in time
    /// </summary>
    [HttpGet("{id:guid}/balance")]
    [ProducesResponseType(typeof(ApiResponse<decimal>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBalanceAtTime(Guid id, [FromQuery] DateTime? at)
    {
        var atTime = at ?? DateTime.UtcNow;
        var balance = await _accountService.GetBalanceAtTimeAsync(id, GetUserId(), atTime);
        return Ok(ApiResponse<decimal>.Success(balance));
    }

    /// <summary>
    /// Delete an account (soft delete)
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteAccount(Guid id)
    {
        await _accountService.DeleteAccountAsync(id, GetUserId());
        return NoContent();
    }
}
```

```csharp
// AzureBank.Api/Controllers/TransactionsController.cs
using System.Security.Claims;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.DTOs.Transaction;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TransactionsController : ControllerBase
{
    private readonly ITransactionService _transactionService;

    public TransactionsController(ITransactionService transactionService)
    {
        _transactionService = transactionService;
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>
    /// Get transaction history with filtering and pagination
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PaginatedResponse<TransactionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransactions([FromQuery] TransactionFilterDto filter)
    {
        var transactions = await _transactionService.GetTransactionsAsync(GetUserId(), filter);
        return Ok(transactions);
    }

    /// <summary>
    /// Get a specific transaction by ID
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<TransactionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransaction(Guid id)
    {
        var transaction = await _transactionService.GetTransactionByIdAsync(id, GetUserId());
        return Ok(ApiResponse<TransactionDto>.Success(transaction));
    }

    /// <summary>
    /// Deposit money into an account
    /// </summary>
    [HttpPost("deposit")]
    [ProducesResponseType(typeof(ApiResponse<TransactionDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Deposit([FromBody] DepositRequest request)
    {
        var transaction = await _transactionService.DepositAsync(GetUserId(), request);
        return CreatedAtAction(nameof(GetTransaction), new { id = transaction.Id },
            ApiResponse<TransactionDto>.Success(transaction, "Deposit successful"));
    }

    /// <summary>
    /// Withdraw money from an account
    /// </summary>
    [HttpPost("withdraw")]
    [ProducesResponseType(typeof(ApiResponse<TransactionDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Withdraw([FromBody] WithdrawRequest request)
    {
        var transaction = await _transactionService.WithdrawAsync(GetUserId(), request);
        return CreatedAtAction(nameof(GetTransaction), new { id = transaction.Id },
            ApiResponse<TransactionDto>.Success(transaction, "Withdrawal successful"));
    }
}
```

---

## 7. BFF Gateway (AzureBank.Bff)

The BFF (Backend-for-Frontend) acts as a security gateway between the browser and the API. JWT tokens are stored server-side in BFF sessions, never exposed to the browser.

### 7.1 BFF Service Interfaces

```csharp
// AzureBank.Bff/Services/Interfaces/ISessionService.cs
namespace AzureBank.Bff.Services.Interfaces;

public interface ISessionService
{
    string CreateSession(string jwtToken, DateTime expiresAt, Guid userId);
    bool TryGetToken(string sessionId, out string? token);
    bool ValidateSession(string sessionId);
    void RevokeSession(string sessionId);
    void RefreshSession(string sessionId, string newToken, DateTime expiresAt);
}
```

```csharp
// AzureBank.Bff/Services/Interfaces/ITokenStoreService.cs
namespace AzureBank.Bff.Services.Interfaces;

public interface ITokenStoreService
{
    Task StoreTokenAsync(string sessionId, string token, DateTime expiresAt);
    Task<string?> GetTokenAsync(string sessionId);
    Task RemoveTokenAsync(string sessionId);
}
```

### 7.2 In-Memory Token Store (MVP)

```csharp
// AzureBank.Bff/Services/Implementations/InMemoryTokenStore.cs
using System.Collections.Concurrent;
using AzureBank.Bff.Services.Interfaces;

namespace AzureBank.Bff.Services.Implementations;

public class InMemoryTokenStore : ITokenStoreService
{
    private readonly ConcurrentDictionary<string, SessionData> _sessions = new();
    private readonly ILogger<InMemoryTokenStore> _logger;

    public InMemoryTokenStore(ILogger<InMemoryTokenStore> logger)
    {
        _logger = logger;
    }

    public Task StoreTokenAsync(string sessionId, string token, DateTime expiresAt)
    {
        _sessions[sessionId] = new SessionData
        {
            Token = token,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow
        };

        _logger.LogDebug("Token stored for session {SessionId}", sessionId);
        return Task.CompletedTask;
    }

    public Task<string?> GetTokenAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var data))
        {
            if (data.ExpiresAt > DateTime.UtcNow)
            {
                return Task.FromResult<string?>(data.Token);
            }

            // Token expired, remove it
            _sessions.TryRemove(sessionId, out _);
            _logger.LogDebug("Expired token removed for session {SessionId}", sessionId);
        }

        return Task.FromResult<string?>(null);
    }

    public Task RemoveTokenAsync(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        _logger.LogDebug("Token removed for session {SessionId}", sessionId);
        return Task.CompletedTask;
    }

    private class SessionData
    {
        public string Token { get; init; } = null!;
        public DateTime ExpiresAt { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
```

### 7.3 Session Service

```csharp
// AzureBank.Bff/Services/Implementations/SessionService.cs
using System.Security.Cryptography;
using AzureBank.Bff.Services.Interfaces;

namespace AzureBank.Bff.Services.Implementations;

public class SessionService : ISessionService
{
    private readonly ITokenStoreService _tokenStore;
    private readonly ILogger<SessionService> _logger;

    public SessionService(ITokenStoreService tokenStore, ILogger<SessionService> logger)
    {
        _tokenStore = tokenStore;
        _logger = logger;
    }

    public string CreateSession(string jwtToken, DateTime expiresAt, Guid userId)
    {
        var sessionId = GenerateSecureSessionId();
        _tokenStore.StoreTokenAsync(sessionId, jwtToken, expiresAt).GetAwaiter().GetResult();

        _logger.LogInformation("Session created for user {UserId}", userId);
        return sessionId;
    }

    public bool TryGetToken(string sessionId, out string? token)
    {
        token = _tokenStore.GetTokenAsync(sessionId).GetAwaiter().GetResult();
        return token != null;
    }

    public bool ValidateSession(string sessionId)
    {
        return TryGetToken(sessionId, out _);
    }

    public void RevokeSession(string sessionId)
    {
        _tokenStore.RemoveTokenAsync(sessionId).GetAwaiter().GetResult();
        _logger.LogInformation("Session revoked: {SessionId}", sessionId[..8]);
    }

    public void RefreshSession(string sessionId, string newToken, DateTime expiresAt)
    {
        _tokenStore.StoreTokenAsync(sessionId, newToken, expiresAt).GetAwaiter().GetResult();
        _logger.LogDebug("Session refreshed: {SessionId}", sessionId[..8]);
    }

    private static string GenerateSecureSessionId()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }
}
```

### 7.4 BFF Auth Controller

```csharp
// AzureBank.Bff/Controllers/BffAuthController.cs
using System.Text.Json;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using Microsoft.AspNetCore.Mvc;

namespace AzureBank.Bff.Controllers;

[ApiController]
[Route("bff/auth")]
public class BffAuthController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly ISessionService _sessionService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BffAuthController> _logger;

    private const string SessionCookieName = "AzureBank.Session";

    public BffAuthController(
        IHttpClientFactory httpClientFactory,
        ISessionService sessionService,
        IConfiguration configuration,
        ILogger<BffAuthController> logger)
    {
        _httpClient = httpClientFactory.CreateClient("BackendApi");
        _sessionService = sessionService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Login - forwards to API, stores JWT server-side, returns session cookie
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/auth/login", request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, JsonDocument.Parse(content).RootElement);
            }

            var apiResponse = JsonSerializer.Deserialize<ApiResponse<LoginResponse>>(content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var loginResponse = apiResponse!.Data!;

            // Create server-side session with JWT
            var sessionId = _sessionService.CreateSession(
                loginResponse.Token,
                loginResponse.ExpiresAt,
                loginResponse.User.Id);

            // Set HTTP-only session cookie
            SetSessionCookie(sessionId, loginResponse.ExpiresAt);

            // Return user info (WITHOUT the JWT token)
            return Ok(new ApiResponse<BffLoginResponse>
            {
                Data = new BffLoginResponse
                {
                    User = loginResponse.User,
                    ExpiresAt = loginResponse.ExpiresAt
                },
                Message = "Login successful"
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to backend API");
            return StatusCode(503, new ErrorResponse
            {
                Type = "SERVICE_UNAVAILABLE",
                Message = "Service temporarily unavailable",
                CorrelationId = HttpContext.TraceIdentifier,
                StatusCode = 503
            });
        }
    }

    /// <summary>
    /// Register - forwards to API, stores JWT server-side, returns session cookie
    /// </summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/api/auth/register", request);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode, JsonDocument.Parse(content).RootElement);
            }

            var apiResponse = JsonSerializer.Deserialize<ApiResponse<LoginResponse>>(content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var loginResponse = apiResponse!.Data!;

            // Create server-side session with JWT
            var sessionId = _sessionService.CreateSession(
                loginResponse.Token,
                loginResponse.ExpiresAt,
                loginResponse.User.Id);

            // Set HTTP-only session cookie
            SetSessionCookie(sessionId, loginResponse.ExpiresAt);

            // Return user info (WITHOUT the JWT token)
            return CreatedAtAction(nameof(Register), new ApiResponse<BffLoginResponse>
            {
                Data = new BffLoginResponse
                {
                    User = loginResponse.User,
                    ExpiresAt = loginResponse.ExpiresAt
                },
                Message = "Registration successful"
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to backend API");
            return StatusCode(503, new ErrorResponse
            {
                Type = "SERVICE_UNAVAILABLE",
                Message = "Service temporarily unavailable",
                CorrelationId = HttpContext.TraceIdentifier,
                StatusCode = 503
            });
        }
    }

    /// <summary>
    /// Logout - revokes session, clears cookie
    /// </summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        if (Request.Cookies.TryGetValue(SessionCookieName, out var sessionId))
        {
            _sessionService.RevokeSession(sessionId);
        }

        Response.Cookies.Delete(SessionCookieName);
        return Ok(ApiResponse.Success("Logged out successfully"));
    }

    /// <summary>
    /// Check session status
    /// </summary>
    [HttpGet("session-status")]
    public IActionResult GetSessionStatus()
    {
        if (Request.Cookies.TryGetValue(SessionCookieName, out var sessionId))
        {
            if (_sessionService.ValidateSession(sessionId))
            {
                return Ok(new { isAuthenticated = true });
            }
        }

        return Ok(new { isAuthenticated = false });
    }

    private void SetSessionCookie(string sessionId, DateTime expiresAt)
    {
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = true,  // HTTPS only
            SameSite = SameSiteMode.Strict,
            Expires = expiresAt,
            Path = "/"
        };

        Response.Cookies.Append(SessionCookieName, sessionId, cookieOptions);
    }
}

/// <summary>
/// BFF login response - excludes JWT token
/// </summary>
public class BffLoginResponse
{
    public UserInfo User { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
}
```

### 7.5 YARP Bearer Token Transform

```csharp
// AzureBank.Bff/Transforms/BearerTokenTransform.cs
using AzureBank.Bff.Services.Interfaces;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace AzureBank.Bff.Transforms;

public class BearerTokenTransformProvider : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context) { }
    public void ValidateCluster(TransformClusterValidationContext context) { }

    public void Apply(TransformBuilderContext context)
    {
        context.AddRequestTransform(async transformContext =>
        {
            var sessionService = transformContext.HttpContext.RequestServices
                .GetRequiredService<ISessionService>();

            const string cookieName = "AzureBank.Session";

            if (transformContext.HttpContext.Request.Cookies.TryGetValue(cookieName, out var sessionId))
            {
                if (sessionService.TryGetToken(sessionId, out var token) && token != null)
                {
                    transformContext.ProxyRequest.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }
            }
        });
    }
}
```

### 7.6 YARP Configuration

```json
// AzureBank.Bff/appsettings.json
{
  "ReverseProxy": {
    "Routes": {
      "api-route": {
        "ClusterId": "backend-api",
        "Match": {
          "Path": "/api/{**catch-all}"
        },
        "Transforms": [
          { "PathRemovePrefix": "" }
        ]
      }
    },
    "Clusters": {
      "backend-api": {
        "Destinations": {
          "primary": {
            "Address": "https://localhost:5002"
          }
        },
        "HttpClient": {
          "DangerousAcceptAnyServerCertificate": true
        }
      }
    }
  },
  "BackendApi": {
    "BaseUrl": "https://localhost:5002"
  },
  "Session": {
    "IdleTimeout": 30,
    "MaxSessionDuration": 60
  }
}
```

### 7.7 Security Headers Middleware

```csharp
// AzureBank.Bff/Middleware/SecurityHeadersMiddleware.cs
namespace AzureBank.Bff.Middleware;

public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Security headers
        context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Append("X-Frame-Options", "DENY");
        context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
        context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        context.Response.Headers.Append("Permissions-Policy",
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()");

        // CSP - Content Security Policy
        context.Response.Headers.Append("Content-Security-Policy",
            "default-src 'self'; " +
            "script-src 'self'; " +
            "style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; " +
            "font-src 'self'; " +
            "connect-src 'self'; " +
            "frame-ancestors 'none';");

        await _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
```

### 7.8 BFF Program.cs

```csharp
// AzureBank.Bff/Program.cs
using AzureBank.Bff.Middleware;
using AzureBank.Bff.Services.Implementations;
using AzureBank.Bff.Services.Interfaces;
using AzureBank.Bff.Transforms;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
builder.Host.UseSerilog((context, config) =>
{
    config.ReadFrom.Configuration(context.Configuration);
});

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Session services
builder.Services.AddSingleton<ITokenStoreService, InMemoryTokenStore>();
builder.Services.AddSingleton<ISessionService, SessionService>();

// HTTP client for backend API
builder.Services.AddHttpClient("BackendApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["BackendApi:BaseUrl"]!);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// YARP Reverse Proxy
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms<BearerTokenTransformProvider>();

// Rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("fixed", config =>
    {
        config.PermitLimit = 100;
        config.Window = TimeSpan.FromMinutes(1);
        config.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        config.QueueLimit = 10;
    });
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:5173")
            .AllowCredentials()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// Middleware pipeline
app.UseSerilogRequestLogging();
app.UseSecurityHeaders();
app.UseCors("AllowFrontend");
app.UseRateLimiter();

app.MapControllers();
app.MapReverseProxy();

app.Run();
```

---

## 8. Middleware & Cross-Cutting Concerns

### 8.1 Global Exception Middleware

```csharp
// AzureBank.Api/Middleware/GlobalExceptionMiddleware.cs
using System.Text.Json;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Exceptions;
using FluentValidation;

namespace AzureBank.Api.Middleware;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var correlationId = context.TraceIdentifier;

        var (statusCode, errorCode, message, errors, details) = exception switch
        {
            AppException appEx => (
                appEx.StatusCode,
                appEx.ErrorCode,
                appEx.Message,
                null as Dictionary<string, string[]>,
                appEx.Details
            ),

            ValidationException validationEx => (
                422,
                ErrorCodes.ValidationError,
                "Validation failed",
                validationEx.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(
                        g => ToCamelCase(g.Key),
                        g => g.Select(e => e.ErrorMessage).ToArray()
                    ),
                null as Dictionary<string, object>
            ),

            _ => (
                500,
                ErrorCodes.InternalError,
                _environment.IsDevelopment() ? exception.Message : "An unexpected error occurred",
                null as Dictionary<string, string[]>,
                null as Dictionary<string, object>
            )
        };

        // Log based on severity
        if (statusCode >= 500)
        {
            _logger.LogError(exception,
                "Unhandled exception. CorrelationId: {CorrelationId}, Path: {Path}",
                correlationId, context.Request.Path);
        }
        else
        {
            _logger.LogWarning(
                "Handled exception. Type: {ExceptionType}, Code: {ErrorCode}, CorrelationId: {CorrelationId}",
                exception.GetType().Name, errorCode, correlationId);
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var response = new ErrorResponse
        {
            Type = errorCode,
            Message = message,
            CorrelationId = correlationId,
            StatusCode = statusCode,
            Errors = errors,
            Details = details
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await context.Response.WriteAsync(json);
    }

    private static string ToCamelCase(string str)
    {
        if (string.IsNullOrEmpty(str)) return str;
        return char.ToLowerInvariant(str[0]) + str[1..];
    }
}

public static class GlobalExceptionMiddlewareExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandler(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<GlobalExceptionMiddleware>();
    }
}
```

### 8.2 Correlation ID Middleware

```csharp
// AzureBank.Api/Middleware/CorrelationIdMiddleware.cs
namespace AzureBank.Api.Middleware;

public class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N");

        context.TraceIdentifier = correlationId;
        context.Response.Headers.Append(CorrelationIdHeader, correlationId);

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<CorrelationIdMiddleware>();
    }
}
```

### 8.3 Service Collection Extensions (API)

```csharp
// AzureBank.Api/Extensions/ServiceCollectionExtensions.cs
using System.Text;
using AzureBank.Api.Data;
using AzureBank.Api.Data.Seed;
using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Implementations;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Shared.Entities;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

namespace AzureBank.Api.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDatabase(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddDbContext<AzureBankDbContext>(options =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sqlOptions =>
                {
                    sqlOptions.EnableRetryOnFailure(3, TimeSpan.FromSeconds(30), null);
                    sqlOptions.CommandTimeout(30);
                });

            if (environment.IsDevelopment())
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        });

        services.AddScoped<DatabaseSeeder>();

        return services;
    }

    /// <summary>
    /// Configure ASP.NET Core Identity
    /// Provides: UserManager, RoleManager, SignInManager, password hashing, lockout, etc.
    /// </summary>
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
        {
            // Password requirements
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = false; // Special chars optional
            options.Password.RequiredUniqueChars = 4;

            // Lockout settings
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;

            // User settings
            options.User.RequireUniqueEmail = true;
            options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@";

            // Sign-in settings
            options.SignIn.RequireConfirmedEmail = false; // Set to true in production if needed
            options.SignIn.RequireConfirmedAccount = false;
        })
        .AddEntityFrameworkStores<AzureBankDbContext>()
        .AddDefaultTokenProviders();

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Core services
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<ITransactionService, TransactionService>();
        services.AddScoped<ITransferService, TransferService>();
        services.AddScoped<IUserService, UserService>();

        // JWT token service (generates tokens after Identity validates)
        services.AddSingleton<IJwtService, JwtService>();

        // Mappers (Mapperly generates these at compile-time)
        services.AddSingleton<AccountMapper>();
        services.AddSingleton<TransactionMapper>();
        services.AddSingleton<UserMapper>();

        return services;
    }

    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwtSettings = configuration.GetSection("Jwt").Get<JwtSettings>()!;
        services.Configure<JwtSettings>(configuration.GetSection("Jwt"));

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = jwtSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtSettings.Secret)),
                ClockSkew = TimeSpan.Zero
            };
        });

        services.AddAuthorization();

        return services;
    }

    public static IServiceCollection AddValidation(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<Program>();
        return services;
    }

    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi();
        return services;
    }
}
```

### 8.4 API Program.cs

```csharp
// AzureBank.Api/Program.cs
using AzureBank.Api.Data.Seed;
using AzureBank.Api.Extensions;
using AzureBank.Api.Middleware;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
builder.Host.UseSerilog((context, config) =>
{
    config
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .WriteTo.Console()
        .WriteTo.File("logs/azurebank-.log", rollingInterval: RollingInterval.Day);
});

// Add services using extension methods
builder.Services
    .AddDatabase(builder.Configuration, builder.Environment)
    .AddIdentityServices()              // ASP.NET Core Identity
    .AddJwtAuthentication(builder.Configuration)
    .AddApplicationServices()
    .AddValidation()
    .AddApiDocumentation();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// Middleware pipeline (order matters!)
app.UseCorrelationId();
app.UseSerilogRequestLogging();
app.UseGlobalExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();  // Scalar UI at /scalar/v1

    // Seed database in development
    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.SeedAsync();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
```

### 8.5 appsettings.json (API)

```json
// AzureBank.Api/appsettings.json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=AzureBank;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
  },
  "Jwt": {
    "Secret": "YOUR_SECRET_KEY_HERE_MINIMUM_32_CHARACTERS_LONG_FOR_HS256",
    "Issuer": "AzureBank.Api",
    "Audience": "AzureBank.Bff",
    "ExpirationMinutes": 15
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    }
  },
  "AllowedHosts": "*"
}
```

### 8.6 appsettings.Development.json (API)

```json
// AzureBank.Api/appsettings.Development.json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\MSSQLLocalDB;Database=AzureBank_Dev;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
  },
  "Jwt": {
    "Secret": "development_only_secret_key_minimum_32_chars_long!!"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft.AspNetCore": "Information",
        "Microsoft.EntityFrameworkCore": "Information"
      }
    }
  }
}
```

---

## 9. Configuration Files

### 9.1 AzureBank.Api.csproj

```xml
<!-- AzureBank.Api/AzureBank.Api.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AzureBank.Shared\AzureBank.Shared.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- EF Core -->
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
    </PackageReference>

    <!-- Identity & Authentication -->
    <PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.*" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.*" />

    <!-- Validation -->
    <PackageReference Include="FluentValidation.AspNetCore" Version="11.*" />

    <!-- API Docs -->
    <PackageReference Include="Scalar.AspNetCore" Version="1.*" />

    <!-- Logging -->
    <PackageReference Include="Serilog.AspNetCore" Version="8.*" />
    <PackageReference Include="Serilog.Formatting.Compact" Version="2.*" />
    <PackageReference Include="Serilog.Enrichers.Environment" Version="2.*" />

    <!-- Mapping -->
    <PackageReference Include="Riok.Mapperly" Version="4.*" />
  </ItemGroup>

</Project>
```

### 9.2 AzureBank.Bff.csproj

```xml
<!-- AzureBank.Bff/AzureBank.Bff.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AzureBank.Shared\AzureBank.Shared.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- YARP -->
    <PackageReference Include="Yarp.ReverseProxy" Version="2.*" />

    <!-- HTTP -->
    <PackageReference Include="Microsoft.Extensions.Http.Polly" Version="8.*" />

    <!-- Data Protection -->
    <PackageReference Include="Microsoft.AspNetCore.DataProtection" Version="10.*" />

    <!-- Logging -->
    <PackageReference Include="Serilog.AspNetCore" Version="8.*" />
    <PackageReference Include="Serilog.Formatting.Compact" Version="2.*" />
  </ItemGroup>

</Project>
```

### 9.3 launchSettings.json (API)

```json
// AzureBank.Api/Properties/launchSettings.json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "https": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "launchUrl": "scalar/v1",
      "applicationUrl": "https://localhost:5002;http://localhost:5003",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

### 9.4 launchSettings.json (BFF)

```json
// AzureBank.Bff/Properties/launchSettings.json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "https": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": false,
      "applicationUrl": "https://localhost:5001;http://localhost:5000",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

---

## 10. Implementation Checklist

### 10.1 Phase 1: Project Setup

- [ ] Create solution structure using CLI commands from Section 2.1
- [ ] Install all NuGet packages from Section 2.2
- [ ] Configure project references between projects
- [ ] Verify solution builds: `dotnet build`

### 10.2 Phase 2: Shared Library

- [ ] Create `Entities/BaseEntity.cs`
- [ ] Create `Entities/User.cs`
- [ ] Create `Entities/Account.cs`
- [ ] Create `Entities/Transaction.cs`
- [ ] Create `Entities/RefreshToken.cs`
- [ ] Create all Enum files in `Enums/`
- [ ] Create `Constants/ValidationRules.cs`
- [ ] Create `Constants/ErrorCodes.cs`
- [ ] Create all DTOs in `DTOs/` subfolders
- [ ] Create all Exception classes in `Exceptions/`
- [ ] Verify shared library builds

### 10.3 Phase 3: Database Layer

- [ ] Create `Data/AzureBankDbContext.cs`
- [ ] Create `Data/Configurations/UserConfiguration.cs`
- [ ] Create `Data/Configurations/AccountConfiguration.cs`
- [ ] Create `Data/Configurations/TransactionConfiguration.cs`
- [ ] Create `Data/Configurations/RefreshTokenConfiguration.cs`
- [ ] Create `Data/Seed/DatabaseSeeder.cs`
- [ ] Configure connection string in appsettings
- [ ] Run: `dotnet ef migrations add InitialCreate`
- [ ] Run: `dotnet ef database update`
- [ ] Verify tables created in database

### 10.4 Phase 4: API Services

- [ ] Create all interfaces in `Services/Interfaces/`
- [ ] Create `Services/Implementations/PasswordHasher.cs`
- [ ] Create `Services/Implementations/JwtService.cs`
- [ ] Create `Services/Implementations/AuthService.cs`
- [ ] Create `Services/Implementations/AccountService.cs`
- [ ] Create `Services/Implementations/TransactionService.cs`
- [ ] Create `Services/Implementations/TransferService.cs`
- [ ] Create `Services/Implementations/UserService.cs`

### 10.5 Phase 5: Validators & Mappers

- [ ] Create all validators in `Validators/` subfolders
- [ ] Create `Mappers/AccountMapper.cs`
- [ ] Create `Mappers/TransactionMapper.cs`
- [ ] Create `Mappers/UserMapper.cs`

### 10.6 Phase 6: Controllers

- [ ] Create `Controllers/AuthController.cs`
- [ ] Create `Controllers/AccountsController.cs`
- [ ] Create `Controllers/TransactionsController.cs`
- [ ] Create `Controllers/TransfersController.cs`
- [ ] Create `Controllers/UsersController.cs`

### 10.7 Phase 7: Middleware & DI

- [ ] Create `Middleware/GlobalExceptionMiddleware.cs`
- [ ] Create `Middleware/CorrelationIdMiddleware.cs`
- [ ] Create `Extensions/ServiceCollectionExtensions.cs`
- [ ] Configure `Program.cs`
- [ ] Configure `appsettings.json`

### 10.8 Phase 8: BFF Setup

- [ ] Create `Services/Interfaces/ISessionService.cs`
- [ ] Create `Services/Interfaces/ITokenStoreService.cs`
- [ ] Create `Services/Implementations/InMemoryTokenStore.cs`
- [ ] Create `Services/Implementations/SessionService.cs`
- [ ] Create `Controllers/BffAuthController.cs`
- [ ] Create `Transforms/BearerTokenTransform.cs`
- [ ] Create `Middleware/SecurityHeadersMiddleware.cs`
- [ ] Configure YARP in `appsettings.json`
- [ ] Configure BFF `Program.cs`

### 10.9 Phase 9: Testing

- [ ] Start API: `dotnet run --project src/AzureBank.Api`
- [ ] Start BFF: `dotnet run --project src/AzureBank.Bff`
- [ ] Test registration: `POST /bff/auth/register`
- [ ] Test login: `POST /bff/auth/login`
- [ ] Verify session cookie is set
- [ ] Test authenticated endpoint: `GET /api/accounts`
- [ ] Test logout: `POST /bff/auth/logout`

### 10.10 Final Verification

- [ ] All endpoints return correct response format
- [ ] Error responses match `ErrorResponse` structure
- [ ] JWT tokens never reach browser
- [ ] Session cookies have correct attributes
- [ ] Logs contain correlation IDs
- [ ] Database seeder creates test data
- [ ] Scalar API docs accessible at `/scalar/v1`

---

## Document History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-12-16 | Backend Lead | Initial skeleton |
| 2.0 | 2026-01-08 | Backend Lead | Complete implementation guide with BFF Pattern |

---

**Status**: COMPLETE - Phase 7 Backend Architecture
