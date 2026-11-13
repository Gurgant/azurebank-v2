import { makeStyles, Text, Button, Spinner, tokens } from '@fluentui/react-components';
import { ArrowSwap24Regular } from '@fluentui/react-icons';
import { Controller, type Control } from 'react-hook-form';
import { colors } from '../../theme/tokens';
import { useTransferWizardStyles } from '../transferWizardStyles';
import { AlertSlot, WaitHint } from '../../components/feedback';
import { AmountField } from '../../components/form/AmountField';
import { formatCurrency, maskAccountNumber } from '../../utils/format';
import type { AccountResponse } from '../../features/api/apiSlice';
import type { TransferFormOutput, TransferFormValues } from '../../forms/moneySchemas';
import type { useSettledPress } from '../../hooks/useSettledPress';
import type { useRecipientCheck } from './useRecipientCheck';

const QUICK_AMOUNTS = [10, 25, 50, 100, 250];

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
    // A plain <input> the stylesheet gives no background is painted the browser's own, which is
    // white in either theme, and its placeholder the browser's grey. The text colour above
    // follows the theme, so in the dark theme the typed handle was near-white on white. Both are
    // the tokens Fluent's own fields take, so this one sits on the same ground as theirs.
    backgroundColor: tokens.colorNeutralBackground1,
    '::placeholder': { color: tokens.colorNeutralForeground4 },
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

/**
 * The transfer page's form step: the source account, the recipient and its check, the amount and
 * the way to the review. It holds no state of its own; the page and `useRecipientCheck` own it.
 */
export interface TransferFormStepProps {
  settledPress: ReturnType<typeof useSettledPress>;
  accounts: AccountResponse[];
  selectedAccount: AccountResponse | null;
  handleSelectAccount: (account: AccountResponse) => void;
  control: Control<TransferFormValues, unknown, TransferFormOutput>;
  keyLive: boolean;
  onBodyEdit: () => void;
  watchedTag: string;
  recipientCheck: ReturnType<typeof useRecipientCheck>;
  availableBalance: number;
  handleQuickAmount: (value: number) => void;
  amountNumber: number;
  canReview: boolean;
  reviewWaitsFor: string | null;
  reviewHintId: string;
  onReview: () => void;
  requestLeave: (to: string) => void;
}

export function TransferFormStep({
  settledPress,
  accounts,
  selectedAccount,
  handleSelectAccount,
  control,
  keyLive,
  onBodyEdit,
  watchedTag,
  recipientCheck,
  availableBalance,
  handleQuickAmount,
  amountNumber,
  canReview,
  reviewWaitsFor,
  reviewHintId,
  onReview,
  requestLeave,
}: TransferFormStepProps) {
  // Merged so the markup keeps addressing one `styles` object.
  const styles = { ...useTransferWizardStyles(), ...useRecipientStyles() };
  const {
    lookupState,
    lookupLanding: { landingRef },
    recipient,
    recipientError,
    stopWaitingForLookup,
    handleVerifyRecipient,
    editHandle,
  } = recipientCheck;

  return (
    <>
      {/* From account */}
      <div {...settledPress.content}>
        <Text className={styles.sectionLabel}>From</Text>
        <div style={{ display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '8px' }}>
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
      <div {...settledPress.content}>
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
                // The field keeps what was typed and the page sends it lower-cased, so a phone must not capitalise or correct it.
                autoCapitalize="none"
                autoCorrect="off"
                spellCheck={false}
                ref={field.ref}
                name={field.name}
                value={field.value}
                disabled={keyLive}
                onBlur={field.onBlur}
                onChange={(e) => editHandle(e.target.value, field.onChange)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') void handleVerifyRecipient(watchedTag);
                }}
              />
            )}
          />
          <Button
            ref={landingRef}
            appearance="secondary"
            onClick={() => void handleVerifyRecipient(watchedTag)}
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
      <div className={styles.amountSection} {...settledPress.content}>
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
              <Text className={styles.subtle}>Available: {formatCurrency(availableBalance)}</Text>
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
      <div className={styles.quickAmounts} {...settledPress.content}>
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

      <div className={styles.actions} {...settledPress.actions}>
        <Button
          appearance="primary"
          size="large"
          style={{ width: '100%', height: '48px' }}
          onClick={() => onReview()}
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
  );
}
