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
});
