import { isFulfilled, isRejectedWithValue, type Middleware } from '@reduxjs/toolkit';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { apiSlice } from '../api/apiSlice';
import { sessionExpired } from './authSlice';
import { markServerActivity } from './sessionActivity';

interface RtkQueryActionMeta {
  arg?: { endpointName?: string };
}

/**
 * The global 401 rule (D3), routed on errorCode — never on endpoint identity:
 *  - INVALID_PIN         stays in the calling form (withdraw dialog / step-up);
 *  - INVALID_CREDENTIALS stays on the login form;
 *  - the BOOT probe's 401 resolves to 'anonymous' in the slice (status not yet
 *    'authenticated' — no banner, no cache wipe);
 *  - every other 401 -> sessionExpired() + full RTK Query cache reset (financial data
 *    must not outlive the session it was fetched under).
 *
 * Every fulfilled/rejected SERVER response also marks activity — the client mirror of
 * the BFF's LastActivity that drives the T-2min expiry warning (D14).
 */
export const sessionMiddleware: Middleware = (middlewareApi) => (next) => (action) => {
  const result = next(action);

  if (isFulfilled(action) || isRejectedWithValue(action)) {
    markServerActivity();
  }

  if (isRejectedWithValue(action)) {
    const problem = action.payload as Partial<ApiProblem> | undefined;
    if (
      problem?.status === 401 &&
      problem.errorCode !== 'INVALID_PIN' &&
      problem.errorCode !== 'INVALID_CREDENTIALS'
    ) {
      const endpointName = (action.meta as RtkQueryActionMeta | undefined)?.arg?.endpointName;
      const { status } = (middlewareApi.getState() as { auth: { status: string } }).auth;

      // The boot probe's 401 is the slice's business (unknown -> anonymous); everything
      // else — including a getMe 401 AFTER an authenticated boot — is a real expiry.
      const isBootProbe = endpointName === 'getMe' && status !== 'authenticated';
      if (!isBootProbe && status !== 'expired') {
        middlewareApi.dispatch(sessionExpired());
        middlewareApi.dispatch(apiSlice.util.resetApiState());
      }
    }
  }

  return result;
};
