/**
 * Executable contract: level-2 step-up (AuthLevelMiddleware + BffAuthController semantics). The
 * product's step-up interceptor is built against these.
 *
 * The vehicle is `/api/accounts/{id}/full-number`, NOT a transfer. Until ADR-0041 it was the
 * transfer, and re-pointing it is the honest half of that change: transfers left the level-2 gate
 * (the PIN now travels in the body and the API verifies it), so a step-up contract asserted against
 * them would have been pinning a 403 the BFF no longer emits. Reveal keeps the session model
 * deliberately (decision D3a) — it is a GET with no amount and no payee, so there is nothing for an
 * in-band credential to be bound TO — which makes it the endpoint where this contract is still real.
 */

import { mockState, seedMockSession } from './state';

const VERIFY_URL = '/bff/auth/verify-pin';

/** The one endpoint still behind the level-2 gate (D3a). */
function reveal() {
  return fetch(`/api/accounts/${mockState.accounts[0].id}/full-number`);
}

function verifyPin(pin: string) {
  return fetch(VERIFY_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pin }),
  });
}

/*
  A session, seeded, because the step-up routes now demand one.

  The mock used to answer /bff/auth/verify-pin for a caller with NO session at all, so these
  tests never had to establish one and quietly exercised a state the product cannot reach: the
  real BFF reads the session first and answers 401. Aligning the mock (contract gate) made that
  visible. Seeding is the faithful fix — a user reaching a PIN prompt is, by construction,
  already signed in.
*/
beforeEach(() => {
  seedMockSession();
});

describe('step-up handler (level-2 semantics)', () => {
  it('403 at level 1 with the BARE step-up body (NOT ProblemDetails) + level headers', async () => {
    const res = await reveal();

    expect(res.status).toBe(403);
    expect(res.headers.get('X-Auth-Level-Required')).toBe('2');
    expect(res.headers.get('X-Auth-Level-Current')).toBe('1');
    const body = await res.json();
    // AuthLevelMiddleware's bare shape — no errorCode, no traceId. Pinned so the future
    // interceptor is forced to branch on the header, never on ProblemDetails parsing.
    expect(body).toEqual({
      type: 'STEP_UP_REQUIRED',
      title: 'PIN Verification Required',
      detail: 'This operation requires PIN verification',
      requiredLevel: 2,
      currentLevel: 1,
      status: 403,
    });
  });

  it('a WRONG pin is HTTP 200 with verified:false — never a 4xx', async () => {
    const res = await verifyPin('000000');

    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.data.verified).toBe(false);
    expect(body.data.authLevel).toBe(1);
  });

  it('correct pin elevates to level 2 and the reveal succeeds', async () => {
    const ok = await verifyPin('123456');
    expect((await ok.json()).data.authLevel).toBe(2);

    const res = await reveal();
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.data.accountNumber).toBeTruthy();
  });

  it('mock state resets between tests: back to level 1', async () => {
    // If the previous test's elevation leaked, this would be 200.
    const res = await reveal();
    expect(res.status).toBe(403);
  });
});
