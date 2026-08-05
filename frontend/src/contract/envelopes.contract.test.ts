import { beforeAll, describe, expect, it } from 'vitest';
import { asProblem, call, elevate, firstAccountId, login } from './client';

/**
 * The SUCCESS envelopes — the positive controls.
 *
 * These already agreed when the suite was written, and that is the point of including them: a gate
 * made only of known failures proves nothing about the parts that work, and cannot tell "the mock
 * was fixed" from "the assertion was written to match whatever the mock did".
 *
 * Note the two list shapes are genuinely DIFFERENT, and both are deliberate: accounts return
 * `{data, message}` while transactions return `{data, pagination}` with no `message`. A reader who
 * assumed one envelope for the whole API would be wrong.
 */

beforeAll(async () => {
  expect((await login()).status).toBe(200);
});

describe('contract: success envelopes', () => {
  it('returns accounts as {data, message} with the number already masked', async () => {
    // Observed: {"data":[{"id","accountNumber":"AB-****-****-01","name","type","balance",
    //                     "isPrimary","createdAt"}],"message":null}
    const { status, body } = await call('/api/accounts');
    const envelope = body as { data?: Record<string, unknown>[]; pagination?: unknown };

    expect(status).toBe(200);
    expect(Array.isArray(envelope.data)).toBe(true);
    expect(envelope).toHaveProperty('message');
    expect(envelope.pagination).toBeUndefined();

    const account = envelope.data?.[0] ?? {};
    expect(Object.keys(account).sort()).toEqual(
      ['accountNumber', 'balance', 'createdAt', 'id', 'isPrimary', 'name', 'type'].sort(),
    );
    /*
      Masked SERVER-side, and asserted here because it is a privacy property rather than a
      formatting one: the full number exists only behind the step-up-gated /full-number endpoint,
      so a mock that returned it unmasked would let a test "prove" a reveal that never happened.
    */
    expect(account.accountNumber).toMatch(/^AB-\*{4}-\*{4}-\d{2}$/);
  });

  it('returns transactions as {data, pagination} with the pagination keys spelled out', async () => {
    // Observed: {"data":[],"pagination":{"page":1,"pageSize":2,"totalItems":0,"totalPages":0,
    //                                    "hasNextPage":false,"hasPreviousPage":false}}
    const { status, body } = await call('/api/transactions?page=1&pageSize=2');
    const envelope = body as { data?: unknown[]; pagination?: Record<string, unknown> };

    expect(status).toBe(200);
    expect(Array.isArray(envelope.data)).toBe(true);
    expect(Object.keys(envelope.pagination ?? {}).sort()).toEqual(
      ['hasNextPage', 'hasPreviousPage', 'page', 'pageSize', 'totalItems', 'totalPages'].sort(),
    );
  });
});

/**
 * `instance`, and the reason it is only SOMETIMES there.
 *
 * The audit item behind these tests read "every mock error body omits `instance`", which invited a
 * sweep that added the member everywhere. Measured on the running stack, that would have been
 * wrong on both counts: one mock site already emitted it, and the member is genuinely absent from
 * the framework's model-state path, the terminal 404 and every body the BFF hand-builds.
 *
 * So `instance` is a fingerprint of WHICH HANDLER answered, exactly like the title and the traceId
 * format. Asserting its absence is therefore as load-bearing as asserting its presence — and both
 * run against the mock AND the real stack, which is the only thing that keeps this honest.
 */
describe('contract: the instance member', () => {
  it('carries the request path on a handler-written problem, WITHOUT the query string', async () => {
    /*
      A FluentValidation rejection reaches `ValidationExceptionHandler`, which assigns
      `Instance = httpContext.Request.Path`. The query string is deliberately included in the
      request and deliberately expected to be absent from the answer: `Request.Path` and
      `Request.QueryString` are separate members, so `pathname + search` would have been a
      plausible and wrong implementation.

      Observed 2026-08-05:
        POST /api/accounts?probe=1 {"name":"Valid Name","type":"99"}
        -> {"type":"https://httpstatuses.com/400","title":"Validation Failed","status":400,
            "detail":"One or more validation errors occurred.","instance":"/api/accounts",...}
    */
    const { status, body } = await call('/api/accounts?probe=1', {
      method: 'POST',
      body: JSON.stringify({ name: 'Valid Name', type: '99' }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('Validation Failed');
    expect(problem.instance).toBe('/api/accounts');
  });

  it('omits it entirely on the framework model-state path', async () => {
    /*
      Nothing in `[ApiController]`'s short-circuit sets Instance — no `InvalidModelStateResponseFactory`
      and no `CustomizeProblemDetails` override exists in either host, and the default
      `ProblemDetailsFactory` leaves it null. The two envelopes are therefore distinguishable by
      this member alone.

      Observed: {"type":"...rfc9110#section-15.5.1","title":"One or more validation errors
                 occurred.","status":400,"errors":{"Page":[...]},"traceId":"00-...-01"}  <- no instance
    */
    const { status, body } = await call('/api/transactions?Page=abc');
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('One or more validation errors occurred.');
    expect(problem.instance).toBeUndefined();
  });

  it('carries the SUBSTITUTED path, ids and all, on a domain 404', async () => {
    /*
      `AppExceptionHandler` sets the same member, and what lands there is the concrete path rather
      than the route template — so a client reading `instance` sees the id it asked about.

      Observed: instance "/api/transactions/00000000-0000-0000-0000-000000000009" alongside
                errorCode ACCOUNT_NOT_FOUND (yes, ACCOUNT_ — a backend quirk the mock mirrors
                rather than corrects; see `notFound` in handlers.ts).
    */
    const unknown = '00000000-0000-0000-0000-000000000009';
    const { status, body } = await call(`/api/transactions/${unknown}`);
    const problem = asProblem(body);

    expect(status).toBe(404);
    expect(problem.instance).toBe(`/api/transactions/${unknown}`);
  });

  it('quotes the idempotency-key format requirement in the server’s exact words', async () => {
    /*
      `IdempotencyException.KeyInvalid()` — measured verbatim, and NOT what the mock said. The
      sibling KeyMissing sentence was aligned in an earlier PR and this one was missed, which is
      why it is here now rather than in that PR: found only because a probe of mine used a
      non-UUID key by accident.

      Observed: {"detail":"The 'Idempotency-Key' header must be a valid UUID.",
                 "instance":"/api/transactions/deposit","errorCode":"IDEMPOTENCY_KEY_INVALID"}
    */
    const { status, body } = await call('/api/transactions/deposit', {
      method: 'POST',
      headers: { 'Idempotency-Key': 'not-a-uuid' },
      body: JSON.stringify({ accountId: await firstAccountId(), amount: 1.0 }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.errorCode).toBe('IDEMPOTENCY_KEY_INVALID');
    expect(problem.detail).toBe("The 'Idempotency-Key' header must be a valid UUID.");
    expect(problem.instance).toBe('/api/transactions/deposit');
  });
});

describe('contract: cache directives on the reveal', () => {
  beforeAll(async () => {
    expect((await elevate()).status).toBe(200);
  });

  it('forbids caching the ONE response that carries an unmasked number', async () => {
    /*
      ASVS 14.3.2. `AccountController.GetFullAccountNumber` sets both headers by hand, and YARP
      forwards them untouched, so the browser sees them through the BFF.

      Asserted on the wire rather than in the C#, because a header is exactly the kind of thing an
      intermediary can strip: this is the test that would notice if the proxy ever stopped
      forwarding it.
    */
    const { status, headers } = await call(`/api/accounts/${await firstAccountId()}/full-number`);

    expect(status).toBe(200);
    expect(headers.get('cache-control')).toBe('no-store');
    expect(headers.get('pragma')).toBe('no-cache');
  });

  it('does not put those headers on an ordinary 200', async () => {
    // The negative control. Without it the assertion above passes just as happily against a stack
    // that marks EVERY response no-store, which would be a different system with different
    // performance and the same green tick.
    const { status, headers } = await call('/api/accounts');

    expect(status).toBe(200);
    expect(headers.get('cache-control')).toBeNull();
    expect(headers.get('pragma')).toBeNull();
  });
});
