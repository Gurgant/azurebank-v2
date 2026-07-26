import { makeStyles, Skeleton, SkeletonItem, tokens } from '@fluentui/react-components';
import { colors, componentSizes } from '../../theme/tokens';
import { useReducedMotion } from '../../hooks/useReducedMotion';

/**
 * A placeholder shaped like the transaction rows it stands in for.
 *
 * The point is not that something appears while loading — a spinner does that. The point is that
 * **nothing moves when the data arrives**. A spinner in a 76px box followed by five 77px rows pushes
 * everything below it down by roughly 300px, and the reader loses their place at the exact moment
 * they started reading. So every measurement here is taken from `TransactionItem`, not chosen:
 * 16px padding, 12px gap, a 44px icon, two text lines at 15px and 13px, and the same 1px divider.
 *
 * Rendering it inside a real `<Skeleton>` is what gives the group one `aria-busy` region and one
 * label, instead of five unlabelled boxes that a screen reader would read out individually.
 */

const useStyles = makeStyles({
  row: {
    display: 'flex',
    alignItems: 'center',
    padding: '16px',
    gap: '12px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${colors.neutral[100]}`,
  },
  icon: {
    width: componentSizes.iconContainer.md.size,
    height: componentSizes.iconContainer.md.size,
    borderRadius: componentSizes.iconContainer.md.radius,
    flexShrink: 0,
  },
  // Takes the remaining width so the amount column stays right-aligned exactly as it does with
  // real data.
  text: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    flex: 1,
    minWidth: 0,
  },
  amount: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    alignItems: 'flex-end',
    flexShrink: 0,
  },
  lineDescription: { height: '15px', width: '60%' },
  lineDate: { height: '13px', width: '35%' },
  lineAmount: { height: '15px', width: '72px' },
  lineType: { height: '12px', width: '48px' },
});

export interface TransactionListSkeletonProps {
  /** How many rows the real list will render. The dashboard asks for exactly five. */
  rows: number;
  /** Announced while the region is busy. */
  label?: string;
}

export function TransactionListSkeleton({
  rows,
  label = 'Loading transactions',
}: TransactionListSkeletonProps) {
  const styles = useStyles();
  const reducedMotion = useReducedMotion();

  return (
    <Skeleton
      aria-label={label}
      aria-busy
      // A shimmer is motion, and a placeholder is the last thing that should insist on moving for
      // someone who asked the OS to stop. `pulse` is the calmer of Fluent's two; reduced motion
      // gets it, everyone else gets the wave.
      animation={reducedMotion ? 'pulse' : 'wave'}
    >
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className={styles.row}>
          <SkeletonItem shape="circle" className={styles.icon} />
          <div className={styles.text}>
            <SkeletonItem className={styles.lineDescription} />
            <SkeletonItem className={styles.lineDate} />
          </div>
          <div className={styles.amount}>
            <SkeletonItem className={styles.lineAmount} />
            <SkeletonItem className={styles.lineType} />
          </div>
        </div>
      ))}
    </Skeleton>
  );
}
