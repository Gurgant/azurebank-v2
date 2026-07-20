import { isFulfilled, isRejectedWithValue, type Middleware } from '@reduxjs/toolkit';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { apiSlice } from '../api/apiSlice';
import { sessionExpired } from './authSlice';
import { markServerActivity } from './sessionActivity';

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
 * Every fulfilled/rejected RTK QUERY response also marks activity — the client mirror
 * of the BFF's LastActivity that drives the T-2min expiry warning (D14). Only api/
 * actions count: the mirror tracks SERVER responses, nothing else.
 */
export const sessionMiddleware: Middleware = (middlewareApi) => (next) => (action) => {
  const result = next(action);

  const isApiAction =
    typeof (action as { type?: unknown }).type === 'string' &&
    (action as { type: string }).type.startsWith('api/');

  if (isApiAction && (isFulfilled(action) || isRejectedWithValue(action))) {
    markServerActivity();
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
