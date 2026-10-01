import { Button, Text } from '@fluentui/react-components';
import { CheckmarkCircle24Filled } from '@fluentui/react-icons';
import { PageHeader } from '../layout';
import { useFocusWhenLost } from '../../hooks/useFocusWhenLost';
import { useTransferWizardStyles } from '../../pages/transferWizardStyles';

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
 * **The sentence is read because focus lands on it.** The control that sent was disabled during
 * the send, so focus is on `body` when this view mounts, and a screen reader would say nothing.
 * The sentence takes that lost focus (`useFocusWhenLost`), so it is heard before the actions, and
 * one Tab reaches "View History". It is a paragraph, not a heading; it is in no alert, status or
 * live region, which would read it a second time; and it describes neither button, which would
 * read it again at the first Tab.
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
}

export function WentThroughView({ title, sentence, onViewHistory, onDone }: WentThroughViewProps) {
  const styles = useTransferWizardStyles();
  // Mounted to be shown, so it is on screen from the first render.
  const sentenceRef = useFocusWhenLost<HTMLParagraphElement>(true);

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
        </div>
        <div className={styles.actions}>
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
