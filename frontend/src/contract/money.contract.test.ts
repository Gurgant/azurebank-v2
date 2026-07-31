import { beforeAll, describe, expect, it } from 'vitest';
import { asProblem, call, elevate, firstAccountId, idempotencyKey, login } from './client';

/**
 * The money endpoints, where a wrong error shape is a wrong screen on a payment.
 *
 * Reaching them needs a level-2 session, so each test signs in and elevates with the seeded PIN.
 */

beforeAll(async () => {
  expect((await login()).status).toBe(200);
  expect((await elevate()).status).toBe(200);
});

describe('contract: money', () => {
  it('answers a same-account internal transfer with a FIELD error, not a domain code', async () => {
    /*
      Three simultaneous differences from what the mock used to say, and the mock was wrong on all
      three: it answered 422 with `errorCode: 'SAME_ACCOUNT_TRANSFER'`.

      The rule lives in `InternalTransferRequestValidator` (`ToAccountId.NotEqual(FromAccountId)`),
      and `ValidateAndThrowAsync` runs BEFORE the service is called — so the service's own
      `BusinessRuleException(SAME_ACCOUNT_TRANSFER)` is unreachable over HTTP and documented as
      defence-in-depth for non-HTTP callers. The backend is not buggy here; the mock invented a code
      the API cannot emit, and `InternalTransferPage` carries copy for a branch only MSW could reach.

      Observed: 400 {"type":"https://httpstatuses.com/400","title":"Validation Failed",
                     "detail":"One or more validation errors occurred.",
                     "instance":"/api/transfers/internal",
                     "errors":{"toAccountId":["Cannot transfer to the same account."]}}
    */
    const id = await firstAccountId();

    const { status, body } = await call('/api/transfers/internal', {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey() },
      body: JSON.stringify({
        fromAccountId: id,
        toAccountId: id,
        amount: 10,
        description: 'contract probe',
      }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.errorCode).toBeUndefined();
    expect(Object.keys(problem.errors ?? {})).toContain('toAccountId');
  });

  it('uses the VALIDATOR envelope where a FluentValidation validator does run', async () => {
    /*
      The other half of the two-envelope contract asserted in validation.contract.test.ts. This
      endpoint HAS a validator, so unlike the transactions list it carries a `detail` and the
      hand-written title. Asserting both sides is what makes the pair a real distinction rather
      than a restatement.
    */
    const id = await firstAccountId();

    const { status, body } = await call('/api/transfers/internal', {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey() },
      body: JSON.stringify({
        fromAccountId: id,
        toAccountId: id,
        amount: 10,
        description: 'contract probe',
      }),
    });
    const problem = asProblem(body);

    // Asserted here too: an envelope check that never looks at the status would pass just as
    // happily against a 500 that happened to carry the same title.
    expect(status).toBe(400);
    expect(problem.title).toBe('Validation Failed');
    expect(problem.detail).toBe('One or more validation errors occurred.');
  });
});
