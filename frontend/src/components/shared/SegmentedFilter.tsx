import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components';
import { colors, transitions } from '../../theme/tokens';

/**
 * A row of mutually exclusive filters, where exactly one is chosen.
 *
 * Extracted for a reason other than "there might be another one later" — there is only one consumer
 * today, and building components for hypothetical consumers is what U1.4 deleted three components
 * for. The reason is that the inline version was **inaccessible**: four bare `<button>`s with no
 * `aria-pressed`, no group, and no accessible name for the group. Which one was selected was
 * communicated by background colour alone, so a screen reader announced four identical buttons and
 * a person could not tell which filter was active.
 *
 * Putting the aria contract in one place is what makes it stay true. Inline, it had to be
 * remembered at every call site, and it was not remembered at the only one.
 *
 * **The dashboard's account chips deliberately do NOT use this.** They share the semantics —
 * single-select, `aria-pressed` — but they are two-line controls carrying a name, a masked number
 * and a balance. Making one component serve both needs a `renderOption` prop, and a component you
 * configure with a render function is two components wearing a trench coat.
 */

export interface SegmentedFilterOption<T extends string> {
  value: T;
  label: string;
}

export interface SegmentedFilterProps<T extends string> {
  /** Names the group for assistive technology — "Filter transactions", not "Filters". */
  label: string;
  options: readonly SegmentedFilterOption<T>[];
  value: T;
  onChange: (value: T) => void;
  className?: string;
}

const useStyles = makeStyles({
  group: {
    display: 'flex',
    gap: '8px',
    padding: '12px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${colors.neutral[200]}`,
    // Filters can outgrow a phone; scrolling them is better than wrapping into a second row that
    // pushes the list down.
    overflowX: 'auto',
    flexShrink: 0,
  },

  option: {
    height: '36px',
    padding: '0 16px',
    backgroundColor: colors.neutral[100],
    borderRadius: '18px',
    border: 'none',
    cursor: 'pointer',
    whiteSpace: 'nowrap',
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
    transition: `all ${transitions.fast}`,

    ':hover': { backgroundColor: colors.neutral[200] },
    ':focus-visible': { outline: `2px solid ${colors.brand[60]}`, outlineOffset: '2px' },
  },

  optionOn: {
    backgroundColor: colors.brandFill.rest,
    color: tokens.colorNeutralForegroundOnBrand,
    ':hover': { backgroundColor: colors.brandFill.hover },
  },
});

export function SegmentedFilter<T extends string>({
  label,
  options,
  value,
  onChange,
  className,
}: SegmentedFilterProps<T>) {
  const styles = useStyles();

  return (
    <div className={mergeClasses(styles.group, className)} role="group" aria-label={label}>
      {options.map((option) => {
        const on = option.value === value;
        return (
          <button
            key={option.value}
            type="button"
            className={mergeClasses(styles.option, on && styles.optionOn)}
            // The whole point of the extraction: selection is announced, not merely coloured.
            aria-pressed={on}
            onClick={() => onChange(option.value)}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

export default SegmentedFilter;
