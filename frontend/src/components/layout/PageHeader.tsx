import { useLocation } from 'react-router-dom';
import { makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { ChevronLeft24Regular, Dismiss24Regular } from '@fluentui/react-icons';
import { colors, componentSizes, surfaces, transitions } from '../../theme/tokens';
import { findActiveNavPlace } from './navItems';

/**
 * The bar at the top of a page: a title, and at most one affordance on each side.
 *
 * Six instances across four pages were written by hand, in three shapes that did not agree —
 * `height: 56px` with `padding: 0 16px` on History, the same height with `0 12px` on both transfer
 * wizards, and no height at all with `12px 16px` and left-aligned content on the transaction
 * detail. (Accounts' mobile header is deliberately NOT here: it is a brand-coloured page hero laid
 * out in a column, a different species that happens to sit at the top.)
 *
 * **This component never navigates.** It takes handlers and calls them; it imports no `useNavigate`
 * and synthesises no destination. That is not stylistic — the transfer wizards' Back and Close
 * carry `disabled={keyLive}`, which is the anti-double-spend guard, and every exit routes through
 * their own `requestLeave`. A header that decided for itself where "back" goes would walk straight
 * through that guard, and nothing would fail until somebody sent money twice. `useLocation` is read
 * (never `useNavigate`) only to look up a nav title.
 *
 * Three shapes fall out of one component:
 *
 *  - **A place** — a nav destination. No affordances: a tab has no parent, and the navigation that
 *    got you here is on screen and lit. Omit `title` too, and it is read from the same table the
 *    nav reads, so the two cannot drift apart.
 *  - **A leaf** — a page inside a section. One back affordance, pointing at the section root, which
 *    the caller supplies.
 *  - **A wizard** — outside the app shell, so this bar is the only way out. Both affordances, each
 *    independently disable-able, and both driven entirely by the caller.
 *
 * Wizards also have states with NO exits at all — the success receipt, and the "we couldn't confirm
 * your transfer" screen where leaving without checking could mean sending twice. Passing neither
 * handler produces exactly that, with the title still centred.
 */

const useStyles = makeStyles({
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    height: componentSizes.header.height,
    padding: '0 12px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${surfaces.border}`,
  },

  /**
   * Both edges reserve their width whether or not a control renders, which is what keeps the title
   * optically centred rather than centred-if-you-are-lucky.
   *
   * The hand-rolled version got this wrong in one place: the transfer success header spaced its
   * right edge with a 40px span and its left with a bare `<span />`, so the title sat 40px off
   * centre on the one screen a user reads most carefully.
   */
  slot: {
    width: '40px',
    flexShrink: 0,
    display: 'flex',
    alignItems: 'center',
  },

  slotEnd: {
    justifyContent: 'flex-end',
  },

  control: {
    width: '40px',
    height: '40px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    borderRadius: '8px',
    color: colors.neutral[800],
    transition: `background-color ${transitions.fast}`,

    ':hover': { backgroundColor: colors.neutral[100] },
    ':disabled': { opacity: 0.4, cursor: 'not-allowed' },
    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '-2px',
    },
  },

  title: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
    textAlign: 'center',
    // The title yields before the two 40px edges do, so a long one truncates instead of shoving
    // the affordances off the bar.
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
});

export interface PageHeaderProps {
  /**
   * Omit on a nav destination: the title is then read from the same table the navigation reads, so
   * a page and its nav item cannot come to disagree about what the place is called.
   */
  title?: string;
  /** Back. Omit on a place — a tab has no parent. The caller decides where it goes. */
  onBack?: () => void;
  backDisabled?: boolean;
  backLabel?: string;
  /** Close. Wizards only, where this bar is the only way out. */
  onClose?: () => void;
  closeDisabled?: boolean;
  closeLabel?: string;
}

export function PageHeader({
  title,
  onBack,
  backDisabled = false,
  backLabel = 'Back',
  onClose,
  closeDisabled = false,
  closeLabel = 'Close',
}: PageHeaderProps) {
  const styles = useStyles();
  const { pathname } = useLocation();
  const heading = title ?? findActiveNavPlace(pathname)?.label ?? '';

  return (
    <div className={styles.header}>
      <div className={styles.slot}>
        {onBack && (
          <button
            type="button"
            className={styles.control}
            aria-label={backLabel}
            disabled={backDisabled}
            onClick={onBack}
          >
            <ChevronLeft24Regular />
          </button>
        )}
      </div>

      <Text className={styles.title}>{heading}</Text>

      <div className={mergeClasses(styles.slot, styles.slotEnd)}>
        {onClose && (
          <button
            type="button"
            className={styles.control}
            aria-label={closeLabel}
            disabled={closeDisabled}
            onClick={onClose}
          >
            <Dismiss24Regular />
          </button>
        )}
      </div>
    </div>
  );
}

export default PageHeader;
