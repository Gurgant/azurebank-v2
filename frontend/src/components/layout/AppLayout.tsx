import { useRef } from 'react';
import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { colors, safeArea, shadows, surfaces, zIndex } from '../../theme/tokens';
import { useResponsive } from '../../hooks/useResponsive';
import { BottomNav } from './BottomNav';
import { BOTTOM_NAV_ROOM, bottomNavRoom } from './bottomNavRoom';
import { focusContainer } from './focusContainer';
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
  /** A sign-out is under way: both Sign out controls say so and take no second press. */
  signingOut?: boolean;
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
  /**
   * The desktop shell's first Tab stop. Off screen until it has focus, never `display: none`, which
   * would take it out of the Tab order. On focus it sits over the sidebar's logo row, at the top
   * left where focus starts, in the chrome's own fill and text and with the app's focus ring.
   */
  skipLink: {
    position: 'absolute',
    top: '8px',
    left: '-10000px',
    // Over the sticky sidebar, which comes later in the page and would otherwise paint over it.
    zIndex: zIndex.header,
    padding: '10px 16px',
    borderRadius: '10px',
    border: `1px solid ${surfaces.border}`,
    backgroundColor: tokens.colorNeutralBackground1,
    color: colors.neutral[900],
    boxShadow: shadows.md,
    fontSize: '15px',
    fontWeight: 600,
    textDecoration: 'underline',

    ':focus': {
      left: '8px',
    },
    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '2px',
    },
  },

  // Mobile layout
  /**
   * The shell takes the display cutout, so no page has to.
   *
   * `viewport-fit=cover` lets the document reach the physical edges, which is what makes the bottom
   * bar's inset mean anything — and hands this box the other three sides. The insets are padding
   * rather than margin on purpose: the canvas still paints edge to edge, under the status bar and
   * behind the cutout, and only the CONTENT steps clear. `mobileContent` has no horizontal padding
   * of its own (pages supply theirs), so in landscape this is the only thing between a page and the
   * notch.
   *
   * The desktop container below deliberately gets none of this. The insets are non-zero only on
   * phones with a cutout, and the widest of those is 932 CSS px in landscape — the desktop layout
   * starts at 1024 and never renders there. Adding it anyway would be four declarations that can
   * only ever compute to zero.
   */
  mobileContainer: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100dvh',
    width: '100%',
    backgroundColor: surfaces.canvas,
    paddingTop: safeArea.top,
    paddingLeft: safeArea.left,
    paddingRight: safeArea.right,
  },

  mobileContent: {
    flex: 1,
    // A column flex box, so the page root can claim the height rather than each page declaring
    // `100vh` for itself. See `desktopContent` for why this replaced the per-page rule.
    display: 'flex',
    flexDirection: 'column',
    // See `desktopContent`: this is the containment that `overflowY: 'auto'` was silently providing.
    overflowX: 'auto',
    // The space the bar occupies: its own height, plus the inset it reserves for the home
    // indicator, plus 10px of gap so the last row of content does not sit flush against it.
    //
    // That 10px used to be justified as "the bar floats 10px off the bottom edge" — which stopped
    // being true when the bar was attached to the edge (`bottom: 0`, only the top corners rounded).
    // The number survived the redesign as a breathing gap, which is worth keeping; the reason for
    // it did not.
    //
    // The bar keeps the room it covers on the root, taller while its utility row is open, so the
    // last control can scroll clear of the open bar. The fallback is the closed bar's room.
    paddingBottom: `var(${BOTTOM_NAV_ROOM}, ${bottomNavRoom(false)})`,
  },

  // No bar to clear, but the home indicator is still there. This was a flat `0` until a reviewer
  // pointed out what `viewport-fit=cover` had just changed about it: with the insets dead the two
  // variants differed only by the bar's height, which was right; with them live, zeroing the padding
  // also discards the inset and puts content under the indicator. Unreachable today — nothing passes
  // `hideBottomNav` — which makes it a trap rather than a bug, and a one-word one to disarm.
  mobileContentNoNav: {
    paddingBottom: safeArea.bottom,
  },

  // The phone's wrapper of the page, there only so that the page holds the same place in the tree
  // as on desktop (see the component). It has no box, so the page root is still a flex item of
  // `mobileContent` and claims the height from it.
  mobilePage: {
    display: 'contents',
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
  signingOut = false,
  className,
}: AppLayoutProps) {
  const styles = useStyles();
  const { isDesktop } = useResponsive();
  const main = useRef<HTMLElement>(null);

  // One tree for both layouts. React keeps a subtree only while every element above it keeps its
  // type and its place, and a page unmounted when the width crosses 1024 px would lose an open
  // dialog, what was typed in it and where focus was. So the sidebar and the bottom bar each hold
  // their slot with `false` when they are not drawn, and the page always sits in the one div of
  // the one main. The skip link holds a slot of its own the same way.
  return (
    <div className={isDesktop ? styles.desktopContainer : styles.mobileContainer}>
      {/* Desktop only: there the sidebar comes before the page, and on a phone the bar comes after
          it. Focus goes to main the way RouteAnnouncer puts it there after a route change, for
          that one focus, so main keeps no tabindex for RouteAnnouncer to find. */}
      {isDesktop && (
        <a
          href="#main-content"
          className={styles.skipLink}
          onClick={(event) => {
            event.preventDefault();
            if (main.current) focusContainer(main.current);
          }}
        >
          Skip to main content
        </a>
      )}
      {isDesktop && !hideSidebar && (
        <Sidebar
          userName={user?.name}
          userEmail={user?.email}
          userAvatar={user?.avatar}
          onLogout={onLogout}
          signingOut={signingOut}
        />
      )}
      <main
        id="main-content"
        ref={main}
        className={
          isDesktop
            ? styles.desktopMain
            : mergeClasses(
                styles.mobileContent,
                hideBottomNav && styles.mobileContentNoNav,
                className,
              )
        }
      >
        <div
          className={isDesktop ? mergeClasses(styles.desktopContent, className) : styles.mobilePage}
        >
          {children}
        </div>
      </main>
      {!isDesktop && !hideBottomNav && <BottomNav onLogout={onLogout} signingOut={signingOut} />}
    </div>
  );
}

export default AppLayout;
