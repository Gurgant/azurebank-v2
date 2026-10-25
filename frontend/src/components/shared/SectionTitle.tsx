import type { ReactNode } from 'react';
import { Text, makeStyles, mergeClasses, shorthands } from '@fluentui/react-components';
import { colors } from '../../theme/tokens';

const useStyles = makeStyles({
  // `display: block` because Fluent's `Text` styles its root inline whatever element it renders,
  // and no margin because a heading tag brings the browser's own margin with it: the reset in
  // `index.css` covers that today, and this keeps the title's box from depending on it. The four
  // sides one by one, so that a page's `marginBottom` takes the place of this one: Griffel keeps
  // one class for each property, the last one given, and `margin` is another property.
  root: {
    display: 'block',
    ...shorthands.margin(0),
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
  },
});

export interface SectionTitleProps {
  children: ReactNode;
  /** For the `aria-labelledby` of the section or card the title names. */
  id?: string;
  /** What a page adds to the title's look, merged after the shared rule so that it wins. */
  className?: string;
}

/**
 * The title of a section or a card, as a level-2 heading under the page's one `<h1>`.
 *
 * Every page draws the same title, 16 px at weight 600 in `colors.neutral[800]`; a page that
 * needs more (a margin under it, a smaller grey label) says so with `className`. The section it
 * titles takes its name from it: `<section aria-labelledby={id}>`, so that a screen reader's list
 * of regions and its list of headings say the same words.
 */
export function SectionTitle({ children, id, className }: SectionTitleProps) {
  const styles = useStyles();
  return (
    <Text as="h2" id={id} className={mergeClasses(styles.root, className)}>
      {children}
    </Text>
  );
}
