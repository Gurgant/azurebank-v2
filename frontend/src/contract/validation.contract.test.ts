import { beforeAll, describe, expect, it } from 'vitest';
import { asProblem, call, login } from './client';

/**
 * The backend has TWO validation envelopes, and which one you get depends on the endpoint.
 *
 *   * Endpoints with a FluentValidation validator throw `ValidationException`, which
 *     `ValidationExceptionHandler` renders by hand: title "Validation Failed", a `detail`, and
 *     `type: https://httpstatuses.com/400`.
 *   * Endpoints without one are rejected by `[ApiController]`'s model-state short-circuit, rendered
 *     by the framework's own `ProblemDetailsFactory`: title "One or more validation errors
 *     occurred.", **no detail at all**, and an rfc9110 `type`.
 *
 * Both were observed on the wire on 2026-07-31. The mock emitted the FluentValidation envelope for
 * everything, so `HistoryPage` — which renders `problem.detail` for exactly this endpoint — showed
 * a sentence under MSW and its generic fallback in production. That is the class of bug a
 * mock-only suite cannot see, because both sides agreed with each other and neither agreed with the
 * server.
 *
 * QUERY KEYS ARE PASCALCASE HERE ON PURPOSE — `PageSize`, not `pageSize` — because that is what
 * `apiSlice` actually sends and what the OpenAPI spec declares. The first draft used camelCase and
 * exposed a different divergence by accident: ASP.NET's model binder is case-INSENSITIVE, so the
 * real stack validated `pageSize=999` and answered 400, while the mock reads the key with a
 * case-SENSITIVE `params.get('PageSize')`, missed it, fell back to the default page size and
 * answered 200. Real, but on a path no consumer takes; making the mock case-insensitive is recorded
 * as a follow-up rather than smuggled in here, and asserting the app's own casing keeps this test
 * about the envelope instead of about the binder.
 */

beforeAll(async () => {
  expect((await login()).status).toBe(200);
});

describe('contract: validation envelopes', () => {
  it('uses the FRAMEWORK envelope where no FluentValidation validator runs', async () => {
    /*
      `TransactionController` injects validators only for deposit and withdraw, so the list and
      summary actions never throw ValidationException and never reach ValidationExceptionHandler.

      Observed: 400 {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1",
                     "title":"One or more validation errors occurred.","status":400,
                     "errors":{"PageSize":["PageSize must be between 1 and 100."]},"traceId":"..."}
    */
    const { status, body } = await call('/api/transactions?Page=1&PageSize=999');
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('One or more validation errors occurred.');
    // The discriminator between the two envelopes, and the member HistoryPage renders.
    expect(problem.detail).toBeUndefined();
    expect(problem.errors).toBeDefined();
  });

  it('keys validation errors by the CLR property name, in PascalCase', async () => {
    /*
      ASP.NET keys ValidationProblemDetails by the ModelState key, which is the bound property name.
      `AddApiControllers` sets `PropertyNamingPolicy` but never `DictionaryKeyPolicy`, so dictionary
      keys are written verbatim — `PageSize`, not `pageSize`.

      Observed: errors: {"PageSize":["PageSize must be between 1 and 100."]}

      No consumer is broken today (CreateAccountDialog normalises via `toFieldName`, the rename
      dialogs compare lowercased, and the money paths read only `Object.values`), but the mock
      disagreed with itself — camelCase in five handlers, PascalCase in three — so a future consumer
      would have been written against whichever one it happened to meet first.
    */
    const { status, body } = await call('/api/transactions?Page=1&PageSize=999');
    const keys = Object.keys(asProblem(body).errors ?? {});

    expect(status).toBe(400);
    expect(keys).toContain('PageSize');
    expect(keys).not.toContain('pageSize');
  });

  it('emits BOTH casings from ONE endpoint, and never both at once', async () => {
    /*
      The case that makes `toFieldName` necessary rather than decorative, on the endpoint the app
      actually posts to. `CreateAccountRequest.Name` carries DataAnnotations and `Type` carries
      none, so the same route answers from two different layers:

        {"name":"x","type":"Checking"}     -> model state, BEFORE the action runs
             title "One or more validation errors occurred.", errors {"Name":[…]}   PascalCase
        {"name":"Valid Name","type":"99"}  -> FluentValidation, INSIDE the action
             title "Validation Failed", errors {"type":[…]}                         camelCase

      ("99" binds: the strict converter rejects JSON *numbers*, but hands any non-empty string to
      `Enum.TryParse`, which accepts the numeric form and yields an undefined member for
      `IsInEnum()` to catch.)

      And they are MUTUALLY EXCLUSIVE — with both fields invalid, model state short-circuits and
      the `type` error is never reported. A consumer that assumed one casing per endpoint, or that
      expected to see every problem in one response, would be wrong on this route.

      Both requests are rejected, so neither creates an account and this test leaves no state.
    */
    const post = (body: unknown) =>
      call('/api/accounts', { method: 'POST', body: JSON.stringify(body) });

    const annotation = await post({ name: 'x', type: 'Checking' });
    expect(annotation.status).toBe(400);
    const annotationProblem = asProblem(annotation.body);
    expect(Object.keys(annotationProblem.errors ?? {})).toContain('Name');
    expect(annotationProblem.title).toBe('One or more validation errors occurred.');

    const validator = await post({ name: 'Valid Name', type: '99' });
    expect(validator.status).toBe(400);
    const validatorProblem = asProblem(validator.body);
    expect(Object.keys(validatorProblem.errors ?? {})).toContain('type');
    expect(validatorProblem.title).toBe('Validation Failed');

    // Both invalid -> the annotation layer wins alone; `type` never appears.
    const both = asProblem((await post({ name: 'x', type: '99' })).body);
    expect(Object.keys(both.errors ?? {})).toEqual(['Name']);
  });

  it('answers a MISSING required member from the deserialiser, keyed by the document root', async () => {
    /*
      A third shape, distinct from both validation envelopes, and the one where the mock used to
      accept a WRITE the API refuses: it defaulted a missing `type` to Checking and answered 201.

      `CreateAccountRequest.Type` is `required`, so System.Text.Json fails before model state has a
      property to name. The key is therefore the JSON path of the ROOT — a bare `$`, NOT `$.type` —
      plus a second entry named after the action's body parameter, which is literally `request`.

      Measured 2026-08-04:
        POST /api/accounts {"name":"No Type Probe"}
          -> {"$":["JSON deserialization for type '…CreateAccountRequest' was missing required
                    properties including: 'type'."],
              "request":["The request field is required."]}

      Asserted on keys and status, not on the exact C# type name, so the contract does not break if
      the DTO is ever renamed — the SHAPE is what a consumer branches on. Rejected, so no account
      is created and this test leaves no state on either target.
    */
    const { status, body } = await call('/api/accounts', {
      method: 'POST',
      body: JSON.stringify({ name: 'No Type Probe' }),
    });
    const keys = Object.keys(asProblem(body).errors ?? {});

    expect(status).toBe(400);
    expect(keys).toContain('$');
    expect(keys).toContain('request');
    // NOT the member-scoped path: that is what a *bad value* produces, not a missing one.
    expect(keys).not.toContain('$.type');
  });

  it('answers a NUMERIC enum from the deserialiser, keyed by the path to the member', async () => {
    /*
      Three inputs to the same field, three different answers — the distinction CodeRabbit caught
      me collapsing on this PR:

        {"type":12345}   -> deserialiser, keyed `$.type`   (the converter refuses the token)
        {"type":"99"}    -> FluentValidation, keyed `type`  (TryParse accepts the numeric STRING,
                                                             then IsInEnum rejects the value)
        (absent)         -> deserialiser, keyed `$`         (fails at the document root)

      Measured 2026-08-04:
        {"errors":{"request":[…],
                   "$.type":["Integer values are not allowed for enum 'AccountType'. …"]}}
    */
    const { status, body } = await call('/api/accounts', {
      method: 'POST',
      body: JSON.stringify({ name: 'Valid Name', type: 12345 }),
    });
    const keys = Object.keys(asProblem(body).errors ?? {});

    expect(status).toBe(400);
    expect(keys).toContain('$.type');
    // The path form, NOT the root — the deserialiser knew which member failed.
    expect(keys).not.toContain('$');
  });

  it('quotes the idempotency header requirement in the server’s exact words', async () => {
    /*
      A `detail` string, which is the one member of this envelope the app renders verbatim
      (`moneyProblem` falls through to `problem.detail`). The mock had a paraphrase — dropping the
      quotes around the header name and the "on this endpoint" clause — so the sentence a user read
      under MSW was not the sentence production shows them.

      Measured 2026-08-04, deposit with no Idempotency-Key:
        {"title":"Bad Request","status":400,"errorCode":"IDEMPOTENCY_KEY_MISSING",
         "detail":"The 'Idempotency-Key' header is required on this endpoint."}

      Rejected before any money moves, so this leaves no state on either target.
    */
    const { status, body } = await call('/api/transactions/deposit', {
      method: 'POST',
      body: JSON.stringify({ accountId: '00000000-0000-0000-0000-000000000000', amount: 5 }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.errorCode).toBe('IDEMPOTENCY_KEY_MISSING');
    expect(problem.detail).toBe("The 'Idempotency-Key' header is required on this endpoint.");
  });

  it('distinguishes a value that would not BIND from one that fails [Range]', async () => {
    /*
      Two failures, two sentences, and the mock used to give the `[Range]` one to both. `?Page=abc`
      never binds, so the annotation is never evaluated and the BINDER answers with its own
      template — which for a filter property names the member. Measured 2026-08-04:

        ?Page=abc -> {"Page":["The value 'abc' is not valid for Page."]}
        ?Page=0   -> {"Page":["Page must be at least 1."]}
    */
    const unbindable = await call('/api/transactions?Page=abc');
    expect(unbindable.status).toBe(400);
    expect(asProblem(unbindable.body).errors?.Page).toEqual([
      "The value 'abc' is not valid for Page.",
    ]);

    const outOfRange = await call('/api/transactions?Page=0');
    expect(outOfRange.status).toBe(400);
    expect(asProblem(outOfRange.body).errors?.Page).toEqual(['Page must be at least 1.']);
  });

  it.each([
    ['exponent notation', '1e2'],
    ['a decimal point', '1.0'],
    ['an Int32 overflow', '3000000000'],
  ])('does not bind %s, even though JS Number() would', async (_case, value) => {
    /*
      `Number('1e2')` is 100 and `Number.isInteger(100)` is true, so the obvious guard accepts
      three values the API refuses. Measured 2026-08-04 — each answers with the BINDER message,
      not the range one:
        ?Page=1e2 / 1.0 / 3000000000 -> "The value '<x>' is not valid for Page."
    */
    const { status, body } = await call(`/api/transactions?Page=${value}`);

    expect(status).toBe(400);
    expect(asProblem(body).errors?.Page).toEqual([`The value '${value}' is not valid for Page.`]);
  });

  it.each([
    ['leading zeros', '007', 7],
    ['a leading sign', '%2B5', 5],
    ['HEX', '0x10', 16],
  ])('DOES bind %s — the binder is more permissive than it looks', async (_case, value, page) => {
    /*
      The other half, and the reason the fix is a measured allow-list rather than a strict decimal
      parse: rejecting these would make the mock stricter than the server all over again. Hex was
      the surprise — measured `?Page=0x10` -> page 16, not a 400.
    */
    const { status, body } = await call(`/api/transactions?Page=${value}&PageSize=5`);

    expect(status).toBe(200);
    expect((body as { pagination?: { page?: number } }).pagination?.page).toBe(page);
  });

  it('keys the azuretag rename PascalCase, in the framework envelope', async () => {
    /*
      This route can emit no other shape: there is no `UpdateAzureTagRequest` validator and
      `UserController` injects none, so `[AzureTagQuery]` (a DataAnnotation) is the only gate and
      model state answers before the action. The mock used the FluentValidation envelope with a
      camelCase key — the one shape this endpoint cannot produce.

      Measured 2026-08-04: PATCH /api/users/me/azuretag {"azureTag":"Bad Tag!"}
        -> {"title":"One or more validation errors occurred.",
            "errors":{"AzureTag":["AzureTag must start with a letter…"]}}
    */
    const { status, body } = await call('/api/users/me/azuretag', {
      method: 'PATCH',
      body: JSON.stringify({ azureTag: 'Bad Tag!' }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('One or more validation errors occurred.');
    expect(Object.keys(problem.errors ?? {})).toContain('AzureTag');
  });

  it('validates the rename BODY before it looks the account up', async () => {
    /*
      `[ApiController]` runs model state before the action, so a bad name is a 400 even when the id
      names nothing — the 404 is only reachable once the body is clean. The mock 404'd first.

      Measured 2026-08-04 against an id that does not exist:
        {"name":"x"}                   -> 400 {"Name":["Account name must be between 2 and 100 characters."]}
        {"name":"Perfectly Fine Name"} -> 404 ACCOUNT_NOT_FOUND
    */
    const missing = '019f7b3f-0000-7000-8000-0000000000ff';

    const badBody = await call(`/api/accounts/${missing}`, {
      method: 'PATCH',
      body: JSON.stringify({ name: 'x' }),
    });
    expect(badBody.status).toBe(400);
    expect(Object.keys(asProblem(badBody.body).errors ?? {})).toContain('Name');

    const goodBody = await call(`/api/accounts/${missing}`, {
      method: 'PATCH',
      body: JSON.stringify({ name: 'Perfectly Fine Name' }),
    });
    expect(goodBody.status).toBe(404);
    expect(asProblem(goodBody.body).errorCode).toBe('ACCOUNT_NOT_FOUND');
  });

  it('reports EVERY bad property in one pass, not the first one it meets', async () => {
    /*
      ASP.NET validates all bound properties together and returns them in a single
      ValidationProblemDetails. The mock checked Page/PageSize, then AccountId, then the dates, each
      returning immediately — so a request wrong in three ways reported one of them and hid the
      rest, and a UI marking every offending field could only ever mark one. Measured:

        ?Page=0&PageSize=1000&AccountId=notaguid
          -> {"Page":["Page must be at least 1."],
              "PageSize":["PageSize must be between 1 and 100."],
              "AccountId":["The value 'notaguid' is not valid for AccountId."]}

      Note the message suffix — `... is not valid for AccountId.` — which the mock also dropped.
    */
    const { status, body } = await call(
      '/api/transactions?Page=0&PageSize=1000&AccountId=notaguid',
    );
    const errors = asProblem(body).errors ?? {};

    expect(status).toBe(400);
    expect(Object.keys(errors).sort()).toEqual(['AccountId', 'Page', 'PageSize']);
    expect(errors.AccountId).toEqual(["The value 'notaguid' is not valid for AccountId."]);
  });

  it('rejects an inverted date window and keys BOTH bounds', async () => {
    /*
      The cross-field rule reports against both members, so the UI can mark either input.

      Observed: 400 {"title":"One or more validation errors occurred.",
                     "errors":{"ToDate":["FromDate must be earlier than or equal to ToDate."],
                               "FromDate":["FromDate must be earlier than or equal to ToDate."]}}

      This one is a REGRESSION GUARD as much as a contract: the mock used to answer 422 here, found
      only by running the real stack, and fixed in PR #64.
    */
    const { status, body } = await call(
      '/api/transactions/summary?FromDate=2026-12-01&ToDate=2026-01-01',
    );
    const keys = Object.keys(asProblem(body).errors ?? {});

    expect(status).toBe(400);
    expect(keys.sort()).toEqual(['FromDate', 'ToDate']);
  });
});

/**
 * The auth routes' own model-state gate.
 *
 * Three cases, and each is a POST to `/bff/auth/*` that spends an auth permit — the limiter runs
 * before the body is read, so a request the gate rejects costs exactly what a successful sign-in
 * costs. This block takes the suite from six permits to nine.
 *
 * That ceiling is a LOCAL constraint, not a CI one: `ci.yml` starts the BFF with
 * `--RateLimiting:AuthPermitLimit=1000` precisely so the three real-stack suites cannot rate-limit
 * each other, and says so in its own comment. What it does mean is that running
 * `test:contract:real` twice inside a minute on a dev machine will 429 — which it already did at
 * six, since two runs are twelve, and at nine a single run leaves only one permit spare. Wait out
 * the window rather than debugging the failure.
 *
 * The exhaustive matrix — absent vs null, whitespace, attribute ordering, wrong JSON types, the
 * register/login wording difference — lives in `mocks/fidelity.test.ts`, where it costs nothing.
 *
 * What these two buy that the mock-only ones cannot: they run against the REAL stack too, so they
 * fail if the mock and the BFF ever disagree about this envelope again. They were added because
 * the mock answered 401 here, having never modelled the gate at all.
 *
 * The `beforeAll` login above is this block's control: a well-formed body still reaches the
 * credential check and answers 200, so a mock that simply 400s every auth POST cannot pass.
 */
describe('contract: the auth model-state gate', () => {
  it('rejects a malformed password before it ever looks at credentials', async () => {
    /*
      `/bff/auth/login` binds the shared `LoginRequest`, so `[ApiController]` short-circuits inside
      the BFF and the API is never called. That makes this the FRAMEWORK envelope — no `detail`, no
      `instance`, no `errorCode` — even though the DTO belongs to the API.

      Observed 2026-08-07 on :5000:
        {"email":"a@b.dev","password":"Ab1!"}
        -> 400 {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1",
                "title":"One or more validation errors occurred.","status":400,
                "errors":{"Password":["Password must be 8-128 characters and Password must contain
                          at least one uppercase, one lowercase, one digit, and one special
                          character."]},"traceId":"00-…"}

      A wrong-but-well-formed password answers 401 INVALID_CREDENTIALS instead; that half is pinned
      mock-side, because tripping it here would spend another auth permit for no extra coverage.
    */
    const { status, body } = await call('/bff/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email: 'a@b.dev', password: 'Ab1!' }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('One or more validation errors occurred.');
    expect(problem.type).toBe('https://tools.ietf.org/html/rfc9110#section-15.5.1');
    // The discriminators against the API's hand-written envelope — see envelopes.contract.test.ts.
    expect(problem.instance).toBeUndefined();
    expect(problem.errorCode).toBeUndefined();
    expect(problem.detail).toBeUndefined();
    expect((problem.errors as Record<string, string[]>).Password).toEqual([
      'Password must be 8-128 characters and Password must contain at least one uppercase, ' +
        'one lowercase, one digit, and one special character.',
    ]);
  });

  it('validates a registration against the TRIMMED name, not the submitted one', async () => {
    /*
      `RegisterRequest.FirstName`/`LastName` trim inside their setters specifically so validation
      sees the normalised value — its own comment says a raw "  a  " would otherwise satisfy the
      {2,50} rule and land a one-character name. Asserted on the wire because a setter is exactly
      the kind of detail a mock reimplements by eye and gets wrong.

      Observed 2026-08-07: firstName "  a  ", lastName "  b  " -> both the length message and the
      pattern message, per field, in attribute order. Nothing is created; this never reaches the
      service.
    */
    const { status, body } = await call('/bff/auth/register', {
      method: 'POST',
      body: JSON.stringify({
        azureTag: 'probe',
        email: 'p@q.dev',
        password: 'ValidPass1!',
        firstName: '  a  ',
        lastName: '  b  ',
      }),
    });
    const errors = asProblem(body).errors as Record<string, string[]>;

    expect(status).toBe(400);
    expect(errors.FirstName).toEqual([
      'First name must be between 2 and 50 characters.',
      'Name can only contain letters, spaces, hyphens, and apostrophes.',
    ]);
    expect(errors.LastName).toEqual([
      'Last name must be between 2 and 50 characters.',
      'Name can only contain letters, spaces, hyphens, and apostrophes.',
    ]);
    // Everything else was well-formed, so nothing else may appear.
    expect(Object.keys(errors).sort()).toEqual(['FirstName', 'LastName']);
  });

  it('keys a member of the wrong JSON type by its path, and says so the same way', async () => {
    /*
      The one case where the mock deliberately emits LESS than the server does, so it is the one
      that most needs asserting against both. A number cannot bind to a `string`, so this fails in
      the reader — key `$.email`, not `Email` — and the real message ends with a byte offset:

        {"email":123,"password":"ValidPass1!"}
        -> 400 {"request":["The request field is required."],
                "$.email":["The JSON value could not be converted to System.String.
                            Path: $.email | LineNumber: 0 | BytePositionInLine: 13."]}

      That offset is not reproducible from a parsed body — measured at 13, 14, 11 and 11 for a
      number, a boolean, an object and an array in otherwise identical bodies — so the mock stops
      the sentence at the path and this asserts the prefix. `toContain` is what lets one assertion
      hold for both targets; `toEqual` here would pass on the mock and fail on the server, which is
      precisely the false green this suite exists to prevent.
    */
    const { status, body } = await call('/bff/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email: 123, password: 'ValidPass1!' }),
    });
    const errors = asProblem(body).errors as Record<string, string[]>;

    expect(status).toBe(400);
    expect(Object.keys(errors).sort()).toEqual(['$.email', 'request']);
    expect(errors['$.email'][0]).toContain(
      'The JSON value could not be converted to System.String. Path: $.email',
    );
    expect(errors.request).toEqual(['The request field is required.']);
  });
});
