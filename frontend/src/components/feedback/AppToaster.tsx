import { useEffect, useRef } from 'react';
import { Toaster, useToastController } from '@fluentui/react-components';
import { useAppSelector } from '../../app/hooks';
import { selectAuthStatus } from '../../features/auth/authSlice';
import { appToasterId, dismissProblemToasts } from './useProblemToast';

/**
 * Mounted exactly once in App, inside FluentProvider and the store's Provider.
 *
 * When a session ends, signed out or expired, the error toasts still open go with it: what they
 * said was about that session, "You're still signed in." from a sign-out that failed before one
 * that worked among them, and they would stay over the sign-in page. A toast that is not an
 * error, such as the demo's "You have a new copy.", is left where it is.
 */
export function AppToaster() {
  const status = useAppSelector(selectAuthStatus);
  const { dismissToast } = useToastController(appToasterId);
  const previousStatus = useRef(status);

  useEffect(() => {
    const was = previousStatus.current;
    previousStatus.current = status;
    if (was === 'authenticated' && (status === 'anonymous' || status === 'expired')) {
      dismissProblemToasts(dismissToast);
    }
  }, [status, dismissToast]);

  // The toaster goes, and its toasts with it: nothing is left to take back.
  useEffect(() => () => dismissProblemToasts(dismissToast), [dismissToast]);

  return <Toaster toasterId={appToasterId} position="top-end" pauseOnHover />;
}
