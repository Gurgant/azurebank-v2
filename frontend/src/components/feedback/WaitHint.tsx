import { Button, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import {
  STOP_WAITING,
  WAIT_KEEP_PAGE_OPEN,
  WAIT_LOADED,
  WAIT_SLOW,
  WAIT_STILL_TRYING,
} from '../../api/problemMessages';
import { useWaitPhase } from '../../hooks/useWaitPhase';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  // Out of the page's flow, still read. An empty box in the flow still moves the page: in a flex
  // column it earns the column's gap. Measured in Chromium on 2026-09-30, before this rule: the
  // text under the sign-in form sat 8 px lower while a sign-in was pending.
  silent: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    margin: '-1px',
    padding: 0,
    border: 0,
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  },
  words: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
});

interface WaitHintProps {
  /**
   * The host's own waiting flag: the hint exists while it is true, and for a moment after a read
   * that said something loads (`failed`).
   */
  active: boolean;
  /**
   * A read may be given up on; a write may not. Stopping a write the server may already be doing
   * does not stop the server, it only hides the answer — and for a money send, drops the one key
   * that could check it. A money send (a transfer, a move between the visitor's own accounts, a
   * deposit, a withdrawal) is the write whose "Still trying…" asks the visitor to keep the page
   * open: the key lives only in this page, and a reload would send the money with a new one.
   */
  kind: 'read' | 'write' | 'moneySend';
  /** Offered as "Stop waiting" from 20 s, on a read only. Without it, a read has no Stop. */
  onStopWaiting?: () => void;
  /**
   * For a read whose content takes the wait's place on the page: whether it failed. Given, a wait
   * that said something and ends without failing says "Loaded." in its region, read and not
   * shown, before the region goes; a failure says nothing more, because its alert speaks. Left
   * out — a check that moves the visitor on rather than putting something on the page — the
   * region goes when the wait ends.
   */
  failed?: boolean;
  /**
   * The host's spacing, such as a margin from the control above. It takes effect once the hint has
   * words: while it is silent the hint is out of the page's flow, and so is any margin given here.
   */
  className?: string;
}

/**
 * The words under a wait: nothing for 5 s, then "Taking longer than usual…", then from 20 s
 * "Still trying…", or on a money send "Still trying… Keep this page open." (ADR-0059). A read
 * that said either and then loads says "Loaded.", unseen.
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
 * pending, so a page at rest has no status region to find — except for the moment after a read
 * that said something loads: the same region then says "Loaded." (WCAG 4.1.3's "system
 * available"), hidden like an empty one, and goes `LOADED_KEPT_MS` later.
 *
 * Until it has words it takes no room: it is kept out of the page's flow, visually hidden but still
 * read, so a wait that ends inside 5 s moves nothing on the page. Only a wait that reaches its
 * words moves what is below it, once, to make room for them.
 *
 * "Stop waiting" follows the words, outside the region so that what the region says is the words
 * alone, at Fluent's medium size rather than the small one: it is the one control a visitor
 * reaches for in the middle of a wait, so it gets the larger target. The row wraps on a narrow
 * screen instead of pushing the page sideways.
 */
export function WaitHint({ active, kind, onStopWaiting, failed, className }: WaitHintProps) {
  const styles = useStyles();
  const phase = useWaitPhase(active);
  const loaded = phase === 'ended' && kind === 'read' && failed === false;

  if (!active && !loaded) return null;

  const words = loaded
    ? WAIT_LOADED
    : phase === 'slow'
      ? WAIT_SLOW
      : phase === 'stillTrying'
        ? kind === 'moneySend'
          ? WAIT_KEEP_PAGE_OPEN
          : WAIT_STILL_TRYING
        : '';
  const offerStop = kind === 'read' && phase === 'stillTrying' && onStopWaiting !== undefined;

  return (
    <div
      data-wait-hint=""
      className={mergeClasses(styles.root, className, (words === '' || loaded) && styles.silent)}
    >
      {/* Atomic: a change of words is read whole, as the alert regions are. */}
      <Text role="status" aria-atomic="true" className={styles.words}>
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
