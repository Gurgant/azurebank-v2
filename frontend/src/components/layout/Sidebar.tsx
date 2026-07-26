import { useLocation, useNavigate } from 'react-router-dom';
import { makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import {
  Home24Regular,
  Home24Filled,
  Wallet24Regular,
  Wallet24Filled,
  History24Regular,
  History24Filled,
  ArrowSwap24Regular,
  ArrowSwap24Filled,
  Settings24Regular,
  SignOut24Regular,
} from '@fluentui/react-icons';
import { colors, componentSizes, shadows, surfaces, transitions } from '../../theme/tokens';
import { Avatar } from '../shared/Avatar';
import { Logo } from '../shared/Logo';

// ============================================
// TYPES
// ============================================

export interface NavItem {
  /** Route path */
  path: string;
  /** Display label */
  label: string;
  /** Regular (inactive) icon */
  icon: React.ReactNode;
  /** Filled (active) icon */
  activeIcon: React.ReactNode;
}

export interface SidebarProps {
  /** User's full name */
  userName?: string;
  /** User's email */
  userEmail?: string;
  /** User's avatar URL */
  userAvatar?: string;
  /** Logout handler */
  onLogout?: () => void;
  /** Settings handler */
  onSettings?: () => void;
  /** Additional CSS class */
  className?: string;
}

// ============================================
// DEFAULT NAV ITEMS
// ============================================

// The app's REAL information architecture (routes in App.tsx). History previously
// pointed at the non-existent /transactions — the exact drift the shared shell
// exists to prevent. Profile/Settings live in the sidebar footer, not here.
const defaultNavItems: NavItem[] = [
  {
    path: '/dashboard',
    label: 'Dashboard',
    icon: <Home24Regular />,
    activeIcon: <Home24Filled />,
  },
  {
    path: '/accounts',
    label: 'Accounts',
    icon: <Wallet24Regular />,
    activeIcon: <Wallet24Filled />,
  },
  {
    path: '/history',
    label: 'History',
    icon: <History24Regular />,
    activeIcon: <History24Filled />,
  },
  {
    path: '/transfer',
    label: 'Transfer',
    icon: <ArrowSwap24Regular />,
    activeIcon: <ArrowSwap24Filled />,
  },
];

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

  navItem: {
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

    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '2px',
    },
  },

  navItemActive: {
    backgroundColor: colors.brand[120],
    color: colors.brand[60],

    ':hover': {
      backgroundColor: colors.brand[110],
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

  navLabel: {
    fontSize: '15px',
    fontWeight: 500,
  },

  navLabelActive: {
    fontWeight: 600,
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
  onSettings,
  className,
}: SidebarProps) {
  const styles = useStyles();
  const location = useLocation();
  const navigate = useNavigate();

  const isActive = (path: string) => {
    if (path === '/dashboard') {
      return location.pathname === path;
    }
    return location.pathname.startsWith(path);
  };

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

      {/* Navigation */}
      <nav className={styles.nav} aria-label="Main navigation">
        {defaultNavItems.map((item) => {
          const active = isActive(item.path);

          return (
            <button
              key={item.path}
              className={mergeClasses(
                styles.navItem,
                active ? styles.navItemActive : styles.navItemInactive,
              )}
              onClick={() => navigate(item.path)}
              aria-current={active ? 'page' : undefined}
              type="button"
            >
              <div className={styles.navIcon}>{active ? item.activeIcon : item.icon}</div>
              <Text className={mergeClasses(styles.navLabel, active && styles.navLabelActive)}>
                {item.label}
              </Text>
            </button>
          );
        })}
      </nav>

      {/* Footer Actions */}
      <div className={styles.footer}>
        {onSettings && (
          <button className={styles.footerButton} onClick={onSettings} type="button">
            <Settings24Regular className={styles.navIcon} />
            <Text className={styles.navLabel}>Settings</Text>
          </button>
        )}
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
