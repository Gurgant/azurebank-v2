import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { beforeAll, describe, expect, it } from 'vitest';
import { asProblem, call, firstAccountId, idempotencyKey, login } from './client';
import { FIXTURES } from './target';

/** The committed contract, read as a third party would — the number the client promises. */
const SPEC_URL = new URL('../../../docs/api/openapiv1.json', import.meta.url);

/**
 * The money endpoints, where a wrong error shape is a wrong screen on a payment.
 *
 * Signs in and STOPS THERE — deliberately. These endpoints needed a level-2 session until ADR-0041;
 * they now carry the PIN in the request body and the API verifies it, so a level-1 session is
 * enough to reach them. Leaving the `elevate()` in would have made this suite unable to notice a
 * regression that re-gated transfers at level 2, because it would have satisfied the gate before
 * ever sending a transfer.
 */

beforeAll(async () => {
  expect((await login()).status).toBe(200);
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
        // ADR-0041 made `pin` a required member. WITHOUT it the request never reaches
        // FluentValidation at all: System.Text.Json refuses to deserialise a missing required
        // property, so the answer is the framework envelope keyed `$`/`request` — measured:
        //   400 {"title":"One or more validation errors occurred.",
        //        "errors":{"$":["JSON deserialization for type '…InternalTransferRequest' was
        //                        missing required properties including: 'pin'."],
        //                  "request":["The request field is required."]}}
        // which is the OTHER envelope, and would silently turn this test into a check of the
        // deserialiser rather than of the same-account rule it exists for.
        pin: FIXTURES.pin,
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
        // ADR-0041 made `pin` a required member. WITHOUT it the request never reaches
        // FluentValidation at all: System.Text.Json refuses to deserialise a missing required
        // property, so the answer is the framework envelope keyed `$`/`request` — measured:
        //   400 {"title":"One or more validation errors occurred.",
        //        "errors":{"$":["JSON deserialization for type '…InternalTransferRequest' was
        //                        missing required properties including: 'pin'."],
        //                  "request":["The request field is required."]}}
        // which is the OTHER envelope, and would silently turn this test into a check of the
        // deserialiser rather than of the same-account rule it exists for.
        pin: FIXTURES.pin,
      }),
    });
    const problem = asProblem(body);

    // Asserted here too: an envelope check that never looks at the status would pass just as
    // happily against a 500 that happened to carry the same title.
    expect(status).toBe(400);
    expect(problem.title).toBe('Validation Failed');
    expect(problem.detail).toBe('One or more validation errors occurred.');
  });

  it('a malformed PIN is the MODEL-BINDING envelope, not the validator one', async () => {
    /*
      The probe the same-account tests above stopped being when they started sending a valid PIN.

      Two envelopes live on this one endpoint and a client branching on them must be able to tell
      them apart. `[Pin]` is a DataAnnotation, so it fires in the automatic model-state filter —
      before the action, before FluentValidation — and reports against the BOUND PROPERTY, hence
      PascalCase `Pin`. The same-account rule next door is FluentValidation and reports camelCase
      `toAccountId`. Sending a request that is invalid in BOTH ways proves which one wins.

      Observed: 400 {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1",
                     "title":"One or more validation errors occurred.",
                     "errors":{"Pin":["PIN must be exactly 6 digits."]}}
      — note the absence of `detail` and `instance`, which the validator envelope carries.

      SENT TO THE MINT, not to the transfer. ADR-0042's flip deleted `InternalTransferRequest.Pin`,
      so the transfer can no longer produce a `Pin` key at all and this probe would have quietly
      become a test of the same-account validator — the precise failure the comment above describes
      it as having escaped once already. The mint is where the PIN is presented now, it carries the
      same `[Pin]` DataAnnotation, and the same-account rule sits beside it on the same endpoint, so
      the two envelopes still meet and can still be told apart.
    */
    const id = await firstAccountId();

    const { status, body } = await call('/api/transfers/internal/authorizations', {
      method: 'POST',
      body: JSON.stringify({
        fromAccountId: id,
        toAccountId: id,
        amount: 10,
        pin: '12',
      }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('One or more validation errors occurred.');
    // The two absences are half the distinction and were only asserted in prose: the framework
    // envelope carries NEITHER, the validator one carries both. Measured on the real API.
    expect(problem.detail).toBeUndefined();
    expect(problem.instance).toBeUndefined();
    expect(Object.keys(problem.errors ?? {})).toContain('Pin');
    expect(Object.keys(problem.errors ?? {})).not.toContain('toAccountId');
  });

  it('refuses a deposit one cent above the PUBLISHED maximum, in the framework envelope', async () => {
    /*
      The bound the form promises is the contract's, and the contract is what this reads first:
      `DepositRequest.properties.amount.maximum` in the committed document. Then the stack is asked
      one cent above it. Nothing here mutates a balance — a refused deposit writes nothing.

      Why a real-stack pin exists for this sentence at all: the mock quoted "$0.01 and $100,000.00"
      as observed for a fortnight after the server had stopped rendering currency symbols (3769dc9),
      and every mock-side test agreed with the mock. Measured 2026-09-03 on POST
      /api/transactions/deposit for 500000, 1000000 and 100000.01 — identical:
        400 {"title":"One or more validation errors occurred.",
             "errors":{"Amount":["Amount must be between 0.01 EUR and 100000.00 EUR"]}}
      and 100000 -> 201. No trailing period on the wire; the document's description carries one.
    */
    const spec = JSON.parse(readFileSync(fileURLToPath(SPEC_URL), 'utf8')) as {
      components: { schemas: Record<string, { properties: Record<string, { maximum?: number }> }> };
    };
    const maximum = spec.components.schemas.DepositRequest.properties.amount.maximum;
    if (maximum === undefined) {
      throw new Error('DepositRequest.amount.maximum is missing from the committed contract');
    }
    expect(maximum).toBe(100000);

    const { status, body } = await call('/api/transactions/deposit', {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey() },
      body: JSON.stringify({
        accountId: await firstAccountId(),
        amount: maximum + 0.01,
        description: 'contract probe: one cent over',
      }),
    });
    const problem = asProblem(body);

    expect(status).toBe(400);
    expect(problem.title).toBe('One or more validation errors occurred.');
    expect(problem.errors?.Amount).toEqual(['Amount must be between 0.01 EUR and 100000.00 EUR']);
  });
});
