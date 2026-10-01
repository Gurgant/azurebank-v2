import { useEffect } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import {
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  makeStyles,
} from '@fluentui/react-components';
import {
  SERVICE_UNAVAILABLE,
  SERVICE_UNAVAILABLE_TITLE,
  TRY_AGAIN,
} from '../../api/problemMessages';
import { useAppDispatch, useAppSelector } from '../../app/hooks';
import { apiSlice } from '../../features/api/apiSlice';
import { selectAuthStatus } from '../../features/auth/authSlice';
import { useWaitLanding } from '../../hooks/useWaitLanding';
import { colors, surfaces } from '../../theme/tokens';
import { AlertSlot, WaitHint } from '../feedback';
import { pageTitle } from './pageTitle';

interface ProtectedRouteProps {
  children: React.ReactNode;
}

const useStyles = makeStyles({
  checking: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    marginTop: '30vh',
  },
  unavailable: {
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
  bar: { maxWidth: '420px', textAlign: 'start' },
});

const selectBootProbe = apiSlice.endpoints.getMe.select();

/**
 * Session guard: gates on the auth status the B3 bootstrap probe resolves (D6).
 * DEV_BYPASS_AUTH is gone for good — a one-way door (D20, ADR-0019): the zero-backend
 * static demo does not exist anymore; the demo capability returns later as the labeled
 * MSW-worker portfolio mode, never as an auth bypass.
 *
 * A probe the service could not answer is not a sign-out. The status stays 'unknown' (the auth
 * slice moves to 'anonymous' only on a 4xx), so instead of the sign-in page — which would read as
 * "you were signed out" over a session that may be alive — the visitor gets a page that says the
 * service is unavailable and offers to try again. It carries its own `main` and `h1`, because the
 * shell that has them is not rendered until the session is known.
 *
 * "Try again" unmounts under the pointer (the wait comes back in its place), so focus is put back
 * on it if the probe fails again; a probe that succeeds lets the visitor in.
 *
 * The check and that page are one `main`, so that its alert is in the page, empty, from the start
 * of the check, and a failure is a change of it that a screen reader reads. The page is titled
 * for what it says, as `RouteError` titles its own, and gives the route its title back when the
 * service answers.
 */
export function ProtectedRoute({ children }: ProtectedRouteProps) {
  const status = useAppSelector(selectAuthStatus);
  const probe = useAppSelector(selectBootProbe);
  const dispatch = useAppDispatch();
  const location = useLocation();
  const styles = useStyles();

  const unavailable = status === 'unknown' && probe.isError;
  const checking = status === 'unknown' && !probe.isError;
  const { landingRef, arm } = useWaitLanding<HTMLButtonElement>(checking, probe.requestId);

  useEffect(() => {
    if (!unavailable) return;
    const before = document.title;
    const title = pageTitle(SERVICE_UNAVAILABLE_TITLE);
    document.title = title;
    return () => {
      // A route change meanwhile titled the page itself; that title stays.
      if (document.title === title) document.title = before;
    };
  }, [unavailable]);

  const tryAgain = () => {
    arm();
    // No subscription of its own: the bootstrap's live one keeps the answer.
    void dispatch(
      apiSlice.endpoints.getMe.initiate(undefined, { forceRefetch: true, subscribe: false }),
    );
  };

  if (unavailable || checking) {
    // While the probe is in flight: hold, don't flash the login page at authenticated users. The
    // hint offers no Stop: without the probe's answer there is nothing to show instead.
    return (
      <main className={unavailable ? styles.unavailable : styles.checking}>
        {unavailable && (
          <Text as="h1" className={styles.title}>
            {SERVICE_UNAVAILABLE_TITLE}
          </Text>
        )}
        {checking && <Spinner size="large" aria-label="Checking your session" />}
        <WaitHint active={checking} kind="read" />
        <AlertSlot className={styles.bar}>
          {unavailable && (
            <MessageBar intent="error">
              <MessageBarBody>{SERVICE_UNAVAILABLE}</MessageBarBody>
            </MessageBar>
          )}
        </AlertSlot>
        {unavailable && (
          <Button appearance="primary" ref={landingRef} onClick={tryAgain}>
            {TRY_AGAIN}
          </Button>
        )}
      </main>
    );
  }

  if (status !== 'authenticated') {
    // returnTo (state.from) brings the user back after login; reason drives the
    // "session expired" note — only ever set for a post-boot expiry (D3/D6).
    return (
      <Navigate
        to="/login"
        state={{ from: location, reason: status === 'expired' ? 'expired' : undefined }}
        replace
      />
    );
  }

  return <>{children}</>;
}
