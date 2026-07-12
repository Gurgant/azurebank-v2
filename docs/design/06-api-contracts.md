# API Contracts
## AzureBank - Bank Account Management System

**Document Version**: 2.0
**Created**: 2025-12-16
**Updated**: 2026-01-08
**Author**: Backend Lead
**Status**: COMPLETE - Phase 4

---

## 1. API Overview

### 1.1 Base Configuration

| Setting | Value |
|---------|-------|
| Base URL | `/api` |
| Content-Type | `application/json` |
| Accept | `application/json` |
| Authentication | Bearer JWT (except public endpoints) |
| Date Format | ISO 8601 (`2026-01-08T14:30:00Z`) |
| Money Format | Decimal (e.g., `1234.56`, NOT cents) |
| Pagination | Page-based with `page` and `pageSize` |

### 1.2 BFF Architecture

> **IMPORTANT**: The frontend does NOT communicate directly with the Backend API.
> All requests go through the BFF (Backend-for-Frontend) gateway.

```
Browser  -->  BFF Gateway (/bff/*, /api/*)  -->  Backend API (/api/*)
              |                                  |
              |  Session Cookie                  |  JWT Bearer Token
              |  (HTTP-only, Secure)             |  (injected by BFF)
```

**Why BFF?**
- JWT tokens are stored server-side, never reach browser
- Session cookie is HTTP-only (JavaScript cannot access)
- Maximum XSS protection for banking application
- See [08-security-design.md](08-security-design.md) for details

### 1.3 Common Headers

**Browser to BFF (Request)**:
```
Cookie: .AzureBank.Session=<session_id>   # Automatic (HTTP-only cookie)
Content-Type: application/json            # Required for POST/PUT/PATCH
X-Correlation-ID: <uuid>                  # Optional, for request tracking
```

**BFF to Backend API (Request - internal)**:
```
Authorization: Bearer <jwt_token>         # Injected by BFF from session
Content-Type: application/json
X-Correlation-ID: <uuid>                  # Propagated from browser request
```

**Response Headers**:
```
Content-Type: application/json
X-Correlation-ID: <uuid>                  # Always returned
X-Request-Duration-Ms: <number>           # Processing time
Set-Cookie: .AzureBank.Session            # Only on login (HTTP-only, Secure, SameSite=Strict)
```

### 1.4 API Versioning

For MVP, no versioning. Future consideration:
- URL path: `/api/v1/...`
- Header: `Api-Version: 1`

---

## 2. BFF Authentication Endpoints

> These endpoints are handled by the BFF, NOT the Backend API.
> The browser calls these endpoints, and they manage session state.

### 2.0 BFF Endpoints Summary

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/bff/auth/login` | POST | None | Login and create session |
| `/bff/auth/logout` | POST | Session | End session |
| `/bff/auth/me` | GET | Session | Get current user info |
| `/bff/auth/session-status` | GET | Session | Get session timeouts |
| `/bff/auth/verify-pin` | POST | Session | Verify PIN for step-up auth |

### 2.0.1 POST /bff/auth/login

**Description**: Authenticate user and create server-side session

**Authentication**: None (Public)

**Request Body**:
```json
{
  "email": "john.doe@example.com",
  "password": "SecurePass123!"
}
```

**Success Response**: `200 OK`
```json
{
  "data": {
    "user": {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "email": "john.doe@example.com",
      "firstName": "John",
      "lastName": "Doe",
      "azureTag": "johndoe"
    }
  },
  "message": "Login successful"
}
```

**Response Headers**:
```
Set-Cookie: .AzureBank.Session=abc123; HttpOnly; Secure; SameSite=Strict; Path=/
```

**Note**: NO TOKEN is returned to the browser. JWT is stored server-side in session.

**Error Responses**:
| Status | Type | Message |
|--------|------|---------|
| 400 | VALIDATION_ERROR | Email is required |
| 401 | INVALID_CREDENTIALS | Invalid credentials |
| 429 | RATE_LIMIT_EXCEEDED | Too many login attempts |

---

### 2.0.2 POST /bff/auth/logout

**Description**: End user session and clear cookie

**Authentication**: Session cookie required

**Request Body**: None

**Success Response**: `200 OK`
```json
{
  "data": null,
  "message": "Logged out successfully"
}
```

**Response Headers**:
```
Set-Cookie: .AzureBank.Session=; HttpOnly; Secure; SameSite=Strict; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT
```

---

### 2.0.3 GET /bff/auth/me

**Description**: Get current authenticated user information

**Authentication**: Session cookie required

**Success Response**: `200 OK`
```json
{
  "data": {
    "user": {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "email": "john.doe@example.com",
      "firstName": "John",
      "lastName": "Doe",
      "azureTag": "johndoe"
    },
    "session": {
      "authLevel": 1,
      "createdAt": "2026-01-08T10:30:00Z",
      "lastActivity": "2026-01-08T10:45:00Z"
    }
  }
}
```

**Error Responses**:
| Status | Type | Message |
|--------|------|---------|
| 401 | SESSION_EXPIRED | Session has expired |

---

### 2.0.4 GET /bff/auth/session-status

**Description**: Get session timeout information for UI warning

**Authentication**: Session cookie required

**Success Response**: `200 OK`
```json
{
  "data": {
    "authLevel": 1,
    "inactivityExpiresIn": 1800,
    "absoluteExpiresIn": 3600,
    "pinVerifiedUntil": null
  }
}
```

**Fields**:
- `authLevel`: Current authentication level (1=session, 2=PIN verified)
- `inactivityExpiresIn`: Seconds until inactivity timeout
- `absoluteExpiresIn`: Seconds until absolute session expiry
- `pinVerifiedUntil`: ISO timestamp when PIN verification expires (null if not verified)

---

### 2.0.5 POST /bff/auth/verify-pin

**Description**: Verify user PIN for step-up authentication (Level 2)

**Authentication**: Session cookie required

**Request Body**:
```json
{
  "pin": "123456"
}
```

**Success Response**: `200 OK`
```json
{
  "data": {
    "verified": true,
    "authLevel": 2,
    "expiresAt": "2026-01-08T10:35:00Z"
  },
  "message": "PIN verified"
}
```

**Error Responses**:
| Status | Type | Message |
|--------|------|---------|
| 400 | INVALID_PIN | Invalid PIN |
| 429 | PIN_LOCKED | Too many failed attempts. Try again in 15 minutes |

**Note**: After 3 failed attempts, PIN verification is locked for 15 minutes.

---

## 3. Backend API Authentication Endpoints

> These endpoints are called by the BFF, NOT directly by the browser.

### 2.1 POST /api/auth/register

**Description**: Create a new user account with initial bank account

**Authentication**: None (Public)

**Request Body**:
```json
{
  "email": "john.doe@example.com",
  "password": "SecurePass123!",
  "confirmPassword": "SecurePass123!",
  "firstName": "John",
  "lastName": "Doe",
  "azureTag": "johndoe"
}
```

**Validation Rules**:
| Field | Rules |
|-------|-------|
| email | Required, valid email format, max 255 chars, unique |
| password | Required, min 8 chars, 1 uppercase, 1 lowercase, 1 number |
| confirmPassword | Required, must match password |
| firstName | Required, 1-100 chars, letters only |
| lastName | Required, 1-100 chars, letters only |
| azureTag | Required, 3-20 chars, alphanumeric + underscore, unique, starts with letter |

**Success Response**: `201 Created`
```json
{
  "data": {
    "user": {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "email": "john.doe@example.com",
      "firstName": "John",
      "lastName": "Doe",
      "azureTag": "johndoe",
      "createdAt": "2026-01-08T10:30:00Z"
    },
    "account": {
      "id": "660e8400-e29b-41d4-a716-446655440001",
      "accountNumber": "AB-1234-5678-90",
      "name": "Primary Account",
      "type": "checking",
      "balance": 0.00,
      "isPrimary": true,
      "createdAt": "2026-01-08T10:30:00Z"
    },
    "token": {
      "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
      "expiresIn": 1800,
      "tokenType": "Bearer"
    }
  },
  "message": "Registration successful"
}
```

**Error Responses**:

`422 Validation Error`:
```json
{
  "type": "VALIDATION_ERROR",
  "message": "Validation failed",
  "correlationId": "abc-123-def",
  "statusCode": 422,
  "errors": {
    "email": ["Email is already registered"],
    "password": ["Password must be at least 8 characters"],
    "azureTag": ["AzureTag is already taken"]
  }
}
```

`409 Conflict`:
```json
{
  "type": "CONFLICT",
  "message": "Email or AzureTag already exists",
  "correlationId": "abc-123-def",
  "statusCode": 409
}
```

---

### 2.2 POST /api/auth/login

**Description**: Authenticate user and receive JWT token

**Authentication**: None (Public)

**Request Body**:
```json
{
  "email": "john.doe@example.com",
  "password": "SecurePass123!"
}
```

**Validation Rules**:
| Field | Rules |
|-------|-------|
| email | Required, valid email format |
| password | Required, non-empty |

**Success Response**: `200 OK`
```json
{
  "data": {
    "user": {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "email": "john.doe@example.com",
      "firstName": "John",
      "lastName": "Doe",
      "azureTag": "johndoe",
      "createdAt": "2026-01-08T10:30:00Z"
    },
    "token": {
      "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
      "expiresIn": 1800,
      "tokenType": "Bearer"
    }
  },
  "message": "Login successful"
}
```

**Error Responses**:

`401 Unauthorized`:
```json
{
  "type": "AUTHENTICATION_FAILED",
  "message": "Invalid email or password",
  "correlationId": "abc-123-def",
  "statusCode": 401
}
```

---

### 2.3 POST /api/auth/logout

**Description**: Invalidate refresh token (if implemented)

**Authentication**: Required

**Request Body**: None

**Success Response**: `200 OK`
```json
{
  "message": "Logout successful"
}
```

---

### 2.4 GET /api/auth/me

**Description**: Get current authenticated user information

**Authentication**: Required

**Success Response**: `200 OK`
```json
{
  "data": {
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "email": "john.doe@example.com",
    "firstName": "John",
    "lastName": "Doe",
    "azureTag": "johndoe",
    "createdAt": "2026-01-08T10:30:00Z"
  }
}
```

---

## 3. Account Endpoints

### 3.1 POST /api/accounts

**Description**: Create a new bank account for the authenticated user

**Authentication**: Required

**Request Body**:
```json
{
  "name": "Savings Account",
  "type": "savings"
}
```

**Validation Rules**:
| Field | Rules |
|-------|-------|
| name | Required, 1-100 chars |
| type | Required, one of: 'checking', 'savings', 'investment' |

**Success Response**: `201 Created`
```json
{
  "data": {
    "id": "770e8400-e29b-41d4-a716-446655440002",
    "accountNumber": "AB-2345-6789-01",
    "name": "Savings Account",
    "type": "savings",
    "balance": 0.00,
    "isPrimary": false,
    "createdAt": "2026-01-08T11:00:00Z"
  },
  "message": "Account created successfully"
}
```

---

### 3.2 GET /api/accounts

**Description**: Get all accounts for the authenticated user

**Authentication**: Required

**Query Parameters**:
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| includeDeleted | boolean | No | false | Include soft-deleted accounts |

**Success Response**: `200 OK`
```json
{
  "data": [
    {
      "id": "660e8400-e29b-41d4-a716-446655440001",
      "accountNumber": "AB-1234-5678-90",
      "name": "Primary Account",
      "type": "checking",
      "balance": 1500.00,
      "isPrimary": true,
      "createdAt": "2026-01-08T10:30:00Z",
      "updatedAt": "2026-01-08T14:30:00Z"
    },
    {
      "id": "770e8400-e29b-41d4-a716-446655440002",
      "accountNumber": "AB-2345-6789-01",
      "name": "Savings Account",
      "type": "savings",
      "balance": 5000.00,
      "isPrimary": false,
      "createdAt": "2026-01-08T11:00:00Z",
      "updatedAt": "2026-01-08T11:00:00Z"
    }
  ]
}
```

---

### 3.3 GET /api/accounts/{id}

**Description**: Get a specific account by ID

**Authentication**: Required (owner only)

**Path Parameters**:
| Parameter | Type | Description |
|-----------|------|-------------|
| id | UUID | Account ID |

**Success Response**: `200 OK`
```json
{
  "data": {
    "id": "660e8400-e29b-41d4-a716-446655440001",
    "accountNumber": "AB-1234-5678-90",
    "name": "Primary Account",
    "type": "checking",
    "balance": 1500.00,
    "isPrimary": true,
    "createdAt": "2026-01-08T10:30:00Z",
    "updatedAt": "2026-01-08T14:30:00Z"
  }
}
```

**Error Responses**:

`404 Not Found`:
```json
{
  "type": "NOT_FOUND",
  "message": "Account with identifier '660e8400-...' was not found",
  "correlationId": "abc-123-def",
  "statusCode": 404
}
```

`403 Forbidden`:
```json
{
  "type": "ACCESS_DENIED",
  "message": "You do not have access to this account",
  "correlationId": "abc-123-def",
  "statusCode": 403
}
```

---

### 3.4 GET /api/accounts/{id}/balance

**Description**: Get current balance or historical balance at a specific time

**Authentication**: Required (owner only)

**Path Parameters**:
| Parameter | Type | Description |
|-----------|------|-------------|
| id | UUID | Account ID |

**Query Parameters**:
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| at | datetime | No | now | ISO 8601 datetime for historical balance |

**Success Response**: `200 OK`

Current balance:
```json
{
  "data": {
    "accountId": "660e8400-e29b-41d4-a716-446655440001",
    "balance": 1500.00,
    "currency": "EUR",
    "asOf": "2026-01-08T14:30:00Z"
  }
}
```

Historical balance:
```json
{
  "data": {
    "accountId": "660e8400-e29b-41d4-a716-446655440001",
    "balance": 1200.00,
    "currency": "EUR",
    "asOf": "2026-01-01T00:00:00Z",
    "isHistorical": true
  }
}
```

---

### 3.5 PATCH /api/accounts/{id}

**Description**: Update account details (name only)

**Authentication**: Required (owner only)

**Path Parameters**:
| Parameter | Type | Description |
|-----------|------|-------------|
| id | UUID | Account ID |

**Request Body**:
```json
{
  "name": "My Checking Account"
}
```

**Success Response**: `200 OK`
```json
{
  "data": {
    "id": "660e8400-e29b-41d4-a716-446655440001",
    "accountNumber": "AB-1234-5678-90",
    "name": "My Checking Account",
    "type": "checking",
    "balance": 1500.00,
    "isPrimary": true,
    "createdAt": "2026-01-08T10:30:00Z",
    "updatedAt": "2026-01-08T15:00:00Z"
  },
  "message": "Account updated successfully"
}
```

---

### 3.6 PATCH /api/accounts/{id}/set-primary

**Description**: Set an account as the primary account for receiving transfers

**Authentication**: Required (owner only)

**Path Parameters**:
| Parameter | Type | Description |
|-----------|------|-------------|
| id | UUID | Account ID |

**Request Body**: None

**Success Response**: `200 OK`
```json
{
  "data": {
    "id": "770e8400-e29b-41d4-a716-446655440002",
    "accountNumber": "AB-2345-6789-01",
    "name": "Savings Account",
    "type": "savings",
    "balance": 5000.00,
    "isPrimary": true,
    "createdAt": "2026-01-08T11:00:00Z",
    "updatedAt": "2026-01-08T15:30:00Z"
  },
  "message": "Account set as primary"
}
```

**Note**: Setting a new primary automatically unsets the previous primary.

---

### 3.7 DELETE /api/accounts/{id}

**Description**: Soft delete an account (balance must be 0)

**Authentication**: Required (owner only)

**Path Parameters**:
| Parameter | Type | Description |
|-----------|------|-------------|
| id | UUID | Account ID |

**Success Response**: `200 OK`
```json
{
  "message": "Account deleted successfully"
}
```

**Error Responses**:

`400 Bad Request`:
```json
{
  "type": "BUSINESS_RULE_VIOLATION",
  "message": "Cannot delete account with non-zero balance. Current balance: €1500.00",
  "correlationId": "abc-123-def",
  "statusCode": 400
}
```

`400 Bad Request`:
```json
{
  "type": "BUSINESS_RULE_VIOLATION",
  "message": "Cannot delete primary account. Set another account as primary first.",
  "correlationId": "abc-123-def",
  "statusCode": 400
}
```

---

## 4. Transaction Endpoints

### 4.1 GET /api/transactions

**Description**: Get transaction history with filtering and pagination

**Authentication**: Required

**Query Parameters**:
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| accountId | UUID | No | all | Filter by account |
| type | string | No | all | Filter by type: deposit, withdrawal, transfer_in, transfer_out |
| from | datetime | No | 30 days ago | Start date (inclusive) |
| to | datetime | No | now | End date (inclusive) |
| page | integer | No | 1 | Page number (1-based) |
| pageSize | integer | No | 20 | Items per page (max 100) |
| sortBy | string | No | createdAt | Sort field: createdAt, amount |
| sortOrder | string | No | desc | Sort order: asc, desc |

**Success Response**: `200 OK`
```json
{
  "data": [
    {
      "id": "880e8400-e29b-41d4-a716-446655440003",
      "accountId": "660e8400-e29b-41d4-a716-446655440001",
      "type": "deposit",
      "amount": 500.00,
      "description": "Salary deposit",
      "balanceAfter": 1500.00,
      "relatedTransactionId": null,
      "recipientAzureTag": null,
      "senderAzureTag": null,
      "createdAt": "2026-01-08T14:30:00Z"
    },
    {
      "id": "990e8400-e29b-41d4-a716-446655440004",
      "accountId": "660e8400-e29b-41d4-a716-446655440001",
      "type": "transfer_out",
      "amount": 100.00,
      "description": "Payment to @janedoe",
      "balanceAfter": 1400.00,
      "relatedTransactionId": "aa0e8400-e29b-41d4-a716-446655440005",
      "recipientAzureTag": "janedoe",
      "senderAzureTag": null,
      "createdAt": "2026-01-08T12:00:00Z"
    }
  ],
  "pagination": {
    "page": 1,
    "pageSize": 20,
    "totalItems": 47,
    "totalPages": 3,
    "hasNextPage": true,
    "hasPreviousPage": false
  }
}
```

---

### 4.2 GET /api/transactions/{id}

**Description**: Get a specific transaction by ID

**Authentication**: Required (owner only)

**Path Parameters**:
| Parameter | Type | Description |
|-----------|------|-------------|
| id | UUID | Transaction ID |

**Success Response**: `200 OK`
```json
{
  "data": {
    "id": "880e8400-e29b-41d4-a716-446655440003",
    "accountId": "660e8400-e29b-41d4-a716-446655440001",
    "type": "deposit",
    "amount": 500.00,
    "description": "Salary deposit",
    "balanceAfter": 1500.00,
    "relatedTransactionId": null,
    "recipientAzureTag": null,
    "senderAzureTag": null,
    "createdAt": "2026-01-08T14:30:00Z"
  }
}
```

---

### 4.3 POST /api/transactions/deposit

**Description**: Deposit money into an account

**Authentication**: Required (owner only)

**Request Body**:
```json
{
  "accountId": "660e8400-e29b-41d4-a716-446655440001",
  "amount": 500.00,
  "description": "Salary deposit"
}
```

**Validation Rules**:
| Field | Rules |
|-------|-------|
| accountId | Required, valid UUID, must belong to user |
| amount | Required, positive number, max 2 decimal places, max 999999999.99 |
| description | Optional, max 255 chars |

**Success Response**: `201 Created`
```json
{
  "data": {
    "transaction": {
      "id": "880e8400-e29b-41d4-a716-446655440003",
      "accountId": "660e8400-e29b-41d4-a716-446655440001",
      "type": "deposit",
      "amount": 500.00,
      "description": "Salary deposit",
      "balanceAfter": 1500.00,
      "createdAt": "2026-01-08T14:30:00Z"
    },
    "newBalance": 1500.00
  },
  "message": "Deposit successful"
}
```

**Error Responses**:

`422 Validation Error`:
```json
{
  "type": "VALIDATION_ERROR",
  "message": "Validation failed",
  "correlationId": "abc-123-def",
  "statusCode": 422,
  "errors": {
    "amount": ["Amount must be greater than 0"]
  }
}
```

---

### 4.4 POST /api/transactions/withdraw

**Description**: Withdraw money from an account

**Authentication**: Required (owner only)

**Request Body**:
```json
{
  "accountId": "660e8400-e29b-41d4-a716-446655440001",
  "amount": 200.00,
  "description": "ATM withdrawal"
}
```

**Validation Rules**:
| Field | Rules |
|-------|-------|
| accountId | Required, valid UUID, must belong to user |
| amount | Required, positive number, max 2 decimal places, max 999999999.99 |
| description | Optional, max 255 chars |

**Success Response**: `201 Created`
```json
{
  "data": {
    "transaction": {
      "id": "bb0e8400-e29b-41d4-a716-446655440006",
      "accountId": "660e8400-e29b-41d4-a716-446655440001",
      "type": "withdrawal",
      "amount": 200.00,
      "description": "ATM withdrawal",
      "balanceAfter": 1300.00,
      "createdAt": "2026-01-08T15:00:00Z"
    },
    "newBalance": 1300.00
  },
  "message": "Withdrawal successful"
}
```

**Error Responses**:

`400 Insufficient Funds`:
```json
{
  "type": "INSUFFICIENT_FUNDS",
  "message": "Insufficient funds. Available: €1300.00, Requested: €2000.00",
  "correlationId": "abc-123-def",
  "statusCode": 400,
  "details": {
    "available": 1300.00,
    "requested": 2000.00
  }
}
```

---

## 5. Transfer Endpoints

### 5.1 GET /api/users/search

**Description**: Search for users by AzureTag (for transfer recipient lookup)

**Authentication**: Required

**Query Parameters**:
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| azureTag | string | Yes | Partial or full AzureTag to search |

**Success Response**: `200 OK`
```json
{
  "data": [
    {
      "azureTag": "janedoe",
      "displayName": "Jane D."
    },
    {
      "azureTag": "janesmith",
      "displayName": "Jane S."
    }
  ]
}
```

**Notes**:
- Returns max 10 results
- Minimum 2 characters to search
- Display name is masked (first name + last initial) for privacy
- Does not return the current user

---

### 5.2 GET /api/users/{azureTag}

**Description**: Get recipient details for transfer confirmation

**Authentication**: Required

**Path Parameters**:
| Parameter | Type | Description |
|-----------|------|-------------|
| azureTag | string | Recipient's AzureTag |

**Success Response**: `200 OK`
```json
{
  "data": {
    "azureTag": "janedoe",
    "displayName": "Jane D.",
    "exists": true
  }
}
```

**Error Responses**:

`404 Not Found`:
```json
{
  "type": "NOT_FOUND",
  "message": "User with AzureTag 'unknownuser' was not found",
  "correlationId": "abc-123-def",
  "statusCode": 404
}
```

---

### 5.3 POST /api/transfers

**Description**: Transfer money to another user's primary account

**Authentication**: Required (sender account owner)

**Request Body**:
```json
{
  "fromAccountId": "660e8400-e29b-41d4-a716-446655440001",
  "recipientAzureTag": "janedoe",
  "amount": 100.00,
  "description": "Dinner payment"
}
```

**Validation Rules**:
| Field | Rules |
|-------|-------|
| fromAccountId | Required, valid UUID, must belong to user |
| recipientAzureTag | Required, 3-20 chars, must exist, cannot be self |
| amount | Required, positive number, max 2 decimal places |
| description | Optional, max 255 chars |

**Success Response**: `201 Created`
```json
{
  "data": {
    "transfer": {
      "id": "cc0e8400-e29b-41d4-a716-446655440007",
      "fromAccountId": "660e8400-e29b-41d4-a716-446655440001",
      "recipientAzureTag": "janedoe",
      "recipientDisplayName": "Jane D.",
      "amount": 100.00,
      "description": "Dinner payment",
      "status": "completed",
      "createdAt": "2026-01-08T16:00:00Z"
    },
    "senderTransaction": {
      "id": "dd0e8400-e29b-41d4-a716-446655440008",
      "type": "transfer_out",
      "amount": 100.00,
      "balanceAfter": 1200.00
    },
    "newBalance": 1200.00
  },
  "message": "Transfer successful"
}
```

**Error Responses**:

`400 Insufficient Funds`:
```json
{
  "type": "INSUFFICIENT_FUNDS",
  "message": "Insufficient funds. Available: €1200.00, Requested: €5000.00",
  "correlationId": "abc-123-def",
  "statusCode": 400,
  "details": {
    "available": 1200.00,
    "requested": 5000.00
  }
}
```

`404 Recipient Not Found`:
```json
{
  "type": "NOT_FOUND",
  "message": "Recipient with AzureTag 'unknownuser' was not found",
  "correlationId": "abc-123-def",
  "statusCode": 404
}
```

`400 Self Transfer`:
```json
{
  "type": "BUSINESS_RULE_VIOLATION",
  "message": "Cannot transfer to yourself. Use internal account transfer instead.",
  "correlationId": "abc-123-def",
  "statusCode": 400
}
```

---

### 5.4 POST /api/transfers/internal

**Description**: Transfer money between own accounts

**Authentication**: Required (owner only)

**Request Body**:
```json
{
  "fromAccountId": "660e8400-e29b-41d4-a716-446655440001",
  "toAccountId": "770e8400-e29b-41d4-a716-446655440002",
  "amount": 500.00,
  "description": "Move to savings"
}
```

**Validation Rules**:
| Field | Rules |
|-------|-------|
| fromAccountId | Required, valid UUID, must belong to user |
| toAccountId | Required, valid UUID, must belong to user, different from fromAccountId |
| amount | Required, positive number, max 2 decimal places |
| description | Optional, max 255 chars |

**Success Response**: `201 Created`
```json
{
  "data": {
    "transfer": {
      "id": "ee0e8400-e29b-41d4-a716-446655440009",
      "fromAccountId": "660e8400-e29b-41d4-a716-446655440001",
      "toAccountId": "770e8400-e29b-41d4-a716-446655440002",
      "amount": 500.00,
      "description": "Move to savings",
      "status": "completed",
      "createdAt": "2026-01-08T17:00:00Z"
    },
    "fromAccountNewBalance": 700.00,
    "toAccountNewBalance": 5500.00
  },
  "message": "Internal transfer successful"
}
```

---

## 6. Error Handling

### 6.1 Standard Error Response Format

All errors follow this structure:

```json
{
  "type": "ERROR_CODE",
  "message": "Human-readable error message",
  "correlationId": "uuid-for-tracking",
  "statusCode": 400,
  "errors": {
    "fieldName": ["Error message 1", "Error message 2"]
  },
  "details": {
    "additionalKey": "additionalValue"
  }
}
```

| Field | Required | Description |
|-------|----------|-------------|
| type | Yes | Machine-readable error code |
| message | Yes | Human-readable message for display |
| correlationId | Yes | UUID for support/debugging |
| statusCode | Yes | HTTP status code |
| errors | No | Field-specific validation errors |
| details | No | Additional context (e.g., available balance) |

### 6.2 Error Types

| Type | HTTP Status | Description |
|------|-------------|-------------|
| VALIDATION_ERROR | 422 | Request validation failed |
| AUTHENTICATION_FAILED | 401 | Invalid credentials |
| UNAUTHORIZED | 401 | Missing or expired token |
| ACCESS_DENIED | 403 | Insufficient permissions |
| NOT_FOUND | 404 | Resource not found |
| CONFLICT | 409 | Resource already exists |
| INSUFFICIENT_FUNDS | 400 | Not enough balance |
| BUSINESS_RULE_VIOLATION | 400 | Business rule violated |
| RATE_LIMIT_EXCEEDED | 429 | Too many requests |
| INTERNAL_SERVER_ERROR | 500 | Unexpected server error |

### 6.3 HTTP Status Code Usage

| Status | When to Use |
|--------|-------------|
| 200 OK | Successful GET, PUT, PATCH, DELETE |
| 201 Created | Successful POST creating a resource |
| 204 No Content | Successful DELETE with no body |
| 400 Bad Request | Business rule violations, insufficient funds |
| 401 Unauthorized | Missing, invalid, or expired token |
| 403 Forbidden | Valid token but no permission |
| 404 Not Found | Resource doesn't exist |
| 409 Conflict | Duplicate resource (email, azureTag) |
| 422 Unprocessable Entity | Validation errors |
| 429 Too Many Requests | Rate limit exceeded |
| 500 Internal Server Error | Unexpected server errors |

---

## 7. DTOs (Data Transfer Objects)

### 7.1 Request DTOs

```csharp
// ============================================
// AUTH DTOs
// ============================================

public record RegisterRequest
{
    public required string Email { get; init; }
    public required string Password { get; init; }
    public required string ConfirmPassword { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required string AzureTag { get; init; }
}

public record LoginRequest
{
    public required string Email { get; init; }
    public required string Password { get; init; }
}

// ============================================
// ACCOUNT DTOs
// ============================================

public record CreateAccountRequest
{
    public required string Name { get; init; }
    public required string Type { get; init; } // checking, savings, investment
}

public record UpdateAccountRequest
{
    public string? Name { get; init; }
}

// ============================================
// TRANSACTION DTOs
// ============================================

public record DepositRequest
{
    public required Guid AccountId { get; init; }
    public required decimal Amount { get; init; }
    public string? Description { get; init; }
}

public record WithdrawRequest
{
    public required Guid AccountId { get; init; }
    public required decimal Amount { get; init; }
    public string? Description { get; init; }
}

public record TransactionFilterRequest
{
    public Guid? AccountId { get; init; }
    public string? Type { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string SortBy { get; init; } = "createdAt";
    public string SortOrder { get; init; } = "desc";
}

// ============================================
// TRANSFER DTOs
// ============================================

public record TransferRequest
{
    public required Guid FromAccountId { get; init; }
    public required string RecipientAzureTag { get; init; }
    public required decimal Amount { get; init; }
    public string? Description { get; init; }
}

public record InternalTransferRequest
{
    public required Guid FromAccountId { get; init; }
    public required Guid ToAccountId { get; init; }
    public required decimal Amount { get; init; }
    public string? Description { get; init; }
}
```

### 7.2 Response DTOs

```csharp
// ============================================
// COMMON DTOs
// ============================================

public record ApiResponse<T>
{
    public T? Data { get; init; }
    public string? Message { get; init; }
}

public record ErrorResponse
{
    public required string Type { get; init; }
    public required string Message { get; init; }
    public required string CorrelationId { get; init; }
    public required int StatusCode { get; init; }
    public Dictionary<string, string[]>? Errors { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}

public record PaginatedResponse<T>
{
    public required List<T> Data { get; init; }
    public required PaginationInfo Pagination { get; init; }
}

public record PaginationInfo
{
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalItems { get; init; }
    public required int TotalPages { get; init; }
    public required bool HasNextPage { get; init; }
    public required bool HasPreviousPage { get; init; }
}

// ============================================
// AUTH DTOs
// ============================================

public record UserDto
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required string AzureTag { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public record TokenDto
{
    public required string AccessToken { get; init; }
    public required int ExpiresIn { get; init; }
    public required string TokenType { get; init; }
}

public record AuthResponse
{
    public required UserDto User { get; init; }
    public required TokenDto Token { get; init; }
}

public record RegisterResponse
{
    public required UserDto User { get; init; }
    public required AccountDto Account { get; init; }
    public required TokenDto Token { get; init; }
}

// ============================================
// ACCOUNT DTOs
// ============================================

public record AccountDto
{
    public required Guid Id { get; init; }
    public required string AccountNumber { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required decimal Balance { get; init; }
    public required bool IsPrimary { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
}

public record BalanceDto
{
    public required Guid AccountId { get; init; }
    public required decimal Balance { get; init; }
    public required string Currency { get; init; }
    public required DateTime AsOf { get; init; }
    public bool IsHistorical { get; init; }
}

// ============================================
// TRANSACTION DTOs
// ============================================

public record TransactionDto
{
    public required Guid Id { get; init; }
    public required Guid AccountId { get; init; }
    public required string Type { get; init; }
    public required decimal Amount { get; init; }
    public string? Description { get; init; }
    public required decimal BalanceAfter { get; init; }
    public Guid? RelatedTransactionId { get; init; }
    public string? RecipientAzureTag { get; init; }
    public string? SenderAzureTag { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public record DepositResponse
{
    public required TransactionDto Transaction { get; init; }
    public required decimal NewBalance { get; init; }
}

public record WithdrawResponse
{
    public required TransactionDto Transaction { get; init; }
    public required decimal NewBalance { get; init; }
}

// ============================================
// TRANSFER DTOs
// ============================================

public record RecipientDto
{
    public required string AzureTag { get; init; }
    public required string DisplayName { get; init; }
}

public record RecipientExistsDto
{
    public required string AzureTag { get; init; }
    public required string DisplayName { get; init; }
    public required bool Exists { get; init; }
}

public record TransferDto
{
    public required Guid Id { get; init; }
    public required Guid FromAccountId { get; init; }
    public required string RecipientAzureTag { get; init; }
    public required string RecipientDisplayName { get; init; }
    public required decimal Amount { get; init; }
    public string? Description { get; init; }
    public required string Status { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public record TransferResponse
{
    public required TransferDto Transfer { get; init; }
    public required TransactionDto SenderTransaction { get; init; }
    public required decimal NewBalance { get; init; }
}

public record InternalTransferResponse
{
    public required TransferDto Transfer { get; init; }
    public required decimal FromAccountNewBalance { get; init; }
    public required decimal ToAccountNewBalance { get; init; }
}
```

---

## 8. Rate Limiting

### 8.1 Rate Limit Configuration

| Endpoint Category | Limit | Window |
|-------------------|-------|--------|
| Auth (login/register) | 5 requests | 1 minute |
| Read operations (GET) | 100 requests | 1 minute |
| Write operations (POST/PUT/PATCH/DELETE) | 30 requests | 1 minute |
| Transfer operations | 10 requests | 1 minute |

### 8.2 Rate Limit Response

**Headers on all responses**:
```
X-RateLimit-Limit: 100
X-RateLimit-Remaining: 95
X-RateLimit-Reset: 1704729600
```

**429 Response**:
```json
{
  "type": "RATE_LIMIT_EXCEEDED",
  "message": "Too many requests. Please retry after 60 seconds.",
  "correlationId": "abc-123-def",
  "statusCode": 429,
  "details": {
    "retryAfter": 60
  }
}
```

**Response Header**:
```
Retry-After: 60
```

---

## 9. API Endpoint Summary

| Method | Endpoint | Auth | Description |
|--------|----------|------|-------------|
| POST | /api/auth/register | No | Register new user |
| POST | /api/auth/login | No | Login |
| POST | /api/auth/logout | Yes | Logout |
| GET | /api/auth/me | Yes | Get current user |
| POST | /api/accounts | Yes | Create account |
| GET | /api/accounts | Yes | List accounts |
| GET | /api/accounts/{id} | Yes | Get account |
| GET | /api/accounts/{id}/balance | Yes | Get balance |
| PATCH | /api/accounts/{id} | Yes | Update account |
| PATCH | /api/accounts/{id}/set-primary | Yes | Set primary |
| DELETE | /api/accounts/{id} | Yes | Delete account |
| GET | /api/transactions | Yes | List transactions |
| GET | /api/transactions/{id} | Yes | Get transaction |
| POST | /api/transactions/deposit | Yes | Deposit |
| POST | /api/transactions/withdraw | Yes | Withdraw |
| GET | /api/users/search | Yes | Search users |
| GET | /api/users/{azureTag} | Yes | Get recipient |
| POST | /api/transfers | Yes | External transfer |
| POST | /api/transfers/internal | Yes | Internal transfer |

**Total Endpoints**: 19

---

## 10. Implementation Checklist

### Phase 4 Tasks
- [x] Define API endpoint inventory
- [x] Create request DTOs
- [x] Create response DTOs
- [x] Define error handling conventions
- [x] Document rate limiting
- [x] Backend Lead <-> Frontend Lead Confrontation (see 13-review-notes.md)
- [ ] Update MSW mock handlers (07-msw-mock-handlers.md)

---

**Document Status**: COMPLETE - Confrontation DONE
**Next Step**: Phase 4.6 - Update MSW Mock Handlers
