import { Children, type ReactNode } from 'react';
import { makeStyles } from '@fluentui/react-components';

const useStyles = makeStyles({
  // Out of the page's flow while it is empty, still in the accessibility tree: an empty box in a
  // flex column would still earn the column's gap. Hidden with `display: none` it would be out of
  // that tree, and filling it would be the insertion this slot exists to avoid. Margin is 0: a
  // quoted spacing literal is refused in a file that is new.
  empty: {
    position: 'absolute',
    width: '1px',
    height: '1px',
    margin: 0,
    padding: 0,
    border: 0,
    overflow: 'hidden',
    clip: 'rect(0 0 0 0)',
    whiteSpace: 'nowrap',
  },
});

interface StatusSlotProps {
  /** What an action did, while there is a sentence; nothing otherwise. */
  children?: ReactNode;
  className?: string;
}

/**
 * The polite `role="status"` a page keeps for what an action did, in the page from the start and
 * empty until there is a sentence.
 *
 * A screen reader reads a change to a live region. A status added to the page with its words
 * already in it is not reliably read, so the slot is always there and the sentence is put into
 * it. Atomic, so the whole sentence is read rather than the part that changed.
 *
 * A failure never goes in it. A failure is an alert, and everything inside this region is read
 * as one polite status.
 */
export function StatusSlot({ children, className }: StatusSlotProps) {
  const styles = useStyles();
  const empty = Children.toArray(children).length === 0;

  return (
    <div role="status" aria-atomic="true" className={empty ? styles.empty : className}>
      {children}
    </div>
  );
}
