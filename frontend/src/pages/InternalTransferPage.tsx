import { useEffect, useRef, useState } from 'react';
import {
  Text,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
  MessageBarActions,
} from '@fluentui/react-components';
import { CheckmarkCircle24Filled, ArrowSwap24Regular } from '@fluentui/react-icons';
import { useForm, type FieldErrors } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import type { ApiProblem } from '../api/problemBaseQuery';
import { useTransferWizardStyles } from './transferWizardStyles';
import { ConfirmDialog } from '../components/shared/ConfirmDialog';
import { ResultUnknownView } from '../components/shared/ResultUnknownView';
import { PageHeader } from '../components/layout/PageHeader';
import {
  useGetAccountsQuery,
  useAuthoriseInternalTransferMutation,
  useTransferInternalMutation,
  type AccountResponse,
} from '../features/api/apiSlice';
import { useMoneyWizard } from '../hooks/useMoneyWizard';
import { formatCurrency, maskAccountNumber } from '../utils/format';
import { RetryCountdown, retryDeadline } from '../components/feedback';
import {
  insufficientFundsMessage,
  internalTransferFormSchema,
  parseAmountInput,
  type InternalTransferFormOutput,
  type InternalTransferFormValues,
} from '../forms/moneySchemas';
import { AmountField } from '../components/form/AmountField';
import { availableBalanceOf } from '../utils/availableBalance';
import { useFundsGate } from '../hooks/useFundsGate';
import { PinInput } from '../components/PinInput';

const QUICK_AMOUNTS = [10, 25, 50, 100, 250];

interface SuccessData {
  amount: number;
  fromName: string;
  toName: string;
  fromNewBalance: number;
  transactionNumber: string;
  replayed: boolean;
}

/**
 * PR-11b — move money between the caller's OWN accounts, now on RHF+Zod (the money-forms
 * rewrite): source, destination and amount live in react-hook-form with
 * `internalTransferFormSchema` as resolver — including the ONE genuinely local cross-field
 * rule (source ≠ destination) as a Zod superRefine. Rides the same step-up interceptor as
 * the external transfer (level-2 gated → the root StepUpModal pops on Send, invisible to
 * this page) and the same idempotency spine + keyLive money-safety guards.
 */
const PIN_LENGTH = 6;
const DEFAULT_PIN_LOCK_SECONDS = 15 * 60;

export function InternalTransferPage() {
  const styles = useTransferWizardStyles();

  const {
    data: accounts = [],
    isSuccess: accountsLoaded,
    error: accountsError,
    refetch: refetchAccounts,
  } = useGetAccountsQuery();
  const accountsProblem = accountsError as ApiProblem | undefined;
  const [transferTrigger] = useTransferInternalMutation();
  const wizard = useMoneyWizard(transferTrigger, {
    // This flow's OWN codes; the protocol's are the wizard's, and the table's type makes naming one
    // of those here a compile error. SAME_ACCOUNT_TRANSFER is kept although it is UNREACHABLE over
    // HTTP — the validator rejects from == to during model binding, so the wire answer is a 400 with
    // an errors dictionary (measured against the running API in U6.1). It costs one line and covers
    // any caller that reaches the service without that validator.
    messages: {
      SAME_ACCOUNT_TRANSFER: 'Choose two different accounts.',
      ACCOUNT_NOT_FOUND: 'One of the accounts could not be found. Please re-check.',
    },
    fallback: 'Transfer failed. Please try again.',
  });
  // Destructured under the names the markup already used, so this change is confined to the machine.
  const { step, isSubmitting, inFlight, error, verifyRequired, keyLive, onBodyEdit, requestLeave } =
    wizard;

  // ===== PIN step (ADR-0041) — same shape as TransferPage and WithdrawDialog. =====
  const [authoriseInternalTransfer, { isLoading: isMinting }] =
    useAuthoriseInternalTransferMutation();

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
    The PIN the sixth digit just completed. A REF for the reason TransferPage gives: `onComplete`
    fires from inside `onChange`, so the state is one render behind when the submit begins.
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

  const [pin, setPin] = useState('');
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

  const [success, setSuccess] = useState<SuccessData | null>(null);

  // ===== RHF form (source / destination / amount) =====
  const [balanceBound, setBalanceBound] = useState(0);
  const { control, handleSubmit, setValue, watch, formState, trigger } = useForm<
    InternalTransferFormValues,
    unknown,
    InternalTransferFormOutput
  >({
    resolver: zodResolver(internalTransferFormSchema(balanceBound)),
    mode: 'onChange',
    defaultValues: { fromAccountId: '', toAccountId: '', amount: '' },
  });

  const watchedFromId = watch('fromAccountId');
  const watchedToId = watch('toAccountId');
  const amountNumber = parseAmountInput(watch('amount'));

  const fromAccount =
    accounts.find((a) => a.id === watchedFromId) ??
    accounts.find((a) => a.isPrimary) ??
    accounts[0] ??
    null;
  const toAccount = accounts.find((a) => a.id === watchedToId) ?? null;
  const availableBalance = fromAccount ? availableBalanceOf(fromAccount) : 0;

  // See `useFundsGate`: the Zod bound is built from a CACHED balance, and reaching the PIN step
  // with a stale one costs a PIN attempt on a request that cannot succeed.
  const confirmFunds = useFundsGate();
  const [checkingFunds, setCheckingFunds] = useState(false);

  // Auto-select the legacy default source (primary ?? first) once accounts load, and keep
  // the schema's balance bound in lockstep with the source account.
  useEffect(() => {
    if (fromAccount && watchedFromId !== fromAccount.id) {
      setValue('fromAccountId', fromAccount.id, { shouldValidate: true });
    }
  }, [fromAccount, watchedFromId, setValue]);
  useEffect(() => {
    setBalanceBound(availableBalance);
  }, [availableBalance]);
  // Re-run amount validation once the bound has actually updated (see TransferPage —
  // an account switch alone must not leave canReview on the previous balance).
  useEffect(() => {
    void trigger('amount');
  }, [balanceBound, trigger]);

  const selectFrom = (account: AccountResponse) => {
    if (keyLive) return;
    setValue('fromAccountId', account.id, { shouldValidate: true });
    if (account.id === watchedToId) {
      // Can't send to the same account — the destination resets like the legacy handler.
      setValue('toAccountId', '', { shouldValidate: true });
    }
    onBodyEdit();
  };

  const selectTo = (account: AccountResponse) => {
    if (keyLive) return;
    if (account.id === fromAccount?.id) return; // same-account is not selectable
    setValue('toAccountId', account.id, { shouldValidate: true });
    onBodyEdit();
  };

  const handleQuickAmount = (value: number) => {
    if (keyLive) return;
    setValue('amount', value.toString(), { shouldValidate: true, shouldDirty: true });
    onBodyEdit();
  };

  // The schema owns everything here (ids present, from ≠ to via superRefine, amount
  // bounds); the derived accounts are the belt-and-suspenders the legacy code kept.
  const canReview =
    formState.isValid && !!fromAccount && !!toAccount && fromAccount.id !== toAccount.id;
  const fromNewBalance = availableBalance - amountNumber;

  /**
   * Review -> PIN, but only once the SERVER has confirmed the amount still fits.
   *
   * Same reasoning as the external transfer: the form's validity was decided against a cached
   * balance, and reaching the PIN screen on a stale one spends an attempt from a three-strike
   * budget on a transfer that could never have completed.
   */
  const onReviewed = async () => {
    if (!fromAccount || !toAccount || fromAccount.id === toAccount.id) return;
    setCheckingFunds(true);
    try {
      const verdict = await confirmFunds(fromAccount.id, amountNumber);
      if (verdict.status === 'insufficient') {
        wizard.toForm();
        wizard.fail(insufficientFundsMessage(verdict.available));
        return;
      }
      // 'unknown' proceeds on purpose — see useFundsGate. The server is still the control.
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
   * open made Send do nothing at all — no request, no message.
   */
  const onInvalid = (errors: FieldErrors<InternalTransferFormValues>) => {
    wizard.toForm();
    wizard.fail(errors.amount?.message ?? 'Please check the details and try again.');
  };

  const onValid = async (data: InternalTransferFormOutput) => {
    // Mirror canReview exactly (incl. from !== to) — belt-and-suspenders against a fromAccount
    // fallback converging on toAccount if the accounts list ever changed under the review step.
    // This client guard is the REAL defence: SAME_ACCOUNT_TRANSFER never reaches the wire.
    if (!fromAccount || !toAccount || fromAccount.id === toAccount.id) return;
    if (enteredPin.current.length !== PIN_LENGTH || pinLockDeadline !== null) return;
    // Narrowed to consts BEFORE the await, and the receipt is built from those same consts, so the
    // review screen and the receipt cannot name two different accounts.
    const from = fromAccount;
    const to = toAccount;

    /*
      Last look before the request leaves. The funds gate took a round trip at Continue; this one
      is free — the freshest CACHED balance, which a mutation elsewhere in this tab may have moved
      since. So an over-balance transfer is not merely refused by the server: it is never sent.
    */
    const stillAvailable = availableBalanceOf(from);
    if (data.amount > stillAvailable) {
      wizard.toForm();
      wizard.fail(insufficientFundsMessage(stillAvailable));
      return;
    }

    /*
      MINT, then SEND (ADR-0042) — the same two-call shape TransferPage uses, and internal transfers
      mint too: they already ask for the PIN, so binding it costs the user nothing and spares the
      codebase an exception to explain later.
    */
    /*
      `keyLive` means the idempotency hook is holding a key from an attempt whose outcome is
      unknown — IN_FLIGHT, a network failure, a 5xx. The only sanctioned forward action there is to
      re-send the SAME intent, so we re-present the SAME authorisation rather than minting a new one.
    */
    let authorizationId: string;
    if (keyLive && lastAuthorization.current) {
      authorizationId = lastAuthorization.current;
    } else {
      try {
        const minted = await authoriseInternalTransfer({
          fromAccountId: data.fromAccountId,
          toAccountId: data.toAccountId,
          amount: data.amount,
          pin: enteredPin.current,
        }).unwrap();
        authorizationId = minted.authorizationId;
        lastAuthorization.current = authorizationId;
        setAuthorizationHeld(true);
      } catch (caught) {
        wizard.failFrom(caught as ApiProblem);
        handleRefusal(caught as ApiProblem);
        return;
      }
    }

    const result = await wizard.run(
      {
        fromAccountId: data.fromAccountId,
        toAccountId: data.toAccountId,
        amount: data.amount,
      },
      // A HEADER at the wire: the server fingerprints the body alone.
      { stepUpAuthorizationId: authorizationId },
    );
    // undefined means it failed and the wizard has already set the banner, the in-flight note or
    // the verify view. Under `strict` this early return is not optional.
    if (!result) {
      handleRefusal(wizard.lastProblem.current);
      return;
    }

    setPin('');
    enteredPin.current = '';
    lastAuthorization.current = null;
    setAuthorizationHeld(false);

    setSuccess({
      amount: data.amount,
      fromName: from.name,
      toName: to.name,
      fromNewBalance: result.fromAccountNewBalance,
      transactionNumber: result.transactionNumber,
      replayed: result.replayed,
    });
  };

  /**
   * What the PIN step does about a refusal, shared by the mint and the send. Mirrors TransferPage,
   * where the reasoning for each branch is written out in full.
   */
  function handleRefusal(refusal: ApiProblem | null) {
    if (refusal?.errorCode === 'INVALID_PIN') {
      setPin('');
      enteredPin.current = '';
      setPinError(true);
      setPinNonce((n) => n + 1);
    } else if (
      refusal?.errorCode === 'AUTHORIZATION_EXPIRED' ||
      refusal?.errorCode === 'AUTHORIZATION_INVALID'
    ) {
      // Stay put; the form keeps the amount and both accounts. WCAG 2.2 SC 3.3.7 is Level A and its
      // exception covers the PIN only. No pinError — an expiry is not a wrong PIN and costs no
      // attempt, so nothing here may imply the lock is closer.
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
    } else if (refusal?.errorCode === 'PIN_LOCKED') {
      setPin('');
      enteredPin.current = '';
      setPinLockDeadline(retryDeadline(refusal.retryAfterSeconds ?? DEFAULT_PIN_LOCK_SECONDS));
    } else if (refusal?.errorCode === 'PIN_REQUIRED') {
      // `requestLeave`, not a bare navigate: this page deliberately owns no destinations of
      // its own, and the wizard refuses any exit while an idempotency key is live. A 422 is
      // a key-DROP class, so this one goes through.
      requestLeave('/pin-setup?returnTo=/transfer/internal');
    }
  }

  if (success) {
    return (
      <div className={styles.page}>
        {/* No exits: the money has moved, so there is nothing to abandon, and the body's two
            buttons are the way on. */}
        <PageHeader title="Transfer Complete" />
        <div className={styles.body}>
          <div className={styles.centeredView}>
            <div className={styles.successIcon}>
              <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
            </div>
            <Text className={styles.successTitle}>Transfer Complete!</Text>
            <Text className={styles.successAmount}>{formatCurrency(success.amount)}</Text>
            {success.replayed && (
              <MessageBar intent="info" role="status">
                <MessageBarBody>
                  This transfer was already processed — showing the existing result.
                </MessageBarBody>
              </MessageBar>
            )}
            <div className={styles.reviewCard} style={{ width: '100%' }}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>From</Text>
                <Text className={styles.reviewValue}>{success.fromName}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>{success.toName}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Reference</Text>
                <Text className={styles.reviewValue}>{success.transactionNumber}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>{success.fromName} balance</Text>
                <Text className={styles.reviewValue}>{formatCurrency(success.fromNewBalance)}</Text>
              </div>
            </div>
          </div>
          <div className={styles.actions}>
            {/* An internal transfer writes a transaction and invalidates the same list an external
                one does, but only external's receipt offered to go and look at it. Drift, not a
                domain difference. */}
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

  if (verifyRequired) {
    return (
      <ResultUnknownView
        title="Move Money"
        repeatWarning="move the money twice"
        onCheckTransactions={() => requestLeave('/history')}
        onStartOver={wizard.startOver}
      />
    );
  }

  return (
    <div className={styles.page}>
      {/* THE GUARDED HEADER — same handlers, same `disabled={keyLive}`, and every exit still
          routes through this page's own `requestLeave`, which re-checks it. PageHeader takes
          handlers and calls them; it never decides a destination, which is why moving this bar into
          a shared component cannot quietly delete the anti-double-spend guard. */}
      <PageHeader
        title={
          step === 'pin' ? 'Confirm with PIN' : step === 'review' ? 'Review Transfer' : 'Move Money'
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
        {accountsProblem && (
          <MessageBar intent="error" role="alert">
            <MessageBarBody>
              {accountsProblem.detail || 'Could not load your accounts.'}
              {accountsProblem.traceId ? ` Support code: ${accountsProblem.traceId}` : ''}
            </MessageBarBody>
            <MessageBarActions>
              <Button appearance="transparent" onClick={() => void refetchAccounts()}>
                Retry
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}
        {error && (
          <MessageBar intent="error" role="alert">
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
            a second payment, which is why it is worded as one — and the wording splits, because the
            two ways to get here are not equally knowable. A 409 IN_FLIGHT means the server told us
            it is working on it. A lost response means we do not know whether anything happened, and
            saying "still processing" there would assert something nobody has been told.
          */
          <MessageBar intent="info" role="status">
            <MessageBarBody>
              {inFlight
                ? 'Still processing — check again to see whether it went through.'
                : "We couldn't reach the bank. Your transfer may or may not have gone through — check again."}
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
                Check again
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
            accounts, and blanking a half-filled money form would be the worse bug. */}
        {accountsProblem && accounts.length === 0 ? null : step === 'form' ? (
          <>
            <div>
              <Text className={styles.sectionLabel}>From</Text>
              <div
                style={{ display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '8px' }}
              >
                {accounts.map((account) => (
                  <button
                    key={account.id}
                    className={`${styles.card} ${fromAccount?.id === account.id ? styles.cardSelected : ''}`}
                    aria-label={`From ${account.name}`}
                    aria-pressed={fromAccount?.id === account.id}
                    onClick={() => selectFrom(account)}
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

            <div>
              <Text className={styles.sectionLabel}>To</Text>
              {/* `isSuccess`, NOT `!isLoading`. On a FAILED fetch RTK Query sets isLoading back
                  to false and leaves data undefined, so `accounts` fell back to [] and this said
                  "you need a second account" to someone who may have five — sending them off to
                  create one to solve a network problem. The message itself is a real rule (internal
                  transfer needs two accounts, external does not); only its gate was wrong. */}
              {accountsLoaded && accounts.length < 2 && (
                <Text className={styles.subtle} style={{ display: 'block', marginBottom: '8px' }}>
                  You need a second account to transfer between your own accounts.
                </Text>
              )}
              <div
                style={{ display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '8px' }}
              >
                {accounts.map((account) => {
                  const isFrom = account.id === fromAccount?.id;
                  return (
                    <button
                      key={account.id}
                      className={`${styles.card} ${toAccount?.id === account.id ? styles.cardSelected : ''}`}
                      aria-label={`To ${account.name}`}
                      aria-pressed={toAccount?.id === account.id}
                      onClick={() => selectTo(account)}
                      disabled={isFrom}
                    >
                      <div className={styles.accountInfo}>
                        <Text className={styles.accountName}>{account.name}</Text>
                        <Text className={styles.accountNumber}>
                          {isFrom ? 'Source account' : maskAccountNumber(account.accountNumber)}
                        </Text>
                      </div>
                      <Text className={styles.accountBalance}>
                        {formatCurrency(account.balance)}
                      </Text>
                    </button>
                  );
                })}
              </div>
            </div>

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
                  className={`${styles.quickBtn} ${amountNumber === quickAmount ? styles.quickBtnSelected : ''}`}
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
              >
                Review Transfer
              </Button>
              <button className={styles.linkBtn} onClick={() => requestLeave('/transfer')}>
                <ArrowSwap24Regular style={{ width: '18px', height: '18px' }} />
                Send to someone else
              </button>
            </div>
          </>
        ) : step === 'pin' ? (
          <>
            {/* PIN — ADR-0041. The credential travels in THIS request; the API verifies it. */}
            <div className={styles.reviewCard}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Moving</Text>
                <Text className={styles.reviewValue}>{formatCurrency(amountNumber)}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>{toAccount?.name}</Text>
              </div>
            </div>
            {/* With no Send control the behaviour has to be discoverable BEFORE the last digit. */}
            <Text style={{ textAlign: 'center' }}>
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
              // The sixth digit sends (ADR-0042); the completed value goes to the REF first,
              // because `onValid` runs before `pin` re-renders. See TransferPage.
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
            />
            {pinLockDeadline !== null && (
              <>
                <MessageBar intent="error" role="alert">
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
            {/* No Send button: the sixth digit is the send. The spinner is what remains visible of
                the request the user just started. */}
            {isSubmitting && (
              <div style={{ display: 'flex', justifyContent: 'center' }}>
                <Spinner size="tiny" label={`Moving ${formatCurrency(amountNumber)}`} />
              </div>
            )}
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
            <div className={styles.reviewCard}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>From</Text>
                <Text className={styles.reviewValue}>{fromAccount?.name}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>{toAccount?.name}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Amount</Text>
                <Text className={styles.reviewValue}>{formatCurrency(amountNumber)}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>{fromAccount?.name} balance</Text>
                <Text className={styles.reviewValue}>{formatCurrency(fromNewBalance)}</Text>
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

export default InternalTransferPage;
