import type { BaseQueryFn, FetchArgs, FetchBaseQueryMeta } from '@reduxjs/toolkit/query';
import { problemBaseQuery } from './problemBaseQuery';
import type { ApiProblem } from './problemBaseQuery';
import { requestStepUp } from '../features/auth/stepUpController';

/**
 * The step-up interceptor (DECISIONS §2.2, X1/X2). Wraps problemBaseQuery — which has
 * ALREADY normalized a level-2 403 to `STEP_UP_REQUIRED` (reading X-Auth-Level-Required
 * before body parsing, D2) and never auto-retries mutations. On that code we drive one
 * shared modal via requestStepUp(); on elevation we replay the request EXACTLY ONCE with
 * the IDENTICAL `args` reference — same body bytes, same Idempotency-Key header — so an
 * idempotent transfer is never double-sent (D21). The happy path is untouched: a non-403
 * result returns immediately.
 */
export const baseQueryWithStepUp: BaseQueryFn<
  string | FetchArgs,
  unknown,
  ApiProblem,
  object,
  FetchBaseQueryMeta
> = async (args, api, extraOptions) => {
  const result = await problemBaseQuery(args, api, extraOptions);

  const problem = result.error as ApiProblem | undefined;
  if (problem?.errorCode !== 'STEP_UP_REQUIRED') {
    return result;
  }

  const outcome = await requestStepUp({ requiredAuthLevel: problem.requiredAuthLevel ?? 2 });

  if (outcome !== 'elevated') {
    // The user cancelled (or the session died). Surface a distinct code the caller can treat
    // as a benign no-op rather than a failure. The key is DROPPED (default hook behavior) —
    // correct, since the BFF gate rejected before the API idempotency middleware recorded it.
    return {
      error: {
        status: 403,
        errorCode: 'STEP_UP_CANCELLED',
        detail: 'PIN verification was cancelled.',
        requiredAuthLevel: problem.requiredAuthLevel,
      } satisfies ApiProblem,
      meta: result.meta,
    };
  }

  // Elevated → replay ONCE with the SAME args (do NOT clone/rebuild — that would re-serialize
  // and mint a new key, tripping 422 KEY_REUSE). We deliberately do not re-intercept: a second
  // 403 here means elevation didn't stick, and it flows up as STEP_UP_REQUIRED rather than
  // looping.
  return problemBaseQuery(args, api, extraOptions);
};
