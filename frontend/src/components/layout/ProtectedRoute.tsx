import { Navigate, useLocation } from 'react-router-dom';
import { useAppSelector } from '../../app/hooks';
import { selectIsAuthenticated } from '../../features/auth/authSlice';

// ============================================
// TYPES
// ============================================

interface ProtectedRouteProps {
  children: React.ReactNode;
}

// ============================================
// DEV MODE - Set to true to bypass authentication for UI testing
// ============================================
const DEV_BYPASS_AUTH = true; // TODO: Set to false for production

// ============================================
// COMPONENT
// ============================================

/**
 * ProtectedRoute - Wraps routes that require authentication
 * Redirects to login if user is not authenticated
 */
export function ProtectedRoute({ children }: ProtectedRouteProps) {
  const isAuthenticated = useAppSelector(selectIsAuthenticated);
  const location = useLocation();

  // DEV MODE: Skip auth check for UI testing
  if (DEV_BYPASS_AUTH) {
    return <>{children}</>;
  }

  if (!isAuthenticated) {
    // Redirect to login page, preserving the intended destination
    return <Navigate to="/login" state={{ from: location }} replace />;
  }

  return <>{children}</>;
}
