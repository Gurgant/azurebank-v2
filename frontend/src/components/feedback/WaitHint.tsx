import { Button, makeStyles, Text, tokens } from '@fluentui/react-components';
import { STOP_WAITING, WAIT_SLOW, WAIT_STILL_TRYING } from '../../api/problemMessages';
import { useWaitPhase } from '../../hooks/useWaitPhase';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  words: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
});

interface WaitHintProps {
  /** The host's own waiting flag: the hint exists only while it is true. */
  active: boolean;
  /**
   * A read may be given up on; a write may not. Stopping a write the server may already be doing
   * does not stop the server, it only hides the answer — and for a money send, drops the one key
   * that could check it.
   */
  kind: 'read' | 'write';
  /** Offered as "Stop waiting" from 20 s, on a read only. Without it, a read has no Stop. */
  onStopWaiting?: () => void;
}

/**
 * The words under a wait: nothing for 5 s, then "Taking longer than usual…", then from 20 s
 * "Still trying…" (ADR-0059).
 *
 * Each host renders its own, under the control or spinner that started the wait, because a Fluent
 * modal hides everything outside it from assistive technology: one page-level hint would be
 * silent exactly inside the dialogs where a money send waits. Never place it inside a
 * `role="alert"` or inside an element another one's `aria-describedby` points at, or its words are
 * read as part of those.
 *
 * The words live in a `role="status"` region of their own — not `RetryCountdown`'s timer, not an
 * alert — that is in the page, empty, from the first instant of the wait: a polite region has to
 * exist before its text changes, or the change is not read. It is gone the moment nothing is
 * pending, so a page at rest has no status region to find.
 *
 * "Stop waiting" follows the words, outside the region so that what the region says is the words
 * alone, at Fluent's medium size rather than the small one: it is the one control a visitor
 * reaches for in the middle of a wait, so it gets the larger target. The row wraps on a narrow
 * screen instead of pushing the page sideways.
 */
export function WaitHint({ active, kind, onStopWaiting }: WaitHintProps) {
  const styles = useStyles();
  const phase = useWaitPhase(active);

  if (!active) return null;

  const words = phase === 'slow' ? WAIT_SLOW : phase === 'stillTrying' ? WAIT_STILL_TRYING : '';
  const offerStop = kind === 'read' && phase === 'stillTrying' && onStopWaiting !== undefined;

  return (
    <div data-wait-hint="" className={styles.root}>
      <Text role="status" className={styles.words}>
        {words}
      </Text>
      {offerStop && (
        <Button appearance="secondary" size="medium" onClick={onStopWaiting}>
          {STOP_WAITING}
        </Button>
      )}
    </div>
  );
}
