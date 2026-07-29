import type { ApiProblem } from './problemBaseQuery';

/**
 * What a failed money request MEANS, decided once instead of in every flow's catch block.
 *
 * `TransferPage` and `InternalTransferPage` each carried a ~30-line `if/else if` chain over
 * `problem.errorCode`. Measured, the two differed in exactly two branches — and one of those,
 * `SAME_ACCOUNT_TRANSFER`, turned out to be unreachable over HTTP (FluentValidation answers a 400
 * with an errors dictionary before the service's 422 can be thrown; verified against the running
 * API in U6.1). So the pages were maintaining two copies of one protocol, and the copies had
 * already drifted.
 *
 * This module is deliberately PURE: it takes a problem and returns what happened. It never touches
 * React state. The caller decides what to do about it — which is what stops a shared piece from
 * quietly owning a protocol outcome it cannot see the consequences of.
 */

/**
 * Codes the IDEMPOTENCY PROTOCOL owns. Their handling is identical for every money flow and is not
 * a flow's business to override — see {@link DomainMessages}.
 */
export const PROTOCOL_CODES = [
  'IDEMPOTENCY_RESULT_UNKNOWN',
  'STEP_UP_CANCELLED',
  'STEP_UP_REQUIRED',
  'IDEMPOTENCY_IN_FLIGHT',
  'VALIDATION_ERROR',
  'IDEMPOTENCY_KEY_REUSE',
  'IDEMPOTENCY_KEY_MISSING',
  'IDEMPOTENCY_KEY_INVALID',
] as const;

export type ProtocolCode = (typeof PROTOCOL_CODES)[number];

/**
 * A flow's own business-code → copy table.
 *
 * The `Partial<Record<ProtocolCode, never>>` half is the point: it makes mapping a PROTOCOL code
 * here a COMPILE error rather than a silent override. Without it, a flow could add
 * `IDEMPOTENCY_IN_FLIGHT: 'Something went wrong'` and turn the in-flight branch — which must set
 * the retry-safe banner, not an error — into a dead end that invites a fresh key. `never` is
 * unsatisfiable by any string, so the only way to write such a key is to delete it.
 */
export type DomainMessages = Record<string, string> & Partial<Record<ProtocolCode, never>>;

/** What the caller should do. Deliberately not "an error string": two outcomes are not errors. */
export type MoneyFailure =
  /** RESULT_UNKNOWN. The hook has latched `verifyRequired`; the flow shows the verify view and sets NO error. */
  | { kind: 'verify' }
  /** The user dismissed the PIN modal. Benign — stay put, say nothing, let them press Send again. */
  | { kind: 'silent' }
  /** The server is still processing the SAME key. Retry is safe and must reuse it. */
  | { kind: 'inFlight' }
  | { kind: 'message'; text: string };

/**
 * Classify a failed money request.
 *
 * Branch ORDER is load-bearing and is preserved verbatim from the two pages it replaces. In
 * particular the flow's own `messages` table is consulted at step 5 — AFTER every protocol branch —
 * so a business code can never pre-empt the protocol.
 */
export function classifyMoneyProblem(
  problem: ApiProblem,
  opts: { messages: DomainMessages; fallback: string },
): MoneyFailure {
  // 1-4: the protocol, in the order both pages had it.
  if (problem.errorCode === 'IDEMPOTENCY_RESULT_UNKNOWN') {
    return { kind: 'verify' };
  }
  if (problem.errorCode === 'STEP_UP_CANCELLED') {
    return { kind: 'silent' };
  }
  if (problem.errorCode === 'STEP_UP_REQUIRED') {
    // The replay 403'd again, so elevation did not stick. Never leak the raw gate string.
    return {
      kind: 'message',
      text: "Verification didn't complete. Please tap Send and try again.",
    };
  }
  if (problem.errorCode === 'IDEMPOTENCY_IN_FLIGHT') {
    return { kind: 'inFlight' };
  }

  // 5: the flow's own business codes. Cannot shadow the above — the type forbids it.
  const domain = opts.messages[problem.errorCode];
  if (domain !== undefined) {
    return { kind: 'message', text: domain };
  }

  // 6-10: the shared tail. Note what is NOT here: ACCOUNT_NOT_FOUND. Its copy genuinely differs
  // per flow ("re-check the handle" reads absurd on a page with no handle), so it belongs in each
  // flow's `messages` rather than in a shared branch speaking for a flow it does not know.
  if (problem.errorCode === 'INSUFFICIENT_FUNDS') {
    return { kind: 'message', text: 'Insufficient funds for this transfer.' };
  }
  if (problem.errorCode === 'VALIDATION_ERROR') {
    const firstFieldError = Object.values(problem.errors ?? {})[0]?.[0];
    return { kind: 'message', text: firstFieldError ?? 'Please check the details and try again.' };
  }
  if (
    problem.errorCode === 'IDEMPOTENCY_KEY_REUSE' ||
    problem.errorCode === 'IDEMPOTENCY_KEY_MISSING' ||
    problem.errorCode === 'IDEMPOTENCY_KEY_INVALID'
  ) {
    return { kind: 'message', text: 'Something went wrong. Please try again.' };
  }
  if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
    return {
      kind: 'message',
      text: "Couldn't reach the server — check your connection and try again.",
    };
  }
  return { kind: 'message', text: problem.detail || opts.fallback };
}
