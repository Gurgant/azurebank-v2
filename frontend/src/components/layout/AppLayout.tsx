import { makeStyles, mergeClasses } from '@fluentui/react-components';
import { componentSizes, breakpoints, zIndex } from '../../theme/tokens';
import { useResponsive } from '../../hooks/useResponsive';
import { Header } from './Header';
import type { HeaderProps } from './Header';
import { BottomNav } from './BottomNav';
import { Sidebar } from './Sidebar';

// ============================================
// TYPES
// ============================================

export interface AppLayoutProps {
  /** Page content */
  children: React.ReactNode;
  /** Header props */
  header?: Omit<HeaderProps, 'className'>;
  /** Hide header */
  hideHeader?: boolean;
  /** Hide bottom navigation (mobile) */
  hideBottomNav?: boolean;
  /** Hide sidebar (desktop) */
  hideSidebar?: boolean;
  /** User info for sidebar */
  user?: {
    name: string;
    email?: string;
    avatar?: string;
  };
  /** Logout handler */
  onLogout?: () => void;
  /** Settings handler */
  onSettings?: () => void;
  /** Additional CSS class for main content */
  className?: string;
  /** Full height content (no scroll) */
  fullHeight?: boolean;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  // Mobile layout
  mobileContainer: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100vh',
    width: '100%',
  },

  mobileContent: {
    flex: 1,
    paddingBottom: componentSizes.bottomNav.height,
    overflowY: 'auto',
    '-webkit-overflow-scrolling': 'touch',
  },

  mobileContentNoNav: {
    paddingBottom: 0,
  },

  mobileContentFullHeight: {
    overflowY: 'hidden',
    display: 'flex',
    flexDirection: 'column',
  },

  // Desktop layout
  desktopContainer: {
    display: 'flex',
    minHeight: '100vh',
  },

  desktopMain: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0, // Allow flex shrinking
  },

  desktopContent: {
    flex: 1,
    overflowY: 'auto',
    padding: '24px 32px',
    maxWidth: '1200px',
    margin: '0 auto',
    width: '100%',
  },

  desktopContentFullHeight: {
    overflowY: 'hidden',
    display: 'flex',
    flexDirection: 'column',
    padding: 0,
  },

  desktopHeader: {
    position: 'sticky',
    top: 0,
    zIndex: zIndex.header,
    borderBottom: 'none',
  },

  // Hide on specific breakpoints
  hiddenMobile: {
    [`@media (max-width: ${breakpoints.tablet - 1}px)`]: {
      display: 'none',
    },
  },

  hiddenDesktop: {
    [`@media (min-width: ${breakpoints.desktop}px)`]: {
      display: 'none',
    },
  },
});

// ============================================
// COMPONENT
// ============================================

export function AppLayout({
  children,
  header,
  hideHeader = false,
  hideBottomNav = false,
  hideSidebar = false,
  user,
  onLogout,
  onSettings,
  className,
  fullHeight = false,
}: AppLayoutProps) {
  const styles = useStyles();
  const { isDesktop } = useResponsive();

  // Desktop layout
  if (isDesktop) {
    return (
      <div className={styles.desktopContainer}>
        {/* Sidebar */}
        {!hideSidebar && (
          <Sidebar
            userName={user?.name}
            userEmail={user?.email}
            userAvatar={user?.avatar}
            onLogout={onLogout}
            onSettings={onSettings}
          />
        )}

        {/* Main Content Area */}
        <main className={styles.desktopMain}>
          {/* Desktop Header (optional - usually shows page title) */}
          {!hideHeader && header && (
            <Header
              {...header}
              className={styles.desktopHeader}
            />
          )}

          {/* Page Content */}
          <div
            className={mergeClasses(
              styles.desktopContent,
              fullHeight && styles.desktopContentFullHeight,
              className
            )}
          >
            {children}
          </div>
        </main>
      </div>
    );
  }

  // Mobile layout
  return (
    <div className={styles.mobileContainer}>
      {/* Mobile Header */}
      {!hideHeader && header && <Header {...header} />}

      {/* Page Content */}
      <main
        className={mergeClasses(
          styles.mobileContent,
          hideBottomNav && styles.mobileContentNoNav,
          fullHeight && styles.mobileContentFullHeight,
          className
        )}
      >
        {children}
      </main>

      {/* Bottom Navigation */}
      {!hideBottomNav && <BottomNav />}
    </div>
  );
}

export default AppLayout;
