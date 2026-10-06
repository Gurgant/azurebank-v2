import { isFulfilled, isRejectedWithValue, type Middleware } from '@reduxjs/toolkit';
import type { BffDemoClaimResponse } from '../../api/bffTypes';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { apiSlice } from '../api/apiSlice';
import { writeDemoCopy } from '../demo/demoCopyStorage';
import { isDemoMode } from '../demo/demoMode';
import { sessionExpired } from './authSlice';
import { learnSessionPolicy, markServerActivity } from './sessionActivity';

/** RTK Query tags actions with the endpoint that produced them; that is the stable wire shape. */
function isActionOf(action: unknown, endpointName: string): boolean {
  return (
    (action as { meta?: { arg?: { endpointName?: string } } }).meta?.arg?.endpointName ===
    endpointName
  );
}

/**
 * The global 401 rule (D3), routed on errorCode — never on endpoint identity:
 *  - INVALID_PIN         stays in the calling form (withdraw dialog / step-up);
 *  - INVALID_CREDENTIALS stays on the login form;
 *  - AUTHORIZATION_REQUIRED / AUTHORIZATION_EXPIRED / AUTHORIZATION_INVALID — all three codes of
 *    ADR-0042 — stay in the transfer wizard. These are 401s about a step-up AUTHORISATION, not
 *    about the session, and the pipeline proves it: `IdempotencyMiddleware` is registered AFTER
 *    UseAuthentication/UseAuthorization, so reaching `StepUpAuthorizationService.ValidateAsync`
 *    at all means the cookie was accepted; REQUIRED is thrown in the same [Authorize]d action,
 *    upstream of that validator and before the payee is resolved, so it proves the cookie at
 *    least as well. Treating them as a dead session signed the user out mid-transfer — and the
 *    wizard's exit blocker exempts /login, so they were not even asked whether to abandon a
 *    payment whose outcome they did not know. Measured through the BFF: the `errorCode`
 *    extension survives on all three 401s, so routing on it here is safe.
 *
 *    REQUIRED joined the list on 2026-09-05. The set was written from the codes the server could
 *    emit that day (e4973da, 2026-08-17); the flip that made REQUIRED emittable landed the next
 *    day (6c3b24e) without touching this file. No shipped page can reach it — both transfer
 *    pages mint before they send, and so does the delete dialog — so no click met it; but the
 *    data layer against the real
 *    stack, handed a headerless transfer at the store, went 'authenticated' -> 'expired' with the
 *    cookie alive (observed, red before the fix). The rest follows from this block: the same
 *    dispatch resets the cache, and in the app ProtectedRoute answers 'expired' with /login and
 *    a "session expired" banner — for a session the server had just accepted. Measured
 *    2026-09-05 at level 1 and at level 2 alike:
 *
 *      POST /api/transfers, no Step-Up-Authorization header
 *        -> 401 {"detail":"This transfer has not been authorised.",
 *                "errorCode":"AUTHORIZATION_REQUIRED", ...}, no WWW-Authenticate
 *      GET /bff/auth/me straight after -> 200, authLevel unchanged
 *
 *    and, since the account closure joined the rail (ADR-0049), measured 2026-09-06T19:16Z on
 *    main 19742ff (row D1 of that run):
 *
 *      DELETE /api/accounts/{id}, no Step-Up-Authorization header
 *        -> 401 {"detail":"This account closure has not been authorised.",
 *                "errorCode":"AUTHORIZATION_REQUIRED", ...}, no WWW-Authenticate
 *      GET /bff/auth/me straight after -> 200, authLevel 1
 *
 *    (errorPath.integration.test.ts pins both on the real stack; auth.test.tsx holds the table);
 *  - a 401 while NOT authenticated is the calling surface's business (the boot probe
 *    resolves to 'anonymous' in the slice; an anonymous user must never see a
 *    "session expired" banner for a session they never had, and their form's mutation
 *    state must not be wiped);
 *  - a 401 while AUTHENTICATED -> sessionExpired() + full RTK Query cache reset
 *    (financial data must not outlive the session it was fetched under).
 *
 * The mirror of the BFF's LastActivity that drives the expiry warning (D14) is maintained
 * here too, and it has to mirror the server's rule rather than approximate it:
 *
 *  - `getSessionStatus` does NOT count. The BFF deliberately excludes that one probe from
 *    activity (ADR-0018) — it is the only way to ask "how long is left" without changing
 *    the answer. Counting it here would let this tab believe a session it merely LOOKED at
 *    was still being used, which is the optimistic direction: the warning arrives late and
 *    the sign-out later still.
 *  - A TRANSPORT failure does not count either. `status: 'NETWORK'` and `'PARSE'` mean the
 *    request never reached the BFF or came back unreadable, so the server's clock did not
 *    move. This used to mark activity on every `rejectedWithValue`, so an offline
 *    "Stay signed in" reset the countdown while doing nothing at all.
 *  - Nor does a 503 that neither server wrote (`errorCode` `HTTP_503`: a body that is not JSON,
 *    an empty one, or JSON with no code). The BFF and the API put `SERVICE_UNAVAILABLE` on every
 *    503 they send, so this one came from something in front of the BFF — an ingress whose BFF
 *    is down — and the BFF never saw the request. Counting it would slide the mirror on every
 *    Retry while the server's clock stands still: the late direction. (The non-JSON one was
 *    `'PARSE'`, and so uncounted, until it became a numeric 503 so that it reads and retries as
 *    the outage.)
 *  - Every other rejection DOES count. A 400, a 409, a 429 all reached the server and slid
 *    its clock, so the mirror must slide with it.
 */
const NON_ACTIVITY_ENDPOINTS = new Set(['getSessionStatus']);

/** Transport-level failures: the request never reached the BFF, so its clock did not move. */
const TRANSPORT_FAILURES = new Set(['NETWORK', 'PARSE']);

/** A 503 with no code of its own: not the BFF's answer, so not the BFF's clock. */
const NOT_THE_BFFS_503 = 'HTTP_503';

/**
 * 401s that belong to the surface that asked, not to the session. See the D3 note above.
 *
 * A set rather than a chain of `!==` because the chain had already grown to two and was about to
 * grow to four: the shape invites someone to add a third `&&` and invites nobody to ask what the
 * list means.
 */
const IN_FLOW_401_CODES = new Set([
  'INVALID_PIN',
  'INVALID_CREDENTIALS',
  'AUTHORIZATION_REQUIRED',
  'AUTHORIZATION_EXPIRED',
  'AUTHORIZATION_INVALID',
]);

function countsAsActivity(action: unknown): boolean {
  const type = (action as { type?: unknown }).type;
  if (typeof type !== 'string' || !type.startsWith('api/')) return false;

  const endpointName = (action as { meta?: { arg?: { endpointName?: string } } }).meta?.arg
    ?.endpointName;
  if (endpointName && NON_ACTIVITY_ENDPOINTS.has(endpointName)) return false;

  if (isFulfilled(action)) return true;
  if (!isRejectedWithValue(action)) return false;

  const problem = action.payload as Partial<ApiProblem> | undefined;
  if (typeof problem?.status === 'string' && TRANSPORT_FAILURES.has(problem.status)) return false;
  return problem?.errorCode !== NOT_THE_BFFS_503;
}

export const sessionMiddleware: Middleware = (middlewareApi) => (next) => (action) => {
  const result = next(action);

  if (countsAsActivity(action)) {
    markServerActivity();
  }

  // The session policy is declared by the server, not guessed here. /bff/auth/me is the only
  // response carrying it, and it arrives at bootstrap and on every keep-alive.
  if (isFulfilled(action) && isActionOf(action, 'getMe')) {
    const session = (action.payload as { session?: Parameters<typeof learnSessionPolicy>[0] })
      ?.session;
    if (session) learnSessionPolicy(session);
  }

  /*
    A demo claim that succeeded has opened a session on another copy, for a visitor who may have
    been signed in to one a moment ago. Two things follow, and they follow HERE, so that every
    caller of the claim gets both, in the same dispatch that signed the new owner in. The reducers
    have already run by this line (`next(action)` is the first statement above), so the auth slice
    already names the new owner.

    1. The copy is kept: its sign-in details go into the browser's storage
       (src/features/demo/demoCopyStorage.ts). What is kept is the answer's `copy`, so the end
       kept with it is the COPY's, `copy.expiresAt`, and never the access token's `expiresAt`
       beside `user`.
    2. The whole cache is dropped. The accounts and the history in it were fetched for whoever was
       signed in before, and a sign-in's `Session` tag would not take them out: they would still
       be in the cache when the new owner's pages ask for theirs. The claim's own entry goes with
       them, and that entry holds the answer, the copy's password and PIN included: the reset is
       what takes them out of the store.

    In that order. The reset is a dispatch, so the store's subscribers are told when the cache
    goes. With the copy written first, they are told of a cache that is gone beside the new
    owner's copy, already kept. The other way round they would be told of it beside the copy that
    was just replaced, and would hear of the new one only if they listen to the storage too.

    A claim that was refused does neither: it opened no session, and the session it came with
    is alive. src/features/demo/claim.test.ts holds each of these.
  */
  if (isFulfilled(action) && isActionOf(action, 'claimDemoCopy')) {
    writeDemoCopy((action.payload as BffDemoClaimResponse).copy);
    middlewareApi.dispatch(apiSlice.util.resetApiState());
  }

  /*
    In the demo a sign-in that succeeded drops the cache as well, and keeps nothing: a sign-in's
    answer carries no copy.

    The reason is the claim's second one. In the demo every visitor signs in to a copy of their
    own, and one browser can sign in to a copy while its cache still holds another's: a sign-out
    drops nothing, and a sign-in invalidates the `Session` tag only. The accounts of the copy
    before would still be in the cache when the owner of the next asks for theirs.

    Off the demo a sign-in drops nothing, as it never has. A deployment with the demo off has to
    behave as it did before there was a demo, down to the requests it makes, and what a sign-out
    leaves in the cache there is left as it is. src/features/demo/claim.test.ts holds both: the
    cache gone after a sign-in in the demo, and still there after one off it.
  */
  if (isFulfilled(action) && isActionOf(action, 'login') && isDemoMode()) {
    middlewareApi.dispatch(apiSlice.util.resetApiState());
  }

  if (isRejectedWithValue(action)) {
    const problem = action.payload as Partial<ApiProblem> | undefined;
    if (problem?.status === 401 && !IN_FLOW_401_CODES.has(problem.errorCode ?? '')) {
      const { status } = (middlewareApi.getState() as { auth: { status: string } }).auth;
      if (status === 'authenticated') {
        middlewareApi.dispatch(sessionExpired());
        middlewareApi.dispatch(apiSlice.util.resetApiState());
      }
    }
  }

  return result;
};
