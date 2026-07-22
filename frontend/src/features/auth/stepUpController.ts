/**
 * Step-up bridge (DECISIONS §2.2). Connects the non-React base-query wrapper
 * (baseQueryWithStepUp) to the single root <StepUpModal/>: when a request comes back
 * `STEP_UP_REQUIRED`, the wrapper calls `requestStepUp()` and awaits the returned promise;
 * the modal captures the PIN, elevates the session via /bff/auth/verify-pin, and calls
 * `settleStepUp()` to resolve it. The wrapper then replays the ORIGINAL request once.
 *
 * `inflight` IS the single-flight mutex — N concurrent 403s share ONE modal and ONE
 * promise (`if (inflight) return inflight`), so we never stack dialogs. No external
 * dependency: this hand-rolled promise is exactly "one shared awaitable". The controller
 * holds NO PIN and NO store reference — verify-pin runs inside the modal via RTK Query.
 */

export type StepUpOutcome = 'elevated' | 'cancelled';

export interface StepUpRequest {
  /** The level the gated endpoint demands (from ApiProblem.requiredAuthLevel; normally 2). */
  requiredAuthLevel: number;
}

type Listener = () => void;

let current: StepUpRequest | null = null; // snapshot for useSyncExternalStore — ref-stable
let inflight: Promise<StepUpOutcome> | null = null; // the mutex
let resolveInflight: ((outcome: StepUpOutcome) => void) | null = null;
const listeners = new Set<Listener>();

function emit(): void {
  for (const listener of listeners) listener();
}

/**
 * Called by the base-query wrapper (non-React). Returns a promise that resolves when the
 * user finishes step-up. Concurrent callers share the same in-flight promise + modal.
 */
export function requestStepUp(request: StepUpRequest): Promise<StepUpOutcome> {
  if (inflight) return inflight; // mutex: reuse the already-open modal
  inflight = new Promise<StepUpOutcome>((resolve) => {
    resolveInflight = resolve;
  });
  current = request;
  emit(); // -> the modal mounts
  void inflight.finally(() => {
    inflight = null;
    resolveInflight = null;
    current = null;
    emit(); // -> the modal unmounts
  });
  return inflight;
}

/** Called by the modal: verified -> 'elevated'; cancel / dead-session 401 -> 'cancelled'. */
export function settleStepUp(outcome: StepUpOutcome): void {
  resolveInflight?.(outcome);
}

/** useSyncExternalStore subscribe. */
export function subscribeStepUp(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** useSyncExternalStore snapshot — returns the module value, ref-stable when unchanged. */
export function getStepUpSnapshot(): StepUpRequest | null {
  return current;
}

/** Test-only: clear module state between vitest cases (mirrors resetMockState's authLevel). */
export function __resetStepUpController(): void {
  // Settle any live resolver first, so a test that left a request pending unblocks
  // deterministically instead of orphaning a promise into the next case.
  resolveInflight?.('cancelled');
  inflight = null;
  resolveInflight = null;
  current = null;
}
