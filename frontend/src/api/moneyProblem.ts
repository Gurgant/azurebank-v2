import type { ApiProblem } from './problemBaseQuery';
import { CONNECTION_FAILED } from './problemMessages';

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
  'AUTHORIZATION_EXPIRED',
  'AUTHORIZATION_INVALID',
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
export type DomainMessages = Record<string, string | { text: string; scope: MessageScope }> &
  Partial<Record<ProtocolCode, never>>;

/**
 * Whether a failure is about something the user can still EDIT, or about the attempt itself.
 *
 * This is what decides whether a message survives a step change, and it is a property of the
 * failure rather than a decision taken at each call site — so a new error code cannot quietly get
 * the wrong lifetime by being handled in the wrong place.
 *
 * The test a reviewer can apply without interpretation: if the message names a value that is
 * editable on the destination step, it is 'input'. If it names an action or a system condition
 * ("tap Send again", "check your connection", "something went wrong"), it is 'attempt'.
 */
export type MessageScope = 'input' | 'attempt';

/** What the caller should do. Deliberately not "an error string": two outcomes are not errors. */
export type MoneyFailure =
  /** RESULT_UNKNOWN. The hook has latched `verifyRequired`; the flow shows the verify view and sets NO error. */
  | { kind: 'verify' }
  /** The user dismissed the PIN modal. Benign — stay put, say nothing, let them press Send again. */
  | { kind: 'silent' }
  /** The server is still processing the SAME key. Retry is safe and must reuse it. */
  | { kind: 'inFlight' }
  | { kind: 'message'; text: string; scope: MessageScope };

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
      // 'attempt': it names an ACTION, and after this the user may be sent back to a step that has
      // no Send button at all — the sharpest instance of a message outliving its own screen.
      scope: 'attempt',
      text: "Verification didn't complete. Please tap Send and try again.",
    };
  }
  if (problem.errorCode === 'IDEMPOTENCY_IN_FLIGHT') {
    return { kind: 'inFlight' };
  }
  /*
    The two step-up authorisation refusals (ADR-0042). PROTOCOL, not domain: they mean the same
    thing on every authorised operation, and a flow that reworded them would be describing its own
    control instead of the one that refused.

    Both are 'attempt'-scoped. Neither names a value editable on the destination step — "your
    confirmation expired" is about the permission, not the amount — and a step change makes both
    stale, which is exactly what 'attempt' encodes.
  */
  if (problem.errorCode === 'AUTHORIZATION_EXPIRED') {
    return {
      kind: 'message',
      scope: 'attempt',
      // GOV.UK's convention: name the security reason, and never leave the data question implicit.
      // The details ARE still on screen (WCAG 2.2 SC 3.3.7 Redundant Entry is Level A), so say so —
      // a user who is not told assumes the opposite and starts over.
      text: 'For your security, your confirmation expired. Your transfer details are still here — enter your PIN again to confirm.',
    };
  }
  if (problem.errorCode === 'AUTHORIZATION_INVALID') {
    /*
      Deliberately incurious. The server answers this uniformly for unknown / not-yours /
      already-spent / bound-to-different-data, so that it is not an oracle for which authorisation
      references are live. Guessing which one it was here would re-create the oracle in the client's
      copy — and would be wrong as often as not.
    */
    return {
      kind: 'message',
      scope: 'attempt',
      text: 'That confirmation can no longer be used. Check the details and confirm again.',
    };
  }

  // 5: the flow's own business codes. Cannot shadow the above — the type forbids it.
  const domain = opts.messages[problem.errorCode];
  if (domain !== undefined) {
    // The flow's own codes are 'input' by default: every one of them today rejects something
    // chosen on the form step (a recipient, an account, a self-transfer). A flow that needs an
    // attempt-scoped business code can pass the object form.
    return typeof domain === 'string'
      ? { kind: 'message', text: domain, scope: 'input' }
      : { kind: 'message', text: domain.text, scope: domain.scope };
  }

  // 6-10: the shared tail. Note what is NOT here: ACCOUNT_NOT_FOUND. Its copy genuinely differs
  // per flow ("re-check the handle" reads absurd on a page with no handle), so it belongs in each
  // flow's `messages` rather than in a shared branch speaking for a flow it does not know.
  if (problem.errorCode === 'INSUFFICIENT_FUNDS') {
    return { kind: 'message', text: 'Insufficient funds for this transfer.', scope: 'input' };
  }
  if (problem.errorCode === 'VALIDATION_ERROR') {
    const firstFieldError = Object.values(problem.errors ?? {})[0]?.[0];
    return {
      kind: 'message',
      scope: 'input',
      text: firstFieldError ?? 'Please check the details and try again.',
    };
  }
  if (
    problem.errorCode === 'IDEMPOTENCY_KEY_REUSE' ||
    problem.errorCode === 'IDEMPOTENCY_KEY_MISSING' ||
    problem.errorCode === 'IDEMPOTENCY_KEY_INVALID'
  ) {
    return { kind: 'message', text: 'Something went wrong. Please try again.', scope: 'attempt' };
  }
  if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
    return {
      kind: 'message',
      scope: 'attempt',
      text: CONNECTION_FAILED,
    };
  }
  return { kind: 'message', text: problem.detail || opts.fallback, scope: 'attempt' };
}
