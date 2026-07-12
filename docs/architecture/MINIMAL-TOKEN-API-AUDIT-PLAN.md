# Minimal Token API - Complete Audit & Execution Plan

**Task:** #21282 - Autenticazione tramite token
**Date:** 2026-01-23
**Backup Location:** `C:\Dev\BackupDevOpsBankSolution\AzureBank.Api`

---

## Executive Summary

Convert AzureBank.Api from full banking API to **minimal token authentication API** containing ONLY:
- Login
- Register
- Logout
- PIN management (Set/Verify)
- Current user endpoint (/me)

**SCOPE:** Only `src/AzureBank.Api/` project will be modified.
**NO CHANGES** to `src/AzureBank.Shared/` or `src/AzureBank.Infrastructure/`.

---

## PART 1: COMPLETE FILE AUDIT

### 1.1 API Project Structure (src/AzureBank.Api) - SCOPE OF CHANGES

| Category | Total | Keep | Delete | Modify |
|----------|-------|------|--------|--------|
| Controllers | 5 | 1 | 4 | 0 |
| Services/Interfaces | 6 | 2 | 4 | 0 |
| Services/Implementations | 6 | 2 | 4 | 0 |
| Validators | 10 | 4 | 6 | 0 |
| Mappers | 3 | 2 | 1 | 0 |
| Extensions | 2 | 2 | 0 | 1 |
| Middleware | 2 | 2 | 0 | 0 |
| Handlers | 3 | 3 | 0 | 0 |
| Converters | 2 | 2 | 0 | 0 |
| Transformers | 10 | 10 | 0 | 0 |
| **TOTAL** | **49** | **30** | **19** | **1** |

### 1.2 Shared Project (src/AzureBank.Shared) - NO CHANGES

| Category | Files | Status |
|----------|-------|--------|
| DTOs/Auth/* | 8 | KEEP ALL |
| DTOs/User/* | 3 | KEEP ALL |
| DTOs/Account/* | 6 | KEEP ALL |
| DTOs/Transaction/* | 5 | KEEP ALL |
| DTOs/Transfer/* | 4 | KEEP ALL |
| DTOs/Common/* | 2 | KEEP ALL |
| Enums/* | 3 | KEEP ALL |
| Entities/* | 5 | KEEP ALL |
| Options/* | 2 | KEEP ALL |
| Exceptions/* | 7 | KEEP ALL |
| Services/* | 2 | KEEP ALL |
| Validation/* | 5 | KEEP ALL |
| Constants/* | 2 | KEEP ALL |
| Utilities/* | 1 | KEEP ALL |
| **TOTAL** | **~55** | **NO DELETIONS** |

### 1.3 Infrastructure Project (src/AzureBank.Infrastructure) - NO CHANGES

All files remain untouched.

---

## PART 2: FILES TO DELETE (API Project Only)

### 2.1 Controllers (DELETE 4 files)

| File | Path | Reason |
|------|------|--------|
| AccountController.cs | `Controllers/AccountController.cs` | Not auth-related |
| TransactionController.cs | `Controllers/TransactionController.cs` | Not auth-related |
| TransferController.cs | `Controllers/TransferController.cs` | Not auth-related |
| UserController.cs | `Controllers/UserController.cs` | User search for transfers, not auth |

### 2.2 Services - Interfaces (DELETE 5 files)

| File | Path | Reason |
|------|------|--------|
| IAccountService.cs | `Services/Interfaces/IAccountService.cs` | Account CRUD not needed |
| IAccountAccessService.cs | `Services/Interfaces/IAccountAccessService.cs` | Ownership checks not needed |
| ITransactionService.cs | `Services/Interfaces/ITransactionService.cs` | Transactions not needed |
| ITransferService.cs | `Services/Interfaces/ITransferService.cs` | Transfers not needed |
| IUserService.cs | `Services/Interfaces/IUserService.cs` | User search not needed |

### 2.3 Services - Implementations (DELETE 5 files)

| File | Path | Reason |
|------|------|--------|
| AccountService.cs | `Services/Implementations/AccountService.cs` | Account CRUD not needed |
| AccountAccessService.cs | `Services/Implementations/AccountAccessService.cs` | Ownership checks not needed |
| TransactionService.cs | `Services/Implementations/TransactionService.cs` | Transactions not needed |
| TransferService.cs | `Services/Implementations/TransferService.cs` | Transfers not needed |
| UserService.cs | `Services/Implementations/UserService.cs` | User search not needed |

### 2.4 Validators (DELETE 6 files)

| File | Path | Reason |
|------|------|--------|
| CreateAccountRequestValidator.cs | `Validators/Account/CreateAccountRequestValidator.cs` | Not used by auth |
| UpdateAccountRequestValidator.cs | `Validators/Account/UpdateAccountRequestValidator.cs` | Not used by auth |
| DepositRequestValidator.cs | `Validators/Transaction/DepositRequestValidator.cs` | Not auth-related |
| WithdrawRequestValidator.cs | `Validators/Transaction/WithdrawRequestValidator.cs` | Not auth-related |
| TransferRequestValidator.cs | `Validators/Transfer/TransferRequestValidator.cs` | Not auth-related |
| InternalTransferRequestValidator.cs | `Validators/Transfer/InternalTransferRequestValidator.cs` | Not auth-related |

### 2.5 Mappers (DELETE 1 file)

| File | Path | Reason |
|------|------|--------|
| TransactionMapper.cs | `Mappers/TransactionMapper.cs` | Not used by auth |

### 2.6 Empty Folders to Remove

| Folder | Reason |
|--------|--------|
| `Validators/Account/` | Will be empty after deletions |
| `Validators/Transaction/` | Will be empty after deletions |
| `Validators/Transfer/` | Will be empty after deletions |

---

## PART 3: FILES TO KEEP (API Project)

### 3.1 Controllers (KEEP 1 file)

| File | Path | Purpose |
|------|------|---------|
| **AuthController.cs** | `Controllers/AuthController.cs` | Login, Register, Logout, PIN, /me |

### 3.2 Services - Interfaces (KEEP 2 files)

| File | Path | Purpose |
|------|------|---------|
| **IJwtService.cs** | `Services/Interfaces/IJwtService.cs` | JWT token operations |
| **IAuthService.cs** | `Services/Interfaces/IAuthService.cs` | Auth operations |

### 3.3 Services - Implementations (KEEP 2 files)

| File | Path | Purpose |
|------|------|---------|
| **JwtService.cs** | `Services/Implementations/JwtService.cs` | JWT generation/validation |
| **AuthService.cs** | `Services/Implementations/AuthService.cs` | Login, Register, PIN |

### 3.4 Validators (KEEP 4 files)

| File | Path | Purpose |
|------|------|---------|
| **LoginRequestValidator.cs** | `Validators/Auth/LoginRequestValidator.cs` | Login validation |
| **RegisterRequestValidator.cs** | `Validators/Auth/RegisterRequestValidator.cs` | Registration validation |
| **SetPinRequestValidator.cs** | `Validators/Auth/SetPinRequestValidator.cs` | PIN set validation |
| **VerifyPinRequestValidator.cs** | `Validators/Auth/VerifyPinRequestValidator.cs` | PIN verify validation |

### 3.5 Mappers (KEEP 2 files)

| File | Path | Purpose |
|------|------|---------|
| **AccountMapper.cs** | `Mappers/AccountMapper.cs` | Used by AuthService (registration creates account) |
| **UserMapper.cs** | `Mappers/UserMapper.cs` | Used by AuthService (login/register response) |

### 3.6 Other (KEEP ALL)

| Category | Files | Purpose |
|----------|-------|---------|
| **Program.cs** | 1 | Entry point (no changes needed) |
| **Extensions** | 2 | Service registration (needs modification) |
| **Middleware** | 2 | CorrelationId, InvalidRequest handling |
| **Handlers** | 3 | Exception handlers |
| **Converters** | 2 | JSON converters |
| **Transformers** | 10 | OpenAPI documentation |

---

## PART 4: FILES TO MODIFY

### 4.1 ServiceCollectionExtensions.cs

**File:** `src/AzureBank.Api/Extensions/ServiceCollectionExtensions.cs`

**Current AddApplicationServices method (lines 84-109):**
```csharp
public static IServiceCollection AddApplicationServices(
    this IServiceCollection services,
    IConfiguration configuration)
{
    // Configuration options
    services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
    services.Configure<SeedDataOptions>(
        configuration.GetSection(SeedDataOptions.SectionName));

    // Mappers (Mapperly source-generated, stateless - singleton is optimal)
    services.AddSingleton<AccountMapper>();
    services.AddSingleton<TransactionMapper>();        // <-- DELETE
    services.AddSingleton<UserMapper>();

    // Core services
    services.AddScoped<IPasswordHasher, Shared.Services.Implementations.PasswordHasher>();
    services.AddScoped<IJwtService, JwtService>();
    services.AddScoped<IAuthService, AuthService>();
    services.AddScoped<IAccountAccessService, AccountAccessService>();  // <-- DELETE
    services.AddScoped<IAccountService, AccountService>();              // <-- DELETE
    services.AddScoped<ITransactionService, TransactionService>();      // <-- DELETE
    services.AddScoped<ITransferService, TransferService>();            // <-- DELETE
    services.AddScoped<IUserService, UserService>();                    // <-- DELETE

    return services;
}
```

**New AddApplicationServices method:**
```csharp
public static IServiceCollection AddApplicationServices(
    this IServiceCollection services,
    IConfiguration configuration)
{
    // Configuration options
    services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
    services.Configure<SeedDataOptions>(
        configuration.GetSection(SeedDataOptions.SectionName));

    // Mappers (Mapperly source-generated, stateless - singleton is optimal)
    services.AddSingleton<AccountMapper>();
    services.AddSingleton<UserMapper>();

    // Core services (Token Authentication only)
    services.AddScoped<IPasswordHasher, Shared.Services.Implementations.PasswordHasher>();
    services.AddScoped<IJwtService, JwtService>();
    services.AddScoped<IAuthService, AuthService>();

    return services;
}
```

**Lines to DELETE:**
- Line 95: `services.AddSingleton<TransactionMapper>();`
- Line 102: `services.AddScoped<IAccountAccessService, AccountAccessService>();`
- Line 103: `services.AddScoped<IAccountService, AccountService>();`
- Line 104: `services.AddScoped<ITransactionService, TransactionService>();`
- Line 105: `services.AddScoped<ITransferService, TransferService>();`
- Line 106: `services.AddScoped<IUserService, UserService>();`

---

## PART 5: EXECUTION PLAN

### Phase 1: Pre-Execution Verification

#### 1.1 Verify Backup
```
Step 1.1.1: Confirm backup exists at C:\Dev\BackupDevOpsBankSolution\AzureBank.Api
Step 1.1.2: Verify backup contains all original files
```

#### 1.2 Clean Git State
```
Step 1.2.1: Run `git status` to verify clean state
Step 1.2.2: Ensure no staged files from previous attempt
```

---

### Phase 2: Delete API Project Files

#### 2.1 Delete Controllers (4 files)
```
Step 2.1.1: Delete Controllers/AccountController.cs
Step 2.1.2: Delete Controllers/TransactionController.cs
Step 2.1.3: Delete Controllers/TransferController.cs
Step 2.1.4: Delete Controllers/UserController.cs
```

#### 2.2 Delete Services/Interfaces (5 files)
```
Step 2.2.1: Delete Services/Interfaces/IAccountService.cs
Step 2.2.2: Delete Services/Interfaces/IAccountAccessService.cs
Step 2.2.3: Delete Services/Interfaces/ITransactionService.cs
Step 2.2.4: Delete Services/Interfaces/ITransferService.cs
Step 2.2.5: Delete Services/Interfaces/IUserService.cs
```

#### 2.3 Delete Services/Implementations (5 files)
```
Step 2.3.1: Delete Services/Implementations/AccountService.cs
Step 2.3.2: Delete Services/Implementations/AccountAccessService.cs
Step 2.3.3: Delete Services/Implementations/TransactionService.cs
Step 2.3.4: Delete Services/Implementations/TransferService.cs
Step 2.3.5: Delete Services/Implementations/UserService.cs
```

#### 2.4 Delete Validators (6 files)
```
Step 2.4.1: Delete Validators/Account/CreateAccountRequestValidator.cs
Step 2.4.2: Delete Validators/Account/UpdateAccountRequestValidator.cs
Step 2.4.3: Delete Validators/Transaction/DepositRequestValidator.cs
Step 2.4.4: Delete Validators/Transaction/WithdrawRequestValidator.cs
Step 2.4.5: Delete Validators/Transfer/TransferRequestValidator.cs
Step 2.4.6: Delete Validators/Transfer/InternalTransferRequestValidator.cs
```

#### 2.5 Delete Mappers (1 file)
```
Step 2.5.1: Delete Mappers/TransactionMapper.cs
```

#### 2.6 Delete Empty Folders
```
Step 2.6.1: Delete Validators/Account/ folder
Step 2.6.2: Delete Validators/Transaction/ folder
Step 2.6.3: Delete Validators/Transfer/ folder
```

---

### Phase 3: Modify ServiceCollectionExtensions.cs

#### 3.1 Remove Service Registrations
```
Step 3.1.1: Remove TransactionMapper registration
Step 3.1.2: Remove IAccountAccessService registration
Step 3.1.3: Remove IAccountService registration
Step 3.1.4: Remove ITransactionService registration
Step 3.1.5: Remove ITransferService registration
Step 3.1.6: Remove IUserService registration
```

---

### Phase 4: Build Verification

#### 4.1 Clean and Build
```
Step 4.1.1: Run `dotnet clean` to remove cached builds
Step 4.1.2: Run `dotnet build` to verify compilation
Step 4.1.3: Fix any compilation errors
```

#### 4.2 Runtime Verification
```
Step 4.2.1: Run `dotnet run` from API project
Step 4.2.2: Open Scalar API docs at /scalar/v1
Step 4.2.3: Verify only auth endpoints are visible:
           - POST /api/auth/login
           - POST /api/auth/register
           - GET /api/auth/me
           - POST /api/auth/logout
           - POST /api/auth/pin
           - POST /api/auth/pin/verify
```

---

### Phase 5: Git Commit

#### 5.1 Stage Files
```
Step 5.1.1: Run `git add -A src/AzureBank.Api/`
Step 5.1.2: Run `git status` to verify staged files
```

#### 5.2 Commit
```
Step 5.2.1: Create commit with Task #21282 reference
```

---

## PART 6: FINAL API ENDPOINTS

After cleanup, the API will expose ONLY these endpoints:

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| POST | /api/auth/login | Anonymous | Login with email/password |
| POST | /api/auth/register | Anonymous | Register new user |
| GET | /api/auth/me | Bearer | Get current user info |
| POST | /api/auth/logout | Bearer | Logout |
| POST | /api/auth/pin | Bearer | Set PIN |
| POST | /api/auth/pin/verify | Bearer | Verify PIN |

---

## PART 7: DEPENDENCY ANALYSIS

### Why AccountMapper is KEPT (Critical)

AuthService.cs line 132:
```csharp
return new RegisterResponse
{
    User = _userMapper.ToLoginInfo(user),
    Account = _accountMapper.ToResponse(account),  // <-- USED HERE
    Token = new TokenResponse { ... }
};
```

### Why Account Entity is KEPT (Critical)

AuthService.cs lines 111-123:
```csharp
var account = new Account
{
    UserId = user.Id,
    AccountNumber = IdGenerator.GenerateAccountNumber(),
    Name = "Primary Account",
    Type = AccountType.Checking,  // <-- AccountType enum used
    Balance = 0,
    IsPrimary = true,
    User = user
};
_context.Accounts.Add(account);  // <-- Account entity used
```

---

## PART 8: RISK ASSESSMENT

| Risk | Mitigation |
|------|------------|
| Backup loss | Backup verified at C:\Dev\BackupDevOpsBankSolution\AzureBank.Api |
| Compilation errors | Phase 4 build verification |
| Missing dependencies | Dependency analysis completed in Part 7 |
| Runtime failures | Phase 4 runtime verification |

---

## PART 9: SUMMARY

### Files to DELETE: 21 files (API Project ONLY)
- Controllers: 4 files
- Services/Interfaces: 5 files
- Services/Implementations: 5 files
- Validators: 6 files
- Mappers: 1 file

### Files to MODIFY: 1 file
- ServiceCollectionExtensions.cs (remove 6 service registrations)

### Projects NOT TOUCHED:
- `src/AzureBank.Shared/` - **NO CHANGES**
- `src/AzureBank.Infrastructure/` - **NO CHANGES**

### Result
Minimal Token Authentication API compliant with:
- OWASP JWT Security Best Practices
- RFC 9700 OAuth 2.0 Security (2025)
- RFC 9106 Argon2id Password Hashing
