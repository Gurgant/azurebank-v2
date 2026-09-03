import { http, HttpResponse } from 'msw';
import { createOpenApiHttp } from 'openapi-msw';
import type { paths } from '../api/schema';
import type { AccountType } from '../api/enums';
import {
  bffProblem,
  fakeTraceId,
  invalidJsonValueProblem,
  missingMemberProblem,
  modelStateProblem,
  problem,
  unreadableBodyProblem,
} from './problem';
import { LOGIN_REQUEST, REGISTER_REQUEST, bindMembers, modelStateFor } from './dataAnnotations';
import {
  MOCK_PASSWORD,
  MOCK_SESSION_COOKIE,
  MOCK_USER,
  expireMockSessionIfDue,
  markMockActivity,
  mockAccessTokenExpiry,
  mockState,
  toWire,
  type MockSessionUser,
  type StoredStepUpAuthorization,
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

const NIL_UUID = '00000000-0000-0000-0000-000000000000';

/*
  `Guid.TryParse`, not "a UUID with hyphens".

  Both places this is used bind through `Guid.TryParse` on the server — `Guid? AccountId` on the two
  transaction filters, and `IdempotencyMiddleware.ReadKey`. That method accepts FIVE formats, and the
  mock accepted one, so it answered 400 to requests the API answers 200 to. Measured against the
  running stack rather than assumed: N, B, P and X all returned the correctly scoped total, and an
  N-format Idempotency-Key was accepted on a deposit.

  Nothing in this app sends anything but D — ids come from the API — so no client could reach the
  gap. It is fixed because a mock that is stricter than the server teaches a test the wrong contract,
  which is the entire failure this file's fidelity suite exists to prevent.
*/
// The backslashes are DOUBLED because these are template literals, not regex literals: `\(` inside
// a template literal is just `(`, which the RegExp then reads as a group opener. Written singly at
// first, and the P form silently became a capturing group — the format test caught it as a 400.
const HEX = '[0-9a-f]';
const GUID_D = `${HEX}{8}-${HEX}{4}-${HEX}{4}-${HEX}{4}-${HEX}{12}`;
const GUID_X =
  `\\{0x${HEX}{1,8},0x${HEX}{1,4},0x${HEX}{1,4},` + `\\{(?:0x${HEX}{1,2},){7}0x${HEX}{1,2}\\}\\}`;
const GUID_RE = new RegExp(
  `^(?:${HEX}{32}|${GUID_D}|\\{${GUID_D}\\}|\\(${GUID_D}\\)|${GUID_X})$`,
  'i',
);

/**
 * Parse to the CANONICAL form, or `null`.
 *
 * Returning the D-form matters as much as accepting the others: the empty-guid rejections compare
 * against `NIL_UUID`, and a nil key sent as 32 undashed zeroes would have slipped past a string
 * comparison the moment the other formats were allowed in. `Guid.TryParse` then `== Guid.Empty`
 * normalises first; so does this.
 */
function parseGuid(value: string): string | null {
  // Trimmed first, because `Guid.TryParse` is documented to allow leading and trailing white space
  // and does — measured: ` <guid> `, `<guid> ` and ` <guid>` each returned 200 with the correct
  // scoped total from the running API. Without this the mock 400s a request the server answers.
  const trimmed = value.trim();
  if (!GUID_RE.test(trimmed)) return null;
  const hex = trimmed.replace(/0x|[^0-9a-f]/gi, '').toLowerCase();
  if (hex.length !== 32) return null;
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

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
 * Used by the reveal handler alone: since ADR-0041 it is the only level-2 route.
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
 * The amount guard every money endpoint runs FIRST, like the framework's model-state pass does
 * before the controller ever sees the request.
 *
 * Named precisely, because it used to say "like FluentValidation does": FluentValidation does NOT
 * run before the controller here. `FluentValidation.AspNetCore` auto-validation is deliberately
 * absent, and each controller calls `ValidateAndThrowAsync` itself INSIDE the action. What runs
 * first is `[ApiController]` model state over the DTO's DataAnnotations — and for an amount that
 * is `[MoneyRange]`, which is also what rejects a bad amount first on the real API.
 *
 * A non-positive amount has to be rejected before any balance math, because the arithmetic is
 * symmetric and the intent is not: a negative deposit debits the account, and a negative
 * withdrawal or transfer credits it. The internal transfer was the only handler that said so; the
 * other three took `body.amount ?? 0` straight into the balance. One copy, four call sites, so
 * they cannot drift apart again.
 *
 * The bounds are the contract's own — `ValidationRules.TransactionMinAmount/MaxAmount`, which the
 * generated Zod schema mirrors as `.min(0.01).max(100000)`. The message quotes the server's
 * wording, invariant digits and the ISO code (`ValidationRules.DescribeAmount`), not the app's
 * EUR display.
 *
 * THE ENVELOPE, THE KEY AND THE WORDING WERE ALL WRONG, and are now measured. `[MoneyRange]` is a
 * DataAnnotation, so an out-of-range amount is a MODEL-STATE failure: framework envelope, key
 * `Amount` (the CLR property), and ONE message for both bounds — not the two the mock invented.
 * Observed 2026-08-04 on `POST /api/transactions/deposit`, for amounts 0, 0.005, 100000.01 and
 * 250000 — all four identical, and at that date still in dollars. THE SENTENCE CHANGED UNDER THE
 * MOCK: the server stopped rendering currency symbols (3769dc9, 2026-08-18) and this file kept
 * quoting the old one as observed for a fortnight. Re-measured 2026-09-03 for 500000, 1000000 and
 * 100000.01 — all three identical — with 100000 answering 201:
 *
 *   title "One or more validation errors occurred."
 *   {"Amount":["Amount must be between 0.01 EUR and 100000.00 EUR"]}
 *
 * Note there is NO trailing period on the wire, although `schema.d.ts`'s description carries one.
 * The OpenAPI description and the runtime message are different strings; the wire wins. The
 * contract suite now pins this sentence on the real stack (`money.contract.test.ts`), which is what
 * was missing while it drifted.
 *
 * A bad SCALE is the other envelope entirely — `[MoneyRange]` does not check decimals, so
 * `.ValidMoneyScale()` catches it inside the action and answers "Validation Failed" keyed
 * lowercase `amount`. Same field, two casings, decided by which layer rejected it.
 */
function amountErrors(amount: unknown): string[] {
  if (typeof amount !== 'number' || !Number.isFinite(amount) || amount < 0.01 || amount > 100_000) {
    return ['Amount must be between 0.01 EUR and 100000.00 EUR'];
  }
  return [];
}

function rejectBadAmount(amount: unknown): ReturnType<typeof modelStateProblem> | null {
  const errors = amountErrors(amount);
  return errors.length > 0 ? modelStateProblem({ Amount: errors }) : null;
}

/**
 * A query-string date that will not parse, rejected the way MODEL BINDING rejects it.
 *
 * `Date.parse('garbage')` is `NaN`, and NaN poisons every comparison silently: `NaN > NaN` is
 * false, so a range guard passes, and `at < NaN` / `at > NaN` are both false, so a filter includes
 * everything. The summary answered 200 with all-time totals and echoed the garbage straight back
 * as `fromDate` — the precise failure the window fix had just been written to prevent.
 *
 * The API never reaches its own code here: `[FromQuery] DateTime?` fails to bind and ASP.NET's
 * default `InvalidModelStateResponseFactory` answers first, with the framework's own sentence.
 *
 * IT KEYS BY THE BINDING NAME, AND THE TWO BINDING KINDS DIFFER IN BOTH KEY AND MESSAGE. That is
 * not a nicety — the mock got it wrong for a year and a fidelity test pinned the wrong answer.
 * Measured on the running stack (2026-08-04), twice each:
 *
 *   a filter PROPERTY  GET /api/transactions/summary?fromDate=garbage
 *     -> {"FromDate":["The value 'garbage' is not valid for FromDate."]}   PascalCase, suffixed
 *   an action PARAMETER  GET /api/accounts/{id}/balance?at=garbage
 *     -> {"at":["The value 'garbage' is not valid."]}                      camelCase, NO suffix
 *
 * So "model state means PascalCase" is a heuristic that breaks on every parameter-bound endpoint,
 * and the suffix is present only when the framework has a property name to name. `binding` is an
 * explicit argument rather than something inferred from the key's own casing, because inferring it
 * would silently do the right thing for the wrong reason the first time someone renames a param.
 *
 * The envelope is the one this mock already builds. The real model-binding failure carries the
 * framework's `ValidationProblemDetails` — title "One or more validation errors occurred.", type
 * `https://tools.ietf.org/html/rfc9110#section-15.5.1` (RFC 9110, which superseded 7231; this
 * comment said rfc7231 and was wrong), no detail — which differs from the app's
 * `ValidationExceptionHandler` ("Validation Failed", with a detail and an instance, keys always
 * camelCased). Modelling two 400 envelopes was not worth it for a difference no client branches
 * on; the key and the message, which one might, are exact.
 */
function unparseableDateErrors(
  raw: Record<string, string | null>,
  binding: 'property' | 'parameter',
): Record<string, string[]> {
  const errors: Record<string, string[]> = {};
  for (const [name, value] of Object.entries(raw)) {
    if (value !== null && Number.isNaN(Date.parse(value))) {
      errors[name] =
        binding === 'property'
          ? [`The value '${value}' is not valid for ${name}.`]
          : [`The value '${value}' is not valid.`];
    }
  }
  return errors;
}

/**
 * A binding-level value error, in the framework's exact wording.
 *
 * Returns the DICTIONARY rather than a response so callers can merge: ASP.NET validates every bound
 * property in ONE pass and reports them together, where the mock used to return the first failure
 * it met and hide the rest. Observed with three bad values at once:
 *   {"Page":["Page must be at least 1."],"PageSize":["PageSize must be between 1 and 100."],
 *    "AccountId":["The value 'notaguid' is not valid for AccountId."]}
 */
function invalidValueError(name: string, value: string): Record<string, string[]> {
  return { [name]: [`The value '${value}' is not valid for ${name}.`] };
}

/**
 * The ledger rows a caller can still SEE — those whose owning account has not been deleted.
 *
 * The API soft-deletes and filters on the flag in BOTH aggregate queries: `!a.IsDeleted` when it
 * resolves the caller's accounts for the list (`TransactionService.cs:174`) and
 * `!t.Account.IsDeleted` inside the summary (`:301`). The mock hard-removes the account row and
 * left its transactions behind, so the feed kept listing them and — the half that fails silently —
 * the totals kept counting them. A wrong number still renders; nothing errors.
 *
 * Scoped deliberately to those two queries. The single-transaction route is NOT filtered here:
 * whether the API 404s a row whose account was deleted is something I did not measure, and
 * guessing it would be inventing a contract rather than matching one.
 */
function visibleTransactions(): typeof mockState.transactions {
  const live = new Set(mockState.accounts.map((a) => a.id));
  return mockState.transactions.filter((t) => live.has(t.accountId));
}

/**
 * A transaction number for a row the mock is about to write, keyed only on the ledger's global
 * length so two rows can never share one.
 *
 * Each money handler used to add its own offset to that same length — 300 for deposits, 400 for
 * withdrawals, 500 and 600 for the two transfers — which reads as separate ranges but is not:
 * a withdrawal at length 0 and a deposit at length 100 both produce `400`. The backend puts a
 * UNIQUE index on this column, so the mock was the only place those two rows could coexist.
 *
 * 24 characters, matching the width the API mints, so every screen renders at production length.
 * The value is deliberately NOT check-symbol-correct — see the note in `state.ts` for why the
 * mod-37 symbol is not reimplemented here.
 */
function mockTransactionNumber(index: number): string {
  return `TXN-20260722-${String(1000 + index).padStart(11, '0')}`;
}

/**
 * An instant in the API's own wire format: `yyyy-MM-ddTHH:mm:ss.fffffffZ`.
 *
 * `Rfc3339DateTimeConverter` writes SEVEN fractional digits where JS's `toISOString()` writes
 * three, so the mock pads rather than inventing its own shape. The `z.iso.datetime()` schema would
 * accept either — the padding is for fidelity, not to satisfy the parser.
 */
function apiInstant(ms: number): string {
  return new Date(ms).toISOString().replace(/\.(\d{3})Z$/, '.$10000Z');
}

/** ASP.NET's `int` model binder accepts these and JS's `Number` does not agree with it. */
const INT32_MIN = -2_147_483_648;
const INT32_MAX = 2_147_483_647;

/**
 * Does this raw query value BIND to an `int`, the way ASP.NET's binder decides it?
 *
 * `Number()` is not that test and the difference is not academic — `Number('1e2')` is 100 and
 * `Number.isInteger(100)` is true, so a naive check accepts a value the API rejects outright. The
 * boundaries were measured on the running stack (2026-08-04), because guessing which exotic forms
 * .NET tolerates is exactly how this class of drift gets written:
 *
 *   ACCEPTED   ?Page=007  -> page 7      leading zeros
 *              ?Page=+5   -> page 5      leading sign
 *              ?Page=0x10 -> page 16     HEX, which surprised me and is why it was measured
 *   REJECTED   ?Page=1e2         -> "The value '1e2' is not valid for Page."
 *              ?Page=1.0         -> ditto — a decimal point is not an int
 *              ?Page=3000000000  -> ditto — Int32 overflow, not a range-annotation failure
 *
 * Returns null when it does not bind, which is the caller's cue to emit the BINDER message rather
 * than the `[Range]` one. Signed hex is deliberately not accepted: `Number('-0x10')` is NaN in JS
 * and the .NET side was never measured, so the mock does not invent an answer for it.
 */
function bindsAsInt32(raw: string): number | null {
  const text = raw.trim();
  const looksBindable = /^[+-]?\d+$/.test(text) || /^0[xX][0-9a-fA-F]+$/.test(text);
  if (!looksBindable) return null;
  const value = Number(text);
  if (!Number.isInteger(value) || value < INT32_MIN || value > INT32_MAX) return null;
  return value;
}

/**
 * The API's 401 for a request that reached it without a usable access token.
 *
 * Distinct from the BFF's own 401 and worth keeping distinct: this one carries
 * `errorCode: AUTH_TOKEN_MISSING`, an `instance`, and an rfc-ish `type`, where the BFF's carries no
 * errorCode at all. Observed verbatim on `/api/accounts`, `/api/transactions` and `/api/transfers`.
 */
function authTokenMissing(pathname: string) {
  return problem({
    status: 401,
    errorCode: 'AUTH_TOKEN_MISSING',
    title: 'Unauthorized',
    detail: 'Authentication is required to access this resource.',
    // Was passed through `extensions`, which appended it AFTER traceId. `Instance` is a declared
    // ProblemDetails member, so it serialises before every extension — this site had the right
    // member in the wrong place, which is why the audit's "every body omits instance" missed it.
    instance: pathname,
  });
}

/**
 * `Instance = httpContext.Request.Path` — the API's three handlers all set exactly this, so every
 * mock response that emulates one derives it the same way rather than hardcoding a route.
 *
 * Pathname only, and measured rather than assumed — `Request.Path` and `Request.QueryString` are
 * separate members, so the question is which one lands here:
 *
 *   GET /api/accounts/{unknown}/full-number?foo=1&bar=2
 *   -> "instance": "/api/accounts/00000000-0000-0000-0000-000000000009/full-number"
 *
 * The query string is absent, so `pathname` is right and `url.pathname + url.search` would be
 * wrong. Note the path is the SUBSTITUTED one, ids and all, not the route template.
 */
const pathOf = (request: Request) => new URL(request.url).pathname;

/**
 * `SessionActivityMiddleware`, which is MIDDLEWARE and therefore runs before everything a
 * controller does — routing, model binding, validation and the action body alike.
 *
 * That ordering is not a detail. It means a cookie-bearing request slides the clock even when it
 * is about to be rejected: a malformed body, a PIN that fails its regex, a 400 the action never
 * sees. The mock had these calls AFTER the body parse, so an active session that fumbled its
 * payload kept sliding in production and quietly aged out under MSW.
 *
 * Confirmed by the strongest available evidence — a request that reaches no controller at all:
 * `GET /bff/auth/no-such-route` answered 404 and STILL slid `inactivityExpiresAt` (measured
 * 2026-08-05, along with `/health`, which also 404s). If routing cannot stop it, binding cannot.
 *
 * Expiry first, then the slide, mirroring `UpdateActivity` -> `GetSession`: a session already past
 * its deadline is evicted and returns null, so there is nothing to slide and `markMockActivity`
 * no-ops. Every `/bff/auth/*` handler calls this FIRST except `session-status`, the one route the
 * middleware excludes.
 *
 * COOKIE-SCOPED, because the real middleware is. It acts only when
 * `context.Request.Cookies.TryGetValue(...)` succeeds, so a caller presenting no cookie leaves
 * another live session's clock alone. Keying on `mockState.session` instead — which is what this
 * did — meant an anonymous request extended whatever session happened to exist.
 *
 * The mock deliberately issues NO `Set-Cookie`, and that is the crux rather than an omission. MSW
 * keeps its own cookie store and REPLAYS it: after one Set-Cookie, a request the caller sent with
 * no cookie header still arrives carrying it, so anonymity becomes inexpressible and a check here
 * would be decoration. Measured, and MSW 2.12's `cookieStore` is a singleton over a `#private`
 * field with no clear or opt-out, so the replay cannot be disabled.
 *
 * The cookie therefore comes from the two places that do NOT feed that store:
 *
 *   jsdom + dev:mock   `document.cookie`, stamped by `seedMockSession` / login / register and
 *                      cleared by logout and `resetMockState`. Verified with the MSW store empty:
 *                      a SAME-ORIGIN request carries it, a cross-origin one does not — which is
 *                      why the unit suite's relative URLs matter.
 *   node (contract)    the client's own jar, seeded for the mock target in `client.ts`. There is
 *                      no `document` there, so `anonymous: true` genuinely sends nothing.
 *
 * Expiry runs before the slide, mirroring `UpdateActivity` -> `GetSession`: a session past its
 * deadline is evicted and returns null, so there is nothing to slide. Every `/bff/auth/*` handler
 * calls this FIRST except `session-status`, the one route the middleware excludes. */
function runSessionActivityMiddleware(request?: Request): void {
  // SPIKE: mirror `Cookies.TryGetValue` — no cookie, no slide.
  if (request && !(request.headers.get('cookie') ?? '').includes(MOCK_SESSION_COOKIE)) return;
  expireMockSessionIfDue();
  markMockActivity();
}

/** `IdempotencyConstants` — 32 KB, enforced by MIDDLEWARE before any key parsing or hashing. */
const IDEMPOTENCY_MAX_BODY_BYTES = 32 * 1024;

/**
 * 413 on an oversized body, and it comes FIRST — before the key is read, let alone hashed or
 * claimed. Measured 2026-08-06 with a 40 KB deposit body carrying a perfectly valid key:
 *
 *   413 {"title":"Payload Too Large","status":413,
 *        "detail":"The request body exceeds the 32 KB limit for this endpoint.",
 *        "instance":"/api/transactions/deposit","errorCode":"IDEMPOTENCY_PAYLOAD_TOO_LARGE"}
 *
 * Ordering is the contract, not a detail: a client that sends something huge with a REUSED key
 * must be refused without the key being consumed, or a retry would meet a claim it never made.
 */
function payloadTooLarge(raw: string, request: Request) {
  if (new TextEncoder().encode(raw).length <= IDEMPOTENCY_MAX_BODY_BYTES) return null;
  return problem({
    status: 413,
    errorCode: 'IDEMPOTENCY_PAYLOAD_TOO_LARGE',
    detail: 'The request body exceeds the 32 KB limit for this endpoint.',
    instance: pathOf(request),
  });
}

/** `ValidationRules.MaxLoginAttempts` / `LoginLockoutMinutes` — 5 and 15. */
const MAX_LOGIN_ATTEMPTS = 5;
const LOGIN_LOCKOUT_SECONDS = 15 * 60;

/** The BFF's `auth` policy: 10 permits per 60s, partitioned by IP. */
const AUTH_PERMIT_LIMIT = 10;
const AUTH_WINDOW_MS = 60_000;

/**
 * The BFF's per-IP rate limiter, which is MIDDLEWARE and therefore answers before the controller.
 *
 * Two 429s exist on the login route and they are NOT interchangeable — this one is the BFF's own,
 * the other (ACCOUNT_LOCKED) is the API's, forwarded. `instance` is the discriminator, and it is
 * measured, 2026-08-06:
 *
 *   rate limit    instance "/bff/auth/login"   Content-Type application/json
 *                 Retry-After: 60, Cache-Control: no-store, NO retryAfterSeconds in the body
 *   account lock  instance "/api/auth/login"   retryAfterSeconds 900 + lockedUntil, NO Retry-After
 *
 * So: the limiter sets a header and no body field; the lockout sets body fields and no header. The
 * mock could produce neither.
 */
function authRateLimited(request: Request): Response | null {
  const now = Date.now();
  mockState.authCallTimes = mockState.authCallTimes.filter((t) => now - t < AUTH_WINDOW_MS);
  if (mockState.authCallTimes.length >= AUTH_PERMIT_LIMIT) {
    return HttpResponse.json(
      {
        type: 'https://httpstatuses.com/429',
        title: 'Too Many Requests',
        status: 429,
        detail: 'Too many requests. Please retry later.',
        instance: pathOf(request),
        errorCode: 'RATE_LIMIT_EXCEEDED',
        traceId: fakeTraceId(),
      },
      {
        status: 429,
        headers: {
          // `application/json`, NOT problem+json — the limiter writes the body itself and does not
          // go through the ProblemDetails pipeline. Measured.
          'Content-Type': 'application/json',
          'Retry-After': String(AUTH_WINDOW_MS / 1000),
          'Cache-Control': 'no-store',
        },
      },
    );
  }
  mockState.authCallTimes.push(now);
  return null;
}

/**
 * The password lockout, which announces itself ONLY when the password is right.
 *
 * Measured 2026-08-06 on a throwaway user: five wrong passwords each answer 401
 * INVALID_CREDENTIALS with no hint that a counter is running — that is ADR-0013's
 * enumeration-neutral shape, not an oversight. The SIXTH request, with the CORRECT password,
 * is the one that reveals the lock:
 *
 *   429 {"detail":"Too many failed login attempts. Your account is temporarily locked; try again
 *        later.","instance":"/api/auth/login","errorCode":"ACCOUNT_LOCKED",
 *        "retryAfterSeconds":900,"lockedUntil":"2026-08-06T18:54:57.3924877+00:00"}
 *
 * No `Retry-After` HEADER: login is a BFF ACTION, so the body is forwarded by
 * `ForwardUpstreamError`, which copies no headers — the same asymmetry as the PIN lockout.
 */
/**
 * WHICH ACCOUNT, IF ANY, AN ADDRESS NAMES — and the two properties that follow from it.
 *
 * `AuthService.LoginAsync` starts at `FindByEmailAsync`, which matches Identity's
 * **NormalizedEmail**. Two consequences the mock has to model, both measured against the API
 * directly on :7215 (which, unlike the BFF, does not rate-limit login) on 2026-08-07:
 *
 *   1. NO ROW, NO COUNTER. An address nobody registered never reaches
 *      `IncrementAndMaybeLockLoginAsync`, so it can never be locked — there is nothing to lock.
 *
 *        ghost1593@azurebank.dev, eight wrong passwords
 *        -> 401 INVALID_CREDENTIALS every single time, no 429 at any point
 *
 *   2. THE LOOKUP IS CASE-INSENSITIVE, so every spelling shares ONE counter.
 *
 *        register casb2159@azurebank.dev
 *        CASB2159@AZUREBANK.DEV x5 wrong  -> 401 INVALID_CREDENTIALS
 *        casb2159@azurebank.dev, CORRECT  -> 429 ACCOUNT_LOCKED, retryAfterSeconds 900
 *
 *      The uppercase failures locked the lowercase account. A case-SENSITIVE mock would have
 *      answered 200 there, which is why this returns the canonical spelling to key state by
 *      rather than the raw input.
 *
 * Returning the account (not a boolean) is what keeps those two honest together: callers key
 * `loginFailures` / `loginLockedUntil` by the value this hands back, so a spelling can neither
 * open its own counter nor escape an existing lock.
 */
function accountForLogin(email: string | undefined): string | null {
  if (!email) return null;
  return email.toLowerCase() === MOCK_USER.email.toLowerCase() ? MOCK_USER.email : null;
}

function loginLockedProblem(email: string, now: number) {
  const until = mockState.loginLockedUntil[email];
  if (!until || Date.parse(until) <= now) return null;
  return problem({
    status: 429,
    errorCode: 'ACCOUNT_LOCKED',
    detail: 'Too many failed login attempts. Your account is temporarily locked; try again later.',
    instance: '/api/auth/login',
    extensions: {
      retryAfterSeconds: Math.ceil((Date.parse(until) - now) / 1000),
      lockedUntil: until,
    },
  });
}

/** `ValidationRules.PinLockoutMinutes` is 15. */
const PIN_LOCKOUT_SECONDS = 15 * 60;

/**
 * `DateTimeOffset.ToString("o")` — seven fractional digits and a numeric **`+00:00`** offset.
 *
 * Not `toISOString()`, which gives three digits and a `Z`. Distinct from `apiInstant()` above:
 * that one models `Rfc3339DateTimeConverter`, which writes `Z`. Two serialisers, two formats, and
 * `lockedUntil` goes through the DateTimeOffset one because `PinLockedException` puts a raw
 * `DateTimeOffset` in the exception's details rather than a converted DTO property.
 *
 * Measured 2026-08-05: "lockedUntil":"2026-08-05T12:10:14.3341758+00:00"
 */
function apiOffsetInstant(ms: number): string {
  return new Date(ms).toISOString().replace(/\.(\d{3})Z$/, '.$10000+00:00');
}

/**
 * The 429 both PIN paths produce — and the one thing they do NOT share.
 *
 * The body is the API's in both cases; what differs is how it reaches the browser:
 *
 *   POST /api/transactions/withdraw   YARP-PROXIED. Response headers are forwarded verbatim, so
 *                                     the `Retry-After` that `AppExceptionHandler` sets survives.
 *   POST /bff/auth/verify-pin         A BFF ACTION. `ForwardUpstreamError` is
 *                                     `StatusCode(status, body)` (BffAuthController.cs:670-703) —
 *                                     it copies no headers, so `Retry-After` is DROPPED.
 *
 * Measured 2026-08-05 on a throwaway user, because reaching a lockout costs three wrong PINs and
 * fifteen minutes, and the seeded admin account is what the e2e suite drives:
 *
 *   verify-pin -> 429, Retry-After ABSENT,  instance "/api/auth/pin/verify"
 *   withdraw   -> 429, Retry-After: 853,    instance "/api/transactions/withdraw"
 *   both       -> detail "Too many incorrect PIN attempts. Your PIN is temporarily locked; try
 *                 again later.", retryAfterSeconds 853, lockedUntil "…+00:00"
 *
 * `retryAfterSeconds` is recomputed per response (900 on the first, 853 later), so it is a
 * countdown and not a constant — a client rendering it is showing remaining time, not the window.
 *
 * `instance` names the path the API saw. For the proxied route that is the caller's path; for
 * verify-pin it is the API's own `/api/auth/pin/verify`, which the browser never requested.
 */
/**
 * BFF ACTIONS forward the upstream problem through `ForwardUpstreamError`, which is
 * `StatusCode(status, body)` — status and JSON body only, **never a header**. Read at the source,
 * so it holds for every action alike: no `Retry-After` survives that hop, and `instance` stays the
 * API's own path. Proxied routes keep the header because YARP copies it.
 */
const BFF_ACTION_INSTANCES: Record<string, string> = {
  'verify-pin': '/api/auth/pin/verify',
  'set-pin': '/api/auth/pin',
};

function pinLockedProblem(lockedUntil: string, now: number, source: 'verify-pin' | string) {
  const retryAfterSeconds = Math.ceil((Date.parse(lockedUntil) - now) / 1000);
  const actionInstance = BFF_ACTION_INSTANCES[source];
  const viaBffAction = actionInstance !== undefined;
  return problem({
    status: 429,
    errorCode: 'PIN_LOCKED',
    detail: 'Too many incorrect PIN attempts. Your PIN is temporarily locked; try again later.',
    instance: viaBffAction ? actionInstance : source,
    extensions: { retryAfterSeconds, lockedUntil },
    ...(viaBffAction ? {} : { headers: { 'Retry-After': String(retryAfterSeconds) } }),
  });
}

/**
 * The account-name and handle rules, which the mock enforced nowhere.
 *
 * Four dialogs — create-account, rename-account, rename-handle and deposit — each contain a branch
 * that reads `errorCode === 'VALIDATION_ERROR'`, walks `problem.errors` and maps the messages onto
 * their own form fields. **Not one of those branches had ever executed**, because the mock accepted
 * every body it was given: an empty name became "New Account", an unknown type was cast straight
 * through, and any handle was lower-cased and stored.
 *
 * So the code that turns a server's field errors into red text under the right input was written,
 * shipped, and never once run. Adding the rules here is what makes those branches reachable at all
 * — the alternative is a mock that can only ever exercise the happy path of the surfaces whose
 * whole job is the unhappy one.
 *
 * Bounds and wording are the validators' own: `Length(2, 100)` with
 * `AccountNameLengthMessage`, `IsInEnum` with its sentence, and the `AzureTagPattern` regex that
 * `[AzureTagQuery]` compiles.
 */
/**
 * Read a request body without letting the read itself decide the status code.
 *
 * Two separate traps, and the second was found only because the first was fixed badly.
 *
 * **`as { name?: string }` is a compile-time fiction.** `JSON.parse('null')` is `null` and
 * `JSON.parse('"x"')` is a string; reading `.name` off either throws. That much the previous
 * version guarded.
 *
 * **But the PARSE throws first, and MSW turns a thrown handler into a 500.** An empty body, or
 * `not json`, never reached the guard at all — measured: `POST /api/accounts` with a malformed
 * body answered **500** where the API answers 400, and so did deposit, withdraw and both
 * transfers. Fifteen handlers parsed a body and not one of them survived an unreadable one, so a
 * client could not distinguish "you sent nonsense" from "the server fell over".
 *
 * Returns the RAW text alongside the object because the money handlers fingerprint the exact bytes
 * for idempotency (`IdempotencyMiddleware` hashes bytes, not a re-serialisation) — reading it once
 * here keeps that byte-exact rather than round-tripping through `JSON.stringify`.
 */
async function readJsonBody(
  request: Request,
): Promise<{ raw: string; body: Record<string, unknown> } | null> {
  const raw = await request.clone().text();
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return null;
  }
  return { raw, body: parsed as Record<string, unknown> };
}

// `satisfies` rather than a bare array: `AccountType` is generated from the spec, so if the enum
// ever changes this stops being a hand-maintained list that can drift in silence.
const ACCOUNT_TYPES = ['Checking', 'Savings', 'Investment'] satisfies AccountType[];

/**
 * Parse an account type the way the API does: CASE-INSENSITIVELY, returning the canonical member.
 *
 * The mock used to demand an exact match and was therefore STRICTER than the server — a fixture
 * rejecting input the product accepts, which is the more insidious direction of drift because it
 * hides a working path instead of inventing a broken one. `StrictJsonStringEnumConverter` refuses
 * JSON *numbers*, but hands any non-empty string to `Enum.TryParse(..., ignoreCase: true)`.
 * Measured 2026-08-04: `POST /api/accounts {"name":"Case Probe","type":"checking"}` -> **201**.
 *
 * It returns the CANONICAL member rather than the caller's string, because the API persists
 * `Checking` for an input of `"checking"` — echoing the input back would drift the read model too.
 *
 * `typeof value !== 'string'` FIRST, and that guard is the point rather than ceremony: `['Checking']`
 * stringifies to `"Checking"`, so any comparison that coerces would accept the ARRAY and then store
 * it on the account, where the response contract promises a string. Same shape as the
 * `RegExp.test(['abc'])` trap documented on the azureTag handler below — a rule that was already
 * written down here and still did not stop the coercing version being written first.
 *
 * null means "no member matches", which is the `IsInEnum` case. Note `"99"` DOES parse on the real
 * side (TryParse accepts the numeric form) and is then rejected by the validator, so a null here
 * and a null there are the same 400 arrived at slightly differently.
 */
function parseAccountType(value: unknown): AccountType | null {
  if (typeof value !== 'string') return null;
  return ACCOUNT_TYPES.find((t) => t.toLowerCase() === value.toLowerCase()) ?? null;
}
const AZURE_TAG_RE = /^[a-z][a-z0-9_]{2,19}$/;

/*
  THE SERVER NO LONGER PUTS A FIGURE IN EITHER SENTENCE. Measured 2026-08-04 the API rendered `:C`
  on its process culture — "Insufficient funds. Available: $50,042.00, Requested: $99,000.00" and
  "Cannot delete account with non-zero balance. Current balance: $50,042.00" — and this file quoted
  both, then kept quoting them after 3769dc9 (2026-08-18) had removed the figures:
  `InsufficientFundsException` says "Insufficient funds." and carries `available`/`requested` as
  NUMERIC extensions (the client formats them in the user's locale), and
  `AccountService.DeleteAccountAsync` says "Cannot delete an account with a non-zero balance." —
  reworded, not merely shortened. `MoneyFormattingTests` forbids `:C` and currency symbols in Api
  and Shared, so a symbol cannot return without a red backend build. Re-measured 2026-09-03 (23:25Z)
  through the BFF; the four sites below and `money.contract.test.ts` quote that run.
*/

/**
 * A bad account name, in the framework's exact answer — which is NOT one message, and NOT keyed
 * the way this mock used to key it.
 *
 * The name is rejected by DataAnnotations, so `[ApiController]` model state answers before the
 * action and the key is the CLR property name: **`Name`**, not `name`. The mock said `name` and a
 * test pinned it, which mattered more than it looks — `toFieldName` exists precisely to absorb the
 * PascalCase form, and while the mock only ever emitted camelCase that branch was never exercised
 * under MSW.
 *
 * `[Required]` and `[StringLength]` are evaluated INDEPENDENTLY and reported together, so an empty
 * string returns TWO messages. `[Required]` trims, so whitespace-only fails it while still
 * satisfying a 2-character minimum. The required message differs per DTO: `CreateAccountRequest`
 * carries a bare `[Required]` (framework wording), `UpdateAccountRequest` overrides it.
 *
 * Measured 2026-08-04 (create / rename):
 *   {"name":""}    -> {"Name":["The Name field is required.","Account name must be between 2 and 100 characters."]}
 *                  -> {"Name":["Account name is required.","Account name must be between 2 and 100 characters."]}
 *   {"name":"  "}  -> {"Name":["The Name field is required."]}
 *   {"name":"x"}   -> {"Name":["Account name must be between 2 and 100 characters."]}
 */
const CREATE_NAME_REQUIRED = 'The Name field is required.';
const RENAME_NAME_REQUIRED = 'Account name is required.';

function rejectBadAccountName(name: unknown, requiredMessage: string): string[] | null {
  const messages: string[] = [];
  const text = typeof name === 'string' ? name : null;
  if (text === null || text.trim().length === 0) {
    messages.push(requiredMessage);
  }
  if (text !== null && (text.length < 2 || text.length > 100)) {
    messages.push('Account name must be between 2 and 100 characters.');
  }
  return messages.length > 0 ? messages : null;
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
function notFound(
  resource: 'Account' | 'Transaction' | 'Recipient',
  identifier: unknown,
  request: Request,
) {
  return problem({
    status: 404,
    errorCode: 'ACCOUNT_NOT_FOUND',
    detail: `${resource} with identifier '${String(identifier)}' was not found.`,
    instance: pathOf(request),
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
  const parsed = await readJsonBody(request);
  if (!parsed) {
    return response.untyped(unreadableBodyProblem(await request.clone().text()));
  }
  const { body } = parsed;

  /*
    ONE ENDPOINT, TWO ENVELOPES, AND THEY ARE MUTUALLY EXCLUSIVE.

    `Name` carries DataAnnotations, so `[ApiController]` model state rejects it BEFORE the action
    runs — PascalCase key, framework envelope, and the validator never executes. `Type` carries no
    annotation at all, so a bad type only surfaces later, from FluentValidation's `IsInEnum()` —
    camelCase key, "Validation Failed" envelope.

    They therefore never appear together. Measured 2026-08-04 with BOTH fields invalid:
      {"name":"x","type":"99"}
        -> title "One or more validation errors occurred.", errors {"Name":[…]}   ← `type` absent

    The mock used to merge both into one camelCase dictionary under the FluentValidation envelope,
    which is wrong in the key, the envelope and the co-occurrence. This is the endpoint that makes
    `toFieldName` necessary, so getting it right here is the whole point.
  */
  /*
    ABSENT is not the same as EMPTY, and this endpoint is where the mock used to accept a WRITE the
    API refuses. `CreateAccountRequest` declares BOTH members `required`, so System.Text.Json fails
    to deserialise before `[Required]` ever runs — the mock previously defaulted a missing `type` to
    Checking and answered 201, persisting a row the real API would never have created.

    Measured: {"name":"No Type Probe"} -> 400 keyed `$` + `request`. See `missingMemberProblem`.
  */
  const missing = (['name', 'type'] as const).filter((m) => body[m] === undefined);
  if (missing.length > 0) {
    return response.untyped(
      missingMemberProblem('AzureBank.Shared.DTOs.Account.CreateAccountRequest', ...missing),
    );
  }

  const nameError = rejectBadAccountName(body.name, CREATE_NAME_REQUIRED);
  if (nameError) {
    return response.untyped(modelStateProblem({ Name: nameError }));
  }
  /*
    A JSON NUMBER never reaches the validator. `StrictJsonStringEnumConverter` rejects the token in
    the deserialiser, so the answer is the framework's conversion envelope keyed by the path to the
    member — `$.type`, not the bare `$` an absent member produces, and not the camelCase `type` a
    bad enum STRING produces. Three inputs to the same field, three different answers.

    Measured: {"name":"Valid Name","type":12345}
      -> {"$.type":["Integer values are not allowed for enum 'AccountType'. Use string values: …"],
          "request":["The request field is required."]}
  */
  if (typeof body.type === 'number') {
    return response.untyped(
      invalidJsonValueProblem(
        '$.type',
        "Integer values are not allowed for enum 'AccountType'. Use string values: Checking, Savings, Investment",
      ),
    );
  }

  const accountType = parseAccountType(body.type);
  if (accountType === null) {
    // Measured: {"name":"Valid Name","type":"99"}
    //   -> {"title":"Validation Failed","detail":"One or more validation errors occurred.",
    //       "instance":"/api/accounts","errors":{"type":["Invalid account type. Must be …"]}}
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 400,
        errors: { type: ['Invalid account type. Must be Checking, Savings, or Investment.'] },
      }),
    );
  }

  /*
    A MONOTONIC sequence, not `accounts.length` — identity must survive a delete.

    Delete removes the row outright, so the length goes back DOWN and the next create reissues an
    identifier that is still in use: create, create, delete the first, create, and the third
    account came back wearing the second's id with both rows live. Every `find(a => a.id === …)`
    in this file then resolves to whichever sits first in the array, so a test that deletes before
    creating was silently operating on the wrong account.

    The masked number is drawn into 10..99 because that is the only shape the API can produce:
    `IdGenerator` builds `AB-{1000-9999}-{1000-9999}-{GetInt32(10,100)}` and `MaskAccountNumber`
    keeps the last TWO characters. The old `70 + index` reached three digits at index 30 — a
    masked number no server could ever emit. Masked values DO repeat after 90 accounts, and that
    is faithful rather than a defect: there are only 90 of them in the real system too.
  */
  const seq = mockState.nextAccountSeq++;
  const account = {
    // 12 HEX digits, so the final group stays a well-formed UUID node however far the counter
    // runs. The old `c${seq}` padded to three DECIMAL digits silently produced a 13-character
    // group — an invalid UUID — from the thousandth account on. Kept `c`-prefixed so a created
    // account is still distinguishable at a glance from the seeded `…a1` / `…b0n` rows.
    id: `019f7b3f-0000-7000-8000-c${seq.toString(16).padStart(11, '0')}`,
    accountNumber: `AB-****-****-${String(10 + (seq % 90)).padStart(2, '0')}`,
    // No casts: `rejectBadAccountName` has proven `name` is a string and `parseAccountType` has
    // returned a canonical member, so both narrow honestly instead of being asserted. Storing the
    // CANONICAL value matters — the API persists `Checking` for an input of `"checking"`, so
    // echoing the caller's casing back would drift the read model too.
    name: String(body.name),
    type: accountType,
    balance: 0,
    isPrimary: false,
    createdAt: '2026-07-21T12:00:00.0000000Z',
  };
  mockState.accounts.push(account);
  return response(201).json({ data: account, message: 'Account created successfully' });
});

/**
 * GET /api/accounts/{id} — the single account, enveloped like every other read.
 *
 * Had no handler at all while `getAccount`/`useGetAccountQuery` sat exported from the barrel. The
 * sentinel at the bottom of this file is what makes that impossible to repeat quietly.
 */
const getAccount = api.get('/api/accounts/{id}', ({ params, request, response }) => {
  const account = mockState.accounts.find((a) => a.id === routeGuid(params.id));
  if (!account) {
    return response.untyped(notFound('Account', routeGuid(params.id), request));
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
  // Model binding runs BEFORE the action, so a malformed `at` is a 400 and never reaches the
  // account lookup. Swallowing it as "current" — which is what `Number.isNaN(atMs)` used to do
  // below — answered 200 with today's balance for a question nobody could have asked.
  const at = new URL(request.url).searchParams.get('at');
  // `at` is an action PARAMETER (`[FromQuery] DateTime? at`), not a filter property — so the key
  // stays lowercase and the message carries no " for <Name>." suffix. Measured, see the helper.
  const badAt = unparseableDateErrors({ at }, 'parameter');
  if (Object.keys(badAt).length > 0) {
    return response.untyped(modelStateProblem(badAt));
  }

  const account = mockState.accounts.find((a) => a.id === routeGuid(params.id));
  if (!account) {
    return response.untyped(notFound('Account', routeGuid(params.id), request));
  }

  const atMs = at ? Date.parse(at) : Number.NaN;
  const now = new Date();

  if (!at || atMs >= now.getTime()) {
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
  /*
    THE BODY IS VALIDATED BEFORE THE ACCOUNT IS LOOKED UP, which is the opposite of the order the
    mock used. `[ApiController]` runs model state over `UpdateAccountRequest` before the action
    body executes, so a bad name is a 400 even when the id names nothing — the 404 is only
    reachable once the body is clean. The mock 404'd first, so a client fixing a rename against the
    mock could satisfy it and still be rejected in production, or vice versa.

    Measured 2026-08-04, unknown id in both cases:
      {"name":"x"}                    -> 400 {"Name":["Account name must be between 2 and 100 characters."]}
      {"name":"Perfectly Fine Name"}  -> 404 ACCOUNT_NOT_FOUND
  */
  const parsed = await readJsonBody(request);
  if (!parsed) {
    return response.untyped(unreadableBodyProblem(await request.clone().text()));
  }
  // `UpdateAccountRequest` overrides the required wording; the KEY is still the CLR property.
  // Measured: PATCH /api/accounts/{id} {"name":""}
  //   -> {"Name":["Account name is required.","Account name must be between 2 and 100 characters."]}
  const renameError = rejectBadAccountName(parsed.body.name, RENAME_NAME_REQUIRED);
  if (renameError) {
    return response.untyped(modelStateProblem({ Name: renameError }));
  }

  const account = mockState.accounts.find((a) => a.id === routeGuid(params.id));
  if (!account) {
    return response.untyped(notFound('Account', routeGuid(params.id), request));
  }
  account.name = parsed.body.name as string;
  return response(200).json({ data: account, message: 'Account updated successfully' });
});

/** PATCH /api/accounts/{id}/set-primary — exactly one primary at a time (A6). */
const setPrimaryAccount = api.patch(
  '/api/accounts/{id}/set-primary',
  ({ params, request, response }) => {
    const account = mockState.accounts.find((a) => a.id === routeGuid(params.id));
    if (!account) {
      return response.untyped(notFound('Account', routeGuid(params.id), request));
    }
    for (const a of mockState.accounts) {
      a.isPrimary = false;
    }
    account.isPrimary = true;
    return response(200).json({ message: 'Account set as primary' });
  },
);

/**
 * DELETE /api/accounts/{id} — the REAL business rules (AccountService): a 422
 * BusinessRuleException for non-zero balance or primary, else soft delete.
 */
const deleteAccount = api.delete('/api/accounts/{id}', ({ params, request, response }) => {
  const account = mockState.accounts.find((a) => a.id === routeGuid(params.id));
  if (!account) {
    return response.untyped(notFound('Account', routeGuid(params.id), request));
  }
  if (account.balance !== 0) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 422,
        errorCode: 'NON_ZERO_BALANCE',
        // No figure since 3769dc9 — the caller already holds the balance from GET /api/accounts
        // (AccountService.DeleteAccountAsync). Measured 2026-09-03, balance 16:
        //   422 NON_ZERO_BALANCE "Cannot delete an account with a non-zero balance."
        detail: 'Cannot delete an account with a non-zero balance.',
      }),
    );
  }
  if (account.isPrimary) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 422,
        errorCode: 'PRIMARY_ACCOUNT_DELETE',
        detail: 'Cannot delete primary account. Set another account as primary first.',
      }),
    );
  }
  mockState.accounts = mockState.accounts.filter((a) => a.id !== routeGuid(params.id));
  return response(200).json({ message: 'Account deleted successfully' });
});

/**
 * GET /api/accounts/{id}/full-number — PIN-gated reveal (ADR-0020). Mirrors the BFF ordering:
 * the level-2 gate first (403 step-up when not elevated), THEN ownership (404), then the full
 * unmasked number the API emits only here.
 */
const revealAccountNumber = api.get(
  '/api/accounts/{id}/full-number',
  ({ params, request, response }) => {
    if (mockState.authLevel < 2) {
      return response.untyped(stepUp403(mockState.authLevel));
    }
    const account = mockState.accounts.find((a) => a.id === routeGuid(params.id));
    if (!account) {
      return response.untyped(notFound('Account', routeGuid(params.id), request));
    }
    return response(200).json(
      {
        data: { accountId: account.id, accountNumber: unmaskForMock(account.accountNumber) },
        message: null,
      },
      {
        /*
          The one response in the system that carries an unmasked account number, and the only one
          the controller gives cache directives to (ASVS 14.3.2). Measured on the running stack,
          and measured on a NEIGHBOUR to be sure it is specific rather than global:

            GET /api/accounts/{id}/full-number -> Cache-Control: no-store, Pragma: no-cache
            GET /api/accounts                  -> neither header present

          It matters here and not only in production: this is exactly the kind of header a
          front-end change could silently drop — a `fetch` with the wrong `cache` mode, a service
          worker added later — and a mock that never sets it can never fail the test that notices.
        */
        headers: { 'Cache-Control': 'no-store', Pragma: 'no-cache' },
      },
    );
  },
);

/**
 * Query lookup that ignores case, because ASP.NET's model binder does.
 *
 * `URLSearchParams.get` is case-SENSITIVE, so the mock used to read `PageSize` and miss `pageSize`
 * entirely — falling back to the default page size and answering 200 where the real stack validated
 * the value and answered 400. Unreachable through the app (apiSlice sends PascalCase, matching the
 * spec) but reachable by anyone hand-writing a URL, and a divergence a mock has no business having.
 * Found by the contract gate, not by reading the code.
 */
function queryParam(params: URLSearchParams, name: string): string | null {
  const wanted = name.toLowerCase();
  for (const [key, value] of params) {
    if (key.toLowerCase() === wanted) return value;
  }
  return null;
}

/**
 * GET /api/transactions — T1, one of the two BARE responses (no envelope, by
 * contract): a PaginatedResponse with real page math, newest first.
 */
const listTransactions = api.get('/api/transactions', ({ request, response }) => {
  const params = new URL(request.url).searchParams;
  const page = Number(queryParam(params, 'Page') ?? 1);
  const pageSize = Number(queryParam(params, 'PageSize') ?? 20);
  const accountId = queryParam(params, 'AccountId');

  // `Number('')` is 0 and `Number('x')` is NaN, and either reached the page math: PageSize 0 makes
  // totalPages Infinity, and a NaN page slices to an empty list while the metadata says otherwise.
  // The real endpoint validates these; the mock has to, or a client could be built against
  // pagination that only the mock would ever produce.
  // `TransactionFilter` carries `[Range(1, int.MaxValue)]` on Page and `[Range(1, 100)]` on
  // PageSize, with those exact messages, and model validation keys the dictionary by PROPERTY
  // name. The mock invented a `pagination` key and a sentence no validator produces, and enforced
  // no upper bound at all — so a client could page 1000 rows at a time against the mock and be
  // rejected in production.
  /*
    TWO DIFFERENT FAILURES, TWO DIFFERENT SENTENCES, and the mock gave the `[Range]` one to both.

    `?Page=abc` never binds, so `[Range]` is never evaluated — the binder answers with its own
    template, which for a filter PROPERTY names the member. `?Page=0` binds fine and then fails the
    annotation. Measured 2026-08-04:

      ?Page=abc  ->  {"Page":["The value 'abc' is not valid for Page."]}
      ?Page=0    ->  {"Page":["Page must be at least 1."]}

    A consumer written against the mock would have looked for the range sentence on a typo'd query
    string and found something else entirely.
  */
  const pageErrors: Record<string, string[]> = {};
  const rawPage = queryParam(params, 'Page');
  const rawPageSize = queryParam(params, 'PageSize');
  if (rawPage !== null && bindsAsInt32(rawPage) === null) {
    Object.assign(pageErrors, invalidValueError('Page', rawPage));
  } else if (page < 1) {
    pageErrors.Page = ['Page must be at least 1.'];
  }
  if (rawPageSize !== null && bindsAsInt32(rawPageSize) === null) {
    Object.assign(pageErrors, invalidValueError('PageSize', rawPageSize));
  } else if (pageSize < 1 || pageSize > 100) {
    pageErrors.PageSize = ['PageSize must be between 1 and 100.'];
  }
  // NOTE: not returned yet — see the merge below. Every property-level failure is reported
  // together, so returning here would hide an AccountId or date error sitting alongside it.

  /*
    Every one of these is model-bound before the action body runs: `TransactionFilter` declares
    `Guid? AccountId`, `DateTime? FromDate`, `DateTime? ToDate` and two `[Range]` ints. So a
    malformed value of ANY of them is a 400 that the action never sees — which puts all of it
    above the service's own 403, not interleaved with it.

    The mock had the 403 in the middle: a request with both a foreign AccountId and an unparseable
    FromDate answered 403, where the API answers 400. Ordering was this PR's whole subject and the
    handler it added got it wrong in the same way.
  */
  const canonicalAccountId = accountId ? parseGuid(accountId) : null;
  /*
    ONE pass, all property-level errors together — measured, because the mock used to answer with
    whichever check happened to run first:
      ?Page=0&PageSize=1000&AccountId=notaguid
        -> {"Page":[...],"PageSize":[...],"AccountId":["The value 'notaguid' is not valid for AccountId."]}
      ?Page=0&FromDate=notadate
        -> {"Page":[...],"FromDate":["The value 'notadate' is not valid for FromDate."]}
  */
  const bindingErrors: Record<string, string[]> = {
    ...pageErrors,
    ...(accountId && !canonicalAccountId ? invalidValueError('AccountId', accountId) : {}),
    ...unparseableDateErrors(
      {
        FromDate: queryParam(params, 'FromDate'),
        ToDate: queryParam(params, 'ToDate'),
      },
      // `TransactionFilter` properties — PascalCase key, and the message names the property.
      'property',
    ),
  };
  if (Object.keys(bindingErrors).length > 0) {
    return response.untyped(modelStateProblem(bindingErrors));
  }

  // Ownership is the SERVICE's check, so it comes after everything the framework does. `AccountId`
  // used to be ignored outright, so both accounts returned byte-identical feeds and the dashboard's
  // scope control appeared to do nothing.
  if (canonicalAccountId && !mockState.accounts.some((a) => a.id === canonicalAccountId)) {
    return response.untyped(
      problem({
        instance: pathOf(request),
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
  const fromMs = queryParam(params, 'FromDate')
    ? Date.parse(queryParam(params, 'FromDate') as string)
    : null;
  const toMs = queryParam(params, 'ToDate')
    ? Date.parse(queryParam(params, 'ToDate') as string)
    : null;

  const ordered = [...visibleTransactions()]
    .filter((t) => !canonicalAccountId || t.accountId === canonicalAccountId)
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
  /*
    The order below is the SERVICE's order, and it is not the obvious one.

    Model binding first: `TransactionSummaryFilter` declares `Guid? AccountId` alongside the two
    dates, so a malformed value of any of them is a 400 the action never sees. Same reasoning, and
    the same trap, as the list handler above — which once answered 403 for a request that carried
    both a foreign AccountId and an unparseable date, where the API answers 400.

    Then the RESOLVED-window guard, which is a 422 — and it comes BEFORE the ownership check,
    because `GetSummaryAsync` resolves and validates the window first and only then asks whether
    the account is the caller's. A request with both an inverted window and a foreign account gets
    422, not 403.
  */
  // Parsed to the CANONICAL form immediately, and everything downstream uses that. Accepting the
  // other four formats and then comparing the RAW string against stored ids is the same mistake one
  // level along: the account exists, the id is valid, and the lookup misses — a 403 for an account
  // the caller owns. Caught by the format test the moment it was written.
  const rawSummaryAccountId = queryParam(params, 'AccountId');
  const summaryAccountId = rawSummaryAccountId ? parseGuid(rawSummaryAccountId) : null;
  /*
    Merged like the list, and with one ordering detail that only the real stack could show:
    `IValidatableObject.Validate` (the inverted-pair rule below) runs ONLY when property-level
    validation is clean. Measured — a bad AccountId together with an inverted window returns the
    AccountId error ALONE, never both.
  */
  const summaryBindingErrors: Record<string, string[]> = {
    ...(rawSummaryAccountId && !summaryAccountId
      ? invalidValueError('AccountId', rawSummaryAccountId)
      : {}),
    ...unparseableDateErrors(
      {
        FromDate: queryParam(params, 'FromDate'),
        ToDate: queryParam(params, 'ToDate'),
      },
      // `TransactionFilter` properties — PascalCase key, and the message names the property.
      'property',
    ),
  };
  if (Object.keys(summaryBindingErrors).length > 0) {
    return response.untyped(modelStateProblem(summaryBindingErrors));
  }

  const now = new Date();
  const monthStart = new Date(
    Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1, 0, 0, 0, 0),
  ).toISOString();
  const rawFrom = queryParam(params, 'FromDate') ?? monthStart;
  const rawTo = queryParam(params, 'ToDate') ?? now.toISOString();
  const fromMs = Date.parse(rawFrom);
  const toMs = Date.parse(rawTo);
  /*
    NORMALISED, never echoed. The API binds the filter to `DateTime?` and writes it back through
    `Rfc3339DateTimeConverter`, so what leaves the server is always a full instant — the caller's
    string never survives the round trip.

    This is not cosmetic. `getTransactionSummary` is one of the strict-schema endpoints, so
    `unwrap(response, transactionSummarySchema)` calls `schema.parse`, which THROWS. Echoing
    `?FromDate=2026-07-01` back as `"2026-07-01"` fails `z.iso.datetime()` and takes the dashboard
    down — under MSW only, which is the worst place for it to live.

    Measured: GET /api/transactions/summary?FromDate=2026-07-01
      -> {"fromDate":"2026-07-01T00:00:00.0000000Z","toDate":"2026-08-04T13:36:48.2114544Z"}
  */
  const fromDate = apiInstant(fromMs);
  const toDate = apiInstant(toMs);

  /*
    An inverted window has TWO answers, and the mock had one. Found by running the real stack:
    an explicitly-provided inverted PAIR is a 400 and a lone future FromDate is a 422.

    The split is where the check lives. `TransactionSummaryFilter` is an `IValidatableObject`, so
    the pair is caught by MODEL validation before the action runs, and the framework reports it as
    a field error on both members — `[nameof(FromDate), nameof(ToDate)]`, hence the same message
    twice. Only the RESOLVED window reaches the service, which is why a lone future FromDate (whose
    ToDate defaults to `now`) is the case that throws INVALID_DATE_RANGE.

    The mock answered 422 to both, and the backend's own integration test
    (`Summary_WithInvertedExplicitRange_ReturnsBadRequest`) had been saying 400 since it was
    written. The fidelity test that asserted 422 for the pair was pinning the mock's mistake.
  */
  if (queryParam(params, 'FromDate') && queryParam(params, 'ToDate') && fromMs > toMs) {
    const inverted = 'FromDate must be earlier than or equal to ToDate.';
    return response.untyped(modelStateProblem({ ToDate: [inverted], FromDate: [inverted] }));
  }

  // The service's own guard, on the RESOLVED window — reachable only when a bound was DEFAULTED,
  // since the pair is already gone above.
  if (fromMs > toMs) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 422,
        errorCode: 'INVALID_DATE_RANGE',
        detail: 'FromDate must be earlier than or equal to ToDate.',
      }),
    );
  }

  // Ownership: the service's own check, so it lands after everything the framework does AND after
  // the window guard. Unknown and foreign ids are the same refusal here as there — the API cannot
  // tell them apart on purpose, and a mock that could would let a test pass against a contract the
  // server does not offer.
  if (summaryAccountId && !mockState.accounts.some((a) => a.id === summaryAccountId)) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 403,
        errorCode: 'ACCESS_DENIED',
        detail: 'You do not have access to this account.',
      }),
    );
  }

  let totalIncome = 0;
  let totalExpenses = 0;
  let pendingCount = 0;
  for (const t of visibleTransactions()) {
    if (summaryAccountId && t.accountId !== summaryAccountId) {
      continue;
    }
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
const getTransaction = api.get('/api/transactions/{id}', ({ params, request, response }) => {
  const transaction = mockState.transactions.find((t) => t.id === routeGuid(params.id));
  if (!transaction) {
    return response.untyped(notFound('Transaction', routeGuid(params.id), request));
  }
  return response(200).json({ data: toWire(transaction), message: null });
});

/** POST /api/transactions/deposit — the stateful idempotency protocol (ADR-0009). */
const deposit = api.post('/api/transactions/deposit', async ({ request, response }) => {
  // Middleware, so it precedes the key checks below. See `payloadTooLarge`.
  const oversized = payloadTooLarge(await request.clone().text(), request);
  if (oversized) return response.untyped(oversized);
  const key = request.headers.get('Idempotency-Key');
  if (!key) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_MISSING',
        detail: "The 'Idempotency-Key' header is required on this endpoint.",
      }),
    );
  }
  // Parsed ONCE, and the parsed value is what the store is keyed by. Keying on the raw header
  // would give `<guid>` and its undashed spelling two separate entries, so the same logical key
  // could execute a deposit twice — the backend stores a parsed `Guid`, so it replays. Accepting
  // the alternate formats is what made that reachable; this is the third place in this file where
  // "accept the format, then use the raw string" was the bug.
  const parsedKey = parseGuid(key);
  if (parsedKey === null || parsedKey === NIL_UUID) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_INVALID',
        detail: "The 'Idempotency-Key' header must be a valid UUID.",
      }),
    );
  }

  const parsedBody = await readJsonBody(request);
  if (!parsedBody) {
    return response.untyped(unreadableBodyProblem(await request.clone().text()));
  }
  const raw = parsedBody.raw;
  /*
    Fingerprint the WIRE BYTES, and do it BEFORE `bindAccountIds` rewrites the parsed body.

    `IdempotencyService.ComputeRequestHashAsync(Stream body, …)` HMACs the raw stream, so two bodies
    differing only in a GUID's casing legitimately hash differently on the server — and must here.
    Moving this below the bind, or switching it to `JSON.stringify(body)`, would make the mock replay
    as one request a pair the API answers with 422 IDEMPOTENCY_KEY_REUSE.
  */
  const fp = fingerprint(raw);
  const stored = mockState.idempotency.get(`deposit|${parsedKey}`);
  if (stored) {
    if (stored.bodyFingerprint !== fp) {
      return response.untyped(
        problem({
          instance: pathOf(request),
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

  const body = parsedBody.body as { accountId?: string; amount?: number; description?: string };
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
    return response.untyped(notFound('Account', body.accountId, request));
  }
  const newBalance = account.balance + amount;
  account.balance = newBalance;
  const index = mockState.transactions.length;
  const transaction = {
    // 0xd00 block (12 hex chars = a VALID uuid) — same scheme as withdraw/transfer below.
    id: `019f7b3f-0000-7000-8000-${(0xd00 + index).toString(16).padStart(12, '0')}`,
    accountId: account.id,
    transactionNumber: mockTransactionNumber(index),
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
    message: 'Deposit successful',
  };
  const text = JSON.stringify(payload);
  mockState.idempotency.set(`deposit|${parsedKey}`, {
    bodyFingerprint: fp,
    status: 201,
    body: text,
  });

  // Round-trip through the TYPED helper so the success shape is compile-checked against
  // schema.d.ts (the stored string above replays byte-identically on retries).
  return response(201).json(payload);
});

/**
 * POST /api/transactions/withdraw — the deposit protocol PLUS the PIN-in-body gate (D1).
 * NOT step-up: the PIN travels in the request body and is verified here, so this endpoint
 * never returns the 403 STEP_UP_REQUIRED shape (since ADR-0041 only the account-number reveal
 * does; no money move is gated at level 2 any more). Failure order mirrors the backend
 * (TransactionService.WithdrawAsync): idempotency → PIN_REQUIRED →
 * PIN_LOCKED → INVALID_PIN → INSUFFICIENT_FUNDS → success. Side effects (balance debit,
 * transaction, stored idempotency record) run ONLY on the success path.
 */
const withdraw = api.post('/api/transactions/withdraw', async ({ request, response }) => {
  // Middleware, so it precedes the key checks below. See `payloadTooLarge`.
  const oversized = payloadTooLarge(await request.clone().text(), request);
  if (oversized) return response.untyped(oversized);
  const key = request.headers.get('Idempotency-Key');
  if (!key) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_MISSING',
        detail: "The 'Idempotency-Key' header is required on this endpoint.",
      }),
    );
  }
  // Parsed ONCE, and the parsed value is what the store is keyed by. Keying on the raw header
  // would give `<guid>` and its undashed spelling two separate entries, so the same logical key
  // could execute a deposit twice — the backend stores a parsed `Guid`, so it replays. Accepting
  // the alternate formats is what made that reachable; this is the third place in this file where
  // "accept the format, then use the raw string" was the bug.
  const parsedKey = parseGuid(key);
  if (parsedKey === null || parsedKey === NIL_UUID) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_INVALID',
        detail: "The 'Idempotency-Key' header must be a valid UUID.",
      }),
    );
  }

  const parsedBody = await readJsonBody(request);
  if (!parsedBody) {
    return response.untyped(unreadableBodyProblem(await request.clone().text()));
  }
  const raw = parsedBody.raw;
  /*
    Fingerprint the WIRE BYTES, and do it BEFORE `bindAccountIds` rewrites the parsed body.

    `IdempotencyService.ComputeRequestHashAsync(Stream body, …)` HMACs the raw stream, so two bodies
    differing only in a GUID's casing legitimately hash differently on the server — and must here.
    Moving this below the bind, or switching it to `JSON.stringify(body)`, would make the mock replay
    as one request a pair the API answers with 422 IDEMPOTENCY_KEY_REUSE.
  */
  const fp = fingerprint(raw);
  const stored = mockState.idempotency.get(`withdraw|${parsedKey}`);
  if (stored) {
    if (stored.bodyFingerprint !== fp) {
      return response.untyped(
        problem({
          instance: pathOf(request),
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

  const body = parsedBody.body as {
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
  /*
    ONE PIN PATH IN THIS FILE, not two.

    Withdraw carried its own inline copy of the enrolment/lock/compare ladder, written before the
    transfers needed one. When ADR-0041 added the model-binding gate + `checkPinInBand` for transfers,
    wiring them into the transfer routes only would have left this endpoint with a DIFFERENT PIN
    behaviour in the same file — and the shape hole the gate exists to close (a malformed pin
    answering 401 and burning a lockout attempt) would have survived here. `WithdrawRequest` carries
    the same `[Required] [Pin] required string Pin`, so it gets the same treatment.
  */
  const pinBind = transferPinBindFailure(body, 'AzureBank.Shared.DTOs.Transaction.WithdrawRequest');
  if (pinBind) return response.untyped(pinBind);
  const pinAnnotations = pinAnnotationErrors((body.pin ?? null) as string | null);
  if (pinAnnotations.length > 0) {
    return response.untyped(modelStateProblem({ Pin: pinAnnotations }));
  }

  const account = mockState.accounts.find((a) => a.id === body.accountId);
  if (!account) {
    return response.untyped(notFound('Account', body.accountId, request));
  }

  const pinRefusal = checkPinInBand(
    body.pin,
    request,
    'PIN must be set before making withdrawals.',
  );
  if (pinRefusal) return response.untyped(pinRefusal);

  // INSUFFICIENT_FUNDS — last, after the PIN passes, like the backend orders it.
  const available = account.balance;
  if (amount > available) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 422,
        errorCode: 'INSUFFICIENT_FUNDS',
        // No figures in the sentence since 3769dc9: both amounts travel as the numeric
        // `available`/`requested` extensions below. Measured 2026-09-03 (balance 6, amount 10):
        //   422 {"detail":"Insufficient funds.","errorCode":"INSUFFICIENT_FUNDS",
        //        "available":6.0000,"requested":10}
        detail: 'Insufficient funds.',
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
    transactionNumber: mockTransactionNumber(index),
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
  mockState.idempotency.set(`withdraw|${parsedKey}`, {
    bodyFingerprint: fp,
    status: 201,
    body: text,
  });
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
 * PATCH /bff/auth/azuretag — rename the caller's own public handle (ADR-0015). Mirrors the service:
 * normalize to lower-case, 409 AZURE_TAG_TAKEN when another user already holds it (our seeded
 * recipients stand in for "other users"), otherwise update the session and echo the new tag.
 *
 * MOVED off the proxied `/api/users/me/azuretag`, and the reason is a drift this mock was on the
 * RIGHT side of. `mockState.session.azureTag = tag` below has always propagated the rename to the
 * session — the real BFF did not, because a proxied PATCH runs no BFF code and `/bff/auth/me` serves
 * the cached session. So every unit test here was green against a mock that behaved BETTER than the
 * product. The contract suite did not catch it either: its rename test pins the VALIDATION envelope,
 * not the propagation.
 *
 * Untyped `http.patch` rather than the openapi-msw `api.patch`, because `/bff/*` is the BFF's own
 * surface and has no OpenAPI document behind it (ADR-0023 names that the weakest boundary here). The
 * type safety lost on this one route is the price of the route being on the side that owns the
 * session.
 */
const renameAzureTag = http.patch('*/bff/auth/azuretag', async ({ request }) => {
  const parsed = await readJsonBody(request);
  if (!parsed) {
    return unreadableBodyProblem(await request.clone().text());
  }
  const body = parsed.body;

  /*
    `[AzureTagQuery]` is model validation, so it runs before the controller and therefore before the
    taken-handle conflict. The mock lower-cased whatever arrived and accepted it, which meant
    RenameAzureTagDialog's field-error branch could never fire — and a handle the API would refuse
    looked available here.

    The `typeof` is not defensive noise. `RegExp.test` stringifies its argument, so
    `AZURE_TAG_RE.test(['abc'])` tests the string "abc", passes, and the ARRAY is what gets stored
    on the session. The type annotation that used to sit here promised a string and checked
    nothing.
  */
  const rawTag = body?.azureTag;
  const tag = typeof rawTag === 'string' ? rawTag : '';
  if (!AZURE_TAG_RE.test(tag)) {
    /*
      The FRAMEWORK envelope, keyed `AzureTag` — this route can produce no other. There is no
      `UpdateAzureTagRequest` validator anywhere under Validators/ and `UserController` injects
      none, so the only thing that rejects the handle is `[AzureTagQuery]`, a DataAnnotation, and
      `[ApiController]` answers before the action runs. The mock used the FluentValidation envelope
      with a camelCase key, i.e. the one shape this endpoint CANNOT emit.

      Measured 2026-08-04, PATCH /api/users/me/azuretag {"azureTag":"Bad Tag!"}:
        {"title":"One or more validation errors occurred.","status":400,
         "errors":{"AzureTag":["AzureTag must start with a letter and contain only lowercase
                                letters, numbers, and underscores."]}}
    */
    return modelStateProblem({
      AzureTag: [
        'AzureTag must start with a letter and contain only lowercase letters, numbers, and underscores.',
      ],
    });
  }

  /*
    SESSION BEFORE CONFLICT, and the order is the security-relevant part.

    Measured on the running BFF, anonymously:
      valid tag                     -> 401
      a tag that IS taken ("admin") -> 401   <- NOT 409
      invalid tag                   -> 400   <- model validation still answers first

    So production is validation -> session -> conflict. An earlier draft here checked the conflict
    FIRST, which would have answered 409 for a taken handle and 401 for a free one — telling an
    anonymous caller which handles exist. That is precisely the oracle ADR-0013 and ADR-0014 are
    written to deny, rebuilt inside the mock that is supposed to model them.

    The body is the BFF's own, copied off the wire rather than from the API's vocabulary:
      {"title":"Unauthorized","status":401,"detail":"Session expired or invalid"}
    No errorCode and no instance — BffAuthController.RenameAzureTag returns a bare ProblemDetails,
    and the AUTH_TOKEN_MISSING used here before is the API's code for a different response.
  */
  if (!mockState.session) {
    return HttpResponse.json(
      { title: 'Unauthorized', status: 401, detail: 'Session expired or invalid' },
      { status: 401, headers: { 'Content-Type': 'application/problem+json' } },
    );
  }

  if (mockState.recipients.some((r) => r.azureTag === tag)) {
    return problem({
      instance: pathOf(request),
      status: 409,
      errorCode: 'AZURE_TAG_TAKEN',
      detail: 'That handle is already taken.',
    });
  }

  mockState.session.azureTag = tag;
  return HttpResponse.json({ data: { azureTag: tag }, message: 'AzureTag updated' });
});

/**
 * The `[Required]` + `[Pin]` DataAnnotations gate for the transfer DTOs.
 *
 * It fires during MODEL BINDING in the API — before the action, before the service, and therefore
 * before any PIN is verified or any lockout attempt counted. `checkPinInBand` below must never see
 * a value this rejects, or the mock answers 401 INVALID_PIN and burns a lockout attempt where the
 * API answers 400 and burns nothing.
 *
 * Measured on the real API and pasted verbatim (a same-account internal transfer, varying only the
 * pin member):
 *
 *   absent  -> 400 {"title":"One or more validation errors occurred.",
 *                   "errors":{"$":["JSON deserialization for type '…InternalTransferRequest' was
 *                                   missing required properties including: 'pin'."],
 *                             "request":["The request field is required."]}}
 *   "12"    -> 400 {"errors":{"Pin":["PIN must be exactly 6 digits."]}}
 *   ""      -> 400 {"errors":{"Pin":["The Pin field is required.","PIN must be exactly 6 digits."]}}
 *
 * PascalCase `Pin`, unlike FluentValidation's camelCase `toAccountId` next door: the key follows how
 * the value was BOUND (PR #75's rule), and these two envelopes coexist on the same endpoint.
 */
function transferPinBindFailure(body: { pin?: unknown }, clrType: string): Response | null {
  if (body.pin === undefined) {
    return missingMemberProblem(clrType, 'pin');
  }

  // null fires [Required] ALONE; "" and whitespace fire Required AND the format rule.
  // Measured on this DTO (an earlier revision inferred it from the auth DTOs' documented behaviour;
  // it is now observed): {"pin":null} -> {"Pin":["The Pin field is required."]}
  /*
    A NON-STRING pin never reaches DataAnnotations at all — System.Text.Json fails the conversion
    first, and the answer is keyed by JSON PATH, not by property name:

      {"pin":123456} -> {"request":["The request field is required."],
                         "$.pin":["The JSON value could not be converted to System.String.
                                   Path: $.pin | LineNumber: 0 | BytePositionInLine: 111."]}
      {"pin":true}   -> same shape

    This mattered more than it looks. The previous revision coerced with `String(body.pin)`, and
    `String(123456)` is "123456" — which matches the six-digit rule, so the MOCK ACCEPTED a payload
    the API refuses outright. The byte position is deliberately not imitated: it moves with every
    payload, and this file's convention (see `unreadableBodyProblem`) is that the KEY and the
    ENVELOPE are what a client branches on.
  */
  // NULL is not a conversion failure: JSON null maps onto a C# null string without complaint, and
  // `[Required]` is what rejects it — measured, {"pin":null} -> {"Pin":["The Pin field is
  // required."]}. Only a non-null non-string aborts the bind.
  if (body.pin !== null && typeof body.pin !== 'string') {
    return invalidJsonValueProblem(
      '$.pin',
      'The JSON value could not be converted to System.String. Path: $.pin',
    );
  }

  return null;
}

/**
 * The `[Required]` / `[Pin]` DataAnnotations on a pin that DID bind — the other half.
 *
 * Split from the function above because the two failure kinds compose differently, and that is a
 * property of the framework rather than a convenience here: a deserialisation failure ABORTS the
 * bind, so no other member is validated and its answer can never carry a second field; annotation
 * failures are collected in one pass and DO appear alongside `Amount`. Returning messages rather
 * than a Response is what lets the caller merge them.
 *
 * Measured: `null` -> ["The Pin field is required."] alone (DataAnnotations skip non-Required
 * validators on null); `""` -> both messages; `"12"` -> the format one.
 */
function pinAnnotationErrors(pin: string | null): string[] {
  const errors: string[] = [];
  // null fires [Required] ALONE — DataAnnotations skip non-Required validators on null but run
  // them all on "". Measured on this DTO, not inferred from the auth ones.
  if (pin === null) {
    return ['The Pin field is required.'];
  }
  if (pin.trim() === '') {
    errors.push('The Pin field is required.');
  }
  if (!/^[0-9]{6}$/.test(pin)) {
    errors.push('PIN must be exactly 6 digits.');
  }
  return errors;
}

/**
 * The in-band PIN check, shared by withdraw and (since ADR-0041) both transfer endpoints.
 *
 * Order and values mirror the API, measured against it rather than copied from the withdraw
 * handler that inspired it — TransferPinVerificationTests records the run:
 *
 *   no PIN enrolled -> 422 PIN_REQUIRED · locked -> 429 PIN_LOCKED · wrong -> 401 INVALID_PIN
 *
 * Lock is checked BEFORE the compare, because the backend refuses before Argon2id runs, and the
 * third consecutive miss trips the lock and answers 429 rather than 401.
 *
 * Returns a Response to send, or null when the PIN passes.
 */
function checkPinInBand(
  pin: string | undefined,
  request: Request,
  detailWhenUnset: string,
): Response | null {
  if (mockState.session && !mockState.session.hasPin) {
    return problem({
      instance: pathOf(request),
      status: 422,
      errorCode: 'PIN_REQUIRED',
      detail: detailWhenUnset,
    });
  }

  const now = Date.now();
  if (mockState.pinLockedUntil && Date.parse(mockState.pinLockedUntil) > now) {
    return pinLockedProblem(mockState.pinLockedUntil, now, pathOf(request));
  }

  if (pin !== mockState.pin) {
    mockState.pinAttempts += 1;
    if (mockState.pinAttempts >= 3) {
      mockState.pinAttempts = 0;
      mockState.pinLockedUntil = apiOffsetInstant(now + PIN_LOCKOUT_SECONDS * 1000);
      return pinLockedProblem(mockState.pinLockedUntil, now, pathOf(request));
    }
    return problem({
      instance: pathOf(request),
      status: 401,
      errorCode: 'INVALID_PIN',
      detail: 'Invalid PIN.',
    });
  }

  mockState.pinAttempts = 0;
  return null;
}

/*
  ============================================================================================
  STEP-UP AUTHORISATIONS (ADR-0042)
  ============================================================================================

  Every status and errorCode below was MEASURED against the running API on 2026-08-16 and is quoted
  beside the branch that produces it. Full transcript with bodies:
  `azurebank-work/plans/step-up-and-audit/A2-PR2-MEASURED-CONTRACT.md`.

  Two off-by-ones the mock must NOT invent, both measured:
    - the lock lands ON the third wrong PIN, not after it (checkPinInBand already does this);
    - an unknown recipient is ACCOUNT_NOT_FOUND, not a dedicated recipient code.

  No Idempotency-Key on either endpoint, deliberately (apiSlice.ts): minting moves no money, so
  there is nothing to deduplicate. It DOES cost a PIN attempt, which is why both call
  `checkPinInBand` — minting is the authentication event and must not be a cheaper oracle than the
  transfer itself.
*/

/** The window the real server uses (StepUpOptions.Window). Two minutes, no refresh. */
const STEP_UP_WINDOW_MS = 2 * 60 * 1000;

/**
 * The wire name, spelled out here and NOT imported from `apiSlice`.
 *
 * Deliberate duplication: the mock stands in for the SERVER, and a constant shared with the client
 * would rename both halves at once — leaving every test green while the real API, which knows only
 * `Step-Up-Authorization`, stopped receiving anything. Two independent spellings is what makes a
 * rename fail here.
 */
const STEP_UP_HEADER = 'Step-Up-Authorization';

/**
 * Read the `Step-Up-Authorization` header the way MVC binds it: PARSE it, do not merely shape-check
 * it, and hand back the CANONICAL id so the lookup cannot miss on formatting alone.
 *
 * `[FromHeader(Name = "Step-Up-Authorization")] Guid?` goes through `Guid.TryParse`, which accepts
 * all five .NET formats and trims surrounding whitespace. The first draft here tested a dashed-only
 * regex, which was wrong in both directions and MEASURED so on the running API — every one of these
 * answered 401 (bound, unknown authorisation) where the mock answered 400:
 *
 *   D  3f2504e0-4f89-41d3-9a0c-0305e82c3399          N  3f2504e04f8941d39a0c0305e82c3399
 *   B  {3f2504e0-…}                                  P  (3f2504e0-…)
 *   X  {0x3f2504e0,0x4f89,0x41d3,{0x9a,0x0c,…}}      and the D form UPPERCASED
 *
 * — while `not-a-guid` alone answered 400. And the canonicalisation half is not theoretical either:
 * an id minted as `01a00ae5-32dd-…` and presented UPPERCASED, or with the dashes stripped, was
 * **spent successfully (201)** by the real API. The mock keys `stepUpAuthorizations` by the
 * lowercase dashed string `crypto.randomUUID()` returns, so a raw-string lookup would have answered
 * AUTHORIZATION_INVALID for an authorisation the server spends.
 *
 * `parseGuid` already existed for exactly this reason on `Idempotency-Key` — same problem, same
 * answer, and its own comment records the whitespace measurement.
 *
 * The refusal stays in the BINDING stage, beside `Pin` and `Amount`, because model binding runs
 * before the action: a junk header must not cost the PIN attempt that a later check would charge.
 * Measured shape, and two details a guess would get wrong — there is **no `errorCode`**, so
 * `classifyMoneyProblem` falls through to the flow's fallback sentence, and the key is the WIRE
 * header name rather than a C# parameter name, because binding keys by the binding source:
 *
 *   400 {"type":"…rfc9110#section-15.5.1","title":"One or more validation errors occurred.",
 *        "errors":{"Step-Up-Authorization":["The value 'not-a-guid' is not valid."]}}
 */
function readStepUpHeader(request: Request): { id: string | null; errors: string[] } {
  const raw = request.headers.get(STEP_UP_HEADER);
  /*
    ABSENT, EMPTY and WHITESPACE-ONLY are one case, and that is measured rather than tidy.

    MVC binds `[FromHeader] Guid?` by trimming first, so all three arrive at the action as `null`
    and land on the same refusal. Observed on the real API with curl, one request per variant:

      Step-Up-Authorization: <empty>  -> 401 AUTHORIZATION_REQUIRED
      Step-Up-Authorization: "  "     -> 401 AUTHORIZATION_REQUIRED
      Step-Up-Authorization: "	"     -> 401 AUTHORIZATION_REQUIRED

    This read the raw value and handed '' straight to parseGuid, which reported a binding error —
    so the mock answered 400 where the API answers 401. The two are not interchangeable: a 400 says
    "your header is malformed, fix the value", a 401 says "you presented no second factor", and a
    client branches differently on each.
  */
  if (raw === null || raw.trim() === '') return { id: null, errors: [] };
  const canonical = parseGuid(raw);
  if (canonical === null) return { id: null, errors: [`The value '${raw}' is not valid.`] };
  return { id: canonical, errors: [] };
}

/**
 * A GUID from the ROUTE — `[HttpGet("{id:guid}")] … Guid id`, so MVC's parser, so every format.
 *
 * MEASURED: `GET /api/transactions/{id}` answered **200 to all four** of D, N, UPPERCASE and
 * `{braces}` for the same transaction, because the constraint is `Guid.TryParse` and the lookup is
 * then `t.Id == transactionId` — a struct compare, sixteen bytes, spelling long gone.
 *
 * The mock compared the raw URL segment against ids seeded lowercase, so it answered 404 to three
 * of those four. Reachable from the app, not just from curl: `/transactions/:id` hands the segment
 * to the query verbatim, so a pasted uppercase id renders "not found" under MSW and the real
 * transaction against the API.
 *
 * Returns the canonical id, or the raw value when it is not a GUID at all — the caller then misses
 * the lookup and answers its own 404, which is what the API does for an unparseable segment too
 * (no route matches, so the framework 404s before any handler).
 */
function routeGuid(value: string | readonly string[] | undefined): string {
  const raw = typeof value === 'string' ? value : '';
  return parseGuid(raw) ?? raw;
}

/**
 * A GUID in the JSON BODY — parsed the way System.Text.Json parses one, which is NOT how
 * `Guid.TryParse` does.
 *
 * THIS IS THE DISTINCTION THE WHOLE FILE TURNS ON, and it is measured, not inferred. The same value
 * is accepted or refused depending on WHERE it was bound from:
 *
 *   route   `[FromRoute] Guid`     MVC TryParse   D · N · B · P · X · any case  -> 200
 *   header  `[FromHeader] Guid?`   MVC TryParse   D · N · B · P · X · any case  -> bound
 *   BODY    a `Guid` member        System.Text.Json   **D FORM ONLY**, case-insensitive
 *
 * Measured on `POST /api/transfers/authorizations`, same account id in six spellings:
 *
 *   D            -> 201        N (no dashes) -> 400 $.fromAccountId
 *   D UPPERCASE  -> 201        B {braces}    -> 400 $.fromAccountId
 *                              X hex-groups  -> 400 $.fromAccountId
 *                              "  D  "       -> 400 $.fromAccountId   (STJ does not trim)
 *
 * and `GET /api/transactions/{id}` answered 200 to D, N, uppercase AND braces — same value, other
 * binding kind, other parser.
 *
 * So `parseGuid` is right for the header and the Idempotency-Key and wrong here: using it for a body
 * member would make the mock ACCEPT four shapes the server refuses. A review comment proposed
 * exactly that, reasoning from the header fix; it would have widened the mock instead of converging
 * it. What IS shared with the header is the canonicalisation: uppercase binds on both, and a raw
 * lookup would then miss a map keyed lowercase.
 */
function parseBodyGuid(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  // No `.trim()`, deliberately — measured above, STJ refuses a padded value.
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) return null;
  return value.toLowerCase();
}

/** The all-zero GUID. `[NotEmptyGuid]` refuses it exactly as it refuses an absent value. */
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

/**
 * An account id that is absent or all-zero, refused where the API refuses it: in MODEL STATE.
 *
 * `[NotEmptyGuid(ErrorMessage = "A valid account ID is required.")]` is a DataAnnotation, so it
 * fires before FluentValidation, before the same-account rule, and before any ownership lookup.
 * MEASURED on all four endpoints — both mints and both transfers — for an absent id and for the
 * all-zero one alike:
 *
 *   400 {"title":"One or more validation errors occurred.",
 *        "errors":{"FromAccountId":["A valid account ID is required."],
 *                  "ToAccountId":["A valid account ID is required."]}}
 *
 * PascalCase, because model state keys by the CLR property. A well-formed but unknown id is a
 * different thing entirely and still answers 404 ACCOUNT_NOT_FOUND — measured alongside.
 *
 * Without this the mock reached its own later branches with `undefined`, and answered two different
 * wrong things: the internal MINT compared `undefined === undefined` and claimed "Cannot transfer to
 * the same account", while the transfers fell through to the ownership lookup and answered 404. A
 * review bot read that 404 back as the contract — which is precisely why the mock is never the
 * oracle.
 */
function accountIdErrors(value: unknown): string[] {
  // A value that is not a GUID at all never reaches this stage — `bindAccountIds` below has
  // already aborted the bind. What is left is ABSENT or ALL-ZERO, which is what `[NotEmptyGuid]`
  // refuses. Verbatim from the wire; the source of truth is `ValidationRules.AccountNotEmptyGuid`.
  if (value === undefined) return ['A valid account ID is required.'];
  // `bindAccountIds` ran first and rewrote the member — exactly as MVC hands the action a bound
  // `Guid` rather than the string that arrived — so `value` is already canonical here. The parse is
  // kept anyway: it costs nothing, and without it this function would silently pass an all-zero id
  // in any other spelling the day someone calls it before the bind.
  return parseBodyGuid(value) === EMPTY_GUID ? ['A valid account ID is required.'] : [];
}

/**
 * A THIRD envelope for the same field: the JSON value is present but is not a GUID.
 *
 * System.Text.Json fails the conversion before DataAnnotations run, so this is not the
 * `[NotEmptyGuid]` message and not a 404 — it is the same bind-abort shape a non-string `pin`
 * produces, keyed by JSON PATH. MEASURED:
 *
 *   {"fromAccountId":"no-such-account"} -> 400
 *     {"request":["The request field is required."],
 *      "$.fromAccountId":["The JSON value could not be converted to System.Guid.
 *                          Path: $.fromAccountId | LineNumber: 0 | BytePositionInLine: …"]}
 *
 * The mock answered 404 ACCOUNT_NOT_FOUND for this body, and `fidelity.test.ts` pinned that 404 as
 * if it were the contract — a test written from the mock rather than from the server. A well-formed
 * id nobody owns IS still a 404; the two cases are simply not the same request.
 */
function bindAccountIds(body: Record<string, unknown>, fields: readonly string[]): Response | null {
  for (const field of fields) {
    const value = body[field];
    if (value === undefined) continue;
    const canonical = parseBodyGuid(value);
    if (canonical === null) {
      return invalidJsonValueProblem(
        `$.${field}`,
        `The JSON value could not be converted to System.Guid. Path: $.${field}`,
      );
    }
    /*
      THE BIND ITSELF, and the reason this is a mutation rather than a return value.

      MVC binds once and every later line sees the bound `Guid`; nothing downstream re-reads the
      string that arrived. Writing the canonical value back onto the parsed body reproduces that in
      one place, so the ownership lookups, the minted binding record and the comparison in
      `validateAuthorization` cannot disagree about which spelling counts — and a future handler
      cannot reintroduce a raw-string compare by forgetting to call something.

      Without it, an id sent as `01A00ABC-…` bound fine and then missed a map keyed lowercase: a
      404 ACCOUNT_NOT_FOUND for an account the server resolves, or a 401 for an authorisation whose
      binding is identical once parsed. Uppercase is the only spelling that reaches this line, since
      every other one was refused above — but it is the spelling a `.toUpperCase()` anywhere in a
      caller would produce.
    */
    body[field] = canonical;
  }
  return null;
}

function mintAuthorization(record: Omit<StoredStepUpAuthorization, 'consumed' | 'expiresAtMs'>) {
  const id = crypto.randomUUID();
  const expiresAtMs = Date.now() + STEP_UP_WINDOW_MS;
  mockState.stepUpAuthorizations.set(id, { ...record, expiresAtMs, consumed: false });
  return { authorizationId: id, expiresAt: new Date(expiresAtMs).toISOString() };
}

/**
 * EXAMINE an authorisation — and do not spend it. Spending is `spendAuthorization`, below.
 *
 * The split is the whole point, and it is measured rather than stylistic. `StepUpAuthorizationService`
 * has two methods and `TransferService` calls them at two different moments: `ValidateAsync` here,
 * where a refusal is cheap, and `ConsumeAsync` INSIDE the database transaction, after the funds
 * check, beside the ledger write. The first draft of this mock did both at once, so a transfer that
 * failed on funds burned the authorisation.
 *
 * MEASURED on the running stack, minting for €60,000 against a €49,952 balance and sending it:
 *
 *   422 INSUFFICIENT_FUNDS   ->  SELECT Status, ConsumedAt FROM StepUpAuthorizations
 *                                Pending | NULL
 *
 * — still Pending after three attempts, and no `Idempotency-Replayed` header on any of them, so
 * every retry re-executed and answered INSUFFICIENT_FUNDS again. Under the old ordering the mock
 * answered AUTHORIZATION_INVALID on the second attempt: it told the user their confirmation was
 * dead when the only thing wrong was the balance, which is both untrue and unactionable.
 *
 * Expiry is checked BEFORE the binding, because someone who waited too long with the right details
 * deserves "your confirmation expired" rather than the uniform refusal a mismatched or forged
 * reference gets. Everything else — unknown, already spent, wrong binding — collapses to
 * AUTHORIZATION_INVALID, so the mock is no more of an oracle than the server is.
 *
 * Returns the refusal to send, or the held record (or null when no header was presented at all).
 */
/**
 * `401 AUTHORIZATION_REQUIRED` — the transfer presented no authorisation at all (ADR-0042).
 *
 * MEASURED on the real API after the flip. Distinct from AUTHORIZATION_INVALID, which means one WAS
 * presented and does not match: the two drive different recoveries, a fresh PIN entry versus a
 * "that confirmation cannot be used" message. An EMPTY header lands here too, not on the 400 that a
 * non-UUID gets, because `[FromHeader] Guid?` binds an empty value to null — verified on the wire.
 */
function authorizationRequired(request: Request): Response {
  return problem({
    instance: pathOf(request),
    status: 401,
    errorCode: 'AUTHORIZATION_REQUIRED',
    detail: 'This transfer has not been authorised.',
  });
}

function validateAuthorization(
  id: string | null,
  request: Request,
  /*
    Loosened from the stored shape on purpose: the request bodies are typed from the SPEC, where
    every member is optional, and the handler has already refused a malformed one by the time this
    runs. Requiring the strict shape here would only force casts at both call sites.
  */
  expected: {
    operation: StoredStepUpAuthorization['operation'];
    fromAccountId?: string;
    recipientAzureTag?: string;
    toAccountId?: string;
    amount?: number;
  },
): { refusal: Response | null; held: StoredStepUpAuthorization | null } {
  /*
    Absence is refused by the CALLERS, at the rung the PIN check used to hold, so that a headerless
    request cannot probe the payee first. By the time this runs the header is known to be present,
    and everything below is about whether what was presented matches. Kept as an assertion rather
    than a silent pass: a future caller that forgets the earlier rung would otherwise reintroduce
    the fall-through this change exists to remove.
  */
  if (!id) {
    throw new Error(
      'validateAuthorization reached with no authorisation id: the caller must answer ' +
        'AUTHORIZATION_REQUIRED at the ownership rung before resolving a payee.',
    );
  }

  const invalid = () =>
    problem({
      instance: pathOf(request),
      status: 401,
      errorCode: 'AUTHORIZATION_INVALID',
      detail: 'This authorisation cannot be used.',
    });

  const held = mockState.stepUpAuthorizations.get(id);
  if (!held || held.consumed) return { refusal: invalid(), held: null };

  if (held.expiresAtMs <= Date.now()) {
    // OBSERVED: 401 / AUTHORIZATION_EXPIRED, detail verbatim from the API.
    return {
      refusal: problem({
        instance: pathOf(request),
        status: 401,
        errorCode: 'AUTHORIZATION_EXPIRED',
        detail: 'This authorisation has expired. Enter your PIN again to confirm.',
      }),
      held: null,
    };
  }

  if (
    held.operation !== expected.operation ||
    held.fromAccountId !== expected.fromAccountId ||
    held.recipientAzureTag !== expected.recipientAzureTag ||
    held.toAccountId !== expected.toAccountId ||
    held.amount !== expected.amount
  ) {
    // RTS Art. 5(1)(d) reaching the wire: any change to the amount or the payee invalidates it.
    return { refusal: invalid(), held: null };
  }

  return { refusal: null, held };
}

/**
 * Spend it — the mock's `ConsumeAsync`, and the ONLY line that mutates an authorisation.
 *
 * Called from the same place the real one is: after the funds check, beside the ledger write, on
 * the path where the transfer actually happens. `held` is null when no header was presented, which
 * is still a valid transfer (PR 2 is backward compatible), so this is a no-op there.
 */
function spendAuthorization(held: StoredStepUpAuthorization | null): void {
  if (held) held.consumed = true;
}

/**
 * The model-binding stage both mint endpoints share.
 *
 * MEASURED on the running API, `POST /api/transfers/authorizations`:
 *
 *   amount 0     -> 400 {"Amount":["Amount must be between 0.01 EUR and 100000.00 EUR"]} (framework;
 *                       sentence re-measured 2026-09-03 on the deposit endpoint — the mint shares
 *                       the annotation, so it is the same string)
 *   amount 10.00001 -> 400 "Validation Failed" {"amount":["Amount cannot have more than 2 decimal
 *                     places."]}                                                     (validator)
 *
 * Only the first is modelled here, and deliberately: neither transfer handler models the SCALE
 * rule either, so adding it to the mint alone would make the mock's mint stricter than its own
 * transfer — the opposite of the property ADR-0042 needs. It is a real, pre-existing gap rather
 * than a decision, and it is written down instead of quietly closed in a PR about something else.
 */
function mintBindingErrors(
  body: { amount?: number; pin?: unknown; fromAccountId?: string; toAccountId?: string },
  // Explicit rather than inferred from the body's own shape: the EXTERNAL mint names its payee by
  // handle and has no ToAccountId in its DTO at all, so a caller that sent one would otherwise have
  // it validated against a rule the server does not apply.
  { bindsDestination = false }: { bindsDestination?: boolean } = {},
): Record<string, string[]> {
  const errors: Record<string, string[]> = {};
  const badFrom = accountIdErrors(body.fromAccountId);
  if (badFrom.length > 0) errors.FromAccountId = badFrom;
  if (bindsDestination) {
    const badTo = accountIdErrors(body.toAccountId);
    if (badTo.length > 0) errors.ToAccountId = badTo;
  }
  const badAmount = amountErrors(body.amount);
  if (badAmount.length > 0) errors.Amount = badAmount;
  const badPin = pinAnnotationErrors((body.pin ?? null) as string | null);
  if (badPin.length > 0) errors.Pin = badPin;
  return errors;
}

/**
 * The PIN member's BIND failures on a mint — the shapes that never reach DataAnnotations at all.
 *
 * The two transfer handlers already route their pin through `transferPinBindFailure`; the mints did
 * not, so a non-string pin reached `pinAnnotationErrors` and a regex met a number. MEASURED on both
 * mint endpoints, and identical to the transfers':
 *
 *   pin absent  -> 400 {"$":["JSON deserialization for type '…TransferAuthorizationRequest' was
 *                            missing required properties including: 'pin'."],
 *                       "request":["The request field is required."]}
 *   pin null    -> 400 {"Pin":["The Pin field is required."]}
 *   pin 123456  -> 400 {"request":[…], "$.pin":["The JSON value could not be converted to
 *                       System.String. Path: $.pin"]}
 *
 * Same helper, different CLR type name — that is the whole difference between the two doors.
 */
function mintPinBindFailure(body: { pin?: unknown }, clrType: string): Response | null {
  return transferPinBindFailure(body, clrType);
}

/**
 * POST /api/transfers/authorizations — mint one for an EXTERNAL transfer.
 *
 * Refusal order mirrors TransferService.AuthoriseTransferAsync: binding, then OWNERSHIP of the
 * source account, then payee resolution, then the PIN. Two of those were missing from the first
 * draft, and both are measured:
 *
 *   unowned fromAccountId, CORRECT pin -> 404 ACCOUNT_NOT_FOUND
 *     detail "Account with identifier '3f2504e0-…' was not found."
 *   (so probing someone else's account costs no PIN attempt, exactly as on the transfer itself)
 *
 * Resolving the payee before the PIN is what stops an authorisation ever naming someone the
 * transfer would then refuse.
 */
const authoriseTransfer = api.post(
  '/api/transfers/authorizations',
  async ({ request, response }) => {
    const body = (await request.json()) as {
      fromAccountId: string;
      recipientAzureTag: string;
      amount: number;
      pin?: string;
    };

    const idBind = bindAccountIds(body as Record<string, unknown>, ['fromAccountId']);
    if (idBind) return response.untyped(idBind);
    const pinBind = mintPinBindFailure(
      body,
      'AzureBank.Shared.DTOs.Transfer.TransferAuthorizationRequest',
    );
    if (pinBind) return response.untyped(pinBind);

    const bindingErrors = mintBindingErrors(body);
    if (Object.keys(bindingErrors).length > 0) {
      return response.untyped(modelStateProblem(bindingErrors));
    }

    // OWNERSHIP FIRST — `AuthoriseTransferAsync` opens with GetAccountWithOwnershipCheckAsync.
    if (!mockState.accounts.some((a) => a.id === body.fromAccountId)) {
      return response.untyped(notFound('Account', body.fromAccountId, request));
    }

    // Self-transfer precedes the recipient lookup inside `ResolveExternalPayeeAsync`, so it cannot
    // be reported as "not found" for a handle that plainly exists — it is the caller's own.
    if (mockState.session?.azureTag?.toLowerCase() === body.recipientAzureTag?.toLowerCase()) {
      return response.untyped(
        problem({
          instance: pathOf(request),
          status: 422,
          errorCode: 'SELF_TRANSFER_NOT_ALLOWED',
          detail: 'Cannot transfer to yourself. Use internal account transfer instead.',
        }),
      );
    }

    const recipient = mockState.recipients.find(
      (r) => r.azureTag.toLowerCase() === body.recipientAzureTag?.toLowerCase(),
    );
    if (!recipient) {
      // MEASURED: 404 / ACCOUNT_NOT_FOUND — the real code, not a dedicated recipient one.
      return response.untyped(notFound('Recipient', body.recipientAzureTag, request));
    }

    // MEASURED: 422 PIN_REQUIRED · 429 PIN_LOCKED (retryAfterSeconds 900, on the THIRD miss) ·
    // 401 INVALID_PIN. Same helper the transfer uses, so the two cannot drift.
    const pinRefusal = checkPinInBand(
      body.pin,
      request,
      'PIN must be set before authorising a transfer.',
    );
    if (pinRefusal) return response.untyped(pinRefusal);

    const minted = mintAuthorization({
      operation: 'Transfer',
      fromAccountId: body.fromAccountId,
      recipientAzureTag: body.recipientAzureTag,
      amount: body.amount,
    });

    // MEASURED: 201 {"data":{authorizationId, expiresAt},"message":"Transfer authorised"}
    return response(201).json({ data: minted, message: 'Transfer authorised' });
  },
);

/**
 * POST /api/transfers/internal/authorizations — the same, for a move between own accounts.
 *
 * MEASURED: from == to with a CORRECT pin is refused by the validator, not by the service —
 *
 *   400 {"type":"https://httpstatuses.com/400","title":"Validation Failed",
 *        "detail":"One or more validation errors occurred.",
 *        "instance":"/api/transfers/internal/authorizations",
 *        "errors":{"toAccountId":["Cannot transfer to the same account."]}}
 *
 * — which is the same envelope and the same camelCase key the internal TRANSFER answers. Without
 * it the mock minted an authorisation for a move the transfer would then refuse: the exact class
 * of mismatch ADR-0042 exists to make impossible.
 */
const authoriseInternalTransfer = api.post(
  '/api/transfers/internal/authorizations',
  async ({ request, response }) => {
    const body = (await request.json()) as {
      fromAccountId: string;
      toAccountId: string;
      amount: number;
      pin?: string;
    };

    const idBind = bindAccountIds(body as Record<string, unknown>, [
      'fromAccountId',
      'toAccountId',
    ]);
    if (idBind) return response.untyped(idBind);
    const pinBind = mintPinBindFailure(
      body,
      'AzureBank.Shared.DTOs.Transfer.InternalTransferAuthorizationRequest',
    );
    if (pinBind) return response.untyped(pinBind);

    const bindingErrors = mintBindingErrors(body, { bindsDestination: true });
    if (Object.keys(bindingErrors).length > 0) {
      return response.untyped(modelStateProblem(bindingErrors));
    }

    // FluentValidation, so it precedes the service and both ownership checks below.
    if (body.fromAccountId === body.toAccountId) {
      return response.untyped(
        problem({
          instance: pathOf(request),
          status: 400,
          errors: { toAccountId: ['Cannot transfer to the same account.'] },
        }),
      );
    }

    // Source then destination, in turn: `AuthoriseInternalTransferAsync` makes two separate
    // ownership calls, so the 404 names the account it could not find.
    if (!mockState.accounts.some((a) => a.id === body.fromAccountId)) {
      return response.untyped(notFound('Account', body.fromAccountId, request));
    }
    if (!mockState.accounts.some((a) => a.id === body.toAccountId)) {
      return response.untyped(notFound('Account', body.toAccountId, request));
    }

    const pinRefusal = checkPinInBand(
      body.pin,
      request,
      'PIN must be set before authorising a transfer.',
    );
    if (pinRefusal) return response.untyped(pinRefusal);

    const minted = mintAuthorization({
      operation: 'InternalTransfer',
      fromAccountId: body.fromAccountId,
      toAccountId: body.toAccountId,
      amount: body.amount,
    });

    return response(201).json({ data: minted, message: 'Internal transfer authorised' });
  },
);

/**
 * POST /api/transfers — the API's idempotency protocol plus the authorisation check (ADR-0042).
 *
 * The level-2 step-up gate that used to open this handler is GONE (ADR-0041): the BFF no longer
 * answers 403 for a transfer; the PIN is presented to the mint and the transfer carries the
 * authorisation it produced. Failure order mirrors the real path: idempotency →
 * AUTHORIZATION_REQUIRED → SELF_TRANSFER_NOT_ALLOWED → recipient not found (ACCOUNT_NOT_FOUND, the
 * real code) → INSUFFICIENT_FUNDS → success (debit sender, push a TransferOut row; the recipient
 * is off-ledger). A missing fromAccount is tolerated (debit only if found).
 */
const transfer = api.post('/api/transfers', async ({ request, response }) => {
  // Middleware, so it precedes the key checks below. See `payloadTooLarge`.
  const oversized = payloadTooLarge(await request.clone().text(), request);
  if (oversized) return response.untyped(oversized);

  const key = request.headers.get('Idempotency-Key');
  if (!key) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_MISSING',
        detail: "The 'Idempotency-Key' header is required on this endpoint.",
      }),
    );
  }
  // Parsed ONCE, and the parsed value is what the store is keyed by. Keying on the raw header
  // would give `<guid>` and its undashed spelling two separate entries, so the same logical key
  // could execute a deposit twice — the backend stores a parsed `Guid`, so it replays. Accepting
  // the alternate formats is what made that reachable; this is the third place in this file where
  // "accept the format, then use the raw string" was the bug.
  const parsedKey = parseGuid(key);
  if (parsedKey === null || parsedKey === NIL_UUID) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_INVALID',
        detail: "The 'Idempotency-Key' header must be a valid UUID.",
      }),
    );
  }

  const parsedBody = await readJsonBody(request);
  if (!parsedBody) {
    return response.untyped(unreadableBodyProblem(await request.clone().text()));
  }
  const raw = parsedBody.raw;
  /*
    Fingerprint the WIRE BYTES, and do it BEFORE `bindAccountIds` rewrites the parsed body.

    `IdempotencyService.ComputeRequestHashAsync(Stream body, …)` HMACs the raw stream, so two bodies
    differing only in a GUID's casing legitimately hash differently on the server — and must here.
    Moving this below the bind, or switching it to `JSON.stringify(body)`, would make the mock replay
    as one request a pair the API answers with 422 IDEMPOTENCY_KEY_REUSE.
  */
  const fp = fingerprint(raw);
  const stored = mockState.idempotency.get(`transfer|${parsedKey}`);
  if (stored) {
    if (stored.bodyFingerprint !== fp) {
      return response.untyped(
        problem({
          instance: pathOf(request),
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

  const body = parsedBody.body as {
    fromAccountId?: string;
    recipientAzureTag?: string;
    amount?: number;
    description?: string;
    pin?: string;
  };
  const amount = body.amount as number;
  const tag = body.recipientAzureTag ?? '';

  /*
    Source account FIRST. `TransferService.TransferAsync` opens with
    `GetAccountWithOwnershipCheckAsync(request.FromAccountId, userId)` — above the sender lookup,
    above the self-transfer guard, above the recipient lookup. The mock resolved it LAST, so a
    transfer from an unknown account to yourself answered SELF_TRANSFER_NOT_ALLOWED where the API
    answers ACCOUNT_NOT_FOUND, and a client branching on the code took the wrong path.
  */
  // Model binding runs before the action. No pin bind-failure here any more: `TransferRequest` has
  // no Pin since ADR-0042's flip, so a `pin` in the body is an unknown property that
  // System.Text.Json ignores — it cannot produce a 400 of its own.
  const idBind = bindAccountIds(body as Record<string, unknown>, ['fromAccountId']);
  if (idBind) return response.untyped(idBind);

  /*
    ONE model-state pass, not two early exits: every DataAnnotation on the body is evaluated
    together, so a doubly-invalid request gets ONE errors map naming each bad field.

    The `Pin` key is gone with the property (ADR-0042). It remains on the two MINT endpoints, which
    is where a malformed PIN is now refused — and refused by model binding there too, so it still
    costs no lockout attempt.
  */
  const bindingErrors: Record<string, string[]> = {};
  const badAmount = amountErrors(body.amount);
  if (badAmount.length > 0) bindingErrors.Amount = badAmount;
  // The header binds in the same stage as the body's annotations, so a request wrong in both ways
  // reports every key at once. See `readStepUpHeader`.
  const stepUp = readStepUpHeader(request);
  if (stepUp.errors.length > 0) bindingErrors[STEP_UP_HEADER] = stepUp.errors;
  const badFrom = accountIdErrors(body.fromAccountId);
  if (badFrom.length > 0) bindingErrors.FromAccountId = badFrom;
  if (Object.keys(bindingErrors).length > 0) {
    return response.untyped(modelStateProblem(bindingErrors));
  }

  const account = mockState.accounts.find((a) => a.id === body.fromAccountId);
  if (!account) {
    /*
      OWNERSHIP BEFORE THE PIN, and this order is measured rather than assumed. `TransferAsync`
      opens with `GetAccountWithOwnershipCheckAsync` and only then calls `VerifyPinOrThrowAsync`.
      Observed against the real API — same unknown account id, two different pins:

        correct pin -> 404 ACCOUNT_NOT_FOUND
        WRONG   pin -> 404 ACCOUNT_NOT_FOUND    (not 401, and no attempt counted)

      The mock had these the other way round, so probing a foreign account id with a junk pin
      answered INVALID_PIN and burned a lockout attempt the API never charges.
    */
    return response.untyped(notFound('Account', body.fromAccountId, request));
  }

  /*
    NO AUTHORISATION PRESENTED, at the rung the in-band PIN check used to occupy (ADR-0042).

    `TransferService.RequireAuthorization` sits after the ownership 404 and BEFORE the payee is
    resolved, exactly where `VerifyPinOrThrowAsync` sat, and the placement carries the same
    property: a caller with no second factor cannot use this endpoint to ask which handles exist.
    Answering here rather than at `validateAuthorization` below is what reproduces that.
  */
  if (stepUp.id === null) return response.untyped(authorizationRequired(request));

  if (mockState.session?.azureTag === tag) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 422,
        errorCode: 'SELF_TRANSFER_NOT_ALLOWED',
        // MEASURED on both the transfer and the mint, byte-identical, because `ResolveExternalPayeeAsync`
        // is the single producer: "Cannot transfer to yourself. Use internal account transfer
        // instead." The mock carried an invented sentence here — pre-existing, unrelated to ADR-0042,
        // but measured in this round and one line to correct.
        detail: 'Cannot transfer to yourself. Use internal account transfer instead.',
      }),
    );
  }
  const recipient = mockState.recipients.find((r) => r.azureTag === tag);
  if (!recipient) {
    // `TransferService` throws NotFoundException("Recipient", tag) here — hence the resource name
    // and the ACCOUNT_NOT_FOUND code, which that constructor uses for every resource.
    return response.untyped(notFound('Recipient', tag, request));
  }

  /*
    Spend the authorisation, if one was presented (ADR-0042).

    POSITION IS THE CONTRACT, and it is not where the first draft put it. `TransferAsync` runs
    ownership → PIN → `ResolveExternalPayeeAsync` → `ValidateAsync` → funds, so the payee is
    resolved BEFORE the authorisation is examined: an unknown handle answers 404 ACCOUNT_NOT_FOUND
    even when the presented authorisation is also wrong. The mock validated first, which turned that
    404 into a 401 — a client debugging a typo'd handle would have been told its confirmation was
    invalid. Still after the PIN and before any money moves: a refusal costs nothing and moves
    nothing.

    A request with no header never reaches here: the rung above answers AUTHORIZATION_REQUIRED,
    which is what ADR-0042's flip made of the fall-through this line used to describe.
  */
  const authorization = validateAuthorization(stepUp.id, request, {
    operation: 'Transfer',
    fromAccountId: body.fromAccountId,
    recipientAzureTag: body.recipientAzureTag,
    amount: body.amount,
  });
  if (authorization.refusal) return response.untyped(authorization.refusal);

  const available = account.balance;
  if (amount > available) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 422,
        errorCode: 'INSUFFICIENT_FUNDS',
        // No figures in the sentence since 3769dc9: both amounts travel as the numeric
        // `available`/`requested` extensions below. Measured 2026-09-03 (balance 6, amount 10):
        //   422 {"detail":"Insufficient funds.","errorCode":"INSUFFICIENT_FUNDS",
        //        "available":6.0000,"requested":10}
        detail: 'Insufficient funds.',
        extensions: { available, requested: amount },
      }),
    );
  }

  // Spent HERE, not above: `ConsumeAsync` runs inside the transfer's own transaction, after the
  // funds check, so a 422 leaves the authorisation Pending and re-usable. Measured — see
  // `validateAuthorization`.
  spendAuthorization(authorization.held);

  const newBalance = available - amount;
  account.balance = newBalance;
  const index = mockState.transactions.length;
  mockState.transactions.push({
    id: `019f7b3f-0000-7000-8000-${(0x800 + index).toString(16).padStart(12, '0')}`,
    accountId: account.id,
    transactionNumber: mockTransactionNumber(index),
    type: 'TransferOut',
    amount,
    balanceAfter: newBalance,
    /*
      The API DEFAULTS this; it does not store null. TransferService.cs:294 writes
      `request.Description ?? $"Transfer to @{recipient.AzureTag}"`, and the row it produces was read
      back off the running stack rather than off the C#:

        POST /api/transfers  {fromAccountId, recipientAzureTag:"janesmith", amount:1.11, pin}
          -- no description field at all --
        GET  /api/transactions
          -> TransferOut  1.11  "Transfer to @janesmith"

      The internal transfer below already modelled its two defaults; this side stored null, so a
      history rendered under MSW showed an empty description where the product shows a sentence.
      Deposit and withdraw are NOT this case and must keep `?? null`: TransactionService.cs:63 and
      :153 assign `request.Description` raw, measured the same way (deposit with no description came
      back with description null).
    */
    description: body.description ?? `Transfer to @${tag}`,
    recipientAzureTag: tag,
    senderAzureTag: null,
    status: 'Completed',
    createdAt: `2026-07-22T12:${String(index).padStart(2, '0')}:00.0000000Z`,
  });

  const payload = {
    data: {
      transactionNumber: mockTransactionNumber(index),
      amount,
      newBalance,
      recipientAzureTag: tag,
      recipientName: recipient.displayName,
      processedAt: '2026-07-22T12:00:00.0000000Z',
    },
    message: 'Transfer successful',
  };
  const text = JSON.stringify(payload);
  mockState.idempotency.set(`transfer|${parsedKey}`, {
    bodyFingerprint: fp,
    status: 201,
    body: text,
  });
  return response(201).json(payload);
});

/**
 * POST /api/transfers/internal — move money between the caller's OWN accounts. Same
 * authorisation + idempotency protocol as the external transfer (no level-2 gate on either since
 * ADR-0041), but double-entry ON-ledger: debit the source, credit the destination, push BOTH a
 * TransferOut and a TransferIn row. Failure order mirrors the backend: idempotency →
 * same-account 400 (validator envelope, `errors.toAccountId`) → ownership (either account missing
 * → ACCOUNT_NOT_FOUND) → AUTHORIZATION_REQUIRED / AUTHORIZATION_INVALID (ADR-0042) →
 * INSUFFICIENT_FUNDS → success.
 */
const transferInternal = api.post('/api/transfers/internal', async ({ request, response }) => {
  // Middleware, so it precedes the key checks below. See `payloadTooLarge`.
  const oversized = payloadTooLarge(await request.clone().text(), request);
  if (oversized) return response.untyped(oversized);

  const key = request.headers.get('Idempotency-Key');
  if (!key) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_MISSING',
        detail: "The 'Idempotency-Key' header is required on this endpoint.",
      }),
    );
  }
  // Parsed ONCE, and the parsed value is what the store is keyed by. Keying on the raw header
  // would give `<guid>` and its undashed spelling two separate entries, so the same logical key
  // could execute a deposit twice — the backend stores a parsed `Guid`, so it replays. Accepting
  // the alternate formats is what made that reachable; this is the third place in this file where
  // "accept the format, then use the raw string" was the bug.
  const parsedKey = parseGuid(key);
  if (parsedKey === null || parsedKey === NIL_UUID) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_INVALID',
        detail: "The 'Idempotency-Key' header must be a valid UUID.",
      }),
    );
  }

  const parsedBody = await readJsonBody(request);
  if (!parsedBody) {
    return response.untyped(unreadableBodyProblem(await request.clone().text()));
  }
  const raw = parsedBody.raw;
  /*
    Fingerprint the WIRE BYTES, and do it BEFORE `bindAccountIds` rewrites the parsed body.

    `IdempotencyService.ComputeRequestHashAsync(Stream body, …)` HMACs the raw stream, so two bodies
    differing only in a GUID's casing legitimately hash differently on the server — and must here.
    Moving this below the bind, or switching it to `JSON.stringify(body)`, would make the mock replay
    as one request a pair the API answers with 422 IDEMPOTENCY_KEY_REUSE.
  */
  const fp = fingerprint(raw);
  const stored = mockState.idempotency.get(`internal|${parsedKey}`);
  if (stored) {
    if (stored.bodyFingerprint !== fp) {
      return response.untyped(
        problem({
          instance: pathOf(request),
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

  const body = parsedBody.body as {
    fromAccountId?: string;
    toAccountId?: string;
    amount?: number;
    description?: string;
    pin?: string;
  };
  // Model binding first. No pin bind-failure: `InternalTransferRequest` has no Pin since ADR-0042.
  const idBind = bindAccountIds(body as Record<string, unknown>, ['fromAccountId', 'toAccountId']);
  if (idBind) return response.untyped(idBind);

  /*
    ONE model-state pass, not two early exits: every DataAnnotation on the body is evaluated
    together, so a doubly-invalid request gets ONE errors map naming each bad field.

    The `Pin` key is gone with the property (ADR-0042). It remains on the two MINT endpoints, which
    is where a malformed PIN is now refused — and refused by model binding there too, so it still
    costs no lockout attempt.
  */
  const bindingErrors: Record<string, string[]> = {};
  const badAmount = amountErrors(body.amount);
  if (badAmount.length > 0) bindingErrors.Amount = badAmount;
  // The header binds in the same stage as the body's annotations, so a request wrong in both ways
  // reports every key at once. See `readStepUpHeader`.
  const stepUp = readStepUpHeader(request);
  if (stepUp.errors.length > 0) bindingErrors[STEP_UP_HEADER] = stepUp.errors;
  const badFrom = accountIdErrors(body.fromAccountId);
  if (badFrom.length > 0) bindingErrors.FromAccountId = badFrom;
  const badTo = accountIdErrors(body.toAccountId);
  if (badTo.length > 0) bindingErrors.ToAccountId = badTo;
  if (Object.keys(bindingErrors).length > 0) {
    return response.untyped(modelStateProblem(bindingErrors));
  }
  const amount = body.amount as number;

  // The truthiness test is now redundant — `accountIdErrors` already refused an absent or all-zero
  // id above — but it is kept because it costs nothing and the rule below must never fire on two
  // undefineds if that guard is ever moved.
  if (body.fromAccountId && body.fromAccountId === body.toAccountId) {
    /*
      A FIELD error at 400, not a domain code at 422 — three differences, and the mock had all
      three wrong.

      `InternalTransferRequestValidator` carries `ToAccountId.NotEqual(x => x.FromAccountId)`, and
      `ValidateAndThrowAsync` runs BEFORE the service is called, so the service's own
      `BusinessRuleException(SAME_ACCOUNT_TRANSFER)` can never reach the wire — `ErrorCodes.cs` says
      as much, calling it defence-in-depth for non-HTTP callers. The backend is not buggy here; the
      mock invented a code the API cannot emit, and `InternalTransferPage` still carries copy for a
      branch only MSW could ever reach.

      Observed: 400 {"type":"https://httpstatuses.com/400","title":"Validation Failed",
                     "detail":"One or more validation errors occurred.",
                     "errors":{"toAccountId":["Cannot transfer to the same account."]}}

      camelCase key here, unlike the model-state envelope: FluentValidation reports against the
      expression it was given, and `AddFluentValidation` is configured to camelCase those names.
    */
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 400,
        errors: { toAccountId: ['Cannot transfer to the same account.'] },
      }),
    );
  }
  // Checked in turn, source first, because `InternalTransferAsync` resolves them that way through
  // two separate `GetAccountWithOwnershipCheckAsync` calls — so the 404 names the account it could
  // not find rather than shrugging at "one of the accounts".
  const from = mockState.accounts.find((a) => a.id === body.fromAccountId);
  if (!from) {
    return response.untyped(notFound('Account', body.fromAccountId, request));
  }
  const to = mockState.accounts.find((a) => a.id === body.toAccountId);
  if (!to) {
    return response.untyped(notFound('Account', body.toAccountId, request));
  }

  /*
    No authorisation presented. Last of the gates, first of the service's own work: FluentValidation
    (same-account) and both ownership checks precede `RequireAuthorization` in
    `InternalTransferAsync`, so none of them can be answered AUTHORIZATION_REQUIRED. Measured on the
    real API: a same-account request answers 400 {"toAccountId":["Cannot transfer to the same
    account."]} — the validator envelope — without the header being consulted at all.
  */
  if (stepUp.id === null) return response.untyped(authorizationRequired(request));

  /*
    Spend the authorisation (ADR-0042). `InternalTransferAsync` calls `ValidateAsync` after the PIN
    and after the same-account rule, so every cheaper refusal above still answers as itself. There
    is no payee to resolve on this endpoint, which is the one difference from the external path.
  */
  const authorization = validateAuthorization(stepUp.id, request, {
    operation: 'InternalTransfer',
    fromAccountId: body.fromAccountId,
    toAccountId: body.toAccountId,
    amount: body.amount,
  });
  if (authorization.refusal) return response.untyped(authorization.refusal);
  if (amount > from.balance) {
    return response.untyped(
      problem({
        instance: pathOf(request),
        status: 422,
        errorCode: 'INSUFFICIENT_FUNDS',
        // No figures in the sentence since 3769dc9: both amounts travel as the numeric
        // `available`/`requested` extensions below. Measured 2026-09-03 on the external transfer
        // (balance 16, amount 1000, minted authorisation): 422 "Insufficient funds.",
        // "available":16.0000,"requested":1000 — the same exception serves the internal one.
        detail: 'Insufficient funds.',
        extensions: { available: from.balance, requested: amount },
      }),
    );
  }

  // Same rule as the external path: spend only where the money actually moves.
  spendAuthorization(authorization.held);

  from.balance -= amount;
  to.balance += amount;
  const index = mockState.transactions.length;
  const transactionNumber = mockTransactionNumber(index);
  const at = `2026-07-22T13:${String(index).padStart(2, '0')}:00.0000000Z`;
  /*
    `TransferId = outgoingTransaction.Id` (TransferService.cs:330) — the response NAMES the
    outgoing ledger row, it does not mint a new identifier. Measured end to end on the running
    stack, which is the only way to see that the id is dereferenceable:

      POST /api/transfers/internal -> transferId 019fd135-37c7-78d4-9d7b-8afcde4c6e3f
      GET  /api/transactions/019fd135-37c7-78d4-9d7b-8afcde4c6e3f -> 200, type "TransferOut"

    The mock returned a synthetic `0xd00 + index` belonging to no row, so the same GET was a 404.
    (The audit also claimed this collided with the deposit block, which uses the same 0xd00 base.
    It does not: every block indexes off `transactions.length`, which strictly increases, so no two
    writes can ever draw the same index. Sharing the base is still a trap worth removing, and
    naming the row removes it.)
  */
  const outgoingId = `019f7b3f-0000-7000-8000-${(0xc00 + index).toString(16).padStart(12, '0')}`;
  mockState.transactions.push({
    id: outgoingId,
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
    transactionNumber: mockTransactionNumber(index + 1),
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
      transferId: outgoingId,
      transactionNumber,
      fromAccountId: from.id,
      toAccountId: to.id,
      amount,
      description: body.description ?? null,
      fromAccountNewBalance: from.balance,
      toAccountNewBalance: to.balance,
      processedAt: '2026-07-22T13:00:00.0000000Z',
    },
    message: 'Internal transfer successful',
  };
  const text = JSON.stringify(payload);
  mockState.idempotency.set(`internal|${parsedKey}`, {
    bodyFingerprint: fp,
    status: 201,
    body: text,
  });
  return response(201).json(payload);
});

/**
 * POST /bff/auth/verify-pin — BFF endpoint (outside the API spec, so plain msw).
 * Source semantics: correct PIN elevates the session to level 2; a WRONG PIN is HTTP 200
 * with verified:false, never a 4xx. Shares the SAME attempt/lock state as withdraw
 * (mockState.pinAttempts/pinLockedUntil) so the 3rd consecutive miss is a 429 PIN_LOCKED.
 */
/**
 * The API's replies to a badly-shaped PIN payload — THREE different ones, measured anonymously.
 *
 *   {}              -> {"$":["JSON deserialization for type '<DTO>' was missing required
 *                            properties including: 'pin'."],
 *                       "request":["The request field is required."]}
 *   {"pin":null}    -> {"Pin":["The Pin field is required."]}
 *   {"pin":"abc"}   -> {"Pin":["PIN must be exactly 6 digits."]}
 *
 * The distinction is behavioural, not cosmetic: `$` maps to no form field, so a consumer walking
 * `problem.errors` falls back to its generic bar, where `Pin` lands on the PIN input. The mock
 * collapsed all three into the six-digits message, so one of those two UI paths was unreachable.
 * The DTO name differs per route, which is why it is a parameter.
 */
function pinPayloadProblem(dto: 'VerifyPinRequest' | 'SetPinRequest', pin: unknown) {
  if (pin === undefined) {
    return modelStateProblem({
      $: [
        `JSON deserialization for type 'AzureBank.Shared.DTOs.Auth.${dto}' was missing required properties including: 'pin'.`,
      ],
      request: ['The request field is required.'],
    });
  }
  if (pin === null) {
    return modelStateProblem({ Pin: ['The Pin field is required.'] });
  }
  return modelStateProblem({ Pin: ['PIN must be exactly 6 digits.'] });
}

const verifyPin = http.post('*/bff/auth/verify-pin', async ({ request }) => {
  // NOT under the BFF's `auth` rate-limit policy: `VerifyPin` carries no `[EnableRateLimiting]`
  // (login, register, reauthenticate and the azure-tag rename do), and twelve calls in a row with a
  // dead cookie answered 401 every time on 2026-09-03, never 429. The mock counted this route
  // against the ten-permit budget until then. Short of the 300/min global backstop, the only
  // 429 the route emits is the API's PIN lockout (PIN_LOCKED, ADR-0010), relayed.
  runSessionActivityMiddleware(request);
  const authBody = await readJsonBody(request);
  if (!authBody) {
    return unreadableBodyProblem(await request.clone().text());
  }
  const pin = authBody.body.pin as string | undefined;
  // Model validation runs before the action, so a malformed PIN is a 400 even for an anonymous
  // caller — and WHICH 400 depends on how it is malformed. See `pinPayloadProblem`.
  if (typeof pin !== 'string' || !/^\d{6}$/.test(pin)) {
    return pinPayloadProblem('VerifyPinRequest', pin);
  }
  /*
    The session gate, AFTER the body and AFTER model validation.

    Measured anonymously against the real BFF rather than reasoned about — an earlier draft had this
    above the body check and was wrong twice over:
      '{not json'      -> 400 "One or more validation errors occurred." (framework envelope)
      {}               -> 400 errors {"$": ["JSON deserialization for type ..."]}
      {"pin":"abc"}    -> 400 errors {"Pin": ["PIN must be exactly 6 digits."]}
      {"pin":"123456"} -> 401 {"title":"Unauthorized","detail":"Session expired or invalid"}
    ASP.NET binds and validates the model BEFORE the action body runs, so every malformed payload is
    a 400 even with no session at all; only a well-formed one reaches the session read.

    The mock used to skip the session entirely, so a caller with NO session got a 200 and, worse,
    `mockState.authLevel = 2` — a nonexistent session could be walked up to elevated and then used
    to push a full transfer through: at the time the level was the only thing the money handlers
    consulted. Since ADR-0041 (dd84179, 2026-08-13) only the account-number reveal reads it, and a
    money move carries its authorisation in-band (ADR-0042) — the same bug today would hand a
    session-less caller the reveal, not a transfer.
  */
  if (!mockState.session) {
    return bffProblem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' });
  }
  const now = Date.now();
  if (mockState.pinLockedUntil && Date.parse(mockState.pinLockedUntil) > now) {
    return pinLockedProblem(mockState.pinLockedUntil, now, 'verify-pin');
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
    mockState.pinLockedUntil = apiOffsetInstant(now + PIN_LOCKOUT_SECONDS * 1000);
    return pinLockedProblem(mockState.pinLockedUntil, now, 'verify-pin');
  }
  /*
    A REFUSED PIN IS A 200, and the body is not the caller's current state — `authLevel` is
    HARD-CODED 1 by the BFF, not echoed back. The mock returned `mockState.authLevel`, so an
    already-elevated user who fumbled a re-entry saw `2` where production says `1`.

    Measured 2026-08-04 (this cost one of three attempts, hence the exact quote):
      POST /bff/auth/verify-pin {"pin":"000000"}
        -> 200 {"data":{"verified":false,"authLevel":1,"pinExpiresAt":null},
                "message":"Invalid PIN"}
  */
  return HttpResponse.json({
    data: { verified: false, authLevel: 1, pinExpiresAt: null },
    message: 'Invalid PIN',
  });
});

/**
 * POST /bff/auth/set-pin — ENROL a PIN, or CHANGE one by proving the current value.
 *
 * This comment used to say "set/overwrite … no old PIN and no step-up required", and the handler
 * implemented exactly that. It was describing a hole, not a design: a caller holding only a session
 * could overwrite the PIN and then clear every gate the PIN protects. The mock therefore modelled
 * the vulnerability as intended behaviour, which is the worst way for a mock to be wrong — a test
 * asserting it would have PINNED the bypass.
 *
 * Measured through the real BFF after the fix, and these are the bodies reproduced below:
 *   enrol, no currentPin                -> 200 {"message":"PIN set successfully"}
 *   change, no currentPin               -> 422 PIN_REQUIRED  "The current PIN is required to change it."
 *   change, wrong currentPin            -> 401 INVALID_PIN   "Invalid PIN."
 *   change, correct currentPin          -> 200 {"message":"PIN set successfully"}
 * `instance` on both errors is "/api/auth/pin" — the API's own path, because the BFF forwards the
 * upstream problem untouched (ForwardUpstreamError), not a BFF-authored body.
 *
 * A bad format is still the API's VALIDATION_ERROR shape (400, `errors` dict, NO errorCode — the FE
 * synthesizes VALIDATION_ERROR). On success it flips session.hasPin so the invalidated /me refetch
 * clears the withdraw gate, and clears any prior lock so the freshly-set PIN works immediately.
 */
const setPin = http.post('*/bff/auth/set-pin', async ({ request }) => {
  runSessionActivityMiddleware(request);
  const authBody = await readJsonBody(request);
  if (!authBody) {
    return unreadableBodyProblem(await request.clone().text());
  }
  const pin = authBody.body.pin as string | undefined;
  if (typeof pin !== 'string' || !/^\d{6}$/.test(pin)) {
    return pinPayloadProblem('SetPinRequest', pin);
  }
  /*
    The session gate, AFTER the body and AFTER model validation.

    Measured anonymously against the real BFF rather than reasoned about — an earlier draft had this
    above the body check and was wrong twice over:
      '{not json'      -> 400 "One or more validation errors occurred." (framework envelope)
      {}               -> 400 errors {"$": ["JSON deserialization for type ..."]}
      {"pin":"abc"}    -> 400 errors {"Pin": ["PIN must be exactly 6 digits."]}
      {"pin":"123456"} -> 401 {"title":"Unauthorized","detail":"Session expired or invalid"}
    ASP.NET binds and validates the model BEFORE the action body runs, so every malformed payload is
    a 400 even with no session at all; only a well-formed one reaches the session read.

    The mock used to skip the session entirely, so a caller with NO session got a 200 and, worse,
    `mockState.authLevel = 2` — a nonexistent session could be walked up to elevated and then used
    to push a full transfer through: at the time the level was the only thing the money handlers
    consulted. Since ADR-0041 (dd84179, 2026-08-13) only the account-number reveal reads it, and a
    money move carries its authorisation in-band (ADR-0042) — the same bug today would hand a
    session-less caller the reveal, not a transfer.
  */
  if (!mockState.session) {
    return bffProblem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' });
  }
  /*
    CHANGING requires the current PIN; ENROLLING does not. Order matters and is measured: the
    format check above runs first (ASP.NET binds and validates before the action), then the session,
    then this. `mockState.pin` being set is the mock's equivalent of a non-null PinHash.
  */
  /*
    ENROLLING costs the PASSWORD (T7/#201), and the mock enforces it because the mock's job is to
    be a faithful consumer of the contract, never a softer one. Measured against the real pipeline
    on this branch:
      enrol, no password        -> 422 PASSWORD_REQUIRED
      enrol, wrong password     -> 401 INVALID_CREDENTIALS (and it COUNTS toward the login lockout)
      enrol, correct password   -> 200
      change, password only     -> 422 PIN_REQUIRED  (the two proofs are not interchangeable)
    The lockout half is deliberately NOT modelled here: this mock has no login-lockout state, and
    inventing one would be a shape the real system does not have. The API integration tests own it.
  */
  const password = authBody.body.password as string | undefined;
  const currentPin = authBody.body.currentPin as string | undefined;

  /*
    The gate is `session.hasPin` — the mock's stand-in for a non-null PinHash — NOT `mockState.pin`.
    A first draft used the latter and broke the PIN-setup wizard: `mockState.pin` is the VALUE
    withdraw verifies against and it defaults to MOCK_PIN unconditionally (state.ts), so every
    enrolment looked like a change and answered 422. The test caught it, which is the test doing
    its job — the two fields read alike and mean different things.

    CurrentPin obeys the FULL verifier contract, because the API routes it through IPinVerifier and
    inherits every one of these. A first draft here only incremented a counter, which modelled an
    endpoint that could be brute-forced without ever locking. Measured against the real pipeline:
      malformed currentPin ("abc")   -> 400, errors dict keyed "CurrentPin" (model validation runs
                                        before the action, so it never reaches the verifier)
      wrong, under the threshold     -> 401 INVALID_PIN
      the THIRD wrong                -> 429 PIN_LOCKED
      correct, but already locked    -> 429 PIN_LOCKED (the lock is checked BEFORE the comparison,
                                        so knowing the PIN does not lift it)
  */
  /*
    FORMAT FIRST, and OUTSIDE the hasPin branch. Model validation runs before the action, so it
    cannot know whether a PIN exists: a SUPPLIED currentPin must be well-formed even on the
    enrolment path, where the value is otherwise ignored. Measured — enrolling with
    {pin:"123456", currentPin:"abc"} is a 400, not a 200 (PinReplacementTests).

    A first draft had this check inside the branch below, which made a state-INdependent rule
    state-dependent and let the mock accept a payload the API rejects.
  */
  if (typeof currentPin === 'string' && !/^\d{6}$/.test(currentPin)) {
    // NOTE the absence of a length>0 guard: "" is SUPPLIED-but-malformed, not absent. [Pin] runs at
    // binding and rejects it, so the API answers 400 — measured — where a genuinely missing value
    // gets the 422 below. A first draft skipped empty strings and produced 422 for both.
    return modelStateProblem({ CurrentPin: ['PIN must be exactly 6 digits.'] });
  }

  if (!mockState.session.hasPin) {
    if (typeof password !== 'string' || password.length === 0) {
      // `problem`, not `bffProblem`: the BFF forwards the API's ProblemDetails verbatim through
      // ForwardUpstreamError, so the body carries errorCode and the API's own instance — the same
      // shape the PIN_REQUIRED branch below already returns.
      return problem({
        instance: '/api/auth/pin',
        status: 422,
        errorCode: 'PASSWORD_REQUIRED',
        detail: 'Your password is required to set a PIN.',
      });
    }
    if (password !== MOCK_PASSWORD) {
      return problem({
        instance: '/api/auth/pin',
        status: 401,
        errorCode: 'INVALID_CREDENTIALS',
        detail: 'Invalid password.',
      });
    }
  }

  if (mockState.session.hasPin) {
    if (typeof currentPin !== 'string' || currentPin.length === 0) {
      return problem({
        instance: '/api/auth/pin',
        status: 422,
        errorCode: 'PIN_REQUIRED',
        detail: 'The current PIN is required to change it.',
      });
    }
    const now = Date.now();
    if (mockState.pinLockedUntil && Date.parse(mockState.pinLockedUntil) > now) {
      return pinLockedProblem(mockState.pinLockedUntil, now, 'set-pin');
    }
    if (currentPin !== mockState.pin) {
      mockState.pinAttempts += 1;
      if (mockState.pinAttempts >= 3) {
        mockState.pinAttempts = 0;
        mockState.pinLockedUntil = apiOffsetInstant(now + PIN_LOCKOUT_SECONDS * 1000);
        return pinLockedProblem(mockState.pinLockedUntil, now, 'set-pin');
      }
      return problem({
        instance: '/api/auth/pin',
        status: 401,
        errorCode: 'INVALID_PIN',
        detail: 'Invalid PIN.',
      });
    }
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
  const limited = authRateLimited(request);
  if (limited) return limited;
  // Middleware, so it runs on these too. Unobservable on SUCCESS (a new session replaces the old
  // one), but a login that FAILS leaves the previous session alive — and the real BFF has already
  // slid its clock by then.
  runSessionActivityMiddleware(request);
  const authBody = await readJsonBody(request);
  if (!authBody) {
    return unreadableBodyProblem(await request.clone().text());
  }
  /*
    The framework's model-state short-circuit, and it belongs HERE — after the body parses and
    before anything looks at credentials. `/bff/auth/login` binds the shared `LoginRequest`, so
    `[ApiController]` rejects a malformed one inside the BFF and never calls the API at all.

    Measured on :5000, 2026-08-07 — the mock answered 401 for the first of these:

      {"email":"a@b.dev","password":"Ab1!"}         -> 400, Password format only
      {"email":"nobody@…","password":"ValidPass1!"} -> 401 INVALID_CREDENTIALS

    Placement is also what keeps a malformed password away from the lockout counter, exactly as on
    the real thing: the counter lives past this line.
  */
  const loginInvalid = modelStateFor(authBody.body, LOGIN_REQUEST);
  if (loginInvalid) return loginInvalid;
  // Bound, not raw: the member names are matched case-insensitively, so everything below reads the
  // canonical spelling exactly as an action would after model binding.
  const bound = bindMembers(authBody.body, LOGIN_REQUEST);
  const email = bound.email as string | undefined;
  const password = bound.password as string | undefined;
  const nowMs = Date.now();
  // null for any address that names no account — see `accountForLogin` for the two measurements.
  const account = accountForLogin(email);
  const locked = account ? loginLockedProblem(account, nowMs) : null;
  /*
    ORDER IS THE SECURITY PROPERTY, and I had it backwards on the first pass.

    A wrong password NEVER reveals the lock — it is the same 401 an unknown address gets, so the
    lock cannot become an enumeration oracle for someone spraying passwords (ADR-0012 states this
    as contract, and `AccountLockedException`'s own docblock repeats it). Only the CORRECT password
    on a locked account gets the 429.

    Measured 2026-08-06, which is how the inverted version was caught before it shipped:

      wrong x5 -> 401 INVALID_CREDENTIALS
      6th WRONG -> 401 INVALID_CREDENTIALS   <- still hidden
      CORRECT   -> 429 ACCOUNT_LOCKED        <- only here

    And while locked the counter is NOT touched: `IncrementAndMaybeLockLoginAsync` is skipped
    entirely (`AuthService.cs:130-134`), so a guesser cannot extend the window by keeping at it.
  */
  if (!account || password !== MOCK_PASSWORD) {
    if (account && !locked) {
      const failures = (mockState.loginFailures[account] ?? 0) + 1;
      mockState.loginFailures[account] = failures;
      if (failures >= MAX_LOGIN_ATTEMPTS) {
        // Reset to 0 as the lock latches: from here the WINDOW is authoritative, not the count.
        mockState.loginFailures[account] = 0;
        mockState.loginLockedUntil[account] = apiOffsetInstant(
          nowMs + LOGIN_LOCKOUT_SECONDS * 1000,
        );
      }
    }
    return problem({
      status: 401,
      errorCode: 'INVALID_CREDENTIALS',
      title: 'Unauthorized',
      /*
        FORWARDED, not hand-built — so this one keeps the full API envelope while the sibling
        "Session expired or invalid" 401 three lines up does not. Measured 2026-08-05:

          POST /bff/auth/login {"email":"…","password":"wrong"}
          -> {"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,
              "detail":"Invalid email or password.","instance":"/api/auth/login",
              "errorCode":"INVALID_CREDENTIALS","traceId":"…"}

        Note the trailing period the mock was missing, and that `instance` names the API's route
        rather than the BFF's: the body was generated upstream and copied through verbatim.
      */
      detail: 'Invalid email or password.',
      instance: '/api/auth/login',
    });
  }
  // The one path that reveals the lock: right password, locked account.
  if (locked) return locked;
  // A correct password clears the counter — an expired lock then starts a fresh window at 1.
  delete mockState.loginFailures[account];
  mockState.session = { ...MOCK_USER };
  /*
    A FRESH SESSION IS ALWAYS LEVEL 1. `SessionService.CreateSession` hardcodes
    `AuthLevel = 1, // Level 1 = authenticated via email/password` (SessionService.cs:45) and mints
    a NEW session id, so any elevation the previous session earned is orphaned with it.

    The mock overwrote `session` and left `authLevel` alone, so signing in while already elevated
    kept level 2 — a step-up bypass. Since ADR-0041 (dd84179, 2026-08-13) the level gates only the
    account-number reveal; money moves carry their authorisation in-band (ADR-0042).
    Every other route to a dead session (logout, clock expiry, resetMockState) already reset it;
    these two were the gap.
  */
  mockState.authLevel = 1;
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
  const limited = authRateLimited(request);
  if (limited) return limited;
  // Middleware, so it runs on these too. Unobservable on SUCCESS (a new session replaces the old
  // one), but a login that FAILS leaves the previous session alive — and the real BFF has already
  // slid its clock by then.
  runSessionActivityMiddleware(request);
  const registerBody = await readJsonBody(request);
  if (!registerBody) {
    return unreadableBodyProblem(await request.clone().text());
  }
  // Same gate, five fields — see the login handler above and `dataAnnotations.ts`. It runs before
  // the duplicate-email check below, so a malformed body never reaches ADR-0013's genericised 409.
  const registerInvalid = modelStateFor(registerBody.body, REGISTER_REQUEST);
  if (registerInvalid) return registerInvalid;
  const body = bindMembers(registerBody.body, REGISTER_REQUEST) as {
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
  mockState.authLevel = 1; // fresh session, level 1 — see the login handler above
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

/**
 * POST /bff/auth/reauthenticate — U6.7, the absolute cap.
 *
 * Models the rule that matters: this does not EXTEND the session, it replaces it. The clock restarts
 * from now, so `expiresAt` moves — which is the whole point, and the one thing a mock that merely
 * slid `sessionLastActivity` would get wrong (that is exactly the bug on the screen this replaces).
 * `authLevel` drops to 1 because a new session cannot inherit a PIN elevation proved to the old one.
 *
 * A wrong password leaves the session completely untouched, like the real BFF: it forwards the API's
 * 401 and never revokes anything, so a typo cannot end the session it was meant to save.
 */
const reauthenticate = http.post('*/bff/auth/reauthenticate', async ({ request }) => {
  const limited = authRateLimited(request);
  if (limited) return limited;
  runSessionActivityMiddleware(request);
  const parsed = await readJsonBody(request);
  if (!parsed) {
    return unreadableBodyProblem(await request.clone().text());
  }
  // A dead session has nothing to re-authenticate into. The slide above already no-opped for it:
  // `UpdateActivity` reads the session first and does nothing when it is gone.
  if (!mockState.session) {
    return bffProblem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' });
  }
  if ((parsed.body.password as string | undefined) !== MOCK_PASSWORD) {
    // Re-authentication calls the API's LOGIN endpoint and forwards its answer, so this is the
    // same body the login route produces — `instance` names `/api/auth/login`, not the BFF's own
    // path. Measured rather than assumed to match its sibling, on a throwaway user:
    //   POST /bff/auth/reauthenticate {"password":"wrong"} (live session)
    //   -> {…,"detail":"Invalid email or password.","instance":"/api/auth/login",
    //       "errorCode":"INVALID_CREDENTIALS"}
    return problem({
      status: 401,
      errorCode: 'INVALID_CREDENTIALS',
      title: 'Unauthorized',
      detail: 'Invalid email or password.',
      instance: '/api/auth/login',
    });
  }
  mockState.sessionCreatedAt = Date.now();
  mockState.sessionLastActivity = Date.now();
  mockState.authLevel = 1;
  return HttpResponse.json({
    data: { user: { ...mockState.session }, expiresAt: mockAccessTokenExpiry() },
    message: 'Re-authenticated successfully',
  });
});

const me = http.get('*/bff/auth/me', ({ request }) => {
  /*
    /me IS activity: the BFF slides LastActivity on every cookie-bearing request and excludes only
    the session-status probe (ADR-0018). Modelled so `inactivityExpiresAt` on this endpoint always
    reads "now plus the full window" — as it does against a real BFF, which is why a client that
    polled it for a countdown would see a frozen number in tests too, not just in a browser.

    Expiry is evaluated BEFORE the slide, inside the helper. The other order revives a session that
    died an hour ago simply because something touched it — the bug the real BFF avoids by putting
    the deadline in the session store rather than in the middleware.
  */
  runSessionActivityMiddleware(request);
  if (!mockState.session) {
    // The BFF's own 401: ProblemDetails WITHOUT errorCode.
    return bffProblem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' });
  }
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
  // Logout deletes the cookie; the next request carries none and answers 401 on every /api route,
  // which is also what a cookie that outlived its session gets (measured 2026-09-03, see
  // `sessionActivity`). The mock once drew a 401/403 line between the two; the BFF never did.
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
 * It is also the AUTHENTICATION gate, which this deliberately was not until now — the previous
 * version said so, and called closing it "a separate change with a twenty-file blast radius". This
 * is that change. Until it landed, `/api/accounts/*` and `/api/transactions/*` were reachable in the
 * mock with no session at all, so page tests ran in a state the product cannot produce and nothing
 * anywhere proved those routes were protected.
 *
 * ONE 401, not two, and the BFF writes it itself. Since ADR-0038 (01c0e31, 2026-08-10)
 * `AuthLevelMiddleware` refuses a request with no live session before proxying, with the API's own
 * AUTH_TOKEN_MISSING members, and since d74603c (2026-08-20) it does so on every /api path whatever
 * the method. Three ways of having no usable session all produce the SAME body (measured
 * 2026-09-03):
 *
 *   anonymous            -> 401 errorCode AUTH_TOKEN_MISSING
 *   unresolvable cookie  -> 401 errorCode AUTH_TOKEN_MISSING
 *   revoked after logout -> 401 errorCode AUTH_TOKEN_MISSING
 *
 * The previous version answered the BFF's OWN 401 here ("Session expired or invalid", no errorCode)
 * for the expiry case. That shape is real, but it belongs to `/bff/auth/*`, where the controller
 * reads the session itself — on a proxied `/api/*` route it never appears. So the expiry branch was
 * drift of exactly the kind this gate exists to catch.
 *
 * Not measured directly: a session that dies by CLOCK rather than by revocation, which needs the
 * inactivity window to elapse. `GetAuthLevel` folds a missing, unknown and expired session into the
 * same 0, so `expireMockSessionIfDue` models it as the same condition, and the gap is named here
 * rather than hidden.
 */
const sessionActivity = http.all('*/api/*', ({ request }) => {
  expireMockSessionIfDue();
  if (!mockState.session) {
    const { pathname } = new URL(request.url);
    /*
      A DEAD SESSION ANSWERS EXACTLY LIKE ONE THAT NEVER WAS, on every /api route.

      This branch used to answer 403 STEP_UP_REQUIRED with currentLevel 0 when a cookie was still
      in the browser but no longer resolved, on the three level-2 routes, quoting a 2026-08-05
      measurement. Three BFF changes after that date made the quote false. ADR-0038 (01c0e31,
      2026-08-10) put the no-session 401 BEFORE the level-2 check, for a missing, unknown and
      expired cookie alike — `GetAuthLevel` folds all three into 0 — so a dead cookie stopped
      being a level-0 step-up that day. ADR-0041 (dd84179, 2026-08-13) then emptied
      `PinRequiredPaths`, leaving the reveal as the only level-2 route, and d74603c (2026-08-20)
      extended the session check to every /api path whatever the method.

      Measured 2026-09-03 through the BFF (:5000 -> :7215, Development). Forged = a cookie never
      issued; dead = the cookie a registration issued, replayed after logout answered 200:

        POST /api/transfers            no cookie / forged / dead -> 401 AUTH_TOKEN_MISSING
        POST /api/transfers/internal   forged / dead             -> 401 AUTH_TOKEN_MISSING
        GET  …/full-number             no cookie / forged / dead -> 401 AUTH_TOKEN_MISSING
        GET  /api/accounts             forged / dead             -> 401 AUTH_TOKEN_MISSING
        GET  …/full-number             LIVE level 1              -> 403 X-Auth-Level-Current: 1
        POST /api/transfers `{}`       LIVE level 1              -> 400 model-state (reaches the API)

      The 401 carries the API's AUTH_TOKEN_MISSING members and values (`instance` = the path, a
      fresh `traceId`) and no X-Auth-Level-* header. The BFF writes it before the proxy: the
      middleware returns without calling next, and `AuthLevelMiddlewareTests` pins the recorder's
      forwarded paths empty for the reveal with a forged and with a revoked cookie and for POST
      /api/transfers with a forged one; the internal and /api/accounts rows follow from the same
      branch (`RequiresSession` covers every /api path) and are pinned without a cookie by the
      sessionless theory and the spec sweep. `auth.contract.test.ts` replays a dead cookie on the
      real stack; the flag that once told the two states apart is gone from `state.ts` because it
      no longer changed any answer.

      Why it mattered: 401 drives the global sign-out, 403 STEP_UP_REQUIRED opens the PIN modal. A
      session that timed out mid-transfer met a PIN prompt under MSW and a sign-out in production.
    */
    return authTokenMissing(pathname);
  }
  markMockActivity();
  // Returning nothing falls through to the endpoint handler below.
  return undefined;
});

/**
 * The last handler in the list, and the reason the list can be trusted.
 *
 * `sessionActivity` above is an `http.all` catch-all over every `/api` path, and it returns
 * `undefined` so the real endpoint handler runs after it. MSW treats that as a MATCH, so a request to an `/api` route
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
// NB: never quote this glob inside a BLOCK comment. The `*` + `/` in the middle of it closes the
// comment early, and what follows becomes code — that mistake put a live `api;` statement in this
// file, which compiled, passed the tests, and left the docblock quoting a pattern that does not
// exist. Only eslint's no-unused-expressions caught it.
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
  authoriseTransfer,
  authoriseInternalTransfer,
  transfer,
  transferInternal,
  verifyPin,
  setPin,
  login,
  register,
  reauthenticate,
  me,
  logout,
  sessionStatus,
  // LAST, always. Everything above declines before this speaks.
  unmockedApiRoute,
];
