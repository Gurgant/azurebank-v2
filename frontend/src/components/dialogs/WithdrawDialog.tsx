import { useEffect, useId, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useSelector } from 'react-redux';
import {
  makeStyles,
  Text,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import {
  ArrowUpload24Regular,
  CheckmarkCircle24Filled,
  Warning24Regular,
  LockClosed24Regular,
} from '@fluentui/react-icons';
import { Controller, useForm, type FieldErrors } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { colors } from '../../theme/tokens';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { useWithdrawMutation } from '../../features/api/apiSlice';
import { useIdempotentMutation } from '../../hooks/useIdempotentMutation';
import { selectCurrentUser } from '../../features/auth/authSlice';
import { formatCurrency } from '../../utils/format';
import { RetryCountdown, retryDeadline } from '../feedback';
import { MoneyDialogShell } from './MoneyDialogShell';
import { useMoneyDialogStyles } from './moneyDialogStyles';
import {
  insufficientFundsMessage,
  parseAmountInput,
  withdrawFormSchema,
  type WithdrawFormOutput,
  type WithdrawFormValues,
} from '../../forms/moneySchemas';
import { availableBalanceOf } from '../../utils/availableBalance';
import { useFundsGate } from '../../hooks/useFundsGate';
import { AmountField } from '../form/AmountField';
import { DescriptionField } from '../form/DescriptionField';
import { PinInput } from '../PinInput';
import { CONNECTION_FAILED } from '../../api/problemMessages';

// ============================================
// TYPES
// ============================================

interface Account {
  id: string;
  name: string;
  accountNumber: string;
  balance: number;
}

export interface WithdrawDialogProps {
  isOpen: boolean;
  onClose: () => void;
  accounts: Account[];
  onSuccess?: () => void;
}

interface SuccessData {
  amount: number;
  accountName: string;
  newBalance: number;
  transactionId: string;
  replayed: boolean;
}

// ============================================
// STYLES  (mirrors DepositDialog; adds the PIN step + lock view)
// ============================================

/**
 * The PIN step's own three keys. They stay here on purpose: they belong to a step deposit does not
 * have, and moving them into the shared module would make it a place where things are put rather
 * than a place where shared things live.
 */
const usePinStyles = makeStyles({
  pinStep: {
    flex: 1,
    padding: '24px 20px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '20px',
    overflowY: 'auto',
  },
  pinInstruction: {
    fontSize: '15px',
    color: colors.neutral[500],
    textAlign: 'center',
    lineHeight: '1.5',
  },
  pinAmount: { fontWeight: 700, color: colors.neutral[800] },
});

// ============================================
// CONSTANTS
// ============================================

const QUICK_AMOUNTS = [50, 100, 200, 500];
const PIN_LENGTH = 6;
const DEFAULT_PIN_LOCK_SECONDS = 15 * 60;

type Step = 'form' | 'pin';

// ============================================
// COMPONENT
// ============================================

/**
 * PR-10 — the withdraw twin of the deposit flow, plus the PIN-in-body gate (D1), now on
 * RHF+Zod (the money-forms rewrite): the FORM step (account/amount/description) lives in
 * react-hook-form with the balance-capped `withdrawFormSchema` as resolver, while the PIN
 * step machine is deliberately untouched (plan D5 — money-critical path, minimal churn).
 * Same idempotency spine (useIdempotentMutation: KEEP on IN_FLIGHT/network/5xx, rotate on
 * any body edit — the PIN is part of the body, so editing it re-keys too). The PIN is NOT
 * step-up: it travels in the withdraw request and is verified server-side, so a wrong PIN
 * is a 401 INVALID_PIN that stays in this dialog (sessionMiddleware exempts it from the
 * global logout). A user with no PIN is sent to /pin-setup first. The dialog cannot be
 * dismissed while an idempotency key is still live — the Fluent shell's Esc/backdrop
 * dismissal funnels through the SAME keyLive guard as the X button.
 */
export function WithdrawDialog({ isOpen, onClose, accounts, onSuccess }: WithdrawDialogProps) {
  const styles = useMoneyDialogStyles();
  const pinStyles = usePinStyles();
  const navigate = useNavigate();
  const errorId = useId();
  const user = useSelector(selectCurrentUser);
  // Gate only when we KNOW the user has no PIN; if unknown, let the server decide
  // (PIN_REQUIRED is handled in the catch as a fallback).
  const needsPinSetup = user ? user.hasPin === false : false;

  const [withdrawTrigger] = useWithdrawMutation();
  const { submit, resetIntent, verifyRequired, keyRetained } =
    useIdempotentMutation(withdrawTrigger);

  const [step, setStep] = useState<Step>('form');
  const [pin, setPin] = useState('');
  const [pinNonce, setPinNonce] = useState(0); // bumped to remount PinInput (refocus box 1)
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [inFlight, setInFlight] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [pinError, setPinError] = useState(false);
  /*
    An ABSOLUTE deadline (D13), not a countdown this dialog drives itself. It used to be a number of
    seconds stored once and never touched, so the PIN box and Withdraw stayed disabled and the
    message kept promising "about 15 minutes" for as long as the dialog was open, well past the
    server's window. `RetryCountdown` ticks and calls back at zero; a deadline rather than a duration
    is what makes a second lock with the same retryAfterSeconds mint a fresh one.
  */
  const [lockDeadline, setLockDeadline] = useState<number | null>(null);
  const [success, setSuccess] = useState<SuccessData | null>(null);

  const defaultAccountId = accounts.length > 0 ? accounts[0].id : '';
  const [resolvedBalanceOf, setResolvedBalanceOf] = useState(defaultAccountId);
  const selectedForBalance = accounts.find((a) => a.id === resolvedBalanceOf) ?? null;
  const availableBalance = selectedForBalance ? availableBalanceOf(selectedForBalance) : 0;

  /*
    The client half of "the PIN is never asked for an operation that cannot succeed". The Zod bound
    above is built from a CACHED balance; this re-reads it from the server at the moment the user
    commits. See `useFundsGate` for why that matters — measured, a doomed request still spends a
    PIN attempt, and three of those lock the PIN for fifteen minutes.
  */
  const confirmFunds = useFundsGate();
  const [checkingFunds, setCheckingFunds] = useState(false);

  // The balance bound is dynamic: the schema (and so the resolver) is rebuilt when the
  // selected account's balance changes — RHF revalidates through the new bounds.
  const schema = useMemo(() => withdrawFormSchema(availableBalance), [availableBalance]);
  const { control, handleSubmit, setValue, watch, formState, trigger } = useForm<
    WithdrawFormValues,
    unknown,
    WithdrawFormOutput
  >({
    resolver: zodResolver(schema),
    mode: 'onChange',
    defaultValues: { accountId: defaultAccountId, amount: '', description: '' },
  });

  // Re-run amount validation when the balance bound changes (an over-balance amount is
  // already RESET on switch, but a kept amount's hint text embeds the balance — the
  // cached result must not describe the previous account).
  useEffect(() => {
    void trigger('amount');
  }, [availableBalance, trigger]);

  const amountNumber = parseAmountInput(watch('amount'));
  const selectedAccount = selectedForBalance;

  // Any body-affecting edit (amount/account/description/PIN) rotates the key: the old key
  // + a new body is a raw-byte fingerprint mismatch → 422 KEY_REUSE. Never while a request
  // is in flight — nulling the key out from under a pending submit would defeat the
  // retained-key dismissal guard. (Structurally the body isn't reachable mid-submit here —
  // the PIN step's input is disabled — but the invariant is stated uniformly.)
  const onBodyEdit = () => {
    if (isSubmitting) return;
    resetIntent();
    setInFlight(false);
    setError(null);
  };

  const handleQuickAmount = (value: number) => {
    setValue('amount', value.toString(), { shouldValidate: true, shouldDirty: true });
    onBodyEdit();
  };

  const handlePinChange = (next: string) => {
    setPin(next);
    setPinError(false);
    onBodyEdit();
  };

  // Move between steps and drop the transient banners so a stale 'Invalid PIN' never lingers
  // on the amount screen (INSUFFICIENT_FUNDS sets its own message AFTER calling setStep, so
  // that one survives on purpose).
  const goToStep = (next: Step) => {
    setStep(next);
    setError(null);
    setPinError(false);
    setInFlight(false);
  };

  /**
   * Continue → PIN, but only once the SERVER has confirmed the amount still fits.
   *
   * This is the whole point of the change: a strong-authentication ceremony must never be spent on
   * a request that is already known to fail. Measured on the running API, an over-balance
   * withdrawal with a mistyped PIN answers 401 INVALID_PIN — the funds check comes AFTER the PIN —
   * so reaching this step with a stale balance costs the user a PIN attempt, and three of those
   * lock them out for fifteen minutes over an operation that could never have succeeded.
   *
   * On refusal we stay on the form. The banner explains the action; the field's own hint turns red
   * on its own, because the refetch updated the cache the Zod bound is built from.
   */
  const advanceToPin = async () => {
    if (!selectedAccount) return;
    setCheckingFunds(true);
    try {
      const verdict = await confirmFunds(selectedAccount.id, amountNumber);
      if (verdict.status === 'insufficient') {
        setError(insufficientFundsMessage(verdict.available));
        return;
      }
      // 'unknown' advances on purpose — see useFundsGate. A courtesy check that became a blocker
      // when the network hiccupped would be worse than the problem it solves.
      goToStep('pin');
    } finally {
      setCheckingFunds(false);
    }
  };

  /**
   * `handleSubmit`'s SECOND argument, and the reason it now has one.
   *
   * The Withdraw button's disabled condition never mentioned form validity, so when the balance
   * moved while the PIN step was open the click ran `handleSubmit`, the resolver refused, and
   * NOTHING happened — no request, no message, no state change. A dead control that looks alive.
   * The errors are passed in rather than read from `formState`, so this reports what actually
   * failed instead of guessing.
   */
  const onInvalid = (errors: FieldErrors<WithdrawFormValues>) => {
    setStep('form');
    setError(errors.amount?.message ?? 'Please check the details and try again.');
  };

  const amountValid = amountNumber > 0 && !formState.errors.amount;
  const newBalance = selectedAccount ? availableBalance - amountNumber : 0;

  const onValid = async (data: WithdrawFormOutput) => {
    const account = accounts.find((a) => a.id === data.accountId);
    if (!account || pin.length !== PIN_LENGTH || lockDeadline !== null) {
      return;
    }
    /*
      Last look before the request leaves. The funds gate took a round trip when Continue was
      pressed; this one is free — it reads the freshest CACHED balance, which a mutation elsewhere
      in this tab may have moved since. Cheap enough to always do, and it means an over-balance
      request is not merely refused by the server but never sent.
    */
    const stillAvailable = availableBalanceOf(account);
    if (data.amount > stillAvailable) {
      setStep('form');
      setError(insufficientFundsMessage(stillAvailable));
      return;
    }
    setError(null);
    setPinError(false);
    setInFlight(false);
    setIsSubmitting(true);
    try {
      const result = await submit({
        accountId: data.accountId,
        amount: data.amount,
        pin,
        description: data.description,
      });
      setSuccess({
        amount: data.amount,
        accountName: account.name,
        newBalance: result.newBalance,
        transactionId: result.transaction.id,
        replayed: result.replayed,
      });
      onSuccess?.();
    } catch (caught) {
      const problem = caught as ApiProblem;
      if (problem.errorCode === 'IDEMPOTENCY_RESULT_UNKNOWN') {
        // hook latched verifyRequired; the verify view renders below.
      } else if (problem.errorCode === 'IDEMPOTENCY_IN_FLIGHT') {
        setInFlight(true);
      } else if (problem.errorCode === 'INVALID_PIN') {
        // Wrong PIN — clear the boxes and remount (refocus box 1) so the retry is usable.
        // Safe (401 exempted from global logout); the hook already dropped the key, so the
        // corrected-PIN retry mints a fresh one.
        setPin('');
        setPinError(true);
        setPinNonce((n) => n + 1);
        setError('Invalid PIN. Please try again.');
      } else if (problem.errorCode === 'PIN_LOCKED') {
        setPin('');
        setLockDeadline(retryDeadline(problem.retryAfterSeconds ?? DEFAULT_PIN_LOCK_SECONDS));
      } else if (problem.errorCode === 'PIN_REQUIRED') {
        // Defensive: the hasPin gate should have caught this. Send them to set a PIN.
        navigate('/pin-setup?returnTo=/accounts');
      } else if (problem.errorCode === 'INSUFFICIENT_FUNDS') {
        // Balance shifted under us — back to the amount step to adjust (message survives:
        // setStep directly, NOT goToStep, so the error set right after is kept).
        setStep('form');
        setError('Insufficient funds — your balance changed. Please check the amount.');
      } else if (problem.errorCode === 'VALIDATION_ERROR') {
        const firstFieldError = Object.values(problem.errors ?? {})[0]?.[0];
        setError(firstFieldError ?? 'Please check the details and try again.');
      } else if (
        problem.errorCode === 'IDEMPOTENCY_KEY_REUSE' ||
        problem.errorCode === 'IDEMPOTENCY_KEY_MISSING' ||
        problem.errorCode === 'IDEMPOTENCY_KEY_INVALID'
      ) {
        setError('Something went wrong. Please try again.');
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        setError(CONNECTION_FAILED);
      } else {
        setError(problem.detail || 'Withdrawal failed. Please try again.');
      }
    } finally {
      setIsSubmitting(false);
    }
  };

  if (!isOpen) return null;

  const showForm = !success && !verifyRequired && !needsPinSetup && step === 'form';
  const showPin = !success && !verifyRequired && !needsPinSetup && step === 'pin';

  // CRITICAL: never dismiss while an idempotency key is still LIVE. `keyRetained` is the hook's
  // source of truth — the key is held while submitting AND after every KEEP outcome
  // (IN_FLIGHT, network, parse, 5xx), not just IN_FLIGHT. The dialog is mount-on-open, so
  // unmounting with a retained key loses it; reopening mints a fresh one and the same amount
  // becomes a NEW intent = a double-spend. Editing the body (onBodyEdit → resetIntent) or a
  // terminal outcome releases the key and re-enables dismissal.
  const keyLive = isSubmitting || keyRetained;
  const requestClose = () => {
    if (!keyLive) {
      onClose();
    }
  };

  const goToPinSetup = () => navigate('/pin-setup?returnTo=/accounts');

  return (
    <MoneyDialogShell
      open={isOpen}
      title={success ? 'Withdrawal Complete' : 'Withdraw Money'}
      icon={<ArrowUpload24Regular />}
      tone="debit"
      onClose={requestClose}
      closeDisabled={keyLive}
    >
      {/* Success */}
      {success && (
        <div className={styles.centeredView}>
          <div className={styles.successIcon}>
            <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
          </div>
          <Text className={styles.successTitle}>Withdrawal Successful!</Text>
          <Text className={styles.successAmount}>-{formatCurrency(success.amount)}</Text>
          {success.replayed && (
            <MessageBar intent="info" role="status">
              <MessageBarBody>
                This withdrawal was already processed — showing the existing result.
              </MessageBarBody>
            </MessageBar>
          )}
          <div className={styles.detailsCard}>
            <div className={styles.detailRow}>
              <Text className={styles.detailLabel}>From</Text>
              <Text className={styles.detailValue}>{success.accountName}</Text>
            </div>
            <div className={styles.detailRow}>
              <Text className={styles.detailLabel}>New balance</Text>
              <Text className={styles.detailValue}>{formatCurrency(success.newBalance)}</Text>
            </div>
          </div>
        </div>
      )}

      {/* RESULT_UNKNOWN — verify-first (§2.3) */}
      {!success && verifyRequired && (
        <div className={styles.centeredView}>
          <div className={styles.warningIcon}>
            <Warning24Regular style={{ width: '40px', height: '40px' }} />
          </div>
          <Text className={styles.stateTitle}>We couldn&apos;t confirm your withdrawal</Text>
          <Text className={styles.stateBody}>
            The request may or may not have gone through. Check your recent transactions before
            trying again — retrying blindly could withdraw twice.
          </Text>
        </div>
      )}

      {/* Needs a PIN first */}
      {!success && !verifyRequired && needsPinSetup && (
        <div className={styles.centeredView}>
          <div className={styles.warningIcon}>
            <LockClosed24Regular style={{ width: '40px', height: '40px' }} />
          </div>
          <Text className={styles.stateTitle}>Set up a PIN to withdraw</Text>
          <Text className={styles.stateBody}>
            Withdrawals need a 6-digit PIN. Set one up and you&apos;ll come right back to your
            accounts.
          </Text>
        </div>
      )}

      {/* Form step */}
      {showForm && (
        <div className={styles.content}>
          <div>
            <Text className={styles.sectionLabel}>Select Account</Text>
            <Controller
              control={control}
              name="accountId"
              render={({ field }) => (
                <>
                  {accounts.map((account) => {
                    const selectAccount = () => {
                      field.onChange(account.id);
                      setResolvedBalanceOf(account.id);
                      /*
                        Switching to a smaller account used to CLEAR an over-balance amount. It no
                        longer does. Silently deleting what someone typed is the hostile half of
                        "don't let them enter too much": the figure vanishes with no explanation,
                        and the very message that would explain it — the red hint naming the new
                        balance — never gets a value to attach to. The amount stays, the schema's
                        `trigger('amount')` effect re-validates it against the new bound, and the
                        user sees exactly why they cannot continue. The two transfer pages already
                        behaved this way; this is the surface that disagreed.
                      */
                      onBodyEdit();
                    };
                    return (
                      // Styled div, so the button semantics are wired by hand
                      // (same pattern as the AccountsPage add-card).
                      <div
                        key={account.id}
                        className={`${styles.accountCard} ${
                          field.value === account.id ? styles.accountCardSelected : ''
                        }`}
                        role="button"
                        tabIndex={0}
                        aria-pressed={field.value === account.id}
                        onClick={selectAccount}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            selectAccount();
                          }
                        }}
                        style={{ marginBottom: '8px' }}
                      >
                        <div className={styles.accountInfo}>
                          <Text className={styles.accountName}>{account.name}</Text>
                          <Text className={styles.accountNumber}>{account.accountNumber}</Text>
                        </div>
                        <Text className={styles.accountBalance}>
                          {formatCurrency(account.balance)}
                        </Text>
                      </div>
                    );
                  })}
                </>
              )}
            />
          </div>

          <div className={styles.amountSection}>
            <Text className={styles.amountLabel}>Enter amount</Text>
            <AmountField
              control={control}
              name="amount"
              ariaLabel="Withdraw amount"
              onBodyEdit={onBodyEdit}
              classNames={{
                wrapper: styles.amountInputWrapper,
                currency: styles.amountCurrency,
                input: styles.amountInput,
                hint: styles.amountHint,
                invalid: styles.amountInvalid,
              }}
              belowSlot={
                <div className={styles.availableRow}>
                  <Text className={styles.newBalance}>
                    {selectedAccount && amountValid
                      ? `New balance: ${formatCurrency(newBalance)}`
                      : `Available: ${formatCurrency(availableBalance)}`}
                  </Text>
                  {/*
                    The constructive half of "don't let them send more than they have": until now
                    a user who wanted to move everything had to read the balance and retype it,
                    which is exactly where an over-balance typo comes from. The accessible name
                    carries the figure, because "Use max" alone tells a screen-reader user nothing.
                  */}
                  <button
                    type="button"
                    className={styles.useMaxBtn}
                    onClick={() => handleQuickAmount(availableBalance)}
                    disabled={availableBalance <= 0}
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

          <DescriptionField
            control={control}
            name="description"
            onBodyEdit={onBodyEdit}
            className={styles.descriptionInput}
          />
        </div>
      )}

      {/* PIN step */}
      {showPin && (
        <div className={pinStyles.pinStep}>
          <Text className={styles.stateTitle}>Verify Withdrawal</Text>
          <Text className={pinStyles.pinInstruction}>
            Enter your 6-digit PIN to confirm withdrawing{' '}
            <span className={pinStyles.pinAmount}>{formatCurrency(amountNumber)}</span> from{' '}
            {selectedAccount?.name}.
          </Text>
          <PinInput
            key={pinNonce}
            value={pin}
            onChange={handlePinChange}
            length={PIN_LENGTH}
            disabled={isSubmitting || lockDeadline !== null}
            error={pinError}
            autoFocus
            ariaLabel="Enter your PIN"
            ariaDescribedBy={error || lockDeadline !== null ? errorId : undefined}
          />
        </div>
      )}

      {/* Footer */}
      <div className={styles.footer}>
        {/* The div below is an `aria-describedby` target, NOT a live region. The role used to live
            there, back when MessageBar carried none; now each banner announces itself (the pattern
            LoginPage set), and a role there too would give one message two atomic announceable
            ancestors — read twice, and with the error and the lock countdown merged into a single
            utterance. The id stays: it is how someone tabbing back to the PIN field hears why the
            attempt failed. */}
        {(error || lockDeadline !== null) && (
          <div id={errorId}>
            {error && (
              <MessageBar intent="error" role="alert" className={styles.errorMessage}>
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            {lockDeadline !== null && (
              <>
                <MessageBar intent="warning" role="alert" className={styles.errorMessage}>
                  <MessageBarBody>Too many incorrect PIN attempts.</MessageBarBody>
                </MessageBar>
                {/* The countdown is a SIBLING of the alert, never a child: role="alert" implies
                    aria-atomic, so a nested timer would re-announce the whole banner every second,
                    assertively. It carries its own polite region. */}
                <RetryCountdown deadline={lockDeadline} onElapsed={() => setLockDeadline(null)} />
              </>
            )}
          </div>
        )}
        {inFlight && (
          <MessageBar intent="info" role="status" className={styles.errorMessage}>
            <MessageBarBody>Still processing — tap Withdraw again to check.</MessageBarBody>
          </MessageBar>
        )}

        {success ? (
          <>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => void navigate(`/transactions/${success.transactionId}`)}
            >
              View Transaction
            </Button>
            <Button
              appearance="secondary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={onClose}
            >
              Done
            </Button>
          </>
        ) : verifyRequired ? (
          <>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => void navigate('/history')}
            >
              Check recent transactions
            </Button>
            <Button
              appearance="secondary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => {
                resetIntent();
                setPin('');
                goToStep('form');
              }}
            >
              It didn&apos;t go through — try again
            </Button>
          </>
        ) : needsPinSetup ? (
          <>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={goToPinSetup}
            >
              Set up PIN
            </Button>
            <Button
              appearance="secondary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={onClose}
            >
              Cancel
            </Button>
          </>
        ) : step === 'form' ? (
          <Button
            appearance="primary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={() => void advanceToPin()}
            disabled={!formState.isValid || checkingFunds}
          >
            {checkingFunds ? (
              <Spinner size="tiny" />
            ) : (
              `Continue ${amountNumber > 0 && amountValid ? `· ${formatCurrency(amountNumber)}` : ''}`.trim()
            )}
          </Button>
        ) : (
          <>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => void handleSubmit(onValid, onInvalid)()}
              disabled={isSubmitting || pin.length !== PIN_LENGTH || lockDeadline !== null}
            >
              {isSubmitting ? <Spinner size="tiny" /> : `Withdraw ${formatCurrency(amountNumber)}`}
            </Button>
            <Button
              appearance="secondary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => goToStep('form')}
              disabled={isSubmitting}
            >
              Back
            </Button>
          </>
        )}
      </div>
    </MoneyDialogShell>
  );
}

export default WithdrawDialog;
