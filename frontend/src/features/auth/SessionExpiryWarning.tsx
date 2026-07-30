import { useCallback, useEffect, useId, useState, type FormEvent } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  makeStyles,
} from '@fluentui/react-components';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { CONNECTION_FAILED } from '../../api/problemMessages';
import { formatLockHorizon } from '../../utils/format';
import { useAppDispatch, useAppSelector } from '../../app/hooks';
import { apiSlice, useReauthenticateMutation } from '../api/apiSlice';
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
 *
 * **U6.7: each branch offers only what it can actually do.** Both branches used to show the same two
 * buttons, and on the ABSOLUTE branch that was a promise the app could not keep: the copy says the
 * session "ends on a fixed schedule, whether or not you are using it", and "Stay signed in" fires
 * `getMe`, which slides the INACTIVITY deadline only. `absoluteExpiresAt` is
 * `sessionCreatedAt + window` and never moves, so the click did nothing and — by the rule above,
 * correctly — the dialog did not close either. The screen was offering what the sentence beside it
 * called impossible.
 *
 * The answer is not to make the cap extendable; a cap you can push is decoration. It is to
 * RE-AUTHENTICATE, which starts a different session. So the absolute branch asks for the password
 * and the inactivity branch keeps its keep-alive, and neither shows the other's action.
 *
 * The third property above is what makes this honest for free: re-authentication does not dismiss
 * anything either. It succeeds, the session's `Session` tag is invalidated, `AuthBootstrap`'s live
 * `getMe` subscription refetches, `sessionMiddleware` relearns the policy from that response, and
 * the dialog closes because the deadline genuinely moved.
 */

const TICK_MS = 1_000;

const useStyles = makeStyles({
  reauth: { display: 'flex', flexDirection: 'column', gap: '12px', marginTop: '16px' },
});

/**
 * What to say when re-authentication fails.
 *
 * `ACCOUNT_LOCKED` is a 429 and, per ADR-0012, the API returns it ONLY when the password was
 * CORRECT — a wrong password against a locked account still answers the generic 401, so the lock is
 * never an oracle for a guesser. That is why these two are separate messages and why neither hints
 * at the other's condition.
 */
function reauthMessage(problem: ApiProblem): string {
  if (problem.errorCode === 'ACCOUNT_LOCKED') {
    return `Too many failed attempts. Try again in about ${formatLockHorizon(
      problem.retryAfterSeconds ?? 15 * 60,
    )}.`;
  }
  if (problem.status === 401) {
    return "That password didn't match. Please try again.";
  }
  if (problem.status === 429) {
    // The BFF's own auth rate limiter rather than the account lock: no unlock time to quote.
    return 'Too many attempts just now. Wait a moment and try again.';
  }
  if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
    return CONNECTION_FAILED;
  }
  return "Couldn't sign you in right now. Please try again.";
}

function formatRemaining(ms: number): string {
  const totalSeconds = Math.max(0, Math.ceil(ms / 1000));
  return `${Math.floor(totalSeconds / 60)}:${String(totalSeconds % 60).padStart(2, '0')}`;
}

export function SessionExpiryWarning() {
  const status = useAppSelector(selectAuthStatus);
  const dispatch = useAppDispatch();
  const styles = useStyles();
  const errorId = useId();
  const [remainingMs, setRemainingMs] = useState<number | null>(null);
  const [ending, setEnding] = useState(false);
  const [password, setPassword] = useState('');
  const [reauthError, setReauthError] = useState<string | null>(null);
  const [reauthenticate, { isLoading: reauthPending }] = useReauthenticateMutation();

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

  // Recover a policy this component never saw. Authenticated with no known deadline means it can do
  // nothing at all — no warning, no sign-out — and nothing else would ever repair that: there is no
  // polling, and the bootstrap probe has already run. One request fixes it, once per mount.
  //
  // It should never fire in production, where the module lives as long as the page. It fires
  // constantly under HMR, which is how the gap was found: a module reload resets the policy, the
  // dialog silently stopped working, and the countdown never appeared. A silent no-op with no route
  // back is worth one request to close, whatever caused it.
  useEffect(() => {
    if (status !== 'authenticated' || getSessionDeadline() !== null) return;
    void dispatch(apiSlice.endpoints.getMe.initiate(undefined, { forceRefetch: true }));
  }, [status, dispatch]);

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

  // Re-authenticate. Deliberately does NOT close the dialog or clear the countdown on success:
  // the tag invalidation refetches /me, that response moves the deadline, and the deadline is what
  // unmounts this. An optimistic close would hide the warning after a request that never landed.
  const submitReauth = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setReauthError(null);
    try {
      await reauthenticate({ password }).unwrap();
      setPassword('');
    } catch (caught) {
      // A 401 INVALID_CREDENTIALS is exempt from the global session-expiry rule (D3), which is what
      // lets a typo stay a typo instead of ending the session it was meant to save.
      setReauthError(reauthMessage(caught as ApiProblem));
    }
  };

  const absolute = isAbsoluteDeadline();
  const warningDue = remainingMs !== null && remainingMs > 0 && remainingMs <= WARNING_LEAD_MS;
  if (status !== 'authenticated' || !warningDue) {
    return null;
  }

  return (
    <Dialog open modalType="alert">
      <DialogSurface>
        {/* One render path for both branches: the form wraps the body either way and only the
            absolute branch gives it anything to submit. Two paths would be two places to forget
            the countdown. */}
        <form onSubmit={absolute ? submitReauth : (event) => event.preventDefault()}>
          <DialogBody>
            <DialogTitle>Session about to expire</DialogTitle>
            <DialogContent>
              {absolute
                ? 'This session has reached its maximum length. For your security it ends on a fixed schedule, whether or not you are using it.'
                : 'You have been inactive for a while.'}{' '}
              {/* Not a live region: announcing a number every second would make a screen reader
                  unusable. The alert dialog announces this body once on open, which is the point. */}
              You will be signed out in <strong>{formatRemaining(remainingMs)}</strong>.
              {absolute && (
                <div className={styles.reauth}>
                  {/* The cap cannot be extended, so the way to keep working is to start a new
                      session. Only the password: the BFF reads the identity from the session, so
                      this cannot sign a different person in behind this page. */}
                  {/* No `aria-describedby` here on purpose. `Field` wires the hint to the input
                      through that very attribute, so setting it would REPLACE the hint's linkage and
                      silently stop it being announced — trading one message for another. The error
                      announces itself instead: it is a `role="alert"`, so it is read when it appears
                      rather than only when the field is next focused. */}
                  <Field label="Enter your password to continue" hint="This starts a new session.">
                    <Input
                      type="password"
                      autoComplete="current-password"
                      value={password}
                      onChange={(_, data) => {
                        setPassword(data.value);
                        setReauthError(null);
                      }}
                      disabled={reauthPending || ending}
                    />
                  </Field>
                  {reauthError && (
                    <MessageBar intent="error" role="alert" id={errorId}>
                      <MessageBarBody>{reauthError}</MessageBarBody>
                    </MessageBar>
                  )}
                </div>
              )}
            </DialogContent>
            <DialogActions>
              {/* No X and no Escape, and that is deliberate — on a security prompt "close" cannot say
                  whether it meant stay or go. The answer to "the only action is the unsafe one" is a
                  second explicit action, not a dismissal. */}
              <Button
                appearance="secondary"
                type="button"
                onClick={() => void endSession(true)}
                disabled={ending || reauthPending}
              >
                Sign out now
              </Button>
              {absolute ? (
                <Button
                  appearance="primary"
                  type="submit"
                  // The spinner replaces the text, which would leave the control with NO accessible
                  // name for exactly as long as it is busy — the one moment someone most needs to be
                  // told what is happening. Named while pending, and left to its own text otherwise.
                  aria-label={reauthPending ? 'Signing in' : undefined}
                  disabled={password.length === 0 || reauthPending || ending}
                >
                  {reauthPending ? <Spinner size="tiny" /> : 'Sign in again'}
                </Button>
              ) : (
                // Kept ONLY here: this is the branch where a keep-alive genuinely moves the
                // deadline it claims to move.
                <Button appearance="primary" type="button" onClick={staySignedIn} disabled={ending}>
                  Stay signed in
                </Button>
              )}
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
