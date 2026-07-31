import { beforeAll, describe, expect, it } from 'vitest';
import { call, firstAccountId, login } from './client';

/**
 * Auth-shaped contract points.
 *
 * Every expected value here was MEASURED against the running stack (API :7215 + BFF :5000, seeded
 * dev database) on 2026-07-31 and pasted in, never inferred from the mock or from the ADR. The
 * observed responses are quoted next to each assertion so a future reader can tell a deliberate
 * contract from a guess that happened to pass.
 */

beforeAll(async () => {
  // Once per file, not per test — the real BFF rate-limits auth to 10 requests per 60s per IP, and
  // a `beforeEach` login tripped it. See the note on `login()`.
  const { status } = await login();
  expect(status).toBe(200);
});

describe('contract: authentication', () => {
  it('answers a successful login with a user envelope', async () => {
    // Observed: 200 {"data":{"user":{"id","email","firstName","lastName","azureTag","hasPin"},
    //                        "expiresAt":"..."},"message":"Login successful"}
    const { status, body } = await login();

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
  });
});
