# External Transfers Feature Design
## AzureBank Account Management System

**Document Version**: 2.0
**Created**: 2025-12-17
**Updated**: 2025-12-17
**Author**: System Architect
**Status**: COMPLETE - Post Gemini Review (Privacy Fix Applied)

---

## IMPORTANT: v2.0 Privacy Changes

Based on Gemini's external review, the following **critical privacy fix** was applied:

| Original Design | Privacy Issue | v2.0 Fix |
|----------------|---------------|----------|
| Sender selects recipient's destination account | Exposes recipient's portfolio structure (Savings, Investment, etc.) | **REMOVED** - Backend routes to Primary Account |

**Key Change**: The sender now only sees the recipient's `@AzureTag` and masked `displayName`. They cannot see which accounts the recipient has.

---

## 1. Executive Summary

This document defines the design for **external transfers** - the ability for users to send money to other AzureBank users' accounts. This is a **core feature** of the application.

### Key Design Decisions

1. **Public Identifier System**: Users have an "AzureTag" (`@username`) that is separate from internal database IDs
2. **Security First**: Internal UUIDs are NEVER exposed to the frontend
3. **User-Friendly**: Simple @username format (like Venmo, PayPal)
4. **Confirmation Flow**: Always show recipient name before transfer

---

## 2. Industry Research Summary

Based on analysis of [IBAN standards](https://www.iban.com/), [Venmo](https://help.venmo.com/cs/articles/what-is-a-visa-payname-vhel178), [Revolut](https://help.revolut.com/), and [World Bank payment guidelines](https://fastpayments.worldbank.org/):

| Platform | Identifier | Format |
|----------|-----------|--------|
| IBAN | International Bank Account Number | IE12BOFI90001712345678 |
| Venmo | Handle | @johndoe |
| PayPal | PayPal.me link | paypal.me/john |
| Revolut | Revtag | revolut.me/john123 |
| Visa+ | Payname | +JohnSmith.PayPal |
| UPI (India) | Virtual Payment Address | john@bank |

**Our Choice**: `@username` format (AzureTag) - familiar, simple, proven pattern

---

## 3. Identifier Architecture

### 3.1 Two-Layer Identifier System

```
┌─────────────────────────────────────────────────────────────────┐
│                    IDENTIFIER ARCHITECTURE                       │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  LAYER 1: INTERNAL (Backend Database)                           │
│  ════════════════════════════════════                           │
│  • UserId: UUID (Primary Key)                                   │
│  • AccountId: UUID (Primary Key)                                │
│  • NEVER sent to frontend in API responses                      │
│  • Used only for database operations                            │
│                                                                  │
│  LAYER 2: PUBLIC (Frontend/User-Facing)                         │
│  ═══════════════════════════════════════                        │
│  • AzureTag: @username (User identifier)                        │
│  • AccountNumber: AB-XXXX-XXXX-XX (Account identifier)          │
│  • Safe to display, share, search                               │
│                                                                  │
│  MAPPING TABLE (Backend):                                        │
│  ┌──────────────┬──────────────────────────────────────────┐   │
│  │ AzureTag     │ UserId (Internal)                         │   │
│  ├──────────────┼──────────────────────────────────────────┤   │
│  │ @johnsmith   │ 550e8400-e29b-41d4-a716-446655440000     │   │
│  │ @janesmith   │ 7c9e6679-7425-40de-944b-e07fc1f90ae7     │   │
│  └──────────────┴──────────────────────────────────────────┘   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 3.2 AzureTag Specification

| Property | Specification |
|----------|---------------|
| **Format** | `@` + alphanumeric + underscore |
| **Regex** | `^@[a-zA-Z][a-zA-Z0-9_]{2,19}$` |
| **Length** | 3-20 characters (excluding @) |
| **Case** | Stored lowercase, search case-insensitive |
| **Uniqueness** | Globally unique per user |
| **Mutability** | **Immutable** after creation |
| **Creation** | Set during registration (required) |

**Valid Examples**: `@johnsmith`, `@jane_doe`, `@user123`
**Invalid Examples**: `@jo` (too short), `@123user` (starts with number), `@john.doe` (invalid char)

### 3.3 Account Number Specification

| Property | Specification |
|----------|---------------|
| **Format** | `AB-XXXX-XXXX-XX` (AB = AzureBank prefix) |
| **Generation** | Auto-generated on account creation |
| **Uniqueness** | Globally unique per account |
| **Check Digit** | Last 2 digits are MOD97 check |
| **Display** | Always formatted with dashes |

**Example**: `AB-1234-5678-90`

---

## 4. Transfer Types

### 4.1 Internal Transfer (Between Own Accounts)
```
User's Savings Account ──────► User's Checking Account
         │                              │
         └───── Same User ──────────────┘
```
- Already implemented
- Uses dropdown selection
- No search needed

### 4.2 External Transfer (To Another User)
```
User A's Account ──────────────► User B's Account
         │                              │
         └───── Different Users ────────┘
```
- NEW FEATURE
- Requires recipient lookup
- Requires confirmation step

---

## 5. User Flow: External Transfer

### 5.1 Complete Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                      EXTERNAL TRANSFER USER FLOW                             │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌───────────────┐                                                          │
│  │   Dashboard   │                                                          │
│  │   Click       │                                                          │
│  │  "Transfer"   │                                                          │
│  └───────┬───────┘                                                          │
│          │                                                                   │
│          ▼                                                                   │
│  ┌───────────────────────────────────────────────────────────────┐         │
│  │                    TRANSFER DIALOG - STEP 1                    │         │
│  │                                                                │         │
│  │   Transfer Type:                                               │         │
│  │   ┌─────────────────────┐  ┌─────────────────────┐           │         │
│  │   │  ○ Between My       │  │  ● To Someone Else  │           │         │
│  │   │    Accounts         │  │                     │           │         │
│  │   └─────────────────────┘  └─────────────────────┘           │         │
│  │                                                                │         │
│  │   From Account:                                                │         │
│  │   ┌────────────────────────────────────────────────┬─────┐   │         │
│  │   │  Savings Account (€12,450.00)                  │  ▼  │   │         │
│  │   └────────────────────────────────────────────────┴─────┘   │         │
│  │                                                                │         │
│  │                            [Next]                              │         │
│  └───────────────────────────────────────────────────────────────┘         │
│          │                                                                   │
│          ▼                                                                   │
│  ┌───────────────────────────────────────────────────────────────┐         │
│  │                    TRANSFER DIALOG - STEP 2                    │         │
│  │                       (Recipient Search)                       │         │
│  │                                                                │         │
│  │   Find Recipient:                                              │         │
│  │   ┌────────────────────────────────────────────────┬────────┐│         │
│  │   │  @                                             │ Search ││         │
│  │   └────────────────────────────────────────────────┴────────┘│         │
│  │   Enter AzureTag (e.g., @johnsmith)                          │         │
│  │                                                                │         │
│  │   ─── OR select from Recent Recipients ───                    │         │
│  │                                                                │         │
│  │   ┌────────────────────────────────────────────────────────┐ │         │
│  │   │  ★ @janesmith - Jane S.                  [Select]     │ │         │
│  │   │  ★ @mikebrown - Mike B.                  [Select]     │ │         │
│  │   └────────────────────────────────────────────────────────┘ │         │
│  │                                                                │         │
│  └───────────────────────────────────────────────────────────────┘         │
│          │                                                                   │
│          │ Search / Select                                                  │
│          ▼                                                                   │
│  ┌───────────────────────────────────────────────────────────────┐         │
│  │                 SEARCH RESULT / CONFIRMATION                   │         │
│  │                                                                │         │
│  │   ┌────────────────────────────────────────────────────────┐ │         │
│  │   │  ✓ Recipient Found                                     │ │         │
│  │   │                                                        │ │         │
│  │   │  👤 Jane Smith                                         │ │         │
│  │   │     @janesmith                                         │ │         │
│  │   │                                                        │ │         │
│  │   │  Select recipient's account:                           │ │         │
│  │   │  ┌──────────────────────────────────────────────────┐ │ │         │
│  │   │  │ ● Savings Account (AB-9876-5432-10)              │ │ │         │
│  │   │  │ ○ Checking Account (AB-1111-2222-33)             │ │ │         │
│  │   │  └──────────────────────────────────────────────────┘ │ │         │
│  │   │                                                        │ │         │
│  │   │  [ ] Save to recent recipients                        │ │         │
│  │   └────────────────────────────────────────────────────────┘ │         │
│  │                                                                │         │
│  │                    [Back]            [Continue]                │         │
│  └───────────────────────────────────────────────────────────────┘         │
│          │                                                                   │
│          ▼                                                                   │
│  ┌───────────────────────────────────────────────────────────────┐         │
│  │                    TRANSFER DIALOG - STEP 3                    │         │
│  │                       (Amount & Details)                       │         │
│  │                                                                │         │
│  │   Amount:                                                      │         │
│  │   ┌─────┬──────────────────────────────────────────────────┐ │         │
│  │   │  €  │  500.00                                          │ │         │
│  │   └─────┴──────────────────────────────────────────────────┘ │         │
│  │   Available: €12,450.00                                       │         │
│  │                                                                │         │
│  │   Description (optional):                                     │         │
│  │   ┌────────────────────────────────────────────────────────┐ │         │
│  │   │  Lunch money                                           │ │         │
│  │   └────────────────────────────────────────────────────────┘ │         │
│  │                                                                │         │
│  │                    [Back]            [Review]                  │         │
│  └───────────────────────────────────────────────────────────────┘         │
│          │                                                                   │
│          ▼                                                                   │
│  ┌───────────────────────────────────────────────────────────────┐         │
│  │                    TRANSFER DIALOG - STEP 4                    │         │
│  │                    (Review & Confirm)                          │         │
│  │                                                                │         │
│  │   ┌────────────────────────────────────────────────────────┐ │         │
│  │   │              REVIEW YOUR TRANSFER                      │ │         │
│  │   │                                                        │ │         │
│  │   │  From:    My Savings Account                          │ │         │
│  │   │           AB-1234-5678-90                             │ │         │
│  │   │                                                        │ │         │
│  │   │  To:      Jane Smith (@janesmith)                     │ │         │
│  │   │           Savings Account                              │ │         │
│  │   │           AB-9876-5432-10                             │ │         │
│  │   │                                                        │ │         │
│  │   │  Amount:  €500.00                                     │ │         │
│  │   │  Note:    Lunch money                                 │ │         │
│  │   │                                                        │ │         │
│  │   │  ────────────────────────────────────────────────────  │ │         │
│  │   │  ⚠ Please verify the recipient details before         │ │         │
│  │   │    confirming. Transfers cannot be reversed.          │ │         │
│  │   └────────────────────────────────────────────────────────┘ │         │
│  │                                                                │         │
│  │               [Cancel]            [Confirm Transfer]           │         │
│  └───────────────────────────────────────────────────────────────┘         │
│          │                                                                   │
│          │ Confirm                                                          │
│          ▼                                                                   │
│  ┌───────────────────────────────────────────────────────────────┐         │
│  │                       SUCCESS                                  │         │
│  │                                                                │         │
│  │                         ✓                                      │         │
│  │                                                                │         │
│  │              Transfer Successful!                              │         │
│  │                                                                │         │
│  │         €500.00 sent to Jane Smith                            │         │
│  │                                                                │         │
│  │                     [Done]                                     │         │
│  └───────────────────────────────────────────────────────────────┘         │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 5.2 Error Scenarios

| Error | When | User Message | Action |
|-------|------|--------------|--------|
| User not found | Search returns no match | "No user found with that AzureTag" | Allow retry |
| Insufficient funds | Amount > balance | "Insufficient funds. Available: €X" | Block submit |
| Self-transfer | Same user selected | "Cannot transfer to yourself" | Block continue |
| Rate limited | Too many searches | "Please wait before searching again" | 30s cooldown |
| Network error | API failure | "Connection error. Please try again" | Retry button |

---

## 6. Data Models

### 6.1 User Entity (Updated)

```typescript
interface User {
  // Internal - NEVER sent to frontend
  id: string;                    // UUID (database primary key)
  passwordHash: string;

  // Public - Safe to expose
  azureTag: string;              // @username (unique, immutable)
  email: string;
  firstName: string;
  surname: string;
  createdAt: string;
  updatedAt: string;
}

// What frontend receives (API response)
interface UserPublicProfile {
  azureTag: string;              // @johnsmith
  displayName: string;           // "John S." (First + Surname initial)
}
```

### 6.2 Account Entity (Updated)

```typescript
interface Account {
  // Internal - NEVER sent to frontend directly
  id: string;                    // UUID (database primary key)
  userId: string;                // Owner's UUID

  // Public - Safe to expose
  accountNumber: string;         // AB-XXXX-XXXX-XX (public identifier)
  name: string;
  accountType: AccountType;
  balance: number;
  currency: string;
  isActive: boolean;
  createdAt: string;
}

// What frontend receives for own accounts
interface AccountResponse {
  accountNumber: string;         // AB-1234-5678-90
  name: string;
  accountType: AccountType;
  balance: number;
  currency: string;
  isActive: boolean;
  createdAt: string;
}

// What frontend receives when searching others' accounts
interface AccountPublicInfo {
  accountNumber: string;         // AB-9876-5432-10
  accountType: AccountType;      // "Savings"
  // NO balance, NO name - privacy!
}
```

### 6.3 Transfer Request (Updated)

```typescript
// Internal transfer (between own accounts)
interface InternalTransferRequest {
  fromAccountNumber: string;     // AB-XXXX-XXXX-XX
  toAccountNumber: string;       // AB-XXXX-XXXX-XX
  amount: number;
  description: string;
}

// External transfer (to another user)
interface ExternalTransferRequest {
  fromAccountNumber: string;     // Sender's account
  toAzureTag: string;            // Recipient's @username
  toAccountNumber: string;       // Recipient's specific account
  amount: number;
  description: string;
}
```

### 6.4 Recipient Search

```typescript
// Search request
interface RecipientSearchRequest {
  azureTag: string;              // @johnsmith (without @)
}

// Search response
interface RecipientSearchResponse {
  found: boolean;
  recipient?: {
    azureTag: string;            // @johnsmith
    displayName: string;         // "John S."
    accounts: AccountPublicInfo[];
  };
}
```

### 6.5 Recent Recipients

```typescript
interface RecentRecipient {
visibleId: string;               // UUID for frontend reference
  recipientAzureTag: string;     // @janesmith
  recipientDisplayName: string;  // "Jane S."
  lastUsedAccountNumber: string; // AB-9876-5432-10
  lastTransferDate: string;
  transferCount: number;
}
```

---

## 7. API Endpoints

### 7.1 New Endpoints Required

| Method | Endpoint | Purpose |
|--------|----------|---------|
| GET | `/api/recipients/search?tag={azureTag}` | Search for recipient by AzureTag |
| GET | `/api/recipients/recent` | Get recent transfer recipients |
| POST | `/api/transfers/external` | Execute external transfer |
| DELETE | `/api/recipients/{id}` | Remove from recent recipients |

### 7.2 Endpoint Specifications

#### Search Recipient
```
GET /api/recipients/search?tag=johnsmith

Response 200:
{
  "found": true,
  "recipient": {
    "azureTag": "@johnsmith",
    "displayName": "John S.",
    "accounts": [
      {
        "accountNumber": "AB-9876-5432-10",
        "accountType": "Savings"
      },
      {
        "accountNumber": "AB-1111-2222-33",
        "accountType": "Checking"
      }
    ]
  }
}

Response 404:
{
  "found": false,
  "message": "No user found with AzureTag @johnsmith"
}

Rate Limit: 5 requests per minute per user
```

#### External Transfer
```
POST /api/transfers/external

Request:
{
  "fromAccountNumber": "AB-1234-5678-90",
  "toAzureTag": "@johnsmith",
  "toAccountNumber": "AB-9876-5432-10",
  "amount": 500.00,
  "description": "Lunch money"
}

Response 200:
{
  "success": true,
  "transactionId": "TXN-20251217-123456",
  "fromAccount": {
    "accountNumber": "AB-1234-5678-90",
    "newBalance": 11950.00
  },
  "toRecipient": {
    "azureTag": "@johnsmith",
    "displayName": "John S.",
    "accountNumber": "AB-9876-5432-10"
  },
  "amount": 500.00,
  "timestamp": "2025-12-17T14:30:00Z"
}
```

---

## 8. Security Considerations

### 8.1 What is NEVER Exposed to Frontend

| Internal Data | Why Hidden |
|--------------|------------|
| `userId` (UUID) | Prevents user enumeration |
| `accountId` (UUID) | Prevents account enumeration |
| `passwordHash` | Obviously sensitive |
| Other users' balances | Privacy |
| Other users' full names | Privacy (only initials) |
| Other users' email | Privacy |

### 8.2 Security Measures

| Measure | Implementation |
|---------|---------------|
| **Rate Limiting** | 5 searches/minute per user |
| **Input Validation** | Strict AzureTag format validation |
| **Confirmation Step** | Always show recipient name before transfer |
| **Display Name Masking** | "John S." not "John Smith" |
| **Audit Logging** | Log all transfer attempts |
| **Amount Limits** | Daily/per-transaction limits (future) |

### 8.3 AzureTag vs Internal ID Mapping

```
Frontend Request                    Backend Processing
────────────────                    ──────────────────

"Transfer to @johnsmith"     ──►    Lookup: @johnsmith → userId
                                    Validate: User exists, active
                                    Map: accountNumber → accountId
                                    Execute: Internal DB transaction
                                    Return: Public info only
```

---

## 9. Frontend Components

### 9.1 New Components Required

```
src/components/transfers/
├── TransferDialog/
│   ├── TransferDialog.tsx           # Main dialog wrapper
│   ├── TransferTypeSelector.tsx     # Internal vs External toggle
│   ├── AccountSelector.tsx          # Source account dropdown
│   ├── RecipientSearch.tsx          # AzureTag search input
│   ├── RecipientResult.tsx          # Display found recipient
│   ├── RecentRecipients.tsx         # Saved recipients list
│   ├── AmountInput.tsx              # Currency amount input
│   ├── TransferReview.tsx           # Confirmation step
│   └── TransferSuccess.tsx          # Success state
├── index.ts
└── TransferDialog.styles.ts
```

### 9.2 State Management

```typescript
// Transfer dialog state
interface TransferDialogState {
  step: 'type' | 'recipient' | 'amount' | 'review' | 'success';
  transferType: 'internal' | 'external';
  fromAccount: AccountResponse | null;

  // For internal transfers
  toOwnAccount: AccountResponse | null;

  // For external transfers
  recipientSearch: string;
  recipientResult: RecipientSearchResponse | null;
  selectedRecipientAccount: AccountPublicInfo | null;
  saveAsRecent: boolean;

  // Common
  amount: number;
  description: string;

  // Status
  isSearching: boolean;
  isTransferring: boolean;
  error: string | null;
}
```

### 9.3 RTK Query Additions

```typescript
// src/features/transfers/transfersApi.ts

export const transfersApi = baseApi.injectEndpoints({
  endpoints: (builder) => ({
    // Search for recipient
    searchRecipient: builder.query<RecipientSearchResponse, string>({
      query: (azureTag) => `/recipients/search?tag=${azureTag}`,
    }),

    // Get recent recipients
    getRecentRecipients: builder.query<RecentRecipient[], void>({
      query: () => '/recipients/recent',
      providesTags: ['RecentRecipient'],
    }),

    // External transfer
    externalTransfer: builder.mutation<TransferResult, ExternalTransferRequest>({
      query: (data) => ({
        url: '/transfers/external',
        method: 'POST',
        body: data,
      }),
      invalidatesTags: ['Account', 'Transaction', 'RecentRecipient'],
    }),

    // Remove recent recipient
    removeRecentRecipient: builder.mutation<void, string>({
      query: (id) => ({
        url: `/recipients/${id}`,
        method: 'DELETE',
      }),
      invalidatesTags: ['RecentRecipient'],
    }),
  }),
});
```

---

## 10. Registration Flow Update

Users must choose their AzureTag during registration:

```
┌─────────────────────────────────────────────────────────────┐
│                    REGISTRATION FORM                         │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌────────────────┐  ┌────────────────┐                    │
│  │ First Name *   │  │ Surname *      │                    │
│  └────────────────┘  └────────────────┘                    │
│                                                              │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ Email *                                              │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                              │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ Choose your AzureTag *                    [NEW!]    │   │
│  │ ┌───┬─────────────────────────────────────────────┐ │   │
│  │ │ @ │ johnsmith                                   │ │   │
│  │ └───┴─────────────────────────────────────────────┘ │   │
│  │ 3-20 characters, letters/numbers/underscore only    │   │
│  │ ✓ @johnsmith is available                           │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                              │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ Password *                                          │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                              │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ Confirm Password *                                  │   │
│  └─────────────────────────────────────────────────────┘   │
│                                                              │
│  [                CREATE ACCOUNT                    ]       │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### AzureTag Validation Rules

| Rule | Validation |
|------|------------|
| Required | Cannot be empty |
| Format | Starts with letter, alphanumeric + underscore only |
| Length | 3-20 characters |
| Unique | Real-time availability check (debounced) |
| Immutable | Cannot be changed after registration |

---

## 11. Implementation Priority

### Phase 1: Backend Foundation
1. Add `azureTag` field to User entity
2. Add `accountNumber` generation for accounts
3. Create recipient search endpoint
4. Create external transfer endpoint
5. Add rate limiting

### Phase 2: Frontend - Registration
6. Add AzureTag field to registration form
7. Add availability check (debounced API call)
8. Update validation rules

### Phase 3: Frontend - Transfer Dialog
9. Create multi-step transfer dialog
10. Add transfer type selector
11. Create recipient search component
12. Create recipient result display
13. Add recent recipients list
14. Create review/confirmation step

### Phase 4: Testing & Polish
15. Unit tests for validation
16. Integration tests for transfer flow
17. Security testing (rate limiting, input validation)
18. UX testing with real users

---

## 12. Summary

### Key Takeaways

1. **AzureTag (`@username`)** is the public user identifier
2. **Account Number (`AB-XXXX-XXXX-XX`)** is the public account identifier
3. **Internal UUIDs are NEVER exposed** to frontend
4. **4-step transfer flow**: Type → Recipient → Amount → Confirm
5. **Security first**: Rate limiting, confirmation, masking

### Files to Update

| Document | Updates Needed |
|----------|---------------|
| 04a-ux-user-flows.md | Add external transfer flow, update registration |
| 04b-ux-wireframes.md | Add transfer dialog wireframes |
| 04e-frontend-components.md | Add transfer components, types |
| 04j-frontend-design-final.md | Consolidate all changes |
| 09-api-contracts.md | Add new endpoints |

---

**Document Status**: COMPLETE - Ready for implementation
