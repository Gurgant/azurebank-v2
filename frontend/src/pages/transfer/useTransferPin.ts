import { useId, useRef, useState } from 'react';
import type { FieldErrors } from 'react-hook-form';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { isLimiterRefusal, isProvenCommit, type MoneyPhase } from '../../api/moneyProblem';
import {
  type AccountResponse,
  type TransferRequest,
  type TransferResponse,
  type WithReplay,
  type useAuthoriseTransferMutation,
} from '../../features/api/apiSlice';
import type { MoneyWizard } from '../../hooks/useMoneyWizard';
import { useMovementCheck } from '../../hooks/useMovementCheck';
import { retryDeadline } from '../../components/feedback';
import {
  insufficientFundsMessage,
  type TransferFormOutput,
  type TransferFormValues,
} from '../../forms/moneySchemas';
import { availableBalanceOf } from '../../utils/availableBalance';
import type { Recipient } from './useRecipientCheck';

export const PIN_LENGTH = 6;
const DEFAULT_PIN_LOCK_SECONDS = 15 * 60;

export interface SuccessData {
  amount: number;
  recipientName: string;
  recipientAzureTag: string;
  newBalance: number;
  transactionNumber: string;
  replayed: boolean;
}

/**
 * What the transfer page's PIN step and the send behind it do, in one place: the PIN and the
 * authorisation held for it, the lock, the check view's ask, the receipt's state, and `onValid`
 * with `handleRefusal`, which mint, send and answer a refusal.
 *
 * The page's wizard, its one request for an authorisation and that request's wait come in as
 * arguments: this has no request hook for the mint or the send and no wizard of its own, because the wizard's blocker
 * and the page's exit rule must read the one wait. The three setters are the page's, which three
 * refusals ask to act. `onValid` takes the render's account and recipient as arguments: it must
 * send what the review showed.
 */
export function useTransferPin({
  wizard,
  authoriseTransfer,
  isMinting,
  setReturnToAmount,
  setSourceRefused,
  setPayeeRefused,
}: {
  wizard: MoneyWizard<TransferRequest, WithReplay<TransferResponse>>;
  authoriseTransfer: ReturnType<typeof useAuthoriseTransferMutation>[0];
  isMinting: boolean;
  setReturnToAmount: (value: boolean) => void;
  setSourceRefused: (value: boolean) => void;
  setPayeeRefused: (value: boolean) => void;
}) {
  const { keyLive, isSubmitting, requestLeave } = wizard;

  /*
    The PIN the sixth digit just completed.

    A REF, not the state, and for the reason `lastProblem` is one: `onComplete` fires from inside
    `onChange`, so `pin` has not re-rendered yet when the submit begins. Reading state here would be
    one render stale — invisible in a hand test, a flake in CI. `setPin` still drives the display.
  */
  const enteredPin = useRef('');

  /*
    The authorisation minted for the intent currently in flight.

    A retry of a RETAINED key is the same intent, so it must not mint a second authorisation: the
    first one may already have been consumed by the request whose answer never arrived, and if it
    was not, this one is still the authorisation for these exact fields. Minting again would leave
    an orphan row and tell the server a different story than the first attempt did.
  */
  const lastAuthorization = useRef<string | null>(null);

  /*
    A reactive mirror of "we are holding an authorisation", because a ref cannot gate a render.

    The ref stays the source of truth — `onValid` reads it synchronously between the mint and the
    send, and turning it into state would reintroduce exactly the stale read `useMoneyWizard`
    documents. This is the same split `useIdempotentMutation` already uses for the key itself.
  */
  const [authorizationHeld, setAuthorizationHeld] = useState(false);

  /*
    When to offer a re-send, and why it is not `inFlight`.

    `inFlight` is set only for 409 IDEMPOTENCY_IN_FLIGHT. But the case this control was built for —
    a response that never arrived — is `status: 'NETWORK'`, which `classifyMoneyProblem` routes to a
    plain message. So for the exact scenario it exists to serve, the button did not render, and with
    every exit held by `exitLocked` the user had no control at all: not a loop, a dead end.

    The honest condition is "we are holding a key AND an authorisation to re-present". That also
    makes the two states mutually exclusive by construction rather than by invariant: holding an
    authorisation offers the re-send, and having had it refused empties the PIN boxes and asks for
    six digits instead.
  */
  const canResend = keyLive && authorizationHeld && !isSubmitting && !isMinting;

  // `pinNonce` remounts PinInput so a cleared retry refocuses box 1 — the same device WithdrawDialog
  // uses, for the same reason.
  const [pin, setPin] = useState('');
  const pinHintId = useId();
  const pinErrorId = useId();
  const pinLockId = useId();
  const [pinError, setPinError] = useState(false);
  const [pinNonce, setPinNonce] = useState(0);
  /*
    An ABSOLUTE deadline (D13), shared with the withdraw dialog and the step-up modal.

    This page used to drive its own one-second decrement, and so did the sibling transfer page, and
    the two dialogs drove nothing at all — three implementations of one requirement, two of which
    were a frozen countdown. `RetryCountdown` is the single primitive; storing the deadline rather
    than a duration is what makes a second lock with the identical retryAfterSeconds mint a fresh one
    instead of reviving an elapsed number.
  */
  const [pinLockDeadline, setPinLockDeadline] = useState<number | null>(null);

  /*
    Under the check view, the listing is asked by the key that was sent, on the account the
    transfer was sent from: the payee's row carries no key, so a closed source leaves nothing
    else to ask. A movement found there is the transfer going through, and the PIN and the
    authorisation go with it, as on `applied: true` (`handleRefusal`).
  */
  const check = useMovementCheck(
    wizard.lastSent,
    (sent) => ({ first: { accountId: sent.fromAccountId, type: 'TransferOut' } }),
    () => {
      wizard.confirmWentThrough();
      setPin('');
      enteredPin.current = '';
      lastAuthorization.current = null;
      setAuthorizationHeld(false);
    },
  );
  const [success, setSuccess] = useState<SuccessData | null>(null);

  /*
    The confirm — the mint, then the send — as the visitor sees it: one wait from the sixth digit,
    with one hint. Not `isMinting || isSubmitting`: if a render fell between the mint's answer and
    the send's start, the hint would take it for a new wait and count again from zero. Set only
    once nothing can return early before a request leaves, and cleared in a `finally`, so it never
    outlives the confirm.
  */
  const [confirming, setConfirming] = useState(false);

  /**
   * `handleSubmit`'s invalid branch. Without it, a form that went invalid while the PIN step was
   * open made Send do nothing at all — no request, no message. The errors arrive as an argument so
   * this reports what actually failed rather than guessing.
   */
  const onInvalid = (errors: FieldErrors<TransferFormValues>) => {
    wizard.toForm();
    wizard.fail(errors.amount?.message ?? 'Please check the details and try again.');
  };

  const onValid = async (
    data: TransferFormOutput,
    selectedAccount: AccountResponse | null,
    recipient: Recipient | null,
  ) => {
    if (!selectedAccount || !recipient) return;
    if (enteredPin.current.length !== PIN_LENGTH || pinLockDeadline !== null) return;
    // Narrowed to a const BEFORE the await, and the receipt is built from that same const. The
    // review screen and the request therefore cannot name two different people.
    const confirmed = recipient;

    /*
      The form has refused an amount over its own bound already (`onInvalid`). That bound is
      state, copied from the cached balance by an effect, so it runs one render behind it: this
      is for the render in which the balance has fallen and the bound has not followed yet. A
      balance this tab has not heard of is the server's to refuse.
    */
    const stillAvailable = availableBalanceOf(selectedAccount);
    if (data.amount > stillAvailable) {
      wizard.toForm();
      wizard.fail(insufficientFundsMessage(stillAvailable));
      return;
    }

    /*
      MINT, then SEND (ADR-0042). Two calls, one user action — the sixth digit starts both, so the
      two-minute window is normally milliseconds wide and the user never learns it exists.

      The mint carries no idempotency key by design, so it cannot go through `wizard.run`. Its
      refusals are nevertheless the SAME ones the send already handles — 401 INVALID_PIN, 429
      PIN_LOCKED, 422 PIN_REQUIRED — so both paths funnel into `handleRefusal` rather than growing a
      second copy free to drift.
    */
    /*
      `keyLive` means the idempotency hook is holding a key from an attempt whose outcome is
      unknown — IN_FLIGHT, a network failure, a 5xx. The only sanctioned forward action there is to
      re-send the SAME intent, so we re-present the SAME authorisation rather than minting a new one.
    */
    const attempt = wizard.beginAttempt();
    /*
      What the page lets go of when the attempt is abandoned: the PIN, and the authorisation this
      attempt minted, which goes with it. The key stays. An attempt that re-presents a held
      authorisation sends at once, with no wait in which the visitor could leave, so it never ends
      here.
    */
    const letGoOfTheAttempt = (mintedHere: boolean) => {
      setPin('');
      enteredPin.current = '';
      setPinNonce((n) => n + 1);
      if (mintedHere) {
        lastAuthorization.current = null;
        setAuthorizationHeld(false);
      }
    };
    let mintedHere = false;
    setConfirming(true);
    try {
      let authorizationId: string;
      if (keyLive && lastAuthorization.current) {
        authorizationId = lastAuthorization.current;
      } else {
        try {
          const minted = await authoriseTransfer({
            fromAccountId: data.fromAccountId,
            recipientAzureTag: confirmed.azureTag,
            amount: data.amount,
            pin: enteredPin.current,
          }).unwrap();
          authorizationId = minted.authorizationId;
          lastAuthorization.current = authorizationId;
          setAuthorizationHeld(true);
          mintedHere = true;
        } catch (caught) {
          // The visitor has left, and this answer is for a page they are no longer on: a refusal
          // here must not navigate to PIN setup nor show a wrong PIN on the page they came back to.
          if (attempt.abandoned) {
            letGoOfTheAttempt(false);
            return;
          }
          // Not a `run` failure, so the wizard has not classified it — hand it the same classifier.
          wizard.failFrom(caught as ApiProblem, { phase: 'mint' });
          handleRefusal(caught as ApiProblem, 'mint');
          return;
        }
      }

      /*
        The visitor chose "Leave anyway" while the PIN was being checked. The Back may have landed
        on another entry of this page, which stays, or on another route, whose page is going.
        Nothing is sent for the PIN they typed before they left; the boxes are emptied, as for an
        outage of the check, and a PIN typed now is a new attempt. An authorisation this attempt
        minted goes with it; the answer of a refused mint is dropped the same way, in the catch
        above.
      */
      if (attempt.abandoned) {
        letGoOfTheAttempt(mintedHere);
        return;
      }

      const result = await wizard.run(
        {
          fromAccountId: data.fromAccountId,
          recipientAzureTag: confirmed.azureTag,
          amount: data.amount,
        },
        // A HEADER at the wire, never a body field: the server fingerprints the body alone.
        { stepUpAuthorizationId: authorizationId },
        attempt,
      );
      // `run` resolves to undefined when it failed — and it has already set the banner, the
      // in-flight note or the verify view. Under `strict` this early return is not optional:
      // reading `result.newBalance` without it does not compile.
      if (!result) {
        /*
          The PIN outcomes, keyed on the CODE rather than the rendered sentence (the wizard exposes
          `lastProblem` as a REF precisely so this does not have to match on prose, and so the read
          is not one render stale).

          A wrong PIN is safe to retry in place: 401 is exempt from the global logout, and the
          idempotency hook has already dropped the key — so the corrected-PIN retry mints a fresh
          one rather than replaying the refused body.
        */
        handleRefusal(wizard.lastProblem.current, 'send');
        return;
      }

      setPin('');
      enteredPin.current = '';
      lastAuthorization.current = null;
      setAuthorizationHeld(false);

      setSuccess({
        amount: data.amount,
        recipientName: confirmed.displayName,
        recipientAzureTag: confirmed.azureTag,
        newBalance: result.newBalance,
        transactionNumber: result.transactionNumber,
        replayed: result.replayed,
      });
    } finally {
      setConfirming(false);
    }
  };

  /**
   * What the PIN step does about a refusal, shared by the mint and the send so the two cannot drift.
   * The banner itself is the wizard's; this owns only the PIN box and the lock. `phase` is which of
   * the two failed: the first arm is the send's alone, the last two the mint's.
   */
  function handleRefusal(refusal: ApiProblem | null, phase: MoneyPhase) {
    if (phase === 'send' && isProvenCommit(refusal)) {
      /*
        The server said the transfer was committed: the page shows the went-through view, and
        nothing on it sends again. So the page's own copies of the PIN and of the authorisation
        are let go as the success path lets them go, a few lines above: the transfer they were for
        is done. Not every copy: the two mutation hooks keep their last arguments and answer, the
        PIN and the authorisation among them, until this page unmounts, after a success too. Read
        from the wizard's ref, like every arm here, so it is this answer and not the one before it.

        Only the send. A mint carries no key, so the same answer to it proves nothing and brings
        no view: emptying the boxes there would leave the visitor on the PIN step with nothing
        typed and nothing said. WithdrawDialog's arm has the same condition.
      */
      setPin('');
      enteredPin.current = '';
      lastAuthorization.current = null;
      setAuthorizationHeld(false);
    } else if (refusal?.errorCode === 'INVALID_PIN') {
      setPin('');
      enteredPin.current = '';
      setPinError(true);
      setPinNonce((n) => n + 1);
    } else if (
      refusal?.errorCode === 'AUTHORIZATION_EXPIRED' ||
      refusal?.errorCode === 'AUTHORIZATION_INVALID'
    ) {
      /*
          The user STAYS on this step, and the form keeps the amount and the payee.

          Not a courtesy: WCAG 2.2 SC 3.3.7 Redundant Entry is LEVEL A, and its exception covers
          security information only. The PIN is inside it; the amount and payee are not, so
          discarding them would be a failure of the criterion rather than a UX preference.

          No `setPinError` — an expiry is not a wrong PIN. It costs no attempt server-side, and
          styling the boxes red would tell the user the lock is closer when it is not.
        */
      setPin('');
      enteredPin.current = '';
      setPinNonce((n) => n + 1);
      /*
          AND DROP THE AUTHORISATION — the line this whole PR exists for.

          `onValid` re-presents `lastAuthorization.current` whenever a key is live, which is right
          for a lost response (re-send the same intent) and catastrophic here: the authorisation the
          server just refused would be re-sent forever, and the six digits the user keeps typing
          would never reach a mint. Dropping it sends the next completion down the mint branch,
          while `submit`'s `keyRef.current ??=` reuses the SAME idempotency key and the body is
          rebuilt from the same unchanged form values — byte-identical, so no 422.

          MEASURED end to end on the running API: key K with an expired authorisation answers 401
          AUTHORIZATION_EXPIRED and releases the record; key K with a freshly minted one answers
          201, no `Idempotency-Replayed`. One payment, one key, two authorisations.
        */
      lastAuthorization.current = null;
      setAuthorizationHeld(false);
    } else if (refusal?.errorCode === 'DAILY_LIMIT_EXCEEDED') {
      /*
          The daily outgoing-transfer bound (ADR-0050). The expiry arm's clean-up, for three reasons
          of its own — and NO `setPinError` — and then a move the expiry arm does not make.

          NO RED BOXES. A1 measured `PinAccessFailedCount` 0 → 0 after three wrong-PIN over-limit
          mints, with 0 authorisations minted: the rung runs before the PIN is ever verified, so
          styling the boxes red would tell the user the lockout is closer when it provably is not.
          The arm above already makes this argument for its own case.

          AND DROP THE AUTHORISATION. On the SEND path the refused authorisation stays Pending
          server-side (measured A3.3) while `lastAuthorization.current` still holds one bound to the
          OLD amount — the amount that must change. `onValid`'s re-present guard
          (`keyLive && lastAuthorization.current`) is false today because a 422 is a key-DROP class
          (`shouldKeepKey`), but leaning on an invariant three files away to keep a stale reference
          harmless is exactly what the arm above refuses to do.

          RETURN TO THE AMOUNT. The amount is what the visitor has to change, and a PIN cannot
          help. The banner is 'input'-scoped, so it survives the move to the form. A 422 drops the
          key, but a resend under a retained key reaches this arm before the page has rendered
          without it, and `toForm()` refuses under a live key: the move waits for the effect on
          `returnToAmount`, which acts once `keyLive` is false.

          A MINT under a held key (the send before it answered AUTHORIZATION_EXPIRED) drops no key,
          so that effect would wait for ever and Back stays disabled. There the page does what the
          closed-account arm below does: `requireVerify` lets the key go and shows the check view.

          Clearing `enteredPin.current` is a DEAD-END decision, not a security one: the PIN never
          leaves the mint body either way, and every non-PIN refusal at this rung already retains it
          today. The amount and the payee are KEPT because WCAG 2.2 SC 3.3.7's exception covers
          security information only — the same argument the expiry arm carries.
        */
      setPin('');
      enteredPin.current = '';
      setPinNonce((n) => n + 1);
      lastAuthorization.current = null;
      setAuthorizationHeld(false);
      if (phase === 'mint' && keyLive) wizard.requireVerify();
      else setReturnToAmount(true);
    } else if (refusal?.errorCode === 'PIN_LOCKED') {
      setPin('');
      enteredPin.current = '';
      setPinLockDeadline(retryDeadline(refusal.retryAfterSeconds ?? DEFAULT_PIN_LOCK_SECONDS));
    } else if (refusal?.errorCode === 'ACCOUNT_NOT_FOUND') {
      // The source account is closed or gone, on the mint or on the send. A 404 is a refusal:
      // nothing moved, and the idempotency hook has dropped the key of a send. The page clears
      // the choice once it renders with this (`sourceGone`), without waiting for the list.
      setSourceRefused(true);
      /*
        Unless a key is held, which only a mint can meet: the send's is dropped on this very
        answer. A mint is made under a held key only after the send before it was answered
        AUTHORIZATION_EXPIRED or AUTHORIZATION_INVALID, the one answer that keeps the key and
        drops the authorisation. Under that key the PIN step can do nothing more: the transfer it
        is for names an account that is gone, and the page's clearing of it (`sourceGone`) waits for
        the key.

        So the page shows the check view, as the money dialogs do for an intent they cannot carry
        on: the key is let go, the view's two buttons are the ways on, and "start over" comes
        back to the form, where the choice is cleared and the sentence said. Letting the key go
        abandons no payment while the key's record lives, 24 hours (ADR-0009): the API gives that
        answer from inside the action, which it runs only for a key under which nothing was
        committed, and every authorisation presented under the key has been refused, so a send
        still on its way is refused too. Past that life the same answer can follow a send that
        was applied, which is why the view says to check the transactions first.
      */
      if (phase === 'mint' && keyLive) wizard.requireVerify();
    } else if (refusal?.errorCode === 'RECIPIENT_NO_ACCOUNT') {
      // A mint under a held key drops no key, so Back and Close would stay disabled with the PIN
      // step in front: the closed-account arm above says why and what the check view does. Its
      // sentence is already shown; "start over" clears it, so the effect on `payeeRefused` says
      // it again on the form. With no key held the answer is as it was.
      if (phase === 'mint' && keyLive) {
        setPin('');
        enteredPin.current = '';
        lastAuthorization.current = null;
        setAuthorizationHeld(false);
        setPayeeRefused(true);
        wizard.requireVerify();
      }
    } else if (refusal?.errorCode === 'PIN_REQUIRED') {
      // No PIN enrolled. Nothing on this page can fix that, so send them where it can be.
      // `requestLeave`, not a bare navigate: this page deliberately owns no destinations of
      // its own, and the wizard refuses any exit while an idempotency key is live. A 422 is
      // a key-DROP class, so this one goes through.
      requestLeave('/pin-setup?returnTo=/transfer');
    } else if (phase === 'mint' && keyLive && refusal !== null && isLimiterRefusal(refusal)) {
      /*
        A limiter refused the mint made under a held key: `RATE_LIMIT_EXCEEDED`, a demo copy past
        its limit, which refuses every mint after this one, or a 429 whose code cannot be read.
        The key keeps Back and Close disabled, and the full boxes send nothing until a digit
        changes. So the page does what the closed-account arm above does: the PIN copies go, and
        `requireVerify` lets the key go and shows the check view. Every authorisation presented
        under the key was refused, so nothing under it can commit: the view waits for no bound,
        and offers "start over" once the listing has answered. With no key held the exits are
        open, and the refusal is said on the PIN step.
      */
      setPin('');
      enteredPin.current = '';
      setPinNonce((n) => n + 1);
      wizard.requireVerify();
    } else if (
      phase === 'mint' &&
      refusal !== null &&
      (refusal.status === 'NETWORK' ||
        refusal.status === 'PARSE' ||
        (typeof refusal.status === 'number' && refusal.status >= 500))
    ) {
      /*
        The MINT got no usable answer: an outage (a 503, or no answer in 65 s, which is NETWORK), a
        broken connection, an unreadable answer, a 5xx. The PIN step then has no control at all —
        the sixth digit is the send, and it cannot fire again on boxes that stay full — so empty
        them and put the caret back in box 1: six digits are the retry.

        Only the mint. After a SEND failure the boxes stay as they are: the resend bar's control
        runs `onValid`, which returns early without six digits, so emptying them would leave that
        bar with a button that does nothing. And `lastAuthorization` is not touched: a failed mint
        never produced one to drop.

        No `setPinError`: nothing says the PIN was wrong. Focus is on `body` when this lands — every
        control on the step is disabled during the mint — so the refocus takes nothing from anyone.
      */
      setPin('');
      enteredPin.current = '';
      setPinNonce((n) => n + 1);
    }
  }

  // The completed value goes to the REF, which `onValid` reads before `pin` has re-rendered.
  const enter = (entered: string) => {
    enteredPin.current = entered;
  };

  // What the page lets go of when the account the PIN was typed for is gone.
  const dropPinAndAuthorization = () => {
    setPin('');
    enteredPin.current = '';
    lastAuthorization.current = null;
    setAuthorizationHeld(false);
  };

  return {
    pin,
    setPin,
    pinError,
    setPinError,
    pinNonce,
    setPinNonce,
    pinLockDeadline,
    setPinLockDeadline,
    pinHintId,
    pinErrorId,
    pinLockId,
    canResend,
    success,
    confirming,
    check,
    enter,
    dropPinAndAuthorization,
    onValid,
    onInvalid,
  };
}
