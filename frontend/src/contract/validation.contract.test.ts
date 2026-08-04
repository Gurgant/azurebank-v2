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
