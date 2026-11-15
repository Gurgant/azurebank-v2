import { useCallback, useEffect, useRef, useState } from 'react';
import { useBlocker, useNavigate } from 'react-router-dom';
import type { ApiProblem } from '../api/problemBaseQuery';
import {
  classifyMoneyProblem,
  type DomainMessages,
  type MessageScope,
  type MoneyPhase,
} from '../api/moneyProblem';
import { useMountedRef } from './useMountedRef';
import {
  useIdempotentMutation,
  type IdempotentTrigger,
  type SentRequest,
} from './useIdempotentMutation';

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

/**
 * One confirm, from the press that starts it. `abandoned` turns true when the visitor chooses to
 * leave, or navigates to the sign-in page, and stays true: the router can then land on another
 * entry of the same route, the page stays mounted, and what the attempt was still waiting for (the
 * answer to its request for an authorisation) must not become a send. A press made after that is
 * a new attempt.
 */
export interface MoneyAttempt {
  readonly abandoned: boolean;
}

export interface MoneyWizard<TBody, TResult> {
  step: MoneyWizardStep;
  /** True from the moment Send is pressed until the request settles. Disables Send. */
  isSubmitting: boolean;
  /** The server is still processing THIS key. Pressing Send again is safe and reuses it. */
  inFlight: boolean;
  /**
   * The last send was answered 503 with `applied: false`: the API says it changed nothing, and the
   * key is kept for the retry (ADR-0058). Read to choose the resend bar's words and control — a
   * plain "Try again" instead of the "may or may not have gone through" check — never the key.
   */
  nothingChanged: boolean;
  error: string | null;
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
  /**
   * The result could not be confirmed. The flow must show its verify view and offer no exits —
   * unless `wentThrough` is set too, which a flow must test first.
   */
  verifyRequired: boolean;
  /**
   * The API said the payment was committed and could not return its receipt: 409
   * `IDEMPOTENCY_RESULT_UNKNOWN` with `applied: true` (ADR-0009). `verifyRequired` is set with
   * it, so a flow that tests this first can say that the payment went through, and one that does
   * not still shows a check and never a live Send. `startOver` re-arms nothing then.
   */
  wentThrough: boolean;
  /**
   * What the sends under a key the check view dropped carried, for the flow to ask the listing by
   * key (`useMovementCheck`); null under no check view, and under a proven commit.
   */
  lastSent: SentRequest<TBody> | null;
  /**
   * The listing found the movement: `wentThrough` is set, and `startOver` re-arms nothing from
   * then on, as after `applied: true`.
   */
  confirmWentThrough: () => void;
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
  run: (
    body: TBody,
    /** Out-of-band request metadata — the step-up authorisation, which is not part of the intent. */
    extras?: { stepUpAuthorizationId?: string },
    /** The attempt that asks for this send: refused, with nothing sent, once it is abandoned. */
    attempt?: MoneyAttempt,
  ) => Promise<TResult | undefined>;

  /**
   * Take the attempt a confirm starts, before its first request. A flow that asks for something
   * before it calls `run` takes it, reads `abandoned` when the answer comes, and passes it to `run`.
   */
  beginAttempt: () => MoneyAttempt;

  /**
   * Apply a failure this wizard did not produce. Minting an authorisation carries no idempotency
   * key, so it cannot go through `run` — but it fails with the same shapes, and this routes it
   * through the same classification rather than letting a page grow its own copy. `phase` says
   * which request failed: an outage during the mint moved no money, and is worded so.
   */
  failFrom: (problem: ApiProblem, context: { phase: MoneyPhase }) => void;
  /** Call after ANY edit that changes the request body. No-op while a key is live. */
  onBodyEdit: () => void;
  toReview: () => void;
  /** Review -> PIN. The last thing before the money moves. */
  toPin: () => void;
  /** Back to the form. Guarded: a live key must not reach the fields that would rotate it. */
  /**
   * Refuse to advance, and say why, with no request having been made.
   *
   * The banner otherwise only ever comes from a failed `run`, which means the only way a flow could
   * explain a refusal was to attempt the thing it was refusing. The funds gate needs the opposite:
   * stop BEFORE the PIN step, and still say something.
   */
  fail: (text: string) => void;
  toForm: () => void;
  /**
   * The verify view's "it didn't go through" — abandons the intent so the next send is a NEW one.
   * Not to be offered while `wentThrough`, and it could not re-arm the send there: the
   * idempotency hook refuses the reset.
   */
  startOver: () => void;
  /**
   * Show the check view over a key that is held, for an answer the flow cannot act on: the key is
   * let go, so the exits open, and no send leaves until `startOver`. The same latch the hook sets
   * on `IDEMPOTENCY_RESULT_UNKNOWN`, and the one the money dialogs set on an edit under a held key
   * (`requireVerify` in `useIdempotentMutation`).
   */
  requireVerify: () => void;
  /**
   * The ONLY way out. Refuses while a key is live. It is the page's own exit and not the
   * browser's Back, so the blocker does not hold it.
   */
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
  options: {
    messages: DomainMessages;
    fallback: string;
    /**
     * The flow's request for an authorisation is out. It comes before the send, and on a first
     * attempt no key is held yet, so `keyLive` can be false for all of it: the browser's Back is
     * held for it all the same.
     */
    minting: boolean;
  },
): MoneyWizard<TBody, TResult> {
  const navigate = useNavigate();
  const {
    submit,
    resetIntent,
    verifyRequired,
    wentThrough,
    keyRetained,
    requireVerify,
    lastSent,
    confirmWentThrough,
  } = useIdempotentMutation(trigger);

  const [step, setStep] = useState<MoneyWizardStep>('form');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [inFlight, setInFlight] = useState(false);
  const [nothingChanged, setNothingChanged] = useState(false);
  // Held WITH its scope, but exposed as a bare string: the scope decides lifetime, and no caller
  // needs to know about it, so not one line of page markup moves.
  const [failure, setFailure] = useState<{ text: string; scope: MessageScope } | null>(null);
  const lastProblem = useRef<ApiProblem | null>(null);
  const mounted = useMountedRef();

  const keyLive = isSubmitting || keyRetained;
  // What the browser's Back is held for: a live key, and the request for an authorisation that
  // comes before the send, when a first attempt holds no key yet.
  const holdsBack = keyLive || options.minting;
  // Set while `requestLeave` navigates, so the blocker below lets the page's own exit through.
  const leavingByItself = useRef(false);
  // Set when "Leave anyway" is chosen. The router removes the page only once it has drawn the next
  // one, a moment after the choice, and `run` sends nothing from a page in that moment.
  const leaving = useRef(false);
  // Counts the choices to leave and the navigations to /login. An attempt remembers the count it
  // started under, so it is abandoned for good by the first choice after it, whatever the router
  // then does.
  const abandonments = useRef(0);

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
    setNothingChanged(false);
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

    So the contract is the one `beforeunload` already concedes in `useIdempotentMutation`: warn,
    then defer to the user.
    It does not close the double-spend window — an abandoned key is still lost until TTL — it makes
    the abandonment deliberate instead of silent.

    It is held from the sixth digit, not from the send. The request for the authorisation comes
    first and the send follows its answer with nothing between them, so on a first attempt a visitor
    who went back in that wait left with no key held and no way to ask after the send. One who
    leaves anyway, by "Leave anyway", takes the page with them, and `useIdempotentMutation` then
    refuses the send. Between the choice and the page going, `run` refuses it too (`leaving`).
    A sign-out is not that: `ProtectedRoute` puts `<Navigate to="/login">` in the place of the
    page the moment the session leaves `authenticated`, so the page, its mounted ref and this
    blocker are gone before the router is asked for `/login`, and the mounted check refuses the
    send. A navigation to the sign-in page that does reach a page still mounted (a Back onto a
    `/login` entry) is not held and has no choice to wait for, so the predicate abandons every
    attempt as it starts.

    A Back can also land on another entry of the same route, and that page is not removed. The
    attempt then carries the refusal: a flow takes `beginAttempt` before its request for the
    authorisation, "Leave anyway" abandons every attempt taken before it, and neither the flow nor
    `run` sends for one. A press after the page is back is a new attempt.

    The predicate is a FUNCTION, not `useBlocker(holdsBack)`, and the `/login` exemption is not
    cosmetic: both money routes sit inside `ProtectedRoute`, which replaces the page with
    `<Navigate to="/login" replace />` the moment auth leaves `authenticated`, and the router
    consults the blocker on REPLACE too. A boolean blocker that were still in place would put
    "are you sure?" in front of a forced sign-out, holding the user on a page whose credentials
    are already dead.

    The page's own exit is not the browser's Back. When the authorisation is refused and the answer
    sends the visitor on (PIN_REQUIRED), `requestLeave` navigates before the render that ends
    `minting` has reached the blocker: it would be held, and then released into the page it was
    to leave.
  */
  const blocker = useBlocker(
    useCallback(
      ({ nextLocation }: { nextLocation: { pathname: string } }) => {
        if (nextLocation.pathname === '/login') {
          // A navigation to /login that reaches a mounted page (a Back onto a /login entry):
          // never held, and not read from `holdsBack` either, which can be one render behind
          // between the authorisation's answer and the send. An attempt that is not pending loses
          // nothing by being counted. `leaving` is left alone: nothing would clear it.
          abandonments.current += 1;
          return false;
        }
        return !leavingByItself.current && holdsBack;
      },
      [holdsBack],
    ),
  );

  // What is held can clear while the prompt is still up: leave the dialog open, press Send again,
  // and a success clears the intent underneath it — leaving "you have unsent money" standing over
  // a completed transfer. The same when the authorisation is refused and no key is held.
  // Reset closes it.
  useEffect(() => {
    if (blocker.state === 'blocked' && !holdsBack) blocker.reset?.();
  }, [blocker, holdsBack]);

  // The choice to leave stands only until the router has finished with it, so that a press made
  // after it, on a page the router did not remove, can send. A POP can land on another entry of
  // the same route. What was started before the choice stays abandoned: that is the attempt's own.
  useEffect(() => {
    if (blocker.state === 'unblocked') leaving.current = false;
  }, [blocker.state]);

  // The other path no in-app control covers, a refresh or tab-close while a key is live, is
  // `useIdempotentMutation`'s, for every money send. Kept ALONGSIDE the blocker, not replaced by
  // it — `useBlocker`'s own JSDoc says it "does not handle hard-reloads or cross-origin
  // navigations", which is exactly what that catches. (Until 2026-10-01 the listener was here,
  // and the deposit and withdraw dialogs had none.)

  /** The one place a classified failure becomes state; see `failFrom`. */
  const applyProblem = (caught: ApiProblem, phase: MoneyPhase) => {
    lastProblem.current = caught ?? null;
    // The latest failure decides, so a later one without `applied: false` takes the words back.
    setNothingChanged(phase === 'send' && caught?.applied === false);
    const failure = classifyMoneyProblem(caught, {
      messages: options.messages,
      fallback: options.fallback,
      phase,
    });
    if (failure.kind === 'inFlight') {
      setInFlight(true);
    } else if (failure.kind === 'message') {
      setFailure({ text: failure.text, scope: failure.scope });
    }
  };

  return {
    exitPrompt:
      blocker.state === 'blocked'
        ? {
            leave: () => {
              abandonments.current += 1;
              leaving.current = true;
              blocker.proceed?.();
            },
            stay: () => blocker.reset?.(),
          }
        : null,
    step,
    isSubmitting,
    inFlight,
    nothingChanged,
    error: failure?.text ?? null,
    lastProblem,
    verifyRequired,
    wentThrough,
    lastSent,
    confirmWentThrough,
    keyLive,

    beginAttempt() {
      const started = abandonments.current;
      return {
        get abandoned() {
          return abandonments.current !== started;
        },
      };
    },

    async run(body, extras, attempt) {
      if (leaving.current || attempt?.abandoned) {
        // Resolves as a failure that says nothing: the visitor chose to leave, and no key is made.
        lastProblem.current = null;
        return undefined;
      }
      setFailure(null);
      lastProblem.current = null;
      setInFlight(false);
      setNothingChanged(false);
      setIsSubmitting(true);
      try {
        return await submit(body, extras);
      } catch (caught) {
        applyProblem(caught as ApiProblem, 'send');
        // 'verify' — the hook has latched verifyRequired and that view renders; setting an error
        // too would put a red banner under a screen whose whole job is to say "we don't know".
        // 'silent' — the user dismissed the PIN modal. Stay on review; Send re-triggers it.
        return undefined;
      } finally {
        setIsSubmitting(false);
      }
    },

    /*
      Report a failure that did NOT come through `run`.

      Minting a step-up authorisation (ADR-0042) is a request this wizard does not own — it carries
      no idempotency key, so it cannot go through `submit` — but its refusals are the same ones the
      send path already classifies, and a page that reimplemented them would be a second copy free
      to drift. `run` and this share one code path on purpose.
    */
    failFrom(problem, { phase }) {
      applyProblem(problem, phase);
    },

    onBodyEdit() {
      // Editing any body field rotates the key — blocked whenever one is LIVE, not merely while
      // submitting. Nulling a retained key and resending mints a fresh key = a NEW intent = a
      // double-spend if the original committed. The safe forward action on a retained key is
      // Send-again, which reuses it.
      if (keyLive) return;
      resetIntent();
      setInFlight(false);
      setNothingChanged(false);
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

    fail(text) {
      // 'input' scope, by the rule in moneyProblem: the message names a value that is editable on
      // the destination step (the amount), so it must SURVIVE the trip back to the form rather
      // than being cleared by the transition that takes the user to where they can fix it.
      setFailure({ text, scope: 'input' });
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

    requireVerify,

    requestLeave(to) {
      // A page the router has already removed, a sign-out drawing /login being one, has nothing
      // to leave. A refusal of the authorisation that comes in late must not take the visitor
      // from the page they are on now.
      if (keyLive || !mounted.current) return;
      // The router asks the blocker inside this call, so the flag is read there and no later.
      leavingByItself.current = true;
      try {
        navigate(to);
      } finally {
        leavingByItself.current = false;
      }
    },
  };
}
