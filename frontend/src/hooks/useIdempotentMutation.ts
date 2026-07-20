import { useCallback, useRef, useState } from 'react';
import type { ApiProblem } from '../api/problemBaseQuery';
import type { IdempotentArg } from '../features/api/apiSlice';

/** Structural shape of an RTK Query mutation trigger taking an IdempotentArg. */
type IdempotentTrigger<TBody, TResult> = (arg: IdempotentArg<TBody>) => {
  unwrap(): Promise<TResult>;
};

/**
 * KEEP outcomes (DECISIONS §2.3): the server may have recorded — or may still record —
 * this key, so re-sending with a FRESH key is a client-manufactured double-spend. The
 * user-driven Retry must reuse the same key + same body bytes. Everything else DROPS.
 */
function shouldKeepKey(problem: ApiProblem): boolean {
  if (problem.errorCode === 'IDEMPOTENCY_IN_FLIGHT') return true;
  if (problem.status === 'NETWORK' || problem.status === 'PARSE') return true;
  return typeof problem.status === 'number' && problem.status >= 500;
}

/**
 * Client half of the idempotency protocol, one instance per money-intent. The key is
 * lazy (`crypto.randomUUID()` on first submit), in-memory only, and re-keyed on
 * `errorCode` — never on HTTP status. Any body-affecting form edit must call
 * `resetIntent` (an edited body with the old key is a byte-fingerprint mismatch → 422).
 *
 * `IDEMPOTENCY_RESULT_UNKNOWN` drops the key and latches `verifyRequired`: submit
 * refuses to mint a new key until the owning flow's explicit "it didn't go through —
 * try again" action calls `resetIntent` (after the verify-transactions dialog, §2.3).
 */
export function useIdempotentMutation<TBody, TResult>(trigger: IdempotentTrigger<TBody, TResult>) {
  const keyRef = useRef<string | null>(null);
  const verifyRequiredRef = useRef(false);
  const [verifyRequired, setVerifyRequired] = useState(false);

  const resetIntent = useCallback(() => {
    keyRef.current = null;
    verifyRequiredRef.current = false;
    setVerifyRequired(false);
  }, []);

  const submit = useCallback(
    async (body: TBody): Promise<TResult> => {
      if (verifyRequiredRef.current) {
        throw new Error('Previous result unknown — verify before submitting again.');
      }
      keyRef.current ??= crypto.randomUUID();
      try {
        const result = await trigger({ idempotencyKey: keyRef.current, body }).unwrap();
        keyRef.current = null;
        return result;
      } catch (error) {
        const problem = error as ApiProblem;
        if (problem.errorCode === 'IDEMPOTENCY_RESULT_UNKNOWN') {
          keyRef.current = null;
          verifyRequiredRef.current = true;
          setVerifyRequired(true);
        } else if (!shouldKeepKey(problem)) {
          keyRef.current = null;
        }
        throw error;
      }
    },
    [trigger],
  );

  return { submit, resetIntent, verifyRequired };
}
