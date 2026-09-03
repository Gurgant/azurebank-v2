import { beforeAll, describe, expect, it } from 'vitest';
import {
  asProblem,
  call,
  firstAccountId,
  idempotencyKey,
  login,
  logout,
  restoreJar,
  snapshotJar,
  type Wire,
} from './client';

/**
 * Auth-shaped contract points.
 *
 * Every expected value here was MEASURED against the running stack (API :7215 + BFF :5000, seeded
 * dev database) and pasted in with its date — first pass 2026-07-31 — never inferred from the mock
 * or from the ADR. The observed responses are quoted next to each assertion so a future reader can
 * tell a deliberate contract from a guess that happened to pass.
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
    /*
      The COMPLETE key set, not a sample. The claim under test is "forwarding keeps the API's whole
      envelope", and asserting only errorCode/detail/instance would still pass against a forwarder
      that dropped `type` or `traceId` — which is precisely the difference from the hand-built
      shape three tests up, where those members are absent by design.
    */
    expect(Object.keys(problem).sort()).toEqual([
      'detail',
      'errorCode',
      'instance',
      'status',
      'title',
      'traceId',
      'type',
    ]);
    expect(problem.errorCode).toBe('INVALID_CREDENTIALS');
    expect(problem.detail).toBe('Invalid email or password.');
    expect(problem.instance).toBe('/api/auth/login');
    expect(problem.type).toBe('https://httpstatuses.com/401');
    // The API's handlers write a BARE 32-hex trace id, not a W3C traceparent — the framework path
    // writes the other one, and which you get identifies which handler answered.
    expect(problem.traceId).toMatch(/^[0-9a-f]{32}$/);
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
    /*
      A single request followed by `not.toBe(before)` would be RACY, and only against the mock:
      there `sessionLastActivity` is `Date.now()` at millisecond resolution, so a request that
      lands in the same millisecond as the previous one produces a byte-identical snapshot and the
      assertion fails for a reason that has nothing to do with the contract. (The real BFF stores
      `DateTime.UtcNow` at 100ns and every step is an HTTP round trip, so it cannot collide.)

      Fake timers are not an option here — this same file runs against a real server, whose clock
      vitest cannot move. So: repeat the marking request until the clock is observed to move, and
      fail loudly if it never does. Bounded, deterministic in outcome, and identical on both
      targets. Ten attempts is far past the millisecond a collision could hide in.
    */
    const before = await clock();
    let moved = false;
    for (let attempt = 0; attempt < 10 && !moved; attempt++) {
      expect((await call('/bff/auth/me')).status).toBe(200);
      moved = (await clock()) !== before;
    }

    expect(
      moved,
      'inactivityExpiresAt never moved across ten authenticated requests — the clock is not sliding',
    ).toBe(true);
  });

  it('does NOT slide for a caller with no cookie', async () => {
    /*
      THE point of keying on the cookie rather than on session state. The middleware acts only when
      `Cookies.TryGetValue` succeeds, so a request that presents no cookie leaves the clock of any
      OTHER live session alone. Nothing in the mock could express this before: it decided "is there
      a session?" from its own state, so an anonymous caller extended whatever session happened to
      exist.
    */
    const before = await clock();
    await call('/bff/auth/me', { anonymous: true });
    expect(await clock()).toBe(before);
  });

  it('slides even for a request that is REJECTED before any action runs', async () => {
    /*
      `SessionActivityMiddleware` is middleware, so it runs before routing, before model binding and
      before the action — which means a cookie-bearing request slides the clock even when the reply
      is a 400 the controller never produced. The mock marked activity AFTER parsing the body, so an
      active session that fumbled its payload kept sliding in production and quietly aged out under
      MSW.

      A malformed PIN is the cheapest rejection that is definitely pre-action: `[Pin]` is a
      DataAnnotation, so model state short-circuits and no PIN is ever compared — which is also why
      this does NOT cost an attempt against the three-strike lockout.
    */
    const before = await clock();
    let moved = false;
    for (let attempt = 0; attempt < 10 && !moved; attempt++) {
      const rejected = await call('/bff/auth/verify-pin', {
        method: 'POST',
        body: JSON.stringify({ pin: 'not-six-digits' }),
      });
      expect(rejected.status).toBe(400);
      moved = (await clock()) !== before;
    }

    expect(moved, 'a rejected-but-authenticated request did not slide the clock').toBe(true);
  });
});

/**
 * LAST in the file on purpose. This case kills the session and does not sign in again: the seven
 * contract files already spend eleven requests against the local auth budget of ten per minute
 * (CI raises RateLimiting:AuthPermitLimit to 1000; a local whole-suite run needs the same), so it
 * must not add one, and vitest runs a file's cases in declaration order, so nothing after it needs
 * the cookie.
 */
describe('contract: a cookie the BFF has forgotten', () => {
  it('answers 401 AUTH_TOKEN_MISSING on the step-up route too, not 403', async () => {
    /*
      The mock said otherwise for three and a half weeks. Its `sessionActivity` quoted a 2026-08-05
      measurement in which a cookie that no longer resolved met 403 STEP_UP_REQUIRED with
      currentLevel 0 on the level-2 routes, so under MSW a session that died mid-transfer opened the
      PIN modal, while in production the same moment is the global sign-out. ADR-0038 (01c0e31,
      2026-08-10) had already put the no-session 401 before the level-2 check for a missing, unknown
      and expired cookie alike; ADR-0041 (2026-08-13) then emptied the transfer gate and d74603c
      (2026-08-20) widened the session check to every /api path.

      Measured 2026-09-03 with the cookie a registration issued, replayed after logout answered 200:
        GET  /api/accounts/{id}/full-number -> 401 {"type":"https://httpstatuses.com/401",
             "title":"Unauthorized","status":401,"detail":"Authentication is required to access
             this resource.","instance":"/api/accounts/…/full-number",
             "errorCode":"AUTH_TOKEN_MISSING","traceId":"…"}  and NO X-Auth-Level-* header
        POST /api/transfers `{}`            -> 401, the same body, instance "/api/transfers"
      A cookie that was never issued, and no cookie at all, answered identically. The BFF writes
      this 401 before the proxy — the middleware returns without calling next, and
      AuthLevelMiddlewareTests pins the forwarded paths empty for the reveal with a revoked and
      with a never-issued cookie and for POST /api/transfers with a never-issued one.

      A REAL dead cookie rather than a forged one, so a BFF that began to treat the two differently
      — a signed-out id it still remembers, say — would be noticed here and not only in the mock.
    */
    const id = await firstAccountId();
    const dead = snapshotJar();
    expect((await logout()).status).toBe(200);
    restoreJar(dead);

    const reveal = await call(`/api/accounts/${id}/full-number`);
    expect(reveal.status).toBe(401);
    expect(asProblem(reveal.body).errorCode).toBe('AUTH_TOKEN_MISSING');
    // The client reads step-up from these headers before any body: their absence is the half of
    // the answer that keeps the PIN modal shut.
    expect(reveal.headers.get('x-auth-level-required')).toBeNull();
    expect(reveal.headers.get('x-auth-level-current')).toBeNull();

    const transfer = await call('/api/transfers', {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey() },
      body: '{}',
    });
    expect(transfer.status).toBe(401);
    expect(asProblem(transfer.body).errorCode).toBe('AUTH_TOKEN_MISSING');
  });
});
