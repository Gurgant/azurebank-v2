import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import {
  MOVEMENT_NONE,
  MOVEMENT_NOT_YET,
  MOVEMENT_UNCHECKED,
  MOVEMENT_UNCHECKED_NOT_YET,
} from '../../api/problemMessages';
import type { MovementCheck } from '../../hooks/useMovementCheck';
import { WaitHint } from '../feedback';

const useStyles = makeStyles({
  words: {
    margin: 0,
    fontSize: '15px',
    lineHeight: '1.5',
    textAlign: 'center',
    // Fluent's second foreground, as the words of a wait are (`WaitHint`), and not the palette's
    // `neutral[500]`: these are words to read at body size, and that grey is under 4.5 to 1.
    color: tokens.colorNeutralForeground2,
  },
  // Out of the view's flow while it has no words, as a silent `WaitHint` is: an empty box in a
  // flex column still earns the column's gap.
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
});

/** The verdict's sentence, or nothing while the listing is asked and once the movement is found. */
function movementCheckWords(check: MovementCheck): string {
  switch (check.verdict) {
    case 'notYet':
      return MOVEMENT_NOT_YET;
    case 'none':
      return MOVEMENT_NONE;
    case 'unchecked':
      return check.pastLive ? MOVEMENT_UNCHECKED : MOVEMENT_UNCHECKED_NOT_YET;
    default:
      return '';
  }
}

/**
 * What the check view says once it has asked the listing by key (ADR-0022), shared by the
 * transfer pages' `ResultUnknownView` and the two money dialogs, which keep their own markup.
 *
 * A `role="status"` region of its own, on screen and empty from the view's first render: a polite
 * region has to exist before its text changes, or the change is not read. Every verdict follows an
 * ask, which empties it, so each is said once. The view lands no focus, and this is how its
 * verdict is heard. Never inside an alert, nor pointed at by an `aria-describedby`, which would
 * read it a second time.
 *
 * The read's `WaitHint` is its sibling, not its child: the hint has a region of its own, and two
 * nested regions would read one sentence twice. It offers no "Stop waiting": "Check recent
 * transactions" and the dialog's X are the ways out of the read.
 */
export function MovementCheckStatus({ check }: { check: MovementCheck }) {
  const styles = useStyles();
  const words = movementCheckWords(check);
  return (
    <>
      <div
        role="status"
        aria-atomic="true"
        className={mergeClasses(styles.words, words === '' && styles.silent)}
      >
        {words}
      </div>
      <WaitHint active={check.checking} kind="read" />
    </>
  );
}
