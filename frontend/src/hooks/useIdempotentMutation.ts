import { useCallback, useRef, useState } from 'react';
import type { ApiProblem } from '../api/problemBaseQuery';
import type { IdempotentArg } from '../features/api/apiSlice';

/**
 * Structural shape of an RTK Query mutation trigger taking an IdempotentArg.
 *
 * `TBody` must stay inside the PARAMETER type, which is its only inference site. Parameterising
 * this over the whole argument instead — an attempt to carry "the transfers require an
 * authorisation" out to the pages — left `TBody` mentioned only in a constraint, so it silently
 * inferred as `unknown` and every money body stopped being type-checked. Measured: a transfer body
 * still carrying the deleted `pin` compiled clean. The requirement it was meant to buy is enforced
 * where it can actually be checked — `required: true` in the published contract, and a mock that
 * refuses a headerless transfer exactly as the API does.
 */
export type IdempotentTrigger<TBody, TResult> = (arg: IdempotentArg<TBody>) => {
  unwrap(): Promise<TResult>;
};

/**
 * KEEP outcomes (ADR-0022): the server may have recorded — or may still record —
 * this key, so re-sending with a FRESH key is a client-manufactured double-spend. The
 * user-driven Retry must reuse the same key + same body bytes. Everything else DROPS.
 */
function shouldKeepKey(problem: ApiProblem): boolean {
  if (problem.errorCode === 'IDEMPOTENCY_IN_FLIGHT') return true;
  /*
    An authorisation refusal KEEPS the key, and the server is why (ADR-0042).

    `TransferService` throws it from inside the action, so `IdempotencyMiddleware` takes its catch
    and calls `ReleaseQuietlyAsync` -> `ReleaseIfNotExecutedAsync`, which removes the row only while
    database truth is still `Processing`. Nothing committed; the key is free. And the fingerprint is
    over the body alone, which a re-authorisation does not touch — the reference travels in a header
    precisely so it can change while the bytes do not.

    MEASURED on the running API, and this is the whole foundation of the retry:
      key K + EXPIRED authorisation -> 401 AUTHORIZATION_EXPIRED
      key K + FRESH  authorisation -> 201            (same key, same body, no replay header)

    Keeping it is also the SAFER half of the trade. If the release ever failed, a retained key
    answers 409 IN_FLIGHT or RESULT_UNKNOWN; a fresh key would execute a second payment. A wrong
    PIN is deliberately not in this set — it never reached the service, so nothing was claimed.
  */
  if (problem.errorCode === 'AUTHORIZATION_EXPIRED') return true;
  if (problem.errorCode === 'AUTHORIZATION_INVALID') return true;
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
 * try again" action calls `resetIntent` (after the verify-transactions dialog, ADR-0022).
 */
export function useIdempotentMutation<TBody, TResult>(trigger: IdempotentTrigger<TBody, TResult>) {
  const keyRef = useRef<string | null>(null);
  const verifyRequiredRef = useRef(false);
  const [verifyRequired, setVerifyRequired] = useState(false);
  // Reactive mirror of "a key is currently held" (keyRef.current !== null). The owning flow
  // reads this to block dismissal while ANY key is live — not just while awaiting: the key is
  // KEPT after an IN_FLIGHT / network / parse / 5xx failure too, and abandoning it then
  // reopening would mint a fresh key = a new intent = a double-spend.
  const [keyRetained, setKeyRetained] = useState(false);

  const resetIntent = useCallback(() => {
    keyRef.current = null;
    verifyRequiredRef.current = false;
    setVerifyRequired(false);
    setKeyRetained(false);
  }, []);

  const submit = useCallback(
    async (
      body: TBody,
      /*
        Out-of-band request metadata: travels WITH the attempt, not inside the intent. The step-up
        authorisation (ADR-0042) belongs here and not in `body` because the body is the thing that
        must stay byte-identical across a retry — the server fingerprints it — while the
        authorisation is exactly what may legitimately differ between attempts.
      */
      extras?: { stepUpAuthorizationId?: string },
    ): Promise<TResult> => {
      if (verifyRequiredRef.current) {
        throw new Error('Previous result unknown — verify before submitting again.');
      }
      keyRef.current ??= crypto.randomUUID();
      setKeyRetained(true);
      try {
        const result = await trigger({
          idempotencyKey: keyRef.current,
          body,
          ...extras,
        }).unwrap();
        keyRef.current = null;
        setKeyRetained(false);
        return result;
      } catch (error) {
        const problem = error as ApiProblem;
        if (problem.errorCode === 'IDEMPOTENCY_RESULT_UNKNOWN') {
          keyRef.current = null;
          verifyRequiredRef.current = true;
          setVerifyRequired(true);
          setKeyRetained(false);
        } else if (!shouldKeepKey(problem)) {
          keyRef.current = null;
          setKeyRetained(false);
        }
        // shouldKeepKey path: the key survives, so keyRetained stays true.
        throw error;
      }
    },
    [trigger],
  );

  /*
    The owning flow wants to change intent while a key is RETAINED.

    That state means the last attempt's outcome is unknown or its authorisation was refused, and a
    new intent there is the client-manufactured double-spend this whole protocol exists to prevent.
    Releasing the key incidentally -- which is what a body edit used to do -- is the dangerous half:
    it is silent, and the user is never told that the previous attempt may still land.

    So the answer is the one the protocol already has for the identical epistemic state.
    `IDEMPOTENCY_RESULT_UNKNOWN` latches verify-first; a network failure, an IN_FLIGHT and an
    authorisation refusal leave the caller knowing exactly as much, and differ only in who noticed.
    This latches the same thing: the key is dropped (so dismissal is no longer blocked) and `submit`
    refuses until the flow's explicit "it didn't go through -- try again" calls `resetIntent`.

    Raised in review on #199, where a test had encoded the incidental release as the escape hatch:
    "editing the body rotates (releases) the key". It is an escape, and it was also the way out
    through the money.
  */
  const requireVerify = useCallback(() => {
    keyRef.current = null;
    verifyRequiredRef.current = true;
    setVerifyRequired(true);
    setKeyRetained(false);
  }, []);

  return { submit, resetIntent, verifyRequired, keyRetained, requireVerify };
}
