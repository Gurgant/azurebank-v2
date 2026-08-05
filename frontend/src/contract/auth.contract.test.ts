import { beforeAll, describe, expect, it } from 'vitest';
import { call, firstAccountId, login, type Wire } from './client';

/**
 * Auth-shaped contract points.
 *
 * Every expected value here was MEASURED against the running stack (API :7215 + BFF :5000, seeded
 * dev database) on 2026-07-31 and pasted in, never inferred from the mock or from the ADR. The
 * observed responses are quoted next to each assertion so a future reader can tell a deliberate
 * contract from a guess that happened to pass.
 */

/*
  One login for the whole file, and its response is KEPT rather than re-fetched.

  Not tidiness: the real BFF rate-limits auth to 10 requests per 60s per IP, so an extra login just
  to inspect the envelope spends a scarce budget for a response this already has in hand.
*/
let loginResponse: Wire;

beforeAll(async () => {
  loginResponse = await login();
  expect(loginResponse.status).toBe(200);
});

describe('contract: authentication', () => {
  it('answers a successful login with a user envelope', async () => {
    // Observed: 200 {"data":{"user":{"id","email","firstName","lastName","azureTag","hasPin"},
    //                        "expiresAt":"..."},"message":"Login successful"}
    const { status, body } = loginResponse;

    expect(status).toBe(200);
    const data = (body as { data?: Record<string, unknown>; message?: unknown }).data ?? {};
    const user = (data as { user?: Record<string, unknown> }).user ?? {};

    expect(Object.keys(user).sort()).toEqual(
      ['azureTag', 'email', 'firstName', 'hasPin', 'id', 'lastName'].sort(),
    );
    expect(data).toHaveProperty('expiresAt');
  });

  it('gates the account-number reveal behind step-up and announces the level in a HEADER', async () => {
    /*
      Observed on a level-1 session:
        403, X-Auth-Level-Required: 2, X-Auth-Level-Current: 1
        {"type":"STEP_UP_REQUIRED","title":"PIN Verification Required","requiredLevel":2,
         "currentLevel":1,"status":403}

      The body is a BARE shape, not RFC9457 ProblemDetails — `type` carries a code rather than a
      URI. That is exactly why `problemBaseQuery` recognises step-up from the HEADER before any body
      parsing (decision D2): trusting this body as ProblemDetails would mis-normalise it.
    */
    const id = await firstAccountId();

    const { status, headers } = await call(`/api/accounts/${id}/full-number`);

    expect(status).toBe(403);
    expect(headers.get('x-auth-level-required')).toBe('2');
    // Asserted too, because the comment above documents it: a missing or wrong CURRENT level would
    // otherwise sail through a test that claims to pin the step-up handshake.
    expect(headers.get('x-auth-level-current')).toBe('1');
  });
});

/**
 * The BFF's own responses, as distinct from the API's — the distinction the mock kept blurring.
 *
 * A `/bff/auth/*` route can answer in two completely different envelopes depending on WHO produced
 * the body: the controller hand-builds a three-member ProblemDetails, while an upstream failure is
 * forwarded verbatim and keeps the API's full envelope. Route alone does not tell you which, so
 * both are pinned here.
 */
describe('contract: the BFF answers in its own envelope', () => {
  it('FORWARDS an upstream credential failure with the API envelope intact', async () => {
    /*
      Same route family, different producer. A bad password is the API's 401, copied through by
      `ForwardUpstreamError`, so it keeps `errorCode`, `traceId` and an `instance` naming the API's
      own path — which the browser never requested.

      Observed: {"type":"https://httpstatuses.com/401","title":"Unauthorized","status":401,
                 "detail":"Invalid email or password.","instance":"/api/auth/login",
                 "errorCode":"INVALID_CREDENTIALS","traceId":"…"}

      Note the trailing period, which the mock omitted. Sending a deliberately wrong password for a
      NON-EXISTENT address on purpose: `AuthService` locks an account after five failures, so
      aiming this at a real one would arm a 15-minute lockout inside the suite.
    */
    const { status, body } = await call('/bff/auth/login', {
      method: 'POST',
      anonymous: true,
      body: JSON.stringify({
        email: 'no-such-account-contract-probe@azurebank.dev',
        password: 'DefinitelyWrong1!',
      }),
    });
    const problem = body as Record<string, unknown>;

    expect(status).toBe(401);
    expect(problem.errorCode).toBe('INVALID_CREDENTIALS');
    expect(problem.detail).toBe('Invalid email or password.');
    expect(problem.instance).toBe('/api/auth/login');
  });
});

describe('contract: the inactivity clock', () => {
  /** `inactivityExpiresAt` is the only value that reports LastActivity, so it is the instrument. */
  const clock = async (): Promise<string> => {
    const { body } = await call('/bff/auth/session-status');
    return (body as { inactivityExpiresAt: string }).inactivityExpiresAt;
  };

  it('does NOT slide when the status probe itself is read', async () => {
    /*
      The single exclusion in `SessionActivityMiddleware`, and the reason the expiry warning can
      exist at all: a client that polls for a countdown must not reset the thing it is counting.
      Reading it twice in a row therefore has to return the same instant.
    */
    expect(await clock()).toBe(await clock());
  });

  it('DOES slide on an ordinary authenticated request', async () => {
    /*
      Measured 2026-08-05 — everything else slides it, including a 404 on a route that does not
      exist and `/health`. The mock slid only on the proxied api routes and `/bff/auth/me`, so a
      client could keep a session alive in production with traffic that let it expire under MSW.

      The glob for those routes is deliberately not written out here: an asterisk followed by a
      slash ends a block comment, and pasting it in is how this test failed on its first run —
      the same trap `handlers.ts` documents against its own catch-all.

      Asserted as "moved", not by how much: the amount is wall-clock and would make this a timing
      test. That it moved at all is the contract.
    */
    const before = await clock();
    expect((await call('/bff/auth/me')).status).toBe(200);
    expect(await clock()).not.toBe(before);
  });
});
