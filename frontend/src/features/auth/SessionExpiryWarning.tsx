import { useEffect, useState } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
} from '@fluentui/react-components';
import { useAppDispatch, useAppSelector } from '../../app/hooks';
import { apiSlice } from '../api/apiSlice';
import { selectAuthStatus } from './authSlice';
import {
  getLastServerActivity,
  INACTIVITY_TIMEOUT_MINUTES,
  WARNING_LEAD_MINUTES,
} from './sessionActivity';

const CHECK_INTERVAL_MS = 30_000;
const WARNING_AT_MS = (INACTIVITY_TIMEOUT_MINUTES - WARNING_LEAD_MINUTES) * 60_000;

/**
 * D14: warns at T-2min before the BFF's 30-min inactivity timeout, using the client
 * mirror of LastActivity (sessionMiddleware timestamps every server response). "Stay
 * signed in" fires GET /bff/auth/me — the deliberate keep-alive; the app never polls
 * to stay alive silently. Unsubmitted form state is accepted loss by policy: no
 * financial intent is ever parked in Web Storage.
 */
export function SessionExpiryWarning() {
  const status = useAppSelector(selectAuthStatus);
  const dispatch = useAppDispatch();
  const [warningDue, setWarningDue] = useState(false);

  useEffect(() => {
    if (status !== 'authenticated') {
      return;
    }
    const timer = setInterval(() => {
      const last = getLastServerActivity();
      setWarningDue(last !== null && Date.now() - last >= WARNING_AT_MS);
    }, CHECK_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [status]);

  const staySignedIn = () => {
    setWarningDue(false);
    void dispatch(apiSlice.endpoints.getMe.initiate(undefined, { forceRefetch: true }));
  };

  if (status !== 'authenticated' || !warningDue) {
    return null;
  }

  return (
    <Dialog open modalType="alert">
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Session about to expire</DialogTitle>
          <DialogContent>
            You have been inactive for a while. For your security, you will be signed out in about{' '}
            {WARNING_LEAD_MINUTES} minutes.
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={staySignedIn}>
              Stay signed in
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
