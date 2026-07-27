import { Link, useLocation, useNavigate } from 'react-router-dom';
import { makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { SignOut24Regular } from '@fluentui/react-icons';
import { colors, componentSizes, shadows, surfaces, transitions } from '../../theme/tokens';
import { Avatar } from '../shared/Avatar';
import { Logo } from '../shared/Logo';
import { NAV_PLACES, TRANSFER_ACTION, navCurrent } from './navItems';

// ============================================
// TYPES
// ============================================

export interface SidebarProps {
  /** User's full name */
  userName?: string;
  /** User's email */
  userEmail?: string;
  /** User's avatar URL */
  userAvatar?: string;
  /** Logout handler */
  onLogout?: () => void;
  /** Additional CSS class */
  className?: string;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  /**
   * `minHeight: 100vh` used to live here as well as on the shell container. It was redundant —
   * a flex child stretches to its container by default, and the container is already full height —
   * and it was the wrong unit on a phone. What actually separates this from the content is the
   * fill: chrome is white, the canvas beside it is `surfaces.canvas`.
   *
   * The border had to move with the canvas. It was `neutral[100]`, which is the value the canvas
   * now uses, so it would have gone from invisible against white (1.10:1, measured) to invisible
   * against the canvas — the same defect reflected. `surfaces.border` is a step darker and reads
   * against both.
   *
   * `shadows.sm` stays, but it is not what defines the seam: `0px 1px 2px rgba(0,0,0,0.05)` is
   * offset downward and its 2px blur bleeds about a pixel sideways at five percent alpha, which is
   * below anything the eye resolves against a near-white canvas. The vertical edge is carried by
   * the fill change and the border, not by this.
   */
  sidebar: {
    width: componentSizes.sidebar.width,
    backgroundColor: tokens.colorNeutralBackground1,
    borderRight: `1px solid ${surfaces.border}`,
    display: 'flex',
    flexDirection: 'column',
    boxShadow: shadows.sm,
  },

  // Logo/Brand section
  brand: {
    height: componentSizes.header.height,
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    padding: '0 24px',
    borderBottom: `1px solid ${surfaces.border}`,
  },

  brandText: {
    fontSize: '20px',
    fontWeight: 700,
    color: colors.brand[60],
  },

  // User profile section
  userSection: {
    padding: '20px',
    borderBottom: `1px solid ${surfaces.border}`,
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },

  userInfo: {
    flex: 1,
    minWidth: 0,
  },

  /**
   * `display: block` is load-bearing, not tidying.
   *
   * Fluent's `Text` renders a `<span>`, so these two stacked as `display: inline` and the sidebar
   * read **"Demo Userdemo@azurebank.dev"** — one run-on string, no space, in the app's most
   * permanent piece of chrome. It survived because `text-overflow: ellipsis` and `white-space:
   * nowrap` were already here: the rules that make a block truncate cleanly do nothing on an inline
   * box, so the styling LOOKED complete while the layout it assumed had never applied.
   */
  userName: {
    display: 'block',
    fontSize: '15px',
    fontWeight: 600,
    color: colors.neutral[800],
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  userEmail: {
    display: 'block',
    fontSize: '13px',
    color: colors.neutral[500],
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },

  // Navigation section
  nav: {
    flex: 1,
    padding: '16px 12px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  /**
   * `position: relative` is the rail's anchor and is load-bearing — without it the `::before`
   * below attaches to the nearest positioned ancestor and lands somewhere else entirely.
   *
   * The transition was `all`, which is a declaration that does not say what it does. Only colour
   * changes here, so only colour is named. Nothing in this component transforms or moves, which is
   * why the reduced-motion default costs it nothing.
   */
  navItem: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '12px 16px',
    borderRadius: '10px',
    backgroundColor: 'transparent',
    border: 'none',
    cursor: 'pointer',
    transition: `background-color ${transitions.fast}, color ${transitions.fast}`,
    width: '100%',
    textAlign: 'left',
    textDecoration: 'none',

    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '2px',
    },
  },

  /**
   * The active row: a rail, a filled icon, a heavier label and a DARKER one — four signals, only
   * one of which is colour, so none of them is doing the job alone (WCAG 1.4.1).
   *
   * The brand-tinted pill that used to live here was deleted because it was the defect. Measured
   * from the tokens: `brand[60]` on `brand[120]` is **4.227:1**, and the label is 15px at weight
   * 600 — which WCAG does not count as large text, that starts at 18.66px bold — so the threshold
   * is 4.5:1 and the app's active navigation item was failing 1.4.3. Lightening the pill could not
   * have fixed it: `brand[60]` reaches only 4.868:1 on pure white, so it is marginal by
   * construction.
   *
   * Moving the label to `neutral[900]` gives 17.740:1 against `neutral[600]`'s 7.557:1 for
   * inactive — active is now darker AND heavier, a monotone increase on both axes. The brand stays
   * on the rail and the icon, where 1.4.11's 3:1 for non-text applies and 4.868:1 clears it.
   *
   * There is a second reason, and it is the design one: in a bank the brand colour is scarce and
   * belongs on the control that moves money. After this the sidebar contains exactly one saturated
   * object, and it is Transfer.
   */
  navItemActive: {
    color: colors.neutral[900],

    ':hover': {
      backgroundColor: colors.neutral[100],
    },

    // The rail: 3px across, exactly as tall as the 24px icon it sits beside, on the row's leading
    // edge. A pseudo-element rather than a span — no extra DOM, and invisible to assistive
    // technology by construction rather than by an aria-hidden anyone could drop.
    '::before': {
      content: '""',
      position: 'absolute',
      left: 0,
      top: '50%',
      width: '3px',
      height: '24px',
      transform: 'translateY(-50%)',
      borderRadius: '999px',
      backgroundColor: colors.brand[60],
    },
  },

  navItemInactive: {
    color: colors.neutral[600],

    ':hover': {
      backgroundColor: colors.neutral[100],
      color: colors.neutral[800],
    },
  },

  navIcon: {
    width: '24px',
    height: '24px',
    flexShrink: 0,
  },

  // Fluent icons paint with `fill: currentColor`, so the brand has to be set on the icon's own
  // container — left to inherit, it would take the row's neutral[900].
  navIconActive: {
    color: colors.brand[60],
  },

  navLabel: {
    fontSize: '15px',
    fontWeight: 500,
  },

  navLabelActive: {
    fontWeight: 600,
  },

  /**
   * The ACT: the one saturated object in the chrome, and the only `<button>` among four links.
   *
   * `shadows.sm`, not `shadows.brand` — that token is documented as the lift under the Dashboard's
   * 180px balance card, and a 0/8/24 at 0.3 alpha under a 44px row reads as a floating chip. Flat
   * fill, not `gradients.brand`: a 135° gradient across 44px is imperceptible and spends the app's
   * most distinctive asset on nothing. No lift on hover either — lifts are for cards, not chrome.
   *
   * White on `brand[60]` measures 4.868:1, on the hover stop 6.009:1, on the pressed stop
   * 11.082:1. All clear 4.5:1 for the 15px/600 label.
   */
  transferAction: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '10px',
    width: '100%',
    minHeight: '44px',
    marginBottom: '8px',
    padding: '0 16px',
    borderRadius: '10px',
    border: 'none',
    cursor: 'pointer',
    backgroundColor: colors.brand[60],
    color: tokens.colorNeutralForegroundOnBrand,
    boxShadow: shadows.sm,
    transition: `background-color ${transitions.fast}`,

    ':hover': { backgroundColor: colors.brand[70] },
    ':active': { backgroundColor: colors.brand[40] },
    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '2px',
    },
  },

  transferLabel: {
    fontSize: '15px',
    fontWeight: 600,
    color: tokens.colorNeutralForegroundOnBrand,
  },

  // Footer section
  footer: {
    padding: '16px 12px',
    borderTop: `1px solid ${surfaces.border}`,
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  footerButton: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '12px 16px',
    borderRadius: '10px',
    backgroundColor: 'transparent',
    border: 'none',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    width: '100%',
    textAlign: 'left',
    color: colors.neutral[600],

    ':hover': {
      backgroundColor: colors.neutral[100],
      color: colors.neutral[800],
    },

    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '2px',
    },
  },

  logoutButton: {
    ':hover': {
      backgroundColor: colors.semantic.error.light,
      color: colors.semantic.error.main,
    },
  },
});

// ============================================
// COMPONENT
// ============================================

export function Sidebar({
  userName = 'User',
  userEmail,
  userAvatar,
  onLogout,
  className,
}: SidebarProps) {
  const styles = useStyles();
  const location = useLocation();
  const navigate = useNavigate();

  return (
    <aside className={mergeClasses(styles.sidebar, className)}>
      {/* Brand */}
      <div className={styles.brand}>
        {/* The tile is square but the mark inside it is 4:3, so the visible mark is two thirds of
            this number — 32 puts a 21px mark beside the 20px wordmark. Sizing it to match the text
            optically rather than nominally is why this is not 20. */}
        <Logo size={32} />
        <Text className={styles.brandText}>AzureBank</Text>
      </div>

      {/* User Section */}
      <div className={styles.userSection}>
        <Avatar name={userName} src={userAvatar} size="md" variant="primary" />
        <div className={styles.userInfo}>
          <Text className={styles.userName}>{userName}</Text>
          {userEmail && <Text className={styles.userEmail}>{userEmail}</Text>}
        </div>
      </div>

      {/*
        This landmark holds exactly ONE non-place control. It is a <button>, never a <Link>, and it
        must never carry aria-current — a landmark may contain an action, but it may not contain a
        second thing claiming to be current.

        The places are links because they ARE links: the old <button onClick={navigate}> gave them
        no href in the accessibility tree, and no cmd-click, middle-click or status-bar preview
        either. Places are links, the act is a button — which turns the distinction from a claim in
        a comment into something a test can read off the DOM.
      */}
      <nav className={styles.nav} aria-label="Main navigation">
        <button
          className={styles.transferAction}
          onClick={() => navigate(TRANSFER_ACTION.path)}
          type="button"
        >
          <TRANSFER_ACTION.icon className={styles.navIcon} />
          <Text className={styles.transferLabel}>{TRANSFER_ACTION.label}</Text>
        </button>

        {NAV_PLACES.map((place) => {
          const current = navCurrent(place, location.pathname);
          const active = current !== undefined;
          const Icon = active ? place.activeIcon : place.icon;

          return (
            <Link
              key={place.path}
              to={place.path}
              className={mergeClasses(
                styles.navItem,
                active ? styles.navItemActive : styles.navItemInactive,
              )}
              aria-current={current}
            >
              <Icon className={mergeClasses(styles.navIcon, active && styles.navIconActive)} />
              <Text className={mergeClasses(styles.navLabel, active && styles.navLabelActive)}>
                {place.label}
              </Text>
            </Link>
          );
        })}
      </nav>

      {/* Sign-out is an action, so it stays a button and stays OUTSIDE the nav landmark. Settings
          moved into the list above: reaching it through a callback here while mobile reached it
          through the table is exactly why only mobile ever lit it. */}
      <div className={styles.footer}>
        {onLogout && (
          <button
            className={mergeClasses(styles.footerButton, styles.logoutButton)}
            onClick={onLogout}
            type="button"
          >
            <SignOut24Regular className={styles.navIcon} />
            <Text className={styles.navLabel}>Logout</Text>
          </button>
        )}
      </div>
    </aside>
  );
}

export default Sidebar;
