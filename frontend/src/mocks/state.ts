/**
 * Shared mutable state for the STATEFUL mock handlers — the semantics that make the mocks
 * honest (idempotency replay, step-up level) rather than shape-only stubs.
 * Reset between tests by src/test/setup.ts.
 */
import type { AccountType, TransactionStatus, TransactionType } from '../api/enums';

export interface StoredIdempotentResponse {
  /** Fingerprint of the RAW request body bytes — the backend hashes bytes, not JSON. */
  bodyFingerprint: string;
  status: number;
  /** Stored body, replayed BYTE-IDENTICALLY (string compare in tests proves it). */
  body: string;
}

export interface MockSessionUser {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  azureTag: string;
  hasPin: boolean;
}

/** A transfer recipient in the exact-match directory (ADR-0014 — no substring/listing). */
export interface MockRecipient {
  azureTag: string;
  /** Masked display name for privacy, e.g. "John D." */
  displayName: string;
}

export interface MockAccount {
  id: string;
  accountNumber: string;
  name: string;
  type: AccountType;
  balance: number;
  isPrimary: boolean;
  createdAt: string;
}

/** Exactly the wire shape of `TransactionResponse`. Nothing here that the real API does not send. */
export interface MockTransaction {
  id: string;
  transactionNumber: string;
  type: TransactionType;
  /** UNSIGNED, like the real contract — direction lives in `type`. */
  amount: number;
  balanceAfter: number;
  description: string | null;
  recipientAzureTag: string | null;
  senderAzureTag: string | null;
  status: TransactionStatus;
  createdAt: string;
}

/**
 * What the mock's ledger STORES: the wire shape plus the column the database has and the API does
 * not return.
 *
 * `Transaction.AccountId` is a real column — `TransactionService` filters on it and 403s when the
 * account is not the caller's — but `TransactionResponse` has no such field, so a client cannot
 * tell which account a row belongs to. That asymmetry is not a mock detail: it is why the dashboard
 * shows a running-balance column only when the feed is scoped to one account. A cross-account list
 * has rows whose `balanceAfter` cannot be attributed to anything.
 *
 * It is stripped on the way out by `toWire`, so the mock cannot let a client depend on a field the
 * real API will never send.
 */
export interface MockLedgerEntry extends MockTransaction {
  accountId: string;
}

/**
 * Projects a ledger entry onto the wire shape. Every response path goes through this.
 *
 * An allow-list rather than `const { accountId, ...wire } = entry`, and deliberately: the rest
 * form excludes only the columns somebody remembered to name, so the NEXT ledger-only field is
 * shipped to clients by default. Listing what goes out means a new internal column stays internal
 * until someone decides otherwise.
 */
export function toWire(entry: MockLedgerEntry): MockTransaction {
  return {
    id: entry.id,
    transactionNumber: entry.transactionNumber,
    type: entry.type,
    amount: entry.amount,
    balanceAfter: entry.balanceAfter,
    description: entry.description,
    recipientAzureTag: entry.recipientAzureTag,
    senderAzureTag: entry.senderAzureTag,
    status: entry.status,
    createdAt: entry.createdAt,
  };
}

/** Money-safe: float accumulation must not leak sub-cent artifacts into a balance. */
function round2(n: number): number {
  return Math.round(n * 100) / 100;
}

/**
 * How much an entry moved its account. `type` carries direction, so this is the one place the
 * sign is decided — the same split the summary aggregate uses for income vs expenses.
 *
 * Every entry moves the balance, whatever its status. A `Pending` transfer has already been
 * debited by the time it is recorded (see `TransferService`), and the seeded `Reversed` withdrawal
 * moved the money too — what is missing is the compensating entry that would put it back, which
 * this seed does not model.
 */
function signedDelta(entry: { type: TransactionType; amount: number }): number {
  return entry.type === 'Deposit' || entry.type === 'TransferIn' ? entry.amount : -entry.amount;
}

/**
 * A step-up authorisation the mock has minted (ADR-0042).
 *
 * STATEFUL on purpose. The single-use guarantee is the whole point of the feature, and a mock that
 * let one be spent twice would teach the client a protocol the server does not implement — green
 * forever, and wrong. The bound fields are stored rather than hashed: the mock has no HMAC key and
 * does not need one, it needs to answer AUTHORIZATION_INVALID on exactly the inputs the server does.
 */
export interface StoredStepUpAuthorization {
  operation: 'Transfer' | 'InternalTransfer';
  fromAccountId: string;
  /** External transfers bind the payee's handle here; internal ones leave it undefined. */
  recipientAzureTag?: string;
  /** Internal transfers bind the destination account here; external ones leave it undefined. */
  toAccountId?: string;
  amount: number;
  /** Epoch ms. Past this the mock answers AUTHORIZATION_EXPIRED, not AUTHORIZATION_INVALID. */
  expiresAtMs: number;
  consumed: boolean;
}

interface MockState {
  /** key -> stored response, per (endpoint|key) like the backend's (user, endpoint, key). */
  idempotency: Map<string, StoredIdempotentResponse>;
  /** authorizationId -> the authorisation minted for it (ADR-0042). Spendable exactly once. */
  stepUpAuthorizations: Map<string, StoredStepUpAuthorization>;
  /** BFF session auth level: 1 = password, 2 = PIN-verified (transfers need 2). */
  authLevel: 1 | 2;
  /** BFF session: null = no cookie/anonymous (default — tests seed or log in explicitly). */
  session: MockSessionUser | null;
  /**
   * The browser still sends a cookie, but it no longer resolves to a session.
   *
   * `session: null` conflated two states the real stack answers DIFFERENTLY, because expiry is
   * server-side and deleting a cookie is not:
   *
   *   no cookie at all       -> `AuthLevelMiddleware` never gates (it only acts when
   *                             `Cookies.TryGetValue` succeeds), the request is proxied without a
   *                             bearer, and the API answers 401 AUTH_TOKEN_MISSING.
   *   cookie, dead session   -> the middleware DOES gate, `GetAuthLevel` returns 0, and the three
   *                             level-2 routes answer 403 STEP_UP_REQUIRED with
   *                             `X-Auth-Level-Current: 0`.
   *
   * Set when a session expires by clock, cleared on login/register (a fresh cookie) and on logout
   * (the cookie is deleted). Everything else — ordinary `/api` routes included — is 401 either
   * way, so this flag only changes the answer on the level-2 three.
   */
  staleSessionCookie: boolean;

  /**
   * The server-side session clock, modelled the way the BFF actually keeps it.
   *
   * Windows rather than deadlines, because that is the shape the real configuration has
   * (`Session:InactivityTimeoutMinutes` / `AbsoluteTimeoutMinutes`) and the shape the client now
   * learns from a response. Deadlines are computed per request, so a test that advances the clock
   * sees the same countdown a browser would instead of a frozen fixture.
   *
   * `sessionLastActivity` slides on `/bff/auth/me` and NOT on `/bff/auth/session-status` — the one
   * asymmetry the whole warning depends on (ADR-0018). A mock that slid both would let a broken
   * client look correct.
   */
  sessionInactivityWindowMs: number;
  sessionAbsoluteWindowMs: number;
  sessionCreatedAt: number;
  sessionLastActivity: number;
  /**
   * The session user's PIN — the value withdraw (PIN-in-body, D1) verifies against. Set-pin
   * overwrites it and flips session.hasPin; it defaults to MOCK_PIN so the seeded MOCK_USER
   * (hasPin:true) can withdraw with '123456'.
   */
  pin: string;
  /** Consecutive wrong-PIN count; MaxPinAttempts (3) trips the lockout, a success resets it. */
  pinAttempts: number;
  /**
   * Monotonic sequence for newly created accounts. NEVER derived from `accounts.length`.
   *
   * Delete removes the row outright, so the length goes DOWN and a length-derived identifier is
   * handed out twice — two live accounts wearing the same id, at which point every
   * `find(a => a.id === …)` in the handlers resolves to whichever happens to sit first. This
   * counter only ever increases, so an id retired by a delete is never reissued.
   */
  nextAccountSeq: number;
  /**
   * Failed-LOGIN bookkeeping, per email — separate from the PIN counters on purpose.
   *
   * ADR-0012 gave passwords their own lockout precisely so a wrong PIN cannot lock password login
   * and vice versa. Keyed by email rather than by user because the real counter lives on the user
   * row and a wrong password is, by construction, an attempt on an account you may not own.
   */
  loginFailures: Record<string, number>;
  /** ISO instant a login lock lifts, per email. */
  loginLockedUntil: Record<string, string>;
  /**
   * Timestamps of calls against the BFF's `auth` rate-limiter policy, newest last.
   *
   * The limiter is per-IP and there is exactly one "IP" in a test process, so a plain array is the
   * whole model. Trimmed to the window on each check rather than swept, because nothing here runs
   * long enough for the array to matter and a sweep would need a timer the mock has no business
   * owning.
   */
  authCallTimes: number[];
  /** ISO instant the PIN lock lifts, or null. A withdraw while locked is a 429 PIN_LOCKED. */
  pinLockedUntil: string | null;
  /**
   * The session user's accounts — REAL contract shapes: PascalCase types, and numbers
   * arrive ALREADY MASKED (`AB-****-****-90`) because AccountMapper.MaskAccountNumber
   * runs server-side; the full number never leaves the API.
   */
  accounts: MockAccount[];
  /** History feed, NEWEST FIRST like the real query orders it. */
  transactions: MockLedgerEntry[];
  /** Exact-match transfer-recipient directory. Looking up self returns exists:false. */
  recipients: MockRecipient[];
}

/** Seeded recipients — 'friend' backs the stepup.test contract; the rest are demo handles. */
function defaultRecipients(): MockRecipient[] {
  return [
    { azureTag: 'friend', displayName: 'A. Friend' },
    { azureTag: 'john_d', displayName: 'John D.' },
    { azureTag: 'anna_k', displayName: 'Anna K.' },
  ];
}

/** Named so the ledger seed and the account seed cannot drift apart on a copied literal. */
export const MAIN_ACCOUNT_ID = '019f7b3f-0000-7000-8000-0000000000a1';
export const SAVINGS_ACCOUNT_ID = '019f7b3f-0000-7000-8000-0000000000a2';

function defaultAccounts(): MockAccount[] {
  return [
    {
      id: MAIN_ACCOUNT_ID,
      accountNumber: 'AB-****-****-90',
      name: 'Main Account',
      type: 'Checking',
      balance: 1250.5,
      isPrimary: true,
      createdAt: '2026-07-01T09:00:00.0000000Z',
    },
    {
      id: SAVINGS_ACCOUNT_ID,
      accountNumber: 'AB-****-****-01',
      name: 'Rainy Day',
      type: 'Savings',
      balance: 830.0,
      isPrimary: false,
      createdAt: '2026-07-05T09:00:00.0000000Z',
    },
  ];
}

/**
 * The seeded ledger: 5 hand-written heroes exercising every type, a Pending and a Reversed status,
 * transfer counterparties and a null description — plus 20 fillers, so the cross-account list spans
 * TWO pages at the real page size of 20.
 *
 * **`balanceAfter` is DERIVED, not written.** It used to be typed in by hand and it reconciled with
 * nothing: the five heroes' balances did not follow from their own amounts, none of them landed on
 * either account's actual balance, and the fillers ran 1000, 1010, 1020... — a running balance that
 * grew as it went further into the past. Every row said "and then you had X" and every X was
 * fiction, which made it impossible to see whether the dashboard's per-account balance column was
 * right, since nothing it could have shown would have been.
 *
 * So the facts are declared here - which account, what type, how much, when - and the running
 * balance falls out of them, walking backwards from each account's CURRENT balance through
 * `signedDelta`. The newest entry on an account therefore lands exactly on that account's balance,
 * by construction rather than by arithmetic somebody has to redo by hand after every edit.
 */
type SeedEntry = Omit<MockLedgerEntry, 'balanceAfter'>;

/**
 * Re-dates the seed into the CURRENT calendar month, preserving the authored order exactly.
 *
 * The dates written in `seedEntries` are a RELATIVE ORDER SPECIFICATION, not absolute instants —
 * this converts them. Why it has to exist: the dashboard summarises the current UTC calendar month,
 * and the seed used to be hardcoded to July 2026. From 1 August every figure on that card read
 * €0.00, in the demo as well as in the tests, and `DashboardPage.test.tsx` went red and stayed red.
 * That is a time bomb with a monthly fuse, and it went off in CI three days after being written.
 *
 * WHY NOT "n days ago", which is the obvious answer: on the 1st of a month "3 days ago" lands in the
 * PREVIOUS month and the bug comes straight back, worse for being intermittent. Everything is
 * therefore anchored INSIDE the elapsed part of the current month — between the 1st and now — so
 * there is no day of the month on which the window can miss it.
 *
 * The one degenerate case is honest and bounded: in the first milliseconds of a month the elapsed
 * span is smaller than the number of entries, so several clamp onto the same instant and their
 * relative order stops being strict. They stay inside the window, which is the property that
 * matters; a demo opened in the first second of a month is not worth more machinery than that.
 */
function redateIntoCurrentMonth(entries: SeedEntry[]): SeedEntry[] {
  // Newest first, by the authored dates — this is the order being preserved.
  const ordered = [...entries].sort((a, b) => b.createdAt.localeCompare(a.createdAt));

  const now = Date.now();
  const today = new Date(now);
  const monthStart = Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1);
  const span = Math.max(now - monthStart, ordered.length);
  const step = Math.max(1, Math.floor(span / (ordered.length + 1)));

  return ordered.map((entry, rank) => {
    const at = new Date(Math.max(monthStart, now - (rank + 1) * step));
    const iso = at.toISOString();
    return {
      ...entry,
      createdAt: iso,
      // The number carries its own date, so it has to move with it or the two contradict each
      // other on screen — the part after the date is kept from the authored value.
      transactionNumber: `TXN-${iso.slice(0, 10).replace(/-/g, '')}-${entry.transactionNumber.split('-')[2]}`,
    };
  });
}

/*
  Transaction numbers here are 24 characters, the width the API actually mints
  (`TXN-yyyyMMdd-` + a 10-character Crockford suffix + a check symbol), so every screen renders
  them at production length — the detail row and the transfer receipt are the two places a longer
  string could wrap or clip, and a short fixture would hide that until production.

  What they deliberately are NOT is check-symbol-correct: the sequence stays a readable
  `…00000000101` rather than random base32. Reimplementing the mod-37 symbol in TypeScript would
  put a second copy of a backend algorithm here with nothing able to keep the two in step, and it
  would buy nothing — no frontend code parses or validates this value; the contract types it as a
  plain string. Length and shape are what the UI consumes, so length and shape are what is matched.
*/
function seedEntries(): SeedEntry[] {
  const heroes: SeedEntry[] = [
    {
      id: '019f7b3f-0000-7000-8000-000000000b01',
      accountId: MAIN_ACCOUNT_ID,
      transactionNumber: 'TXN-20260720-00000000101',
      type: 'Deposit',
      amount: 1250.5,
      description: 'Salary',
      recipientAzureTag: null,
      senderAzureTag: null,
      status: 'Completed',
      createdAt: '2026-07-20T09:15:00.0000000Z',
    },
    {
      id: '019f7b3f-0000-7000-8000-000000000b02',
      accountId: MAIN_ACCOUNT_ID,
      transactionNumber: 'TXN-20260720-00000000102',
      type: 'Withdrawal',
      amount: 50,
      description: null,
      recipientAzureTag: null,
      senderAzureTag: null,
      status: 'Completed',
      createdAt: '2026-07-20T18:30:00.0000000Z',
    },
    {
      id: '019f7b3f-0000-7000-8000-000000000b03',
      accountId: MAIN_ACCOUNT_ID,
      transactionNumber: 'TXN-20260719-00000000103',
      type: 'TransferOut',
      amount: 200,
      description: 'Dinner split',
      recipientAzureTag: 'john_d',
      senderAzureTag: null,
      status: 'Pending',
      createdAt: '2026-07-19T14:00:00.0000000Z',
    },
    {
      // On the savings account, so the two per-account feeds differ in KIND and not only in
      // length - scoping the dashboard to Rainy Day has to show something Main does not.
      id: '019f7b3f-0000-7000-8000-000000000b04',
      accountId: SAVINGS_ACCOUNT_ID,
      transactionNumber: 'TXN-20260719-00000000104',
      type: 'TransferIn',
      amount: 75,
      description: null,
      recipientAzureTag: null,
      senderAzureTag: 'anna_k',
      status: 'Completed',
      createdAt: '2026-07-19T10:00:00.0000000Z',
    },
    {
      id: '019f7b3f-0000-7000-8000-000000000b05',
      accountId: MAIN_ACCOUNT_ID,
      transactionNumber: 'TXN-20260718-00000000105',
      type: 'Withdrawal',
      amount: 30,
      description: 'ATM — disputed',
      recipientAzureTag: null,
      senderAzureTag: null,
      status: 'Reversed',
      createdAt: '2026-07-18T11:00:00.0000000Z',
    },
  ];
  // Split across both accounts, so neither per-account feed is a rounding error next to the other.
  const fillers: SeedEntry[] = Array.from({ length: 20 }, (_, i) => ({
    id: `019f7b3f-0000-7000-8000-000000000f${String(i).padStart(2, '0')}`,
    accountId: i % 2 === 0 ? MAIN_ACCOUNT_ID : SAVINGS_ACCOUNT_ID,
    transactionNumber: `TXN-20260710-${String(200 + i).padStart(11, '0')}`,
    type: 'Deposit' as const,
    amount: 10,
    description: `Top-up #${i + 1}`,
    recipientAzureTag: null,
    senderAzureTag: null,
    status: 'Completed' as const,
    // Descending days, so a larger `i` is always further in the past.
    createdAt: `2026-07-${String(10 - Math.floor(i / 3)).padStart(2, '0')}T12:${String(59 - i).padStart(2, '0')}:00.0000000Z`,
  }));
  return redateIntoCurrentMonth([...heroes, ...fillers]);
}

/**
 * Sorts newest-first and fills in the running balance per account.
 *
 * The sort is what makes "newest first" TRUE rather than asserted: the hand-ordered seed had its
 * two 20 July heroes the wrong way round (09:15 listed above 18:30) while claiming to be ordered,
 * and the real query is `OrderByDescending(t => t.CreatedAt)`.
 */
function withRunningBalance(entries: SeedEntry[], accounts: MockAccount[]): MockLedgerEntry[] {
  const ordered = [...entries].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const running = new Map(accounts.map((a) => [a.id, a.balance]));

  return ordered.map((entry) => {
    const balanceAfter = round2(running.get(entry.accountId) ?? 0);
    // Walking backwards in time: undo this entry to get the balance the PREVIOUS one left behind.
    running.set(entry.accountId, round2(balanceAfter - signedDelta(entry)));
    return { ...entry, balanceAfter };
  });
}

function defaultTransactions(): MockLedgerEntry[] {
  return withRunningBalance(seedEntries(), defaultAccounts());
}

/** The one seeded credential pair the mock login accepts. */
export const MOCK_USER: MockSessionUser = {
  id: '7c9e6679-7425-40de-944b-e07fc1f90ae7',
  email: 'demo@azurebank.dev',
  firstName: 'Demo',
  lastName: 'User',
  azureTag: 'demo_user',
  hasPin: true,
};
export const MOCK_PASSWORD = 'Password1!';
/** The one seeded PIN the mock verifies withdrawals (and step-up) against. */
export const MOCK_PIN = '123456';

/** Matches the BFF's base appsettings (30/60). Development runs 10/20; tests set what they need. */
export const MOCK_INACTIVITY_WINDOW_MS = 30 * 60_000;
export const MOCK_ABSOLUTE_WINDOW_MS = 60 * 60_000;

export const mockState: MockState = {
  idempotency: new Map(),
  authLevel: 1,
  session: null,
  staleSessionCookie: false,
  sessionInactivityWindowMs: MOCK_INACTIVITY_WINDOW_MS,
  sessionAbsoluteWindowMs: MOCK_ABSOLUTE_WINDOW_MS,
  sessionCreatedAt: 0,
  sessionLastActivity: 0,
  pin: MOCK_PIN,
  pinAttempts: 0,
  stepUpAuthorizations: new Map(),
  nextAccountSeq: 0,
  pinLockedUntil: null,
  loginFailures: {},
  loginLockedUntil: {},
  authCallTimes: [],
  accounts: defaultAccounts(),
  transactions: defaultTransactions(),
  recipients: defaultRecipients(),
};

/** Test helper: start authenticated without walking the login flow. */
export function seedMockSession(user: MockSessionUser = MOCK_USER): void {
  mockState.session = { ...user };
  // A fresh session means a fresh cookie: whatever was stale is not any more.
  mockState.staleSessionCookie = false;
  setMockSessionCookie(true);
  // And a fresh session is level 1: `SessionService.CreateSession` hardcodes it. A helper that
  // left an inherited 2 in place would hand a test elevation it never verified a PIN for.
  mockState.authLevel = 1;
  // A seeded session starts its clock now, exactly as a real login would. Leaving it at 0 would
  // hand every test a session that expired in 1970 and make the warning fire on the first tick.
  mockState.sessionCreatedAt = Date.now();
  mockState.sessionLastActivity = Date.now();
}

/** The BFF slides LastActivity on every cookie-bearing request EXCEPT the session-status probe. */
export const MOCK_SESSION_COOKIE = '.AzureBank.Session';

/** SPIKE: stamp/clear the cookie in environments that have a document (jsdom + dev:mock). */
export function setMockSessionCookie(present: boolean): void {
  if (typeof document === 'undefined') return;
  document.cookie = present
    ? `${MOCK_SESSION_COOKIE}=mock-session; Path=/`
    : `${MOCK_SESSION_COOKIE}=; Path=/; Max-Age=0`;
}

export function markMockActivity(): void {
  if (mockState.session) mockState.sessionLastActivity = Date.now();
}

/**
 * End the mock session if either deadline has passed, and report whether it is gone.
 *
 * Every authenticated handler must call this BEFORE marking activity. Without it the mock has no
 * expiry at all: `/bff/auth/me` would revive a session that died an hour ago simply by touching it,
 * and the status probe would keep answering `isAuthenticated: true` forever. A client that only
 * ever meets an immortal session cannot be tested against the one behaviour that matters here.
 *
 * The effective deadline is the EARLIER of the two rules, which is what the real BFF enforces.
 */
export function expireMockSessionIfDue(now: number = Date.now()): boolean {
  if (!mockState.session) return true;
  const due = Math.min(
    mockState.sessionLastActivity + mockState.sessionInactivityWindowMs,
    mockState.sessionCreatedAt + mockState.sessionAbsoluteWindowMs,
  );
  if (now >= due) {
    mockState.session = null;
    // Same reset the logout handler performs, and for the same reason: a dead session takes its
    // elevation with it. Clearing only `session` would let the NEXT seeded login start already
    // PIN-verified, so a test could walk past step-up without ever verifying a PIN — the gate
    // silently absent rather than visibly broken.
    mockState.authLevel = 1;
    // Expiry is SERVER-side; the browser still holds the cookie. See `staleSessionCookie`.
    mockState.staleSessionCookie = true;
    return true;
  }
  return false;
}

/**
 * What login and registration report as `expiresAt`: the ACCESS TOKEN's expiry.
 *
 * Not the session's absolute cap, which is a different rule with a different length —
 * `BffAuthController.Login` forwards `loginResponse.ExpiresAt` straight from the API, and the API's
 * `Jwt:ExpirationMinutes` is 15 against a session cap of 20 (Development) or 60 (base). Reporting
 * the cap here would make the two look like one value that happens to be configured twice, which is
 * exactly the kind of accidental invariant a later reader builds on.
 */
const MOCK_ACCESS_TOKEN_WINDOW_MS = 15 * 60_000;

export function mockAccessTokenExpiry(): string {
  return new Date(mockState.sessionCreatedAt + MOCK_ACCESS_TOKEN_WINDOW_MS).toISOString();
}

export function resetMockState(): void {
  mockState.idempotency.clear();
  mockState.stepUpAuthorizations.clear();
  mockState.authLevel = 1;
  mockState.session = null;
  mockState.staleSessionCookie = false;
  setMockSessionCookie(false);
  mockState.sessionInactivityWindowMs = MOCK_INACTIVITY_WINDOW_MS;
  mockState.sessionAbsoluteWindowMs = MOCK_ABSOLUTE_WINDOW_MS;
  mockState.sessionCreatedAt = 0;
  mockState.sessionLastActivity = 0;
  mockState.pin = MOCK_PIN;
  mockState.pinAttempts = 0;
  mockState.nextAccountSeq = 0;
  mockState.pinLockedUntil = null;
  /*
    The lockout and rate-limit counters MUST reset here, and forgetting them is not a small bug:
    they accumulate across every test in a file, so the eleventh login anywhere in that file starts
    answering 429 and the failure lands on whichever test happens to be eleventh. Caught exactly
    that way — the first assertion of the U3 test got a rate-limit 429 instead of a 401.
  */
  mockState.loginFailures = {};
  mockState.loginLockedUntil = {};
  mockState.authCallTimes = [];
  mockState.accounts = defaultAccounts();
  mockState.transactions = defaultTransactions();
  mockState.recipients = defaultRecipients();
}
