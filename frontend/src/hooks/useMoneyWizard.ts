import { useCallback, useEffect, useRef, useState } from 'react';
import { useBlocker, useNavigate } from 'react-router-dom';
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

/**
 * `pin` is ADR-0041's step. A transfer now carries its PIN in the request body and the API verifies
 * it, so the credential is collected HERE — in the flow — rather than by the root step-up modal
 * reacting to a 403 from the BFF. Both consumers of this hook move money and both need it, which is
 * why the step lives in the shared machine; the PIN VALUE does not, because only the page knows
 * which body field it belongs in.
 */
export type MoneyWizardStep = 'form' | 'review' | 'pin';

export interface MoneyWizard<TBody, TResult> {
  step: MoneyWizardStep;
  /** True from the moment Send is pressed until the request settles. Disables Send. */
  isSubmitting: boolean;
  /** The server is still processing THIS key. Pressing Send again is safe and reuses it. */
  inFlight: boolean;
  error: string | null;
  /**
   * The `errorCode` of the most recent failure, or null. Exposed because the PIN step has to react
   * to WHICH failure it was — clear the boxes on a wrong PIN, show the lock on PIN_LOCKED, send an
   * un-enrolled user to setup — and matching on the rendered SENTENCE to decide that would break
   * the moment someone rewords a message.
   */
  /**
   * The most recent failure, as a REF rather than state — and the distinction is the whole point.
   *
   * `run` is awaited, so the handler that reads this is still executing inside the render that
   * called it. State written during the await schedules the NEXT render; it does not reach the
   * object this handler closed over. Reading `wizard.lastErrorCode` from such a handler therefore
   * returned the value from BEFORE the call — null on the first failure — and every PIN recovery
   * branch was dead code. A ref is the same object across renders, so a write during `run` is
   * visible to the awaiting caller immediately.
   *
   * Proven by transfer-pin-recovery.test.tsx, which fails on all three branches without this.
   *
   * ⚠️ VALID ONLY BETWEEN `run`'s start and the caller's resumption. `run` nulls it on entry and
   * writes it in the catch, and nothing re-renders when it changes — so reading it DURING RENDER is
   * always a mistake. It exists for the awaiting handler and for nothing else.
   */
  lastProblem: { current: ApiProblem | null };
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
  /** Review -> PIN. The last thing before the money moves. */
  toPin: () => void;
  /** Back to the form. Guarded: a live key must not reach the fields that would rotate it. */
  toForm: () => void;
  /** The verify view's "it didn't go through" — abandons the intent so the next send is a NEW one. */
  startOver: () => void;
  /** The ONLY way out. Refuses while a key is live. */
  requestLeave: (to: string) => void;
  /**
   * The browser's own Back, which no in-app guard can reach.
   *
   * `null` unless a POP is being held. When set, the page must ask — and both answers must be
   * offered: `leave` abandons the intent, `stay` cancels the navigation.
   */
  exitPrompt: { leave: () => void; stay: () => void } | null;
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
  const lastProblem = useRef<ApiProblem | null>(null);

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

  /*
    The browser's Back button, which the in-app guards cannot reach.

    `useBlocker` is Data-mode only — the availability table in `react-router/docs/start/modes.md`
    ticks it for Framework and Data and leaves Declarative blank — which is why this waited for the
    router migration the comment here used to defer it to (ADR-0028).

    It WARNS; it does not veto, and that is the decision the rest of the wizard argues against.
    Every other exit refuses while a key is live: `backDisabled`, `closeDisabled`, `requestLeave`,
    `toForm`. Matching them here would lock the last door. `shouldKeepKey` retains the key on any
    >= 500 while `verifyRequired` stays false, so after a 502 the verify view — the one screen that
    offers "check my transactions" and "start over" — does not render, Back and Close are disabled,
    and `requestLeave` no-ops. Browser Back is the ONLY exit from that state short of closing the
    tab. A blocker with no `proceed()` would make closing the tab the app's error recovery.

    So the contract is the one `beforeunload` already concedes below: warn, then defer to the user.
    It does not close the double-spend window — an abandoned key is still lost until TTL — it makes
    the abandonment deliberate instead of silent.

    The predicate is a FUNCTION, not `useBlocker(keyLive)`, and the `/login` exemption is not
    cosmetic: `ProtectedRoute` renders `<Navigate to="/login" replace />` the moment auth leaves
    `authenticated`, the router consults the blocker on REPLACE too, and both money routes sit
    inside `ProtectedRoute`. A boolean blocker would put "are you sure?" in front of a FORCED
    session-expiry logout, holding the user on a page whose credentials are already dead.
  */
  const blocker = useBlocker(
    useCallback(
      ({ nextLocation }: { nextLocation: { pathname: string } }) =>
        keyLive && nextLocation.pathname !== '/login',
      [keyLive],
    ),
  );

  // The key can clear while the prompt is still up: leave the dialog open, press Send again, and a
  // success clears the intent underneath it — leaving "you have unsent money" standing over a
  // completed transfer. Reset closes it.
  useEffect(() => {
    if (blocker.state === 'blocked' && !keyLive) blocker.reset?.();
  }, [blocker, keyLive]);

  // The other path no in-app control covers: a refresh or tab-close while a key is live. Kept
  // ALONGSIDE the blocker, not replaced by it — `useBlocker`'s own JSDoc says it "does not handle
  // hard-reloads or cross-origin navigations", which is exactly what this catches.
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
    exitPrompt:
      blocker.state === 'blocked'
        ? { leave: () => blocker.proceed?.(), stay: () => blocker.reset?.() }
        : null,
    step,
    isSubmitting,
    inFlight,
    error: failure?.text ?? null,
    lastProblem,
    verifyRequired,
    keyLive,

    async run(body) {
      setFailure(null);
      lastProblem.current = null;
      setInFlight(false);
      setIsSubmitting(true);
      try {
        return await submit(body);
      } catch (caught) {
        lastProblem.current = (caught as ApiProblem) ?? null;
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
      lastProblem.current = null;
    },

    toReview() {
      goToStep('review', { keepInputErrors: true });
    },

    toPin() {
      // keepInputErrors: false — an INSUFFICIENT_FUNDS banner from a previous attempt must not
      // follow the user onto a screen where the only editable thing is the PIN.
      goToStep('pin', { keepInputErrors: false });
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
