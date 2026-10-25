import { useNavigate } from 'react-router-dom';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { SIGN_OUT_FAILED } from '../../api/problemMessages';
import { useAppSelector } from '../../app/hooks';
import { useProblemToast } from '../../components/feedback';
import { selectCurrentUser } from '../../features/auth/authSlice';
import { useLogoutMutation } from '../../features/api/apiSlice';
import { AppLayout } from './AppLayout';
import { ProtectedRoute } from './ProtectedRoute';

interface ProtectedShellProps {
  children: React.ReactNode;
}

/**
 * The ONE composition of guard + app shell for standard protected pages: session gate
 * (ProtectedRoute), then AppLayout fed with the REAL session user and the REAL logout.
 * Full-screen flows that deliberately have no shell (the transfer wizard) keep using
 * bare ProtectedRoute.
 */
export function ProtectedShell({ children }: ProtectedShellProps) {
  return (
    <ProtectedRoute>
      <ShellWithSession>{children}</ShellWithSession>
    </ProtectedRoute>
  );
}

/** Rendered only past the guard — the user is guaranteed present here. */
function ShellWithSession({ children }: ProtectedShellProps) {
  const navigate = useNavigate();
  const user = useAppSelector(selectCurrentUser);
  // Pending from the press to the answer: both Sign out controls show it and take no second press.
  const [logout, { isLoading: signingOut }] = useLogoutMutation();
  const showProblem = useProblemToast();

  const handleLogout = async () => {
    try {
      // Server-side revocation + cookie deletion; the fulfilled action flips the auth
      // slice to 'anonymous'. Navigation happens ONLY on success: a failed revocation
      // must never masquerade as a logout — the session cookie would still be alive
      // behind a login screen.
      await logout().unwrap();
      navigate('/login', { replace: true });
    } catch (caught) {
      // A 401 says the session was already gone, which sessionMiddleware takes from here, and
      // AppToaster has cleared that session's toasts by now: a toast said now would stay over the
      // sign-in page. Any other failure left the visitor signed in, and the toast says so first.
      const problem = caught as ApiProblem;
      if (problem.status !== 401) showProblem(problem, SIGN_OUT_FAILED);
    }
  };

  return (
    <AppLayout
      user={user ? { name: `${user.firstName} ${user.lastName}`, email: user.email } : undefined}
      onLogout={() => {
        void handleLogout();
      }}
      signingOut={signingOut}
    >
      {children}
    </AppLayout>
  );
}
