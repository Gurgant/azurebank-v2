import type { ReactNode } from 'react';
import { useAppSelector } from '../../app/hooks';
import { selectAuthStatus } from '../../features/auth/authSlice';
import { ProtectedShell } from './ProtectedShell';

/**
 * Renders its child inside the app shell when there is a session, and bare when there is not.
 *
 * One route, two surroundings. `/about` is reachable from the sign-in page (where there is nothing
 * to navigate and the shell would be a lie) and from the avatar sheet (where losing the navigation
 * would strand you with only the browser's back button). Two pages would say the same words twice
 * and drift the day one of them is edited.
 *
 * NOT a guard: it never redirects. An anonymous visitor sees the page, which is the point — this is
 * where the app says it is a portfolio project rather than a bank.
 */
export function ShellOrBare({ children }: { children: ReactNode }) {
  const signedIn = useAppSelector(selectAuthStatus) === 'authenticated';
  return signedIn ? <ProtectedShell>{children}</ProtectedShell> : <>{children}</>;
}

export default ShellOrBare;
