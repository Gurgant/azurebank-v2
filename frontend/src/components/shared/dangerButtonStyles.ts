import { makeStyles, tokens } from '@fluentui/react-components';

/**
 * The red fill of a button whose action cannot be undone, put on a Fluent `Button` of any
 * appearance. Fluent repaints a button's fill and text on hover and on a press, and a press is
 * both `:hover:active` (the pointer) and `:active:focus-visible` (Space held on the focused
 * button), so each is set here under Fluent's own selector, or its blue or grey shows through.
 * Fluent's status reds keep white text at 6.07:1 and better in both themes; the palette's
 * `error.main` would give 3.92:1. It sets no disabled look of its own: the account deletion's
 * button is never disabled, and ConfirmDialog's, disabled while it loads, keeps the red and
 * reads "Loading...".
 */
export const useDangerButtonStyles = makeStyles({
  fill: {
    backgroundColor: tokens.colorStatusDangerBackground3,
    color: tokens.colorNeutralForegroundStaticInverted,
    ':hover': {
      backgroundColor: tokens.colorStatusDangerBackground3Hover,
      color: tokens.colorNeutralForegroundStaticInverted,
    },
    ':hover:active,:active:focus-visible': {
      backgroundColor: tokens.colorStatusDangerBackground3Pressed,
      color: tokens.colorNeutralForegroundStaticInverted,
    },
  },
});
