import { http, HttpResponse } from 'msw';
import { createOpenApiHttp } from 'openapi-msw';
import type { paths } from '../api/schema';
import type { AccountType } from '../api/enums';
import {
  invalidJsonValueProblem,
  missingMemberProblem,
  modelStateProblem,
  problem,
} from './problem';
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
 * generated Zod schema mirrors as `.min(0.01).max(100000)`. The message reads in dollars because
 * the API's own does; this is the mock quoting the contract, not the app's EUR display.
 *
 * THE ENVELOPE, THE KEY AND THE WORDING WERE ALL WRONG, and are now measured. `[MoneyRange]` is a
 * DataAnnotation, so an out-of-range amount is a MODEL-STATE failure: framework envelope, key
 * `Amount` (the CLR property), and ONE message for both bounds — not the two the mock invented.
 * Observed 2026-08-04 on `POST /api/transactions/deposit`, for amounts 0, 0.005, 100000.01 and
 * 250000 — all four identical:
 *
 *   title "One or more validation errors occurred."
 *   {"Amount":["Amount must be between $0.01 and $100,000.00"]}
 *
 * Note there is NO trailing period on the wire, although `schema.d.ts`'s description carries one.
 * The OpenAPI description and the runtime message are different strings; the wire wins.
 *
 * A bad SCALE is the other envelope entirely — `[MoneyRange]` does not check decimals, so
 * `.ValidMoneyScale()` catches it inside the action and answers "Validation Failed" keyed
 * lowercase `amount`. Same field, two casings, decided by which layer rejected it.
 */
function rejectBadAmount(amount: unknown): ReturnType<typeof modelStateProblem> | null {
  if (typeof amount !== 'number' || !Number.isFinite(amount) || amount < 0.01 || amount > 100_000) {
    return modelStateProblem({ Amount: ['Amount must be between $0.01 and $100,000.00'] });
  }
  return null;
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
    extensions: { instance: pathname },
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

/**
 * The 400 an unreadable body earns.
 *
 * Model binding rejects it before any action runs, so this is a validation shape and not a domain
 * error. The empty-body sentence is ASP.NET's own and is stable; the malformed-JSON one is an
 * approximation on purpose — the framework's text embeds the parse position
 * ("'n' is an invalid start of a value. Path: $ | LineNumber: 0 …"), which depends on the payload
 * and would be fake precision to imitate. The KEY is faithful: `$`, the JSON path.
 */
function badRequestBody(raw: string) {
  return raw.trim().length === 0
    ? problem({ status: 400, errors: { $: ['A non-empty request body is required.'] } })
    : problem({ status: 400, errors: { $: ['The request body could not be read as JSON.'] } });
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

/**
 * Money as the API's own messages render it: C#'s `:C` on the server's ambient culture, which on
 * this stack is en-US — `$50,042.00`, grouped, two decimals, a leading `$`.
 *
 * Three separate `detail` strings embed a formatted amount and the mock omitted all three, so the
 * user saw a sentence with the numbers under MSW and a different sentence without them in
 * production. Measured 2026-08-04:
 *
 *   "Insufficient funds. Available: $50,042.00, Requested: $99,000.00"
 *   "Cannot delete account with non-zero balance. Current balance: $50,042.00"
 *   "Amount must be between $0.01 and $100,000.00"        (already handled in rejectBadAmount)
 *
 * NOT the app's EUR display — this is the mock quoting the server's wording, and the server
 * formats in dollars regardless of what the UI later chooses to render.
 */
function serverCurrency(amount: number): string {
  return `$${amount.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

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
  const parsed = await readJsonBody(request);
  if (!parsed) {
    return response.untyped(badRequestBody(await request.clone().text()));
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
        status: 400,
        errors: { type: ['Invalid account type. Must be Checking, Savings, or Investment.'] },
      }),
    );
  }

  const index = mockState.accounts.length;
  const account = {
    id: `019f7b3f-0000-7000-8000-00000000c${String(index).padStart(3, '0')}`,
    accountNumber: `AB-****-****-${70 + index}`,
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

  const account = mockState.accounts.find((a) => a.id === params.id);
  if (!account) {
    return response.untyped(notFound('Account', params.id));
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
    return response.untyped(badRequestBody(await request.clone().text()));
  }
  // `UpdateAccountRequest` overrides the required wording; the KEY is still the CLR property.
  // Measured: PATCH /api/accounts/{id} {"name":""}
  //   -> {"Name":["Account name is required.","Account name must be between 2 and 100 characters."]}
  const renameError = rejectBadAccountName(parsed.body.name, RENAME_NAME_REQUIRED);
  if (renameError) {
    return response.untyped(modelStateProblem({ Name: renameError }));
  }

  const account = mockState.accounts.find((a) => a.id === params.id);
  if (!account) {
    return response.untyped(notFound('Account', params.id));
  }
  account.name = parsed.body.name as string;
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
        // The API always appends the amount, so the user is told WHAT to empty, not just that
        // they must. Measured: "Cannot delete account with non-zero balance. Current balance: $50,042.00"
        detail: `Cannot delete account with non-zero balance. Current balance: ${serverCurrency(account.balance)}`,
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

  const ordered = [...mockState.transactions]
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
  const fromDate = queryParam(params, 'FromDate') ?? monthStart;
  const toDate = queryParam(params, 'ToDate') ?? now.toISOString();
  const fromMs = Date.parse(fromDate);
  const toMs = Date.parse(toDate);

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
        status: 403,
        errorCode: 'ACCESS_DENIED',
        detail: 'You do not have access to this account.',
      }),
    );
  }

  let totalIncome = 0;
  let totalExpenses = 0;
  let pendingCount = 0;
  for (const t of mockState.transactions) {
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
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_INVALID',
        detail: 'The Idempotency-Key header must be a single non-empty UUID.',
      }),
    );
  }

  const parsedBody = await readJsonBody(request);
  if (!parsedBody) {
    return response.untyped(badRequestBody(await request.clone().text()));
  }
  const raw = parsedBody.raw;
  const fp = fingerprint(raw);
  const stored = mockState.idempotency.get(`deposit|${parsedKey}`);
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
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_INVALID',
        detail: 'The Idempotency-Key header must be a single non-empty UUID.',
      }),
    );
  }

  const parsedBody = await readJsonBody(request);
  if (!parsedBody) {
    return response.untyped(badRequestBody(await request.clone().text()));
  }
  const raw = parsedBody.raw;
  const fp = fingerprint(raw);
  const stored = mockState.idempotency.get(`withdraw|${parsedKey}`);
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
        // The API embeds BOTH amounts so the user knows the shortfall without doing the
        // arithmetic. Measured: "Insufficient funds. Available: $50,042.00, Requested: $99,000.00"
        detail: `Insufficient funds. Available: ${serverCurrency(available)}, Requested: ${serverCurrency(amount)}`,
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
 * PATCH /api/users/me/azuretag — rename the caller's own public handle (ADR-0015). Mirrors the
 * service: normalize to lower-case, 409 AZURE_TAG_TAKEN when another user already holds it (our
 * seeded recipients stand in for "other users"), otherwise update the session and echo the new tag.
 */
const renameAzureTag = api.patch('/api/users/me/azuretag', async ({ request, response }) => {
  const parsed = await readJsonBody(request);
  if (!parsed) {
    return response.untyped(badRequestBody(await request.clone().text()));
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
    return response.untyped(
      modelStateProblem({
        AzureTag: [
          'AzureTag must start with a letter and contain only lowercase letters, numbers, and underscores.',
        ],
      }),
    );
  }

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
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_INVALID',
        detail: 'The Idempotency-Key header must be a single non-empty UUID.',
      }),
    );
  }

  const parsedBody = await readJsonBody(request);
  if (!parsedBody) {
    return response.untyped(badRequestBody(await request.clone().text()));
  }
  const raw = parsedBody.raw;
  const fp = fingerprint(raw);
  const stored = mockState.idempotency.get(`transfer|${parsedKey}`);
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

  const body = parsedBody.body as {
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
        // The API embeds BOTH amounts so the user knows the shortfall without doing the
        // arithmetic. Measured: "Insufficient funds. Available: $50,042.00, Requested: $99,000.00"
        detail: `Insufficient funds. Available: ${serverCurrency(available)}, Requested: ${serverCurrency(amount)}`,
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
        status: 400,
        errorCode: 'IDEMPOTENCY_KEY_INVALID',
        detail: 'The Idempotency-Key header must be a single non-empty UUID.',
      }),
    );
  }

  const parsedBody = await readJsonBody(request);
  if (!parsedBody) {
    return response.untyped(badRequestBody(await request.clone().text()));
  }
  const raw = parsedBody.raw;
  const fp = fingerprint(raw);
  const stored = mockState.idempotency.get(`internal|${parsedKey}`);
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

  const body = parsedBody.body as {
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
        // The API embeds BOTH amounts so the user knows the shortfall without doing the
        // arithmetic. Measured: "Insufficient funds. Available: $50,042.00, Requested: $99,000.00"
        detail: `Insufficient funds. Available: ${serverCurrency(from.balance)}, Requested: ${serverCurrency(amount)}`,
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
  const authBody = await readJsonBody(request);
  if (!authBody) {
    return badRequestBody(await request.clone().text());
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
    to push a full transfer through, since the level is the only thing the money handlers consult.
  */
  if (expireMockSessionIfDue() || !mockState.session) {
    return problem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' });
  }
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
 * POST /bff/auth/set-pin — set/overwrite the user's PIN (SetPinController, AuthLevel 1: no
 * old PIN and no step-up required). A bad format is the API's VALIDATION_ERROR shape (400,
 * `errors` dict, NO errorCode — the FE synthesizes VALIDATION_ERROR). On success it flips
 * session.hasPin so the invalidated /me refetch clears the withdraw gate, and clears any
 * prior lock so the freshly-set PIN works immediately.
 */
const setPin = http.post('*/bff/auth/set-pin', async ({ request }) => {
  const authBody = await readJsonBody(request);
  if (!authBody) {
    return badRequestBody(await request.clone().text());
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
    to push a full transfer through, since the level is the only thing the money handlers consult.
  */
  if (expireMockSessionIfDue() || !mockState.session) {
    return problem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' });
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
  const authBody = await readJsonBody(request);
  if (!authBody) {
    return badRequestBody(await request.clone().text());
  }
  const email = authBody.body.email as string | undefined;
  const password = authBody.body.password as string | undefined;
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
  const registerBody = await readJsonBody(request);
  if (!registerBody) {
    return badRequestBody(await request.clone().text());
  }
  const body = registerBody.body as {
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
  const parsed = await readJsonBody(request);
  if (!parsed) {
    return badRequestBody(await request.clone().text());
  }
  // Checked first, and WITHOUT marking activity: a dead session has nothing to re-authenticate into.
  if (expireMockSessionIfDue() || !mockState.session) {
    return problem({ status: 401, title: 'Unauthorized', detail: 'Session expired or invalid' });
  }
  if ((parsed.body.password as string | undefined) !== MOCK_PASSWORD) {
    return problem({
      status: 401,
      errorCode: 'INVALID_CREDENTIALS',
      title: 'Unauthorized',
      detail: 'Invalid email or password',
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
 * It is also the AUTHENTICATION gate, which this deliberately was not until now — the previous
 * version said so, and called closing it "a separate change with a twenty-file blast radius". This
 * is that change. Until it landed, `/api/accounts/*` and `/api/transactions/*` were reachable in the
 * mock with no session at all, so page tests ran in a state the product cannot produce and nothing
 * anywhere proved those routes were protected.
 *
 * ONE 401, not two, and that is measured rather than reasoned. The proxy does not answer for itself:
 * it forwards whatever token the session yields, and the API rejects a request that arrives without
 * one. Three ways of having no usable session all produce the SAME body:
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
 * Not directly produced: a session that dies by CLOCK rather than by revocation, which needs the
 * inactivity window to elapse. It is the same condition — a cookie that no longer resolves to a live
 * session — and all three measured forms of that agree, so it is modelled the same way and the gap
 * is named here rather than hidden.
 */
const sessionActivity = http.all('*/api/*', ({ request }) => {
  expireMockSessionIfDue();
  if (!mockState.session) {
    return authTokenMissing(new URL(request.url).pathname);
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
