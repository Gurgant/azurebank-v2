import { describe, expect, it } from 'vitest';
import { asProblem, call } from './client';

/**
 * Endpoints that must reject a caller with NO session at all.
 *
 * A separate file, and not for tidiness. These tests sign in nowhere, and against the mock the
 * broken behaviour they document is itself a state mutation: the mock's `verify-pin` answers 200
 * AND sets `mockState.authLevel = 2`. Sitting in the same file as the step-up test, that elevated
 * the mock and made the reveal endpoint answer 200 — which looked exactly like "the mock never
 * gates the reveal" and is not true; the reveal handler checks the level correctly and had simply
 * been handed an elevated one. Isolating them keeps a real drift from manufacturing a fake second
 * one, and costs zero logins against a rate-limited backend.
 */

describe('contract: endpoints that require a session', () => {
  it('refuses to verify a PIN for a caller with no session', async () => {
    /*
      The sharpest drift the audit found, and not a cosmetic one. The mock answered 200 and elevated
      itself, so a nonexistent session could be walked up to AuthLevel 2 and then used to push a
      full transfer through — the elevation gate is the only gate the money handler consults. The
      real BFF reads the session FIRST and never looks at the PIN.

      Observed: 401 {"title":"Unauthorized","status":401,"detail":"Session expired or invalid"}
      — no errorCode member, so problemBaseQuery synthesises HTTP_401.
    */
    const { status, body } = await call('/bff/auth/verify-pin', {
      method: 'POST',
      anonymous: true,
      body: JSON.stringify({ pin: '123456' }),
    });

    expect(status).toBe(401);
    expect(asProblem(body).errorCode).toBeUndefined();
  });

  it('validates the PIN payload BEFORE it reads the session', async () => {
    /*
      Order matters, and it is measured, not reasoned. ASP.NET binds and validates the model before
      the action body runs, so a malformed PIN is rejected even for a caller with no session at all
      — the session read never happens. Observed anonymously:

        {"pin":"abc"}    -> 400 {"title":"One or more validation errors occurred.",
                                 "errors":{"Pin":["PIN must be exactly 6 digits."]}}
        {"pin":"123456"} -> 401 (the test above)

      Two contracts in one response: the ORDER, and the fact that this is the framework envelope
      keyed by the CLR property (`Pin`) rather than the FluentValidation one keyed camelCase. The
      mock had both wrong — it answered 401 here, and spelled the key `pin`.
    */
    const { status, body } = await call('/bff/auth/verify-pin', {
      method: 'POST',
      anonymous: true,
      body: JSON.stringify({ pin: 'abc' }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('One or more validation errors occurred.');
    expect(Object.keys(problem.errors ?? {})).toContain('Pin');
  });

  it('distinguishes an ABSENT pin from a malformed one', async () => {
    /*
      Three different 400s, not one, and the difference is behavioural rather than cosmetic: `$`
      maps to no form field, so a consumer walking `problem.errors` falls back to its generic bar,
      where `Pin` lands on the PIN input. The mock collapsed all three into the six-digit message,
      which made one of those two UI paths unreachable. Measured anonymously:

        {}            -> errors {"$":["JSON deserialization for type '...VerifyPinRequest' was
                                       missing required properties including: 'pin'."],
                                 "request":["The request field is required."]}
        {"pin":null}  -> errors {"Pin":["The Pin field is required."]}
        {"pin":"abc"} -> errors {"Pin":["PIN must be exactly 6 digits."]}

      The KEYS are asserted, not the messages: the `$` text embeds a fully-qualified .NET type name,
      and pinning that would turn an innocuous DTO rename into a red contract test.
    */
    const absent = await call('/bff/auth/verify-pin', {
      method: 'POST',
      anonymous: true,
      body: JSON.stringify({}),
    });
    expect(absent.status).toBe(400);
    expect(Object.keys(asProblem(absent.body).errors ?? {})).toContain('$');

    const nulled = await call('/bff/auth/verify-pin', {
      method: 'POST',
      anonymous: true,
      body: JSON.stringify({ pin: null }),
    });
    expect(nulled.status).toBe(400);
    expect(Object.keys(asProblem(nulled.body).errors ?? {})).toContain('Pin');
  });

  it('refuses to set a PIN for a caller with no session', async () => {
    // Same gate, same observed body: both actions read the session before doing anything else.
    const { status, body } = await call('/bff/auth/set-pin', {
      method: 'POST',
      anonymous: true,
      body: JSON.stringify({ pin: '654321' }),
    });

    expect(status).toBe(401);
    expect(asProblem(body).errorCode).toBeUndefined();
  });

  it.each([
    ['/api/accounts', '/api/accounts'],
    ['/api/transactions?Page=1&PageSize=2', '/api/transactions'],
    ['/api/transactions/summary', '/api/transactions/summary'],
  ])('rejects an unauthenticated caller on %s', async (path, instance) => {
    /*
      The hole ADR-0029 was opened admitting, now closed. These routes used to be reachable in the
      mock with no session at all, so page tests ran in a state the product cannot produce and
      nothing anywhere proved they were protected.

      The BFF answers this itself: since ADR-0038 `AuthLevelMiddleware` refuses a request with no
      live session before proxying, with the API's own AUTH_TOKEN_MISSING members, and since
      d74603c (2026-08-20) on every /api path whatever the method. This case sends the anonymous
      form; `auth.contract.test.ts` replays a dead cookie on the reveal and on a transfer, and the
      work log's transcript for item 232 (`measure-2026-09-03.txt`, the one client.ts cites) has
      the forged and the dead cookie on /api/accounts too — all identical:

        401 {"type":"https://httpstatuses.com/401","title":"Unauthorized",
             "detail":"Authentication is required to access this resource.",
             "instance":"<path>","errorCode":"AUTH_TOKEN_MISSING","traceId":"<32-hex>"}

      Note the errorCode: this is NOT the BFF's own 401 ("Session expired or invalid", no errorCode)
      asserted above for the PIN routes. Two different 401s, and the mock used to conflate them.
    */
    const { status, body } = await call(path, { anonymous: true });
    const problem = asProblem(body);

    expect(status).toBe(401);
    expect(problem.errorCode).toBe('AUTH_TOKEN_MISSING');
    expect(problem.detail).toBe('Authentication is required to access this resource.');
    /*
      `instance` too, and the table carries the expected PATH rather than reusing the request string
      — the query is not part of it. Without this the three cases assert one identical body and a
      handler echoing the wrong path, or a constant, would satisfy all of them.
    */
    expect(problem.instance).toBe(instance);
  });
  it('hand-builds a 401 with THREE members and nothing else', async () => {
    /*
      `BffAuthController` writes `new ProblemDetails{Title, Detail, Status}` at five sites and
      ASP.NET omits the null members, so `type`, `instance` and any extension are simply absent.
      The mock built this through the shared `problem()` helper, which decorates every body with a
      `type` and a synthesised `traceId` the BFF never writes.

      Observed 2026-08-05, anonymous:
        GET /bff/auth/me -> 401 {"title":"Unauthorized","status":401,"detail":"Session expired or invalid"}
                            Content-Type: application/problem+json; charset=utf-8
    */
    const { status, body } = await call('/bff/auth/me', { anonymous: true });

    expect(status).toBe(401);
    expect(Object.keys(body as object).sort()).toEqual(['detail', 'status', 'title']);
    expect((body as { detail: string }).detail).toBe('Session expired or invalid');
  });
});
