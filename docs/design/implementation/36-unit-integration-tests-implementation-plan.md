# Unit & Integration Tests Implementation Plan

**Document Version**: 1.0
**Created**: 2026-01-19
**Status**: IMPLEMENTATION PLAN - READY FOR EXECUTION
**Scope**: Complete service layer unit tests + integration test enhancements

---

## Table of Contents

1. [Current State Analysis](#1-current-state-analysis)
2. [Gap Analysis](#2-gap-analysis)
3. [Implementation Strategy](#3-implementation-strategy)
4. [Phase 1: Service Unit Tests](#4-phase-1-service-unit-tests)
5. [Phase 2: Validator Unit Tests](#5-phase-2-validator-unit-tests)
6. [Phase 3: Integration Test Enhancements](#6-phase-3-integration-test-enhancements)
7. [Phase 4: Testcontainers Setup](#7-phase-4-testcontainers-setup)
8. [Execution Checklist](#8-execution-checklist)
9. [Testing Patterns Reference](#9-testing-patterns-reference)

---

## 1. Current State Analysis

### 1.1 Existing Test Files

| Category | File | Tests | Status |
|----------|------|-------|--------|
| **Architecture** | LayerDependencyTests.cs | 6 | ✅ Complete |
| **Architecture** | NamingConventionTests.cs | 8 | ✅ Complete |
| **Architecture** | DesignRuleTests.cs | 10 | ✅ Complete |
| **Unit/Services** | AccountServiceTests.cs | 18 (4 skipped) | ✅ Complete |
| **Unit/Validators** | DepositRequestValidatorTests.cs | 14 | ✅ Complete |
| **Unit/Utilities** | IdGeneratorTests.cs | 7 | ✅ Complete |
| **Integration** | AuthEndpointTests.cs | 11 | ✅ Complete |
| **Integration** | AccountEndpointTests.cs | 10 | ✅ Complete |
| **Integration** | TransactionEndpointTests.cs | 9 | ✅ Complete |
| **Integration** | TransferEndpointTests.cs | 6 | ✅ Complete |
| **Fixtures** | CustomWebApplicationFactory.cs | - | ✅ Complete |
| **Fixtures** | SqlServerContainerFixture.cs | - | ✅ Complete |

**TOTAL EXISTING: ~99 tests**

### 1.2 Existing Services (to be tested)

| Service | Location | Dependencies | Unit Tests |
|---------|----------|--------------|------------|
| **AccountService** | Api/Services/Implementations | DbContext, AccountAccessService, Mapper, Logger | ✅ EXISTS |
| **TransactionService** | Api/Services/Implementations | DbContext, AccountAccessService, Mapper, Logger | ❌ MISSING |
| **TransferService** | Api/Services/Implementations | DbContext, AccountAccessService, AccountService, Mapper, Logger | ❌ MISSING |
| **AuthService** | Api/Services/Implementations | UserManager, JwtService, PasswordHasher, DbContext | ❌ MISSING |
| **JwtService** | Api/Services/Implementations | IOptions<JwtSettings> | ❌ MISSING |
| **PasswordHasher** | Api/Services/Implementations | (pure crypto - no deps) | ❌ MISSING |
| **UserService** | Api/Services/Implementations | DbContext, Mapper, Logger | ❌ MISSING |
| **AccountAccessService** | Api/Services/Implementations | DbContext | ❌ MISSING |

### 1.3 Existing Validators (to be tested)

| Validator | Location | Unit Tests |
|-----------|----------|------------|
| **DepositRequestValidator** | Api/Validators/Transaction | ✅ EXISTS |
| **WithdrawRequestValidator** | Api/Validators/Transaction | ❌ MISSING |
| **LoginRequestValidator** | Api/Validators/Auth | ❌ MISSING |
| **RegisterRequestValidator** | Api/Validators/Auth | ❌ MISSING |
| **SetPinRequestValidator** | Api/Validators/Auth | ❌ MISSING |
| **VerifyPinRequestValidator** | Api/Validators/Auth | ❌ MISSING |
| **CreateAccountRequestValidator** | Api/Validators/Account | ❌ MISSING |
| **UpdateAccountRequestValidator** | Api/Validators/Account | ❌ MISSING |
| **TransferRequestValidator** | Api/Validators/Transfer | ❌ MISSING |
| **InternalTransferRequestValidator** | Api/Validators/Transfer | ❌ MISSING |

---

## 2. Gap Analysis

### 2.1 Missing Unit Tests Summary

```
┌─────────────────────────────────────────────────────────────────┐
│                    UNIT TEST GAP ANALYSIS                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  SERVICES (7 missing)                        Est. Tests         │
│  ├── TransactionServiceTests                    ~15             │
│  ├── TransferServiceTests                       ~12             │
│  ├── AuthServiceTests                           ~18             │
│  ├── JwtServiceTests                            ~8              │
│  ├── PasswordHasherTests                        ~10             │
│  ├── UserServiceTests                           ~6              │
│  └── AccountAccessServiceTests                  ~8              │
│                                           ──────────            │
│                                           Subtotal: ~77         │
│                                                                 │
│  VALIDATORS (9 missing)                      Est. Tests         │
│  ├── WithdrawRequestValidatorTests              ~16             │
│  ├── LoginRequestValidatorTests                 ~10             │
│  ├── RegisterRequestValidatorTests              ~18             │
│  ├── SetPinRequestValidatorTests                ~8              │
│  ├── VerifyPinRequestValidatorTests             ~6              │
│  ├── CreateAccountRequestValidatorTests         ~10             │
│  ├── UpdateAccountRequestValidatorTests         ~8              │
│  ├── TransferRequestValidatorTests              ~14             │
│  └── InternalTransferRequestValidatorTests      ~12             │
│                                           ──────────            │
│                                           Subtotal: ~102        │
│                                                                 │
│  TOTAL NEW TESTS TO IMPLEMENT:              ~179 tests          │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 Priority Matrix

| Priority | Category | Rationale |
|----------|----------|-----------|
| **P1 - Critical** | TransactionService, TransferService | Core business logic, money movement |
| **P1 - Critical** | AuthService, PasswordHasher | Security-critical operations |
| **P2 - High** | JwtService | Security, token validation |
| **P2 - High** | All validators | Input validation, security boundary |
| **P3 - Medium** | UserService, AccountAccessService | Supporting services |

---

## 3. Implementation Strategy

### 3.1 Approach

```
┌─────────────────────────────────────────────────────────────────┐
│                    IMPLEMENTATION ORDER                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  PHASE 1: Service Unit Tests (P1 + P2)                          │
│  ├── 1.1 PasswordHasherTests (no deps - easiest)                │
│  ├── 1.2 JwtServiceTests (minimal deps)                         │
│  ├── 1.3 AccountAccessServiceTests (DbContext only)             │
│  ├── 1.4 TransactionServiceTests (core business)                │
│  ├── 1.5 TransferServiceTests (core business)                   │
│  ├── 1.6 AuthServiceTests (complex deps)                        │
│  └── 1.7 UserServiceTests (simple)                              │
│                                                                 │
│  PHASE 2: Validator Unit Tests                                  │
│  ├── 2.1 Auth validators (4 validators)                         │
│  ├── 2.2 Account validators (2 validators)                      │
│  └── 2.3 Transfer validators (2 validators)                     │
│           (WithdrawRequestValidator exists as template)         │
│                                                                 │
│  PHASE 3: Integration Test Enhancements (Optional)              │
│  ├── 3.1 Edge case tests                                        │
│  ├── 3.2 Error scenario tests                                   │
│  └── 3.3 Concurrent operation tests                             │
│                                                                 │
│  PHASE 4: Testcontainers Full Integration (Optional)            │
│  └── 4.1 Enable skipped tests with real SQL Server              │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### 3.2 Test Design Principles

| Principle | Implementation |
|-----------|----------------|
| **AAA Pattern** | Every test: Arrange → Act → Assert |
| **One Assert Concept** | Test one behavior per test method |
| **Descriptive Names** | `MethodName_Scenario_ExpectedResult` |
| **Test Isolation** | Fresh mocks/context per test |
| **No Test Interdependence** | Tests can run in any order |

---

## 4. Phase 1: Service Unit Tests

### 4.1 PasswordHasherTests

**File**: `Unit/Services/PasswordHasherTests.cs`
**Priority**: P1 - Critical (Security)
**Dependencies**: None (pure cryptography)

#### 4.1.1 Test Categories

| Category | Tests | Description |
|----------|-------|-------------|
| **Password Hashing** | 4 | Hash generation and format |
| **Password Verification** | 4 | Correct/incorrect verification |
| **PIN Hashing** | 3 | PIN-specific profile tests |
| **PIN Verification** | 3 | PIN verification tests |
| **Security** | 2 | Timing attack resistance |

#### 4.1.2 Detailed Test Specifications

```
GROUP A: Password Hashing (4 tests)
├── A.1: HashPassword_WithValidPassword_ReturnsNonEmptyHash
│        - Input: "SecurePass123!"
│        - Assert: Result is not null/empty
│        - Assert: Result contains Argon2 format marker
│
├── A.2: HashPassword_WithSamePassword_ReturnsDifferentHashes
│        - Input: Same password twice
│        - Assert: Hash1 != Hash2 (different salts)
│
├── A.3: HashPassword_ReturnsProperArgon2Format
│        - Input: Any valid password
│        - Assert: Hash starts with "$argon2id$"
│        - Assert: Hash contains version, memory, iterations
│
└── A.4: HashPassword_WithEmptyPassword_StillReturnsHash
         - Input: "" (empty string)
         - Assert: Returns valid hash (validator handles empty check)

GROUP B: Password Verification (4 tests)
├── B.1: VerifyPassword_WithCorrectPassword_ReturnsTrue
│        - Arrange: Hash a password
│        - Act: Verify with same password
│        - Assert: Returns true
│
├── B.2: VerifyPassword_WithIncorrectPassword_ReturnsFalse
│        - Arrange: Hash "Password1"
│        - Act: Verify with "Password2"
│        - Assert: Returns false
│
├── B.3: VerifyPassword_WithMalformedHash_ReturnsFalse
│        - Input: Invalid hash format
│        - Assert: Returns false (no exception)
│
└── B.4: VerifyPassword_CasesSensitive_ReturnsFalse
         - Arrange: Hash "Password"
         - Act: Verify with "password"
         - Assert: Returns false

GROUP C: PIN Hashing (3 tests)
├── C.1: HashPin_WithValidPin_ReturnsHash
│        - Input: "123456"
│        - Assert: Valid hash returned
│
├── C.2: HashPin_UsesDifferentProfileThanPassword
│        - Compare memory cost in PIN hash vs password hash
│        - Assert: PIN uses lower memory (19MB vs 64MB)
│
└── C.3: HashPin_WithSamePin_ReturnsDifferentHashes
         - Input: Same PIN twice
         - Assert: Different hashes (different salts)

GROUP D: PIN Verification (3 tests)
├── D.1: VerifyPin_WithCorrectPin_ReturnsTrue
├── D.2: VerifyPin_WithIncorrectPin_ReturnsFalse
└── D.3: VerifyPin_CannotVerifyPasswordHashWithPin_ReturnsFalse
         - Arrange: Hash with HashPassword()
         - Act: Verify with VerifyPin()
         - Assert: Returns false (different profiles)
```

#### 4.1.3 Implementation Template

```csharp
// File: Unit/Services/PasswordHasherTests.cs
namespace AzureBank.Tests.Unit.Services;

public class PasswordHasherTests
{
    private readonly PasswordHasher _sut = new();

    #region Password Hashing Tests

    [Fact]
    public void HashPassword_WithValidPassword_ReturnsNonEmptyHash()
    {
        // Arrange
        var password = "SecurePass123!";

        // Act
        var hash = _sut.HashPassword(password);

        // Assert
        hash.Should().NotBeNullOrEmpty();
        hash.Should().StartWith("$argon2id$");
    }

    // ... more tests

    #endregion
}
```

---

### 4.2 JwtServiceTests

**File**: `Unit/Services/JwtServiceTests.cs`
**Priority**: P2 - High (Security)
**Dependencies**: IOptions<JwtSettings>

#### 4.2.1 Test Categories

| Category | Tests | Description |
|----------|-------|-------------|
| **Token Generation** | 4 | Generate valid JWT tokens |
| **Token Validation** | 4 | Validate and extract claims |

#### 4.2.2 Detailed Test Specifications

```
GROUP A: Token Generation (4 tests)
├── A.1: GenerateToken_WithValidUser_ReturnsNonEmptyToken
│        - Arrange: Create ApplicationUser with Id, Email, AzureTag
│        - Act: GenerateToken(user)
│        - Assert: Token is not null/empty
│        - Assert: Token has 3 parts (header.payload.signature)
│
├── A.2: GenerateToken_ContainsExpectedClaims
│        - Act: GenerateToken, then decode payload
│        - Assert: Contains "sub" claim with UserId
│        - Assert: Contains "email" claim
│        - Assert: Contains "azureTag" claim
│        - Assert: Contains "role" = "User"
│
├── A.3: GenerateToken_SetsCorrectExpiration
│        - Arrange: JwtSettings.ExpirationMinutes = 15
│        - Act: GenerateToken, decode "exp" claim
│        - Assert: Expiration is ~15 minutes from now
│
└── A.4: GenerateToken_SignsWithCorrectAlgorithm
         - Assert: Token header has "alg": "HS256"

GROUP B: Token Validation (4 tests)
├── B.1: ValidateToken_WithValidToken_ReturnsTrueAndUserId
│        - Arrange: Generate token for user with known Id
│        - Act: ValidateToken(token)
│        - Assert: IsValid = true
│        - Assert: UserId matches original
│
├── B.2: ValidateToken_WithExpiredToken_ReturnsFalse
│        - Arrange: Generate token with past expiration
│        - Act: ValidateToken(token)
│        - Assert: IsValid = false
│
├── B.3: ValidateToken_WithTamperedToken_ReturnsFalse
│        - Arrange: Generate valid token, modify payload
│        - Act: ValidateToken(modifiedToken)
│        - Assert: IsValid = false
│
└── B.4: ValidateToken_WithWrongIssuer_ReturnsFalse
         - Arrange: Token from different issuer
         - Assert: IsValid = false
```

#### 4.2.3 Test Setup

```csharp
// File: Unit/Services/JwtServiceTests.cs
namespace AzureBank.Tests.Unit.Services;

public class JwtServiceTests
{
    private readonly JwtService _sut;
    private readonly JwtSettings _settings;

    public JwtServiceTests()
    {
        _settings = new JwtSettings
        {
            Secret = "ThisIsAVeryLongSecretKeyForTestingPurposesOnly!",
            Issuer = "AzureBank.Tests",
            Audience = "AzureBank.Api",
            ExpirationMinutes = 15
        };

        var options = Options.Create(_settings);
        _sut = new JwtService(options);
    }

    private ApplicationUser CreateTestUser() => new()
    {
        Id = Guid.NewGuid(),
        Email = "test@example.com",
        AzureTag = "test.user",
        FirstName = "Test",
        LastName = "User"
    };
}
```

---

### 4.3 AccountAccessServiceTests

**File**: `Unit/Services/AccountAccessServiceTests.cs`
**Priority**: P3 - Medium
**Dependencies**: DbContext

#### 4.3.1 Test Specifications

```
GROUP A: GetAccountWithOwnershipCheckAsync (4 tests)
├── A.1: GetAccountWithOwnershipCheckAsync_ValidOwner_ReturnsAccount
├── A.2: GetAccountWithOwnershipCheckAsync_WrongOwner_ThrowsForbiddenException
├── A.3: GetAccountWithOwnershipCheckAsync_NonExistentAccount_ThrowsNotFoundException
└── A.4: GetAccountWithOwnershipCheckAsync_DeletedAccount_ThrowsNotFoundException

GROUP B: ValidateAccountOwnershipAsync (4 tests)
├── B.1: ValidateAccountOwnershipAsync_ValidOwner_ReturnsTrue
├── B.2: ValidateAccountOwnershipAsync_WrongOwner_ThrowsForbiddenException
├── B.3: ValidateAccountOwnershipAsync_NonExistentAccount_ThrowsNotFoundException
└── B.4: ValidateAccountOwnershipAsync_MultipleAccounts_ValidatesCorrectly
```

---

### 4.4 TransactionServiceTests

**File**: `Unit/Services/TransactionServiceTests.cs`
**Priority**: P1 - Critical (Core Business)
**Dependencies**: DbContext, AccountAccessService, Mapper, Logger

#### 4.4.1 Test Categories

| Category | Tests | Description |
|----------|-------|-------------|
| **DepositAsync** | 5 | Deposit money operations |
| **WithdrawAsync** | 5 | Withdraw money operations |
| **GetTransactionsAsync** | 5 | List and filter transactions |
| **GetTransactionByIdAsync** | 3 | Get single transaction |

#### 4.4.2 Detailed Test Specifications

```
GROUP A: DepositAsync (5 tests)
├── A.1: DepositAsync_WithValidRequest_CreatesTransaction
│        - Arrange: Account with balance 100
│        - Act: Deposit 50
│        - Assert: Transaction created with Type=Deposit
│        - Assert: Amount = 50
│        - Assert: Account balance = 150
│
├── A.2: DepositAsync_UpdatesAccountBalance
│        - Arrange: Account with balance 0
│        - Act: Deposit 100
│        - Assert: Account.Balance = 100
│
├── A.3: DepositAsync_RecordsBalanceBeforeAndAfter
│        - Assert: Transaction.BalanceBefore = original balance
│        - Assert: Transaction.BalanceAfter = new balance
│
├── A.4: DepositAsync_GeneratesTransactionNumber
│        - Assert: TransactionNumber matches "TXN-YYYYMMDD-XXXXXX" format
│
└── A.5: DepositAsync_SetsStatusToCompleted
         - Assert: Transaction.Status = Completed

GROUP B: WithdrawAsync (5 tests)
├── B.1: WithdrawAsync_WithSufficientFunds_CreatesTransaction
│        - Arrange: Account with balance 500
│        - Act: Withdraw 200
│        - Assert: Transaction created, balance = 300
│
├── B.2: WithdrawAsync_WithInsufficientFunds_ThrowsBusinessRuleException
│        - Arrange: Account with balance 50
│        - Act: Withdraw 100
│        - Assert: Throws BusinessRuleException
│        - Assert: Message contains "Insufficient funds"
│
├── B.3: WithdrawAsync_WithExactBalance_Succeeds
│        - Arrange: Account with balance 100
│        - Act: Withdraw 100
│        - Assert: Success, balance = 0
│
├── B.4: WithdrawAsync_ValidatesPinBeforeWithdraw
│        - This is handled at controller level, service trusts PIN is verified
│        - Skip or test that service doesn't care about PIN
│
└── B.5: WithdrawAsync_RecordsWithdrawalType
         - Assert: Transaction.Type = Withdrawal

GROUP C: GetTransactionsAsync (5 tests)
├── C.1: GetTransactionsAsync_ReturnsUserTransactionsOnly
│        - Arrange: Transactions for user1 and user2
│        - Act: GetTransactions for user1
│        - Assert: Only user1's transactions returned
│
├── C.2: GetTransactionsAsync_FiltersByAccountId
│        - Arrange: User with 2 accounts, transactions in each
│        - Act: Filter by account1
│        - Assert: Only account1 transactions
│
├── C.3: GetTransactionsAsync_FiltersByDateRange
│        - Arrange: Transactions on different dates
│        - Act: Filter FromDate to ToDate
│        - Assert: Only transactions in range
│
├── C.4: GetTransactionsAsync_PaginatesCorrectly
│        - Arrange: 25 transactions
│        - Act: Page=2, PageSize=10
│        - Assert: Returns transactions 11-20
│
└── C.5: GetTransactionsAsync_OrdersByCreatedAtDescending
         - Assert: Most recent first

GROUP D: GetTransactionByIdAsync (3 tests)
├── D.1: GetTransactionByIdAsync_ExistingTransaction_ReturnsTransaction
├── D.2: GetTransactionByIdAsync_NonExistent_ThrowsNotFoundException
└── D.3: GetTransactionByIdAsync_OtherUsersTransaction_ThrowsForbiddenException
```

#### 4.4.3 Test Setup Template

```csharp
// File: Unit/Services/TransactionServiceTests.cs
namespace AzureBank.Tests.Unit.Services;

public class TransactionServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly Mock<IAccountAccessService> _accountAccessMock;
    private readonly TransactionMapper _mapper;
    private readonly Mock<ILogger<TransactionService>> _loggerMock;
    private readonly TransactionService _sut;

    public TransactionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AzureBankDbContext(options);
        _accountAccessMock = new Mock<IAccountAccessService>();
        _mapper = new TransactionMapper();
        _loggerMock = new Mock<ILogger<TransactionService>>();

        _sut = new TransactionService(
            _context,
            _accountAccessMock.Object,
            _mapper,
            _loggerMock.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Helper Methods

    private Account CreateTestAccount(Guid userId, decimal balance = 0)
    {
        return new Account
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccountNumber = $"AB-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(1000, 9999)}-{Random.Shared.Next(10, 99)}",
            Name = "Test Account",
            Type = AccountType.Checking,
            Balance = balance,
            IsPrimary = true,
            CreatedAt = DateTime.UtcNow,
            RowVersion = [0, 0, 0, 0, 0, 0, 0, 1]
        };
    }

    #endregion
}
```

---

### 4.5 TransferServiceTests

**File**: `Unit/Services/TransferServiceTests.cs`
**Priority**: P1 - Critical (Core Business)
**Dependencies**: DbContext, AccountAccessService, AccountService, Mapper, Logger

#### 4.5.1 Test Specifications

```
GROUP A: TransferAsync (External Transfer) (6 tests)
├── A.1: TransferAsync_ValidTransfer_CreatesTwoTransactions
│        - Assert: Debit transaction for sender
│        - Assert: Credit transaction for recipient
│
├── A.2: TransferAsync_UpdatesBothAccountBalances
│        - Arrange: Sender=1000, Recipient=500
│        - Act: Transfer 200
│        - Assert: Sender=800, Recipient=700
│
├── A.3: TransferAsync_InsufficientFunds_ThrowsBusinessRuleException
├── A.4: TransferAsync_NonExistentRecipient_ThrowsNotFoundException
├── A.5: TransferAsync_TransferToSelf_ThrowsBusinessRuleException
│        - Act: Transfer to own AzureTag
│        - Assert: Throws "Cannot transfer to yourself"
│
└── A.6: TransferAsync_ReturnsCorrectResponse
         - Assert: Amount, NewBalance, RecipientAzureTag

GROUP B: InternalTransferAsync (6 tests)
├── B.1: InternalTransferAsync_ValidTransfer_CreatesTwoTransactions
├── B.2: InternalTransferAsync_SameAccount_ThrowsBusinessRuleException
│        - Assert: "Cannot transfer to the same account"
│
├── B.3: InternalTransferAsync_OtherUsersAccount_ThrowsForbiddenException
│        - Arrange: User1 tries to transfer TO User2's account
│        - Assert: Throws ForbiddenException
│
├── B.4: InternalTransferAsync_FromOtherUsersAccount_ThrowsForbiddenException
│        - Arrange: User1 tries to transfer FROM User2's account
│
├── B.5: InternalTransferAsync_InsufficientFunds_ThrowsBusinessRuleException
└── B.6: InternalTransferAsync_ReturnsCorrectResponse
         - Assert: FromAccountNewBalance, ToAccountNewBalance
```

---

### 4.6 AuthServiceTests

**File**: `Unit/Services/AuthServiceTests.cs`
**Priority**: P1 - Critical (Security)
**Dependencies**: UserManager, JwtService, PasswordHasher, DbContext

#### 4.6.1 Test Specifications

```
GROUP A: RegisterAsync (6 tests)
├── A.1: RegisterAsync_ValidRequest_CreatesUserAndAccount
├── A.2: RegisterAsync_DuplicateEmail_ThrowsConflictException
├── A.3: RegisterAsync_DuplicateAzureTag_ThrowsConflictException
├── A.4: RegisterAsync_ReturnsTokenAndUserInfo
├── A.5: RegisterAsync_CreatesPrimaryAccount
└── A.6: RegisterAsync_HashesPassword

GROUP B: LoginAsync (5 tests)
├── B.1: LoginAsync_ValidCredentials_ReturnsToken
├── B.2: LoginAsync_InvalidEmail_ThrowsUnauthorizedException
├── B.3: LoginAsync_InvalidPassword_ThrowsUnauthorizedException
├── B.4: LoginAsync_LockedOutUser_ThrowsBusinessRuleException
└── B.5: LoginAsync_ReturnsUserInfo

GROUP C: PIN Management (4 tests)
├── C.1: SetPinAsync_ValidPin_StoresHashedPin
├── C.2: VerifyPinAsync_CorrectPin_ReturnsTrue
├── C.3: VerifyPinAsync_IncorrectPin_ReturnsFalse
└── C.4: VerifyPinAsync_NoPinSet_ReturnsFalse
```

---

### 4.7 UserServiceTests

**File**: `Unit/Services/UserServiceTests.cs`
**Priority**: P3 - Medium
**Dependencies**: DbContext, Mapper, Logger

#### 4.7.1 Test Specifications

```
GROUP A: GetUserByIdAsync (3 tests)
├── A.1: GetUserByIdAsync_ExistingUser_ReturnsUserResponse
├── A.2: GetUserByIdAsync_NonExistent_ThrowsNotFoundException
└── A.3: GetUserByIdAsync_ReturnsMaskedData

GROUP B: SearchUsersAsync (3 tests)
├── B.1: SearchUsersAsync_MatchingTag_ReturnsResults
├── B.2: SearchUsersAsync_ExcludesCurrentUser
└── B.3: SearchUsersAsync_ReturnsPrivacyMaskedResults
         - Assert: Returns "FirstName LastInitial."
```

---

## 5. Phase 2: Validator Unit Tests

### 5.1 Strategy

Use **DepositRequestValidatorTests** as template - same pattern for all validators.

### 5.2 Auth Validators

#### 5.2.1 RegisterRequestValidatorTests

**File**: `Unit/Validators/RegisterRequestValidatorTests.cs`

```
Tests (18):
├── Valid Request Tests (3)
│   ├── ValidRequest_ShouldNotHaveErrors
│   ├── MinimumValidValues_ShouldNotHaveErrors
│   └── MaximumValidValues_ShouldNotHaveErrors
│
├── AzureTag Validation (4)
│   ├── EmptyAzureTag_ShouldHaveError
│   ├── TooShortAzureTag_ShouldHaveError (< 3 chars)
│   ├── TooLongAzureTag_ShouldHaveError (> 30 chars)
│   └── InvalidCharacters_ShouldHaveError
│
├── Email Validation (3)
│   ├── EmptyEmail_ShouldHaveError
│   ├── InvalidEmailFormat_ShouldHaveError
│   └── ValidEmail_ShouldNotHaveError
│
├── Password Validation (5)
│   ├── EmptyPassword_ShouldHaveError
│   ├── TooShortPassword_ShouldHaveError (< 8 chars)
│   ├── NoUppercase_ShouldHaveError
│   ├── NoLowercase_ShouldHaveError
│   └── NoDigit_ShouldHaveError
│
└── Name Validation (3)
    ├── EmptyFirstName_ShouldHaveError
    ├── EmptyLastName_ShouldHaveError
    └── TooLongName_ShouldHaveError
```

#### 5.2.2 LoginRequestValidatorTests

```
Tests (10):
├── ValidRequest_ShouldNotHaveErrors
├── EmptyEmail_ShouldHaveError
├── InvalidEmailFormat_ShouldHaveError
├── EmptyPassword_ShouldHaveError
└── ... (similar pattern)
```

#### 5.2.3 SetPinRequestValidatorTests

```
Tests (8):
├── ValidPin_ShouldNotHaveErrors
├── EmptyPin_ShouldHaveError
├── TooShortPin_ShouldHaveError (< 6 digits)
├── TooLongPin_ShouldHaveError (> 6 digits)
├── NonNumericPin_ShouldHaveError
└── ...
```

#### 5.2.4 VerifyPinRequestValidatorTests

```
Tests (6):
├── ValidPin_ShouldNotHaveErrors
├── EmptyPin_ShouldHaveError
├── InvalidFormat_ShouldHaveError
└── ...
```

### 5.3 Account Validators

#### 5.3.1 CreateAccountRequestValidatorTests

```
Tests (10):
├── ValidRequest_ShouldNotHaveErrors
├── EmptyName_ShouldHaveError
├── TooShortName_ShouldHaveError (< 2 chars)
├── TooLongName_ShouldHaveError (> 50 chars)
├── InvalidType_ShouldHaveError
└── ...
```

#### 5.3.2 UpdateAccountRequestValidatorTests

```
Tests (8):
├── ValidRequest_ShouldNotHaveErrors
├── EmptyName_ShouldHaveError
├── TooLongName_ShouldHaveError
└── ...
```

### 5.4 Transfer Validators

#### 5.4.1 TransferRequestValidatorTests

```
Tests (14):
├── ValidRequest_ShouldNotHaveErrors
├── EmptyFromAccountId_ShouldHaveError
├── EmptyRecipientAzureTag_ShouldHaveError
├── InvalidAzureTag_ShouldHaveError
├── ZeroAmount_ShouldHaveError
├── NegativeAmount_ShouldHaveError
├── AmountExceedsMaximum_ShouldHaveError
└── ...
```

#### 5.4.2 InternalTransferRequestValidatorTests

```
Tests (12):
├── ValidRequest_ShouldNotHaveErrors
├── EmptyFromAccountId_ShouldHaveError
├── EmptyToAccountId_ShouldHaveError
├── SameFromAndTo_ShouldHaveError
├── ZeroAmount_ShouldHaveError
└── ...
```

#### 5.4.3 WithdrawRequestValidatorTests

```
Tests (16):
├── ValidRequest_ShouldNotHaveErrors
├── EmptyAccountId_ShouldHaveError
├── EmptyPin_ShouldHaveError
├── InvalidPinFormat_ShouldHaveError
├── ZeroAmount_ShouldHaveError
├── NegativeAmount_ShouldHaveError
├── AmountExceedsMaximum_ShouldHaveError
└── ...
```

---

## 6. Phase 3: Integration Test Enhancements

### 6.1 Missing Edge Cases

| Test File | Missing Scenarios |
|-----------|-------------------|
| **AuthEndpointTests** | Concurrent registration, Token refresh, Lockout after failed attempts |
| **AccountEndpointTests** | Delete account validation, Concurrent updates |
| **TransactionEndpointTests** | Concurrent deposits/withdrawals, Large transactions |
| **TransferEndpointTests** | Transfer to self validation |

### 6.2 Additional Integration Tests

```
AuthEndpointTests additions:
├── Register_ConcurrentWithSameEmail_OneSucceedsOneConflicts
├── Login_MultipleFailedAttempts_LocksAccount
└── Token_AfterExpiration_ReturnsUnauthorized

AccountEndpointTests additions:
├── DeleteAccount_WithBalance_ReturnsBadRequest
├── DeleteAccount_PrimaryAccount_ReturnsBadRequest
└── ConcurrentUpdate_SameAccount_HandlesOptimisticConcurrency
```

---

## 7. Phase 4: Testcontainers Setup

### 7.1 Enable Skipped Tests

The following tests in `AccountServiceTests` are skipped because they need SQL Server:

```csharp
[Fact(Skip = "Requires SQL Server - RowVersion is auto-generated by DB")]
public async Task CreateAccountAsync_WithValidRequest_CreatesAccount()

[Fact(Skip = "Requires SQL Server - RowVersion is auto-generated by DB")]
public async Task CreateAccountAsync_GeneratesUniqueAccountNumber()

// ... etc
```

### 7.2 Implementation

```csharp
// Create SQL Server test collection
[Collection("SqlServer")]
public class AccountServiceSqlServerTests : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _dbFixture;

    public AccountServiceSqlServerTests(SqlServerContainerFixture dbFixture)
    {
        _dbFixture = dbFixture;
    }

    [Fact]
    public async Task CreateAccountAsync_WithValidRequest_CreatesAccount()
    {
        // Use real SQL Server from Testcontainers
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseSqlServer(_dbFixture.ConnectionString)
            .Options;

        await using var context = new AzureBankDbContext(options);
        // ... test with real RowVersion support
    }
}
```

---

## 8. Execution Checklist

### 8.1 Phase 1 Checklist

```
[ ] 1.1 PasswordHasherTests
    [ ] Create file Unit/Services/PasswordHasherTests.cs
    [ ] Implement GROUP A: Password Hashing (4 tests)
    [ ] Implement GROUP B: Password Verification (4 tests)
    [ ] Implement GROUP C: PIN Hashing (3 tests)
    [ ] Implement GROUP D: PIN Verification (3 tests)
    [ ] Run tests and verify all pass

[ ] 1.2 JwtServiceTests
    [ ] Create file Unit/Services/JwtServiceTests.cs
    [ ] Implement GROUP A: Token Generation (4 tests)
    [ ] Implement GROUP B: Token Validation (4 tests)
    [ ] Run tests and verify all pass

[ ] 1.3 AccountAccessServiceTests
    [ ] Create file Unit/Services/AccountAccessServiceTests.cs
    [ ] Implement all 8 tests
    [ ] Run tests and verify all pass

[ ] 1.4 TransactionServiceTests
    [ ] Create file Unit/Services/TransactionServiceTests.cs
    [ ] Implement GROUP A: DepositAsync (5 tests)
    [ ] Implement GROUP B: WithdrawAsync (5 tests)
    [ ] Implement GROUP C: GetTransactionsAsync (5 tests)
    [ ] Implement GROUP D: GetTransactionByIdAsync (3 tests)
    [ ] Run tests and verify all pass

[ ] 1.5 TransferServiceTests
    [ ] Create file Unit/Services/TransferServiceTests.cs
    [ ] Implement GROUP A: TransferAsync (6 tests)
    [ ] Implement GROUP B: InternalTransferAsync (6 tests)
    [ ] Run tests and verify all pass

[ ] 1.6 AuthServiceTests
    [ ] Create file Unit/Services/AuthServiceTests.cs
    [ ] Implement GROUP A: RegisterAsync (6 tests)
    [ ] Implement GROUP B: LoginAsync (5 tests)
    [ ] Implement GROUP C: PIN Management (4 tests)
    [ ] Run tests and verify all pass

[ ] 1.7 UserServiceTests
    [ ] Create file Unit/Services/UserServiceTests.cs
    [ ] Implement GROUP A: GetUserByIdAsync (3 tests)
    [ ] Implement GROUP B: SearchUsersAsync (3 tests)
    [ ] Run tests and verify all pass
```

### 8.2 Phase 2 Checklist

```
[ ] 2.1 Auth Validators
    [ ] RegisterRequestValidatorTests (18 tests)
    [ ] LoginRequestValidatorTests (10 tests)
    [ ] SetPinRequestValidatorTests (8 tests)
    [ ] VerifyPinRequestValidatorTests (6 tests)

[ ] 2.2 Account Validators
    [ ] CreateAccountRequestValidatorTests (10 tests)
    [ ] UpdateAccountRequestValidatorTests (8 tests)

[ ] 2.3 Transfer Validators
    [ ] WithdrawRequestValidatorTests (16 tests)
    [ ] TransferRequestValidatorTests (14 tests)
    [ ] InternalTransferRequestValidatorTests (12 tests)
```

### 8.3 Final Verification

```
[ ] Run all tests: dotnet test
[ ] Verify no failing tests
[ ] Check test coverage (optional)
[ ] Update PROGRESS.md with completion status
```

---

## 9. Testing Patterns Reference

### 9.1 Service Test Template

```csharp
using AzureBank.Api.Services.Implementations;
using AzureBank.Infrastructure.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace AzureBank.Tests.Unit.Services;

public class ExampleServiceTests : IDisposable
{
    private readonly AzureBankDbContext _context;
    private readonly Mock<IDependency> _dependencyMock;
    private readonly Mock<ILogger<ExampleService>> _loggerMock;
    private readonly ExampleService _sut;

    public ExampleServiceTests()
    {
        // Setup in-memory database
        var options = new DbContextOptionsBuilder<AzureBankDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AzureBankDbContext(options);
        _dependencyMock = new Mock<IDependency>();
        _loggerMock = new Mock<ILogger<ExampleService>>();

        _sut = new ExampleService(
            _context,
            _dependencyMock.Object,
            _loggerMock.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region MethodName Tests

    [Fact]
    public async Task MethodName_Scenario_ExpectedResult()
    {
        // Arrange
        var input = CreateTestData();

        // Act
        var result = await _sut.MethodName(input);

        // Assert
        result.Should().NotBeNull();
        result.Property.Should().Be(expectedValue);
    }

    #endregion

    #region Helper Methods

    private TestEntity CreateTestData() => new()
    {
        Id = Guid.NewGuid(),
        // ... properties
    };

    #endregion
}
```

### 9.2 Validator Test Template

```csharp
using AzureBank.Api.Validators;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace AzureBank.Tests.Unit.Validators;

public class ExampleRequestValidatorTests
{
    private readonly ExampleRequestValidator _validator = new();

    #region Valid Request Tests

    [Fact]
    public void Validate_WithValidRequest_ShouldNotHaveErrors()
    {
        // Arrange
        var request = new ExampleRequest
        {
            Field1 = "ValidValue",
            Field2 = 100
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    #endregion

    #region Field1 Validation Tests

    [Fact]
    public void Validate_WithEmptyField1_ShouldHaveError()
    {
        // Arrange
        var request = new ExampleRequest
        {
            Field1 = string.Empty,
            Field2 = 100
        };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Field1);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Validate_WithInvalidField1_ShouldHaveError(string? value)
    {
        // Arrange
        var request = new ExampleRequest { Field1 = value!, Field2 = 100 };

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Field1);
    }

    #endregion
}
```

### 9.3 Mock Setup Patterns

```csharp
// Setup mock to return specific value
_mockService
    .Setup(x => x.GetDataAsync(It.IsAny<Guid>()))
    .ReturnsAsync(expectedResult);

// Setup mock to throw exception
_mockService
    .Setup(x => x.GetDataAsync(It.Is<Guid>(id => id == invalidId)))
    .ThrowsAsync(new NotFoundException("Not found"));

// Verify mock was called
_mockService.Verify(
    x => x.SaveAsync(It.IsAny<Entity>()),
    Times.Once);

// Verify mock was called with specific parameter
_mockService.Verify(
    x => x.SaveAsync(It.Is<Entity>(e => e.Name == "Expected")),
    Times.Once);
```

---

## Summary

### Expected Test Counts After Implementation

| Category | Before | After | Added |
|----------|--------|-------|-------|
| Architecture | 24 | 24 | 0 |
| Unit/Services | 18 | ~95 | ~77 |
| Unit/Validators | 14 | ~116 | ~102 |
| Unit/Utilities | 7 | 7 | 0 |
| Integration | 36 | 36 | 0 |
| **TOTAL** | **99** | **~278** | **~179** |

### Implementation Timeline

| Phase | Duration | Tests |
|-------|----------|-------|
| Phase 1 (Services) | Day 1-2 | ~77 tests |
| Phase 2 (Validators) | Day 2-3 | ~102 tests |
| Phase 3 (Integration Enhancements) | Optional | ~10 tests |
| Phase 4 (Testcontainers) | Optional | Enable skipped |

---

**END OF IMPLEMENTATION PLAN**
