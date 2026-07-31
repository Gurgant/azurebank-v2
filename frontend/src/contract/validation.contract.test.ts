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
