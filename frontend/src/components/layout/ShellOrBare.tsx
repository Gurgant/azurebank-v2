import type { ReactNode } from 'react';
import { useAppSelector } from '../../app/hooks';
import { selectAuthStatus } from '../../features/auth/authSlice';
import { ProtectedRoute } from './ProtectedRoute';
import { ProtectedShell } from './ProtectedShell';

/**
 * Renders its child inside the app shell when there is a session, and bare when there is not,
 * once the session check has said which.
 *
 * One route, two surroundings. `/contact` is reachable from the sign-in page (where there is
 * nothing to navigate and the shell would be a lie) and from the avatar sheet (where losing the
 * navigation would strand you with only the browser's back button). Two pages would say the same
 * words twice and drift the day one of them is edited. The address that names no page is drawn the
 * same way.
 *
 * Until the check has answered, the child cannot tell which way back is true, and a check the
 * service could not answer leaves it that way (the auth slice moves to 'anonymous' only on a 4xx).
 * Drawn bare, it would offer sign-in to a visitor whose session may be alive, which reads as being
 * signed out. So while the status is 'unknown' it waits behind `ProtectedRoute`, as a guarded page
 * does: the check's spinner, then the page that says the service is unavailable and offers to try
 * again. 'expired' is drawn bare, as 'anonymous' is: the session is known to be gone.
 *
 * It never redirects. An anonymous visitor sees the page, which is the point: `/contact` is where
 * the app says it is a portfolio project rather than a bank.
 */
export function ShellOrBare({ children }: { children: ReactNode }) {
  const status = useAppSelector(selectAuthStatus);
  if (status === 'unknown') return <ProtectedRoute>{children}</ProtectedRoute>;
  return status === 'authenticated' ? <ProtectedShell>{children}</ProtectedShell> : <>{children}</>;
}

export default ShellOrBare;
