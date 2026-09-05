import { isFulfilled, isRejectedWithValue, type Middleware } from '@reduxjs/toolkit';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { apiSlice } from '../api/apiSlice';
import { sessionExpired } from './authSlice';
import { learnSessionPolicy, markServerActivity } from './sessionActivity';

/** RTK Query tags actions with the endpoint that produced them; that is the stable wire shape. */
function isGetMeAction(action: unknown): boolean {
  return (
    (action as { meta?: { arg?: { endpointName?: string } } }).meta?.arg?.endpointName === 'getMe'
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
 *    pages mint before they send — so no click met it; but the data layer against the real
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
 *    (errorPath.integration.test.ts pins it on the real stack; auth.test.tsx holds the table);
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
 *  - Every other rejection DOES count. A 400, a 409, a 429 all reached the server and slid
 *    its clock, so the mirror must slide with it.
 */
const NON_ACTIVITY_ENDPOINTS = new Set(['getSessionStatus']);

/** Transport-level failures: the request never reached the BFF, so its clock did not move. */
const TRANSPORT_FAILURES = new Set(['NETWORK', 'PARSE']);

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

  const status = (action.payload as Partial<ApiProblem> | undefined)?.status;
  return !(typeof status === 'string' && TRANSPORT_FAILURES.has(status));
}

export const sessionMiddleware: Middleware = (middlewareApi) => (next) => (action) => {
  const result = next(action);

  if (countsAsActivity(action)) {
    markServerActivity();
  }

  // The session policy is declared by the server, not guessed here. /bff/auth/me is the only
  // response carrying it, and it arrives at bootstrap and on every keep-alive.
  if (isFulfilled(action) && isGetMeAction(action)) {
    const session = (action.payload as { session?: Parameters<typeof learnSessionPolicy>[0] })
      ?.session;
    if (session) learnSessionPolicy(session);
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
