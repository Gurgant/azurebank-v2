import { useLocation, useNavigate } from 'react-router-dom';
import { makeStyles, mergeClasses, Text } from '@fluentui/react-components';
import {
  Home24Regular,
  Home24Filled,
  History24Regular,
  History24Filled,
  ArrowSwap24Regular,
  ArrowSwap24Filled,
  Person24Regular,
  Person24Filled,
} from '@fluentui/react-icons';
import { colors, componentSizes, zIndex, transitions, shadows } from '../../theme/tokens';

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

export interface BottomNavProps {
  /** Custom navigation items (overrides default) */
  items?: NavItem[];
  /** Additional CSS class */
  className?: string;
}

// ============================================
// DEFAULT NAV ITEMS
// ============================================

const defaultNavItems: NavItem[] = [
  {
    path: '/dashboard',
    label: 'Home',
    icon: <Home24Regular />,
    activeIcon: <Home24Filled />,
  },
  {
    path: '/transactions',
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
  {
    path: '/profile',
    label: 'Profile',
    icon: <Person24Regular />,
    activeIcon: <Person24Filled />,
  },
];

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  nav: {
    position: 'fixed',
    bottom: 0,
    left: 0,
    right: 0,
    height: componentSizes.bottomNav.height,
    backgroundColor: '#FFFFFF',
    borderTop: `1px solid ${colors.neutral[100]}`,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-around',
    padding: `0 ${componentSizes.bottomNav.paddingX}`,
    paddingBottom: 'env(safe-area-inset-bottom, 0px)',
    zIndex: zIndex.bottomNav,
    boxShadow: shadows.sm,
  },

  navItem: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '4px',
    flex: 1,
    height: '100%',
    maxWidth: '80px',
    backgroundColor: 'transparent',
    border: 'none',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    padding: '8px 4px',

    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '-2px',
      borderRadius: '8px',
    },
  },

  navItemActive: {
    color: colors.brand[60],
  },

  navItemInactive: {
    color: colors.neutral[400],
    ':hover': {
      color: colors.neutral[600],
    },
  },

  icon: {
    width: '24px',
    height: '24px',
    transition: `transform ${transitions.fast}`,
  },

  iconActive: {
    transform: 'scale(1.1)',
  },

  label: {
    fontSize: '11px',
    fontWeight: 500,
    lineHeight: 1,
    transition: `color ${transitions.fast}`,
  },

  labelActive: {
    fontWeight: 600,
  },

  // Indicator dot above active item
  indicator: {
    position: 'absolute',
    top: '-2px',
    left: '50%',
    transform: 'translateX(-50%)',
    width: '4px',
    height: '4px',
    borderRadius: '50%',
    backgroundColor: colors.brand[60],
  },

  navItemWrapper: {
    position: 'relative',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
  },
});

// ============================================
// COMPONENT
// ============================================

export function BottomNav({ items = defaultNavItems, className }: BottomNavProps) {
  const styles = useStyles();
  const location = useLocation();
  const navigate = useNavigate();

  const isActive = (path: string) => {
    // Exact match for home, startsWith for others
    if (path === '/dashboard') {
      return location.pathname === path;
    }
    return location.pathname.startsWith(path);
  };

  return (
    <nav className={mergeClasses(styles.nav, className)} aria-label="Main navigation">
      {items.map((item) => {
        const active = isActive(item.path);

        return (
          <button
            key={item.path}
            className={mergeClasses(
              styles.navItem,
              active ? styles.navItemActive : styles.navItemInactive
            )}
            onClick={() => navigate(item.path)}
            aria-label={item.label}
            aria-current={active ? 'page' : undefined}
            type="button"
          >
            <div className={styles.navItemWrapper}>
              {active && <div className={styles.indicator} />}
              <div className={mergeClasses(styles.icon, active && styles.iconActive)}>
                {active ? item.activeIcon : item.icon}
              </div>
            </div>
            <Text
              className={mergeClasses(
                styles.label,
                active && styles.labelActive
              )}
            >
              {item.label}
            </Text>
          </button>
        );
      })}
    </nav>
  );
}

export default BottomNav;
