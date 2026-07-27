import { makeStyles, mergeClasses } from '@fluentui/react-components';
import { componentSizes, surfaces } from '../../theme/tokens';
import { useResponsive } from '../../hooks/useResponsive';
import { BottomNav } from './BottomNav';
import { Sidebar } from './Sidebar';

// ============================================
// TYPES
// ============================================

export interface AppLayoutProps {
  /** Page content */
  children: React.ReactNode;
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
  /** Additional CSS class for main content */
  className?: string;
}

// ============================================
// STYLES
// ============================================

/**
 * The shell owns the canvas. Every page used to paint its own.
 *
 * Four of the five shell pages carried `minHeight: 100vh` plus `backgroundColor: neutral[50]`, and
 * that produced three separate defects, all of them measured in a real browser at 1600×1000 rather
 * than reasoned about:
 *
 *  1. **The canvas was a slab, not a ground.** The page's background sat INSIDE `desktopContent`,
 *     which is capped at 1200px and centred, so the grey ran from x=345 to x=1481 with white
 *     gutters either side. A floating rectangle, on a white page, next to a white sidebar.
 *  2. **`SettingsPage` never painted one**, so it rendered white beside a white sidebar separated
 *     by a 1.10:1 hairline. Which page you were on decided what colour the app was.
 *  3. **48px of scrollbar that had nothing to scroll.** `minHeight: 100vh` inside a box with
 *     `padding: 24px` top and bottom makes the document 100vh + 48px on every page, whatever the
 *     content: measured `scrollHeight 1048` against `innerHeight 1000` with the content occupying
 *     exactly 1000.
 *
 * The fix is structural rather than cosmetic: the canvas is painted once, here, on the element that
 * spans the viewport, and the 1200px cap goes back to being what it should always have been — a
 * measure for the CONTENT, not a boundary for the ground.
 *
 * `100dvh` rather than `100vh`: on a phone `vh` is the viewport with the URL bar hidden, so a
 * `min-height: 100vh` shell is always a little scrollable even when empty. `dvh` tracks the visible
 * viewport, which is what "fill the screen" has always meant here.
 */
const useStyles = makeStyles({
  // Mobile layout
  mobileContainer: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100dvh',
    width: '100%',
    backgroundColor: surfaces.canvas,
  },

  mobileContent: {
    flex: 1,
    // A column flex box, so the page root can claim the height rather than each page declaring
    // `100vh` for itself. See `desktopContent` for why this replaced the per-page rule.
    display: 'flex',
    flexDirection: 'column',
    // See `desktopContent`: this is the containment that `overflowY: 'auto'` was silently providing.
    overflowX: 'auto',
    // The bar floats 10px off the bottom edge and clears the home indicator, so the space it
    // occupies is its own height PLUS that inset — padding of just the height leaves the last row
    // of content under the card.
    paddingBottom: `calc(${componentSizes.bottomNav.height} + 10px + env(safe-area-inset-bottom, 0px))`,
  },

  mobileContentNoNav: {
    paddingBottom: 0,
  },

  // Desktop layout
  desktopContainer: {
    display: 'flex',
    minHeight: '100dvh',
    backgroundColor: surfaces.canvas,
  },

  desktopMain: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0, // Allow flex shrinking
  },

  /**
   * The content column: a measure for the content, and the box that hands the page its height.
   *
   * Two of its declarations are here because removing the old `overflowY: 'auto'` turned out to
   * remove more than it looked like:
   *
   * **`overflowX`.** Per CSS Overflow 3, `overflow-x: visible` computes to `auto` when the other
   * axis is not `visible` — so the single `overflowY: 'auto'` was ALSO containing horizontal
   * overflow inside this 1200px column, and deleting it let the page escape to the document.
   * `DashboardPage`'s two-column row has a min-content floor of about 399px on the left plus a
   * fixed 360px right column, which does not fit between roughly 1024 and 1094px of layout
   * viewport; measured in Chrome, the document gained up to 70px of horizontal scroll there, and
   * because `Sidebar` is a static flex item at x=0 with no `flexShrink`, scrolling right slid the
   * whole navigation off the left edge. The containment is declared explicitly now instead of
   * arriving as a side effect of a rule about the other axis. (The over-width itself is older than
   * this file's change and belongs to a Dashboard layout pass — this only stops it escaping.)
   *
   * **`display: flex`.** `minHeight: 100dvh` reaches this box — it is a `flex: 1` item of a
   * stretched `desktopMain` — but a block box passes no height to its children, so the page root
   * sat at `height: auto` and every `flex: 1` state inside it had no free space to claim. The
   * empty-history and transaction-not-found states stopped centring and hugged the top: measured,
   * History's empty-state icon moved from y=542 to y=256. Making this a column flex container is
   * what lets the page root take the height with `flex: 1`, which is what "the height belongs to
   * the shell" has to mean if it is going to be true.
   */
  desktopContent: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    overflowX: 'auto',
    padding: '24px 32px',
    maxWidth: '1200px',
    margin: '0 auto',
    width: '100%',
  },
});

// ============================================
// COMPONENT
// ============================================

export function AppLayout({
  children,
  hideBottomNav = false,
  hideSidebar = false,
  user,
  onLogout,
  className,
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
          />
        )}

        {/* Main Content Area */}
        <main className={styles.desktopMain}>
          {/* Page Content */}
          <div className={mergeClasses(styles.desktopContent, className)}>{children}</div>
        </main>
      </div>
    );
  }

  // Mobile layout
  return (
    <div className={styles.mobileContainer}>
      {/* Page Content */}
      <main
        className={mergeClasses(
          styles.mobileContent,
          hideBottomNav && styles.mobileContentNoNav,
          className,
        )}
      >
        {children}
      </main>

      {/* Bottom Navigation */}
      {!hideBottomNav && <BottomNav onLogout={onLogout} />}
    </div>
  );
}

export default AppLayout;
