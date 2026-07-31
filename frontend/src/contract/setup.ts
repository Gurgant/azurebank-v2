import { afterAll, beforeAll } from 'vitest';
import { server } from '../mocks/server';
import { resetMockState } from '../mocks/state';
import { BASE_URL, CONTRACT_TARGET } from './target';
import { resetJar } from './client';

/**
 * Setup for the contract suite. The ONLY difference between the two runs lives here.
 *
 * mock: MSW listens, so the wildcard-origin handlers answer requests aimed at the BFF's address.
 * real: MSW never starts, so those same requests leave the process.
 */

if (CONTRACT_TARGET === 'mock') {
  beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
  /*
    Reset per FILE, not per test, so the two targets keep the same session lifetime.

    The unit suite resets mock state after every test and should keep doing so. Here it would break
    the symmetry that makes these assertions meaningful: the real target signs in once per file
    because the BFF rate-limits auth to 10 requests per 60s, so wiping the mock's session between
    tests would leave the mock authenticating eleven times while the real stack authenticates four
    — two different tests wearing one name.
  */
  afterAll(() => {
    server.resetHandlers();
    resetMockState();
    resetJar();
    server.close();
  });
} else {
  /*
    Fail LOUDLY when the stack is not up, and never skip.

    A skipped suite reports success, which is precisely the failure mode this directory exists to
    kill: `real` passing without having asked the real backend anything would be the purest form of
    green-and-false. So the preflight throws with instructions instead.
  */
  beforeAll(async () => {
    try {
      const probe = await fetch(`${BASE_URL}/bff/auth/me`, { method: 'GET' });
      // Any HTTP answer proves something is listening; 401 is the expected one when anonymous.
      if (probe.status === 0) throw new Error('no response');
    } catch (cause) {
      throw new Error(
        `CONTRACT_TARGET=real needs the stack running at ${BASE_URL}, and nothing answered.\n` +
          `Start it with the launch configs 'azurebank-api' (:7215) and 'azurebank-bff' (:5000),\n` +
          `and make sure the dev database is migrated and seeded.\n` +
          `Refusing to skip: a skipped contract run reports success without asking the backend anything.\n` +
          `Cause: ${String(cause)}`,
      );
    }
  });

  /*
    Per FILE, matching the mock branch and matching where `login()` is called.

    This was an afterEACH, which quietly broke the moment the login moved from `beforeEach` to
    `beforeAll` for the rate limiter: the jar was cleared after the first test, so every later test
    in the file went out anonymous and got a 401 that looked exactly like contract drift. Worth the
    comment — the symptom pointed at the backend and the cause was here.
  */
  afterAll(() => resetJar());
}
