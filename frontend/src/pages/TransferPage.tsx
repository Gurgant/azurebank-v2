import { useEffect, useState } from 'react';
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
import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { colors } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import { useTransferWizardStyles } from './transferWizardStyles';
import { ResultUnknownView } from '../components/shared/ResultUnknownView';
import { PageHeader } from '../components/layout/PageHeader';
import {
  useGetAccountsQuery,
  useLazyLookupRecipientQuery,
  useTransferMutation,
  type AccountResponse,
} from '../features/api/apiSlice';
import { useMoneyWizard } from '../hooks/useMoneyWizard';
import { formatCurrency, formatLockHorizon, maskAccountNumber } from '../utils/format';
import {
  normalizeAzureTag,
  parseAmountInput,
  transferFormSchema,
  type TransferFormOutput,
  type TransferFormValues,
} from '../forms/moneySchemas';
import { AmountField } from '../components/form/AmountField';
import { CONNECTION_FAILED } from '../api/problemMessages';

// ============================================
// CONSTANTS
// ============================================

const QUICK_AMOUNTS = [10, 25, 50, 100, 250];

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
 * The six keys that belong to the recipient step — the part of this flow internal transfer does not
 * have. Everything else comes from `useTransferWizardStyles`, which both wizards share.
 */
const useRecipientStyles = makeStyles({
  recipientRow: { display: 'flex', gap: '8px' },
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
    backgroundColor: colors.brand[60],
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
});

// ============================================
// COMPONENT
// ============================================

/**
 * PR-11 — the real external transfer (to another user's primary account by AzureTag), now on
 * RHF+Zod (the money-forms rewrite): step-1's fields (from-account, recipient handle, amount)
 * live in react-hook-form with the balance-capped `transferFormSchema` as resolver, while the
 * VERIFIED-recipient truth stays async component state (the exact-match lookup IS the
 * validator, ADR-0014 — a schema cannot own server truth). The PIN is NOT collected here:
 * submitting a level-2-gated transfer 403s, the root StepUpModal pops via the base-query
 * interceptor, the session elevates, and the SAME request replays (same Idempotency-Key).
 * This page only knows the transfer mutation; step-up is invisible to it.
 */
export function TransferPage() {
  // Merged so the markup keeps addressing one `styles` object.
  const styles = { ...useTransferWizardStyles(), ...useRecipientStyles() };

  const {
    data: accounts = [],
    error: accountsError,
    refetch: refetchAccounts,
  } = useGetAccountsQuery();
  const accountsProblem = accountsError as ApiProblem | undefined;
  const [lookup, lookupState] = useLazyLookupRecipientQuery();
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
  const { step, isSubmitting, inFlight, error, verifyRequired, keyLive, onBodyEdit, requestLeave } =
    wizard;

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
  const availableBalance = selectedAccount?.balance ?? 0;

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
      const result = await lookup(tag).unwrap();
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
  const newBalance = availableBalance - amountNumber;

  const onValid = async (data: TransferFormOutput) => {
    if (!selectedAccount || !recipient) return;
    // Narrowed to a const BEFORE the await, and the receipt is built from that same const. The
    // review screen and the request therefore cannot name two different people.
    const confirmed = recipient;

    const result = await wizard.run({
      fromAccountId: data.fromAccountId,
      recipientAzureTag: confirmed.azureTag,
      amount: data.amount,
    });
    // `run` resolves to undefined when it failed — and it has already set the banner, the in-flight
    // note or the verify view. Under `strict` this early return is not optional: reading
    // `result.newBalance` without it does not compile.
    if (!result) return;

    setSuccess({
      amount: data.amount,
      recipientName: confirmed.displayName,
      recipientAzureTag: confirmed.azureTag,
      newBalance: result.newBalance,
      transactionNumber: result.transactionNumber,
      replayed: result.replayed,
    });
  };

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
        title={step === 'review' ? 'Review Transfer' : 'Send Money'}
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
        {inFlight && (
          <MessageBar intent="info" role="status">
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
              {recipientError && (
                <Text
                  role="alert"
                  className={styles.hint}
                  style={{ marginTop: '8px', display: 'block' }}
                >
                  {recipientError}
                </Text>
              )}
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
              >
                Review Transfer
              </Button>
              <button className={styles.linkBtn} onClick={() => requestLeave('/transfer/internal')}>
                <ArrowSwap24Regular style={{ width: '18px', height: '18px' }} />
                Between your own accounts
              </button>
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
