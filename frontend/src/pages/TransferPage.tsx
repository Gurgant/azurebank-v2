import { useEffect, useId, useRef, useState } from 'react';
import { useLocation } from 'react-router-dom';
import { Button, MessageBar, MessageBarBody, MessageBarActions } from '@fluentui/react-components';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import type { ApiProblem } from '../api/problemBaseQuery';
import { useTransferWizardStyles } from './transferWizardStyles';
import { TransferPinStep } from './TransferPinStep';
import { PIN_LENGTH, useTransferPin } from './transfer/useTransferPin';
import { useRecipientCheck } from './transfer/useRecipientCheck';
import { TransferFormStep } from './transfer/TransferFormStep';
import { TransferReviewStep } from './transfer/TransferReviewStep';
import { TransferReceipt } from './transfer/TransferReceipt';
import { ConfirmDialog } from '../components/shared/ConfirmDialog';
import { ResultUnknownView } from '../components/shared/ResultUnknownView';
import { WentThroughView } from '../components/shared/WentThroughView';
import { PageHeader } from '../components/layout/PageHeader';
import {
  useGetAccountsQuery,
  useAuthoriseTransferMutation,
  useTransferMutation,
  type AccountResponse,
} from '../features/api/apiSlice';
import { useFocusWhenLost } from '../hooks/useFocusWhenLost';
import { useMoneyWizard } from '../hooks/useMoneyWizard';
import { usePageRead } from '../hooks/usePageRead';
import { useSettledPress } from '../hooks/useSettledPress';
import { AlertSlot, ReadErrorBar, WaitHint } from '../components/feedback';
import {
  insufficientFundsMessage,
  normalizeAzureTag,
  parseAmountInput,
  transferFormSchema,
  type TransferFormOutput,
  type TransferFormValues,
} from '../forms/moneySchemas';
import { availableBalanceOf } from '../utils/availableBalance';
import { useFundsGate } from '../hooks/useFundsGate';
import {
  ACCOUNT_CLOSED,
  CHECK_AGAIN,
  NO_DOUBLE_CHARGE,
  TRANSFER_FOUND,
  TRANSFER_WENT_THROUGH,
  TRY_AGAIN,
} from '../api/problemMessages';

// ============================================
// CONSTANTS
// ============================================

const NO_ACCOUNT_SENTENCE =
  "That recipient can't receive money right now. Choose another recipient.";

/**
 * The account a navigation asked to start from: the Transfer button on an account's card puts
 * `{ fromAccountId }` in the history entry's state. `location.state` is an untrusted cast, so it
 * is parsed as the sign-in page parses its own, and any other shape is ignored. The id is a
 * request, not a choice: the page uses it only if it names one of the accounts it received.
 */
const transferNavStateSchema = z.object({ fromAccountId: z.string() });

function requestedSourceId(state: unknown): string | null {
  const parsed = transferNavStateSchema.safeParse(state);
  return parsed.success ? parsed.data.fromAccountId : null;
}

/**
 * A transfer to another user by handle, in three steps: the form, the review and the PIN.
 *
 * The form's fields (source account, recipient handle, amount) live in react-hook-form, with the
 * balance-capped `transferFormSchema` as resolver. The verified recipient is component state: the
 * exact-match lookup is the validator (ADR-0014), and a schema cannot hold the server's answer.
 *
 * The sixth PIN digit mints a one-shot authorisation and sends the transfer with it (ADR-0042):
 * the PIN goes to the mint, and the transfer carries the authorisation in the
 * `Step-Up-Authorization` header.
 */
export function TransferPage() {
  const styles = useTransferWizardStyles();

  const accountsQuery = useGetAccountsQuery();
  const { data: accountsData, error: accountsError, requestId: accountsRequestId } = accountsQuery;
  const accounts = accountsData ?? [];
  // Read once, as the page mounts: a later navigation to this page leaves the source the form
  // already names as it is.
  const location = useLocation();
  const [requestedSource] = useState(() => requestedSourceId(location.state));
  // The accounts bar follows the wait, as on the reading pages: a Retry keeps the old error while
  // it runs, so the bar goes away for the new wait and comes back, announced, only if it fails too.
  const accountsRead = usePageRead(accountsQuery, { endpoint: 'getAccounts', arg: undefined });
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

  const [transferTrigger] = useTransferMutation();
  // Before the wizard, which holds the browser's Back for the request for an authorisation.
  const [authoriseTransfer, { isLoading: isMinting }] = useAuthoriseTransferMutation();
  const wizard = useMoneyWizard(transferTrigger, {
    // This flow's OWN codes. Everything protocol-shaped — step-up, in-flight, key reuse, network —
    // is the wizard's, and the type of this table makes naming one of those here a compile error.
    // The two 404 codes live here rather than in the shared tail because their wording is
    // flow-specific. RECIPIENT_NOT_FOUND is the API's code for a handle no user holds.
    // ACCOUNT_NOT_FOUND reaches this page for the caller's own source account, closed or gone,
    // never for the payee: the page says the account is closed, and clears the choice (below).
    messages: {
      SELF_TRANSFER_NOT_ALLOWED: "You can't send money to yourself.",
      ACCOUNT_NOT_FOUND: ACCOUNT_CLOSED,
      RECIPIENT_NOT_FOUND: 'That recipient could not be found. Please re-check the handle.',
      RECIPIENT_NO_ACCOUNT: NO_ACCOUNT_SENTENCE,
    },
    fallback: 'Transfer failed. Please try again.',
    minting: isMinting,
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
  /*
    Every exit, held for BOTH phases of the submit.

    `keyLive` is `isSubmitting || keyRetained` (`useMoneyWizard`), and neither is set until
    `wizard.run` starts — so for the whole MINT round trip `keyLive` is false and every control it
    guards is live. The PIN input was taught the two-phase guard when the mint landed; the exits
    were not, so a user could press Back mid-mint, watch the wizard step backwards, and have the
    transfer complete underneath them.

    One name for the rule, used by the header and by the PIN step's own Back, so the two cannot
    answer differently. The browser's Back is not a control of the page: the wizard holds it for
    the mint (`minting`).
  */
  const exitLocked = keyLive || isMinting;

  const recipientCheck = useRecipientCheck(onBodyEdit);
  const { lookupState, recipient, recipientError, ownHandle } = recipientCheck;

  // The PIN hook takes these three setters as arguments and calls them (the page calls them too),
  // so the state is declared before the hook.
  const [returnToAmount, setReturnToAmount] = useState(false);
  const [sourceRefused, setSourceRefused] = useState(false);
  // The payee's refusal under a held key (the RECIPIENT_NO_ACCOUNT arm), said again on the form.
  const [payeeRefused, setPayeeRefused] = useState(false);
  const {
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
  } = useTransferPin({
    wizard,
    authoriseTransfer,
    isMinting,
    setReturnToAmount,
    setSourceRefused,
    setPayeeRefused,
  });

  /*
    The check view's own actions wait until what it draws has settled (`ResultUnknownView`). Its
    "start over" takes the view away and brings back the form with its values, and the second
    press of a double click there lands on the form: a quick amount, "Use max" or the account
    card under it would change the transfer the visitor goes on to review, with the form never
    seen. So the form's content and its actions wait once more as the view goes. Measured on the
    running stack at 360×800 and 320×568 without this wait, the second press 155 to 166 ms after
    the first: it landed on the amount field, which took focus, and with a second account listed
    on no control.
  */
  const settledPress = useSettledPress(verifyRequired, '', { afterLeaving: true });

  /*
    The receipt's title takes the focus the send lost: the PIN boxes are disabled for the send, so
    the receipt appears with focus on `body`, where a screen reader says nothing of it. Called up
    here, not in the receipt's branch, because the page returns that branch early.
  */
  const receiptTitle = useFocusWhenLost<HTMLParagraphElement>(!!success);

  // ===== RHF step-1 form =====
  // The balance bound tracks the SELECTED account; defaults resolve once accounts load.
  const [balanceBound, setBalanceBound] = useState(0);
  const { control, handleSubmit, setValue, setFocus, watch, formState, trigger } = useForm<
    TransferFormValues,
    unknown,
    TransferFormOutput
  >({
    resolver: zodResolver(transferFormSchema(balanceBound)),
    mode: 'onChange',
    defaultValues: { fromAccountId: '', recipientTag: '', amount: '' },
  });

  const focusAmountOnForm = useRef(false);
  useEffect(() => {
    // The amount input is unmounted on the PIN step: wait for the form and its RHF ref. The flag
    // is set together with a `toForm()` that cannot be refused, and spent once the form is the
    // step shown, so no later, unrelated return to the form moves focus.
    if (!focusAmountOnForm.current || wizard.step !== 'form') return;
    focusAmountOnForm.current = false;
    setFocus('amount');
  }, [wizard.step, setFocus]);

  /*
    The daily refusal asks to go back to the amount. A send under a retained key answers it while
    `run` is still in its `try`, and `toForm` refuses under a live key: the refusal handler's own
    `wizard` is the render the send began in, with a live key and no way to see it dropped. So the
    handler only asks, and this effect moves once a render says the key is gone.
  */
  useEffect(() => {
    if (!returnToAmount || keyLive) return;
    setReturnToAmount(false);
    focusAmountOnForm.current = true;
    wizard.toForm();
  }, [returnToAmount, keyLive, wizard]);

  const watchedAccountId = watch('fromAccountId');
  const watchedTag = watch('recipientTag');
  const amountNumber = parseAmountInput(watch('amount'));

  /*
    The source is the account the form names. The default stands in for it only while the form
    has never named one: the account the navigation asked for (`requestedSource`) when it is
    among the accounts received, else the primary, else the first. Once a choice has been
    cleared because its account is gone (`mustChooseSource`), nothing stands in, the requested
    account included, and the visitor chooses.
  */
  const [mustChooseSource, setMustChooseSource] = useState(false);
  const selectedAccount =
    accounts.find((a) => a.id === watchedAccountId) ??
    (watchedAccountId === '' && !mustChooseSource
      ? (accounts.find((a) => a.id === requestedSource) ??
        accounts.find((a) => a.isPrimary) ??
        accounts[0])
      : undefined) ??
    null;
  const availableBalance = selectedAccount ? availableBalanceOf(selectedAccount) : 0;

  /*
    The source the form names is gone: the accounts list, read again, came back without it, or
    the server answered ACCOUNT_NOT_FOUND for it (`sourceRefused`, set where that answer lands).
    A list that failed to load is not a list without the account: only one that was received
    counts, and a failed reload keeps the last one received.
  */
  const sourceGone =
    watchedAccountId !== '' &&
    (sourceRefused ||
      (accountsData !== undefined && !accountsData.some((a) => a.id === watchedAccountId)));

  // See `useFundsGate`: the Zod bound is built from a CACHED balance, and reaching the PIN step
  // with a stale one costs a PIN attempt on a request that cannot succeed.
  const confirmFunds = useFundsGate();
  const [checkingFunds, setCheckingFunds] = useState(false);

  // Write the default (the requested account, the primary or the first) into the form once
  // accounts load, and keep the schema's balance bound in lockstep with the selected account.
  useEffect(() => {
    if (selectedAccount && watchedAccountId !== selectedAccount.id) {
      setValue('fromAccountId', selectedAccount.id, { shouldValidate: true });
    }
  }, [selectedAccount, watchedAccountId, setValue]);
  // With no source the bound stays where it was: an amount is not judged against the balance
  // of no account, and "Exceeds available balance of €0.00" would be a second, untrue complaint.
  useEffect(() => {
    if (selectedAccount) setBalanceBound(availableBalance);
  }, [selectedAccount, availableBalance]);
  // Re-run amount validation once the bound (and thus the resolver) has actually updated:
  // mode 'onChange' only revalidates the field that changed, so an account switch alone
  // would leave the amount's cached validity (and canReview) on the PREVIOUS balance.
  useEffect(() => {
    void trigger('amount');
  }, [balanceBound, trigger]);

  /*
    NO SILENT SWITCH. When the source is gone the choice is cleared, the page goes back to the
    form and says that the account is closed, and nothing can be sent until the visitor chooses
    another: no default stands in, so money never leaves an account the visitor did not pick.
    The PIN typed for the account that is gone, and an authorisation minted for it, go with it.

    Not while a key is live or a mint is running: the answer still owed decides first, and
    `toForm` refuses under a live key anyway. Nor under the receipt or the check view, which
    this would change the page beneath. `wizard` is a new object on every render, so this runs
    after each one, and has nothing to do once it has run: the form no longer names a source.
  */
  useEffect(() => {
    if (!sourceGone || exitLocked || success || verifyRequired) return;
    setValue('fromAccountId', '', { shouldValidate: true });
    setMustChooseSource(true);
    setSourceRefused(false);
    dropPinAndAuthorization();
    wizard.toForm();
    wizard.fail(ACCOUNT_CLOSED);
  }, [sourceGone, exitLocked, success, verifyRequired, setValue, dropPinAndAuthorization, wizard]);

  // "Start over" clears the failure with the intent; the payee's refusal is said again, once, on
  // the form. The visitor's next change of recipient clears it like any input failure.
  useEffect(() => {
    if (!payeeRefused || exitLocked || success || verifyRequired) return;
    setPayeeRefused(false);
    wizard.fail(NO_ACCOUNT_SENTENCE);
  }, [payeeRefused, exitLocked, success, verifyRequired, wizard]);

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

  // The schema owns account/tag-format/amount validity; the VERIFIED recipient is the
  // extra, server-truth gate that Zod cannot own.
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

    The visitor's own handle is refused locally to spare a rate-limited lookup (ADR-0014).
    Its flag asks for another handle too, without relying on an answer from the server.

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
  // The API refused the handle's form, which no other word of the check says: the same press gets
  // the same refusal, so this too asks for another handle and not for "Verify".
  const handleRefused =
    recipientError !== null &&
    lookupState.isError &&
    (lookupState.error as ApiProblem | undefined)?.errorCode === 'VALIDATION_ERROR';
  const reviewWaitsFor = canReview
    ? null
    : !selectedAccount
      ? accounts.length > 0
        ? 'Choose an account to continue.'
        : 'You have no account to send from.'
      : !normalizeAzureTag(watchedTag)
        ? "Enter the recipient's @handle to continue."
        : !recipient
          ? lookupState.isFetching
            ? 'Checking the handle…'
            : ownHandle || nobodyHasTheHandle || handleRefused
              ? 'Change the handle to continue.'
              : 'Press Verify to check the handle.'
          : watch('amount').trim() === ''
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
      // The list just read has no such account. No PIN step for it: the page, which reads the
      // same list, clears the choice and says so (`sourceGone`).
      if (verdict.status === 'closed') return;
      // 'unknown' proceeds on purpose: a courtesy check must not become an outage. The server
      // still refuses: its `INSUFFICIENT_FUNDS` becomes the banner through `wizard.run`'s classifier
      // (`classifyMoneyProblem`, `api/moneyProblem.ts`). `onValid` only has the local check against
      // the cached balance (`useTransferPin.ts`).
      setPin('');
      setPinError(false);
      setPinNonce((n) => n + 1);
      wizard.toPin();
    } finally {
      setCheckingFunds(false);
    }
  };

  const sendReviewed = (data: TransferFormOutput) => onValid(data, selectedAccount, recipient);

  // ===== Success receipt =====
  if (success) {
    return (
      <TransferReceipt success={success} receiptTitle={receiptTitle} requestLeave={requestLeave} />
    );
  }

  /*
    ===== The listing found the movement the send wrote, by its key =====

    Before the went-through view, which `confirmWentThrough` sets with it: the same title and
    ways on, with the movement's amount, reference and time under the sentence.
  */
  if (check.movement) {
    return (
      <WentThroughView
        title="Transfer Complete"
        sentence={TRANSFER_FOUND}
        movement={check.movement}
        sign="-"
        onViewHistory={() => requestLeave('/history')}
        onDone={() => requestLeave('/dashboard')}
      />
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
        check={check}
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
        {/* Loading/error/empty are first-class states — the convention AccountsPage and
            DashboardPage already follow and both transfer wizards did not. Without this a failed
            accounts load left the page standing with empty pickers and no explanation, so a
            transient network fault silently blocked transfers. */}
        <WaitHint
          active={accountsHoldThePage && accountsRead.waiting}
          kind="read"
          failed={accountsRead.failed}
          onStopWaiting={accountsRead.stopWaiting}
        />
        {/* Keyed by the request that failed, so that a Retry which fails again before its wait is
            ever drawn still puts a new bar into the alert, and it is announced again. */}
        <AlertSlot>
          {accountsRead.problem && (
            <ReadErrorBar
              key={accountsRequestId}
              problem={accountsRead.problem}
              fallback="Could not load your accounts."
              onRetry={accountsRead.retry}
              retryRef={accountsRead.landingRef}
            />
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
                  BOTH phases, exactly as the boxes in `TransferPinStep`. Today this control cannot
                  actually reach the mint — `onValid` re-presents `lastAuthorization.current`
                  whenever a key is live, and a live key implies a send, which implies a mint that
                  already succeeded. The guard does not depend on that invariant on purpose: it is
                  one identifier, and the invariant is three files away from anyone editing this
                  button.
                */
                disabled={isMinting || isSubmitting}
                onClick={() => void handleSubmit(sendReviewed, onInvalid)()}
              >
                {!inFlight && nothingChanged ? TRY_AGAIN : CHECK_AGAIN}
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
          <TransferFormStep
            settledPress={settledPress}
            accounts={accounts}
            selectedAccount={selectedAccount}
            handleSelectAccount={handleSelectAccount}
            control={control}
            keyLive={keyLive}
            onBodyEdit={onBodyEdit}
            watchedTag={watchedTag}
            recipientCheck={recipientCheck}
            availableBalance={availableBalance}
            handleQuickAmount={handleQuickAmount}
            amountNumber={amountNumber}
            canReview={canReview}
            reviewWaitsFor={reviewWaitsFor}
            reviewHintId={reviewHintId}
            onReview={() => wizard.toReview()}
            requestLeave={requestLeave}
          />
        ) : step === 'pin' ? (
          <TransferPinStep
            verb="Sending"
            amountNumber={amountNumber}
            to={
              <>
                {recipient?.displayName} (@{recipient?.azureTag})
              </>
            }
            pinLength={PIN_LENGTH}
            pin={pin}
            pinNonce={pinNonce}
            pinError={pinError}
            pinLockDeadline={pinLockDeadline}
            error={error}
            pinHintId={pinHintId}
            pinErrorId={pinErrorId}
            pinLockId={pinLockId}
            isMinting={isMinting}
            isSubmitting={isSubmitting}
            keyLive={keyLive}
            exitLocked={exitLocked}
            confirming={confirming}
            onPinChange={(next) => {
              setPin(next);
              setPinError(false);
            }}
            // The completed value goes to the REF first: `onValid` runs before `pin` re-renders.
            onPinComplete={(entered) => {
              enter(entered);
              void handleSubmit(sendReviewed, onInvalid)();
            }}
            onLockElapsed={() => setPinLockDeadline(null)}
            onBack={() => wizard.toReview()}
          />
        ) : (
          <TransferReviewStep
            selectedAccount={selectedAccount}
            recipient={recipient}
            amountNumber={amountNumber}
            newBalance={newBalance}
            onReviewed={onReviewed}
            isSubmitting={isSubmitting}
            checkingFunds={checkingFunds}
            keyLive={keyLive}
            onBack={() => wizard.toForm()}
          />
        )}
      </div>

      {/* The browser's Back, held. Every other exit refuses while a key is live or the PIN is
          being checked; this one ASKS, because after a KEEP-class failure it is the only exit
          left (ADR-0028). It is held for the PIN check too, where leaving anyway sends
          nothing: the words, which say "if it did reach the bank", are cautious there and not
          untrue. */}
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
