import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Text, Button, Spinner, MessageBar, MessageBarBody } from '@fluentui/react-components';
import {
  ArrowDownload24Regular,
  CheckmarkCircle24Filled,
  Warning24Regular,
} from '@fluentui/react-icons';
import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { useDepositMutation } from '../../features/api/apiSlice';
import { useIdempotentMutation } from '../../hooks/useIdempotentMutation';
import { formatCurrency } from '../../utils/format';
import { MoneyDialogShell } from './MoneyDialogShell';
import { useMoneyDialogStyles } from './moneyDialogStyles';
import {
  depositFormSchema,
  parseAmountInput,
  type DepositFormOutput,
  type DepositFormValues,
} from '../../forms/moneySchemas';
import { AmountField } from '../form/AmountField';
import { DescriptionField } from '../form/DescriptionField';

// ============================================
// TYPES
// ============================================

interface Account {
  id: string;
  name: string;
  accountNumber: string;
  balance: number;
}

export interface DepositDialogProps {
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
// STYLES
// ============================================

// ============================================
// CONSTANTS
// ============================================

const QUICK_AMOUNTS = [50, 100, 200, 500];

// ============================================
// COMPONENT
// ============================================

/**
 * T3 — the first production idempotent mutation (PR-9), now on RHF+Zod (the money-forms
 * rewrite): the form state (account/amount/description) lives in react-hook-form with
 * `depositFormSchema` as the resolver — the SAME #33 bounds, exact legacy copy — while the
 * idempotency spine is untouched: useIdempotentMutation keeps the key across KEEP outcomes
 * (IN_FLIGHT / network / 5xx) so Retry re-sends the SAME key + body, and every body edit
 * rotates it (an edited body with the old key is a 422 KEY_REUSE). A replayed 2xx surfaces
 * a polite note (D4); RESULT_UNKNOWN latches a verify-first flow (§2.3). No step-up —
 * deposit is auth level 1. The shell is a Fluent Dialog now: focus trap, Escape and
 * aria-modal come from the platform, and BOTH dismissal paths (Esc/backdrop and the X)
 * funnel through the keyLive guard.
 */
export function DepositDialog({ isOpen, onClose, accounts, onSuccess }: DepositDialogProps) {
  const styles = useMoneyDialogStyles();
  const navigate = useNavigate();

  const [depositTrigger] = useDepositMutation();
  const { submit, resetIntent, verifyRequired, keyRetained } =
    useIdempotentMutation(depositTrigger);

  const schema = useMemo(() => depositFormSchema(), []);
  const { control, handleSubmit, setValue, watch, formState } = useForm<
    DepositFormValues,
    unknown,
    DepositFormOutput
  >({
    resolver: zodResolver(schema),
    mode: 'onChange',
    defaultValues: {
      accountId: accounts.length > 0 ? accounts[0].id : '',
      amount: '',
      description: '',
    },
  });

  const [isSubmitting, setIsSubmitting] = useState(false);
  const [inFlight, setInFlight] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<SuccessData | null>(null);

  const accountId = watch('accountId');
  const amountNumber = parseAmountInput(watch('amount'));
  const selectedAccount = accounts.find((account) => account.id === accountId) ?? null;

  // Any body-affecting edit rotates the key: the old key + a new body is a raw-byte
  // fingerprint mismatch → 422 KEY_REUSE. Also clears transient in-flight/error state.
  // Blocked while a request is in flight: rotating/nulling the key out from under a pending
  // submit would defeat the retained-key guard (a subsequent NETWORK/5xx/IN_FLIGHT could
  // then close/resubmit into a NEW intent while the original still settles).
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

  const amountValid = amountNumber > 0 && !formState.errors.amount;
  const newBalance = selectedAccount ? selectedAccount.balance + amountNumber : 0;

  const onValid = async (data: DepositFormOutput) => {
    const account = accounts.find((a) => a.id === data.accountId);
    if (!account) return;
    setError(null);
    setInFlight(false);
    setIsSubmitting(true);
    try {
      const result = await submit({
        accountId: data.accountId,
        amount: data.amount,
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
      // D17 / §2.3: route on errorCode, never a blanket toast. RESULT_UNKNOWN is
      // handled by the hook (latches verifyRequired) — we just render that view.
      if (problem.errorCode === 'IDEMPOTENCY_RESULT_UNKNOWN') {
        // hook set verifyRequired; the verify view renders below.
      } else if (problem.errorCode === 'IDEMPOTENCY_IN_FLIGHT') {
        setInFlight(true);
      } else if (problem.errorCode === 'VALIDATION_ERROR') {
        // Field-agnostic: surface the first server message rather than assuming 'amount'.
        const firstFieldError = Object.values(problem.errors ?? {})[0]?.[0];
        setError(firstFieldError ?? 'Please check the amount and try again.');
      } else if (
        problem.errorCode === 'IDEMPOTENCY_KEY_REUSE' ||
        problem.errorCode === 'IDEMPOTENCY_KEY_MISSING' ||
        problem.errorCode === 'IDEMPOTENCY_KEY_INVALID'
      ) {
        // Client protocol bug — never surface the raw code (D17).
        setError('Something went wrong. Please try again.');
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        // Transport failure — a raw "TypeError: Failed to fetch" would leak (D17). The
        // key is KEPT (shouldKeepKey) so tapping Deposit again is a safe same-key retry.
        setError("Couldn't reach the server — check your connection and try again.");
      } else {
        setError(problem.detail || 'Deposit failed. Please try again.');
      }
    } finally {
      setIsSubmitting(false);
    }
  };

  if (!isOpen) return null;

  const showForm = !success && !verifyRequired;

  // CRITICAL: never dismiss while an idempotency key is still LIVE. `keyRetained` covers
  // submitting AND every KEEP outcome (IN_FLIGHT / network / 5xx) — the dialog is
  // mount-on-open, so unmounting with a retained key loses it, and reopening mints a fresh
  // one so the same amount becomes a NEW intent = a real double-deposit. Esc and backdrop
  // dismissal (Fluent onOpenChange) funnel through the same guard as the X button.
  const keyLive = isSubmitting || keyRetained;
  const requestClose = () => {
    if (!keyLive) {
      onClose();
    }
  };

  return (
    <MoneyDialogShell
      open={isOpen}
      title={success ? 'Deposit Complete' : 'Deposit Money'}
      icon={<ArrowDownload24Regular />}
      tone="credit"
      onClose={requestClose}
      closeDisabled={keyLive}
    >
      {/* Success */}
      {success && (
        <div className={styles.centeredView}>
          <div className={styles.successIcon}>
            <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
          </div>
          <Text className={styles.successTitle}>Deposit Successful!</Text>
          <Text className={styles.successAmount}>+{formatCurrency(success.amount)}</Text>
          {success.replayed && (
            <MessageBar intent="info" role="status">
              <MessageBarBody>
                This deposit was already processed — showing the existing result.
              </MessageBarBody>
            </MessageBar>
          )}
          <div className={styles.detailsCard}>
            <div className={styles.detailRow}>
              <Text className={styles.detailLabel}>To</Text>
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
          <Text className={styles.stateTitle}>We couldn&apos;t confirm your deposit</Text>
          <Text className={styles.stateBody}>
            The request may or may not have gone through. Check your recent transactions before
            trying again — retrying blindly could deposit twice.
          </Text>
        </div>
      )}

      {/* Form */}
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
                      // A div can't be `disabled` — guard the mid-flight edit here.
                      if (isSubmitting) return;
                      field.onChange(account.id);
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
              ariaLabel="Deposit amount"
              disabled={isSubmitting}
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
                ) : null
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
                disabled={isSubmitting}
              >
                €{quickAmount}
              </button>
            ))}
          </div>

          <DescriptionField
            control={control}
            name="description"
            disabled={isSubmitting}
            onBodyEdit={onBodyEdit}
            className={styles.descriptionInput}
          />
        </div>
      )}

      {/* Footer */}
      <div className={styles.footer}>
        {error && (
          <MessageBar intent="error" role="alert" className={styles.errorMessage}>
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
        {inFlight && (
          <MessageBar intent="info" role="status" className={styles.errorMessage}>
            <MessageBarBody>Still processing — tap Deposit again to check.</MessageBarBody>
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
              onClick={resetIntent}
            >
              It didn&apos;t go through — try again
            </Button>
          </>
        ) : (
          <Button
            appearance="primary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={() => void handleSubmit(onValid)()}
            disabled={isSubmitting || !formState.isValid}
          >
            {isSubmitting ? (
              <Spinner size="tiny" />
            ) : (
              `Deposit ${amountNumber > 0 ? formatCurrency(amountNumber) : ''}`
            )}
          </Button>
        )}
      </div>
    </MoneyDialogShell>
  );
}

export default DepositDialog;
