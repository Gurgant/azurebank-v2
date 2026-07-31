import { useRouteError } from 'react-router-dom';
import { Button, Text, makeStyles } from '@fluentui/react-components';
import { colors, surfaces } from '../../theme/tokens';

/**
 * What a data router shows when a route throws.
 *
 * Not a new capability — a safety net the MIGRATION made necessary. A data router intercepts a render
 * error thrown in the route tree and, with no `errorElement`, renders React Router's own default
 * page: unstyled, "Unexpected Application Error", belonging to a library rather than to a bank.
 *
 * What it does NOT cover is worth knowing before trusting it. This is a ROUTE boundary, so it sees
 * the route tree only — the toaster, auth bootstrap, session warning and step-up modal render as
 * siblings ABOVE `RouterProvider` and go straight past it. Nothing else catches them either: the app
 * has no React error boundary at all (`main.tsx` renders `<StrictMode><App /></StrictMode>`, and
 * nothing implements `componentDidCatch`). A throw up there still blanks the screen — a pre-existing
 * gap this narrows rather than creates. Both halves are pinned in RouteError.test.tsx.
 *
 * Deliberately plain: it must not depend on anything that could be the thing that broke, so no
 * queries, no store reads, and a full reload rather than a router navigation.
 */
const useStyles = makeStyles({
  root: {
    minHeight: '100dvh',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '12px',
    padding: '24px',
    textAlign: 'center',
    backgroundColor: surfaces.canvas,
  },
  title: { fontSize: '20px', fontWeight: 600, color: colors.neutral[900] },
  body: { fontSize: '14px', color: colors.neutral[700], maxWidth: '420px' },
});

export function RouteError() {
  const styles = useStyles();
  const error = useRouteError();

  // Logged, not rendered: the message can carry request detail, and this screen is reachable by
  // anyone. The user gets a sentence they can act on instead.
  console.error('Route error', error);

  return (
    <div className={styles.root} role="alert">
      <Text as="h1" className={styles.title}>
        Something went wrong
      </Text>
      <Text className={styles.body}>
        This page could not be displayed. Your accounts and any completed transfers are unaffected.
      </Text>
      <Button appearance="primary" onClick={() => window.location.assign('/')}>
        Back to the dashboard
      </Button>
    </div>
  );
}

export default RouteError;
