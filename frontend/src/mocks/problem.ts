import { HttpResponse } from 'msw';

/**
 * Builds the API's real error envelope: RFC 9457 ProblemDetails + `errorCode` + a bare
 * 32-hex `traceId`, exactly as the backend handlers emit it (AppExceptionHandler /
 * GlobalExceptionHandler / ValidationExceptionHandler). Two contract quirks preserved on
 * purpose:
 *  - validation 400s carry an `errors` dictionary but NO errorCode;
 *  - extra members (available, retryAfterSeconds, lockedUntil...) are spread at top level.
 */
export interface ProblemInit {
  status: number;
  errorCode?: string;
  title?: string;
  detail?: string;
  /**
   * `Instance = httpContext.Request.Path` — the request path, and ONLY on the API's three
   * hand-written handlers (plus the JWT events and two BFF middlewares). See the table in
   * `problemBody` for where it is present and where it is measurably absent; omitting it here
   * is a real state, not an oversight.
   */
  instance?: string;
  /** field -> messages, as the ValidationExceptionHandler emits (400 only, no errorCode). */
  errors?: Record<string, string[]>;
  /** Extra top-level members (available, retryAfterSeconds, lockedUntil, ...). */
  extensions?: Record<string, unknown>;
  headers?: Record<string, string>;
}

const DEFAULT_TITLES: Record<number, string> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  413: 'Payload Too Large',
  422: 'Unprocessable Entity',
  429: 'Too Many Requests',
  500: 'Internal Server Error',
};

/** Deterministic fake trace id: bare 32-hex, like Activity.TraceId.ToString(). */
export function fakeTraceId(): string {
  return Array.from({ length: 32 }, () => Math.floor(Math.random() * 16).toString(16)).join('');
}

/*
  Which responses actually carry `instance`, measured on the running stack 2026-08-05. The audit
  entry that prompted this said "EVERY mock error body omits instance"; both halves of that are
  wrong, and applying it as written would have added the member to paths that provably lack it.

    PRESENT — the API's own handlers, which all assign `Instance = httpContext.Request.Path`:
      FluentValidation 400   POST /api/accounts {"type":"99"}  -> instance "/api/accounts"
      domain 404/422/400     AppExceptionHandler               -> instance "/api/accounts/{id}"
      JWT 401                OnChallenge                       -> instance "/api/accounts"

    ABSENT — nothing in the framework's model-state path sets it, and the BFF hand-builds its
    own bodies without it:
      model-state 400        ?Page=abc, and DataAnnotation failures  (-> modelStateProblem)
      deserialisation 400    missing/uncoercible member              (-> missingMemberProblem)
      terminal 404           no route match
      BFF 401                GET /bff/auth/me with no cookie: {"title","status","detail"} only
      BFF step-up 403        a bare non-ProblemDetails shape entirely (see stepUp403)

  So `instance` is a property of WHICH HANDLER answered, exactly like the title and the traceId
  format — one more axis of the same distinction #75 and #78 drew.
*/
export function problemBody(init: ProblemInit) {
  const { status, errorCode, title, detail, instance, errors, extensions } = init;
  /*
    A validation 400 is NOT a generic 400. `ValidationExceptionHandler` writes its own
    ProblemDetails with `Title = "Validation Failed"` and
    `Detail = "One or more validation errors occurred."`, where `AppExceptionHandler` would have
    said "Bad Request" and the exception's own message. The mock was giving every validation
    failure the generic title and an EMPTY detail, so any surface that renders `problem.detail`
    showed nothing at all for the one class of error that always has something to say.

    Keyed off `errors` rather than off the status, because that dictionary is exactly what
    distinguishes the two handlers.
  */
  const isValidation = status === 400 && errors !== undefined;
  /*
    Member ORDER is the C# insertion order, not a preference. `ProblemDetails` serialises its
    declared members first (type, title, status, detail, instance) and then `Extensions` in the
    order each handler adds them:

      AppExceptionHandler        errorCode, traceId, then the exception's own Details
      ValidationExceptionHandler traceId, then errors

    One template reproduces both, because no response ever carries an errorCode AND an errors
    dictionary. The mock previously emitted traceId BEFORE errorCode, which matches neither.
  */
  return {
    type: `https://httpstatuses.com/${status}`,
    title: title ?? (isValidation ? 'Validation Failed' : (DEFAULT_TITLES[status] ?? 'Error')),
    status,
    detail: detail ?? (isValidation ? 'One or more validation errors occurred.' : ''),
    ...(instance ? { instance } : {}),
    ...(errorCode ? { errorCode } : {}),
    traceId: fakeTraceId(),
    ...(errors ? { errors } : {}),
    ...extensions,
  };
}

export function problem(init: ProblemInit) {
  return HttpResponse.json(problemBody(init), {
    status: init.status,
    headers: { 'Content-Type': 'application/problem+json', ...init.headers },
  });
}

/**
 * The OTHER validation envelope, and the reason it has to exist.
 *
 * The API rejects a bad request in one of two completely different ways, and which one you get
 * depends on whether the endpoint has a FluentValidation validator:
 *
 *   * WITH a validator — `ValidationException` reaches `ValidationExceptionHandler`, which writes
 *     the body above by hand: title "Validation Failed", a `detail`, `type` httpstatuses.com/400,
 *     and a BARE 32-hex `traceId` (it writes `Activity.Current?.TraceId.ToString()` itself).
 *   * WITHOUT one — `[ApiController]` short-circuits on model state and the FRAMEWORK renders it:
 *     title "One or more validation errors occurred.", **no `detail` member at all**, an rfc9110
 *     `type`, and a full W3C traceparent as the `traceId`.
 *
 * `TransactionController` injects validators only for deposit and withdraw, so its list and summary
 * actions take the second path. The mock used the first for everything, which is why `HistoryPage`
 * — which renders `problem.detail` for exactly that endpoint — showed a sentence under MSW and its
 * generic fallback in production. Verified on the running stack, both shapes, on 2026-07-31.
 *
 * Keys are the CLR property names (`PageSize`, `FromDate`), because ASP.NET keys ModelState by the
 * bound property and `AddApiControllers` never sets a `DictionaryKeyPolicy`.
 */
export function modelStateProblem(errors: Record<string, string[]>) {
  return HttpResponse.json(
    {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      errors,
      traceId: fakeTraceParent(),
    },
    { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

/**
 * The framework's answer when the JSON could not be DESERIALISED — a required member was absent.
 *
 * This is a third thing, distinct from both validation envelopes, and the mock had no model of it
 * at all: it answered 201 and persisted a row the API refuses outright.
 *
 * A `required` C# member is enforced by System.Text.Json, not by `[Required]`, so the failure
 * happens before model state has a property to name. The key is therefore the JSON path of the
 * document ROOT — a bare `$`, not `$.<member>` — and the framework adds a second entry named after
 * the action's body parameter, which is literally `request` in every controller here.
 *
 * Measured 2026-08-04, `POST /api/accounts {"name":"No Type Probe"}`:
 *
 *   {"title":"One or more validation errors occurred.","status":400,
 *    "errors":{"$":["JSON deserialization for type 'AzureBank.Shared.DTOs.Account.CreateAccountRequest'
 *                    was missing required properties including: 'type'."],
 *              "request":["The request field is required."]}}
 *
 * `toFieldName('$')` is `'$'`, so it matches no form field and the message correctly reaches the
 * form-level error rather than being aimed at a control that does not exist.
 */
export function missingMemberProblem(clrType: string, ...members: string[]) {
  const list = members.map((m) => `'${m}'`).join(', ');
  return modelStateProblem({
    $: [
      `JSON deserialization for type '${clrType}' was missing required properties including: ${list}.`,
    ],
    request: ['The request field is required.'],
  });
}

/**
 * The sibling case: the member is PRESENT but its JSON could not be converted.
 *
 * Same envelope, different key — the path names the member (`$.type`) because the deserialiser got
 * far enough to know which one failed, where an ABSENT required member fails at the root (`$`).
 * Conflating the two is easy and wrong, so they are separate functions rather than one with a flag.
 *
 * Measured 2026-08-04, `POST /api/accounts {"name":"Valid Name","type":12345}`:
 *
 *   {"errors":{"request":["The request field is required."],
 *              "$.type":["Integer values are not allowed for enum 'AccountType'.
 *                         Use string values: Checking, Savings, Investment"]}}
 *
 * Note this is NOT the same as a bad enum *string*: `"99"` deserialises fine (the converter defers
 * to `Enum.TryParse`, which accepts the numeric form) and is then caught by FluentValidation's
 * `IsInEnum` — the "Validation Failed" envelope keyed camelCase `type`. A JSON **number** never
 * reaches the validator at all.
 */
export function invalidJsonValueProblem(path: string, message: string) {
  return modelStateProblem({
    [path]: [message],
    request: ['The request field is required.'],
  });
}

/**
 * The third deserialisation shape: the body could not be READ at all.
 *
 * Sibling of the two above and, until measured, assumed to be the FluentValidation envelope with
 * a `$` key for both cases. Neither half held. `POST /api/accounts` on the running stack,
 * 2026-08-05:
 *
 *   body ""               -> {"":["A non-empty request body is required."],
 *                             "request":["The request field is required."]}
 *   body "not json at all" -> {"$":["'not json at all' is an invalid JSON literal. Expected the
 *                                    literal 'null'. Path: $ | LineNumber: 0 | BytePositionInLine: 1."],
 *                             "request":["The request field is required."]}
 *
 * Two things to keep straight. The envelope is the FRAMEWORK one (rfc9110 type, no `detail`), not
 * the handler-written one — this reaches `[ApiController]` model state, never a validator. And an
 * EMPTY body is keyed by the empty string, not by `$`: a **sixth** distinct key shape, after
 * `Name`, `at`, `$`, `$.type` and FluentValidation's camelCase. `toFieldName('')` is `''`, which
 * matches no control, so the sentence lands as a form-level error — the right place for it.
 *
 * The malformed-JSON sentence stays an approximation on purpose: the framework's text embeds the
 * offending token and the parse position, so imitating it exactly would be fake precision that
 * changes with every payload. The KEY and the ENVELOPE are what a client branches on, and those
 * are now faithful.
 */
export function unreadableBodyProblem(raw: string) {
  /*
    `raw.length`, NOT `raw.trim().length` — the distinction is ABSENT vs UNPARSEABLE, and a body of
    "   " is present. It was a `trim()` here before (carried over unchanged when this moved out of
    handlers.ts), which put whitespace in the empty-body branch and answered with the wrong key.
    CodeRabbit raised it; measured before accepting, since the question is what the API does:

      POST /api/accounts  body "   "
      -> {"$":["The input does not contain any JSON tokens. Expected the input to start with a
                valid JSON token, when isFinalBlock is true. Path: $ | LineNumber: 0 |
                BytePositionInLine: 3."], "request":[…]}

    So whitespace reaches System.Text.Json and fails there like any other garbage — the `$` branch.
  */
  return raw.length === 0
    ? modelStateProblem({
        '': ['A non-empty request body is required.'],
        request: ['The request field is required.'],
      })
    : modelStateProblem({
        $: ['The request body could not be read as JSON.'],
        request: ['The request field is required.'],
      });
}

/** W3C traceparent (`00-<32hex>-<16hex>-01`), which is what the framework path emits. */
function fakeTraceParent(): string {
  const hex = (n: number) =>
    Array.from({ length: n }, () => Math.floor(Math.random() * 16).toString(16)).join('');
  return `00-${hex(32)}-${hex(16)}-01`;
}
