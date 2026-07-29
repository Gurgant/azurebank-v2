import { useEffect, useState } from 'react';
import {
  Text,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
  MessageBarActions,
} from '@fluentui/react-components';
import { CheckmarkCircle24Filled, ArrowSwap24Regular } from '@fluentui/react-icons';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import type { ApiProblem } from '../api/problemBaseQuery';
import { useTransferWizardStyles } from './transferWizardStyles';
import { ResultUnknownView } from '../components/shared/ResultUnknownView';
import { PageHeader } from '../components/layout/PageHeader';
import {
  useGetAccountsQuery,
  useTransferInternalMutation,
  type AccountResponse,
} from '../features/api/apiSlice';
import { useMoneyWizard } from '../hooks/useMoneyWizard';
import { formatCurrency, maskAccountNumber } from '../utils/format';
import {
  internalTransferFormSchema,
  parseAmountInput,
  type InternalTransferFormOutput,
  type InternalTransferFormValues,
} from '../forms/moneySchemas';
import { AmountField } from '../components/form/AmountField';

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
  const availableBalance = fromAccount?.balance ?? 0;

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

  const onValid = async (data: InternalTransferFormOutput) => {
    // Mirror canReview exactly (incl. from !== to) — belt-and-suspenders against a fromAccount
    // fallback converging on toAccount if the accounts list ever changed under the review step.
    // This client guard is the REAL defence: SAME_ACCOUNT_TRANSFER never reaches the wire.
    if (!fromAccount || !toAccount || fromAccount.id === toAccount.id) return;
    // Narrowed to consts BEFORE the await, and the receipt is built from those same consts, so the
    // review screen and the receipt cannot name two different accounts.
    const from = fromAccount;
    const to = toAccount;

    const result = await wizard.run({
      fromAccountId: data.fromAccountId,
      toAccountId: data.toAccountId,
      amount: data.amount,
    });
    // undefined means it failed and the wizard has already set the banner, the in-flight note or
    // the verify view. Under `strict` this early return is not optional.
    if (!result) return;

    setSuccess({
      amount: data.amount,
      fromName: from.name,
      toName: to.name,
      fromNewBalance: result.fromAccountNewBalance,
      transactionNumber: result.transactionNumber,
      replayed: result.replayed,
    });
  };

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
              <MessageBar intent="info">
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
        title={step === 'review' ? 'Review Transfer' : 'Move Money'}
        onBack={() => (step === 'review' ? wizard.toForm() : requestLeave('/dashboard'))}
        backDisabled={keyLive}
        onClose={() => requestLeave('/dashboard')}
        closeDisabled={keyLive}
      />

      <div className={styles.body}>
        {/* Loading/error/empty are first-class states (D22) — the convention AccountsPage and
            DashboardPage already follow and both transfer wizards did not. Without this a failed
            accounts load left the page standing with empty pickers and no explanation, so a
            transient network fault silently blocked transfers. */}
        {accountsProblem && (
          <MessageBar intent="error">
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
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
        {inFlight && (
          <MessageBar intent="info">
            <MessageBarBody>Still processing — tap Send again to check.</MessageBarBody>
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
                }}
                belowSlot={
                  <Text className={styles.subtle}>
                    Available: {formatCurrency(availableBalance)}
                  </Text>
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
                onClick={() => void handleSubmit(onValid)()}
                disabled={isSubmitting}
              >
                {isSubmitting ? <Spinner size="tiny" /> : `Send ${formatCurrency(amountNumber)}`}
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
    </div>
  );
}

export default InternalTransferPage;
