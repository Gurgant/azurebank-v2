import type { RefObject } from 'react';
import { Text, Button, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { CheckmarkCircle24Filled } from '@fluentui/react-icons';
import { PageHeader } from '../../components/layout/PageHeader';
import { useTransferWizardStyles } from '../transferWizardStyles';
import { formatCurrency } from '../../utils/format';
import type { SuccessData } from './useTransferPin';

/**
 * The transfer page's receipt: the page the visitor lands on once the money has moved. The page
 * returns it early, so the page's hooks and its blocker stay mounted under it; `receiptTitle` is
 * the ref that takes the focus the send lost.
 */
export interface TransferReceiptProps {
  success: SuccessData;
  receiptTitle: RefObject<HTMLParagraphElement | null>;
  requestLeave: (to: string) => void;
}

export function TransferReceipt({ success, receiptTitle, requestLeave }: TransferReceiptProps) {
  const styles = useTransferWizardStyles();

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
          {/* A paragraph that takes focus, in no alert or status: one landing, one reading. */}
          <Text as="p" className={styles.successTitle} ref={receiptTitle} tabIndex={-1}>
            Transfer Sent!
          </Text>
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
