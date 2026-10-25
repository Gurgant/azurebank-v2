import { componentSizes, safeArea } from '../../theme/tokens';

/**
 * The root custom property that holds the room the bottom bar covers, set by `BottomNav` while it
 * is mounted. The root's scroll padding and the page's bottom padding in `AppLayout` both read it,
 * so the two follow the bar when its utility row opens and closes.
 */
export const BOTTOM_NAV_ROOM = '--ab-bottom-nav-room';

/**
 * The bar's height, open or closed, plus the home indicator's inset and a 10px gap so the last
 * row of content does not sit flush against it. Open, the utility row adds its own height, and
 * the sign-out's wait words under it add theirs while they show.
 */
export function bottomNavRoom(open: boolean, waitWords = false): string {
  const { height, utilityRowHeight, waitWordsHeight } = componentSizes.bottomNav;
  const openRows = waitWords
    ? `${height} + ${utilityRowHeight} + ${waitWordsHeight}`
    : `${height} + ${utilityRowHeight}`;
  const rows = open ? openRows : height;
  return `calc(${rows} + 10px + ${safeArea.bottom})`;
}
