import { Button, Text } from '@fluentui/react-components';
import { CheckmarkCircle24Filled } from '@fluentui/react-icons';
import { PageHeader } from '../layout';
import type { FoundMovement } from '../../api/movementByKey';
import { useFocusWhenLost } from '../../hooks/useFocusWhenLost';
import { useSettledPress } from '../../hooks/useSettledPress';
import { useTransferWizardStyles } from '../../pages/transferWizardStyles';
import { formatCurrency, formatDateTime } from '../../utils/format';

/**
 * The screen a transfer shows when the server has said the payment went through and could not
 * return its receipt: 409 `IDEMPOTENCY_RESULT_UNKNOWN` with `applied: true` (ADR-0009).
 *
 * It stands where the receipt would: the receipt's title and icon, and the receipt's two ways on,
 * "View History" and "Done". In place of the amount, the payee and the new balance, which the
 * answer does not carry, it has one sentence.
 *
 * **It has no "start over", and no prop one could be wired to.** `ResultUnknownView` offers "It
 * didn't go through" because there nobody knows; here the server does, and a new key over a
 * committed payment is a second payment. Nor a Back or a Close: `PageHeader` is given neither
 * handler, because there is no form behind this view to go back to.
 *
 * **It does not navigate.** Both actions are handed in, the rule `ResultUnknownView`, `PageHeader`
 * and `MoneyDialogShell` follow: a shared component takes a handler and calls it, and the owning
 * flow decides the destination.
 *
 * **Focus lands on the sentence so that it is read.** The control that sent was disabled during
 * the send, so focus is on `body` when this view mounts, and a screen reader would say nothing.
 * The sentence takes that lost focus (`useFocusWhenLost`), to be heard before the actions, and
 * one Tab reaches "View History". It is a paragraph, not a heading; it is in no alert, status or
 * live region, which would be a second reading; and it describes neither button, which would be
 * another at the first Tab. That is the reasoning; what a screen reader was heard to say is in
 * ADR-0059's Validation.
 *
 * It is also the view for a send whose answer was lost and whose movement the listing then found
 * by its key (`useMovementCheck`). It then shows that movement under the sentence: its amount,
 * signed as the flow's receipt signs it, its reference and its time. Never its key.
 */
export interface WentThroughViewProps {
  /** The flow's success title, the receipt's own. */
  title: string;
  /** What went through, in the flow's words. */
  sentence: string;
  /** Take the visitor to their transactions. Routed through the flow. */
  onViewHistory: () => void;
  /** Take the visitor home. Routed through the flow. */
  onDone: () => void;
  /** The movement the listing found by the send's key. Without it, the sentence alone. */
  movement?: FoundMovement;
  /** How the flow's receipt signs the amount: `-` for money that left, nothing for a move. */
  sign?: '-' | '';
}

export function WentThroughView({
  title,
  sentence,
  onViewHistory,
  onDone,
  movement,
  sign = '',
}: WentThroughViewProps) {
  const styles = useTransferWizardStyles();
  // Mounted to be shown, so it is on screen from the first render.
  const sentenceRef = useFocusWhenLost<HTMLParagraphElement>(true);
  // A press on the actions waits until the view has settled, as in the money dialogs. The view
  // takes another's place, so a press made as it appears was meant for that one: the check view's
  // when the listing found the movement, and the PIN step's when the answer said `applied: true`.
  const settledPress = useSettledPress(true, '');

  return (
    <div className={styles.page}>
      {/* No onBack, no onClose: see the note above. */}
      <PageHeader title={title} />
      <div className={styles.body}>
        <div className={styles.centeredView}>
          <div className={styles.successIcon}>
            <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
          </div>
          <Text as="p" className={styles.outcomeSentence} ref={sentenceRef} tabIndex={-1}>
            {sentence}
          </Text>
          {movement && (
            <>
              <Text className={styles.successAmount}>
                {sign}
                {formatCurrency(movement.amount)}
              </Text>
              <div className={styles.reviewCard} style={{ width: '100%' }}>
                <div className={styles.reviewRow}>
                  <Text className={styles.reviewLabel}>Reference</Text>
                  <Text className={styles.reviewValue}>{movement.transactionNumber}</Text>
                </div>
                <div className={styles.reviewRow}>
                  <Text className={styles.reviewLabel}>Date &amp; time</Text>
                  <Text className={styles.reviewValue}>{formatDateTime(movement.createdAt)}</Text>
                </div>
              </div>
            </>
          )}
        </div>
        <div className={styles.actions} {...settledPress.actions}>
          <Button
            appearance="primary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={onViewHistory}
          >
            View History
          </Button>
          <Button
            appearance="secondary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={onDone}
          >
            Done
          </Button>
        </div>
      </div>
    </div>
  );
}

export default WentThroughView;
