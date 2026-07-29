import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import type { ApiProblem } from '../api/problemBaseQuery';
import { classifyMoneyProblem, type DomainMessages } from '../api/moneyProblem';
import { useIdempotentMutation, type IdempotentTrigger } from './useIdempotentMutation';

/**
 * The form → review → send machine that every money wizard runs, owned once.
 *
 * `TransferPage` and `InternalTransferPage` each carried their own copy: the same six pieces of
 * state, the same four exits, the same `beforeunload` guard, the same idempotency-key discipline,
 * and a catch block that differed in two branches out of ten. An audit measured 83% of the smaller
 * page appearing verbatim inside the larger one. The duplication that mattered was never the
 * markup — it was this: the code where a divergence costs a euro rather than a pixel.
 *
 * **What this hook deliberately does NOT own.**
 *
 * It never sees the form. A tempting design has it own `useForm` so it can drive the
 * balance-bounded resolver, but that is unnecessary — the pages already close that loop with
 * `useState` declared before `useForm` and an effect after — and it costs three extra generics plus
 * a callback that has to reach back into the hook's own return value while it is still in the
 * temporal dead zone.
 *
 * It never sees the RESPONSE either. `run` hands the result straight back, typed, and each flow maps
 * it onto its own receipt. The two response DTOs overlap in about three fields out of nine and six —
 * external returns `newBalance` and a recipient, internal returns `fromAccountNewBalance` and
 * `toAccountNewBalance` — so a shared receipt shape would need a discriminator, and a discriminator
 * is how "which account did we just debit" becomes a runtime question.
 *
 * And it owns no copy beyond the protocol's own. Each flow passes its business codes in.
 *
 * **Why none of these are `useCallback`.**
 *
 * `requestLeave`, `toForm` and `run` all read `keyLive`, and `keyLive` is exactly the value that
 * changes while they are alive. `useCallback((to) => { if (!keyLive) navigate(to) }, [navigate])`
 * captures `keyLive === false` at mount and never un-captures it — permanently disarming the one
 * guard standing between a live idempotency key and a navigation away from it. Recreating them each
 * render is not a missed optimisation here; it is the correctness condition.
 */

export type MoneyWizardStep = 'form' | 'review';

export interface MoneyWizard<TBody, TResult> {
  step: MoneyWizardStep;
  /** True from the moment Send is pressed until the request settles. Disables Send. */
  isSubmitting: boolean;
  /** The server is still processing THIS key. Pressing Send again is safe and reuses it. */
  inFlight: boolean;
  error: string | null;
  /** The result could not be confirmed. The flow must show its verify view and offer no exits. */
  verifyRequired: boolean;
  /**
   * `isSubmitting || keyRetained` — an idempotency key is alive. Every exit must consult this: a key
   * survives IN_FLIGHT, network and 5xx failures too, and abandoning one then starting again mints a
   * fresh key, which is a second intent against a request that may already have committed.
   */
  keyLive: boolean;
  /**
   * Send. Resolves to the result, or to `undefined` if it failed — the flow's own error state has
   * already been set by then. Returning `undefined` rather than throwing is what makes
   * `if (!result) return;` a type obligation at the call site under `strict`.
   */
  run: (body: TBody) => Promise<TResult | undefined>;
  /** Call after ANY edit that changes the request body. No-op while a key is live. */
  onBodyEdit: () => void;
  toReview: () => void;
  /** Back to the form. Guarded: a live key must not reach the fields that would rotate it. */
  toForm: () => void;
  /** The verify view's "it didn't go through" — abandons the intent so the next send is a NEW one. */
  startOver: () => void;
  /** The ONLY way out. Refuses while a key is live. */
  requestLeave: (to: string) => void;
}

export function useMoneyWizard<TBody, TResult>(
  trigger: IdempotentTrigger<TBody, TResult>,
  options: { messages: DomainMessages; fallback: string },
): MoneyWizard<TBody, TResult> {
  const navigate = useNavigate();
  const { submit, resetIntent, verifyRequired, keyRetained } = useIdempotentMutation(trigger);

  const [step, setStep] = useState<MoneyWizardStep>('form');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [inFlight, setInFlight] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const keyLive = isSubmitting || keyRetained;

  // The one nav path the in-app controls cannot cover: a refresh or tab-close while a key is live.
  // (In-app popstate needs a data-router useBlocker — deferred with the router migration.)
  useEffect(() => {
    if (!keyLive) return;
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [keyLive]);

  return {
    step,
    isSubmitting,
    inFlight,
    error,
    verifyRequired,
    keyLive,

    async run(body) {
      setError(null);
      setInFlight(false);
      setIsSubmitting(true);
      try {
        return await submit(body);
      } catch (caught) {
        const failure = classifyMoneyProblem(caught as ApiProblem, options);
        if (failure.kind === 'inFlight') {
          setInFlight(true);
        } else if (failure.kind === 'message') {
          setError(failure.text);
        }
        // 'verify' — the hook has latched verifyRequired and that view renders; setting an error
        // too would put a red banner under a screen whose whole job is to say "we don't know".
        // 'silent' — the user dismissed the PIN modal. Stay on review; Send re-triggers it.
        return undefined;
      } finally {
        setIsSubmitting(false);
      }
    },

    onBodyEdit() {
      // Editing any body field rotates the key — blocked whenever one is LIVE, not merely while
      // submitting. Nulling a retained key and resending mints a fresh key = a NEW intent = a
      // double-spend if the original committed. The safe forward action on a retained key is
      // Send-again, which reuses it.
      if (keyLive) return;
      resetIntent();
      setInFlight(false);
      setError(null);
    },

    toReview() {
      setStep('review');
    },

    toForm() {
      // Belt and braces. The callers also render Back with `disabled={keyLive}`, but that is one
      // JSX attribute away from being deleted, and the fields behind this transition are what
      // rotate the key.
      if (keyLive) return;
      setStep('form');
    },

    startOver() {
      // Deliberately NOT guarded on keyLive: reaching this means the key was already dropped and
      // `verifyRequired` latched, and re-arming `submit` is the entire point of the action.
      resetIntent();
      setStep('form');
    },

    requestLeave(to) {
      if (!keyLive) navigate(to);
    },
  };
}
