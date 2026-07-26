import { useCallback, useEffect, useState } from 'react';
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
import { selectAuthStatus, sessionExpired, signedOut } from './authSlice';
import {
  WARNING_LEAD_MS,
  getSessionDeadline,
  isAbsoluteDeadline,
  syncFromProbe,
} from './sessionActivity';

/**
 * D14: the warning before the session ends, and the sign-out that follows it.
 *
 * The previous version had neither. Its "you will be signed out in about 2 minutes" was the
 * constant `WARNING_LEAD_MINUTES` interpolated once at render, so it read the same after thirty
 * seconds and after thirty minutes; and nothing anywhere signed the user out. The only route to a
 * signed-out state was a 401 arriving on a real response, which an idle tab never makes — so the
 * dialog it showed was a promise the app had no way of keeping.
 *
 * Three properties hold it together now:
 *
 * **The clock is read, never trusted.** Every tick recomputes `deadline - Date.now()`. Browsers
 * throttle timers in background tabs, so a `setTimeout(remaining)` would be the one genuinely
 * fragile design available here: it fires late or not at all, and nothing notices. A ticking
 * comparison against a stored deadline degrades to "updates less often" and never to "wrong".
 *
 * **Expiry is confirmed before it is acted on.** Reaching zero prompts one `session-status` probe —
 * the single route the BFF excludes from activity (ADR-0018), so asking does not change the answer.
 * Another tab may have kept the session warm; signing someone out of a live session is a worse
 * failure than warning them late.
 *
 * **Nothing is dismissed optimistically.** "Stay signed in" no longer hides the dialog on click. It
 * fires the keep-alive and lets the dialog close when the deadline actually moves, so an offline
 * click leaves the warning on screen — which is the truth.
 */

const TICK_MS = 1_000;

function formatRemaining(ms: number): string {
  const totalSeconds = Math.max(0, Math.ceil(ms / 1000));
  return `${Math.floor(totalSeconds / 60)}:${String(totalSeconds % 60).padStart(2, '0')}`;
}

export function SessionExpiryWarning() {
  const status = useAppSelector(selectAuthStatus);
  const dispatch = useAppDispatch();
  const [remainingMs, setRemainingMs] = useState<number | null>(null);
  const [ending, setEnding] = useState(false);

  // The countdown. `null` means the deadline is not known yet — before the first /bff/auth/me
  // response, or against a BFF too old to declare its window — and it stays null rather than
  // guessing, because guessing the window is the defect this replaces.
  useEffect(() => {
    if (status !== 'authenticated') {
      setRemainingMs(null);
      return;
    }
    const tick = () => {
      const deadline = getSessionDeadline();
      setRemainingMs(deadline === null ? null : deadline - Date.now());
    };
    tick();
    const id = window.setInterval(tick, TICK_MS);
    return () => window.clearInterval(id);
  }, [status]);

  // A backgrounded tab is where this tab's idea of the deadline goes stale: its own timers are
  // throttled while another tab may be keeping the session alive. One probe on return costs a
  // request nobody notices and is the only thing that corrects the drift.
  useEffect(() => {
    if (status !== 'authenticated') return;
    const resync = () => {
      if (document.visibilityState !== 'visible') return;
      void dispatch(apiSlice.endpoints.getSessionStatus.initiate(undefined, { forceRefetch: true }))
        .unwrap()
        .then((probe) => {
          if (probe.isAuthenticated) syncFromProbe(probe);
        })
        .catch(() => {
          // A dead session answers 401, which sessionMiddleware already routes.
        });
    };
    document.addEventListener('visibilitychange', resync);
    return () => document.removeEventListener('visibilitychange', resync);
  }, [status, dispatch]);

  /**
   * End the session locally, whatever the server says.
   *
   * `ProtectedShell` navigates only on a successful logout, deliberately: a failed revocation must
   * never masquerade as a logout while the cookie is still alive. That reasoning is right *there*,
   * where the session is healthy — and wrong here, where it is already dying. A 401 from logout
   * means the session is gone, which is the outcome asked for, not a failure to report. Treating it
   * as one is what would trap someone in this dialog forever.
   */
  const endSession = useCallback(
    async (deliberate: boolean) => {
      setEnding(true);
      try {
        await dispatch(apiSlice.endpoints.logout.initiate()).unwrap();
        dispatch(deliberate ? signedOut() : sessionExpired());
      } catch (caught) {
        const failedStatus = (caught as { status?: number | string } | undefined)?.status;
        // 401: already gone, so a deliberate sign-out still counts as one. Anything else leaves
        // the cookie's fate unknown, and 'expired' is the honest word for that.
        dispatch(deliberate && failedStatus === 401 ? signedOut() : sessionExpired());
      } finally {
        // Financial data must not outlive the session it was fetched under.
        dispatch(apiSlice.util.resetApiState());
        setEnding(false);
      }
    },
    [dispatch],
  );

  const expired = remainingMs !== null && remainingMs <= 0;

  useEffect(() => {
    if (!expired || ending) return;
    let cancelled = false;

    void (async () => {
      try {
        const probe = await dispatch(
          apiSlice.endpoints.getSessionStatus.initiate(undefined, { forceRefetch: true }),
        ).unwrap();
        if (cancelled) return;
        if (probe.isAuthenticated) {
          syncFromProbe(probe);
          const deadline = getSessionDeadline();
          // Another tab kept it alive — resume counting rather than ending a live session.
          if (deadline !== null && deadline > Date.now()) return;
        }
      } catch {
        // The probe itself failed. A security deadline fails closed: end the session.
      }
      if (!cancelled) await endSession(false);
    })();

    return () => {
      cancelled = true;
    };
  }, [expired, ending, dispatch, endSession]);

  // Fires the keep-alive and nothing else. The dialog closes when the deadline moves, which only
  // happens if the request actually reached the BFF.
  const staySignedIn = () => {
    void dispatch(apiSlice.endpoints.getMe.initiate(undefined, { forceRefetch: true }));
  };

  const warningDue = remainingMs !== null && remainingMs > 0 && remainingMs <= WARNING_LEAD_MS;
  if (status !== 'authenticated' || !warningDue) {
    return null;
  }

  return (
    <Dialog open modalType="alert">
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Session about to expire</DialogTitle>
          <DialogContent>
            {isAbsoluteDeadline()
              ? 'This session has reached its maximum length. For your security it ends on a fixed schedule, whether or not you are using it.'
              : 'You have been inactive for a while.'}{' '}
            {/* Not a live region: announcing a number every second would make a screen reader
                unusable. The alert dialog announces this body once on open, which is the point. */}
            You will be signed out in <strong>{formatRemaining(remainingMs)}</strong>.
          </DialogContent>
          <DialogActions>
            {/* No X and no Escape, and that is deliberate — on a security prompt "close" cannot say
                whether it meant stay or go. The answer to "the only action is the unsafe one" is a
                second explicit action, not a dismissal. */}
            <Button appearance="secondary" onClick={() => void endSession(true)} disabled={ending}>
              Sign out now
            </Button>
            <Button appearance="primary" onClick={staySignedIn} disabled={ending}>
              Stay signed in
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
