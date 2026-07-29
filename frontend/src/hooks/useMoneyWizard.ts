import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import type { ApiProblem } from '../api/problemBaseQuery';
import { classifyMoneyProblem, type DomainMessages, type MessageScope } from '../api/moneyProblem';
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
  // Held WITH its scope, but exposed as a bare string: the scope decides lifetime, and no caller
  // needs to know about it, so not one line of page markup moves.
  const [failure, setFailure] = useState<{ text: string; scope: MessageScope } | null>(null);

  const keyLive = isSubmitting || keyRetained;

  /*
    One rule, and it is the whole policy:

      every step transition clears the transient failure state, EXCEPT a failure the server
      attributed to a value the user can still edit — that one survives, and is cleared instead by
      the edit that supersedes it (onBodyEdit) or by the next run().

    Worth being honest about in the ADR: NO source decides this. Four research lanes and ~40 primary
    sources — WCAG, GOV.UK, Fluent, Carbon, USWDS, Cloudscape, NN/g — say nothing about what an error
    message should do across a wizard step change. AWS Cloudscape ships a Wizard, documents a review
    step with back-navigation AND models server errors, and still says nothing. So "clear" and "keep"
    both have zero documented backing; this is a reasoned decision, not a standard.

    What IS standards-backed is next door: WCAG 2.2 SC 3.3.7 Redundant Entry (Level A) forbids making
    the user re-enter data. This helper only ever touches banners, never the form.
  */
  const goToStep = (next: MoneyWizardStep, { keepInputErrors }: { keepInputErrors: boolean }) => {
    setInFlight(false);
    setFailure((current) => (keepInputErrors && current?.scope === 'input' ? current : null));
    setStep(next);
  };

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
    error: failure?.text ?? null,
    verifyRequired,
    keyLive,

    async run(body) {
      setFailure(null);
      setInFlight(false);
      setIsSubmitting(true);
      try {
        return await submit(body);
      } catch (caught) {
        const failure = classifyMoneyProblem(caught as ApiProblem, options);
        if (failure.kind === 'inFlight') {
          setInFlight(true);
        } else if (failure.kind === 'message') {
          setFailure({ text: failure.text, scope: failure.scope });
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
      setFailure(null);
    },

    toReview() {
      goToStep('review', { keepInputErrors: true });
    },

    toForm() {
      // Belt and braces. The callers also render Back with `disabled={keyLive}`, but that is one
      // JSX attribute away from being deleted, and the fields behind this transition are what
      // rotate the key.
      if (keyLive) return;
      goToStep('form', { keepInputErrors: true });
    },

    startOver() {
      // Deliberately NOT guarded on keyLive: reaching this means the key was already dropped and
      // `verifyRequired` latched, and re-arming `submit` is the entire point of the action.
      resetIntent();
      // Unconditional, input errors included: this ABANDONS the intent, so nothing said about the
      // previous attempt is still said about the request being rebuilt.
      goToStep('form', { keepInputErrors: false });
    },

    requestLeave(to) {
      if (!keyLive) navigate(to);
    },
  };
}
