import { makeStyles, mergeClasses } from '@fluentui/react-components';

/**
 * The AzureBank app icon.
 *
 * It renders `public/favicon.svg` — literally the file the browser puts in the tab. That is the
 * whole design: the tile in the tab strip and the tile in the sidebar are one object, not two
 * things that resemble each other, and there is no second geometry to keep in step. `favicon.svg`
 * is itself generated from `public/logo.svg` by `scripts/generate-icons.js`, so the mark, the white
 * plate, its 18% corner radius and the 5% inset all have exactly one definition.
 *
 * **The plate lives in the asset, not in CSS**, and that is deliberate. Rebuilding it here — a white
 * `<div>` with a border-radius wrapping the bare mark — would look identical until the day the
 * generator's radius or inset changed, at which point the tab and the app would quietly disagree.
 *
 * The plate is also why this component has one variant where it used to have two. The earlier
 * version carried a `mono` mode that painted the mark as a `currentColor` silhouette, because the
 * mark's dark blue is `#0c6ea1` against a `#0077b6` panel — about 1.2:1, a letter dissolving into
 * its own background. The plate solves that better than the silhouette did: it keeps the mark's own
 * two colours instead of flattening them away, and it works on every surface rather than only on
 * brand. On a white surface the plate is simply invisible, which is the correct behaviour and needs
 * no second code path.
 */

const useStyles = makeStyles({
  root: {
    display: 'block',
    // The lockups place this in a flex row beside the wordmark; without this the icon is the thing
    // that gives way when the row is tight, and a squashed logo is worse than a wrapped word.
    flexShrink: 0,
  },
});

export interface LogoProps {
  /** Edge length in pixels. The tile is square, so this is both width and height. */
  size: number;
  className?: string;
  /**
   * Decorative by default: every placement so far sets the mark beside the word "AzureBank", and
   * announcing it again is noise in a screen reader. Pass a label only where the mark stands alone.
   */
  label?: string;
}

export function Logo({ size, className, label }: LogoProps) {
  const styles = useStyles();

  // An empty alt is what marks an image decorative — it removes the element from the accessibility
  // tree on its own, so no aria-hidden is needed alongside it.
  return (
    <img
      src="/favicon.svg"
      alt={label ?? ''}
      width={size}
      height={size}
      className={mergeClasses(styles.root, className)}
    />
  );
}
