import { Link } from 'react-router-dom';
import { makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { useAppSelector } from '../app/hooks';
import { selectAuthStatus } from '../features/auth/authSlice';
import { colors } from '../theme/tokens';

/**
 * What an address that names no page shows: App.tsx gives it every such address.
 *
 * The address stays in the bar, so the visitor can see what was wrong with it, and the page offers
 * one way back. Signed in, it renders inside the app shell and the way back is Home. Signed out,
 * it stands alone and the way back is the sign-in page, which is where Home would send the visitor
 * anyway. App.tsx draws it through `ShellOrBare`, which picks between the two and holds the page
 * while the session is checked.
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

export function NotFoundPage() {
  const styles = useStyles();
  const isAuthenticated = useAppSelector(selectAuthStatus) === 'authenticated';
  // Alone, the page is the main landmark, as the sign-in page's form panel is. In the shell,
  // AppLayout's <main> already holds it, and a second one inside would nest.
  const Container = isAuthenticated ? 'div' : 'main';

  return (
    <Container className={mergeClasses(styles.page, !isAuthenticated && styles.standalone)}>
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
    </Container>
  );
}
