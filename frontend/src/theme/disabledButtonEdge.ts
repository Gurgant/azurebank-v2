import {
  makeStyles,
  mergeClasses,
  shorthands,
  tokens,
  type ButtonState,
} from '@fluentui/react-components';

/**
 * A disabled button keeps a shape: an edge in the colour of its own disabled label.
 *
 * WHAT WAS MEASURED, in Chromium on 2026-10-06, on a disabled main button ("Review Transfer"):
 *
 * | theme | fill      | behind it          | fill to ground | edge        |
 * | ----- | --------- | ------------------ | -------------- | ----------- |
 * | light | `#f0f0f0` | page `#f3f4f6`     | 1.04 to 1      | transparent |
 * | light | `#f0f0f0` | card `#ffffff`     | 1.14 to 1      | transparent |
 * | dark  | `#141414` | page `#141414`     | 1.00 to 1      | transparent |
 * | dark  | `#141414` | card `#292929`     | 1.27 to 1      | transparent |
 *
 * So the button was its label and nothing else: grey words with no box around them. A disabled
 * secondary button ("Verify") had the library's disabled stroke, 1.20 to 1 on the light page and
 * 1.83 to 1 on the dark one.
 *
 * WHY AN EDGE, AND WHY NOT IN THE THEME OBJECTS. The library paints a disabled main button's
 * border `transparent` (`useRootDisabledStyles.primary` in its `useButtonStyles.styles`), so no
 * stroke token of a theme reaches it. The one token that does is the fill,
 * `colorNeutralBackgroundDisabled`, and a fill that stands out from both grounds costs either
 * the label or the look. The label is `colorNeutralForegroundDisabled`, 1.65 to 1 on its fill in
 * light and 2.76 to 1 in dark. Computed that day over every grey, with WCAG's formula and the
 * colours measured above: in light the fills at 1.5 to 1 from the page and from a card are
 * `#c9c9c9` and darker, and the label keeps its 1.65 only on `#929292` and darker, a slab
 * darker than its own words (on `#c9c9c9` it is 1.13); in dark they are `#454545` and lighter,
 * and the label keeps its 2.76 only on `#a7a7a7` and lighter (on `#454545` it is 1.43). The
 * edge leaves fill and label as they are.
 *
 * WHICH COLOUR. The label's own, `colorNeutralForegroundDisabled`: a neutral token both themes
 * define, and already the colour that says "disabled" on this very button, so the box reads as
 * switched off and never as a button to press. Against what is behind it, measured after the
 * change: light 1.71 to 1 on the page and 1.88 on a card, dark 2.76 and 2.18. The library's own
 * disabled stroke was not taken: measured on the page at 1.20 in light and 1.83 in dark, and by
 * the same formula 1.32 and 1.45 on a card.
 *
 * WHICH BUTTONS. The appearances that have a shape when they can be pressed: main, secondary and
 * outline. A subtle or transparent button is bare by design (the X of a dialog, the eye of a
 * password field) and stays bare when disabled. Inputs and everything else that is not a
 * `Button` are not touched: no token of the theme changes.
 *
 * The selectors are the library's own, to the letter. Griffel keeps one class for each property
 * and selector, the last one given, so a rule under another selector would sit beside the
 * library's `transparent` and not in its place; and without the two states the edge would go
 * the moment the pointer is over the button. `shorthands.borderColor` for the same reason: the
 * library sets the four sides one by one.
 *
 * Not in forced colours: there the library's own rule, inside its media query, comes after
 * this one. Seen in Chromium with forced colours emulated: the edge was `GrayText`, as the
 * label was.
 */
const useStyles = makeStyles({
  edge: {
    ...shorthands.borderColor(tokens.colorNeutralForegroundDisabled),
    ':hover': {
      ...shorthands.borderColor(tokens.colorNeutralForegroundDisabled),
    },
    ':hover:active,:active:focus-visible': {
      ...shorthands.borderColor(tokens.colorNeutralForegroundDisabled),
    },
  },
});

/** The appearances whose button has a box when it can be pressed. */
const SHAPED: readonly ButtonState['appearance'][] = ['primary', 'secondary', 'outline'];

/**
 * Handed to `FluentProvider` as the app's own step after the library's styles for `Button`
 * (`ThemeProvider.tsx`): the library calls it with each button's state, and what it adds to the
 * root's classes comes last, over the library's and over the page's own `className`.
 */
export function useDisabledButtonEdge(state: unknown): void {
  const styles = useStyles();
  const button = state as ButtonState;
  if ((button.disabled || button.disabledFocusable) && SHAPED.includes(button.appearance)) {
    /*
      A write to an argument, and the lint rule against one is switched off for this line alone.
      Writing the classes into the state it is handed is what the library asks of a style hook:
      `Button` calls this, takes nothing back from it and draws from the same `state`
      (`Button.js` in its `react-button`), and its own `useButtonStyles_unstable` makes the same
      write one line before.
    */
    // eslint-disable-next-line react-hooks/immutability
    button.root.className = mergeClasses(button.root.className, styles.edge);
  }
}
