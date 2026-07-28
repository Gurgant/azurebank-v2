import { useRef, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { ChevronDown24Regular, ChevronUp24Regular, SignOut24Regular } from '@fluentui/react-icons';
import { colors, componentSizes, surfaces, zIndex, transitions, shadows } from '../../theme/tokens';
import {
  NAV_PLACES,
  TRANSFER_ACTION,
  primaryPlaces,
  resolveNavPlace,
  utilityPlaces,
} from './navItems';

// ============================================
// TYPES
// ============================================

export interface BottomNavProps {
  /** Additional CSS class */
  className?: string;
  /** Sign out. Omit and the sheet simply does not offer it. */
  onLogout?: () => void;
}

const UTILITY_ROW_ID = 'bottom-nav-utility-row';

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  nav: {
    position: 'fixed',
    // Attached to the bottom edge, with only the TOP corners rounded.
    //
    // Floating it 10px off every edge was worse for two measurable reasons. The home indicator
    // then sits over PAGE CONTENT visible through the gap, where iOS expects it over the bar's own
    // surface; and content scrolls THROUGH that gap on three sides, so slivers of transactions slide
    // past beside and beneath the card. Attached, the content disappears behind one clean edge.
    bottom: 0,
    left: 0,
    right: 0,
    borderTopLeftRadius: '22px',
    borderTopRightRadius: '22px',
    // Internal padding, not an outer inset: the card's surface reaches under the home indicator
    // while its contents stay clear of it.
    paddingBottom: 'env(safe-area-inset-bottom, 0px)',
    borderTop: `1px solid ${surfaces.border}`,
    // Clips the utility row's corners to the card's radius when it opens.
    overflow: 'hidden',
    // A COLUMN, not a row. The bar is pinned to the bottom edge and sized by its content, so a
    // second row appended AFTER the main one pushes the main one up by exactly one row height:
    // the row you were already using lifts, and the new options land in the space it vacated.
    // That is the whole gesture — the bar grows, it does not cover the screen with a panel.
    flexDirection: 'column',
    // NO fixed height: the bar is as tall as its rows. `mainRow` keeps the original 72px, so
    // closed the bar is exactly what it always was; open, it is 72 + the utility row, and
    // because it is pinned to `bottom: 0` the growth pushes the main row up rather than
    // pushing the new row off the bottom of the screen — which is what a fixed height did.
    backgroundColor: tokens.colorNeutralBackground1,
    // Same reasoning as the sidebar's right edge: this seam separates chrome from the canvas, and
    // it cannot be the canvas's own colour.
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-around',
    zIndex: zIndex.bottomNav,
    boxShadow: shadows.sm,
  },

  /**
   * One cell shape for all five slots — four places and the act — so the act reads as the same
   * species of control, differing only in what it is made of.
   *
   * `position: relative` anchors the rail. At a 320px viewport a cell is (320 - 16) / 5 = 60.8px
   * wide against the bar's 72px height, so every target clears 44x44 in both directions.
   */
  /** The always-present row. Keeps the height the bar had before it could grow. */
  mainRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-around',
    width: '100%',
    height: componentSizes.bottomNav.height,
  },

  /**
   * The row that appears above it. Same cell shape, same height, so the lift reads as the bar
   * getting taller rather than as a different object arriving.
   */
  /**
   * Opens BELOW the main row, which is why it comes after it in the DOM.
   *
   * Above would keep the row under the thumb still — better ergonomics, and it is what this comment
   * used to describe after the order was flipped and the prose was not. Below wins on reading
   * order instead: the primary row is the more important one and should be read first. The cost is
   * real and accepted — the primary row rises one row height on every toggle.
   *
   * A reviewer read the stale version of this comment and proposed reordering the code to match it,
   * which is the whole reason a comment that outlives its code is worse than no comment.
   */
  utilityRow: {
    display: 'flex',
    gap: '8px',
    width: '100%',
    padding: '2px 10px 10px',
  },

  /** The same tile the dashboard uses for Deposit and Withdraw. No new shapes. */
  utilityTile: {
    // `navItemActive`'s rail is `position: absolute; top: 0; left: 50%`, so it anchors to the
    // nearest POSITIONED ancestor. Without this it resolved against the `position: fixed` nav and
    // painted a bar across the centre of the whole card's top edge — the dark mark visible in the
    // first screenshots, which I wrongly attributed to Home's own indicator sitting on the seam.
    position: 'relative',
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '6px',
    minHeight: '62px',
    padding: '10px 4px',
    borderRadius: '14px',
    backgroundColor: colors.neutral[50],
    border: 'none',
    cursor: 'pointer',
    textDecoration: 'none',
    color: colors.neutral[700],
    font: 'inherit',
    ':hover': { backgroundColor: colors.neutral[100] },
    ':focus-visible': { outline: `2px solid ${colors.brand[60]}`, outlineOffset: '-2px' },
  },

  sheetBody: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    paddingBottom: 'env(safe-area-inset-bottom, 0px)',
  },

  sheetRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '14px',
    width: '100%',
    padding: '14px 8px',
    // 48px minimum target, which is the floor Material states for a touch control.
    minHeight: '48px',
    background: 'none',
    border: 'none',
    borderRadius: '10px',
    font: 'inherit',
    color: colors.neutral[800],
    textDecoration: 'none',
    cursor: 'pointer',
    textAlign: 'left',
    ':hover': { backgroundColor: colors.neutral[50] },
    ':focus-visible': { outline: `2px solid ${colors.brand[60]}`, outlineOffset: '-2px' },
  },

  navItem: {
    position: 'relative',
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
    textDecoration: 'none',
    transition: `color ${transitions.fast}`,
    padding: '8px 4px',

    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      // Inset, so the fixed bar cannot clip the ring.
      outlineOffset: '-2px',
      borderRadius: '8px',
    },
  },

  /**
   * The same rail as the sidebar, rotated: 3px thick and 24px long — exactly the width of the icon
   * below it — drawn on the cell's leading cross-axis edge, which on a horizontal bar is the top
   * seam.
   *
   * It replaces a 4px dot that floated at `top: -2px` above the icon, a shape that appeared nowhere
   * else in the application.
   */
  navItemActive: {
    color: colors.neutral[900],

    '::before': {
      content: '""',
      position: 'absolute',
      top: 0,
      left: '50%',
      width: '24px',
      height: '3px',
      transform: 'translateX(-50%)',
      borderRadius: '999px',
      backgroundColor: colors.brand[60],
    },
  },

  /**
   * `neutral[400]` measured **2.539:1** on white. That fails 1.4.3 for the 11px label and fails
   * 1.4.11 for the 24px icon — on the primary navigation of a banking app. `neutral[600]` is
   * 7.557:1 and is already what the sidebar uses for inactive, so closing the failure also puts
   * the two surfaces on one token.
   *
   * This visibly darkens the whole bar. That is the correct trade, and it is the change someone
   * will notice before they notice why.
   */
  navItemInactive: {
    color: colors.neutral[600],
    ':hover': {
      color: colors.neutral[800],
    },
  },

  icon: {
    width: '24px',
    height: '24px',
  },

  // The label carries the darkness, the icon carries the brand. The old `scale(1.1)` here was a
  // wobble rather than a signal, and the rail now does that job.
  iconActive: {
    color: colors.brand[60],
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

  /**
   * The ACT, in the centre slot: the same cell as a place, saturated only in the icon's container.
   *
   * The disc is 40px and not 44px, and the arithmetic is the reason — including the part that is
   * easy to drop. `box-sizing: border-box` is global, so the bar's 72px
   * (`componentSizes.bottomNav.height`) is a BORDER box and its own 1px `borderTop` comes out of
   * it: 71px of content. The cell's `height: 100%` resolves against that and is border-box too, so
   * `padding: '8px 4px'` leaves **55px**. The stack is 40 (disc) + 4 (gap) + 11 (label at
   * `lineHeight: 1`) = 55. It fits exactly, with nothing spare — measured in Chrome, not derived.
   *
   * So this is already at the limit rather than one pixel from it. Raising `gap` to 5px, or the
   * bar's height token shrinking, is enough. What happens then is NOT the overhang the paragraph
   * below refuses: the disc is a flex item at the default `flex-shrink: 1`, so it silently
   * compresses into an ellipse instead of clipping. That is the failure to watch for — a quiet
   * deformation, not a visible break.
   *
   * No notch, no overhang, no floating circle above the bar's edge — nothing overflows, so there is
   * no z-index and no safe-area interaction to get wrong. A raised disc hovering over a banking tab
   * bar is the loudest assembled-from-a-template signal available, and it is refused on purpose.
   * Same shape as a tab, different species.
   */
  transferDisc: {
    width: '40px',
    height: '40px',
    borderRadius: '50%',
    backgroundColor: colors.brand[60],
    color: tokens.colorNeutralForegroundOnBrand,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    transition: `background-color ${transitions.fast}`,
  },

  transferLabel: {
    fontSize: '11px',
    fontWeight: 600,
    lineHeight: 1,
    // 4.868:1 on white, above the 4.5:1 this 11px text needs.
    color: colors.brand[60],
  },
});

// ============================================
// COMPONENT
// ============================================

export function BottomNav({ className, onLogout }: BottomNavProps) {
  const styles = useStyles();
  const [sheetOpen, setSheetOpen] = useState(false);
  const toggleRef = useRef<HTMLButtonElement>(null);

  // Every control that navigates also closes the row — in its own handler, not in an effect on
  // pathname. `react-hooks/set-state-in-effect` forbids the effect version, and it is right to:
  // closing in response to a render that already happened is a cascade, where closing in the
  // handler is just the thing the press means. The utility links already did this; Home, Accounts,
  // History and Transfer did not, so opening More and tapping Home left a menu hanging over the
  // page you had just moved to.
  const closeSheet = () => setSheetOpen(false);
  const location = useLocation();
  const navigate = useNavigate();

  // Split by CATEGORY, never by index. This was `const [home, accounts, ...rest] = NAV_PLACES`,
  // which put the act after position 2 by counting — so adding any place before Settings silently
  // slid the Transfer disc off centre, and nothing in the suite would have said so.
  //
  // Three primary places and the act make four cells plus the avatar: two left, disc, two right.
  // The act stays centred because the arithmetic that centres it is now "half the primary places",
  // not the literal number 2.
  const bar = primaryPlaces();
  const utility = utilityPlaces();
  const mid = Math.ceil(bar.length / 2);
  const before = bar.slice(0, mid);
  const rest = bar.slice(mid);

  // The avatar lights when the page you are on lives INSIDE the sheet. Without this, standing on
  // /settings or /about would leave the whole bar dark — which is the exact defect PR #47 fixed on
  // /settings, reintroduced one level down.
  const utilityCurrent = utility.find((p) => resolveNavPlace(p, location.pathname).active);

  const renderPlace = (place: (typeof NAV_PLACES)[number]) => {
    const { current, active, Icon } = resolveNavPlace(place, location.pathname);

    return (
      <Link
        key={place.path}
        to={place.path}
        className={mergeClasses(
          styles.navItem,
          active ? styles.navItemActive : styles.navItemInactive,
        )}
        aria-current={current}
        onClick={closeSheet}
      >
        {/* No aria-label: Fluent icons are aria-hidden when untitled, so the accessible name comes
            from the visible text alone. That is what makes "the accessible name is the label"
            worth asserting instead of tautological. */}
        <Icon className={mergeClasses(styles.icon, active && styles.iconActive)} />
        <Text className={mergeClasses(styles.label, active && styles.labelActive)}>
          {place.label}
        </Text>
      </Link>
    );
  };

  return (
    <nav
      className={mergeClasses(styles.nav, className)}
      aria-label="Main navigation"
      onKeyDown={(e) => {
        if (e.key === 'Escape' && sheetOpen) {
          setSheetOpen(false);
          toggleRef.current?.focus();
        }
      }}
    >
      <div className={styles.mainRow}>
        {before.map(renderPlace)}

        {/* The one non-place control in this landmark. A button, never a link, and it carries no
          aria-current under any route. */}
        <button
          className={styles.navItem}
          onClick={() => {
            closeSheet();
            navigate(TRANSFER_ACTION.path);
          }}
          type="button"
        >
          <div className={styles.transferDisc}>
            <TRANSFER_ACTION.icon className={styles.icon} />
          </div>
          <Text className={styles.transferLabel}>{TRANSFER_ACTION.label}</Text>
        </button>

        {rest.map(renderPlace)}

        {/* The SECOND non-place control in this landmark, and the reason the docblock below no
          longer says "exactly ONE". Utility navigation needed a host on mobile; the surveyed banks
          that have a header all use one (Revolut, N26, Starling, Wise, bunq, PayPal, Nubank) and we
          have no mobile header.

          A chevron, not an avatar: the bar LIFTS, and a chevron is the one glyph that says which
          way. Up when closed, down when open — the icon is the instruction. An avatar said "you"
          when the thing behind it is mostly "this app". */}
        <button
          className={mergeClasses(
            styles.navItem,
            utilityCurrent && !sheetOpen ? styles.navItemActive : styles.navItemInactive,
          )}
          type="button"
          ref={toggleRef}
          onClick={() => setSheetOpen((v) => !v)}
          aria-expanded={sheetOpen}
          aria-controls={UTILITY_ROW_ID}
          aria-current={utilityCurrent && !sheetOpen ? 'true' : undefined}
        >
          {sheetOpen ? (
            <ChevronDown24Regular className={styles.icon} />
          ) : (
            <ChevronUp24Regular
              className={mergeClasses(styles.icon, utilityCurrent && styles.iconActive)}
            />
          )}
          <Text className={mergeClasses(styles.label, utilityCurrent && styles.labelActive)}>
            More
          </Text>
        </button>
      </div>

      {sheetOpen && (
        <div className={styles.utilityRow} id={UTILITY_ROW_ID}>
          {utility.map((place) => {
            const { current, active, Icon } = resolveNavPlace(place, location.pathname);
            return (
              <Link
                key={place.path}
                to={place.path}
                className={mergeClasses(styles.utilityTile, active && styles.navItemActive)}
                aria-current={current}
                onClick={() => setSheetOpen(false)}
              >
                <Icon className={mergeClasses(styles.icon, active && styles.iconActive)} />
                <Text className={mergeClasses(styles.label, active && styles.labelActive)}>
                  {place.label}
                </Text>
              </Link>
            );
          })}

          {/* An act, like Transfer: a button, no href, never lit. It was unreachable from the bar
              entirely — signing out meant going two levels into Settings, on the surface where
              signing out matters most. */}
          {onLogout && (
            <button
              type="button"
              className={styles.utilityTile}
              onClick={() => {
                setSheetOpen(false);
                onLogout();
              }}
            >
              <SignOut24Regular className={styles.icon} />
              <Text className={styles.label}>Sign out</Text>
            </button>
          )}
        </div>
      )}
    </nav>
  );
}

export default BottomNav;
