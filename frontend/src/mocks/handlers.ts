import { http, HttpResponse } from 'msw';
import { createOpenApiHttp } from 'openapi-msw';
import type { paths } from '../api/schema';
import type { AccountType } from '../api/enums';
import { problem } from './problem';
import { MOCK_PASSWORD, MOCK_USER, mockState, type MockSessionUser } from './state';

/**
 * Contract-faithful MSW handlers. Success bodies go through openapi-msw's TYPED response
 * helper — they must compile against the generated schema.d.ts, which is exactly the
 * enforcement this scaffolding exists for. Error bodies use the shared problem() builder.
 * Edge-case semantics are derived from the BACKEND SOURCE, not intuition:
 *  - idempotency fingerprints the RAW body bytes (IdempotencyMiddleware hashes bytes);
 *  - replay returns the stored bytes verbatim + `Idempotency-Replayed: true`;
 *  - the step-up 403 body is AuthLevelMiddleware's bare shape, NOT ProblemDetails;
 *  - a wrong PIN is HTTP 200 with verified:false (BffAuthController), not a 4xx.
 */
const api = createOpenApiHttp<paths>({ baseUrl: '*' });

const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const NIL_UUID = '00000000-0000-0000-0000-000000000000';

/** FNV-1a over the raw body string — a cheap stand-in for the backend's byte HMAC. */
function fingerprint(raw: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < raw.length; i++) {
    hash ^= raw.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16);
}

/** GET /api/accounts — the session user's accounts, primary first like the real query. */
const listAccounts = api.get('/api/accounts', ({ response }) => {
  const ordered = [...mockState.accounts].sort(
    (a, b) => Number(b.isPrimary) - Number(a.isPrimary) || a.createdAt.localeCompare(b.createdAt),
  );
  return response(200).json({ data: ordered, message: null });
});

/**
 * POST /api/accounts — create (A4): server assigns id/number; the number arrives MASKED
 * like the real mapper's output. NEVER primary: AccountService hard-codes
 * IsPrimary = false on create — the primary exists only via registration's auto-created
 * account or the separate set-primary operation.
 */
const createAccount = api.post('/api/accounts', async ({ request, response }) => {
  const body = (await request.clone().json()) as { name?: string; type?: string };
  const index = mockState.accounts.length;
  const account = {
    id: `019f7b3f-0000-7000-8000-00000000c${String(index).padStart(3, '0')}`,
    accountNumber: `AB-****-****-${70 + index}`,
    name: body.name ?? 'New Account',
    type: (body.type ?? 'Checking') as AccountType,
    balance: 0,
    isPrimary: false,
    createdAt: '2026-07-21T12:00:00.0000000Z',
  };
  mockState.accounts.push(account);
  return response(201).json({ data: account, message: 'Account created successfully.' });
});

/** PATCH /api/accounts/{id} — rename (A5): name only, per the contract. */
const renameAccount = api.patch('/api/accounts/{id}', async ({ params, request, response }) => {
  const account = mockState.accounts.find((a) => a.id === params.id);
  if (!account) {
    return response.untyped(
      problem({
        status: 404,
        errorCode: 'NOT_FOUND',
        detail: `Account with identifier '${params.id}' was not found.`,
      }),
    );
  }
  const body = (await request.clone().json()) as { name?: string };
  account.name = body.name ?? account.name;
  return response(200).json({ data: account, message: 'Account updated successfully' });
});

/** PATCH /api/accounts/{id}/set-primary — exactly one primary at a time (A6). */
const setPrimaryAccount = api.patch('/api/accounts/{id}/set-primary', ({ params, response }) => {
  const account = mockState.accounts.find((a) => a.id === params.id);
  if (!account) {
    return response.untyped(
      problem({
        status: 404,
        errorCode: 'NOT_FOUND',
        detail: `Account with identifier '${params.id}' was not found.`,
      }),
    );
  }
  for (const a of mockState.accounts) {
    a.isPrimary = false;
  }
  account.isPrimary = true;
  return response(200).json({ message: 'Account set as primary' });
});

/**
 * DELETE /api/accounts/{id} — the REAL business rules (AccountService): a 422
 * BusinessRuleException for non-zero balance or primary, else soft delete.
 */
const deleteAccount = api.delete('/api/accounts/{id}', ({ params, response }) => {
  const account = mockState.accounts.find((a) => a.id === params.id);
  if (!account) {
    return response.untyped(
      problem({
        status: 404,
        errorCode: 'NOT_FOUND',
        detail: `Account with identifier '${params.id}' was not found.`,
      }),
    );
  }
  if (account.balance !== 0) {
    return response.untyped(
      problem({
        status: 422,
        errorCode: 'NON_ZERO_BALANCE',
        detail: 'Cannot delete account with non-zero balance.',
      }),
    );
  }
  if (account.isPrimary) {
    return response.untyped(
      problem({
        status: 422,
        errorCode: 'PRIMARY_ACCOUNT_DELETE',
        detail: 'Cannot delete primary account. Set another account as primary first.',
      }),
    );
  }
  mockState.accounts = mockState.accounts.filter((a) => a.id !== params.id);
  return response(200).json({ message: 'Account deleted successfully' });
});

/**
 * GET /api/transactions — T1, one of the two BARE responses (no envelope, by
 * contract): a PaginatedResponse with real page math, newest first.
 */
const listTransactions = api.get('/api/transactions', ({ request, response }) => {
  const params = new URL(request.url).searchParams;
  const page = Number(params.get('Page') ?? 1);
  const pageSize = Number(params.get('PageSize') ?? 20);

  const ordered = [...mockState.transactions].sort((a, b) =>
    b.createdAt.localeCompare(a.createdAt),
  );
  const totalItems = ordered.length;
  const totalPages = Math.max(1, Math.ceil(totalItems / pageSize));
  const data = ordered.slice((page - 1) * pageSize, page * pageSize);

  return response(200).json({
    data,
    pagination: {
      page,
      pageSize,
      totalItems,
      totalPages,
      hasNextPage: page < totalPages,
      hasPreviousPage: page > 1,
    },
  });
});

/** GET /api/transactions/{id} — T2 detail, enveloped; unknown ids are a real 404. */
const getTransaction = api.get('/api/transactions/{id}', ({ params, response }) => {
  const transaction = mockState.transactions.find((t) => t.id === params.id);
  if (!transaction) {
    return response.untyped(
      problem({
        status: 404,
        errorCode: 'NOT_FOUND',
        detail: `Transaction with identifier '${params.id}' was not found.`,
      }),
    );
  }
  return response(200).json({ data: transaction, message: null });
});

/** POST /api/transactions/deposit — the stateful idempotency protocol (ADR-0009). */
const deposit = api.post('/api/transactions/deposit', async ({ request, response }) => {
  const key = request.headers.get('Idempotency-Key');
  if (!key) {
    return response.untyped(
      problem({
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_MISSING',
        detail: 'The Idempotency-Key header is required.',
      }),
    );
  }
  if (!UUID_RE.test(key) || key === NIL_UUID) {
    return response.untyped(
      problem({
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_INVALID',
        detail: 'The Idempotency-Key header must be a single non-empty UUID.',
      }),
    );
  }

  const raw = await request.clone().text();
  const fp = fingerprint(raw);
  const stored = mockState.idempotency.get(`deposit|${key}`);
  if (stored) {
    if (stored.bodyFingerprint !== fp) {
      return response.untyped(
        problem({
          status: 422,
          errorCode: 'IDEMPOTENCY_KEY_REUSE',
          detail: 'This idempotency key was already used with a different payload.',
        }),
      );
    }
    // Replay: the STORED bytes, verbatim, plus the replay marker header.
    return response.untyped(
      new HttpResponse(stored.body, {
        status: stored.status,
        headers: { 'Content-Type': 'application/json', 'Idempotency-Replayed': 'true' },
      }),
    );
  }

  const body = JSON.parse(raw) as { accountId?: string; amount?: number };
  const amount = body.amount ?? 0;
  const payload = {
    data: {
      transaction: {
        id: '019f7b3f-0000-7000-8000-000000000001',
        transactionNumber: 'TXN-20260720-000001',
        type: 'Deposit' as const,
        amount,
        balanceAfter: 1000 + amount,
        description: null,
        recipientAzureTag: null,
        senderAzureTag: null,
        status: 'Completed' as const,
        createdAt: '2026-07-20T12:00:00.0000000Z',
      },
      newBalance: 1000 + amount,
    },
    message: 'Deposit completed successfully.',
  };
  const text = JSON.stringify(payload);
  mockState.idempotency.set(`deposit|${key}`, { bodyFingerprint: fp, status: 201, body: text });

  // Round-trip through the TYPED helper so the success shape is compile-checked against
  // schema.d.ts (the stored string above replays byte-identically on retries).
  return response(201).json(payload);
});

/** POST /api/transfers — level-2 step-up gate (AuthLevelMiddleware semantics). */
const transfer = api.post('/api/transfers', async ({ request, response }) => {
  if (mockState.authLevel < 2) {
    // The 403 body is AuthLevelMiddleware's BARE shape — deliberately not ProblemDetails.
    return response.untyped(
      HttpResponse.json(
        {
          type: 'STEP_UP_REQUIRED',
          title: 'PIN Verification Required',
          detail: 'This operation requires PIN verification',
          requiredLevel: 2,
          currentLevel: mockState.authLevel,
          status: 403,
        },
        {
          status: 403,
          headers: {
            'X-Auth-Level-Required': '2',
            'X-Auth-Level-Current': String(mockState.authLevel),
          },
        },
      ),
    );
  }

  const body = (await request.clone().json()) as { amount?: number; recipientAzureTag?: string };
  return response(201).json({
    data: {
      transactionNumber: 'TXN-20260720-000002',
      amount: body.amount ?? 0,
      newBalance: 1000 - (body.amount ?? 0),
      recipientAzureTag: body.recipientAzureTag ?? 'someone',
      recipientName: 'John D.',
      processedAt: '2026-07-20T12:00:00.0000000Z',
    },
    message: 'Transfer completed successfully.',
  });
});

/**
 * POST /bff/auth/verify-pin — BFF endpoint (outside the API spec, so plain msw).
 * Source semantics: correct PIN elevates the session to level 2 for 5 minutes; a WRONG
 * PIN is HTTP 200 with verified:false, never a 4xx (PIN lockout is a separate 429).
 */
const MOCK_PIN = '123456';
const verifyPin = http.post('*/bff/auth/verify-pin', async ({ request }) => {
  const { pin } = (await request.json()) as { pin?: string };
  if (pin === MOCK_PIN) {
    mockState.authLevel = 2;
    return HttpResponse.json({
      data: { verified: true, authLevel: 2, pinExpiresAt: '2026-07-20T12:05:00.0000000Z' },
      message: 'PIN verified.',
    });
  }
  return HttpResponse.json({
    data: { verified: false, authLevel: mockState.authLevel, pinExpiresAt: null },
    message: 'PIN verification failed.',
  });
});

/**
 * BFF auth handlers (outside the API spec — plain msw, shapes mirror BffResponses.cs).
 * Semantics from the controller source: login/register 200/201 with the SAME
 * ApiResponse<BffLoginResponse> envelope; /me is enveloped, session-status is BARE;
 * logout is message-only; an unauthenticated /me is a 401 ProblemDetails WITHOUT an
 * errorCode (the BFF's own shape — normalizes to HTTP_401 client-side).
 */
const login = http.post('*/bff/auth/login', async ({ request }) => {
  const { email, password } = (await request.json()) as { email?: string; password?: string };
  if (email !== MOCK_USER.email || password !== MOCK_PASSWORD) {
    return problem({
      status: 401,
      errorCode: 'INVALID_CREDENTIALS',
      title: 'Unauthorized',
      detail: 'Invalid email or password',
    });
  }
  mockState.session = { ...MOCK_USER };
  return HttpResponse.json({
    data: { user: { ...MOCK_USER }, expiresAt: '2026-07-20T13:00:00.0000000Z' },
    message: 'Login successful',
  });
});

const register = http.post('*/bff/auth/register', async ({ request }) => {
  const body = (await request.json()) as {
    azureTag?: string;
    email?: string;
    firstName?: string;
    lastName?: string;
  };
  if (body.email === 'taken@azurebank.dev') {
    // ADR-0013 bounded acceptance: the genericised duplicate outcome.
    return problem({
      status: 409,
      errorCode: 'REGISTRATION_FAILED',
      detail: 'We could not create an account with these details.',
    });
  }
  const user: MockSessionUser = {
    id: '019f7b3f-0000-7000-8000-00000000aaaa',
    email: body.email ?? 'new@azurebank.dev',
    firstName: body.firstName ?? 'New',
    lastName: body.lastName ?? 'User',
    azureTag: body.azureTag ?? 'new_user',
    hasPin: false,
  };
  mockState.session = user;
  // Real registration (AuthService) atomically creates the user's primary account:
  // 'Primary Account', Checking, balance 0, isPrimary true — mirror it.
  mockState.accounts = [
    {
      id: '019f7b3f-0000-7000-8000-00000000b001',
      accountNumber: 'AB-****-****-11',
      name: 'Primary Account',
      type: 'Checking',
      balance: 0,
      isPrimary: true,
      createdAt: '2026-07-20T12:00:00.0000000Z',
    },
  ];
  return HttpResponse.json(
    {
      data: { user, expiresAt: '2026-07-20T13:00:00.0000000Z' },
      message: 'Registration successful',
    },
    { status: 201 },
  );
});

const me = http.get('*/bff/auth/me', () => {
  if (!mockState.session) {
    // The BFF's own 401: ProblemDetails WITHOUT errorCode.
    return problem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' });
  }
  return HttpResponse.json({
    data: {
      user: { ...mockState.session },
      session: {
        authLevel: mockState.authLevel,
        createdAt: '2026-07-20T12:00:00.0000000Z',
        lastActivity: '2026-07-20T12:10:00.0000000Z',
        expiresAt: '2026-07-20T13:00:00.0000000Z',
        isPinVerified: mockState.authLevel === 2,
        pinExpiresAt: mockState.authLevel === 2 ? '2026-07-20T12:15:00.0000000Z' : null,
      },
    },
    message: null,
  });
});

const logout = http.post('*/bff/auth/logout', () => {
  mockState.session = null;
  mockState.authLevel = 1;
  return HttpResponse.json({ message: 'Logged out successfully' });
});

const sessionStatus = http.get('*/bff/auth/session-status', () => {
  // BARE by contract — the second non-envelope response besides GET /api/transactions.
  if (!mockState.session) {
    return HttpResponse.json({ isAuthenticated: false, authLevel: null, isPinVerified: null });
  }
  return HttpResponse.json({
    isAuthenticated: true,
    authLevel: mockState.authLevel,
    isPinVerified: mockState.authLevel === 2,
  });
});

export const handlers = [
  listAccounts,
  createAccount,
  renameAccount,
  setPrimaryAccount,
  deleteAccount,
  listTransactions,
  getTransaction,
  deposit,
  transfer,
  verifyPin,
  login,
  register,
  me,
  logout,
  sessionStatus,
];
