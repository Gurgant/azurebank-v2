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
    if (
      problem?.status === 401 &&
      problem.errorCode !== 'INVALID_PIN' &&
      problem.errorCode !== 'INVALID_CREDENTIALS'
    ) {
      const { status } = (middlewareApi.getState() as { auth: { status: string } }).auth;
      if (status === 'authenticated') {
        middlewareApi.dispatch(sessionExpired());
        middlewareApi.dispatch(apiSlice.util.resetApiState());
      }
    }
  }

  return result;
};
