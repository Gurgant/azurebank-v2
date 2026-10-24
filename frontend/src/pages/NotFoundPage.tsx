import { Link } from 'react-router-dom';
import { makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { useAppSelector } from '../app/hooks';
import { ProtectedRoute, ShellOrBare } from '../components/layout';
import { selectAuthStatus } from '../features/auth/authSlice';
import { colors } from '../theme/tokens';

/**
 * What an address that names no page shows: App.tsx gives it every such address.
 *
 * The address stays in the bar, so the visitor can see what was wrong with it, and the page offers
 * one way back. Signed in, it renders inside the app shell (`NotFoundRoute`, below) and the way
 * back is Home. Signed out, it stands alone and the way back is the sign-in page, which is
 * where Home would send the visitor anyway.
 *
 * It reads the auth status and nothing else, and asks the server nothing: a visitor without a
 * session is shown nothing of an account and causes no request that needs one.
 */

const useStyles = makeStyles({
  // In the shell the content column is a column flex box, and `flex: 1` takes its height.
  page: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '12px',
    padding: '48px 16px',
    textAlign: 'center',
  },
  // Alone there is no shell to hand it a height, so it takes the screen's.
  standalone: { minHeight: '100dvh' },
  title: { fontSize: '20px', fontWeight: 600, color: colors.neutral[900] },
  body: { fontSize: '14px', color: colors.neutral[700], maxWidth: '420px' },
  // Fluent's link colour holds 4.5 to 1 on the shell's grey canvas too; the palette's brand[60]
  // measured 4.42 to 1 there.
  link: {
    color: tokens.colorBrandForegroundLink,
    fontSize: '14px',
    fontWeight: 600,
    textDecoration: 'none',
    ':hover': { color: tokens.colorBrandForegroundLinkHover, textDecoration: 'underline' },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
});

/**
 * The route's element: the page, once the session check has said whether there is a session.
 *
 * Until then the page cannot tell which way back is true, and a check the service could not answer
 * leaves it that way (the auth slice moves to 'anonymous' only on a 4xx). Shown bare, it would offer
 * sign-in to a visitor whose session may be alive, which reads as being signed out. So while the
 * status is 'unknown' it waits behind `ProtectedRoute`, as a guarded page does: the check's spinner,
 * then the page that says the service is unavailable and offers to try again. Once the status is
 * known, `ShellOrBare` draws the page in the shell or alone.
 */
export function NotFoundRoute() {
  const known = useAppSelector(selectAuthStatus) !== 'unknown';

  return known ? (
    <ShellOrBare>
      <NotFoundPage />
    </ShellOrBare>
  ) : (
    <ProtectedRoute>
      <NotFoundPage />
    </ProtectedRoute>
  );
}

export function NotFoundPage() {
  const styles = useStyles();
  const isAuthenticated = useAppSelector(selectAuthStatus) === 'authenticated';

  return (
    <div className={mergeClasses(styles.page, !isAuthenticated && styles.standalone)}>
      <Text as="h1" className={styles.title}>
        Page not found
      </Text>
      <Text as="p" className={styles.body}>
        There is no page at this address.
      </Text>
      {isAuthenticated ? (
        <Link to="/dashboard" className={styles.link}>
          Go to Home
        </Link>
      ) : (
        <Link to="/login" className={styles.link}>
          Go to sign in
        </Link>
      )}
    </div>
  );
}
