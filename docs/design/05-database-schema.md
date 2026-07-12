# Database Schema Design
## Bank Account Management System - AzureBank

**Document Version**: 2.1
**Created**: 2025-12-16
**Updated**: 2026-01-09
**Author**: Database Engineer
**Status**: COMPLETE - Phase 3

---

## 1. Executive Summary

This document defines the complete database schema for AzureBank, incorporating:
- **AzureTag** system for public user identification
- **Primary Account** routing for external transfers (Gemini v4.1 fix)
- Full audit trail for all transactions
- Historical balance queries support

### Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Primary Keys | UNIQUEIDENTIFIER (GUID) | No sequential exposure, distributed-friendly |
| Money Type | DECIMAL(19,4) | Precision for financial calculations |
| Soft Deletes | Yes (IsDeleted flag) | Audit trail preservation |
| Timestamps | UTC always | Consistency across timezones |
| AzureTag | IMMUTABLE after creation | User identity stability |

---

## 2. Entity Relationship Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         AZUREBANK DATABASE SCHEMA                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────┐         ┌─────────────────────┐                   │
│  │       USERS         │         │      ACCOUNTS       │                   │
│  ├─────────────────────┤         ├─────────────────────┤                   │
│  │ Id (PK, GUID)       │───1:N──▶│ Id (PK, GUID)       │                   │
│  │ AzureTag (UNIQUE)   │         │ UserId (FK)         │                   │
│  │ Email (UNIQUE)      │         │ AccountNumber (UQ)  │                   │
│  │ PasswordHash        │         │ Name                │                   │
│  │ FirstName           │         │ Type                │                   │
│  │ LastName            │         │ Balance             │                   │
│  │ CreatedAt           │         │ IsPrimary           │◀─── Gemini Fix    │
│  │ UpdatedAt           │         │ IsDeleted           │                   │
│  │ IsDeleted           │         │ CreatedAt           │                   │
│  └─────────────────────┘         │ UpdatedAt           │                   │
│                                  └──────────┬──────────┘                   │
│                                             │                               │
│                                             │ 1:N                           │
│                                             ▼                               │
│                                  ┌─────────────────────┐                   │
│                                  │    TRANSACTIONS     │                   │
│                                  ├─────────────────────┤                   │
│                                  │ Id (PK, GUID)       │                   │
│                                  │ TransactionNumber   │                   │
│                                  │ AccountId (FK)      │                   │
│                                  │ Type                │                   │
│                                  │ Amount              │                   │
│                                  │ BalanceAfter        │                   │
│                                  │ Description         │                   │
│                                  │ RelatedTxId (FK)    │───┐ Self-ref     │
│                                  │ CreatedAt           │◀──┘ (transfers)   │
│                                  └─────────────────────┘                   │
│                                                                             │
│  ┌─────────────────────┐                                                   │
│  │   REFRESH_TOKENS    │  (Optional - for JWT refresh)                     │
│  ├─────────────────────┤                                                   │
│  │ Id (PK, GUID)       │                                                   │
│  │ UserId (FK)         │                                                   │
│  │ Token               │                                                   │
│  │ ExpiresAt           │                                                   │
│  │ CreatedAt           │                                                   │
│  │ RevokedAt           │                                                   │
│  └─────────────────────┘                                                   │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘

RELATIONSHIPS:
─────────────
Users     1:N  Accounts      (One user can have multiple accounts)
Accounts  1:N  Transactions  (One account has many transactions)
Transactions 1:1 Transactions (Self-reference for transfer pairs)
Users     1:N  RefreshTokens (One user can have multiple sessions)
```

---

## 3. Entity Definitions

### 3.1 Users Table

**Purpose**: Store user authentication and profile information

| Column | Type | Constraints | Description |
|--------|------|-------------|-------------|
| Id | UNIQUEIDENTIFIER | PK, DEFAULT NEWID() | Unique identifier |
| AzureTag | NVARCHAR(20) | UNIQUE, NOT NULL | Public @username (IMMUTABLE) |
| Email | NVARCHAR(255) | UNIQUE, NOT NULL | Login credential |
| PasswordHash | NVARCHAR(255) | NOT NULL | Argon2id hash |
| FirstName | NVARCHAR(100) | NOT NULL | User's first name |
| LastName | NVARCHAR(100) | NOT NULL | User's last name |
| CreatedAt | DATETIME2 | NOT NULL, DEFAULT GETUTCDATE() | Account creation time |
| UpdatedAt | DATETIME2 | NOT NULL, DEFAULT GETUTCDATE() | Last modification time |
| IsDeleted | BIT | NOT NULL, DEFAULT 0 | Soft delete flag |
| DeletedAt | DATETIME2 | NULL | Soft delete timestamp |

**AzureTag Rules**:
- Format: `@username` (stored without @)
- Length: 3-20 characters
- Characters: alphanumeric + underscore only
- Case-insensitive uniqueness (stored lowercase)
- **IMMUTABLE** after registration

---

### 3.2 Accounts Table

**Purpose**: Store bank account information

| Column | Type | Constraints | Description |
|--------|------|-------------|-------------|
| Id | UNIQUEIDENTIFIER | PK, DEFAULT NEWID() | Unique identifier |
| UserId | UNIQUEIDENTIFIER | FK -> Users.Id, NOT NULL | Account owner |
| AccountNumber | NVARCHAR(14) | UNIQUE, NOT NULL | Format: AB-XXXX-XXXX-XX |
| Name | NVARCHAR(100) | NOT NULL | Account display name |
| Type | NVARCHAR(20) | NOT NULL | 'checking', 'savings', 'investment' |
| Balance | DECIMAL(19,4) | NOT NULL, DEFAULT 0 | Current balance |
| IsPrimary | BIT | NOT NULL, DEFAULT 0 | Primary account for transfers |
| IsDeleted | BIT | NOT NULL, DEFAULT 0 | Soft delete flag |
| CreatedAt | DATETIME2 | NOT NULL, DEFAULT GETUTCDATE() | Creation time |
| UpdatedAt | DATETIME2 | NOT NULL, DEFAULT GETUTCDATE() | Last modification time |
| DeletedAt | DATETIME2 | NULL | Soft delete timestamp |
| RowVersion | ROWVERSION | NOT NULL | Optimistic concurrency token |

**Account Number Format**:
- `AB` = AzureBank prefix
- `XXXX-XXXX` = 8 random digits
- `XX` = Check digits (Luhn algorithm)
- Example: `AB-1234-5678-90`

**IsPrimary Constraint**:
- Only ONE account per user can be primary
- First account created is automatically primary
- Used for routing external transfers (Gemini privacy fix)

---

### 3.3 Transactions Table

**Purpose**: Store all financial transactions with full audit trail

| Column | Type | Constraints | Description |
|--------|------|-------------|-------------|
| Id | UNIQUEIDENTIFIER | PK, DEFAULT NEWID() | Unique identifier |
| TransactionNumber | NVARCHAR(20) | UNIQUE, NOT NULL | Human-readable ID |
| AccountId | UNIQUEIDENTIFIER | FK -> Accounts.Id, NOT NULL | Associated account |
| Type | NVARCHAR(20) | NOT NULL | Transaction type (see enum) |
| Amount | DECIMAL(19,4) | NOT NULL, CHECK > 0 | Transaction amount |
| BalanceBefore | DECIMAL(19,4) | NOT NULL | Balance before transaction |
| BalanceAfter | DECIMAL(19,4) | NOT NULL | Balance after transaction |
| Description | NVARCHAR(500) | NULL | User-provided description |
| RelatedTransactionId | UNIQUEIDENTIFIER | FK -> Transactions.Id, NULL | Paired transfer transaction |
| RecipientAzureTag | NVARCHAR(20) | NULL | For outgoing transfers |
| SenderAzureTag | NVARCHAR(20) | NULL | For incoming transfers |
| Status | NVARCHAR(20) | NOT NULL, DEFAULT 'completed' | Transaction status |
| CreatedAt | DATETIME2 | NOT NULL, DEFAULT GETUTCDATE() | Transaction time |

**Transaction Types** (Enum):
```
deposit       - Money deposited into account
withdrawal    - Money withdrawn from account
transfer_out  - Money sent to another user
transfer_in   - Money received from another user
```

**Transaction Number Format**:
- `TXN-YYYYMMDD-XXXXXX`
- Example: `TXN-20260108-123456`

---

### 3.4 RefreshTokens Table (Optional)

**Purpose**: Store JWT refresh tokens for session management

| Column | Type | Constraints | Description |
|--------|------|-------------|-------------|
| Id | UNIQUEIDENTIFIER | PK, DEFAULT NEWID() | Unique identifier |
| UserId | UNIQUEIDENTIFIER | FK -> Users.Id, NOT NULL | Token owner |
| Token | NVARCHAR(500) | NOT NULL | Refresh token value |
| ExpiresAt | DATETIME2 | NOT NULL | Token expiration |
| CreatedAt | DATETIME2 | NOT NULL, DEFAULT GETUTCDATE() | Token creation |
| RevokedAt | DATETIME2 | NULL | Revocation time (if revoked) |
| ReplacedByTokenId | UNIQUEIDENTIFIER | FK -> RefreshTokens.Id, NULL | For token rotation |

---

## 4. SQL DDL Scripts

### 4.1 Create Tables

```sql
-- ============================================
-- AZUREBANK DATABASE SCHEMA
-- Version: 2.0
-- Created: 2026-01-08
-- ============================================

-- Drop tables if exist (for clean setup - remove in production)
-- DROP TABLE IF EXISTS RefreshTokens;
-- DROP TABLE IF EXISTS Transactions;
-- DROP TABLE IF EXISTS Accounts;
-- DROP TABLE IF EXISTS Users;

-- ============================================
-- USERS TABLE
-- ============================================
CREATE TABLE Users (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    AzureTag NVARCHAR(20) NOT NULL,
    Email NVARCHAR(255) NOT NULL,
    PasswordHash NVARCHAR(255) NOT NULL,
    FirstName NVARCHAR(100) NOT NULL,
    LastName NVARCHAR(100) NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    IsDeleted BIT NOT NULL DEFAULT 0,
    DeletedAt DATETIME2 NULL,

    -- Constraints
    CONSTRAINT UQ_Users_AzureTag UNIQUE (AzureTag),
    CONSTRAINT UQ_Users_Email UNIQUE (Email),
    CONSTRAINT CK_Users_AzureTag_Format CHECK (
        LEN(AzureTag) >= 3 AND
        LEN(AzureTag) <= 20 AND
        AzureTag NOT LIKE '%[^a-z0-9_]%'
    ),
    CONSTRAINT CK_Users_Email_Format CHECK (
        Email LIKE '%_@_%.__%'
    )
);

-- ============================================
-- ACCOUNTS TABLE
-- ============================================
CREATE TABLE Accounts (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    UserId UNIQUEIDENTIFIER NOT NULL,
    AccountNumber NVARCHAR(14) NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    Type NVARCHAR(20) NOT NULL,
    Balance DECIMAL(19,4) NOT NULL DEFAULT 0,
    IsPrimary BIT NOT NULL DEFAULT 0,
    IsDeleted BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    UpdatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    DeletedAt DATETIME2 NULL,

    -- Foreign Keys
    CONSTRAINT FK_Accounts_Users FOREIGN KEY (UserId)
        REFERENCES Users(Id) ON DELETE NO ACTION,

    -- Constraints
    CONSTRAINT UQ_Accounts_AccountNumber UNIQUE (AccountNumber),
    CONSTRAINT CK_Accounts_Type CHECK (
        Type IN ('checking', 'savings', 'investment')
    ),
    CONSTRAINT CK_Accounts_Balance_NonNegative CHECK (Balance >= 0),
    CONSTRAINT CK_Accounts_AccountNumber_Format CHECK (
        AccountNumber LIKE 'AB-[0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9]-[0-9][0-9]'
    )
);

-- Ensure only ONE primary account per user
CREATE UNIQUE INDEX UX_Accounts_UserId_Primary
ON Accounts(UserId)
WHERE IsPrimary = 1 AND IsDeleted = 0;

-- ============================================
-- TRANSACTIONS TABLE
-- ============================================
CREATE TABLE Transactions (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    TransactionNumber NVARCHAR(20) NOT NULL,
    AccountId UNIQUEIDENTIFIER NOT NULL,
    Type NVARCHAR(20) NOT NULL,
    Amount DECIMAL(19,4) NOT NULL,
    BalanceBefore DECIMAL(19,4) NOT NULL,
    BalanceAfter DECIMAL(19,4) NOT NULL,
    Description NVARCHAR(500) NULL,
    RelatedTransactionId UNIQUEIDENTIFIER NULL,
    RecipientAzureTag NVARCHAR(20) NULL,
    SenderAzureTag NVARCHAR(20) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'completed',
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),

    -- Foreign Keys
    CONSTRAINT FK_Transactions_Accounts FOREIGN KEY (AccountId)
        REFERENCES Accounts(Id) ON DELETE NO ACTION,
    CONSTRAINT FK_Transactions_RelatedTransaction FOREIGN KEY (RelatedTransactionId)
        REFERENCES Transactions(Id) ON DELETE NO ACTION,

    -- Constraints
    CONSTRAINT UQ_Transactions_TransactionNumber UNIQUE (TransactionNumber),
    CONSTRAINT CK_Transactions_Type CHECK (
        Type IN ('deposit', 'withdrawal', 'transfer_out', 'transfer_in')
    ),
    CONSTRAINT CK_Transactions_Amount_Positive CHECK (Amount > 0),
    CONSTRAINT CK_Transactions_Status CHECK (
        Status IN ('pending', 'completed', 'failed', 'reversed')
    )
);

-- ============================================
-- REFRESH_TOKENS TABLE (Optional)
-- ============================================
CREATE TABLE RefreshTokens (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    UserId UNIQUEIDENTIFIER NOT NULL,
    Token NVARCHAR(500) NOT NULL,
    ExpiresAt DATETIME2 NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    RevokedAt DATETIME2 NULL,
    ReplacedByTokenId UNIQUEIDENTIFIER NULL,

    -- Foreign Keys
    CONSTRAINT FK_RefreshTokens_Users FOREIGN KEY (UserId)
        REFERENCES Users(Id) ON DELETE CASCADE,
    CONSTRAINT FK_RefreshTokens_ReplacedBy FOREIGN KEY (ReplacedByTokenId)
        REFERENCES RefreshTokens(Id) ON DELETE NO ACTION
);
```

### 4.2 Create Indexes

```sql
-- ============================================
-- PERFORMANCE INDEXES
-- ============================================

-- Users indexes
CREATE INDEX IX_Users_Email ON Users(Email) WHERE IsDeleted = 0;
CREATE INDEX IX_Users_AzureTag ON Users(AzureTag) WHERE IsDeleted = 0;
CREATE INDEX IX_Users_CreatedAt ON Users(CreatedAt);

-- Accounts indexes
CREATE INDEX IX_Accounts_UserId ON Accounts(UserId) WHERE IsDeleted = 0;
CREATE INDEX IX_Accounts_AccountNumber ON Accounts(AccountNumber) WHERE IsDeleted = 0;
CREATE INDEX IX_Accounts_Type ON Accounts(Type) WHERE IsDeleted = 0;

-- Transactions indexes (critical for performance)
CREATE INDEX IX_Transactions_AccountId ON Transactions(AccountId);
CREATE INDEX IX_Transactions_CreatedAt ON Transactions(CreatedAt DESC);
CREATE INDEX IX_Transactions_Type ON Transactions(Type);
CREATE INDEX IX_Transactions_AccountId_CreatedAt ON Transactions(AccountId, CreatedAt DESC);
CREATE INDEX IX_Transactions_RelatedTransactionId ON Transactions(RelatedTransactionId)
    WHERE RelatedTransactionId IS NOT NULL;

-- RefreshTokens indexes
CREATE INDEX IX_RefreshTokens_UserId ON RefreshTokens(UserId);
CREATE INDEX IX_RefreshTokens_Token ON RefreshTokens(Token);
CREATE INDEX IX_RefreshTokens_ExpiresAt ON RefreshTokens(ExpiresAt);
```

---

## 5. Stored Procedures & Functions

### 5.1 Generate Account Number

```sql
-- ============================================
-- FUNCTION: Generate unique account number
-- ============================================
CREATE OR ALTER FUNCTION dbo.GenerateAccountNumber()
RETURNS NVARCHAR(14)
AS
BEGIN
    DECLARE @AccountNumber NVARCHAR(14);
    DECLARE @Digits NVARCHAR(8);
    DECLARE @CheckDigits NVARCHAR(2);

    -- Generate 8 random digits
    SET @Digits = RIGHT('00000000' + CAST(ABS(CHECKSUM(NEWID())) % 100000000 AS NVARCHAR), 8);

    -- Calculate check digits (simplified Luhn)
    DECLARE @Sum INT = 0;
    DECLARE @i INT = 1;
    WHILE @i <= 8
    BEGIN
        SET @Sum = @Sum + CAST(SUBSTRING(@Digits, @i, 1) AS INT);
        SET @i = @i + 1;
    END
    SET @CheckDigits = RIGHT('00' + CAST(@Sum % 100 AS NVARCHAR), 2);

    -- Format: AB-XXXX-XXXX-XX
    SET @AccountNumber = 'AB-' + LEFT(@Digits, 4) + '-' + RIGHT(@Digits, 4) + '-' + @CheckDigits;

    RETURN @AccountNumber;
END;
GO
```

### 5.2 Generate Transaction Number

```sql
-- ============================================
-- FUNCTION: Generate transaction number
-- ============================================
CREATE OR ALTER FUNCTION dbo.GenerateTransactionNumber()
RETURNS NVARCHAR(20)
AS
BEGIN
    DECLARE @TxnNumber NVARCHAR(20);
    DECLARE @DatePart NVARCHAR(8);
    DECLARE @RandomPart NVARCHAR(6);

    SET @DatePart = FORMAT(GETUTCDATE(), 'yyyyMMdd');
    SET @RandomPart = RIGHT('000000' + CAST(ABS(CHECKSUM(NEWID())) % 1000000 AS NVARCHAR), 6);

    SET @TxnNumber = 'TXN-' + @DatePart + '-' + @RandomPart;

    RETURN @TxnNumber;
END;
GO
```

### 5.3 Get Balance At Time

```sql
-- ============================================
-- FUNCTION: Get account balance at specific time
-- ============================================
CREATE OR ALTER FUNCTION dbo.GetBalanceAtTime(
    @AccountId UNIQUEIDENTIFIER,
    @AtTime DATETIME2
)
RETURNS DECIMAL(19,4)
AS
BEGIN
    DECLARE @Balance DECIMAL(19,4);

    -- Get the balance after the last transaction before or at @AtTime
    SELECT TOP 1 @Balance = BalanceAfter
    FROM Transactions
    WHERE AccountId = @AccountId
      AND CreatedAt <= @AtTime
    ORDER BY CreatedAt DESC;

    -- If no transactions found, return 0 (or initial balance)
    IF @Balance IS NULL
        SET @Balance = 0;

    RETURN @Balance;
END;
GO
```

---

## 6. Triggers

### 6.1 Update Timestamp Trigger

```sql
-- ============================================
-- TRIGGER: Auto-update UpdatedAt timestamp
-- ============================================
CREATE OR ALTER TRIGGER TR_Users_UpdatedAt
ON Users
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE Users
    SET UpdatedAt = GETUTCDATE()
    FROM Users u
    INNER JOIN inserted i ON u.Id = i.Id;
END;
GO

CREATE OR ALTER TRIGGER TR_Accounts_UpdatedAt
ON Accounts
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE Accounts
    SET UpdatedAt = GETUTCDATE()
    FROM Accounts a
    INNER JOIN inserted i ON a.Id = i.Id;
END;
GO
```

### 6.2 Set First Account as Primary

```sql
-- ============================================
-- TRIGGER: Auto-set first account as primary
-- ============================================
CREATE OR ALTER TRIGGER TR_Accounts_SetPrimary
ON Accounts
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;

    -- If this is the user's first account, make it primary
    UPDATE Accounts
    SET IsPrimary = 1
    FROM Accounts a
    INNER JOIN inserted i ON a.Id = i.Id
    WHERE NOT EXISTS (
        SELECT 1 FROM Accounts
        WHERE UserId = i.UserId
          AND Id != i.Id
          AND IsDeleted = 0
    );
END;
GO
```

---

## 7. Seed Data (Development)

```sql
-- ============================================
-- SEED DATA FOR DEVELOPMENT/TESTING
-- ============================================

-- Clear existing data (development only!)
-- DELETE FROM Transactions;
-- DELETE FROM Accounts;
-- DELETE FROM Users;

-- ============================================
-- USERS (Password: "Test123!" hashed with Argon2id)
-- ============================================
INSERT INTO Users (Id, AzureTag, Email, PasswordHash, FirstName, LastName)
VALUES
    ('11111111-1111-1111-1111-111111111111', 'johnsmith', 'john@example.com',
     '$argon2id$v=19$m=65536,t=3,p=4$c29tZXNhbHQ$RdescudvJCsgt3ub+b+dWRWJTmaaJObG',
     'John', 'Smith'),

    ('22222222-2222-2222-2222-222222222222', 'janesmith', 'jane@example.com',
     '$argon2id$v=19$m=65536,t=3,p=4$c29tZXNhbHQ$RdescudvJCsgt3ub+b+dWRWJTmaaJObG',
     'Jane', 'Smith'),

    ('33333333-3333-3333-3333-333333333333', 'mikebrown', 'mike@example.com',
     '$argon2id$v=19$m=65536,t=3,p=4$c29tZXNhbHQ$RdescudvJCsgt3ub+b+dWRWJTmaaJObG',
     'Mike', 'Brown');

-- ============================================
-- ACCOUNTS
-- ============================================
INSERT INTO Accounts (Id, UserId, AccountNumber, Name, Type, Balance, IsPrimary)
VALUES
    -- John's accounts
    ('AAAA1111-1111-1111-1111-111111111111', '11111111-1111-1111-1111-111111111111',
     'AB-1234-5678-90', 'Main Savings', 'savings', 12450.00, 1),
    ('AAAA2222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111',
     'AB-1234-5678-91', 'Checking', 'checking', 2300.00, 0),

    -- Jane's accounts
    ('BBBB1111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
     'AB-2345-6789-01', 'Personal Savings', 'savings', 8500.00, 1),

    -- Mike's accounts
    ('CCCC1111-1111-1111-1111-111111111111', '33333333-3333-3333-3333-333333333333',
     'AB-3456-7890-12', 'Investment Account', 'investment', 25000.00, 1);

-- ============================================
-- TRANSACTIONS (Sample history)
-- ============================================
INSERT INTO Transactions (Id, TransactionNumber, AccountId, Type, Amount, BalanceBefore, BalanceAfter, Description, CreatedAt)
VALUES
    -- John's transactions
    ('TTTT1111-1111-1111-1111-111111111111', 'TXN-20260105-000001',
     'AAAA1111-1111-1111-1111-111111111111', 'deposit', 5000.00, 7450.00, 12450.00,
     'Salary deposit', DATEADD(DAY, -3, GETUTCDATE())),

    ('TTTT2222-2222-2222-2222-222222222222', 'TXN-20260106-000001',
     'AAAA1111-1111-1111-1111-111111111111', 'withdrawal', 500.00, 12450.00, 11950.00,
     'ATM withdrawal', DATEADD(DAY, -2, GETUTCDATE())),

    ('TTTT3333-3333-3333-3333-333333333333', 'TXN-20260107-000001',
     'AAAA1111-1111-1111-1111-111111111111', 'deposit', 500.00, 11950.00, 12450.00,
     'Refund', DATEADD(DAY, -1, GETUTCDATE())),

    -- Transfer example (John -> Jane)
    ('TTTT4444-4444-4444-4444-444444444444', 'TXN-20260108-000001',
     'AAAA1111-1111-1111-1111-111111111111', 'transfer_out', 200.00, 12450.00, 12250.00,
     'Lunch money', GETUTCDATE()),

    ('TTTT5555-5555-5555-5555-555555555555', 'TXN-20260108-000002',
     'BBBB1111-1111-1111-1111-111111111111', 'transfer_in', 200.00, 8300.00, 8500.00,
     'From @johnsmith: Lunch money', GETUTCDATE());

-- Link the transfer transactions
UPDATE Transactions SET RelatedTransactionId = 'TTTT5555-5555-5555-5555-555555555555',
                        RecipientAzureTag = 'janesmith'
WHERE Id = 'TTTT4444-4444-4444-4444-444444444444';

UPDATE Transactions SET RelatedTransactionId = 'TTTT4444-4444-4444-4444-444444444444',
                        SenderAzureTag = 'johnsmith'
WHERE Id = 'TTTT5555-5555-5555-5555-555555555555';

-- ============================================
-- VERIFY SEED DATA
-- ============================================
SELECT 'Users' AS TableName, COUNT(*) AS RecordCount FROM Users
UNION ALL
SELECT 'Accounts', COUNT(*) FROM Accounts
UNION ALL
SELECT 'Transactions', COUNT(*) FROM Transactions;
```

---

## 8. Entity Framework Core Mappings

### 8.1 Entity Configurations (for Backend)

```csharp
// User.cs
public class User
{
    public Guid Id { get; set; }
    public string AzureTag { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}

// Account.cs
public class Account
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string AccountNumber { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Type { get; set; } = null!;
    public decimal Balance { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public User User { get; set; } = null!;
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}

// Transaction.cs
public class Transaction
{
    public Guid Id { get; set; }
    public string TransactionNumber { get; set; } = null!;
    public Guid AccountId { get; set; }
    public string Type { get; set; } = null!;
    public decimal Amount { get; set; }
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? Description { get; set; }
    public Guid? RelatedTransactionId { get; set; }
    public string? RecipientAzureTag { get; set; }
    public string? SenderAzureTag { get; set; }
    public string Status { get; set; } = "completed";
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Account Account { get; set; } = null!;
    public Transaction? RelatedTransaction { get; set; }
}
```

---

## 9. Query Examples

### 9.1 Find User by AzureTag (for transfers)

```sql
-- Find recipient for external transfer
SELECT
    u.Id,
    u.AzureTag,
    u.FirstName,
    LEFT(u.LastName, 1) + '.' AS MaskedLastName,  -- Privacy: "John S."
    a.Id AS PrimaryAccountId
FROM Users u
INNER JOIN Accounts a ON u.Id = a.UserId AND a.IsPrimary = 1 AND a.IsDeleted = 0
WHERE u.AzureTag = @AzureTag
  AND u.IsDeleted = 0;
```

### 9.2 Get Transaction History with Filtering

```sql
-- Get transactions for account with date range
SELECT
    t.TransactionNumber,
    t.Type,
    t.Amount,
    t.BalanceAfter,
    t.Description,
    t.RecipientAzureTag,
    t.SenderAzureTag,
    t.CreatedAt
FROM Transactions t
WHERE t.AccountId = @AccountId
  AND (@FromDate IS NULL OR t.CreatedAt >= @FromDate)
  AND (@ToDate IS NULL OR t.CreatedAt <= @ToDate)
ORDER BY t.CreatedAt DESC
OFFSET @Offset ROWS
FETCH NEXT @PageSize ROWS ONLY;
```

### 9.3 Get Balance at Specific Time

```sql
-- Historical balance query
SELECT dbo.GetBalanceAtTime(@AccountId, @AtTime) AS Balance;
```

---

## 10. Design Decisions Log

| Decision | Choice | Rationale | Date |
|----------|--------|-----------|------|
| Primary Key Type | GUID | Distributed-friendly, no sequential exposure | 2026-01-08 |
| Money Precision | DECIMAL(19,4) | Standard financial precision | 2026-01-08 |
| AzureTag Storage | Lowercase only | Case-insensitive matching | 2026-01-08 |
| Soft Deletes | Yes | Audit trail, data recovery | 2026-01-08 |
| IsPrimary for Accounts | Yes | Gemini privacy fix - route transfers | 2026-01-08 |
| Transaction Self-Reference | Yes | Link transfer pairs | 2026-01-08 |
| Timestamps | UTC | Timezone consistency | 2026-01-08 |

---

## 11. Next Steps

- [ ] Review with Backend Lead (Phase 3.4 Confrontation)
- [ ] Finalize index strategy based on query patterns
- [ ] Create Entity Framework migrations
- [ ] Test seed data with actual queries
- [ ] Document API endpoints that use these tables

---

**Document Status**: COMPLETE - Phase 3.4 Confrontation DONE
**Next Phase**: Phase 4 - API Design
