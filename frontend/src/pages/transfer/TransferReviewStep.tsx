import { Text, Button, Spinner } from '@fluentui/react-components';
import { useTransferWizardStyles } from '../transferWizardStyles';
import { WaitHint } from '../../components/feedback';
import { formatCurrency } from '../../utils/format';
import type { AccountResponse } from '../../features/api/apiSlice';
import type { Recipient } from './useRecipientCheck';

/**
 * The transfer page's review step: what is about to be sent, and the funds check on Continue. It
 * holds no state; the check's wait (`checkingFunds`) and `onReviewed` are the page's.
 */
export interface TransferReviewStepProps {
  selectedAccount: AccountResponse | null;
  recipient: Recipient | null;
  amountNumber: number;
  newBalance: number;
  onReviewed: () => Promise<void>;
  isSubmitting: boolean;
  checkingFunds: boolean;
  keyLive: boolean;
  onBack: () => void;
}

export function TransferReviewStep({
  selectedAccount,
  recipient,
  amountNumber,
  newBalance,
  onReviewed,
  isSubmitting,
  checkingFunds,
  keyLive,
  onBack,
}: TransferReviewStepProps) {
  const styles = useTransferWizardStyles();

  return (
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
          onClick={onBack}
          disabled={keyLive}
        >
          Back
        </Button>
      </div>
      {/* The funds check is a read, but it offers no Stop: it fails open, so a stopped check
          would only move on to the PIN step with the check skipped. */}
      <WaitHint active={checkingFunds} kind="read" />
    </>
  );
}
