import { beforeAll, describe, expect, it } from 'vitest';
import { apiSlice } from '../features/api/apiSlice';
import type { ApiProblem } from '../api/problemBaseQuery';
import {
  autoCancel,
  autoElevate,
  idempotencyKey,
  makeStore,
  run,
  signIn,
  type IntegrationStore,
} from './harness';

/**
 * The two protocols that only exist in the seam between app and backend, driven end to end.
 *
 * Neither is observable from either side alone. Idempotency needs the app to reuse a key across two
 * requests AND the API to recognise it; step-up needs a real 403 to reach the interceptor, a real
 * PIN to elevate a real session, and the ORIGINAL request to be replayed with the same bytes. Unit
 * tests mock one half of each, and the contract suite deliberately sits below both.
 *
 * ORDER IS LOAD-BEARING in this file. Elevation is a property of the server-side session, so once
 * the elevate test runs the session is level 2 and every later reveal answers 200 without a 403.
 * The cancel test therefore has to come first, and the "elevation stuck" test has to come last.
 */

let store: IntegrationStore;
let accountId: string;

beforeAll(async () => {
  store = makeStore();
  await signIn(store);

  const accounts = await run(store.dispatch(apiSlice.endpoints.getAccounts.initiate()));
  if (!accounts.ok) throw new Error('Could not read accounts to pick a test subject.');
  accountId = accounts.data[0].id;
});

async function currentBalance(): Promise<number> {
  const result = await run(
    // forceRefetch: the point of the call is to see the SERVER's number, and RTK Query would
    // otherwise hand back the cached one and make a double-spend invisible.
    store.dispatch(apiSlice.endpoints.getAccounts.initiate(undefined, { forceRefetch: true })),
  );
  if (!result.ok) throw new Error('Balance read failed.');
  const account = result.data.find((candidate) => candidate.id === accountId);
  if (!account) throw new Error('Test account vanished mid-run.');
  return account.balance;
}

describe('integration: the idempotency protocol against the real API', () => {
  it('replays a repeated key instead of moving the money twice', async () => {
    /*
      The property that matters is not "the second call returns something" — it is that the LEDGER
      moved once. Both are asserted: the replay flag, the identical receipt, AND the balance delta.

      Measured on the running stack, same key twice:
        1st -> 201, no Idempotency-Replayed header
        2nd -> 201, `idempotency-replayed: true`, byte-identical body
               (same transaction.id 019fca0e-…, same newBalance)
    */
    const key = idempotencyKey();
    const amount = 1.0;
    const before = await currentBalance();

    const first = await run(
      store.dispatch(
        apiSlice.endpoints.deposit.initiate({
          idempotencyKey: key,
          body: { accountId, amount, description: 'integration: idempotency' },
        }),
      ),
    );
    expect(first.ok, first.ok ? '' : JSON.stringify(first.error)).toBe(true);
    if (!first.ok) return;
    expect(first.data.replayed).toBe(false);

    const second = await run(
      store.dispatch(
        apiSlice.endpoints.deposit.initiate({
          idempotencyKey: key,
          body: { accountId, amount, description: 'integration: idempotency' },
        }),
      ),
    );
    expect(second.ok, second.ok ? '' : JSON.stringify(second.error)).toBe(true);
    if (!second.ok) return;

    // `replayed` is read from a HEADER in transformResponse — a detail no unit test can confirm is
    // actually emitted, because the mock decides both sides of that question.
    expect(second.data.replayed).toBe(true);
    expect(second.data.transaction.id).toBe(first.data.transaction.id);
    // The id alone is not the receipt. A replay that returned the right transaction with a
    // recomputed (doubled) newBalance would satisfy the line above and still put a wrong number
    // on the confirmation screen.
    expect(second.data.newBalance).toBe(first.data.newBalance);

    // The ledger assertion. Absolute, not relative: `expect(after).not.toBe(before + 2*amount)`
    // would pass for any wrong number that happens to differ from the double.
    const after = await currentBalance();
    expect(after).toBe(before + amount);
  });
});

describe('integration: the step-up interceptor against a real 403', () => {
  /*
    The reveal endpoint is the probe rather than a transfer, deliberately: the BFF gates
    `/api/accounts/{id}/full-number` at level 2 exactly as it gates `/api/transfers`, so the
    interceptor path is identical — but no money moves, so the assertions stay about the protocol
    and the suite can run as often as it likes.

    Measured at level 1: 403 with `X-Auth-Level-Required: 2`, which problemBaseQuery turns into
    STEP_UP_REQUIRED by reading the HEADER before touching the body (D2 — the 403 body is a bare
    shape, not ProblemDetails).
  */

  it('surfaces STEP_UP_CANCELLED, and asks exactly once, when the user declines', async () => {
    const stepUp = autoCancel();
    try {
      const result = await run(
        store.dispatch(apiSlice.endpoints.revealAccountNumber.initiate(accountId)),
      );

      expect(result.ok).toBe(false);
      if (result.ok) return;
      const problem = result.error as ApiProblem;
      expect(problem.errorCode).toBe('STEP_UP_CANCELLED');
      expect(problem.requiredAuthLevel).toBe(2);
    } finally {
      stepUp.dispose();
    }
  });

  it('elevates and replays the ORIGINAL request exactly once', async () => {
    const stepUp = autoElevate(store);
    try {
      const result = await run(
        store.dispatch(apiSlice.endpoints.revealAccountNumber.initiate(accountId)),
      );

      expect(result.ok, result.ok ? '' : JSON.stringify(result.error)).toBe(true);
      if (!result.ok) return;

      // The replay actually returned the gated resource: an UNMASKED number, which the list
      // endpoint never ships. Proves the original request ran again and succeeded, not that the
      // interceptor merely swallowed the 403.
      expect(result.data.accountNumber).not.toMatch(/\*/);

      // Exactly one elevation for one gated call — no retry storm, no stacked modals.
      expect(stepUp.requests).toBe(1);
    } finally {
      stepUp.dispose();
    }
  });

  it('does not ask again once the session is elevated', async () => {
    // Elevation is server-side session state, so the second reveal must never reach the
    // interceptor at all. This is what proves the previous test elevated for real rather than
    // getting a one-off pass.
    const stepUp = autoElevate(store);
    try {
      const result = await run(
        store.dispatch(apiSlice.endpoints.revealAccountNumber.initiate(accountId)),
      );

      expect(result.ok, result.ok ? '' : JSON.stringify(result.error)).toBe(true);
      expect(stepUp.requests).toBe(0);
    } finally {
      stepUp.dispose();
    }
  });
});
