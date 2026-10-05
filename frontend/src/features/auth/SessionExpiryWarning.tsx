import {
  useCallback,
  useEffect,
  useId,
  useState,
  useSyncExternalStore,
  type FormEvent,
} from 'react';
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
import { isServiceOutage, type ApiProblem } from '../../api/problemBaseQuery';
import { CONNECTION_FAILED, SERVICE_UNAVAILABLE, SIGN_OUT_FAILED } from '../../api/problemMessages';
import { WaitHint } from '../../components/feedback';
import { useWaitLanding } from '../../hooks/useWaitLanding';
import { atMedia } from '../../theme/breakpoints';
import { formatLockHorizon } from '../../utils/format';
import { useAppDispatch, useAppSelector } from '../../app/hooks';
import { apiSlice, useReauthenticateMutation } from '../api/apiSlice';
import { getDemoCopySnapshot, isDemoCopyOwner, subscribeDemoCopy } from '../demo/demoCopyStorage';
import { COPY_HAS_ENDED, STAY_SIGNED_IN } from '../demo/demoWords';
import { selectAuthStatus, selectCurrentUser, sessionExpired, signedOut } from './authSlice';
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
 * RE-AUTHENTICATE, which starts a different session. So each branch offers the one way to stay
 * that works there, and no branch shows another's:
 *
 * - the INACTIVITY branch keeps its keep-alive, "Stay signed in";
 * - the ABSOLUTE branch asks for the password, with "Sign in again";
 * - the ABSOLUTE branch on the demo, for the owner of the copy this browser keeps, has one button
 *   where the password field would be, and no "Sign in again". It is named "Stay signed in" too,
 *   and it is not the keep-alive: it signs in again with the kept password. Nobody chose that
 *   password or knows it by heart. The claim drew it and the browser kept it
 *   (src/features/demo/demoCopyStorage.ts), so a field would only send the owner to look for it.
 *
 * THE OWNER is decided as the dashboard's panel decides it (src/features/demo/DemoCopyPanel.tsx):
 * the page is the demo, and the kept copy's address is the signed-in user's. Anyone else at the
 * cap is asked for the password: off the demo, on a browser that keeps no copy, on a browser that
 * keeps another copy than the one signed in. The kept copy is read each time the dialog is drawn
 * and once more when the button is pressed, and it is never held here. A copy that another tab
 * replaced since is not this session's copy: its password is not sent, and the dialog's next
 * draw, a tick of the countdown away, asks for the password.
 *
 * The third property above is what makes this honest for free: re-authentication does not dismiss
 * anything either. It succeeds, the session's `Session` tag is invalidated, `AuthBootstrap`'s live
 * `getMe` subscription refetches, `sessionMiddleware` relearns the policy from that response, and
 * the dialog closes because the deadline genuinely moved.
 */

const TICK_MS = 1_000;

const useStyles = makeStyles({
  reauth: { display: 'flex', flexDirection: 'column', gap: '12px', marginTop: '16px' },
  // On a narrow screen as wide as the dialog, as "Sign out now" is under it there: Fluent stacks a
  // dialog's actions at full width up to 480px. From there up, as wide as its words: left to the
  // column it would be a bar across the dialog over a "Sign out now" of ordinary size.
  stay: { [atMedia.sm]: { alignSelf: 'flex-start' } },
  signOutError: { marginTop: '16px' },
  hint: { marginTop: '12px' },
});

/**
 * What to say when re-authentication fails.
 *
 * `ACCOUNT_LOCKED` is a 429 and, per ADR-0012, the API returns it ONLY when the password was
 * CORRECT — a wrong password against a locked account still answers the generic 401, so the lock is
 * never an oracle for a guesser. That is why these two are separate messages and why neither hints
 * at the other's condition.
 *
 * `owner` says who asked: the owner of the demo copy this browser keeps, whose button sent the
 * kept password. It changes the 401's line and no other. A password nobody typed was not
 * mistyped: the API answers a copy that has ended as it answers a wrong password, so for the
 * owner that answer means the copy has ended. The copy stays kept all the same. Forgetting it
 * here would put a password field in front of the owner in the open dialog; the sign-in page
 * forgets it, when it is offered there and refused or once it is past its end.
 */
function reauthMessage(problem: ApiProblem, owner: boolean): string {
  if (problem.errorCode === 'ACCOUNT_LOCKED') {
    return `Too many failed attempts. Try again in about ${formatLockHorizon(
      problem.retryAfterSeconds ?? 15 * 60,
    )}.`;
  }
  if (problem.status === 401) {
    return owner ? COPY_HAS_ENDED : "That password didn't match. Please try again.";
  }
  if (problem.status === 429) {
    // The BFF's own auth rate limiter rather than the account lock: no unlock time to quote.
    return 'Too many attempts just now. Wait a moment and try again.';
  }
  // Before the transport branch: a request with no answer in 65 s is `NETWORK` too, and it is the
  // service that did not answer, not the visitor's connection.
  if (isServiceOutage(problem)) {
    return SERVICE_UNAVAILABLE;
  }
  if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
    return CONNECTION_FAILED;
  }
  return "Couldn't sign you in right now. Please try again.";
}

/**
 * What to say when "Sign out now" could not sign the visitor out: first that they are still signed
 * in, then why.
 *
 * Why is the connection sentence for a failed connection or a body that could not be read; the
 * outage sentence otherwise — the service is down, gave no answer in 65 s, or turned the request
 * away for a reason the visitor can do nothing about but try again later.
 */
function signOutMessage(problem: ApiProblem | undefined): string {
  const why =
    problem !== undefined &&
    !isServiceOutage(problem) &&
    (problem.status === 'NETWORK' || problem.status === 'PARSE')
      ? CONNECTION_FAILED
      : SERVICE_UNAVAILABLE;
  return `${SIGN_OUT_FAILED} ${why}`;
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
  const descriptionId = useId();
  const [remainingMs, setRemainingMs] = useState<number | null>(null);
  // The sign-out on its way, if any: the one the visitor asked for, or the deadline's.
  const [endingBy, setEndingBy] = useState<'visitor' | 'deadline' | null>(null);
  const ending = endingBy !== null;
  // "Sign out now" is disabled while it is pending, and a browser hands the focus of a control it
  // disables to the page. When the sign-out fails the dialog stays, so focus goes back to it.
  const signOutLanding = useWaitLanding<HTMLButtonElement>(endingBy === 'visitor', undefined);
  const [password, setPassword] = useState('');
  const [reauthError, setReauthError] = useState<string | null>(null);
  const [signOutError, setSignOutError] = useState<string | null>(null);
  const [reauthenticate, { isLoading: reauthPending }] = useReauthenticateMutation();
  // The demo copy this browser keeps, read from its key each time this is drawn, and whether the
  // signed-in visitor is its owner. Off the demo there is no copy and no owner, and the key is not
  // read to find that out (src/features/demo/demoCopyStorage.ts).
  const user = useAppSelector(selectCurrentUser);
  const keptCopy = useSyncExternalStore(subscribeDemoCopy, getDemoCopySnapshot);
  const owner = isDemoCopyOwner(keptCopy, user);

  // A failed sign-out belongs to the session it failed in. Should that session end some other way
  // (a 401 elsewhere), its words must not greet the visitor in the next one.
  const [statusSeen, setStatusSeen] = useState(status);
  if (statusSeen !== status) {
    setStatusSeen(status);
    setSignOutError(null);
  }

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
   * End the session: at the deadline whatever the server says, and on "Sign out now" only when
   * the server has.
   *
   * `ProtectedShell` navigates only on a successful logout, deliberately: a failed revocation must
   * never masquerade as a logout while the cookie is still alive. That reasoning is right *there*,
   * where the session is healthy — and wrong here, where it is already dying. A 401 from logout
   * means the session is gone, which is the outcome asked for, not a failure to report. Treating it
   * as one is what would trap someone in this dialog forever.
   *
   * Any other failure of "Sign out now" is ProtectedShell's case again. The BFF ends a session and
   * answers at once, without the API, so a logout that failed otherwise most likely never ran and
   * the cookie is alive: a sign-in page would say "signed out" and "expired" would say "timed
   * out", and neither happened. So nothing is ended and nothing is cleared: the dialog stays, says
   * that the visitor is still signed in and why, and "Sign out now", focused again, can be pressed
   * again. The deadline still ends the session if nothing else does — at zero the end is not a
   * request but a fact, and 'expired' is the honest word for it whatever the logout answers.
   */
  const endSession = useCallback(
    async (deliberate: boolean) => {
      setEndingBy(deliberate ? 'visitor' : 'deadline');
      // A new attempt replaces the words of the last failure, a failed sign-in's included: the
      // dialog says one thing at a time.
      setSignOutError(null);
      if (deliberate) setReauthError(null);
      let ended = true;
      try {
        await dispatch(apiSlice.endpoints.logout.initiate()).unwrap();
        dispatch(deliberate ? signedOut() : sessionExpired());
      } catch (caught) {
        const problem = caught as ApiProblem | undefined;
        if (deliberate && problem?.status !== 401) {
          ended = false;
          setSignOutError(signOutMessage(problem));
        } else {
          // 401: already gone, so a deliberate sign-out still counts as one. At the deadline,
          // anything else leaves the cookie's fate unknown, and 'expired' is the honest word.
          dispatch(deliberate ? signedOut() : sessionExpired());
        }
      } finally {
        // Financial data must not outlive the session it was fetched under.
        if (ended) dispatch(apiSlice.util.resetApiState());
        setEndingBy(null);
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
  // happens if the request actually reached the BFF. A failed sign-out's words go: the visitor has
  // chosen to stay.
  const staySignedIn = () => {
    setSignOutError(null);
    void dispatch(apiSlice.endpoints.getMe.initiate(undefined, { forceRefetch: true }));
  };

  // Re-authenticate. Deliberately does NOT close the dialog or clear the countdown on success:
  // the tag invalidation refetches /me, that response moves the deadline, and the deadline is what
  // unmounts this. An optimistic close would hide the warning after a request that never landed.
  //
  // What is sent is what the form that was drawn stands for: the typed password, or, from the
  // owner's button, the password the browser keeps.
  const submitReauth = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setReauthError(null);
    setSignOutError(null);
    let sent = password;
    if (owner) {
      // Read from the key now, and not taken from the last draw, which can be a second old.
      // Another tab may have started over since: the session is then the new copy's, and the
      // password kept for the old one would be refused and worded as a copy that has ended, which
      // would be false. Nothing is sent then. The next draw, a tick away, asks for the password.
      const kept = getDemoCopySnapshot();
      if (kept === null || !isDemoCopyOwner(kept, user)) return;
      sent = kept.password;
    }
    try {
      await reauthenticate({ password: sent }).unwrap();
      setPassword('');
    } catch (caught) {
      // A 401 INVALID_CREDENTIALS is exempt from the global session-expiry rule (D3), which is what
      // lets a typo stay a typo instead of ending the session it was meant to save.
      setReauthError(reauthMessage(caught as ApiProblem, owner));
    }
  };

  const absolute = isAbsoluteDeadline();
  // A re-authentication or a sign-out on its way: the one wait this dialog shows words for.
  const waiting = reauthPending || ending;
  // "Sign out now" keeps the dialog up until the visitor has an answer: while it is on its way,
  // and once it has failed, with the words, until they choose again. A failure the BFF answered
  // itself (a 429, say) counted as activity there and moves the deadline here, so without this the
  // dialog could close a second after the failure and leave the visitor signed in without a word.
  // Only the sign-out they asked for: the deadline's has hidden the dialog at zero, and activity
  // that moves the deadline while it is on its way must not bring the dialog back.
  const signingOut = endingBy === 'visitor' || signOutError !== null;
  const warningDue =
    remainingMs !== null && remainingMs > 0 && (remainingMs <= WARNING_LEAD_MS || signingOut);
  if (status !== 'authenticated' || !warningDue) {
    return null;
  }

  return (
    <Dialog open modalType="alert">
      {/* Described by the sentence and the countdown only: the hint and a failure's alert, also in
          the content, speak for themselves, and a description that held them would read them
          again. */}
      <DialogSurface aria-describedby={descriptionId}>
        {/* One render path for both branches: the form wraps the body either way and only the
            absolute branch gives it anything to submit. Two paths would be two places to forget
            the countdown. */}
        <form onSubmit={absolute ? submitReauth : (event) => event.preventDefault()}>
          <DialogBody>
            <DialogTitle>Session about to expire</DialogTitle>
            <DialogContent>
              {/* Not a live region: announcing a number every second would make a screen reader
                  unusable. It is the dialog's description, read with it once on open, which is
                  the point. */}
              <span id={descriptionId}>
                {absolute
                  ? 'This session has reached its maximum length. For your security it ends on a fixed schedule, whether or not you are using it.'
                  : 'You have been inactive for a while.'}{' '}
                You will be signed out in <strong>{formatRemaining(remainingMs)}</strong>.
              </span>
              {absolute && (
                <div className={styles.reauth}>
                  {/* The cap cannot be extended, so the way to keep working is to start a new
                      session. Only the password: the BFF reads the identity from the session, so
                      this cannot sign a different person in behind this page. */}
                  {owner ? (
                    // The owner of the demo copy this browser keeps has no password to type: this
                    // submits the form in the field's place, and the kept password is what is
                    // sent. Here, in the content, and not among the actions below: where the
                    // field would be, it is the dialog's first control, so the dialog opens with
                    // focus on it and Space or Enter, pressed by someone who meant to stay, signs
                    // in again and does not sign out.
                    //
                    // While it waits it keeps its words and shows its spinner beside them, as its
                    // icon. "Sign in again", below, puts its spinner in its words' place and has
                    // to be given a name for the wait; this one is found by the same name
                    // throughout.
                    <Button
                      className={styles.stay}
                      appearance="primary"
                      type="submit"
                      icon={reauthPending ? <Spinner size="tiny" /> : undefined}
                      disabled={reauthPending || ending}
                    >
                      {STAY_SIGNED_IN}
                    </Button>
                  ) : (
                    // No `aria-describedby` here on purpose. `Field` wires the hint to the input
                    // through that very attribute, so setting it would REPLACE the hint's linkage
                    // and silently stop it being announced — trading one message for another. The
                    // error announces itself instead: it is a `role="alert"`, so it is read when it
                    // appears rather than only when the field is next focused.
                    <Field
                      label="Enter your password to continue"
                      hint="This starts a new session."
                    >
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
                  )}
                  {reauthError && (
                    <MessageBar intent="error" role="alert" id={errorId}>
                      <MessageBarBody>{reauthError}</MessageBarBody>
                    </MessageBar>
                  )}
                </div>
              )}
              {signOutError && (
                <MessageBar intent="error" role="alert" className={styles.signOutError}>
                  <MessageBarBody>{signOutError}</MessageBarBody>
                </MessageBar>
              )}
              {/* Last, and outside both alerts: the words of a wait are not part of a failure.
                  Its margin is the hint's own, not a wrapper's: a wrapper stays in the flow while
                  the hint has no words, and its margin grew the dialog when the wait began. */}
              {waiting && <WaitHint active kind="write" className={styles.hint} />}
            </DialogContent>
            <DialogActions>
              {/* No X and no Escape, and that is deliberate — on a security prompt "close" cannot say
                  whether it meant stay or go. The answer to "the only action is the unsafe one" is a
                  second explicit action, not a dismissal. */}
              {/* First, in the page and on screen alike, in the branch where it is offered: the
                  dialog opens with focus on its first control, so Space or Enter, pressed by
                  someone who meant to stay, keeps the session instead of ending it. Kept ONLY here:
                  this is the branch where a keep-alive genuinely moves the deadline it claims to
                  move. At the cap the password field comes first, or, for the owner of a demo
                  copy, the button that stands in its place. */}
              {!absolute && (
                <Button appearance="primary" type="button" onClick={staySignedIn} disabled={ending}>
                  Stay signed in
                </Button>
              )}
              <Button
                ref={signOutLanding.landingRef}
                appearance="secondary"
                type="button"
                onClick={() => {
                  signOutLanding.arm();
                  void endSession(true);
                }}
                disabled={ending || reauthPending}
              >
                Sign out now
              </Button>
              {absolute && !owner && (
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
              )}
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
