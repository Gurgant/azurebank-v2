import { describe, expect, it } from 'vitest';
import { apiSlice } from '../features/api/apiSlice';
import type { ApiProblem } from '../api/problemBaseQuery';
import { currentCookieHeader } from './setup';
import { makeStore, run } from './harness';

/**
 * The signed-out path, in a file that never signs in.
 *
 * Its own file for a measured reason rather than tidiness: the cookie jar is module state shared by
 * every test in a file, so a "fresh store" inside a signed-in file still carries the session and the
 * call succeeds. The first draft of this test lived in errorPath and failed exactly that way —
 * `expected true to be false`, because the request was never anonymous at all. Vitest isolates the
 * module registry per file, so signed-out is a property of the FILE and cannot be undone by a
 * neighbouring test.
 */

const problem = (error: unknown) => error as ApiProblem;

describe('integration: the app’s data layer with no session', () => {
  it('starts with an empty cookie jar', () => {
    // The negative control. Without it, every assertion below would also pass while signed IN,
    // and the file would prove nothing about the anonymous path.
    expect(currentCookieHeader()).toBe('');
  });

  it('surfaces the API’s own AUTH_TOKEN_MISSING rather than a synthesised code', async () => {
    /*
      The synthesis rule must never overwrite a code the wire actually carried. Measured:

        401 {"type":"https://httpstatuses.com/401","title":"Unauthorized",
             "detail":"Authentication is required to access this resource.",
             "instance":"/api/accounts","errorCode":"AUTH_TOKEN_MISSING","traceId":"<32-hex>"}

      Note this is the API's 401, forwarded by the proxy — NOT the BFF's own
      ("Session expired or invalid", no errorCode), which answers on /bff/auth/*.
    */
    const result = await run(makeStore().dispatch(apiSlice.endpoints.getAccounts.initiate()));

    expect(result.ok).toBe(false);
    if (result.ok) return;
    const apiProblem = problem(result.error);
    expect(apiProblem.status).toBe(401);
    expect(apiProblem.errorCode).toBe('AUTH_TOKEN_MISSING');
    expect(apiProblem.detail).toBe('Authentication is required to access this resource.');
  });

  it('gets the BFF’s own 401 on a /bff/auth route, with no errorCode to carry', async () => {
    // The other 401 in the system, and the reason the two are worth asserting side by side: the
    // BFF answers for itself here, so there is no errorCode and problemBaseQuery synthesises one.
    const result = await run(
      makeStore().dispatch(apiSlice.endpoints.verifyPin.initiate({ pin: '123456' })),
    );

    expect(result.ok).toBe(false);
    if (result.ok) return;
    const apiProblem = problem(result.error);
    expect(apiProblem.status).toBe(401);
    expect(apiProblem.errorCode).toBe('HTTP_401');
    expect(apiProblem.detail).toBe('Session expired or invalid');
  });
});
