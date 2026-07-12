# DTO Architecture Audit & Migration Plan

**Date:** January 26, 2026
**Scope:** Evaluate DTO placement across AzureBank solution
**Status:** AUDIT COMPLETE - Awaiting Implementation

---

## Executive Summary

### Current State
DTOs are centralized in `AzureBank.Shared` project, shared by API and BFF. This violates Clean Architecture principles where **Request DTOs should be owned by the layer that validates them** (API).

### Recommendation
**MOVE Request DTOs to API project.** Response DTOs and common wrappers remain in Shared.

### Why This Matters
| Concern | Current | Target |
|---------|---------|--------|
| Validation Coupling | Validators in API, DTOs in Shared | Both in API |
| BFF Independence | Uses API DTOs directly | BFF-specific DTOs |
| API Contract Ownership | Shared owns contract | API owns contract |
| Refactoring Safety | DTO change affects all projects | API changes isolated |

---

## Part 1: Complete Audit

### 1.1 Project Dependency Graph

```
┌─────────────────────────────────────────────────────────────────────┐
│                        CURRENT ARCHITECTURE                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌────────────────┐      ┌────────────────┐      ┌────────────────┐ │
│  │  AzureBank.Api │      │ AzureBank.Bff  │      │ AzureBank.Infra│ │
│  │                │      │                │      │                │ │
│  │  Controllers   │      │  BFF Gateway   │      │  DbContext     │ │
│  │  Services      │      │  YARP Proxy    │      │  Repositories  │ │
│  │  Validators    │◄─────┤  Session Mgmt  │      │                │ │
│  │  Mappers       │      │                │      │                │ │
│  │  Transformers  │      │                │      │                │ │
│  └───────┬────────┘      └───────┬────────┘      └───────┬────────┘ │
│          │                       │                       │          │
│          │                       │                       │          │
│          ▼                       ▼                       ▼          │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │                     AzureBank.Shared                          │  │
│  │                                                                │  │
│  │  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐          │  │
│  │  │ DTOs    │  │ Entities│  │ Enums   │  │Constants│          │  │
│  │  │ (ALL)   │  │         │  │         │  │         │          │  │
│  │  └─────────┘  └─────────┘  └─────────┘  └─────────┘          │  │
│  │                                                                │  │
│  │  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐          │  │
│  │  │Exceptions│ │Validation│ │ Options │  │Utilities│          │  │
│  │  │         │  │Attributes│  │         │  │         │          │  │
│  │  └─────────┘  └─────────┘  └─────────┘  └─────────┘          │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### 1.2 DTO Classification Matrix

| Category | Count | Current Location | Recommended Location |
|----------|-------|------------------|---------------------|
| **Request DTOs** | 11 | Shared | → **API** |
| **Response DTOs** | 15 | Shared | Shared (keep) |
| **Filter/Query DTOs** | 1 | Shared | → **API** |
| **Common Wrappers** | 3 | Shared | Shared (keep) |
| **TOTAL** | 30 | - | - |

### 1.3 Complete DTO Inventory

#### REQUEST DTOs (Move to API)

| DTO | Current Path | Validators In API | BFF Uses |
|-----|--------------|-------------------|----------|
| `LoginRequest` | Shared/DTOs/Auth | ✅ Yes | ✅ Yes |
| `RegisterRequest` | Shared/DTOs/Auth | ✅ Yes | ✅ Yes |
| `SetPinRequest` | Shared/DTOs/Auth | ✅ Yes | ✅ Yes |
| `VerifyPinRequest` | Shared/DTOs/Auth | ✅ Yes | ✅ Yes |
| `CreateAccountRequest` | Shared/DTOs/Account | ✅ Yes | ❌ No |
| `UpdateAccountRequest` | Shared/DTOs/Account | ✅ Yes | ❌ No |
| `SetPrimaryAccountRequest` | Shared/DTOs/Account | 🔲 Planned | ❌ No |
| `DepositRequest` | Shared/DTOs/Transaction | ✅ Yes | ❌ No |
| `WithdrawRequest` | Shared/DTOs/Transaction | ✅ Yes | ❌ No |
| `TransferRequest` | Shared/DTOs/Transfer | ✅ Yes | ❌ No |
| `InternalTransferRequest` | Shared/DTOs/Transfer | ✅ Yes | ❌ No |
| `TransactionFilter` | Shared/DTOs/Transaction | ❌ No | ❌ No |

**Total Request DTOs: 12** (11 + 1 filter)

#### RESPONSE DTOs (Keep in Shared)

| DTO | Current Path | Used By API | Used By BFF |
|-----|--------------|-------------|-------------|
| `LoginResponse` | Shared/DTOs/Auth | ✅ | ✅ |
| `RegisterResponse` | Shared/DTOs/Auth | ✅ | ✅ |
| `TokenResponse` | Shared/DTOs/Auth | ✅ | ✅ |
| `UserLoginInfo` | Shared/DTOs/Auth | ✅ | ✅ |
| `AccountResponse` | Shared/DTOs/Account | ✅ | ✅ (proxy) |
| `AccountSummaryResponse` | Shared/DTOs/Account | ✅ | ✅ (proxy) |
| `BalanceResponse` | Shared/DTOs/Account | ✅ | ✅ (proxy) |
| `TransactionResponse` | Shared/DTOs/Transaction | ✅ | ✅ (proxy) |
| `DepositResponse` | Shared/DTOs/Transaction | ✅ | ✅ (proxy) |
| `WithdrawResponse` | Shared/DTOs/Transaction | ✅ | ✅ (proxy) |
| `TransferResponse` | Shared/DTOs/Transfer | ✅ | ✅ (proxy) |
| `InternalTransferResponse` | Shared/DTOs/Transfer | ✅ | ✅ (proxy) |
| `UserResponse` | Shared/DTOs/User | ✅ | ✅ |
| `RecipientSearchResult` | Shared/DTOs/User | ✅ | ✅ (proxy) |
| `RecipientLookupResponse` | Shared/DTOs/User | ✅ | ✅ (proxy) |

**Total Response DTOs: 15**

#### COMMON WRAPPERS (Keep in Shared)

| DTO | Purpose | Used By |
|-----|---------|---------|
| `ApiResponse<T>` | Generic success wrapper | API, BFF |
| `ApiResponse` | Simple success wrapper | API, BFF |
| `PaginatedResponse<T>` | Pagination wrapper | API |
| `PaginationMetadata` | Pagination metadata | API |

**Total Wrappers: 4**

### 1.4 BFF DTO Usage Analysis

The BFF currently uses Shared DTOs in two ways:

**1. Auth Endpoints (Direct Use)**
```csharp
// BFF creates LoginRequest, sends to API
[HttpPost("login")]
public async Task<IActionResult> Login([FromBody] LoginRequest request)
```

**2. Proxy Endpoints (Pass-Through)**
```csharp
// BFF proxies request body to API unchanged
// Response DTOs returned to frontend
```

**Problem:** BFF is tightly coupled to API's request DTOs.

**Enterprise Best Practice:** BFF should have its own `BffLoginRequest` that maps to API's `LoginRequest`.

### 1.5 Files That Reference Request DTOs

#### In AzureBank.Api

| File Type | Files | Impact |
|-----------|-------|--------|
| Controllers | 5 | Update `using` statements |
| Services | 7 | Already receive DTOs as params |
| Validators | 10 | Update `using` statements |
| Mappers | 3 | Update `using` statements |

#### In AzureBank.Bff

| File | Current Usage | Post-Migration |
|------|---------------|----------------|
| `BffAuthController.cs` | Uses `LoginRequest`, `RegisterRequest`, `SetPinRequest`, `VerifyPinRequest` | Create BFF-specific DTOs |

#### In AzureBank.Infrastructure

| Usage | Files | Impact |
|-------|-------|--------|
| None | 0 | No changes needed |

---

## Part 2: Enterprise Best Practices Analysis

### 2.1 Clean Architecture Guidance

> **Uncle Bob:** "The important thing is that isolated, simple, data structures are passed across the boundaries."

**Interpretation:** DTOs at each boundary (API, BFF) should be owned by that layer.

### 2.2 Microsoft Microservices Architecture

> **Microsoft Learn:** "A microservice API is a contract between the service and its clients. You'll be able to evolve a microservice independently only if you do not break its API contract."

**Interpretation:** API owns its request contract (DTOs). Changes don't cascade.

### 2.3 BFF Pattern Guidance

> **Sam Newman:** "Each BFF is tailored to the needs of a specific frontend."

**Interpretation:** BFF should have its own DTOs, not share with API.

### 2.4 FluentValidation Placement

> **Enterprise Pattern:** "Validators live alongside Commands/Queries (which ARE the request DTOs)."

**Interpretation:** Request DTOs + Validators should be co-located in API.

### 2.5 Summary Decision Matrix

| Question | Answer | Confidence |
|----------|--------|------------|
| Should Request DTOs move to API? | **YES** | High |
| Should Response DTOs stay in Shared? | **YES** | High |
| Should BFF have its own DTOs? | **YES** (auth only) | Medium |
| Should this happen before or after FluentValidation migration? | **AFTER** | High |

---

## Part 3: Target Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                        TARGET ARCHITECTURE                           │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │                        AzureBank.Api                            │ │
│  │  ┌──────────────────────────────────────────────────────────┐  │ │
│  │  │                    DTOs/ (NEW)                            │  │ │
│  │  │  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐     │  │ │
│  │  │  │Auth/    │  │Account/ │  │Trans/   │  │Transfer/│     │  │ │
│  │  │  │Request  │  │Request  │  │Request  │  │Request  │     │  │ │
│  │  │  └─────────┘  └─────────┘  └─────────┘  └─────────┘     │  │ │
│  │  └──────────────────────────────────────────────────────────┘  │ │
│  │                                                                  │ │
│  │  ┌──────────────────────────────────────────────────────────┐  │ │
│  │  │                    Validators/                            │  │ │
│  │  │  (Same structure, references local DTOs)                  │  │ │
│  │  └──────────────────────────────────────────────────────────┘  │ │
│  │                                                                  │ │
│  │  Controllers, Services, Mappers, Transformers                   │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                          │                                          │
│                          │ References                               │
│                          ▼                                          │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │                      AzureBank.Shared                           │ │
│  │  ┌──────────────────────────────────────────────────────────┐  │ │
│  │  │                    DTOs/ (KEPT)                           │  │ │
│  │  │  ┌─────────┐  ┌─────────┐                                │  │ │
│  │  │  │Response/│  │Common/  │  (Response DTOs + Wrappers)    │  │ │
│  │  │  │(All)    │  │         │                                │  │ │
│  │  │  └─────────┘  └─────────┘                                │  │ │
│  │  └──────────────────────────────────────────────────────────┘  │ │
│  │                                                                  │ │
│  │  Entities, Enums, Constants, Exceptions, Options, Utilities     │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                          ▲                                          │
│                          │ References                               │
│                          │                                          │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │                        AzureBank.Bff                            │ │
│  │  ┌──────────────────────────────────────────────────────────┐  │ │
│  │  │                    Models/ (NEW)                          │  │ │
│  │  │  ┌─────────────────────────────────────────────────────┐ │  │ │
│  │  │  │ BffLoginRequest, BffRegisterRequest, etc.            │ │  │ │
│  │  │  │ (BFF-specific DTOs for auth endpoints)               │ │  │ │
│  │  │  └─────────────────────────────────────────────────────┘ │  │ │
│  │  └──────────────────────────────────────────────────────────┘  │ │
│  │                                                                  │ │
│  │  No reference to AzureBank.Api (decoupled)                      │ │
│  └────────────────────────────────────────────────────────────────┘ │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Part 4: Sequenced Implementation Plan

### Plan Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                    SEQUENTIAL IMPLEMENTATION PLAN                    │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │  PLAN A: FluentValidation Migration                          │   │
│  │  (From existing audit - 7 phases)                            │   │
│  │  Status: Ready to implement                                  │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                              │                                       │
│                              ▼                                       │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │  PLAN B: DTO Migration (THIS DOCUMENT)                       │   │
│  │  Move Request DTOs from Shared to API                        │   │
│  │  Status: Planned (execute after Plan A)                      │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                              │                                       │
│                              ▼                                       │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │  PLAN C: BFF DTO Independence (Future)                       │   │
│  │  Create BFF-specific DTOs for auth endpoints                 │   │
│  │  Status: Planned (execute after Plan B)                      │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

---

## PLAN B: DTO Migration - Detailed Implementation

### Phase B1: Preparation & Setup

#### Sub-Phase B1.1: Create Target Structure

| Step | Action | Details |
|------|--------|---------|
| B1.1.1 | Create API DTOs folder structure | `src/AzureBank.Api/DTOs/` |
| B1.1.2 | Create Account subfolder | `DTOs/Account/` |
| B1.1.3 | Create Auth subfolder | `DTOs/Auth/` |
| B1.1.4 | Create Transaction subfolder | `DTOs/Transaction/` |
| B1.1.5 | Create Transfer subfolder | `DTOs/Transfer/` |

**Folder Structure to Create:**
```
src/AzureBank.Api/
└── DTOs/
    ├── Account/
    ├── Auth/
    ├── Transaction/
    └── Transfer/
```

#### Sub-Phase B1.2: Build Verification

| Step | Action | Details |
|------|--------|---------|
| B1.2.1 | Run `dotnet build` | Verify solution builds cleanly |
| B1.2.2 | Run tests | Verify all tests pass |
| B1.2.3 | Create git branch | `feature/dto-migration` |
| B1.2.4 | Commit baseline | Commit clean state |

---

### Phase B2: Move Account Request DTOs

#### Sub-Phase B2.1: Move Files

| Step | Source | Destination |
|------|--------|-------------|
| B2.1.1 | `Shared/DTOs/Account/CreateAccountRequest.cs` | `Api/DTOs/Account/CreateAccountRequest.cs` |
| B2.1.2 | `Shared/DTOs/Account/UpdateAccountRequest.cs` | `Api/DTOs/Account/UpdateAccountRequest.cs` |
| B2.1.3 | `Shared/DTOs/Account/SetPrimaryAccountRequest.cs` | `Api/DTOs/Account/SetPrimaryAccountRequest.cs` |

#### Sub-Phase B2.2: Update Namespaces

| Step | File | Old Namespace | New Namespace |
|------|------|---------------|---------------|
| B2.2.1 | CreateAccountRequest.cs | `AzureBank.Shared.DTOs.Account` | `AzureBank.Api.DTOs.Account` |
| B2.2.2 | UpdateAccountRequest.cs | `AzureBank.Shared.DTOs.Account` | `AzureBank.Api.DTOs.Account` |
| B2.2.3 | SetPrimaryAccountRequest.cs | `AzureBank.Shared.DTOs.Account` | `AzureBank.Api.DTOs.Account` |

#### Sub-Phase B2.3: Update References

| Step | File | Change |
|------|------|--------|
| B2.3.1 | `AccountController.cs` | Update `using` to `AzureBank.Api.DTOs.Account` |
| B2.3.2 | `AccountService.cs` | Update `using` statement |
| B2.3.3 | `IAccountService.cs` | Update `using` statement |
| B2.3.4 | `AccountMapper.cs` | Update `using` statement |
| B2.3.5 | `CreateAccountRequestValidator.cs` | Update `using` statement |
| B2.3.6 | `UpdateAccountRequestValidator.cs` | Update `using` statement |

#### Sub-Phase B2.4: Verify & Commit

| Step | Action |
|------|--------|
| B2.4.1 | `dotnet build` - verify compilation |
| B2.4.2 | Run account-related tests |
| B2.4.3 | Commit: "Move Account Request DTOs to API project" |

---

### Phase B3: Move Auth Request DTOs

#### Sub-Phase B3.1: Move Files

| Step | Source | Destination |
|------|--------|-------------|
| B3.1.1 | `Shared/DTOs/Auth/LoginRequest.cs` | `Api/DTOs/Auth/LoginRequest.cs` |
| B3.1.2 | `Shared/DTOs/Auth/RegisterRequest.cs` | `Api/DTOs/Auth/RegisterRequest.cs` |
| B3.1.3 | `Shared/DTOs/Auth/SetPinRequest.cs` | `Api/DTOs/Auth/SetPinRequest.cs` |
| B3.1.4 | `Shared/DTOs/Auth/VerifyPinRequest.cs` | `Api/DTOs/Auth/VerifyPinRequest.cs` |

#### Sub-Phase B3.2: Update Namespaces

| Step | File | New Namespace |
|------|------|---------------|
| B3.2.1 | LoginRequest.cs | `AzureBank.Api.DTOs.Auth` |
| B3.2.2 | RegisterRequest.cs | `AzureBank.Api.DTOs.Auth` |
| B3.2.3 | SetPinRequest.cs | `AzureBank.Api.DTOs.Auth` |
| B3.2.4 | VerifyPinRequest.cs | `AzureBank.Api.DTOs.Auth` |

#### Sub-Phase B3.3: Update API References

| Step | File | Change |
|------|------|--------|
| B3.3.1 | `AuthController.cs` | Update `using` to `AzureBank.Api.DTOs.Auth` |
| B3.3.2 | `AuthService.cs` | Update `using` statement |
| B3.3.3 | `IAuthService.cs` | Update `using` statement |
| B3.3.4 | `LoginRequestValidator.cs` | Update `using` statement |
| B3.3.5 | `RegisterRequestValidator.cs` | Update `using` statement |
| B3.3.6 | `SetPinRequestValidator.cs` | Update `using` statement |
| B3.3.7 | `VerifyPinRequestValidator.cs` | Update `using` statement |

#### Sub-Phase B3.4: Handle BFF Impact (Critical)

**Important:** BFF currently uses these Auth Request DTOs. Two options:

**Option A: Temporary - Keep copies in Shared (Not Recommended)**
- Duplicates code
- Defeats purpose of migration

**Option B: Create BFF-Specific DTOs (Recommended)**
- This is what PLAN C handles in detail
- For now, we'll mark this as a breaking change for BFF

| Step | Action |
|------|--------|
| B3.4.1 | Add TODO comment in BFF | Mark files needing update |
| B3.4.2 | Document breaking change | Update CLAUDE-CONTEXT.md |

#### Sub-Phase B3.5: Verify & Commit

| Step | Action |
|------|--------|
| B3.5.1 | `dotnet build` - API project only |
| B3.5.2 | Note: BFF will have build errors (expected) |
| B3.5.3 | Commit: "Move Auth Request DTOs to API project (BFF breaking)" |

---

### Phase B4: Move Transaction Request DTOs

#### Sub-Phase B4.1: Move Files

| Step | Source | Destination |
|------|--------|-------------|
| B4.1.1 | `Shared/DTOs/Transaction/DepositRequest.cs` | `Api/DTOs/Transaction/DepositRequest.cs` |
| B4.1.2 | `Shared/DTOs/Transaction/WithdrawRequest.cs` | `Api/DTOs/Transaction/WithdrawRequest.cs` |
| B4.1.3 | `Shared/DTOs/Transaction/TransactionFilter.cs` | `Api/DTOs/Transaction/TransactionFilter.cs` |

#### Sub-Phase B4.2: Update Namespaces & References

| Step | File | Change |
|------|------|--------|
| B4.2.1 | DepositRequest.cs | Namespace → `AzureBank.Api.DTOs.Transaction` |
| B4.2.2 | WithdrawRequest.cs | Namespace → `AzureBank.Api.DTOs.Transaction` |
| B4.2.3 | TransactionFilter.cs | Namespace → `AzureBank.Api.DTOs.Transaction` |
| B4.2.4 | `TransactionController.cs` | Update `using` statements |
| B4.2.5 | `TransactionService.cs` | Update `using` statements |
| B4.2.6 | `ITransactionService.cs` | Update `using` statements |
| B4.2.7 | `DepositRequestValidator.cs` | Update `using` statements |
| B4.2.8 | `WithdrawRequestValidator.cs` | Update `using` statements |

#### Sub-Phase B4.3: Verify & Commit

| Step | Action |
|------|--------|
| B4.3.1 | `dotnet build` |
| B4.3.2 | Commit: "Move Transaction Request DTOs to API project" |

---

### Phase B5: Move Transfer Request DTOs

#### Sub-Phase B5.1: Move Files

| Step | Source | Destination |
|------|--------|-------------|
| B5.1.1 | `Shared/DTOs/Transfer/TransferRequest.cs` | `Api/DTOs/Transfer/TransferRequest.cs` |
| B5.1.2 | `Shared/DTOs/Transfer/InternalTransferRequest.cs` | `Api/DTOs/Transfer/InternalTransferRequest.cs` |

#### Sub-Phase B5.2: Update Namespaces & References

| Step | File | Change |
|------|------|--------|
| B5.2.1 | TransferRequest.cs | Namespace → `AzureBank.Api.DTOs.Transfer` |
| B5.2.2 | InternalTransferRequest.cs | Namespace → `AzureBank.Api.DTOs.Transfer` |
| B5.2.3 | `TransferController.cs` | Update `using` statements |
| B5.2.4 | `TransferService.cs` | Update `using` statements |
| B5.2.5 | `ITransferService.cs` | Update `using` statements |
| B5.2.6 | `TransferRequestValidator.cs` | Update `using` statements |
| B5.2.7 | `InternalTransferRequestValidator.cs` | Update `using` statements |

#### Sub-Phase B5.3: Verify & Commit

| Step | Action |
|------|--------|
| B5.3.1 | `dotnet build` |
| B5.3.2 | Commit: "Move Transfer Request DTOs to API project" |

---

### Phase B6: Cleanup Shared Project

#### Sub-Phase B6.1: Delete Moved Files from Shared

| Step | File to Delete |
|------|----------------|
| B6.1.1 | `Shared/DTOs/Account/CreateAccountRequest.cs` |
| B6.1.2 | `Shared/DTOs/Account/UpdateAccountRequest.cs` |
| B6.1.3 | `Shared/DTOs/Account/SetPrimaryAccountRequest.cs` |
| B6.1.4 | `Shared/DTOs/Auth/LoginRequest.cs` |
| B6.1.5 | `Shared/DTOs/Auth/RegisterRequest.cs` |
| B6.1.6 | `Shared/DTOs/Auth/SetPinRequest.cs` |
| B6.1.7 | `Shared/DTOs/Auth/VerifyPinRequest.cs` |
| B6.1.8 | `Shared/DTOs/Transaction/DepositRequest.cs` |
| B6.1.9 | `Shared/DTOs/Transaction/WithdrawRequest.cs` |
| B6.1.10 | `Shared/DTOs/Transaction/TransactionFilter.cs` |
| B6.1.11 | `Shared/DTOs/Transfer/TransferRequest.cs` |
| B6.1.12 | `Shared/DTOs/Transfer/InternalTransferRequest.cs` |

**Total: 12 files deleted**

#### Sub-Phase B6.2: Reorganize Shared DTOs Folder

After deletion, Shared/DTOs should contain only:

```
Shared/DTOs/
├── Common/
│   ├── ApiResponse.cs
│   └── PaginatedResponse.cs
├── Account/
│   ├── AccountResponse.cs
│   ├── AccountSummaryResponse.cs
│   └── BalanceResponse.cs
├── Auth/
│   ├── LoginResponse.cs
│   ├── RegisterResponse.cs
│   ├── TokenResponse.cs
│   └── UserLoginInfo.cs
├── Transaction/
│   ├── TransactionResponse.cs
│   ├── DepositResponse.cs
│   └── WithdrawResponse.cs
├── Transfer/
│   ├── TransferResponse.cs
│   └── InternalTransferResponse.cs
└── User/
    ├── UserResponse.cs
    ├── RecipientSearchResult.cs
    └── RecipientLookupResponse.cs
```

#### Sub-Phase B6.3: Verify & Commit

| Step | Action |
|------|--------|
| B6.3.1 | `dotnet build` - Full solution |
| B6.3.2 | Verify no orphan references |
| B6.3.3 | Commit: "Clean up Shared DTOs after migration" |

---

### Phase B7: API Project Final Verification

#### Sub-Phase B7.1: Build & Test

| Step | Action | Expected |
|------|--------|----------|
| B7.1.1 | `dotnet build AzureBank.Api` | Success |
| B7.1.2 | Run all API tests | All pass |
| B7.1.3 | Start API server | Starts without errors |
| B7.1.4 | Test Scalar/OpenAPI | DTOs documented correctly |

#### Sub-Phase B7.2: Verify DTO Structure

| Step | Verification |
|------|--------------|
| B7.2.1 | Check `Api/DTOs/Account/` has 3 files |
| B7.2.2 | Check `Api/DTOs/Auth/` has 4 files |
| B7.2.3 | Check `Api/DTOs/Transaction/` has 3 files |
| B7.2.4 | Check `Api/DTOs/Transfer/` has 2 files |

**Total Request DTOs in API: 12 files**

#### Sub-Phase B7.3: Final Commit

| Step | Action |
|------|--------|
| B7.3.1 | Commit: "Complete DTO migration - Phase B complete" |
| B7.3.2 | Create PR or merge to main |

---

## PLAN C: BFF DTO Independence (Future Sprint)

> **Note:** Execute this AFTER Plan B is complete.

### Phase C1: Create BFF Models Structure

| Step | Action |
|------|--------|
| C1.1 | Create `Bff/Models/` folder |
| C1.2 | Create `Bff/Models/Auth/` subfolder |

### Phase C2: Create BFF-Specific Auth DTOs

| Step | File to Create | Based On |
|------|----------------|----------|
| C2.1 | `BffLoginRequest.cs` | LoginRequest |
| C2.2 | `BffRegisterRequest.cs` | RegisterRequest |
| C2.3 | `BffSetPinRequest.cs` | SetPinRequest |
| C2.4 | `BffVerifyPinRequest.cs` | VerifyPinRequest |

### Phase C3: Update BFF Controller

| Step | File | Change |
|------|------|--------|
| C3.1 | `BffAuthController.cs` | Use BFF-specific DTOs |
| C3.2 | Create mapper | Map BFF DTOs to API request format |

### Phase C4: Add BFF Validation (Optional)

| Step | File | Purpose |
|------|------|---------|
| C4.1 | `BffLoginRequestValidator.cs` | BFF-specific validation |
| C4.2 | etc. | As needed |

---

## Files Summary

### PLAN B: Files to Create

| # | Path | Description |
|---|------|-------------|
| 1 | `Api/DTOs/Account/CreateAccountRequest.cs` | Moved from Shared |
| 2 | `Api/DTOs/Account/UpdateAccountRequest.cs` | Moved from Shared |
| 3 | `Api/DTOs/Account/SetPrimaryAccountRequest.cs` | Moved from Shared |
| 4 | `Api/DTOs/Auth/LoginRequest.cs` | Moved from Shared |
| 5 | `Api/DTOs/Auth/RegisterRequest.cs` | Moved from Shared |
| 6 | `Api/DTOs/Auth/SetPinRequest.cs` | Moved from Shared |
| 7 | `Api/DTOs/Auth/VerifyPinRequest.cs` | Moved from Shared |
| 8 | `Api/DTOs/Transaction/DepositRequest.cs` | Moved from Shared |
| 9 | `Api/DTOs/Transaction/WithdrawRequest.cs` | Moved from Shared |
| 10 | `Api/DTOs/Transaction/TransactionFilter.cs` | Moved from Shared |
| 11 | `Api/DTOs/Transfer/TransferRequest.cs` | Moved from Shared |
| 12 | `Api/DTOs/Transfer/InternalTransferRequest.cs` | Moved from Shared |

### PLAN B: Files to Delete

| # | Path |
|---|------|
| 1-12 | Same 12 files from Shared (after moving) |

### PLAN B: Files to Modify

| # | Path | Change |
|---|------|--------|
| 1 | `AccountController.cs` | Update using statements |
| 2 | `AuthController.cs` | Update using statements |
| 3 | `TransactionController.cs` | Update using statements |
| 4 | `TransferController.cs` | Update using statements |
| 5 | `UserController.cs` | Check if affected |
| 6 | `AccountService.cs` | Update using statements |
| 7 | `AuthService.cs` | Update using statements |
| 8 | `TransactionService.cs` | Update using statements |
| 9 | `TransferService.cs` | Update using statements |
| 10-16 | All 7 service interfaces | Update using statements |
| 17-26 | All 10 validators | Update using statements |
| 27-29 | All 3 mappers | Update using statements |

**Total files to modify: ~29**

---

## Dependency Order

```
┌────────────────────────────────────────────────────────────────────┐
│                     EXECUTION DEPENDENCY GRAPH                      │
├────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  PLAN A: FluentValidation Migration                                │
│  ├── Phase 1: Create missing validators                            │
│  ├── Phase 2: Enhance schema transformer                           │
│  ├── Phase 3: Remove Data Annotations from DTOs      ◄─────────┐  │
│  ├── Phase 4: Delete custom validation attributes              │  │
│  ├── Phase 5: Delete DataAnnotationSchemaTransformer           │  │
│  ├── Phase 6: Testing                                          │  │
│  └── Phase 7: Documentation                                    │  │
│                              │                                  │  │
│                              ▼                                  │  │
│  ─────────────────────────────────────────────────────────────│──│
│                              │                                  │  │
│  PLAN B: DTO Migration       │ DEPENDS ON PLAN A               │  │
│  ├── Phase B1: Preparation   │ (DTOs must be annotation-free   │  │
│  ├── Phase B2: Move Account  │  before moving)                 │  │
│  ├── Phase B3: Move Auth     │                                 │  │
│  ├── Phase B4: Move Transaction                                │  │
│  ├── Phase B5: Move Transfer                                   │  │
│  ├── Phase B6: Cleanup Shared ────────────────────────────────┘  │
│  └── Phase B7: Final Verification                                 │
│                              │                                     │
│                              ▼                                     │
│  ─────────────────────────────────────────────────────────────────│
│                              │                                     │
│  PLAN C: BFF DTO Independence │ DEPENDS ON PLAN B                 │
│  ├── Phase C1: Create structure                                   │
│  ├── Phase C2: Create BFF DTOs                                    │
│  ├── Phase C3: Update controller                                  │
│  └── Phase C4: Add validation (optional)                          │
│                                                                     │
└────────────────────────────────────────────────────────────────────┘
```

---

## Risk Assessment

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| Build errors during migration | Medium | Low | Commit after each phase |
| BFF breaks after Plan B | High | Medium | Plan C resolves this |
| Missed references | Low | Low | Use IDE "Find All References" |
| OpenAPI schema changes | Low | Low | Schema transformer handles |

---

## Estimated Effort

| Plan | Phases | Estimated Effort |
|------|--------|------------------|
| Plan A | 7 | 2-3 hours |
| Plan B | 7 | 1-2 hours |
| Plan C | 4 | 1 hour |
| **Total** | **18** | **4-6 hours** |

---

## Appendix: Enterprise Patterns Reference

### Pattern 1: Request DTOs in API Layer

```csharp
// AzureBank.Api/DTOs/Account/CreateAccountRequest.cs
namespace AzureBank.Api.DTOs.Account;

public class CreateAccountRequest
{
    public required string Name { get; set; }
    public required AccountType Type { get; set; }
}
```

### Pattern 2: Response DTOs in Shared Layer

```csharp
// AzureBank.Shared/DTOs/Account/AccountResponse.cs
namespace AzureBank.Shared.DTOs.Account;

public class AccountResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    // ... shared across API and BFF
}
```

### Pattern 3: BFF-Specific DTOs

```csharp
// AzureBank.Bff/Models/Auth/BffLoginRequest.cs
namespace AzureBank.Bff.Models.Auth;

public class BffLoginRequest
{
    public required string Email { get; set; }
    public required string Password { get; set; }
    // BFF may add: RememberMe, DeviceId, etc.
}
```

---

*Last Updated: January 26, 2026*
