import { makeStyles, mergeClasses, Spinner } from '@fluentui/react-components';
import { SignOut24Regular } from '@fluentui/react-icons';

const useStyles = makeStyles({
  // The spinner is 20px; the box keeps the icon's 24px, so the label does not move.
  spinnerBox: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
});

interface SignOutIconProps {
  /** A sign-out is under way. */
  busy: boolean;
  /** The host's icon size, kept by the spinner's box too. */
  className: string;
}

/**
 * The icon of both Sign out controls, the sidebar's and the phone row's, so the two cannot come to
 * show a wait differently. The spinner is hidden from assistive technology: the button's own
 * `aria-busy` says it.
 */
export function SignOutIcon({ busy, className }: SignOutIconProps) {
  const styles = useStyles();
  return busy ? (
    <span className={mergeClasses(className, styles.spinnerBox)}>
      <Spinner size="tiny" aria-hidden="true" />
    </span>
  ) : (
    <SignOut24Regular className={className} />
  );
}
