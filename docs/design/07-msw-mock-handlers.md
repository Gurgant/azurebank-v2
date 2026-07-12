# MSW Mock Handlers
## AzureBank - Bank Account Management System

**Document Version**: 2.0
**Created**: 2025-12-16
**Updated**: 2026-01-08
**Author**: Frontend Lead
**Status**: COMPLETE - Phase 4

---

## 1. Overview

Mock Service Worker (MSW) handlers for frontend-first development. These handlers simulate both the BFF and Backend API as defined in `06-api-contracts.md`.

### 1.1 BFF Pattern Considerations

> **IMPORTANT**: In production, the frontend communicates with the BFF, which handles session management and proxies API requests. In development with MSW, we simulate this behavior.

**What MSW Must Simulate**:
- BFF authentication endpoints (`/bff/auth/*`)
- Session cookie behavior (HTTP-only simulation)
- Backend API endpoints (`/api/*`)
- Session validation on protected routes

**Key Differences from Production**:
- MSW cannot set true HTTP-only cookies (browser limitation)
- We use a mock session store to track logged-in state
- Session ID is still returned in Set-Cookie header for realism

### 1.2 Key Principles
- All handlers match the API contract exactly
- Mock data is realistic and covers edge cases
- Error scenarios are easily testable
- Session state is persisted in memory during session
- BFF endpoints handle authentication flow
- API endpoints check session validity

---

## 2. Setup Configuration

### 2.1 Browser Setup

```typescript
// src/mocks/browser.ts
import { setupWorker } from 'msw/browser';
import { handlers } from './handlers';

export const worker = setupWorker(...handlers);
```

### 2.2 Main Entry Point Integration

```typescript
// src/main.tsx
async function enableMocking() {
  if (import.meta.env.DEV) {
    const { worker } = await import('./mocks/browser');
    return worker.start({
      onUnhandledRequest: 'bypass', // Don't warn for unhandled requests
    });
  }
  return Promise.resolve();
}

enableMocking().then(() => {
  ReactDOM.createRoot(document.getElementById('root')!).render(
    <React.StrictMode>
      <App />
    </React.StrictMode>
  );
});
```

### 2.3 Handler Registration

```typescript
// src/mocks/handlers/index.ts
import { bffAuthHandlers } from './bff-auth.handlers';  // NEW: BFF auth endpoints
import { authHandlers } from './auth.handlers';          // Backend auth (for BFF internal use)
import { accountHandlers } from './account.handlers';
import { transactionHandlers } from './transaction.handlers';
import { transferHandlers } from './transfer.handlers';
import { userHandlers } from './user.handlers';

export const handlers = [
  ...bffAuthHandlers,     // BFF endpoints MUST come first
  ...authHandlers,
  ...accountHandlers,
  ...transactionHandlers,
  ...transferHandlers,
  ...userHandlers,
];
```

### 2.4 Mock Session Store

```typescript
// src/mocks/data/session.ts

export interface MockSession {
  sessionId: string;
  userId: string;
  accessToken: string;
  authLevel: number;        // 1 = session, 2 = PIN verified
  createdAt: string;
  lastActivity: string;
  pinVerifiedAt?: string;
}

// In-memory session store (simulates server-side session storage)
export const sessionStore = new Map<string, MockSession>();

export function createSession(userId: string, accessToken: string): string {
  const sessionId = `sess_${Date.now()}_${Math.random().toString(36).slice(2)}`;
  const session: MockSession = {
    sessionId,
    userId,
    accessToken,
    authLevel: 1,
    createdAt: new Date().toISOString(),
    lastActivity: new Date().toISOString(),
  };
  sessionStore.set(sessionId, session);
  return sessionId;
}

export function getSession(sessionId: string): MockSession | undefined {
  const session = sessionStore.get(sessionId);
  if (session) {
    session.lastActivity = new Date().toISOString();
  }
  return session;
}

export function deleteSession(sessionId: string): void {
  sessionStore.delete(sessionId);
}

export function verifyPin(sessionId: string): void {
  const session = sessionStore.get(sessionId);
  if (session) {
    session.authLevel = 2;
    session.pinVerifiedAt = new Date().toISOString();
  }
}

// Helper to extract session ID from cookie header
export function getSessionFromCookies(cookieHeader: string | null): MockSession | undefined {
  if (!cookieHeader) return undefined;
  const match = cookieHeader.match(/\.AzureBank\.Session=([^;]+)/);
  if (!match) return undefined;
  return getSession(match[1]);
}
```

---

## 3. Mock Data

### 3.1 Mock Database

```typescript
// src/mocks/data/db.ts
import { v4 as uuidv4 } from 'uuid';

// ============================================
// TYPES
// ============================================

export interface MockUser {
  id: string;
  email: string;
  passwordHash: string; // Store password as-is for mock purposes
  firstName: string;
  lastName: string;
  azureTag: string;
  createdAt: string;
}

export interface MockAccount {
  id: string;
  userId: string;
  accountNumber: string;
  name: string;
  type: 'checking' | 'savings' | 'investment';
  balance: number;
  isPrimary: boolean;
  isDeleted: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface MockTransaction {
  id: string;
  accountId: string;
  type: 'deposit' | 'withdrawal' | 'transfer_in' | 'transfer_out';
  amount: number;
  description: string | null;
  balanceAfter: number;
  relatedTransactionId: string | null;
  recipientAzureTag: string | null;
  senderAzureTag: string | null;
  createdAt: string;
}

// ============================================
// INITIAL DATA
// ============================================

export const mockUsers: MockUser[] = [
  {
    id: '550e8400-e29b-41d4-a716-446655440000',
    email: 'john.doe@example.com',
    passwordHash: 'SecurePass123!',
    firstName: 'John',
    lastName: 'Doe',
    azureTag: 'johndoe',
    createdAt: '2026-01-01T10:00:00Z',
  },
  {
    id: '550e8400-e29b-41d4-a716-446655440001',
    email: 'jane.smith@example.com',
    passwordHash: 'SecurePass123!',
    firstName: 'Jane',
    lastName: 'Smith',
    azureTag: 'janesmith',
    createdAt: '2026-01-02T10:00:00Z',
  },
  {
    id: '550e8400-e29b-41d4-a716-446655440002',
    email: 'bob.wilson@example.com',
    passwordHash: 'SecurePass123!',
    firstName: 'Bob',
    lastName: 'Wilson',
    azureTag: 'bobwilson',
    createdAt: '2026-01-03T10:00:00Z',
  },
];

export const mockAccounts: MockAccount[] = [
  {
    id: '660e8400-e29b-41d4-a716-446655440001',
    userId: '550e8400-e29b-41d4-a716-446655440000',
    accountNumber: 'AB-1234-5678-90',
    name: 'Primary Account',
    type: 'checking',
    balance: 2500.00,
    isPrimary: true,
    isDeleted: false,
    createdAt: '2026-01-01T10:00:00Z',
    updatedAt: '2026-01-08T14:30:00Z',
  },
  {
    id: '660e8400-e29b-41d4-a716-446655440002',
    userId: '550e8400-e29b-41d4-a716-446655440000',
    accountNumber: 'AB-2345-6789-01',
    name: 'Savings Account',
    type: 'savings',
    balance: 10000.00,
    isPrimary: false,
    isDeleted: false,
    createdAt: '2026-01-02T10:00:00Z',
    updatedAt: '2026-01-02T10:00:00Z',
  },
  {
    id: '660e8400-e29b-41d4-a716-446655440003',
    userId: '550e8400-e29b-41d4-a716-446655440001',
    accountNumber: 'AB-3456-7890-12',
    name: 'Main Account',
    type: 'checking',
    balance: 5000.00,
    isPrimary: true,
    isDeleted: false,
    createdAt: '2026-01-02T10:00:00Z',
    updatedAt: '2026-01-02T10:00:00Z',
  },
];

export const mockTransactions: MockTransaction[] = [
  {
    id: '770e8400-e29b-41d4-a716-446655440001',
    accountId: '660e8400-e29b-41d4-a716-446655440001',
    type: 'deposit',
    amount: 1000.00,
    description: 'Initial deposit',
    balanceAfter: 1000.00,
    relatedTransactionId: null,
    recipientAzureTag: null,
    senderAzureTag: null,
    createdAt: '2026-01-01T10:30:00Z',
  },
  {
    id: '770e8400-e29b-41d4-a716-446655440002',
    accountId: '660e8400-e29b-41d4-a716-446655440001',
    type: 'deposit',
    amount: 2000.00,
    description: 'Salary January',
    balanceAfter: 3000.00,
    relatedTransactionId: null,
    recipientAzureTag: null,
    senderAzureTag: null,
    createdAt: '2026-01-05T09:00:00Z',
  },
  {
    id: '770e8400-e29b-41d4-a716-446655440003',
    accountId: '660e8400-e29b-41d4-a716-446655440001',
    type: 'withdrawal',
    amount: 200.00,
    description: 'ATM withdrawal',
    balanceAfter: 2800.00,
    relatedTransactionId: null,
    recipientAzureTag: null,
    senderAzureTag: null,
    createdAt: '2026-01-06T15:00:00Z',
  },
  {
    id: '770e8400-e29b-41d4-a716-446655440004',
    accountId: '660e8400-e29b-41d4-a716-446655440001',
    type: 'transfer_out',
    amount: 300.00,
    description: 'Payment to @janesmith',
    balanceAfter: 2500.00,
    relatedTransactionId: '770e8400-e29b-41d4-a716-446655440005',
    recipientAzureTag: 'janesmith',
    senderAzureTag: null,
    createdAt: '2026-01-07T12:00:00Z',
  },
  {
    id: '770e8400-e29b-41d4-a716-446655440005',
    accountId: '660e8400-e29b-41d4-a716-446655440003',
    type: 'transfer_in',
    amount: 300.00,
    description: 'Payment from @johndoe',
    balanceAfter: 5000.00,
    relatedTransactionId: '770e8400-e29b-41d4-a716-446655440004',
    recipientAzureTag: null,
    senderAzureTag: 'johndoe',
    createdAt: '2026-01-07T12:00:00Z',
  },
];

// ============================================
// HELPERS
// ============================================

export function generateAccountNumber(): string {
  const digits = Array.from({ length: 8 }, () =>
    Math.floor(Math.random() * 10)
  ).join('');
  const formatted = `${digits.slice(0, 4)}-${digits.slice(4)}`;
  const checkDigits = String(Math.floor(Math.random() * 100)).padStart(2, '0');
  return `AB-${formatted}-${checkDigits}`;
}

export function generateToken(userId: string): string {
  // Simple mock token (not a real JWT)
  const payload = btoa(JSON.stringify({ sub: userId, exp: Date.now() + 1800000 }));
  return `mock.${payload}.signature`;
}

export function parseToken(token: string): { sub: string; exp: number } | null {
  try {
    const parts = token.split('.');
    if (parts.length !== 3 || parts[0] !== 'mock') return null;
    return JSON.parse(atob(parts[1]));
  } catch {
    return null;
  }
}

export function generateCorrelationId(): string {
  return uuidv4();
}
```

### 3.2 Mock State Manager

```typescript
// src/mocks/data/state.ts
import {
  mockUsers, mockAccounts, mockTransactions,
  MockUser, MockAccount, MockTransaction
} from './db';

// In-memory state (resets on page refresh)
class MockState {
  users: MockUser[] = [...mockUsers];
  accounts: MockAccount[] = [...mockAccounts];
  transactions: MockTransaction[] = [...mockTransactions];

  // Current authenticated user (set after login)
  currentUserId: string | null = null;

  reset() {
    this.users = [...mockUsers];
    this.accounts = [...mockAccounts];
    this.transactions = [...mockTransactions];
    this.currentUserId = null;
  }

  getUserById(id: string): MockUser | undefined {
    return this.users.find(u => u.id === id);
  }

  getUserByEmail(email: string): MockUser | undefined {
    return this.users.find(u => u.email.toLowerCase() === email.toLowerCase());
  }

  getUserByAzureTag(tag: string): MockUser | undefined {
    return this.users.find(u => u.azureTag.toLowerCase() === tag.toLowerCase());
  }

  getAccountsByUserId(userId: string): MockAccount[] {
    return this.accounts.filter(a => a.userId === userId && !a.isDeleted);
  }

  getAccountById(id: string): MockAccount | undefined {
    return this.accounts.find(a => a.id === id && !a.isDeleted);
  }

  getPrimaryAccount(userId: string): MockAccount | undefined {
    return this.accounts.find(a => a.userId === userId && a.isPrimary && !a.isDeleted);
  }

  getTransactionsByAccountId(accountId: string): MockTransaction[] {
    return this.transactions
      .filter(t => t.accountId === accountId)
      .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());
  }
}

export const state = new MockState();
```

---

## 4. BFF Auth Handlers (NEW)

> These handlers simulate the BFF authentication endpoints.
> They manage session state and do NOT return tokens to the client.

```typescript
// src/mocks/handlers/bff-auth.handlers.ts
import { http, HttpResponse, delay } from 'msw';
import { state } from '../data/state';
import { generateToken, generateCorrelationId } from '../data/db';
import {
  createSession,
  getSessionFromCookies,
  deleteSession,
  verifyPin,
  sessionStore
} from '../data/session';

export const bffAuthHandlers = [
  // ============================================
  // POST /bff/auth/login
  // ============================================
  http.post('/bff/auth/login', async ({ request }) => {
    await delay(300);

    const correlationId = generateCorrelationId();
    const body = await request.json() as { email: string; password: string };

    // Validation
    if (!body.email || !body.password) {
      return HttpResponse.json(
        {
          type: 'VALIDATION_ERROR',
          message: 'Email and password are required',
          correlationId,
          statusCode: 400,
        },
        { status: 400, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    // Find user
    const user = state.getUserByEmail(body.email.toLowerCase());
    if (!user || user.passwordHash !== body.password) {
      return HttpResponse.json(
        {
          type: 'INVALID_CREDENTIALS',
          message: 'Invalid credentials',
          correlationId,
          statusCode: 401,
        },
        { status: 401, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    // Generate token (stored server-side, NOT returned to client)
    const token = generateToken(user.id);

    // Create session
    const sessionId = createSession(user.id, token);

    // Return user info WITHOUT token
    return HttpResponse.json(
      {
        data: {
          user: {
            id: user.id,
            email: user.email,
            firstName: user.firstName,
            lastName: user.lastName,
            azureTag: user.azureTag,
          },
        },
        message: 'Login successful',
      },
      {
        status: 200,
        headers: {
          'X-Correlation-ID': correlationId,
          'Set-Cookie': `.AzureBank.Session=${sessionId}; HttpOnly; Secure; SameSite=Strict; Path=/`,
        },
      }
    );
  }),

  // ============================================
  // POST /bff/auth/logout
  // ============================================
  http.post('/bff/auth/logout', async ({ request }) => {
    await delay(100);

    const correlationId = generateCorrelationId();
    const cookieHeader = request.headers.get('cookie');
    const session = getSessionFromCookies(cookieHeader);

    if (session) {
      deleteSession(session.sessionId);
    }

    return HttpResponse.json(
      {
        data: null,
        message: 'Logged out successfully',
      },
      {
        status: 200,
        headers: {
          'X-Correlation-ID': correlationId,
          'Set-Cookie': '.AzureBank.Session=; HttpOnly; Secure; SameSite=Strict; Path=/; Max-Age=0',
        },
      }
    );
  }),

  // ============================================
  // GET /bff/auth/me
  // ============================================
  http.get('/bff/auth/me', async ({ request }) => {
    await delay(100);

    const correlationId = generateCorrelationId();
    const cookieHeader = request.headers.get('cookie');
    const session = getSessionFromCookies(cookieHeader);

    if (!session) {
      return HttpResponse.json(
        {
          type: 'SESSION_EXPIRED',
          message: 'Session has expired',
          correlationId,
          statusCode: 401,
        },
        { status: 401, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    const user = state.getUserById(session.userId);
    if (!user) {
      return HttpResponse.json(
        {
          type: 'USER_NOT_FOUND',
          message: 'User not found',
          correlationId,
          statusCode: 404,
        },
        { status: 404, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    return HttpResponse.json(
      {
        data: {
          user: {
            id: user.id,
            email: user.email,
            firstName: user.firstName,
            lastName: user.lastName,
            azureTag: user.azureTag,
          },
          session: {
            authLevel: session.authLevel,
            createdAt: session.createdAt,
            lastActivity: session.lastActivity,
          },
        },
      },
      { status: 200, headers: { 'X-Correlation-ID': correlationId } }
    );
  }),

  // ============================================
  // GET /bff/auth/session-status
  // ============================================
  http.get('/bff/auth/session-status', async ({ request }) => {
    await delay(50);

    const correlationId = generateCorrelationId();
    const cookieHeader = request.headers.get('cookie');
    const session = getSessionFromCookies(cookieHeader);

    if (!session) {
      return HttpResponse.json(
        {
          type: 'SESSION_EXPIRED',
          message: 'Session has expired',
          correlationId,
          statusCode: 401,
        },
        { status: 401, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    // Calculate expiry times (mock values for development)
    const sessionCreated = new Date(session.createdAt).getTime();
    const lastActivity = new Date(session.lastActivity).getTime();
    const now = Date.now();

    const inactivityTimeout = 10 * 60 * 1000;  // 10 min for dev
    const absoluteTimeout = 20 * 60 * 1000;    // 20 min for dev

    return HttpResponse.json(
      {
        data: {
          authLevel: session.authLevel,
          inactivityExpiresIn: Math.max(0, Math.floor((lastActivity + inactivityTimeout - now) / 1000)),
          absoluteExpiresIn: Math.max(0, Math.floor((sessionCreated + absoluteTimeout - now) / 1000)),
          pinVerifiedUntil: session.pinVerifiedAt || null,
        },
      },
      { status: 200, headers: { 'X-Correlation-ID': correlationId } }
    );
  }),

  // ============================================
  // POST /bff/auth/verify-pin
  // ============================================
  http.post('/bff/auth/verify-pin', async ({ request }) => {
    await delay(200);

    const correlationId = generateCorrelationId();
    const cookieHeader = request.headers.get('cookie');
    const session = getSessionFromCookies(cookieHeader);

    if (!session) {
      return HttpResponse.json(
        {
          type: 'SESSION_EXPIRED',
          message: 'Session has expired',
          correlationId,
          statusCode: 401,
        },
        { status: 401, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    const body = await request.json() as { pin: string };

    // Mock PIN validation (in real app, would check against user's stored PIN hash)
    // For mock purposes, accept "123456" as valid PIN
    if (body.pin !== '123456') {
      return HttpResponse.json(
        {
          type: 'INVALID_PIN',
          message: 'Invalid PIN',
          correlationId,
          statusCode: 400,
        },
        { status: 400, headers: { 'X-Correlation-ID': correlationId } }
      );
    }

    // Update session auth level
    verifyPin(session.sessionId);

    const expiresAt = new Date(Date.now() + 10 * 60 * 1000).toISOString(); // 10 min for dev

    return HttpResponse.json(
      {
        data: {
          verified: true,
          authLevel: 2,
          expiresAt,
        },
        message: 'PIN verified',
      },
      { status: 200, headers: { 'X-Correlation-ID': correlationId } }
    );
  }),
];
```

---

## 5. Backend Auth Handlers

> These handlers simulate the Backend API authentication endpoints.
> In production, only the BFF calls these - the browser never accesses them directly.

```typescript
// src/mocks/handlers/auth.handlers.ts
import { http, HttpResponse, delay } from 'msw';
import { v4 as uuidv4 } from 'uuid';
import { state } from '../data/state';
import { generateAccountNumber, generateToken, generateCorrelationId, parseToken } from '../data/db';

export const authHandlers = [
  // ============================================
  // POST /api/auth/register
  // ============================================
  http.post('/api/auth/register', async ({ request }) => {
    await delay(300); // Simulate network latency

    const correlationId = generateCorrelationId();
    const body = await request.json() as {
      email: string;
      password: string;
      confirmPassword: string;
      firstName: string;
      lastName: string;
      azureTag: string;
    };

    // Validation
    const errors: Record<string, string[]> = {};

    if (!body.email || !body.email.includes('@')) {
      errors.email = ['Valid email is required'];
    } else if (state.getUserByEmail(body.email)) {
      errors.email = ['Email is already registered'];
    }

    if (!body.password || body.password.length < 8) {
      errors.password = ['Password must be at least 8 characters'];
    }

    if (body.password !== body.confirmPassword) {
      errors.confirmPassword = ['Passwords do not match'];
    }

    if (!body.azureTag || body.azureTag.length < 3) {
      errors.azureTag = ['AzureTag must be at least 3 characters'];
    } else if (state.getUserByAzureTag(body.azureTag)) {
      errors.azureTag = ['AzureTag is already taken'];
    }

    if (Object.keys(errors).length > 0) {
      return HttpResponse.json({
        type: 'VALIDATION_ERROR',
        message: 'Validation failed',
        correlationId,
        statusCode: 422,
        errors,
      }, { status: 422 });
    }

    // Create user
    const userId = uuidv4();
    const now = new Date().toISOString();

    const newUser = {
      id: userId,
      email: body.email,
      passwordHash: body.password,
      firstName: body.firstName,
      lastName: body.lastName,
      azureTag: body.azureTag.toLowerCase(),
      createdAt: now,
    };
    state.users.push(newUser);

    // Create primary account
    const accountId = uuidv4();
    const newAccount = {
      id: accountId,
      userId,
      accountNumber: generateAccountNumber(),
      name: 'Primary Account',
      type: 'checking' as const,
      balance: 0,
      isPrimary: true,
      isDeleted: false,
      createdAt: now,
      updatedAt: now,
    };
    state.accounts.push(newAccount);

    state.currentUserId = userId;

    return HttpResponse.json({
      data: {
        user: {
          id: newUser.id,
          email: newUser.email,
          firstName: newUser.firstName,
          lastName: newUser.lastName,
          azureTag: newUser.azureTag,
          createdAt: newUser.createdAt,
        },
        account: {
          id: newAccount.id,
          accountNumber: newAccount.accountNumber,
          name: newAccount.name,
          type: newAccount.type,
          balance: newAccount.balance,
          isPrimary: newAccount.isPrimary,
          createdAt: newAccount.createdAt,
        },
        token: {
          accessToken: generateToken(userId),
          expiresIn: 1800,
          tokenType: 'Bearer',
        },
      },
      message: 'Registration successful',
    }, { status: 201 });
  }),

  // ============================================
  // POST /api/auth/login
  // ============================================
  http.post('/api/auth/login', async ({ request }) => {
    await delay(300);

    const correlationId = generateCorrelationId();
    const body = await request.json() as {
      email: string;
      password: string;
    };

    const user = state.getUserByEmail(body.email);

    if (!user || user.passwordHash !== body.password) {
      return HttpResponse.json({
        type: 'AUTHENTICATION_FAILED',
        message: 'Invalid email or password',
        correlationId,
        statusCode: 401,
      }, { status: 401 });
    }

    state.currentUserId = user.id;

    return HttpResponse.json({
      data: {
        user: {
          id: user.id,
          email: user.email,
          firstName: user.firstName,
          lastName: user.lastName,
          azureTag: user.azureTag,
          createdAt: user.createdAt,
        },
        token: {
          accessToken: generateToken(user.id),
          expiresIn: 1800,
          tokenType: 'Bearer',
        },
      },
      message: 'Login successful',
    });
  }),

  // ============================================
  // POST /api/auth/logout
  // ============================================
  http.post('/api/auth/logout', async () => {
    await delay(100);
    state.currentUserId = null;
    return HttpResponse.json({ message: 'Logout successful' });
  }),

  // ============================================
  // GET /api/auth/me
  // ============================================
  http.get('/api/auth/me', async ({ request }) => {
    await delay(100);

    const correlationId = generateCorrelationId();
    const authHeader = request.headers.get('Authorization');

    if (!authHeader?.startsWith('Bearer ')) {
      return HttpResponse.json({
        type: 'UNAUTHORIZED',
        message: 'Missing or invalid token',
        correlationId,
        statusCode: 401,
      }, { status: 401 });
    }

    const token = authHeader.substring(7);
    const payload = parseToken(token);

    if (!payload || payload.exp < Date.now()) {
      return HttpResponse.json({
        type: 'UNAUTHORIZED',
        message: 'Token expired',
        correlationId,
        statusCode: 401,
      }, { status: 401 });
    }

    const user = state.getUserById(payload.sub);
    if (!user) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: 'User not found',
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    return HttpResponse.json({
      data: {
        id: user.id,
        email: user.email,
        firstName: user.firstName,
        lastName: user.lastName,
        azureTag: user.azureTag,
        createdAt: user.createdAt,
      },
    });
  }),
];
```

---

## 5. Account Handlers

```typescript
// src/mocks/handlers/account.handlers.ts
import { http, HttpResponse, delay } from 'msw';
import { v4 as uuidv4 } from 'uuid';
import { state } from '../data/state';
import { generateAccountNumber, generateCorrelationId, parseToken } from '../data/db';

// Helper to verify auth and get user
function verifyAuth(request: Request): { userId: string } | HttpResponse {
  const correlationId = generateCorrelationId();
  const authHeader = request.headers.get('Authorization');

  if (!authHeader?.startsWith('Bearer ')) {
    return HttpResponse.json({
      type: 'UNAUTHORIZED',
      message: 'Missing or invalid token',
      correlationId,
      statusCode: 401,
    }, { status: 401 });
  }

  const token = authHeader.substring(7);
  const payload = parseToken(token);

  if (!payload || payload.exp < Date.now()) {
    return HttpResponse.json({
      type: 'UNAUTHORIZED',
      message: 'Token expired',
      correlationId,
      statusCode: 401,
    }, { status: 401 });
  }

  return { userId: payload.sub };
}

export const accountHandlers = [
  // ============================================
  // GET /api/accounts
  // ============================================
  http.get('/api/accounts', async ({ request }) => {
    await delay(200);

    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const accounts = state.getAccountsByUserId(auth.userId);

    return HttpResponse.json({
      data: accounts.map(a => ({
        id: a.id,
        accountNumber: a.accountNumber,
        name: a.name,
        type: a.type,
        balance: a.balance,
        isPrimary: a.isPrimary,
        createdAt: a.createdAt,
        updatedAt: a.updatedAt,
      })),
    });
  }),

  // ============================================
  // GET /api/accounts/:id
  // ============================================
  http.get('/api/accounts/:id', async ({ request, params }) => {
    await delay(150);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const account = state.getAccountById(params.id as string);

    if (!account) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: `Account with identifier '${params.id}' was not found`,
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    if (account.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'ACCESS_DENIED',
        message: 'You do not have access to this account',
        correlationId,
        statusCode: 403,
      }, { status: 403 });
    }

    return HttpResponse.json({
      data: {
        id: account.id,
        accountNumber: account.accountNumber,
        name: account.name,
        type: account.type,
        balance: account.balance,
        isPrimary: account.isPrimary,
        createdAt: account.createdAt,
        updatedAt: account.updatedAt,
      },
    });
  }),

  // ============================================
  // GET /api/accounts/:id/balance
  // ============================================
  http.get('/api/accounts/:id/balance', async ({ request, params }) => {
    await delay(100);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const account = state.getAccountById(params.id as string);

    if (!account || account.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: `Account not found`,
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    const url = new URL(request.url);
    const at = url.searchParams.get('at');

    if (at) {
      // Historical balance (simplified - just return current for mock)
      return HttpResponse.json({
        data: {
          accountId: account.id,
          balance: account.balance,
          currency: 'EUR',
          asOf: at,
          isHistorical: true,
        },
      });
    }

    return HttpResponse.json({
      data: {
        accountId: account.id,
        balance: account.balance,
        currency: 'EUR',
        asOf: new Date().toISOString(),
      },
    });
  }),

  // ============================================
  // POST /api/accounts
  // ============================================
  http.post('/api/accounts', async ({ request }) => {
    await delay(300);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const body = await request.json() as {
      name: string;
      type: 'checking' | 'savings' | 'investment';
    };

    // Validation
    if (!body.name || body.name.length < 1) {
      return HttpResponse.json({
        type: 'VALIDATION_ERROR',
        message: 'Validation failed',
        correlationId,
        statusCode: 422,
        errors: { name: ['Name is required'] },
      }, { status: 422 });
    }

    const now = new Date().toISOString();
    const newAccount = {
      id: uuidv4(),
      userId: auth.userId,
      accountNumber: generateAccountNumber(),
      name: body.name,
      type: body.type || 'checking',
      balance: 0,
      isPrimary: false,
      isDeleted: false,
      createdAt: now,
      updatedAt: now,
    };
    state.accounts.push(newAccount);

    return HttpResponse.json({
      data: {
        id: newAccount.id,
        accountNumber: newAccount.accountNumber,
        name: newAccount.name,
        type: newAccount.type,
        balance: newAccount.balance,
        isPrimary: newAccount.isPrimary,
        createdAt: newAccount.createdAt,
      },
      message: 'Account created successfully',
    }, { status: 201 });
  }),

  // ============================================
  // PATCH /api/accounts/:id
  // ============================================
  http.patch('/api/accounts/:id', async ({ request, params }) => {
    await delay(200);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const account = state.getAccountById(params.id as string);

    if (!account || account.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: `Account not found`,
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    const body = await request.json() as { name?: string };

    if (body.name) {
      account.name = body.name;
      account.updatedAt = new Date().toISOString();
    }

    return HttpResponse.json({
      data: {
        id: account.id,
        accountNumber: account.accountNumber,
        name: account.name,
        type: account.type,
        balance: account.balance,
        isPrimary: account.isPrimary,
        createdAt: account.createdAt,
        updatedAt: account.updatedAt,
      },
      message: 'Account updated successfully',
    });
  }),

  // ============================================
  // PATCH /api/accounts/:id/set-primary
  // ============================================
  http.patch('/api/accounts/:id/set-primary', async ({ request, params }) => {
    await delay(200);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const account = state.getAccountById(params.id as string);

    if (!account || account.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: `Account not found`,
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    // Unset current primary
    const currentPrimary = state.getPrimaryAccount(auth.userId);
    if (currentPrimary) {
      currentPrimary.isPrimary = false;
      currentPrimary.updatedAt = new Date().toISOString();
    }

    // Set new primary
    account.isPrimary = true;
    account.updatedAt = new Date().toISOString();

    return HttpResponse.json({
      data: {
        id: account.id,
        accountNumber: account.accountNumber,
        name: account.name,
        type: account.type,
        balance: account.balance,
        isPrimary: account.isPrimary,
        createdAt: account.createdAt,
        updatedAt: account.updatedAt,
      },
      message: 'Account set as primary',
    });
  }),

  // ============================================
  // DELETE /api/accounts/:id
  // ============================================
  http.delete('/api/accounts/:id', async ({ request, params }) => {
    await delay(200);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const account = state.getAccountById(params.id as string);

    if (!account || account.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: `Account not found`,
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    if (account.balance !== 0) {
      return HttpResponse.json({
        type: 'BUSINESS_RULE_VIOLATION',
        message: `Cannot delete account with non-zero balance. Current balance: €${account.balance.toFixed(2)}`,
        correlationId,
        statusCode: 400,
      }, { status: 400 });
    }

    if (account.isPrimary) {
      return HttpResponse.json({
        type: 'BUSINESS_RULE_VIOLATION',
        message: 'Cannot delete primary account. Set another account as primary first.',
        correlationId,
        statusCode: 400,
      }, { status: 400 });
    }

    account.isDeleted = true;

    return HttpResponse.json({ message: 'Account deleted successfully' });
  }),
];
```

---

## 6. Transaction Handlers

```typescript
// src/mocks/handlers/transaction.handlers.ts
import { http, HttpResponse, delay } from 'msw';
import { v4 as uuidv4 } from 'uuid';
import { state } from '../data/state';
import { generateCorrelationId, parseToken } from '../data/db';

function verifyAuth(request: Request): { userId: string } | HttpResponse {
  const correlationId = generateCorrelationId();
  const authHeader = request.headers.get('Authorization');

  if (!authHeader?.startsWith('Bearer ')) {
    return HttpResponse.json({
      type: 'UNAUTHORIZED',
      message: 'Missing or invalid token',
      correlationId,
      statusCode: 401,
    }, { status: 401 });
  }

  const token = authHeader.substring(7);
  const payload = parseToken(token);

  if (!payload || payload.exp < Date.now()) {
    return HttpResponse.json({
      type: 'UNAUTHORIZED',
      message: 'Token expired',
      correlationId,
      statusCode: 401,
    }, { status: 401 });
  }

  return { userId: payload.sub };
}

export const transactionHandlers = [
  // ============================================
  // GET /api/transactions
  // ============================================
  http.get('/api/transactions', async ({ request }) => {
    await delay(200);

    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const url = new URL(request.url);
    const accountId = url.searchParams.get('accountId');
    const type = url.searchParams.get('type');
    const page = parseInt(url.searchParams.get('page') || '1', 10);
    const pageSize = Math.min(parseInt(url.searchParams.get('pageSize') || '20', 10), 100);

    // Get user's accounts
    const userAccounts = state.getAccountsByUserId(auth.userId);
    const userAccountIds = new Set(userAccounts.map(a => a.id));

    // Get transactions
    let transactions = state.transactions
      .filter(t => userAccountIds.has(t.accountId))
      .sort((a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime());

    // Apply filters
    if (accountId) {
      transactions = transactions.filter(t => t.accountId === accountId);
    }
    if (type) {
      transactions = transactions.filter(t => t.type === type);
    }

    // Pagination
    const totalItems = transactions.length;
    const totalPages = Math.ceil(totalItems / pageSize);
    const start = (page - 1) * pageSize;
    const paginatedTransactions = transactions.slice(start, start + pageSize);

    return HttpResponse.json({
      data: paginatedTransactions.map(t => ({
        id: t.id,
        accountId: t.accountId,
        type: t.type,
        amount: t.amount,
        description: t.description,
        balanceAfter: t.balanceAfter,
        relatedTransactionId: t.relatedTransactionId,
        recipientAzureTag: t.recipientAzureTag,
        senderAzureTag: t.senderAzureTag,
        createdAt: t.createdAt,
      })),
      pagination: {
        page,
        pageSize,
        totalItems,
        totalPages,
        hasNextPage: page < totalPages,
        hasPreviousPage: page > 1,
      },
    });
  }),

  // ============================================
  // GET /api/transactions/:id
  // ============================================
  http.get('/api/transactions/:id', async ({ request, params }) => {
    await delay(100);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const transaction = state.transactions.find(t => t.id === params.id);

    if (!transaction) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: `Transaction not found`,
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    const account = state.getAccountById(transaction.accountId);
    if (!account || account.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'ACCESS_DENIED',
        message: 'Access denied',
        correlationId,
        statusCode: 403,
      }, { status: 403 });
    }

    return HttpResponse.json({
      data: {
        id: transaction.id,
        accountId: transaction.accountId,
        type: transaction.type,
        amount: transaction.amount,
        description: transaction.description,
        balanceAfter: transaction.balanceAfter,
        relatedTransactionId: transaction.relatedTransactionId,
        recipientAzureTag: transaction.recipientAzureTag,
        senderAzureTag: transaction.senderAzureTag,
        createdAt: transaction.createdAt,
      },
    });
  }),

  // ============================================
  // POST /api/transactions/deposit
  // ============================================
  http.post('/api/transactions/deposit', async ({ request }) => {
    await delay(300);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const body = await request.json() as {
      accountId: string;
      amount: number;
      description?: string;
    };

    // Validation
    if (!body.amount || body.amount <= 0) {
      return HttpResponse.json({
        type: 'VALIDATION_ERROR',
        message: 'Validation failed',
        correlationId,
        statusCode: 422,
        errors: { amount: ['Amount must be greater than 0'] },
      }, { status: 422 });
    }

    const account = state.getAccountById(body.accountId);
    if (!account || account.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: 'Account not found',
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    // Update balance
    account.balance += body.amount;
    account.updatedAt = new Date().toISOString();

    // Create transaction
    const transaction = {
      id: uuidv4(),
      accountId: account.id,
      type: 'deposit' as const,
      amount: body.amount,
      description: body.description || null,
      balanceAfter: account.balance,
      relatedTransactionId: null,
      recipientAzureTag: null,
      senderAzureTag: null,
      createdAt: new Date().toISOString(),
    };
    state.transactions.push(transaction);

    return HttpResponse.json({
      data: {
        transaction: {
          id: transaction.id,
          accountId: transaction.accountId,
          type: transaction.type,
          amount: transaction.amount,
          description: transaction.description,
          balanceAfter: transaction.balanceAfter,
          createdAt: transaction.createdAt,
        },
        newBalance: account.balance,
      },
      message: 'Deposit successful',
    }, { status: 201 });
  }),

  // ============================================
  // POST /api/transactions/withdraw
  // ============================================
  http.post('/api/transactions/withdraw', async ({ request }) => {
    await delay(300);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const body = await request.json() as {
      accountId: string;
      amount: number;
      description?: string;
    };

    // Validation
    if (!body.amount || body.amount <= 0) {
      return HttpResponse.json({
        type: 'VALIDATION_ERROR',
        message: 'Validation failed',
        correlationId,
        statusCode: 422,
        errors: { amount: ['Amount must be greater than 0'] },
      }, { status: 422 });
    }

    const account = state.getAccountById(body.accountId);
    if (!account || account.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: 'Account not found',
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    // Check balance
    if (account.balance < body.amount) {
      return HttpResponse.json({
        type: 'INSUFFICIENT_FUNDS',
        message: `Insufficient funds. Available: €${account.balance.toFixed(2)}, Requested: €${body.amount.toFixed(2)}`,
        correlationId,
        statusCode: 400,
        details: {
          available: account.balance,
          requested: body.amount,
        },
      }, { status: 400 });
    }

    // Update balance
    account.balance -= body.amount;
    account.updatedAt = new Date().toISOString();

    // Create transaction
    const transaction = {
      id: uuidv4(),
      accountId: account.id,
      type: 'withdrawal' as const,
      amount: body.amount,
      description: body.description || null,
      balanceAfter: account.balance,
      relatedTransactionId: null,
      recipientAzureTag: null,
      senderAzureTag: null,
      createdAt: new Date().toISOString(),
    };
    state.transactions.push(transaction);

    return HttpResponse.json({
      data: {
        transaction: {
          id: transaction.id,
          accountId: transaction.accountId,
          type: transaction.type,
          amount: transaction.amount,
          description: transaction.description,
          balanceAfter: transaction.balanceAfter,
          createdAt: transaction.createdAt,
        },
        newBalance: account.balance,
      },
      message: 'Withdrawal successful',
    }, { status: 201 });
  }),
];
```

---

## 7. Transfer Handlers

```typescript
// src/mocks/handlers/transfer.handlers.ts
import { http, HttpResponse, delay } from 'msw';
import { v4 as uuidv4 } from 'uuid';
import { state } from '../data/state';
import { generateCorrelationId, parseToken } from '../data/db';

function verifyAuth(request: Request): { userId: string } | HttpResponse {
  const correlationId = generateCorrelationId();
  const authHeader = request.headers.get('Authorization');

  if (!authHeader?.startsWith('Bearer ')) {
    return HttpResponse.json({
      type: 'UNAUTHORIZED',
      message: 'Missing or invalid token',
      correlationId,
      statusCode: 401,
    }, { status: 401 });
  }

  const token = authHeader.substring(7);
  const payload = parseToken(token);

  if (!payload || payload.exp < Date.now()) {
    return HttpResponse.json({
      type: 'UNAUTHORIZED',
      message: 'Token expired',
      correlationId,
      statusCode: 401,
    }, { status: 401 });
  }

  return { userId: payload.sub };
}

export const transferHandlers = [
  // ============================================
  // POST /api/transfers (External)
  // ============================================
  http.post('/api/transfers', async ({ request }) => {
    await delay(400);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const body = await request.json() as {
      fromAccountId: string;
      recipientAzureTag: string;
      amount: number;
      description?: string;
    };

    // Find sender account
    const senderAccount = state.getAccountById(body.fromAccountId);
    if (!senderAccount || senderAccount.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: 'Source account not found',
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    // Find recipient
    const recipient = state.getUserByAzureTag(body.recipientAzureTag);
    if (!recipient) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: `Recipient with AzureTag '${body.recipientAzureTag}' was not found`,
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    // Check self-transfer
    if (recipient.id === auth.userId) {
      return HttpResponse.json({
        type: 'BUSINESS_RULE_VIOLATION',
        message: 'Cannot transfer to yourself. Use internal account transfer instead.',
        correlationId,
        statusCode: 400,
      }, { status: 400 });
    }

    // Find recipient primary account
    const recipientAccount = state.getPrimaryAccount(recipient.id);
    if (!recipientAccount) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: 'Recipient has no primary account',
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    // Check balance
    if (senderAccount.balance < body.amount) {
      return HttpResponse.json({
        type: 'INSUFFICIENT_FUNDS',
        message: `Insufficient funds. Available: €${senderAccount.balance.toFixed(2)}, Requested: €${body.amount.toFixed(2)}`,
        correlationId,
        statusCode: 400,
        details: {
          available: senderAccount.balance,
          requested: body.amount,
        },
      }, { status: 400 });
    }

    const now = new Date().toISOString();
    const senderUser = state.getUserById(auth.userId)!;

    // Update balances
    senderAccount.balance -= body.amount;
    senderAccount.updatedAt = now;
    recipientAccount.balance += body.amount;
    recipientAccount.updatedAt = now;

    // Create linked transactions
    const senderTxId = uuidv4();
    const recipientTxId = uuidv4();

    const senderTx = {
      id: senderTxId,
      accountId: senderAccount.id,
      type: 'transfer_out' as const,
      amount: body.amount,
      description: body.description || `Payment to @${recipient.azureTag}`,
      balanceAfter: senderAccount.balance,
      relatedTransactionId: recipientTxId,
      recipientAzureTag: recipient.azureTag,
      senderAzureTag: null,
      createdAt: now,
    };

    const recipientTx = {
      id: recipientTxId,
      accountId: recipientAccount.id,
      type: 'transfer_in' as const,
      amount: body.amount,
      description: body.description || `Payment from @${senderUser.azureTag}`,
      balanceAfter: recipientAccount.balance,
      relatedTransactionId: senderTxId,
      recipientAzureTag: null,
      senderAzureTag: senderUser.azureTag,
      createdAt: now,
    };

    state.transactions.push(senderTx, recipientTx);

    return HttpResponse.json({
      data: {
        transfer: {
          id: uuidv4(),
          fromAccountId: senderAccount.id,
          recipientAzureTag: recipient.azureTag,
          recipientDisplayName: `${recipient.firstName} ${recipient.lastName[0]}.`,
          amount: body.amount,
          description: body.description || null,
          status: 'completed',
          createdAt: now,
        },
        senderTransaction: {
          id: senderTx.id,
          type: senderTx.type,
          amount: senderTx.amount,
          balanceAfter: senderTx.balanceAfter,
        },
        newBalance: senderAccount.balance,
      },
      message: 'Transfer successful',
    }, { status: 201 });
  }),

  // ============================================
  // POST /api/transfers/internal
  // ============================================
  http.post('/api/transfers/internal', async ({ request }) => {
    await delay(300);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const body = await request.json() as {
      fromAccountId: string;
      toAccountId: string;
      amount: number;
      description?: string;
    };

    // Find accounts
    const fromAccount = state.getAccountById(body.fromAccountId);
    const toAccount = state.getAccountById(body.toAccountId);

    if (!fromAccount || fromAccount.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: 'Source account not found',
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    if (!toAccount || toAccount.userId !== auth.userId) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: 'Destination account not found',
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    if (fromAccount.id === toAccount.id) {
      return HttpResponse.json({
        type: 'BUSINESS_RULE_VIOLATION',
        message: 'Cannot transfer to the same account',
        correlationId,
        statusCode: 400,
      }, { status: 400 });
    }

    // Check balance
    if (fromAccount.balance < body.amount) {
      return HttpResponse.json({
        type: 'INSUFFICIENT_FUNDS',
        message: `Insufficient funds. Available: €${fromAccount.balance.toFixed(2)}, Requested: €${body.amount.toFixed(2)}`,
        correlationId,
        statusCode: 400,
        details: {
          available: fromAccount.balance,
          requested: body.amount,
        },
      }, { status: 400 });
    }

    const now = new Date().toISOString();

    // Update balances
    fromAccount.balance -= body.amount;
    fromAccount.updatedAt = now;
    toAccount.balance += body.amount;
    toAccount.updatedAt = now;

    // Create linked transactions
    const fromTxId = uuidv4();
    const toTxId = uuidv4();

    const fromTx = {
      id: fromTxId,
      accountId: fromAccount.id,
      type: 'transfer_out' as const,
      amount: body.amount,
      description: body.description || `Transfer to ${toAccount.name}`,
      balanceAfter: fromAccount.balance,
      relatedTransactionId: toTxId,
      recipientAzureTag: null,
      senderAzureTag: null,
      createdAt: now,
    };

    const toTx = {
      id: toTxId,
      accountId: toAccount.id,
      type: 'transfer_in' as const,
      amount: body.amount,
      description: body.description || `Transfer from ${fromAccount.name}`,
      balanceAfter: toAccount.balance,
      relatedTransactionId: fromTxId,
      recipientAzureTag: null,
      senderAzureTag: null,
      createdAt: now,
    };

    state.transactions.push(fromTx, toTx);

    return HttpResponse.json({
      data: {
        transfer: {
          id: uuidv4(),
          fromAccountId: fromAccount.id,
          toAccountId: toAccount.id,
          amount: body.amount,
          description: body.description || null,
          status: 'completed',
          createdAt: now,
        },
        fromAccountNewBalance: fromAccount.balance,
        toAccountNewBalance: toAccount.balance,
      },
      message: 'Internal transfer successful',
    }, { status: 201 });
  }),
];
```

---

## 8. User Search Handlers

```typescript
// src/mocks/handlers/user.handlers.ts
import { http, HttpResponse, delay } from 'msw';
import { state } from '../data/state';
import { generateCorrelationId, parseToken } from '../data/db';

function verifyAuth(request: Request): { userId: string } | HttpResponse {
  const correlationId = generateCorrelationId();
  const authHeader = request.headers.get('Authorization');

  if (!authHeader?.startsWith('Bearer ')) {
    return HttpResponse.json({
      type: 'UNAUTHORIZED',
      message: 'Missing or invalid token',
      correlationId,
      statusCode: 401,
    }, { status: 401 });
  }

  const token = authHeader.substring(7);
  const payload = parseToken(token);

  if (!payload || payload.exp < Date.now()) {
    return HttpResponse.json({
      type: 'UNAUTHORIZED',
      message: 'Token expired',
      correlationId,
      statusCode: 401,
    }, { status: 401 });
  }

  return { userId: payload.sub };
}

export const userHandlers = [
  // ============================================
  // GET /api/users/search
  // ============================================
  http.get('/api/users/search', async ({ request }) => {
    await delay(150);

    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const url = new URL(request.url);
    const query = url.searchParams.get('azureTag')?.toLowerCase() || '';

    if (query.length < 2) {
      return HttpResponse.json({ data: [] });
    }

    const results = state.users
      .filter(u =>
        u.id !== auth.userId && // Exclude self
        u.azureTag.toLowerCase().includes(query)
      )
      .slice(0, 10)
      .map(u => ({
        azureTag: u.azureTag,
        displayName: `${u.firstName} ${u.lastName[0]}.`,
      }));

    return HttpResponse.json({ data: results });
  }),

  // ============================================
  // GET /api/users/:azureTag
  // ============================================
  http.get('/api/users/:azureTag', async ({ request, params }) => {
    await delay(100);

    const correlationId = generateCorrelationId();
    const auth = verifyAuth(request);
    if (auth instanceof HttpResponse) return auth;

    const user = state.getUserByAzureTag(params.azureTag as string);

    if (!user) {
      return HttpResponse.json({
        type: 'NOT_FOUND',
        message: `User with AzureTag '${params.azureTag}' was not found`,
        correlationId,
        statusCode: 404,
      }, { status: 404 });
    }

    return HttpResponse.json({
      data: {
        azureTag: user.azureTag,
        displayName: `${user.firstName} ${user.lastName[0]}.`,
        exists: true,
      },
    });
  }),
];
```

---

## 9. Error Simulation Helpers

```typescript
// src/mocks/utils/errorSimulation.ts

// Toggle these flags to simulate different error scenarios
export const errorSimulation = {
  networkError: false,
  serverError: false,
  rateLimitError: false,
  slowResponse: false,
  slowResponseMs: 3000,
};

// Add to handler if needed:
// if (errorSimulation.networkError) {
//   return HttpResponse.error();
// }
// if (errorSimulation.serverError) {
//   return HttpResponse.json({ type: 'INTERNAL_SERVER_ERROR', ... }, { status: 500 });
// }
```

---

## 10. Test Credentials

For development and testing:

| Email | Password | AzureTag |
|-------|----------|----------|
| john.doe@example.com | SecurePass123! | johndoe |
| jane.smith@example.com | SecurePass123! | janesmith |
| bob.wilson@example.com | SecurePass123! | bobwilson |

---

## 11. Implementation Checklist

- [x] Browser setup (browser.ts)
- [x] Handler registration (handlers/index.ts)
- [x] Mock database (data/db.ts)
- [x] Mock state manager (data/state.ts)
- [x] Auth handlers (auth.handlers.ts)
- [x] Account handlers (account.handlers.ts)
- [x] Transaction handlers (transaction.handlers.ts)
- [x] Transfer handlers (transfer.handlers.ts)
- [x] User search handlers (user.handlers.ts)
- [x] Error simulation utilities

---

**Document Status**: COMPLETE - Phase 4
**Next Step**: Implement in frontend codebase
