import { http, HttpResponse } from 'msw';
import { createOpenApiHttp } from 'openapi-msw';
import type { paths } from '../api/schema';
import type { AccountType } from '../api/enums';
import { problem } from './problem';
import {
  MOCK_PASSWORD,
  MOCK_USER,
  expireMockSessionIfDue,
  markMockActivity,
  mockAccessTokenExpiry,
  mockState,
  toWire,
  type MockSessionUser,
} from './state';

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
 * The amount guard every money endpoint runs FIRST, like FluentValidation does before the
 * controller ever sees the request.
 *
 * A non-positive amount has to be rejected before any balance math, because the arithmetic is
 * symmetric and the intent is not: a negative deposit debits the account, and a negative
 * withdrawal or transfer credits it. The internal transfer was the only handler that said so; the
 * other three took `body.amount ?? 0` straight into the balance. One copy, four call sites, so
 * they cannot drift apart again.
 *
 * The bounds are the contract's own — `ValidationRules.TransactionMinAmount/MaxAmount`, which the
 * generated Zod schema mirrors as `.min(0.01).max(100000)`. The message reads in dollars because
 * the API's own message does (see `schema.d.ts`: "Amount must be between $0.01 and $100,000.00.");
 * this is the mock quoting the contract, not the app's EUR display.
 */
function rejectBadAmount(amount: unknown): ReturnType<typeof problem> | null {
  if (typeof amount !== 'number' || !Number.isFinite(amount) || amount < 0.01) {
    return problem({ status: 400, errors: { amount: ['Amount must be at least $0.01.'] } });
  }
  if (amount > 100_000) {
    return problem({ status: 400, errors: { amount: ['Amount cannot exceed $100,000.00.'] } });
  }
  return null;
}

/**
 * The 404 every missing resource produces, mirroring `NotFoundException(resource, identifier)`.
 *
 * Read the constructor before changing this — it is surprising, and the mock had it wrong in two
 * different directions before these were one function:
 *
 *  - The message is `"{resource} with identifier '{id}' was not found."` — the resource name leads.
 *    Three handlers had invented "No account was found with identifier '…'." instead.
 *  - The code is `ErrorCodes.AccountNotFound` — **for every resource**, Account, User, Transaction
 *    and Recipient alike. The two-argument constructor hard-codes it and never looks at
 *    `TransactionNotFound` or `UserNotFound`, which exist but are unreachable from this path. Five
 *    handlers had guessed a plausible `NOT_FOUND` that the API cannot emit.
 *
 * Mirroring the quirk is the point. A mock that "corrects" the server teaches a client to expect a
 * code it will never receive, and the bug then belongs to production rather than to this file.
 */
function notFound(resource: 'Account' | 'Transaction' | 'Recipient', identifier: unknown) {
  return problem({
    status: 404,
    errorCode: 'ACCOUNT_NOT_FOUND',
    detail: `${resource} with identifier '${String(identifier)}' was not found.`,
  });
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

/**
 * GET /api/accounts/{id} — the single account, enveloped like every other read.
 *
 * Had no handler at all while `getAccount`/`useGetAccountQuery` sat exported from the barrel. The
 * sentinel at the bottom of this file is what makes that impossible to repeat quietly.
 */
const getAccount = api.get('/api/accounts/{id}', ({ params, response }) => {
  const account = mockState.accounts.find((a) => a.id === params.id);
  if (!account) {
    return response.untyped(notFound('Account', params.id));
  }
  return response(200).json({ data: account, message: null });
});

/**
 * GET /api/accounts/{id}/balance — current, or as of a moment in the past.
 *
 * `AccountService.GetBalanceAsync` has two branches and the boundary is not the obvious one: `at`
 * omitted OR `at >= now` both mean CURRENT, so a client asking about the future is answered about
 * the present with `isHistorical: false`. The historical branch takes the `balanceAfter` of the
 * most recent entry at or before `at`, and 0 when the account had no entries yet — not the opening
 * balance, zero.
 *
 * Reproducible here only because the ledger gained per-account entries: before that, "the most
 * recent transaction on THIS account" was not a question the mock could answer.
 */
const getAccountBalance = api.get('/api/accounts/{id}/balance', ({ params, request, response }) => {
  const account = mockState.accounts.find((a) => a.id === params.id);
  if (!account) {
    return response.untyped(notFound('Account', params.id));
  }

  const at = new URL(request.url).searchParams.get('at');
  const atMs = at ? Date.parse(at) : Number.NaN;
  const now = new Date();

  if (!at || Number.isNaN(atMs) || atMs >= now.getTime()) {
    return response(200).json({
      data: {
        accountId: account.id,
        balance: account.balance,
        currency: 'EUR',
        asOf: now.toISOString(),
        isHistorical: false,
      },
      message: null,
    });
  }

  const priorEntry = mockState.transactions
    .filter((t) => t.accountId === account.id && Date.parse(t.createdAt) <= atMs)
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0];

  return response(200).json({
    data: {
      accountId: account.id,
      balance: priorEntry ? priorEntry.balanceAfter : 0,
      currency: 'EUR',
      asOf: new Date(atMs).toISOString(),
      isHistorical: true,
    },
    message: null,
  });
});

/** PATCH /api/accounts/{id} — rename (A5): name only, per the contract. */
const renameAccount = api.patch('/api/accounts/{id}', async ({ params, request, response }) => {
  const account = mockState.accounts.find((a) => a.id === params.id);
  if (!account) {
    return response.untyped(notFound('Account', params.id));
  }
  const body = (await request.clone().json()) as { name?: string };
  account.name = body.name ?? account.name;
  return response(200).json({ data: account, message: 'Account updated successfully' });
});

/** PATCH /api/accounts/{id}/set-primary — exactly one primary at a time (A6). */
const setPrimaryAccount = api.patch('/api/accounts/{id}/set-primary', ({ params, response }) => {
  const account = mockState.accounts.find((a) => a.id === params.id);
  if (!account) {
    return response.untyped(notFound('Account', params.id));
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
    return response.untyped(notFound('Account', params.id));
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
    return response.untyped(notFound('Account', params.id));
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
  const accountId = params.get('AccountId');

  // `Number('')` is 0 and `Number('x')` is NaN, and either reached the page math: PageSize 0 makes
  // totalPages Infinity, and a NaN page slices to an empty list while the metadata says otherwise.
  // The real endpoint validates these; the mock has to, or a client could be built against
  // pagination that only the mock would ever produce.
  // `TransactionFilter` carries `[Range(1, int.MaxValue)]` on Page and `[Range(1, 100)]` on
  // PageSize, with those exact messages, and model validation keys the dictionary by PROPERTY
  // name. The mock invented a `pagination` key and a sentence no validator produces, and enforced
  // no upper bound at all — so a client could page 1000 rows at a time against the mock and be
  // rejected in production.
  const pageErrors: Record<string, string[]> = {};
  if (!Number.isInteger(page) || page < 1) {
    pageErrors.page = ['Page must be at least 1.'];
  }
  if (!Number.isInteger(pageSize) || pageSize < 1 || pageSize > 100) {
    pageErrors.pageSize = ['PageSize must be between 1 and 100.'];
  }
  if (Object.keys(pageErrors).length > 0) {
    return response.untyped(problem({ status: 400, errors: pageErrors }));
  }

  // `AccountId` used to be ignored outright, so both accounts returned byte-identical feeds and the
  // dashboard's scope control appeared to do nothing. The real service filters on it, and 403s
  // first when the account is not one of the caller's — a mock that answered 200 with somebody
  // else's ledger would let a broken client look correct.
  if (accountId && !mockState.accounts.some((a) => a.id === accountId)) {
    return response.untyped(
      problem({
        status: 403,
        errorCode: 'ACCESS_DENIED',
        detail: 'You do not have access to this account.',
      }),
    );
  }

  /*
    The date window, which the mock ignored outright.

    `GetTransactionsAsync` applies both bounds INCLUSIVELY and independently —
    `if (filter.FromDate.HasValue) query.Where(t => t.CreatedAt >= ...)` and the same for ToDate
    with `<=`. Neither has a default here: an absent bound means unbounded, unlike the summary
    below, which defaults to the current calendar month. Two endpoints, two different rules, and
    the mock had neither.

    `Date.parse` rather than string comparison: the ledger stores 7-digit fractional seconds and
    callers send 3, so lexicographic ordering would put `…09:15:00.0000000Z` after
    `…09:15:00.000Z` and drop rows on an exact boundary.
  */
  const fromMs = params.get('FromDate') ? Date.parse(params.get('FromDate') as string) : null;
  const toMs = params.get('ToDate') ? Date.parse(params.get('ToDate') as string) : null;

  const ordered = [...mockState.transactions]
    .filter((t) => !accountId || t.accountId === accountId)
    .filter((t) => {
      const at = Date.parse(t.createdAt);
      if (fromMs !== null && !Number.isNaN(fromMs) && at < fromMs) {
        return false;
      }
      return !(toMs !== null && !Number.isNaN(toMs) && at > toMs);
    })
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const totalItems = ordered.length;
  // `Math.ceil`, NOT `max(1, ...)`. An empty result is ZERO pages in `PaginatedResponse` — the
  // no-accounts branch returns `TotalPages = 0` explicitly and the general path computes the same
  // ceiling. Reporting 1 tells a client there is a page to show when there is not.
  const totalPages = Math.ceil(totalItems / pageSize);
  const data = ordered.slice((page - 1) * pageSize, page * pageSize).map(toWire);

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
  /*
    Resolve the window FIRST and use the SAME values for the filter and the echo — they must never
    diverge.

    The default start is the FIRST DAY OF THE CURRENT UTC MONTH, not the epoch:
    `filter.FromDate ?? new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)`. The mock
    used 1970, so "this month so far" summed the entire ledger — and the dashboard's month card,
    whose whole label is "July so far", was quietly reporting all time. Missing ToDate is `now`,
    which the mock did have right.
  */
  const now = new Date();
  const monthStart = new Date(
    Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1, 0, 0, 0, 0),
  ).toISOString();
  const fromDate = params.get('FromDate') ?? monthStart;
  const toDate = params.get('ToDate') ?? now.toISOString();
  const fromMs = Date.parse(fromDate);
  const toMs = Date.parse(toDate);

  // The guard is on the RESOLVED window and it is unconditional — the service's own comment says
  // the filter's model validation only sees the explicitly-provided pair, so a lone future
  // FromDate lands here rather than there. The mock answered 200 with zeroed totals instead.
  if (fromMs > toMs) {
    return response.untyped(
      problem({
        status: 422,
        errorCode: 'INVALID_DATE_RANGE',
        detail: 'FromDate must be earlier than or equal to ToDate.',
      }),
    );
  }

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
    return response.untyped(notFound('Transaction', params.id));
  }
  return response(200).json({ data: toWire(transaction), message: null });
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
  const badAmount = rejectBadAmount(body.amount);
  if (badAmount) {
    return response.untyped(badAmount);
  }
  const amount = body.amount as number;

  // Stateful side effects run ONCE, here on the fresh (non-replayed) path — the replay
  // branch above returns the stored bytes without re-applying them (idempotent).
  const account = mockState.accounts.find((a) => a.id === body.accountId);
  /*
    An unknown account is a 404, exactly as `GetAccountWithOwnershipCheckAsync` makes it one.

    This used to fabricate a 1000 balance and answer 201 with a transaction that no account and no
    ledger row owned — a phantom success. Withholding the ledger write instead was worse in one
    specific way: ids and transaction numbers are derived from `mockState.transactions.length`, so
    the phantom's identifiers were handed straight back to the next real write. The only thing the
    fabrication ever bought was letting the protocol fixtures post to a synthetic id, and those now
    use a seeded account, which costs them nothing.
  */
  if (!account) {
    return response.untyped(notFound('Account', body.accountId));
  }
  const newBalance = account.balance + amount;
  account.balance = newBalance;
  const index = mockState.transactions.length;
  const transaction = {
    // 0xd00 block (12 hex chars = a VALID uuid) — same scheme as withdraw/transfer below.
    id: `019f7b3f-0000-7000-8000-${(0xd00 + index).toString(16).padStart(12, '0')}`,
    accountId: account.id,
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
  // The account is guaranteed real by the 404 above, so this always files against something that
  // exists — which is the whole point: an entry whose `balanceAfter` belonged to no account would
  // sit in the cross-account feed reconciling with nothing.
  mockState.transactions.push(transaction);

  const payload = {
    data: { transaction: toWire(transaction), newBalance },
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
  const badAmount = rejectBadAmount(body.amount);
  if (badAmount) {
    return response.untyped(badAmount);
  }
  const amount = body.amount as number;

  /*
    The account is resolved FIRST, before a single PIN check, because that is the first statement
    of `TransactionService.WithdrawAsync` — `GetAccountWithOwnershipCheckAsync` runs above the
    PIN-set check, the lockout window and the compare.

    The mock had it last, and the order is not cosmetic. Withdrawing from an id that does not
    exist, with a wrong PIN, answered 401 INVALID_PIN here and 404 ACCOUNT_NOT_FOUND in
    production — and worse, the mock CONSUMED a PIN attempt for it. You could lock yourself out
    of the mock by fumbling an account id, which the real API never lets you do because it never
    reaches the PIN.
  */
  const account = mockState.accounts.find((a) => a.id === body.accountId);
  if (!account) {
    return response.untyped(notFound('Account', body.accountId));
  }

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

  // INSUFFICIENT_FUNDS — last, after the PIN passes, like the backend orders it.
  const available = account.balance;
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
  account.balance = newBalance;
  const index = mockState.transactions.length;
  const transaction = {
    id: `019f7b3f-0000-7000-8000-${(0x400 + index).toString(16).padStart(12, '0')}`,
    accountId: account.id,
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

  const payload = {
    data: { transaction: toWire(transaction), newBalance },
    message: 'Withdrawal successful',
  };
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
  const badAmount = rejectBadAmount(body.amount);
  if (badAmount) {
    return response.untyped(badAmount);
  }
  const amount = body.amount as number;
  const tag = body.recipientAzureTag ?? '';

  /*
    Source account FIRST. `TransferService.TransferAsync` opens with
    `GetAccountWithOwnershipCheckAsync(request.FromAccountId, userId)` — above the sender lookup,
    above the self-transfer guard, above the recipient lookup. The mock resolved it LAST, so a
    transfer from an unknown account to yourself answered SELF_TRANSFER_NOT_ALLOWED where the API
    answers ACCOUNT_NOT_FOUND, and a client branching on the code took the wrong path.
  */
  const account = mockState.accounts.find((a) => a.id === body.fromAccountId);
  if (!account) {
    return response.untyped(notFound('Account', body.fromAccountId));
  }

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
    // `TransferService` throws NotFoundException("Recipient", tag) here — hence the resource name
    // and the ACCOUNT_NOT_FOUND code, which that constructor uses for every resource.
    return response.untyped(notFound('Recipient', tag));
  }

  const available = account.balance;
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
  account.balance = newBalance;
  const index = mockState.transactions.length;
  mockState.transactions.push({
    id: `019f7b3f-0000-7000-8000-${(0x800 + index).toString(16).padStart(12, '0')}`,
    accountId: account.id,
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
  const badAmount = rejectBadAmount(body.amount);
  if (badAmount) {
    return response.untyped(badAmount);
  }
  const amount = body.amount as number;

  if (body.fromAccountId && body.fromAccountId === body.toAccountId) {
    return response.untyped(
      problem({
        status: 422,
        errorCode: 'SAME_ACCOUNT_TRANSFER',
        detail: 'Cannot transfer to the same account.',
      }),
    );
  }
  // Checked in turn, source first, because `InternalTransferAsync` resolves them that way through
  // two separate `GetAccountWithOwnershipCheckAsync` calls — so the 404 names the account it could
  // not find rather than shrugging at "one of the accounts".
  const from = mockState.accounts.find((a) => a.id === body.fromAccountId);
  if (!from) {
    return response.untyped(notFound('Account', body.fromAccountId));
  }
  const to = mockState.accounts.find((a) => a.id === body.toAccountId);
  if (!to) {
    return response.untyped(notFound('Account', body.toAccountId));
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
    accountId: from.id,
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
    accountId: to.id,
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
  // A login starts the session clock. Without this the fixtures sit at epoch 0 and every
  // subsequent request looks like a session that expired in 1970.
  mockState.sessionCreatedAt = Date.now();
  mockState.sessionLastActivity = Date.now();
  return HttpResponse.json({
    // The access token's expiry, which is what the real controller forwards here — NOT the
    // session's absolute cap. They are separate rules with separate lengths.
    data: { user: { ...MOCK_USER }, expiresAt: mockAccessTokenExpiry() },
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
  // Registration IS a login: the BFF sets the cookie on the 201, so the clock starts here too.
  mockState.sessionCreatedAt = Date.now();
  mockState.sessionLastActivity = Date.now();
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
      data: { user, expiresAt: mockAccessTokenExpiry() },
      message: 'Registration successful',
    },
    { status: 201 },
  );
});

const me = http.get('*/bff/auth/me', () => {
  // Expiry is checked BEFORE activity is marked. The other order revives a session that died an
  // hour ago simply because something touched it — which is exactly the bug the real BFF avoids by
  // evaluating the deadline in the session store, not in the middleware.
  if (expireMockSessionIfDue() || !mockState.session) {
    // The BFF's own 401: ProblemDetails WITHOUT errorCode.
    return problem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' });
  }
  // /me IS activity: the BFF slides LastActivity on every cookie-bearing request and excludes only
  // the session-status probe (ADR-0018). Modelled here so `inactivityExpiresAt` on this endpoint
  // always reads "now plus the full window" — exactly as it does against a real BFF, which is why a
  // client that polled it for a countdown would see a frozen number in tests too, not just in a
  // browser.
  markMockActivity();
  return HttpResponse.json({
    data: {
      user: { ...mockState.session },
      session: {
        authLevel: mockState.authLevel,
        createdAt: new Date(mockState.sessionCreatedAt).toISOString(),
        lastActivity: new Date(mockState.sessionLastActivity).toISOString(),
        expiresAt: new Date(
          mockState.sessionCreatedAt + mockState.sessionAbsoluteWindowMs,
        ).toISOString(),
        inactivityExpiresAt: new Date(
          mockState.sessionLastActivity + mockState.sessionInactivityWindowMs,
        ).toISOString(),
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
  // Checked here too, and still without marking activity: the probe must be able to report a dead
  // session, which is the whole reason the client can trust it at the zero crossing.
  expireMockSessionIfDue();
  // BARE by contract — the second non-envelope response besides GET /api/transactions.
  if (!mockState.session) {
    return HttpResponse.json({
      isAuthenticated: false,
      authLevel: null,
      isPinVerified: null,
      serverTime: null,
      inactivityExpiresAt: null,
      absoluteExpiresAt: null,
    });
  }
  // Deliberately does NOT mark activity. That exclusion (ADR-0018) is the only reason a client-side
  // countdown is possible, so a mock that slid the clock here would let a broken client pass.
  return HttpResponse.json({
    isAuthenticated: true,
    authLevel: mockState.authLevel,
    isPinVerified: mockState.authLevel === 2,
    // The server's clock, so the client can compute a remaining duration without touching its own.
    serverTime: new Date().toISOString(),
    inactivityExpiresAt: new Date(
      mockState.sessionLastActivity + mockState.sessionInactivityWindowMs,
    ).toISOString(),
    absoluteExpiresAt: new Date(
      mockState.sessionCreatedAt + mockState.sessionAbsoluteWindowMs,
    ).toISOString(),
  });
});

/**
 * The session-activity middleware, modelled where the real one lives.
 *
 * `SessionActivityMiddleware` runs BEFORE routing and slides `LastActivity` on EVERY cookie-bearing
 * request, excluding only the session-status probe (ADR-0018). Modelling that per-endpoint would
 * have meant repeating it in fifteen handlers and forgetting it in the sixteenth, so it sits in
 * front of `/api/*` the same way the real one sits in front of everything.
 *
 * Until this existed the mock clock ran FAST relative to the server. Only `/bff/auth/me` marked
 * activity, so a user reading accounts, filtering history or sending a transfer looked idle to the
 * mock while the real BFF counted every one of those as activity — and a countdown read against
 * that clock would expire someone the server considered perfectly alive.
 *
 * The 401 is narrow on purpose: it fires only when a session EXISTED and has just died, never when
 * there was none to begin with. These handlers have never gated on authentication — most tests call
 * them with no session at all — and turning them into a gate is a separate change with a twenty-file
 * blast radius. What matters here is that a session cannot outlive its own deadline.
 */
const sessionActivity = http.all('*/api/*', () => {
  const hadSession = mockState.session !== null;
  if (expireMockSessionIfDue() && hadSession) {
    return problem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' });
  }
  markMockActivity();
  // Returning nothing falls through to the endpoint handler below.
  return undefined;
});

/**
 * The last handler in the list, and the reason the list can be trusted.
 *
 * `sessionActivity` above is registered as `http.all('*/ api; /*')` and returns `undefined` so the
 * real endpoint handler runs after it. MSW treats that as a MATCH, so a request to an `/api` route
 * with no handler at all is "handled" — and `onUnhandledRequest: 'error'` never fires. Measured:
 * fetching an unmocked `/api/...` inside vitest throws a bare `fetch failed`, exactly like a
 * network outage, because the request escaped MSW entirely. In `dev:mock` (`bypass`) it escapes to
 * Vite, which answers with HTML and the query dies on a parse error.
 *
 * That is how `GET /api/accounts/{id}` and `/{id}/balance` sat unmocked while both of their RTK
 * Query hooks were exported: nothing anywhere said so. A mock that cannot report its own gaps is
 * an oracle you cannot audit.
 *
 * So: an explicit sentinel, LAST, reached only when every real handler has declined. It names the
 * method and the path, which is the sentence the developer needed in the first place.
 */
const unmockedApiRoute = http.all('*/api/*', ({ request }) => {
  const { pathname } = new URL(request.url);
  return problem({
    status: 501,
    errorCode: 'MOCK_HANDLER_MISSING',
    title: 'Not Implemented',
    detail: `No mock handler for ${request.method} ${pathname}. The API has this route; add a handler in src/mocks/handlers.ts.`,
  });
});

export const handlers = [
  sessionActivity,
  listAccounts,
  getAccount,
  getAccountBalance,
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
  // LAST, always. Everything above declines before this speaks.
  unmockedApiRoute,
];
