/**
 * Executable contract: level-2 step-up around transfers (AuthLevelMiddleware +
 * BffAuthController semantics). The product's step-up interceptor is built against these.
 */

const TRANSFER_URL = '/api/transfers';
const VERIFY_URL = '/bff/auth/verify-pin';
const KEY = '7c9e6679-7425-40de-944b-e07fc1f90ae7';

function transfer() {
  return fetch(TRANSFER_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Idempotency-Key': KEY },
    body: JSON.stringify({ fromAccountId: 'a', recipientAzureTag: 'friend', amount: 25 }),
  });
}

function verifyPin(pin: string) {
  return fetch(VERIFY_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pin }),
  });
}

describe('step-up handler (level-2 semantics)', () => {
  it('403 at level 1 with the BARE step-up body (NOT ProblemDetails) + level headers', async () => {
    const res = await transfer();

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

  it('correct pin elevates to level 2 and the transfer succeeds', async () => {
    const ok = await verifyPin('123456');
    expect((await ok.json()).data.authLevel).toBe(2);

    const res = await transfer();
    expect(res.status).toBe(201);
    const body = await res.json();
    expect(body.data.transactionNumber).toMatch(/^TXN-/);
    expect(body.data.recipientAzureTag).toBe('friend');
  });

  it('mock state resets between tests: back to level 1', async () => {
    // If the previous test's elevation leaked, this would be 201.
    const res = await transfer();
    expect(res.status).toBe(403);
  });
});
