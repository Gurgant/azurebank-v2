import { useEffect, useId, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useSelector } from 'react-redux';
import {
  Dialog,
  DialogSurface,
  makeStyles,
  Text,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import {
  Dismiss24Regular,
  ArrowUpload24Regular,
  CheckmarkCircle24Filled,
  Warning24Regular,
  LockClosed24Regular,
} from '@fluentui/react-icons';
import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { colors, transitions } from '../../theme/tokens';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { useWithdrawMutation } from '../../features/api/apiSlice';
import { useIdempotentMutation } from '../../hooks/useIdempotentMutation';
import { selectCurrentUser } from '../../features/auth/authSlice';
import { formatCurrency } from '../../utils/format';
import {
  parseAmountInput,
  withdrawFormSchema,
  type WithdrawFormOutput,
  type WithdrawFormValues,
} from '../../forms/moneySchemas';
import { AmountField } from '../form/AmountField';
import { DescriptionField } from '../form/DescriptionField';
import { PinInput } from '../PinInput';

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

const useStyles = makeStyles({
  surface: {
    width: '100%',
    maxWidth: '480px',
    maxHeight: '90vh',
    padding: 0,
    borderRadius: '16px',
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '20px 20px 16px 20px',
    borderBottom: `1px solid ${colors.neutral[200]}`,
  },
  headerTitle: {
    fontSize: '20px',
    fontWeight: 600,
    color: colors.neutral[800],
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },
  headerIcon: {
    width: '32px',
    height: '32px',
    borderRadius: '8px',
    backgroundColor: colors.brand[130],
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.brand[60],
  },
  closeButton: {
    width: '40px',
    height: '40px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    borderRadius: '8px',
    color: colors.neutral[500],
    ':hover': { backgroundColor: colors.neutral[100] },
    ':disabled': { opacity: 0.5, cursor: 'not-allowed' },
  },
  content: {
    flex: 1,
    padding: '24px 20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
    overflowY: 'auto',
  },
  sectionLabel: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
    marginBottom: '8px',
  },
  accountCard: {
    width: '100%',
    padding: '16px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    ':hover': { backgroundColor: colors.neutral[50] },
  },
  accountCardSelected: {
    border: `2px solid ${colors.brand[60]}`,
    backgroundColor: colors.brand[130],
  },
  accountInfo: { display: 'flex', flexDirection: 'column', gap: '2px' },
  accountName: { fontSize: '16px', fontWeight: 500, color: colors.neutral[800] },
  accountNumber: {
    fontSize: '14px',
    fontFamily: 'Consolas, monospace',
    color: colors.neutral[500],
  },
  accountBalance: { fontSize: '16px', fontWeight: 600, color: colors.neutral[800] },
  amountSection: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '16px',
    backgroundColor: colors.neutral[50],
    borderRadius: '12px',
  },
  amountLabel: { fontSize: '14px', fontWeight: 400, color: colors.neutral[500] },
  amountInputWrapper: { display: 'flex', alignItems: 'baseline', gap: '4px' },
  amountCurrency: { fontSize: '32px', fontWeight: 300, color: colors.neutral[800] },
  amountInput: {
    fontSize: '48px',
    fontWeight: 700,
    color: colors.neutral[800],
    border: 'none',
    outline: 'none',
    background: 'transparent',
    textAlign: 'center',
    width: '180px',
    '::placeholder': { color: colors.neutral[300] },
  },
  newBalance: { fontSize: '13px', color: colors.neutral[500] },
  amountHint: { fontSize: '13px', fontWeight: 500, color: colors.semantic.error.main },
  quickAmounts: { display: 'flex', gap: '8px', flexWrap: 'wrap', justifyContent: 'center' },
  quickBtn: {
    minWidth: '70px',
    height: '36px',
    padding: '0 16px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '8px',
    cursor: 'pointer',
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
    transition: `all ${transitions.fast}`,
    ':hover': { backgroundColor: colors.neutral[50] },
    ':disabled': { opacity: 0.5, cursor: 'not-allowed' },
  },
  quickBtnSelected: {
    backgroundColor: colors.brand[120],
    border: `1px solid ${colors.brand[60]}`,
    color: colors.brand[60],
  },
  descriptionInput: {
    width: '100%',
    padding: '12px',
    borderRadius: '8px',
    border: `1px solid ${colors.neutral[200]}`,
    fontSize: '14px',
    fontFamily: 'inherit',
    color: colors.neutral[800],
    outline: 'none',
    ':focus': { border: `1px solid ${colors.brand[60]}` },
  },
  // ===== PIN STEP =====
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
  // ===== STATE VIEWS =====
  centeredView: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '16px',
    padding: '32px 20px',
    textAlign: 'center',
  },
  successIcon: {
    width: '80px',
    height: '80px',
    backgroundColor: colors.semantic.success.light,
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.semantic.success.main,
  },
  warningIcon: {
    width: '80px',
    height: '80px',
    backgroundColor: '#FEF3E2',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: '#B45309',
  },
  successTitle: { fontSize: '24px', fontWeight: 700, color: colors.semantic.success.main },
  stateTitle: { fontSize: '20px', fontWeight: 700, color: colors.neutral[800] },
  successAmount: { fontSize: '32px', fontWeight: 700, color: colors.neutral[800] },
  stateBody: { fontSize: '15px', color: colors.neutral[500], lineHeight: '1.5' },
  detailsCard: {
    width: '100%',
    backgroundColor: colors.neutral[50],
    borderRadius: '12px',
    padding: '4px 16px',
    marginTop: '8px',
  },
  detailRow: {
    display: 'flex',
    justifyContent: 'space-between',
    padding: '12px 0',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    ':last-child': { borderBottom: 'none' },
  },
  detailLabel: { fontSize: '14px', color: colors.neutral[500] },
  detailValue: { fontSize: '14px', fontWeight: 600, color: colors.neutral[800] },
  footer: {
    padding: '16px 20px 24px 20px',
    borderTop: `1px solid ${colors.neutral[200]}`,
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  errorMessage: { marginBottom: '4px' },
});

// ============================================
// CONSTANTS
// ============================================

const QUICK_AMOUNTS = [50, 100, 200, 500];
const PIN_LENGTH = 6;
const DEFAULT_PIN_LOCK_SECONDS = 15 * 60;

type Step = 'form' | 'pin';

/** "15 minutes" / "45 seconds" — a static lock horizon; the window is minutes long. */
function formatLockHorizon(seconds: number): string {
  if (seconds >= 60) {
    const minutes = Math.ceil(seconds / 60);
    return `${minutes} minute${minutes === 1 ? '' : 's'}`;
  }
  return `${seconds} second${seconds === 1 ? '' : 's'}`;
}

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
  const styles = useStyles();
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
  const [lockedSeconds, setLockedSeconds] = useState<number | null>(null);
  const [success, setSuccess] = useState<SuccessData | null>(null);

  const defaultAccountId = accounts.length > 0 ? accounts[0].id : '';
  const [resolvedBalanceOf, setResolvedBalanceOf] = useState(defaultAccountId);
  const selectedForBalance = accounts.find((a) => a.id === resolvedBalanceOf) ?? null;
  const availableBalance = selectedForBalance?.balance ?? 0;

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

  const amountValid = amountNumber > 0 && !formState.errors.amount;
  const newBalance = selectedAccount ? availableBalance - amountNumber : 0;

  const onValid = async (data: WithdrawFormOutput) => {
    const account = accounts.find((a) => a.id === data.accountId);
    if (!account || pin.length !== PIN_LENGTH || lockedSeconds !== null) {
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
        setLockedSeconds(problem.retryAfterSeconds ?? DEFAULT_PIN_LOCK_SECONDS);
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
        setError("Couldn't reach the server — check your connection and try again.");
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
    <Dialog
      open={isOpen}
      modalType="modal"
      onOpenChange={(_event, data) => {
        if (!data.open) requestClose();
      }}
    >
      <DialogSurface
        className={styles.surface}
        aria-label={success ? 'Withdrawal Complete' : 'Withdraw Money'}
        aria-describedby={undefined}
      >
        {/* Header */}
        <div className={styles.header}>
          <div className={styles.headerTitle}>
            <div className={styles.headerIcon}>
              <ArrowUpload24Regular />
            </div>
            {success ? 'Withdrawal Complete' : 'Withdraw Money'}
          </div>
          <button
            className={styles.closeButton}
            aria-label="Close"
            onClick={requestClose}
            disabled={keyLive}
          >
            <Dismiss24Regular />
          </button>
        </div>

        {/* Success */}
        {success && (
          <div className={styles.centeredView}>
            <div className={styles.successIcon}>
              <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
            </div>
            <Text className={styles.successTitle}>Withdrawal Successful!</Text>
            <Text className={styles.successAmount}>-{formatCurrency(success.amount)}</Text>
            {success.replayed && (
              <MessageBar intent="info">
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
                        // Switching to a smaller account may strand an over-balance
                        // amount — clear it, exactly like the legacy handler.
                        if (amountNumber > account.balance) {
                          setValue('amount', '', { shouldValidate: true });
                        }
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
                }}
                belowSlot={
                  selectedAccount && amountValid ? (
                    <Text className={styles.newBalance}>
                      New balance: {formatCurrency(newBalance)}
                    </Text>
                  ) : (
                    <Text className={styles.newBalance}>
                      Available: {formatCurrency(availableBalance)}
                    </Text>
                  )
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
          <div className={styles.pinStep}>
            <Text className={styles.stateTitle}>Verify Withdrawal</Text>
            <Text className={styles.pinInstruction}>
              Enter your 6-digit PIN to confirm withdrawing{' '}
              <span className={styles.pinAmount}>{formatCurrency(amountNumber)}</span> from{' '}
              {selectedAccount?.name}.
            </Text>
            <PinInput
              key={pinNonce}
              value={pin}
              onChange={handlePinChange}
              length={PIN_LENGTH}
              disabled={isSubmitting || lockedSeconds !== null}
              error={pinError}
              autoFocus
              ariaLabel="Enter your PIN"
              ariaDescribedBy={error || lockedSeconds !== null ? errorId : undefined}
            />
          </div>
        )}

        {/* Footer */}
        <div className={styles.footer}>
          {(error || lockedSeconds !== null) && (
            <div role="alert" id={errorId}>
              {error && (
                <MessageBar intent="error" className={styles.errorMessage}>
                  <MessageBarBody>{error}</MessageBarBody>
                </MessageBar>
              )}
              {lockedSeconds !== null && (
                <MessageBar intent="warning" className={styles.errorMessage}>
                  <MessageBarBody>
                    Too many incorrect PIN attempts. Try again in about{' '}
                    {formatLockHorizon(lockedSeconds)}.
                  </MessageBarBody>
                </MessageBar>
              )}
            </div>
          )}
          {inFlight && (
            <div role="status">
              <MessageBar intent="info" className={styles.errorMessage}>
                <MessageBarBody>Still processing — tap Withdraw again to check.</MessageBarBody>
              </MessageBar>
            </div>
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
              onClick={() => goToStep('pin')}
              disabled={!formState.isValid}
            >
              {`Continue ${amountNumber > 0 && amountValid ? `· ${formatCurrency(amountNumber)}` : ''}`.trim()}
            </Button>
          ) : (
            <>
              <Button
                appearance="primary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => void handleSubmit(onValid)()}
                disabled={isSubmitting || pin.length !== PIN_LENGTH || lockedSeconds !== null}
              >
                {isSubmitting ? (
                  <Spinner size="tiny" />
                ) : (
                  `Withdraw ${formatCurrency(amountNumber)}`
                )}
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
      </DialogSurface>
    </Dialog>
  );
}

export default WithdrawDialog;
