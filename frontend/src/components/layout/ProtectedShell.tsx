import { useNavigate } from 'react-router-dom';
import { useAppSelector } from '../../app/hooks';
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
  const [logout] = useLogoutMutation();

  const handleLogout = async () => {
    try {
      // Server-side revocation + cookie deletion; the fulfilled action flips the auth
      // slice to 'anonymous' (and the guard would redirect anyway — the navigate just
      // makes it immediate and lands on a clean login, not a returnTo loop).
      await logout().unwrap();
    } finally {
      navigate('/login', { replace: true });
    }
  };

  return (
    <AppLayout
      user={user ? { name: `${user.firstName} ${user.lastName}`, email: user.email } : undefined}
      onLogout={() => {
        void handleLogout();
      }}
      onSettings={() => navigate('/settings')}
    >
      {children}
    </AppLayout>
  );
}
