import { Button, Text } from '@fluentui/react-components';
import { Warning24Regular } from '@fluentui/react-icons';
import { PageHeader } from '../layout';
import { useTransferWizardStyles } from '../../pages/transferWizardStyles';
import { CHECK_AGAIN } from '../../api/problemMessages';
import { useFocusWhenLost } from '../../hooks/useFocusWhenLost';
import type { MovementCheck } from '../../hooks/useMovementCheck';
import { useSettledPress } from '../../hooks/useSettledPress';
import { MovementCheckStatus } from './MovementCheckStatus';

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
 * them retrying blindly could send it again. The buttons below are the only ways forward, and each
 * forces the user to confront the ambiguity.
 *
 * **It does not navigate or reset by itself.** Every action is handed in. That is the same rule
 * `PageHeader` and `MoneyDialogShell` state: a shared component takes a handler and calls it, and
 * never decides a destination — which is why moving this view here cannot quietly change what
 * "start over" means. `onStartOver` in particular must re-arm the idempotency intent, and only the
 * owning flow's wizard can do that.
 *
 * **Focus lands on its title so that it is read.** The PIN boxes are disabled during the send and
 * gone with their step when this view mounts, so focus is on `body`, and a screen reader would
 * say nothing. The title takes that lost focus (`useFocusWhenLost`), as `WentThroughView`'s
 * sentence does, and for the same reasons it is a paragraph in no alert, status or live region,
 * and describes no button. It lands before the listing by key has answered: each verdict is said
 * by the status region of `MovementCheckStatus`.
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
  /**
   * The listing by key, asked for the send whose answer was lost (`useMovementCheck`). Given, the
   * view says what it found in a status region of its own, offers "Check again" while asking can
   * still tell more, and draws "start over" only once nothing sent under the key can commit any
   * more. Without it, or while it is `off`, the view is the one it always was.
   */
  check?: MovementCheck;
}

export function ResultUnknownView({
  title,
  repeatWarning,
  onCheckTransactions,
  onStartOver,
  check,
}: ResultUnknownViewProps) {
  const styles = useTransferWizardStyles();
  const asking = check !== undefined && check.verdict !== 'off';
  const landing = check?.landingRef;
  const fullWidth = { width: '100%', height: '48px' };
  // Mounted to be shown, so it is on screen from the first render.
  const titleRef = useFocusWhenLost<HTMLParagraphElement>(true);
  // A press on the actions waits until what the view draws has settled, as in the money dialogs:
  // each verdict, and the new-key action at the bound, moves its buttons (`useSettledPress`).
  // "Start over" takes this view away and brings back the form step, whose account cards, quick
  // amounts and "Use max" change what the visitor goes on to review. This view's wait goes with
  // it, so the page that owns the form gives that form a wait of its own as the view goes
  // (`afterLeaving` on TransferPage and InternalTransferPage).
  const settledPress = useSettledPress(
    true,
    `${check?.verdict ?? 'off'} ${check?.canCheckAgain ?? true} ${check?.pastLive ?? false}`,
  );

  const checkTransactions = (primary: boolean) => (
    <Button
      key="check-transactions"
      ref={primary ? landing : undefined}
      appearance={primary ? 'primary' : 'secondary'}
      size="large"
      style={fullWidth}
      onClick={onCheckTransactions}
    >
      Check recent transactions
    </Button>
  );
  const startOver = (
    <Button
      key="start-over"
      appearance="secondary"
      size="large"
      style={fullWidth}
      onClick={onStartOver}
    >
      It didn&apos;t go through — start over
    </Button>
  );

  return (
    <div className={styles.page}>
      {/* No onBack, no onClose — see the note above. This is deliberate, not an omission. */}
      <PageHeader title={title} />
      <div className={styles.body}>
        <div className={styles.centeredView}>
          <div className={styles.warningIcon}>
            <Warning24Regular style={{ width: '40px', height: '40px' }} />
          </div>
          <Text as="p" className={styles.outcomeSentence} ref={titleRef} tabIndex={-1}>
            {check?.verdict === 'none' ? (
              <>Your transfer didn&apos;t go through</>
            ) : (
              <>We couldn&apos;t confirm your transfer</>
            )}
          </Text>
          {/* Hidden once the listing says it did not go through: "may or may not" is no longer
              true. */}
          {check?.verdict !== 'none' && (
            <Text className={styles.stateBody}>
              The request may or may not have gone through. Check your recent transactions before
              trying again — retrying blindly could {repeatWarning}.
            </Text>
          )}
          {asking && <MovementCheckStatus check={check} />}
        </div>
        <div className={styles.actions} {...settledPress.actions}>
          {!asking ? (
            <>
              {checkTransactions(true)}
              {startOver}
            </>
          ) : check.checkAgainFirst ? (
            <>
              <Button
                key="check-again"
                ref={landing}
                appearance="primary"
                size="large"
                style={fullWidth}
                disabled={check.checking}
                onClick={check.checkAgain}
              >
                {CHECK_AGAIN}
              </Button>
              {checkTransactions(false)}
            </>
          ) : (
            <>
              {checkTransactions(true)}
              {check.offerCheckAgain && (
                <Button
                  key="check-again"
                  appearance="secondary"
                  size="large"
                  style={fullWidth}
                  disabled={check.checking}
                  onClick={check.checkAgain}
                >
                  {CHECK_AGAIN}
                </Button>
              )}
              {check.offerNewKey && startOver}
            </>
          )}
        </div>
      </div>
    </div>
  );
}

export default ResultUnknownView;
