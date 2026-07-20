import { Navigate, useLocation } from 'react-router-dom';
import { Spinner } from '@fluentui/react-components';
import { useAppSelector } from '../../app/hooks';
import { selectAuthStatus } from '../../features/auth/authSlice';

interface ProtectedRouteProps {
  children: React.ReactNode;
}

/**
 * Session guard: gates on the auth status the B3 bootstrap probe resolves (D6).
 * DEV_BYPASS_AUTH is gone for good — a one-way door (D20, ADR-0019): the zero-backend
 * static demo does not exist anymore; the demo capability returns later as the labeled
 * MSW-worker portfolio mode, never as an auth bypass.
 */
export function ProtectedRoute({ children }: ProtectedRouteProps) {
  const status = useAppSelector(selectAuthStatus);
  const location = useLocation();

  if (status === 'unknown') {
    // Boot probe in flight: hold, don't flash the login page at authenticated users.
    return (
      <Spinner size="large" aria-label="Checking your session" style={{ marginTop: '30vh' }} />
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
