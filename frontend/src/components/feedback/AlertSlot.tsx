import { Children, type ReactNode } from 'react';
import { makeStyles } from '@fluentui/react-components';

const useStyles = makeStyles({
  // Out of the page's flow while it is empty, still in the accessibility tree: an empty box in a
  // flex column would still earn the column's gap. Hidden with `display: none` it would be out of
  // that tree, and filling it would be the insertion this slot exists to avoid.
  empty: {
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

interface AlertSlotProps {
  /** The read's error bar (or bars) while it has failed; nothing otherwise. */
  children?: ReactNode;
  className?: string;
}

/**
 * The `role="alert"` a read page keeps for its error bar, in the page from the start and empty
 * until the read fails (ADR-0059).
 *
 * A screen reader reads a CHANGE to a live region. An alert added to the page with its words
 * already in it is announced by most screen readers, says the APG, and "generally" not, says MDN;
 * W3C's technique ARIA19 keeps the container in the page from the start and puts the message into
 * it. So the slot is always there, and the bar inside it — a `MessageBar` with no role of its own,
 * or the two would be nested live regions — comes and goes: in on a failure, out while the read
 * runs again, in again if that fails too. Each of those is a change of this one region.
 *
 * Atomic, so the whole bar is read rather than the part that changed, and a page with two bars
 * puts both into one slot: two alerts raised in one render may be read as one, since a screen
 * reader may drop what is queued when an assertive change arrives.
 *
 * Never put anything in it that is not a failure: everything inside is read as one alert.
 */
export function AlertSlot({ children, className }: AlertSlotProps) {
  const styles = useStyles();
  const empty = Children.toArray(children).length === 0;

  return (
    <div role="alert" aria-atomic="true" className={empty ? styles.empty : className}>
      {children}
    </div>
  );
}
