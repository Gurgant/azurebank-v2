import { useEffect, useId, useRef, useState } from 'react';
import {
  makeStyles,
  Text,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
  MessageBarActions,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircle24Filled, ArrowSwap24Regular } from '@fluentui/react-icons';
import { Controller, useForm, type FieldErrors } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { colors } from '../theme/tokens';
import { isServiceOutage, type ApiProblem } from '../api/problemBaseQuery';
import { isProvenCommit, type MoneyPhase } from '../api/moneyProblem';
import { useTransferWizardStyles } from './transferWizardStyles';
import { ConfirmDialog } from '../components/shared/ConfirmDialog';
import { ResultUnknownView } from '../components/shared/ResultUnknownView';
import { WentThroughView } from '../components/shared/WentThroughView';
import { PageHeader } from '../components/layout/PageHeader';
import {
  useGetAccountsQuery,
  useLazyLookupRecipientQuery,
  useAuthoriseTransferMutation,
  useTransferMutation,
  type AccountResponse,
} from '../features/api/apiSlice';
import { useAppDispatch } from '../app/hooks';
import { useMoneyWizard } from '../hooks/useMoneyWizard';
import { abortRunning, useWaitLanding, type StartedRead } from '../hooks/useWaitLanding';
import { readWait } from '../hooks/useWaitPhase';
import { formatCurrency, formatLockHorizon, maskAccountNumber } from '../utils/format';
import { AlertSlot, RetryCountdown, WaitHint, retryDeadline } from '../components/feedback';
import {
  insufficientFundsMessage,
  normalizeAzureTag,
  parseAmountInput,
  transferFormSchema,
  type TransferFormOutput,
  type TransferFormValues,
} from '../forms/moneySchemas';
import { AmountField } from '../components/form/AmountField';
import { availableBalanceOf } from '../utils/availableBalance';
import { useFundsGate } from '../hooks/useFundsGate';
import { PinInput } from '../components/PinInput';
import {
  CONNECTION_FAILED,
  NO_DOUBLE_CHARGE,
  SERVICE_UNAVAILABLE,
  TRANSFER_WENT_THROUGH,
  TRY_AGAIN,
} from '../api/problemMessages';

// ============================================
// CONSTANTS
// ============================================

const QUICK_AMOUNTS = [10, 25, 50, 100, 250];

const PIN_LENGTH = 6;
const DEFAULT_PIN_LOCK_SECONDS = 15 * 60;

interface Recipient {
  azureTag: string;
  displayName: string;
}

interface SuccessData {
  amount: number;
  recipientName: string;
  recipientAzureTag: string;
  newBalance: number;
  transactionNumber: string;
  replayed: boolean;
}

function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  return (parts[0][0] + (parts[1]?.[0] ?? '')).toUpperCase();
}

// ============================================
// STYLES
// ============================================

/**
 * The keys that belong to the recipient step — the part of this flow internal transfer does not
 * have. Everything else comes from `useTransferWizardStyles`, which both wizards share.
 */
const useRecipientStyles = makeStyles({
  recipientRow: { display: 'flex', gap: '8px' },
  // The recipient check's hint, once it has words. While it is silent it takes no room at all.
  lookupHint: { marginTop: '8px' },
  input: {
    flex: 1,
    padding: '12px',
    borderRadius: '8px',
    border: `1px solid ${colors.neutral[300]}`,
    fontSize: '15px',
    fontFamily: 'inherit',
    color: colors.neutral[800],
    outline: 'none',
    ':focus': { border: `1px solid ${colors.brand[60]}` },
    ':disabled': { backgroundColor: colors.neutral[100] },
  },
  recipientCard: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '14px 16px',
    backgroundColor: colors.semantic.success.light,
    borderRadius: '12px',
  },
  avatar: {
    width: '40px',
    height: '40px',
    borderRadius: '50%',
    backgroundColor: colors.brandFill.rest,
    color: tokens.colorNeutralForegroundOnBrand,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '15px',
    fontWeight: 600,
    flexShrink: 0,
  },
  recipientName: { fontSize: '15px', fontWeight: 600, color: colors.neutral[800] },
  recipientTag: { fontSize: '13px', color: colors.neutral[500] },
  /*
    The line under "Review Transfer" that says what the button is still waiting for. One line
    tall whether it has words or not (`minHeight` is its `lineHeight`), so the link under it
    never moves when the words come, change or go. `display: block` because Fluent's `Text`
    renders inline whatever element it is asked for, and an inline box has no minimum height.
    Fluent's second foreground, as the words of a wait are (`WaitHint`): 9.13 to 1 on the light
    page and 12.68 on the dark one, measured. The palette's `neutral[500]`, which the page's
    other small lines take, is 4.39 to 1 on the light page: under the 4.5 words this small need.
  */
  reviewHint: {
    display: 'block',
    margin: 0,
    minHeight: '20px',
    fontSize: '13px',
    lineHeight: '20px',
    textAlign: 'center',
    color: tokens.colorNeutralForeground2,
  },
});

// ============================================
// COMPONENT
// ============================================

/**
 * PR-11 — the real external transfer (to another user's primary account by AzureTag), now on
 * RHF+Zod (the money-forms rewrite): step-1's fields (from-account, recipient handle, amount)
 * live in react-hook-form with the balance-capped `transferFormSchema` as resolver, while the
 * VERIFIED-recipient truth stays async component state (the exact-match lookup IS the
 * validator, ADR-0014 — a schema cannot own server truth).
 *
 * ADR-0041 changed where the PIN comes from. It used to be collected by the ROOT step-up modal:
 * the BFF answered 403 on a level-1 session, the base-query interceptor popped the modal, the
 * session elevated, and the same request replayed. That made the PIN a property of the SESSION —
 * one entry then covered every transfer for five minutes, and any caller reaching the API directly
 * skipped it entirely. The PIN is now collected HERE, as the last step before sending, and travels
 * in the request body for the API to verify. The review screen's promise that you confirm with your
 * PIN on the next step is, as of this change, true.
 */
export function TransferPage() {
  // Merged so the markup keeps addressing one `styles` object.
  const styles = { ...useTransferWizardStyles(), ...useRecipientStyles() };

  const {
    data: accounts = [],
    isLoading: accountsLoading,
    isFetching: accountsFetching,
    error: accountsError,
    refetch: refetchAccounts,
    requestId: accountsRequestId,
  } = useGetAccountsQuery();
  // The accounts bar follows the wait, as on the reading pages: a Retry keeps the old error while
  // it runs, so the bar goes away for the new wait and comes back, announced, only if it fails too.
  const accountsRead = readWait({
    isLoading: accountsLoading,
    isFetching: accountsFetching,
    error: accountsError,
  });
  const accountsProblem = accountsRead.failed ? (accountsError as ApiProblem) : undefined;
  /*
    With no accounts there is nothing to send from, so while they load, fail or load again the
    accounts ARE the page: the form waits for them. That also keeps their wait the only one on the
    page — no recipient check can start beside it.

    With accounts on the page, reading them again holds nobody up: the form stays usable, and the
    reload is not a wait the page shows. The funds check on Continue is such a reload, of this same
    entry, with a hint of its own; an accounts hint beside it would be a second hint, and its
    "Stop waiting" would stop the check and move on with it skipped.
  */
  const accountsHoldThePage =
    accounts.length === 0 && (accountsRead.waiting || accountsError !== undefined);
  const [lookup, lookupState] = useLazyLookupRecipientQuery();

  const dispatch = useAppDispatch();
  const accountsLanding = useWaitLanding<HTMLButtonElement>(
    accountsRead.waiting,
    accountsRequestId,
  );
  const stopWaitingForAccounts = () => {
    if (abortRunning(dispatch, [{ endpoint: 'getAccounts', arg: undefined }])) {
      accountsLanding.arm();
    }
  };
  const retryAccounts = () => {
    accountsLanding.arm();
    void refetchAccounts();
  };

  /*
    The recipient check the visitor is waiting on, kept so that "Stop waiting" can find it: a lazy
    query has no argument on the page to name it by. Stopping it lands on Verify, which is where the
    check starts again.
  */
  const runningLookup = useRef<StartedRead | null>(null);
  const lookupLanding = useWaitLanding<HTMLButtonElement>(
    lookupState.isFetching,
    lookupState.requestId,
  );
  const stopWaitingForLookup = () => {
    const running = runningLookup.current;
    if (running && abortRunning(dispatch, [running])) lookupLanding.arm();
  };
  const [transferTrigger] = useTransferMutation();
  const wizard = useMoneyWizard(transferTrigger, {
    // This flow's OWN codes. Everything protocol-shaped — step-up, in-flight, key reuse, network —
    // is the wizard's, and the type of this table makes naming one of those here a compile error.
    // ACCOUNT_NOT_FOUND lives here rather than in the shared tail because its wording is
    // flow-specific: "re-check the handle" is meaningless on a page with no handle.
    messages: {
      SELF_TRANSFER_NOT_ALLOWED: "You can't send money to yourself.",
      ACCOUNT_NOT_FOUND: 'That recipient could not be found. Please re-check the handle.',
    },
    fallback: 'Transfer failed. Please try again.',
  });
  // Destructured under the names the markup already used, so this change is confined to the
  // machine: not one element below moves.
  const {
    step,
    isSubmitting,
    inFlight,
    nothingChanged,
    error,
    verifyRequired,
    wentThrough,
    keyLive,
    onBodyEdit,
    requestLeave,
  } = wizard;

  // ===== PIN step (ADR-0041) =====
  const [authoriseTransfer, { isLoading: isMinting }] = useAuthoriseTransferMutation();

  /*
    Every exit, held for BOTH phases of the submit.

    `keyLive` is `isSubmitting || keyRetained` (useMoneyWizard.ts:142), and neither is set until
    `wizard.run` starts — so for the whole MINT round trip `keyLive` is false and every control it
    guards is live. The PIN input was taught the two-phase guard when the mint landed; the exits
    were not, so a user could press Back mid-mint, watch the wizard step backwards, and have the
    transfer complete underneath them.

    One name for the rule, used by the header and by the PIN step's own Back, so the two cannot
    answer differently.
  */
  const exitLocked = keyLive || isMinting;

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
  // uses, for the same reason. (Until 2026-09-17 the boxes here had no autoFocus, so the remount
  // refocused nothing and this comment described WithdrawDialog only.)
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

  const [recipient, setRecipient] = useState<Recipient | null>(null);
  const [recipientError, setRecipientError] = useState<string | null>(null);
  const [success, setSuccess] = useState<SuccessData | null>(null);

  // ===== RHF step-1 form =====
  // The balance bound tracks the SELECTED account; defaults resolve once accounts load.
  const [balanceBound, setBalanceBound] = useState(0);
  const { control, handleSubmit, setValue, watch, formState, trigger } = useForm<
    TransferFormValues,
    unknown,
    TransferFormOutput
  >({
    resolver: zodResolver(transferFormSchema(balanceBound)),
    mode: 'onChange',
    defaultValues: { fromAccountId: '', recipientTag: '', amount: '' },
  });

  const watchedAccountId = watch('fromAccountId');
  const watchedTag = watch('recipientTag');
  const amountNumber = parseAmountInput(watch('amount'));

  const selectedAccount =
    accounts.find((a) => a.id === watchedAccountId) ??
    accounts.find((a) => a.isPrimary) ??
    accounts[0] ??
    null;
  const availableBalance = selectedAccount ? availableBalanceOf(selectedAccount) : 0;

  // See `useFundsGate`: the Zod bound is built from a CACHED balance, and reaching the PIN step
  // with a stale one costs a PIN attempt on a request that cannot succeed.
  const confirmFunds = useFundsGate();
  const [checkingFunds, setCheckingFunds] = useState(false);

  /*
    The confirm — the mint, then the send — as the visitor sees it: one wait from the sixth digit,
    with one hint. Not `isMinting || isSubmitting`: if a render fell between the mint's answer and
    the send's start, the hint would take it for a new wait and count again from zero. Set only
    once nothing can return early before a request leaves, and cleared in a `finally`, so it never
    outlives the confirm.
  */
  const [confirming, setConfirming] = useState(false);

  // Auto-select the legacy default (primary ?? first) into the form once accounts load,
  // and keep the schema's balance bound in lockstep with the selected account.
  useEffect(() => {
    if (selectedAccount && watchedAccountId !== selectedAccount.id) {
      setValue('fromAccountId', selectedAccount.id, { shouldValidate: true });
    }
  }, [selectedAccount, watchedAccountId, setValue]);
  useEffect(() => {
    setBalanceBound(availableBalance);
  }, [availableBalance]);
  // Re-run amount validation once the bound (and thus the resolver) has actually updated:
  // mode 'onChange' only revalidates the field that changed, so an account switch alone
  // would leave the amount's cached validity (and canReview) on the PREVIOUS balance.
  useEffect(() => {
    void trigger('amount');
  }, [balanceBound, trigger]);

  const handleSelectAccount = (account: AccountResponse) => {
    if (keyLive) return;
    setValue('fromAccountId', account.id, { shouldValidate: true });
    onBodyEdit();
  };

  const handleQuickAmount = (value: number) => {
    if (keyLive) return;
    setValue('amount', value.toString(), { shouldValidate: true, shouldDirty: true });
    onBodyEdit();
  };

  const handleVerifyRecipient = async () => {
    const tag = normalizeAzureTag(watchedTag);
    if (!tag) return;
    setRecipient(null);
    setRecipientError(null);
    onBodyEdit();
    try {
      const request = lookup(tag);
      runningLookup.current = request;
      const result = await request.unwrap();
      if (result.exists) {
        setRecipient({ azureTag: result.azureTag, displayName: result.displayName });
      } else {
        setRecipientError(`We couldn't find @${tag}. Check the handle and try again.`);
      }
    } catch (caught) {
      /*
        A bare catch here told every failure the same story — "check your connection" — and this
        endpoint has a DEDICATED rate limiter: /api/users/* runs a tight per-user sliding window
        (ADR-0014) and rejects with 429 + Retry-After. So the one failure a user is most likely to
        provoke, by tapping Verify repeatedly, sent them to check a connection that was fine.

        Branching on STATUS rather than errorCode is deliberate: the BFF's rejection body is a bare
        ProblemDetails with no errorCode, so the client synthesises HTTP_429 and there is no code to
        match on. `retryAfterSeconds` is read from the Retry-After header the limiter always sets.
      */
      const problem = caught as ApiProblem;
      if (problem.status === 429) {
        setRecipientError(
          problem.retryAfterSeconds !== undefined
            ? `Too many lookups. Try again in ${formatLockHorizon(problem.retryAfterSeconds)}.`
            : 'Too many lookups. Please wait a moment and try again.',
        );
      } else if (isServiceOutage(problem)) {
        // Before the transport branch: a check with no answer in 65 s is `status: 'NETWORK'` too,
        // and "check your connection" would send the visitor after the wrong problem.
        setRecipientError(SERVICE_UNAVAILABLE);
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        setRecipientError(CONNECTION_FAILED);
      } else {
        setRecipientError(problem.detail || "We couldn't check that handle. Please try again.");
      }
    }
  };

  // The schema owns account/tag-format/amount validity; the VERIFIED recipient is the
  // extra, server-truth gate that Zod cannot own (D6).
  const canReview = formState.isValid && !!selectedAccount && !!recipient;
  /*
    What "Review Transfer" is still waiting for, while it cannot be pressed: the first of
    `canReview`'s conditions that is not met, taken in the order the page asks for them (an
    account, a handle, the handle checked, an amount). Said in one line under the button, which
    is described by it. Until 2026-10-06 the page said nothing, and a visitor who had typed a
    handle and an amount had no word that "Verify" was the step left.

    Each condition is read from what the page holds, not from the form's verdict: `isValid`
    covers the account, the handle and the amount in one flag, and arrives a render after the
    key press that changed it. The amount is asked of the form's own rule for it, now, against
    the balance as it stands. Measured in Chromium that day with the last sentence as the plain
    "everything else": typing the first digit of a good amount put "Change the amount to
    continue." in the page for the moment before the form agreed. No frame showed it, and it
    was still a sentence that was not true.

    So in that moment, with every condition this names met and the form a render behind, the
    line says nothing. A condition added to `canReview` and not named here leaves it saying
    nothing for good: name it here.

    While the check runs "Verify" is busy and cannot be pressed, so the line does not ask for it.

    A handle the check found nobody for is not sent back to "Verify": the same press gets the
    same answer, so the line asks for another handle, under the check's own sentence, which
    names the one it could not find. Until a second change of 2026-10-06 it went on saying
    "Press Verify to check the handle." there; seen in Chromium that day, right under "We
    couldn't find…". Three things together say the handle in the field is that one: the
    check's sentence is still up (a key press in the field takes it down), the check's last
    answer is that nobody has the handle, and that answer is the latest (the same handle
    checked again and turned away, as the limiter turns a check away, is a handle to check).
  */
  const reviewHintId = useId();
  const amountCanBeSent = transferFormSchema(availableBalance).shape.amount.safeParse(
    watch('amount'),
  ).success;
  const nobodyHasTheHandle =
    recipientError !== null && !lookupState.isError && lookupState.currentData?.exists === false;
  const reviewWaitsFor = canReview
    ? null
    : !selectedAccount
      ? 'You have no account to send from.'
      : !normalizeAzureTag(watchedTag)
        ? "Enter the recipient's @handle to continue."
        : !recipient
          ? lookupState.isFetching
            ? 'Checking the handle…'
            : nobodyHasTheHandle
              ? 'Change the handle to continue.'
              : 'Press Verify to check the handle.'
          : amountNumber <= 0
            ? 'Enter an amount to continue.'
            : !amountCanBeSent
              ? 'Change the amount to continue.'
              : null;
  const newBalance = availableBalance - amountNumber;

  /**
   * Review -> PIN, but only once the SERVER has confirmed the amount still fits.
   *
   * The form's validity was decided against a cached balance, possibly a page load ago. Asking the
   * server here — before the PIN screen exists — is what keeps a strong-authentication ceremony
   * from being spent on a transfer that is already known to fail, and what keeps a mistyped PIN on
   * such an attempt from counting toward the fifteen-minute lockout.
   */
  const onReviewed = async () => {
    if (!selectedAccount || !recipient) return;
    setCheckingFunds(true);
    try {
      const verdict = await confirmFunds(selectedAccount.id, amountNumber);
      if (verdict.status === 'insufficient') {
        // Back to the form, where the amount is editable — and `fail` is 'input'-scoped, so the
        // banner survives that transition rather than being cleared by it.
        wizard.toForm();
        wizard.fail(insufficientFundsMessage(verdict.available));
        return;
      }
      // 'unknown' proceeds on purpose: a courtesy check must not become an outage. The server
      // still refuses, and `INSUFFICIENT_FUNDS` is already handled below.
      setPin('');
      setPinError(false);
      setPinNonce((n) => n + 1);
      wizard.toPin();
    } finally {
      setCheckingFunds(false);
    }
  };

  /**
   * `handleSubmit`'s invalid branch. Without it, a form that went invalid while the PIN step was
   * open made Send do nothing at all — no request, no message. The errors arrive as an argument so
   * this reports what actually failed rather than guessing.
   */
  const onInvalid = (errors: FieldErrors<TransferFormValues>) => {
    wizard.toForm();
    wizard.fail(errors.amount?.message ?? 'Please check the details and try again.');
  };

  const onValid = async (data: TransferFormOutput) => {
    if (!selectedAccount || !recipient) return;
    if (enteredPin.current.length !== PIN_LENGTH || pinLockDeadline !== null) return;
    // Narrowed to a const BEFORE the await, and the receipt is built from that same const. The
    // review screen and the request therefore cannot name two different people.
    const confirmed = recipient;

    /*
      Last look before the request leaves. The funds gate took a round trip at Continue; this one
      is free — the freshest CACHED balance, which a mutation elsewhere in this tab may have moved
      since. An over-balance transfer is therefore not merely refused by the server: it is never
      sent.
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
        } catch (caught) {
          // Not a `run` failure, so the wizard has not classified it — hand it the same classifier.
          wizard.failFrom(caught as ApiProblem, { phase: 'mint' });
          handleRefusal(caught as ApiProblem, 'mint');
          return;
        }
      }

      const result = await wizard.run(
        {
          fromAccountId: data.fromAccountId,
          recipientAzureTag: confirmed.azureTag,
          amount: data.amount,
        },
        // A HEADER at the wire, never a body field: the server fingerprints the body alone.
        { stepUpAuthorizationId: authorizationId },
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
   * the two failed: the first arm is the send's alone, the last the mint's.
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
          The daily outgoing-transfer bound (ADR-0050). The expiry arm's exact shape, for three
          reasons of its own — and NO `setPinError`, NO navigation.

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

          NO NAVIGATION. SELF_TRANSFER_NOT_ALLOWED and RECIPIENT_NO_ACCOUNT already fall through this
          rung without moving the user, the banner is 'input'-scoped so it survives the two Backs,
          and `exitLocked` is false after a 422 so Back is live. WHERE the user should land is a flow
          decision that belongs with #165/#166.

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
    } else if (refusal?.errorCode === 'PIN_LOCKED') {
      setPin('');
      enteredPin.current = '';
      setPinLockDeadline(retryDeadline(refusal.retryAfterSeconds ?? DEFAULT_PIN_LOCK_SECONDS));
    } else if (refusal?.errorCode === 'PIN_REQUIRED') {
      // No PIN enrolled. Nothing on this page can fix that, so send them where it can be.
      // `requestLeave`, not a bare navigate: this page deliberately owns no destinations of
      // its own, and the wizard refuses any exit while an idempotency key is live. A 422 is
      // a key-DROP class, so this one goes through.
      requestLeave('/pin-setup?returnTo=/transfer');
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

  // ===== Success receipt =====
  if (success) {
    return (
      <div className={styles.page}>
        {/* No exits: the money has moved, so there is nothing to abandon, and the body's two
            buttons are the way on. The bare `<span />` on the left used to leave this title 40px
            off centre against the 40px spacer on the right. */}
        <PageHeader title="Transfer Complete" />
        <div className={styles.body}>
          <div className={styles.centeredView}>
            <div className={styles.successIcon}>
              <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
            </div>
            <Text className={styles.successTitle}>Transfer Sent!</Text>
            <Text className={styles.successAmount}>-{formatCurrency(success.amount)}</Text>
            {success.replayed && (
              <MessageBar intent="info" role="status">
                <MessageBarBody>
                  This transfer was already processed — showing the existing result.
                </MessageBarBody>
              </MessageBar>
            )}
            <div className={styles.reviewCard} style={{ width: '100%' }}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>
                  {success.recipientName} (@{success.recipientAzureTag})
                </Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Reference</Text>
                <Text className={styles.reviewValue}>{success.transactionNumber}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>New balance</Text>
                <Text className={styles.reviewValue}>{formatCurrency(success.newBalance)}</Text>
              </div>
            </div>
          </div>
          <div className={styles.actions}>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => requestLeave('/history')}
            >
              View History
            </Button>
            <Button
              appearance="secondary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => requestLeave('/dashboard')}
            >
              Done
            </Button>
          </div>
        </div>
      </div>
    );
  }

  /*
    ===== The server said it went through, and could not return the receipt =====

    Before the check view, and the order matters: `wentThrough` is set together with
    `verifyRequired`, so tested second it would never be reached, and the page would offer "It
    didn't go through" over a transfer the server has called committed. The receipt's title and
    its two ways on; `requestLeave` goes through because that answer dropped the key.
  */
  if (wentThrough) {
    return (
      <WentThroughView
        title="Transfer Complete"
        sentence={TRANSFER_WENT_THROUGH}
        onViewHistory={() => requestLeave('/history')}
        onDone={() => requestLeave('/dashboard')}
      />
    );
  }

  // ===== RESULT_UNKNOWN verify view =====
  if (verifyRequired) {
    return (
      <ResultUnknownView
        title="Send Money"
        repeatWarning="send twice"
        onCheckTransactions={() => requestLeave('/history')}
        onStartOver={wizard.startOver}
      />
    );
  }

  return (
    <div className={styles.page}>
      {/* THE GUARDED HEADER. `keyLive` now comes from the wizard, but the rule is unchanged and the
          guard is doubled rather than moved: `disabled={keyLive}` still bars the control, and
          `requestLeave` / `toForm` re-check it themselves, so deleting one JSX attribute no longer
          opens the exit. PageHeader takes handlers and calls them; it never decides a destination,
          which is why the shared bar cannot quietly delete the guard — and the wizard likewise
          takes a destination and refuses it, rather than choosing one. */}
      <PageHeader
        title={
          step === 'pin' ? 'Confirm with PIN' : step === 'review' ? 'Review Transfer' : 'Send Money'
        }
        onBack={() =>
          step === 'pin'
            ? wizard.toReview()
            : step === 'review'
              ? wizard.toForm()
              : requestLeave('/dashboard')
        }
        backDisabled={exitLocked}
        onClose={() => requestLeave('/dashboard')}
        closeDisabled={exitLocked}
      />

      <div className={styles.body}>
        {/* Loading/error/empty are first-class states (D22) — the convention AccountsPage and
            DashboardPage already follow and both transfer wizards did not. Without this a failed
            accounts load left the page standing with empty pickers and no explanation, so a
            transient network fault silently blocked transfers. */}
        <WaitHint
          active={accountsHoldThePage && accountsRead.waiting}
          kind="read"
          failed={accountsRead.failed}
          onStopWaiting={stopWaitingForAccounts}
        />
        {/* Keyed by the request that failed, so that a Retry which fails again before its wait is
            ever drawn still puts a new bar into the alert, and it is announced again. */}
        <AlertSlot>
          {accountsProblem && (
            <MessageBar key={accountsRequestId} intent="error">
              <MessageBarBody>
                {accountsProblem.detail || 'Could not load your accounts.'}
                {accountsProblem.traceId ? ` Support code: ${accountsProblem.traceId}` : ''}
              </MessageBarBody>
              <MessageBarActions>
                <Button
                  ref={accountsLanding.landingRef}
                  appearance="transparent"
                  onClick={retryAccounts}
                >
                  Retry
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}
        </AlertSlot>
        {error && (
          <MessageBar id={pinErrorId} intent="error" role="alert">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
        {canResend && (
          /*
            The retained-key state, and the ONLY place a deliberate re-send control belongs.

            Removing the Send button left this state with no way out: the page does not clear the
            PIN on IN_FLIGHT (only PIN-specific refusals clear it), so the boxes stay full,
            `onComplete` cannot fire again because the value never changes, and the banner asked the
            user to tap a control that no longer existed. Found by audit, not by a test — every test
            that reaches this state used to click Send.

            This re-sends the SAME key, the SAME body and the SAME authorisation. It is a check, not
            a second payment, which is why it is worded as one — and the wording splits three ways,
            because the ways to get here are not equally knowable. A 409 IN_FLIGHT means the server
            told us it is working on it. A 503 with `applied: false` (`nothingChanged`) means the
            API told us nothing happened, so the control is a plain "Try again", and the bar never
            says "may or may not" under an alert saying nothing was changed. Anything else — a lost
            response, a 5xx — means we do not know whether anything happened, and saying "still
            processing" there would assert something nobody has been told.

            Each form ends with the promise that retrying is safe, and this is the one place it is
            true: the only control that re-sends the same key. During the wait nothing on the page
            can, and a reload would send a new key, so the promise is never made there.
          */
          <MessageBar intent="info" role="status">
            <MessageBarBody>
              {inFlight
                ? `Still processing — check again to see whether it went through. ${NO_DOUBLE_CHARGE}`
                : nothingChanged
                  ? NO_DOUBLE_CHARGE
                  : `We couldn't reach the bank. Your transfer may or may not have gone through — check again. ${NO_DOUBLE_CHARGE}`}
            </MessageBarBody>
            <MessageBarActions>
              <Button
                appearance="primary"
                size="small"
                /*
                  BOTH phases, exactly as the PIN input above. Today this control cannot actually
                  reach the mint — `onValid` re-presents `lastAuthorization.current` whenever a key
                  is live, and a live key implies a send, which implies a mint that already
                  succeeded. The guard does not depend on that invariant on purpose: it is one
                  identifier, and the invariant is three files away from anyone editing this button.
                */
                disabled={isMinting || isSubmitting}
                onClick={() => void handleSubmit(onValid, onInvalid)()}
              >
                {!inFlight && nothingChanged ? TRY_AGAIN : 'Check again'}
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}

        {/* Nothing to pick from, so nothing to fill in. AccountsPage takes the same line — it
            hides its grid while a problem is showing — and without this the amount field stays
            editable over an empty account list, stacking "Available: €0.00" and "Exceeds available
            balance of €0.00" underneath the real error.

            Scoped to `accounts.length === 0` rather than to the problem alone, because RTK Query
            keeps the last data when a REFETCH fails: in that case the picker still has real
            accounts, and blanking a half-filled money form would be the worse bug.

            Nor while they first load, or while a Retry runs (when only the bar goes away): an
            empty form under the accounts' wait would show an available balance of zero, and would
            let a recipient check start beside it. */}
        {accountsHoldThePage ? null : step === 'form' ? (
          <>
            {/* From account */}
            <div>
              <Text className={styles.sectionLabel}>From</Text>
              <div
                style={{ display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '8px' }}
              >
                {accounts.map((account) => (
                  <button
                    key={account.id}
                    className={`${styles.card} ${
                      selectedAccount?.id === account.id ? styles.cardSelected : ''
                    }`}
                    // Drift, not a design choice: the internal wizard's identical cards have
                    // carried both of these since it was written, so a screen-reader user picking a
                    // source account heard "pressed" on one page and an unnamed button with no
                    // selected state on the other. `aria-pressed` is what makes the SELECTION
                    // audible at all — the blue border says it to sighted users only.
                    //
                    // `aria-label` REPLACES the contents-derived name, so this DOES drop the masked
                    // number and the balance from the announcement. A review proposed folding both
                    // into the label; measured, that breaks six assertions — including pre-existing
                    // ones on the internal page, which queries its cards by exact accessible name —
                    // and makes every card verbose. Declined, because the balance that governs the
                    // transfer is already announced as "Available: …" beside the amount field on
                    // both pages, and the number is masked. The short form also keeps the two
                    // wizards identical, which is the point of this PR.
                    aria-label={`From ${account.name}`}
                    aria-pressed={selectedAccount?.id === account.id}
                    onClick={() => handleSelectAccount(account)}
                  >
                    <div className={styles.accountInfo}>
                      <Text className={styles.accountName}>{account.name}</Text>
                      <Text className={styles.accountNumber}>
                        {maskAccountNumber(account.accountNumber)}
                      </Text>
                    </div>
                    <Text className={styles.accountBalance}>{formatCurrency(account.balance)}</Text>
                  </button>
                ))}
              </div>
            </div>

            {/* Recipient */}
            <div>
              <Text className={styles.sectionLabel}>To (recipient&apos;s @handle)</Text>
              <div className={styles.recipientRow} style={{ marginTop: '8px' }}>
                <Controller
                  control={control}
                  name="recipientTag"
                  render={({ field }) => (
                    <input
                      className={styles.input}
                      placeholder="@handle"
                      aria-label="Recipient handle"
                      ref={field.ref}
                      name={field.name}
                      value={field.value}
                      disabled={keyLive}
                      onBlur={field.onBlur}
                      onChange={(e) => {
                        field.onChange(e.target.value);
                        setRecipient(null);
                        setRecipientError(null);
                        onBodyEdit();
                      }}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') void handleVerifyRecipient();
                      }}
                    />
                  )}
                />
                <Button
                  ref={lookupLanding.landingRef}
                  appearance="secondary"
                  onClick={() => void handleVerifyRecipient()}
                  disabled={!watchedTag.trim() || lookupState.isFetching}
                >
                  {lookupState.isFetching ? <Spinner size="tiny" /> : 'Verify'}
                </Button>
              </div>
              {recipient && (
                <div className={styles.recipientCard} style={{ marginTop: '10px' }}>
                  <div className={styles.avatar}>{initials(recipient.displayName)}</div>
                  <div>
                    <Text className={styles.recipientName}>{recipient.displayName}</Text>
                    <br />
                    <Text className={styles.recipientTag}>@{recipient.azureTag}</Text>
                  </div>
                </div>
              )}
              {/* The check's own alert, there and empty before it runs, as on the read pages: its
                  line comes and goes inside it. Verify empties it, so each failure is a change. */}
              <AlertSlot>
                {recipientError && (
                  <Text className={styles.hint} style={{ marginTop: '8px', display: 'block' }}>
                    {recipientError}
                  </Text>
                )}
              </AlertSlot>
              {/* A handle that matches nobody is an answer, not an error, but for the visitor it
                  is the check failing: the alert above says so, and "Loaded." would contradict it. */}
              <WaitHint
                active={lookupState.isFetching}
                kind="read"
                failed={lookupState.isError || lookupState.currentData?.exists === false}
                onStopWaiting={stopWaitingForLookup}
                className={styles.lookupHint}
              />
            </div>

            {/* Amount */}
            <div className={styles.amountSection}>
              <Text className={styles.subtle}>Amount</Text>
              <AmountField
                control={control}
                name="amount"
                ariaLabel="Transfer amount"
                disabled={keyLive}
                onBodyEdit={onBodyEdit}
                classNames={{
                  wrapper: styles.amountWrapper,
                  currency: styles.amountCurrency,
                  input: styles.amountInput,
                  hint: styles.hint,
                  invalid: styles.amountInvalid,
                }}
                belowSlot={
                  <div className={styles.availableRow}>
                    <Text className={styles.subtle}>
                      Available: {formatCurrency(availableBalance)}
                    </Text>
                    {/* The constructive half of the balance cap: reaching the maximum without
                        retyping it is what stops the over-balance typo at the source. */}
                    <button
                      type="button"
                      className={styles.useMaxBtn}
                      onClick={() => handleQuickAmount(availableBalance)}
                      disabled={keyLive || availableBalance <= 0}
                      aria-label={`Use maximum, ${formatCurrency(availableBalance)}`}
                    >
                      Use max
                    </button>
                  </div>
                }
              />
            </div>
            <div className={styles.quickAmounts}>
              {QUICK_AMOUNTS.map((quickAmount) => (
                <button
                  key={quickAmount}
                  className={`${styles.quickBtn} ${
                    amountNumber === quickAmount ? styles.quickBtnSelected : ''
                  }`}
                  onClick={() => handleQuickAmount(quickAmount)}
                  disabled={quickAmount > availableBalance}
                >
                  €{quickAmount}
                </button>
              ))}
            </div>

            <div className={styles.actions}>
              <Button
                appearance="primary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => wizard.toReview()}
                disabled={!canReview}
                aria-describedby={reviewWaitsFor ? reviewHintId : undefined}
              >
                Review Transfer
              </Button>
              {/* Always on the page, and empty once the button can be pressed: its room is kept,
                  so nothing under it moves. Words to read and the button's description, not a
                  region that speaks by itself: at an amount the bank cannot send, the field's
                  own alert speaks at the same key press, and the two would be said together. */}
              <Text as="p" id={reviewHintId} className={styles.reviewHint}>
                {reviewWaitsFor}
              </Text>
              <button className={styles.linkBtn} onClick={() => requestLeave('/transfer/internal')}>
                <ArrowSwap24Regular style={{ width: '18px', height: '18px' }} />
                Between your own accounts
              </button>
            </div>
          </>
        ) : step === 'pin' ? (
          <>
            {/* PIN — ADR-0041. The credential travels in THIS request; the API verifies it. */}
            <div className={styles.reviewCard}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Sending</Text>
                <Text className={styles.reviewValue}>{formatCurrency(amountNumber)}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>
                  {recipient?.displayName} (@{recipient?.azureTag})
                </Text>
              </div>
            </div>
            {/* Says what the missing button used to: with no Send control, the behaviour has to be
                discoverable BEFORE the last digit, not discovered by it. */}
            <Text id={pinHintId} style={{ textAlign: 'center' }}>
              Enter your 6-digit PIN. The transfer sends as soon as the last digit is in.
            </Text>
            <PinInput
              key={pinNonce}
              length={PIN_LENGTH}
              value={pin}
              onChange={(next) => {
                setPin(next);
                setPinError(false);
              }}
              /*
                The sixth digit sends (ADR-0042). Not a new pattern: `PinInput` documents
                `onComplete` as "an Enter-less submit affordance" and `StepUpModal` has wired it
                since PR-10 — this adopts what already shipped rather than inventing a second way
                to confirm. Review has already discharged WCAG 2.2 SC 3.3.4 (Level AA: "reviewing,
                confirming, and correcting"), so a second confirm buys nothing but a click.

                The completed value goes to the REF first: `onValid` runs before `pin` re-renders.
              */
              onComplete={(entered) => {
                enteredPin.current = entered;
                void handleSubmit(onValid, onInvalid)();
              }}
              // `isMinting` as well as `isSubmitting`: the submit has TWO phases now, and
              // `isSubmitting` only covers the second. Without this the boxes stay live for the
              // whole mint round trip — a window in which a second completion starts a second
              // mint AND a second send.
              disabled={isMinting || isSubmitting || pinLockDeadline !== null}
              error={pinError}
              // Named for what it asks and described by the instruction and by whatever refused
              // the last attempt, as WithdrawDialog's boxes are; autoFocus is what lets the
              // `pinNonce` remount put the caret back in box 1 for a retry.
              autoFocus
              ariaLabel="Enter your PIN"
              ariaDescribedBy={[
                pinHintId,
                error ? pinErrorId : null,
                pinLockDeadline !== null ? pinLockId : null,
              ]
                .filter(Boolean)
                .join(' ')}
            />
            {pinLockDeadline !== null && (
              <>
                <MessageBar id={pinLockId} intent="error" role="alert">
                  <MessageBarBody>Too many incorrect PIN attempts.</MessageBarBody>
                </MessageBar>
                {/* Sibling, not child: role="alert" implies aria-atomic, so a nested timer would
                    re-announce the whole banner every second. It carries its own polite region. */}
                <RetryCountdown
                  deadline={pinLockDeadline}
                  onElapsed={() => setPinLockDeadline(null)}
                />
              </>
            )}
            {/* No Send button: the sixth digit is the send. A spinner still has to exist, or the
                one thing the user cannot see is the request they just started. */}
            {isSubmitting && (
              <div style={{ display: 'flex', justifyContent: 'center' }}>
                <Spinner size="tiny" label={`Sending ${formatCurrency(amountNumber)}`} />
              </div>
            )}
            {/* Under the boxes and the spinner, and outside the boxes' described-by targets. It
                promises nothing: during the wait no control here can re-send the same key. From
                20 s it asks the visitor to keep the page open, since a reload would send the
                transfer again with a new key — once there is a key: while the PIN alone is being
                checked none exists yet, nothing can be sent twice, and it says only that it is
                still trying. The kind changes the words, never the wait's clock. */}
            <WaitHint active={confirming} kind={isMinting && !keyLive ? 'write' : 'moneySend'} />
            <div className={styles.actions}>
              <Button
                appearance="secondary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => wizard.toReview()}
                disabled={exitLocked}
              >
                Back
              </Button>
            </div>
          </>
        ) : (
          <>
            {/* Review */}
            <div className={styles.reviewCard}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>From</Text>
                <Text className={styles.reviewValue}>{selectedAccount?.name}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>
                  {recipient?.displayName} (@{recipient?.azureTag})
                </Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Amount</Text>
                <Text className={styles.reviewValue}>{formatCurrency(amountNumber)}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>New balance</Text>
                <Text className={styles.reviewValue}>{formatCurrency(newBalance)}</Text>
              </div>
            </div>
            <Text className={styles.subtle} style={{ textAlign: 'center' }}>
              You&apos;ll confirm with your PIN on the next step.
            </Text>
            <div className={styles.actions}>
              <Button
                appearance="primary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => void onReviewed()}
                disabled={isSubmitting || checkingFunds}
              >
                {checkingFunds ? <Spinner size="tiny" /> : 'Continue'}
              </Button>
              <Button
                appearance="secondary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => wizard.toForm()}
                disabled={keyLive}
              >
                Back
              </Button>
            </div>
            {/* The funds check is a read, but it offers no Stop: it fails open, so a stopped check
                would only move on to the PIN step with the check skipped. */}
            <WaitHint active={checkingFunds} kind="read" />
          </>
        )}
      </div>

      {/* The browser's Back, held. Every other exit refuses while a key is live; this one ASKS,
          because after a KEEP-class failure it is the only exit left (ADR-0028). */}
      <ConfirmDialog
        isOpen={wizard.exitPrompt !== null}
        onClose={() => wizard.exitPrompt?.stay()}
        onConfirm={() => wizard.exitPrompt?.leave()}
        title="Leave without finishing?"
        message="This transfer has not been confirmed. If it did reach the bank, leaving now means you will not see the result here — check your history before sending it again."
        confirmText="Leave anyway"
        cancelText="Stay on this page"
      />
    </div>
  );
}
