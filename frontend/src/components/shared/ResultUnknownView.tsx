import { Button, Text } from '@fluentui/react-components';
import { Warning24Regular } from '@fluentui/react-icons';
import { PageHeader } from '../layout';
import { useTransferWizardStyles } from '../../pages/transferWizardStyles';

/**
 * The screen a money flow shows when the server cannot say whether the money moved.
 *
 * Both transfer wizards rendered this, 41 lines each, differing in exactly two: the header title
 * and four words of copy. It is the highest-stakes screen in the whole flow — the one standing
 * between a user and a double-spend — so two copies of it was the least defensible duplication in
 * the pair.
 *
 * **It renders no exits, and that is the feature.** No Back, no Close, no Send. `PageHeader` is
 * given neither `onBack` nor `onClose`, so the bar has nothing to click. A stray exit here would
 * let someone wander off without checking whether their money moved, and the copy on screen tells
 * them retrying blindly could send it again. The two buttons below are the only ways forward and
 * both force the user to confront the ambiguity.
 *
 * **It does not navigate or reset by itself.** Both actions are handed in. That is the same rule
 * `PageHeader` and `MoneyDialogShell` state: a shared component takes a handler and calls it, and
 * never decides a destination — which is why moving this view here cannot quietly change what
 * "start over" means. `onStartOver` in particular must re-arm the idempotency intent, and only the
 * owning flow's wizard can do that.
 */
export interface ResultUnknownViewProps {
  /** The flow's own header title, so the user does not appear to have changed screens. */
  title: string;
  /**
   * How this flow describes spending twice — "send twice" for an external transfer, "move the money
   * twice" between own accounts. Passed as words rather than a flow discriminator: the sentence is
   * assembled once, and neither flow can grow a branch the other cannot see.
   */
  repeatWarning: string;
  /** Take the user to their transactions so they can check. Routed through the flow. */
  onCheckTransactions: () => void;
  /** Abandon this intent and start a NEW one — the flow's wizard re-arms the key. */
  onStartOver: () => void;
}

export function ResultUnknownView({
  title,
  repeatWarning,
  onCheckTransactions,
  onStartOver,
}: ResultUnknownViewProps) {
  const styles = useTransferWizardStyles();

  return (
    <div className={styles.page}>
      {/* No onBack, no onClose — see the note above. This is deliberate, not an omission. */}
      <PageHeader title={title} />
      <div className={styles.body}>
        <div className={styles.centeredView}>
          <div className={styles.warningIcon}>
            <Warning24Regular style={{ width: '40px', height: '40px' }} />
          </div>
          <Text className={styles.stateTitle}>We couldn&apos;t confirm your transfer</Text>
          <Text className={styles.stateBody}>
            The request may or may not have gone through. Check your recent transactions before
            trying again — retrying blindly could {repeatWarning}.
          </Text>
        </div>
        <div className={styles.actions}>
          <Button
            appearance="primary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={onCheckTransactions}
          >
            Check recent transactions
          </Button>
          <Button
            appearance="secondary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={onStartOver}
          >
            It didn&apos;t go through — start over
          </Button>
        </div>
      </div>
    </div>
  );
}

export default ResultUnknownView;
