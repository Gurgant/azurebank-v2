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

/**
 * The BFF AuthLevelMiddleware 403 for a level-2 route — its BARE shape (deliberately NOT
 * ProblemDetails) plus the X-Auth-Level-* headers the client normalizes into STEP_UP_REQUIRED.
 * Shared by the transfer, internal-transfer, and reveal handlers.
 */
function stepUp403(currentLevel: number) {
  return HttpResponse.json(
    {
      type: 'STEP_UP_REQUIRED',
      title: 'PIN Verification Required',
      detail: 'This operation requires PIN verification',
      requiredLevel: 2,
      currentLevel,
      status: 403,
    },
    {
      status: 403,
      headers: {
        'X-Auth-Level-Required': '2',
        'X-Auth-Level-Current': String(currentLevel),
      },
    },
  );
}

/**
 * The API never exposes the full number (AccountMapper masks it server-side), so the mock holds
 * none. Synthesize a deterministic unmasked value from the visible last group for the reveal
 * endpoint: `AB-****-****-90` → `AB-1234-5678-90`.
 */
function unmaskForMock(masked: string): string {
  return `AB-1234-5678-${masked.slice(-2)}`;
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
 * GET /api/accounts/{id}/full-number — PIN-gated reveal (ADR-0020). Mirrors the BFF ordering:
 * the level-2 gate first (403 step-up when not elevated), THEN ownership (404), then the full
 * unmasked number the API emits only here.
 */
const revealAccountNumber = api.get('/api/accounts/{id}/full-number', ({ params, response }) => {
  if (mockState.authLevel < 2) {
    return response.untyped(stepUp403(mockState.authLevel));
  }
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
  return response(200).json({
    data: { accountId: account.id, accountNumber: unmaskForMock(account.accountNumber) },
    message: null,
  });
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

/**
 * GET /api/transactions/summary — enveloped aggregate over the stateful ledger,
 * mirroring the real SQL semantics exactly: Completed-only sums (income = Deposit +
 * TransferIn, expenses = Withdrawal + TransferOut), Pending counted separately,
 * inclusive window, resolved bounds echoed back. Date math via Date.parse — the mock
 * ledger uses 7-digit fractions while callers send 3-digit ISO, so lexicographic
 * comparison would lie.
 */
const transactionSummary = api.get('/api/transactions/summary', ({ request, response }) => {
  const params = new URL(request.url).searchParams;
  // Resolve the window FIRST and use the SAME values for both the filter and the echo —
  // they must never diverge. Defaults mirror the real API: missing ToDate = "now".
  const fromDate = params.get('FromDate') ?? '1970-01-01T00:00:00.0000000Z';
  const toDate = params.get('ToDate') ?? new Date().toISOString();
  const fromMs = Date.parse(fromDate);
  const toMs = Date.parse(toDate);

  let totalIncome = 0;
  let totalExpenses = 0;
  let pendingCount = 0;
  for (const t of mockState.transactions) {
    const at = Date.parse(t.createdAt);
    if (at < fromMs || at > toMs) {
      continue;
    }
    if (t.status === 'Pending') {
      pendingCount += 1;
    }
    if (t.status !== 'Completed') {
      continue;
    }
    if (t.type === 'Deposit' || t.type === 'TransferIn') {
      totalIncome += t.amount;
    } else {
      totalExpenses += t.amount;
    }
  }
  // Money-safe rounding: float accumulation must not leak sub-cent artifacts.
  totalIncome = Math.round(totalIncome * 100) / 100;
  totalExpenses = Math.round(totalExpenses * 100) / 100;

  return response(200).json({
    data: {
      totalIncome,
      totalExpenses,
      netChange: Math.round((totalIncome - totalExpenses) * 100) / 100,
      pendingCount,
      fromDate,
      toDate,
    },
    message: null,
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

  const body = JSON.parse(raw) as { accountId?: string; amount?: number; description?: string };
  const amount = body.amount ?? 0;

  // Stateful side effects run ONCE, here on the fresh (non-replayed) path — the replay
  // branch above returns the stored bytes without re-applying them (idempotent).
  const account = mockState.accounts.find((a) => a.id === body.accountId);
  const newBalance = (account?.balance ?? 1000) + amount;
  if (account) {
    account.balance = newBalance;
  }
  const index = mockState.transactions.length;
  const transaction = {
    id: `019f7b3f-0000-7000-8000-0000000000d${String(index).padStart(2, '0')}`,
    transactionNumber: `TXN-20260722-${String(300 + index).padStart(6, '0')}`,
    type: 'Deposit' as const,
    amount,
    balanceAfter: newBalance,
    description: body.description ?? null,
    recipientAzureTag: null,
    senderAzureTag: null,
    status: 'Completed' as const,
    // Latest timestamp so it leads the newest-first history feed.
    createdAt: `2026-07-22T10:${String(index).padStart(2, '0')}:00.0000000Z`,
  };
  mockState.transactions.push(transaction);

  const payload = {
    data: { transaction, newBalance },
    message: 'Deposit completed successfully.',
  };
  const text = JSON.stringify(payload);
  mockState.idempotency.set(`deposit|${key}`, { bodyFingerprint: fp, status: 201, body: text });

  // Round-trip through the TYPED helper so the success shape is compile-checked against
  // schema.d.ts (the stored string above replays byte-identically on retries).
  return response(201).json(payload);
});

/**
 * POST /api/transactions/withdraw — the deposit protocol PLUS the PIN-in-body gate (D1).
 * NOT step-up: the PIN travels in the request body and is verified here, so this endpoint
 * never returns the 403 STEP_UP_REQUIRED shape (that gates transfers only). Failure order
 * mirrors the backend (TransactionService.WithdrawAsync): idempotency → PIN_REQUIRED →
 * PIN_LOCKED → INVALID_PIN → INSUFFICIENT_FUNDS → success. Side effects (balance debit,
 * transaction, stored idempotency record) run ONLY on the success path.
 */
const withdraw = api.post('/api/transactions/withdraw', async ({ request, response }) => {
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
  const stored = mockState.idempotency.get(`withdraw|${key}`);
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
    return response.untyped(
      new HttpResponse(stored.body, {
        status: stored.status,
        headers: { 'Content-Type': 'application/json', 'Idempotency-Replayed': 'true' },
      }),
    );
  }

  const body = JSON.parse(raw) as {
    accountId?: string;
    amount?: number;
    pin?: string;
    description?: string;
  };
  const amount = body.amount ?? 0;

  // PIN_REQUIRED — the user never set a PIN. Gated only when a session exists; tests that
  // render the dialog without seeding a session are treated as a PIN-holder (they pass one).
  if (mockState.session && !mockState.session.hasPin) {
    return response.untyped(
      problem({
        status: 422,
        errorCode: 'PIN_REQUIRED',
        detail: 'PIN must be set before making withdrawals.',
      }),
    );
  }

  // PIN_LOCKED — attempt-limiting window still open (checked BEFORE the PIN compare, like
  // the backend refuses before Argon2id runs).
  const now = Date.now();
  if (mockState.pinLockedUntil && Date.parse(mockState.pinLockedUntil) > now) {
    const retryAfterSeconds = Math.ceil((Date.parse(mockState.pinLockedUntil) - now) / 1000);
    return response.untyped(
      problem({
        status: 429,
        errorCode: 'PIN_LOCKED',
        detail: 'Too many incorrect PIN attempts. Please try again later.',
        extensions: { retryAfterSeconds, lockedUntil: mockState.pinLockedUntil },
        headers: { 'Retry-After': String(retryAfterSeconds) },
      }),
    );
  }

  // INVALID_PIN — wrong PIN. The 3rd consecutive miss trips the 15-minute lock and returns
  // 429 PIN_LOCKED (not 401), exactly like ValidationRules.MaxPinAttempts.
  if (body.pin !== mockState.pin) {
    mockState.pinAttempts += 1;
    if (mockState.pinAttempts >= 3) {
      mockState.pinAttempts = 0;
      const retryAfterSeconds = 15 * 60;
      mockState.pinLockedUntil = new Date(now + retryAfterSeconds * 1000).toISOString();
      return response.untyped(
        problem({
          status: 429,
          errorCode: 'PIN_LOCKED',
          detail: 'Too many incorrect PIN attempts. Please try again later.',
          extensions: { retryAfterSeconds, lockedUntil: mockState.pinLockedUntil },
          headers: { 'Retry-After': String(retryAfterSeconds) },
        }),
      );
    }
    return response.untyped(
      problem({ status: 401, errorCode: 'INVALID_PIN', detail: 'Invalid PIN.' }),
    );
  }
  // Correct PIN clears the attempt counter.
  mockState.pinAttempts = 0;

  // INSUFFICIENT_FUNDS — after the PIN passes, like the backend orders it.
  const account = mockState.accounts.find((a) => a.id === body.accountId);
  const available = account?.balance ?? 0;
  if (amount > available) {
    return response.untyped(
      problem({
        status: 422,
        errorCode: 'INSUFFICIENT_FUNDS',
        detail: 'Insufficient funds for this withdrawal.',
        extensions: { available, requested: amount },
      }),
    );
  }

  // Success — debit, record, store (once), reply.
  const newBalance = available - amount;
  if (account) {
    account.balance = newBalance;
  }
  const index = mockState.transactions.length;
  const transaction = {
    id: `019f7b3f-0000-7000-8000-${(0x400 + index).toString(16).padStart(12, '0')}`,
    transactionNumber: `TXN-20260722-${String(400 + index).padStart(6, '0')}`,
    type: 'Withdrawal' as const,
    amount,
    balanceAfter: newBalance,
    description: body.description ?? null,
    recipientAzureTag: null,
    senderAzureTag: null,
    status: 'Completed' as const,
    createdAt: `2026-07-22T11:${String(index).padStart(2, '0')}:00.0000000Z`,
  };
  mockState.transactions.push(transaction);

  const payload = { data: { transaction, newBalance }, message: 'Withdrawal successful' };
  const text = JSON.stringify(payload);
  mockState.idempotency.set(`withdraw|${key}`, { bodyFingerprint: fp, status: 201, body: text });
  return response(201).json(payload);
});

/**
 * GET /api/users/{azureTag} — exact-match recipient lookup (ADR-0014, level-1, no listing).
 * A nonexistent OR self tag returns HTTP 200 { exists:false, displayName:'' } — never a 404,
 * and self is masked identically to unknown (UserService semantics).
 */
const lookupRecipient = api.get('/api/users/{azureTag}', ({ params, response }) => {
  const tag = params.azureTag;
  const isSelf = mockState.session?.azureTag === tag;
  const found = isSelf ? undefined : mockState.recipients.find((r) => r.azureTag === tag);
  if (found) {
    return response(200).json({
      data: { azureTag: found.azureTag, displayName: found.displayName, exists: true },
      message: null,
    });
  }
  return response(200).json({
    data: { azureTag: tag, displayName: '', exists: false },
    message: null,
  });
});

/**
 * PATCH /api/users/me/azuretag — rename the caller's own public handle (ADR-0015). Mirrors the
 * service: normalize to lower-case, 409 AZURE_TAG_TAKEN when another user already holds it (our
 * seeded recipients stand in for "other users"), otherwise update the session and echo the new tag.
 */
const renameAzureTag = api.patch('/api/users/me/azuretag', async ({ request, response }) => {
  const body = (await request.clone().json()) as { azureTag?: string };
  const tag = (body.azureTag ?? '').toLowerCase();
  if (mockState.recipients.some((r) => r.azureTag === tag)) {
    return response.untyped(
      problem({
        status: 409,
        errorCode: 'AZURE_TAG_TAKEN',
        detail: 'That handle is already taken.',
      }),
    );
  }
  if (mockState.session) {
    mockState.session.azureTag = tag;
  }
  return response(200).json({ data: { azureTag: tag }, message: 'AzureTag updated' });
});

/**
 * POST /api/transfers — the level-2 step-up gate (BFF AuthLevelMiddleware, runs BEFORE the
 * API) PLUS the API's idempotency protocol. Failure order mirrors the real path: 403 gate →
 * idempotency → SELF_TRANSFER_NOT_ALLOWED → recipient not found (ACCOUNT_NOT_FOUND, the real
 * code) → INSUFFICIENT_FUNDS → success (debit sender, push a TransferOut row; the recipient
 * is off-ledger). A missing fromAccount is tolerated (debit only if found) so the stepup.test
 * contract (fromAccountId:'a') keeps passing.
 */
const transfer = api.post('/api/transfers', async ({ request, response }) => {
  if (mockState.authLevel < 2) {
    return response.untyped(stepUp403(mockState.authLevel));
  }

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
  const stored = mockState.idempotency.get(`transfer|${key}`);
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
    return response.untyped(
      new HttpResponse(stored.body, {
        status: stored.status,
        headers: { 'Content-Type': 'application/json', 'Idempotency-Replayed': 'true' },
      }),
    );
  }

  const body = JSON.parse(raw) as {
    fromAccountId?: string;
    recipientAzureTag?: string;
    amount?: number;
    description?: string;
  };
  const amount = body.amount ?? 0;
  const tag = body.recipientAzureTag ?? '';

  if (mockState.session?.azureTag === tag) {
    return response.untyped(
      problem({
        status: 422,
        errorCode: 'SELF_TRANSFER_NOT_ALLOWED',
        detail: 'You cannot transfer money to yourself.',
      }),
    );
  }
  const recipient = mockState.recipients.find((r) => r.azureTag === tag);
  if (!recipient) {
    // Recipient-not-found surfaces as ACCOUNT_NOT_FOUND — NotFoundException hard-codes it.
    return response.untyped(
      problem({
        status: 404,
        errorCode: 'ACCOUNT_NOT_FOUND',
        detail: `No user was found with the handle '${tag}'.`,
      }),
    );
  }

  const account = mockState.accounts.find((a) => a.id === body.fromAccountId);
  const available = account?.balance ?? 1000; // tolerate a missing fromAccount (stepup.test)
  if (amount > available) {
    return response.untyped(
      problem({
        status: 422,
        errorCode: 'INSUFFICIENT_FUNDS',
        detail: 'Insufficient funds for this transfer.',
        extensions: { available, requested: amount },
      }),
    );
  }

  const newBalance = available - amount;
  if (account) {
    account.balance = newBalance;
  }
  const index = mockState.transactions.length;
  mockState.transactions.push({
    id: `019f7b3f-0000-7000-8000-${(0x800 + index).toString(16).padStart(12, '0')}`,
    transactionNumber: `TXN-20260722-${String(500 + index).padStart(6, '0')}`,
    type: 'TransferOut',
    amount,
    balanceAfter: newBalance,
    description: body.description ?? null,
    recipientAzureTag: tag,
    senderAzureTag: null,
    status: 'Completed',
    createdAt: `2026-07-22T12:${String(index).padStart(2, '0')}:00.0000000Z`,
  });

  const payload = {
    data: {
      transactionNumber: `TXN-20260722-${String(500 + index).padStart(6, '0')}`,
      amount,
      newBalance,
      recipientAzureTag: tag,
      recipientName: recipient.displayName,
      processedAt: '2026-07-22T12:00:00.0000000Z',
    },
    message: 'Transfer completed successfully.',
  };
  const text = JSON.stringify(payload);
  mockState.idempotency.set(`transfer|${key}`, { bodyFingerprint: fp, status: 201, body: text });
  return response(201).json(payload);
});

/**
 * POST /api/transfers/internal — move money between the caller's OWN accounts. Same level-2
 * step-up gate + idempotency as the external transfer, but double-entry ON-ledger: debit the
 * source, credit the destination, push BOTH a TransferOut and a TransferIn row. Failure order
 * mirrors the backend: 403 gate → idempotency → SAME_ACCOUNT_TRANSFER (from==to) → ownership
 * (either account missing → ACCOUNT_NOT_FOUND) → INSUFFICIENT_FUNDS → success.
 */
const transferInternal = api.post('/api/transfers/internal', async ({ request, response }) => {
  if (mockState.authLevel < 2) {
    return response.untyped(stepUp403(mockState.authLevel));
  }

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
  const stored = mockState.idempotency.get(`internal|${key}`);
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
    return response.untyped(
      new HttpResponse(stored.body, {
        status: stored.status,
        headers: { 'Content-Type': 'application/json', 'Idempotency-Replayed': 'true' },
      }),
    );
  }

  const body = JSON.parse(raw) as {
    fromAccountId?: string;
    toAccountId?: string;
    amount?: number;
    description?: string;
  };
  const amount = body.amount ?? 0;

  // Validation runs first (like FluentValidation before the controller). A non-positive
  // amount must be rejected BEFORE any balance math — otherwise a negative amount would
  // invert the transfer (credit the source, debit the destination). Mirrors
  // InternalTransferRequestValidator (Amount >= TransactionMinAmount).
  if (amount < 0.01) {
    return response.untyped(
      problem({ status: 400, errors: { amount: ['Amount must be at least $0.01.'] } }),
    );
  }

  if (body.fromAccountId && body.fromAccountId === body.toAccountId) {
    return response.untyped(
      problem({
        status: 422,
        errorCode: 'SAME_ACCOUNT_TRANSFER',
        detail: 'Cannot transfer to the same account.',
      }),
    );
  }
  const from = mockState.accounts.find((a) => a.id === body.fromAccountId);
  const to = mockState.accounts.find((a) => a.id === body.toAccountId);
  if (!from || !to) {
    return response.untyped(
      problem({
        status: 404,
        errorCode: 'ACCOUNT_NOT_FOUND',
        detail: 'One of the accounts could not be found.',
      }),
    );
  }
  if (amount > from.balance) {
    return response.untyped(
      problem({
        status: 422,
        errorCode: 'INSUFFICIENT_FUNDS',
        detail: 'Insufficient funds for this transfer.',
        extensions: { available: from.balance, requested: amount },
      }),
    );
  }

  from.balance -= amount;
  to.balance += amount;
  const index = mockState.transactions.length;
  const transactionNumber = `TXN-20260722-${String(600 + index).padStart(6, '0')}`;
  const at = `2026-07-22T13:${String(index).padStart(2, '0')}:00.0000000Z`;
  mockState.transactions.push({
    id: `019f7b3f-0000-7000-8000-${(0xc00 + index).toString(16).padStart(12, '0')}`,
    transactionNumber,
    type: 'TransferOut',
    amount,
    balanceAfter: from.balance,
    description: body.description ?? `Internal transfer to ${to.name}`,
    recipientAzureTag: null,
    senderAzureTag: null,
    status: 'Completed',
    createdAt: at,
  });
  mockState.transactions.push({
    id: `019f7b3f-0000-7000-8000-${(0xc00 + index + 1).toString(16).padStart(12, '0')}`,
    transactionNumber: `TXN-20260722-${String(600 + index + 1).padStart(6, '0')}`,
    type: 'TransferIn',
    amount,
    balanceAfter: to.balance,
    description: body.description ?? `Internal transfer from ${from.name}`,
    recipientAzureTag: null,
    senderAzureTag: null,
    status: 'Completed',
    createdAt: at,
  });

  const payload = {
    data: {
      transferId: `019f7b3f-0000-7000-8000-${(0xd00 + index).toString(16).padStart(12, '0')}`,
      transactionNumber,
      fromAccountId: from.id,
      toAccountId: to.id,
      amount,
      description: body.description ?? null,
      fromAccountNewBalance: from.balance,
      toAccountNewBalance: to.balance,
      processedAt: '2026-07-22T13:00:00.0000000Z',
    },
    message: 'Internal transfer completed successfully.',
  };
  const text = JSON.stringify(payload);
  mockState.idempotency.set(`internal|${key}`, { bodyFingerprint: fp, status: 201, body: text });
  return response(201).json(payload);
});

/**
 * POST /bff/auth/verify-pin — BFF endpoint (outside the API spec, so plain msw).
 * Source semantics: correct PIN elevates the session to level 2; a WRONG PIN is HTTP 200
 * with verified:false, never a 4xx. Shares the SAME attempt/lock state as withdraw
 * (mockState.pinAttempts/pinLockedUntil) so the 3rd consecutive miss is a 429 PIN_LOCKED.
 */
const verifyPin = http.post('*/bff/auth/verify-pin', async ({ request }) => {
  const { pin } = (await request.json()) as { pin?: string };
  const now = Date.now();
  if (mockState.pinLockedUntil && Date.parse(mockState.pinLockedUntil) > now) {
    const retryAfterSeconds = Math.ceil((Date.parse(mockState.pinLockedUntil) - now) / 1000);
    return problem({
      status: 429,
      errorCode: 'PIN_LOCKED',
      detail: 'Too many incorrect PIN attempts. Please try again later.',
      extensions: { retryAfterSeconds, lockedUntil: mockState.pinLockedUntil },
      headers: { 'Retry-After': String(retryAfterSeconds) },
    });
  }
  if (pin === mockState.pin) {
    mockState.pinAttempts = 0;
    mockState.authLevel = 2;
    return HttpResponse.json({
      data: { verified: true, authLevel: 2, pinExpiresAt: '2026-07-20T12:05:00.0000000Z' },
      message: 'PIN verified.',
    });
  }
  mockState.pinAttempts += 1;
  if (mockState.pinAttempts >= 3) {
    mockState.pinAttempts = 0;
    const retryAfterSeconds = 15 * 60;
    mockState.pinLockedUntil = new Date(now + retryAfterSeconds * 1000).toISOString();
    return problem({
      status: 429,
      errorCode: 'PIN_LOCKED',
      detail: 'Too many incorrect PIN attempts. Please try again later.',
      extensions: { retryAfterSeconds, lockedUntil: mockState.pinLockedUntil },
      headers: { 'Retry-After': String(retryAfterSeconds) },
    });
  }
  return HttpResponse.json({
    data: { verified: false, authLevel: mockState.authLevel, pinExpiresAt: null },
    message: 'PIN verification failed.',
  });
});

/**
 * POST /bff/auth/set-pin — set/overwrite the user's PIN (SetPinController, AuthLevel 1: no
 * old PIN and no step-up required). A bad format is the API's VALIDATION_ERROR shape (400,
 * `errors` dict, NO errorCode — the FE synthesizes VALIDATION_ERROR). On success it flips
 * session.hasPin so the invalidated /me refetch clears the withdraw gate, and clears any
 * prior lock so the freshly-set PIN works immediately.
 */
const setPin = http.post('*/bff/auth/set-pin', async ({ request }) => {
  const { pin } = (await request.json()) as { pin?: string };
  if (!pin || !/^\d{6}$/.test(pin)) {
    return problem({ status: 400, errors: { pin: ['PIN must be exactly 6 digits.'] } });
  }
  mockState.pin = pin;
  mockState.pinAttempts = 0;
  mockState.pinLockedUntil = null;
  if (mockState.session) {
    mockState.session.hasPin = true;
  }
  return HttpResponse.json({ message: 'PIN set successfully' });
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
    data: { user: { ...MOCK_USER }, expiresAt: '2099-01-01T00:00:00.0000000Z' },
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
      data: { user, expiresAt: '2099-01-01T00:00:00.0000000Z' },
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
        expiresAt: '2099-01-01T00:00:00.0000000Z',
        isPinVerified: mockState.authLevel === 2,
        // Future (before the session's own expiry) so a PIN-verified fixture is self-consistent —
        // isPinVerified:true must not ship an already-elapsed pinExpiresAt.
        pinExpiresAt: mockState.authLevel === 2 ? '2098-01-01T00:00:00.0000000Z' : null,
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
  revealAccountNumber,
  listTransactions,
  transactionSummary,
  getTransaction,
  deposit,
  withdraw,
  lookupRecipient,
  renameAzureTag,
  transfer,
  transferInternal,
  verifyPin,
  setPin,
  login,
  register,
  me,
  logout,
  sessionStatus,
];
