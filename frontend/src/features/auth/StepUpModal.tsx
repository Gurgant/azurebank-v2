import { useId, useState, useSyncExternalStore } from 'react';
import {
  Dialog,
  DialogSurface,
  DialogBody,
  DialogTitle,
  DialogContent,
  DialogActions,
  Button,
  Spinner,
  Text,
  MessageBar,
  MessageBarBody,
  makeStyles,
} from '@fluentui/react-components';
import { ShieldKeyhole24Regular } from '@fluentui/react-icons';
import { colors } from '../../theme/tokens';
import { isServiceOutage, type ApiProblem } from '../../api/problemBaseQuery';
import { SERVICE_UNAVAILABLE } from '../../api/problemMessages';
import { useVerifyPinMutation } from '../api/apiSlice';
import { RetryCountdown, WaitHint, retryDeadline } from '../../components/feedback';
import { PinInput } from '../../components/PinInput';
import { useReturnFocus } from '../../hooks/useReturnFocus';
import { getStepUpSnapshot, settleStepUp, subscribeStepUp } from './stepUpController';

const DEFAULT_PIN_LOCK_SECONDS = 15 * 60;

const useStyles = makeStyles({
  surface: { maxWidth: '400px' },
  intro: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    textAlign: 'center',
  },
  icon: {
    width: '56px',
    height: '56px',
    borderRadius: '50%',
    backgroundColor: colors.brand[130],
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.brand[60],
  },
  // Centred here as well as on `intro`: Fluent's `Text` sets `text-align: start` on itself, so a
  // sentence that wraps (this one does at 375 px) would sit left-aligned under a centred title.
  subtitle: {
    fontSize: '14px',
    color: colors.neutral[500],
    lineHeight: '1.5',
    textAlign: 'center',
  },
  pinArea: { display: 'flex', justifyContent: 'center', padding: '12px 0' },
});

/**
 * The single, root-mounted step-up (PIN elevation) modal (ADR-0022). It is driven
 * entirely by the module-level stepUpController: when a level-2-gated request 403s, the
 * base-query wrapper calls requestStepUp() and this modal appears. Since ADR-0041 the only
 * route that 403s this way is the account-number reveal — a transfer's PIN goes to the
 * authorisation mint instead (ADR-0042) and never through here. The subtitle below says what
 * that one route asks the PIN for, so a second route that came to open this modal would need
 * words of its own. The user's PIN elevates the SESSION via /bff/auth/verify-pin; on success it
 * settles 'elevated' and the wrapper replays the original request. Because a
 * wrong PIN is HTTP 200 { verified:false } (never a 4xx), success is read from data.verified,
 * not the error channel. Closing the surface unmounts the form, so state resets per open.
 */
export function StepUpModal() {
  const styles = useStyles();
  const snapshot = useSyncExternalStore(subscribeStepUp, getStepUpSnapshot);
  const open = snapshot !== null;
  return (
    <Dialog
      open={open}
      modalType="alert"
      onOpenChange={(_, data) => {
        if (!data.open) settleStepUp('cancelled');
      }}
    >
      <DialogSurface className={styles.surface}>
        {/* Mount the form only while open, so its PIN/error/lock state resets per open. */}
        {open && <StepUpForm />}
      </DialogSurface>
    </Dialog>
  );
}

function StepUpForm() {
  const styles = useStyles();
  const errorId = useId();
  // Here and not in `StepUpModal`, which stays mounted between two asks: this form is what comes
  // with an open and goes with a close.
  useReturnFocus();
  const [verifyPin, { isLoading }] = useVerifyPinMutation();
  const [pin, setPin] = useState('');
  const [error, setError] = useState<string | null>(null);

  /*
    An ABSOLUTE deadline (D13), not a countdown this component has to drive itself.

    It used to be a number of seconds, stored once and never touched again — so the PIN box and
    Verify stayed disabled and the message kept saying "about 15 minutes" for as long as the modal
    was open, long after the server's window had closed. Closing and reopening escaped it, at the
    cost of abandoning whatever the modal was gating.

    `RetryCountdown` owns the ticking and calls back at zero; storing the deadline rather than a
    duration is what makes a SECOND lock with the identical retryAfterSeconds mint a fresh one
    instead of reviving an already-elapsed number.
  */
  const [lockDeadline, setLockDeadline] = useState<number | null>(null);

  const verify = async (candidate: string) => {
    if (candidate.length !== 6 || isLoading || lockDeadline !== null) return;
    setError(null);
    try {
      const result = await verifyPin({ pin: candidate }).unwrap();
      if (result.verified) {
        settleStepUp('elevated'); // the controller closes this modal; the wrapper replays
      } else {
        // Wrong PIN is a 200 verified:false — NOT the error channel.
        setError('Incorrect PIN. Please try again.');
        setPin('');
      }
    } catch (caught) {
      const problem = caught as ApiProblem;
      if (problem.errorCode === 'PIN_LOCKED') {
        setLockDeadline(retryDeadline(problem.retryAfterSeconds ?? DEFAULT_PIN_LOCK_SECONDS));
        setPin('');
      } else if (isServiceOutage(problem)) {
        // Before the transport branch: a check with no answer in 65 s is `NETWORK` too, and it is
        // the service that did not answer, not the visitor's connection.
        setError(SERVICE_UNAVAILABLE);
        setPin('');
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        // A transport blip must NOT silently abandon what the PIN was asked for — keep the modal
        // open so the user can retry the PIN without re-driving the whole flow.
        setError("Couldn't verify right now — check your connection and try again.");
        setPin('');
      } else if (problem.status === 401) {
        // A real 401 = dead session (sessionMiddleware already logs out); abandon step-up.
        settleStepUp('cancelled');
      } else {
        // Any OTHER unexpected status (e.g. a 500) must be SURFACED, never masked as a
        // cancellation — keep the modal open with a visible error so the failure isn't silent.
        setError("Couldn't verify right now — please try again.");
        setPin('');
      }
    }
  };

  const describedBy = error || lockDeadline !== null ? errorId : undefined;

  return (
    <DialogBody>
      <DialogTitle>
        <div className={styles.intro}>
          <div className={styles.icon}>
            <ShieldKeyhole24Regular />
          </div>
          Verify it&apos;s you
        </div>
      </DialogTitle>
      <DialogContent>
        <div className={styles.intro}>
          <Text className={styles.subtitle}>
            Enter your 6-digit PIN to show the full account number.
          </Text>
          <div className={styles.pinArea}>
            <PinInput
              value={pin}
              onChange={(next) => {
                setPin(next);
                setError(null);
              }}
              onComplete={verify}
              disabled={isLoading || lockDeadline !== null}
              error={!!error}
              autoFocus
              ariaLabel="Enter your PIN"
              ariaDescribedBy={describedBy}
            />
          </div>
          {/* The live-region role goes ON the MessageBar, never on a wrapper around it. A wrapper
              works, but it puts the announcement on an element the component does not own, so the
              banner's semantics depend on markup a refactor can quietly drop. This was the last
              surface still carrying the wrapper form after the other five were converted. */}
          {error && (
            <MessageBar intent="error" role="alert" id={errorId}>
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
          {lockDeadline !== null && (
            <div id={errorId}>
              <MessageBar intent="warning" role="alert">
                <MessageBarBody>Too many incorrect PIN attempts.</MessageBarBody>
              </MessageBar>
              {/* Sibling, not child: role="alert" implies aria-atomic, so a nested timer would
                  re-announce the whole banner every second. It carries its own polite region. */}
              <RetryCountdown deadline={lockDeadline} onElapsed={() => setLockDeadline(null)} />
            </div>
          )}
          {/* Above the actions, outside the alert and the PIN's described-by target. */}
          <WaitHint active={isLoading} kind="write" />
        </div>
      </DialogContent>
      <DialogActions>
        <Button
          appearance="secondary"
          onClick={() => settleStepUp('cancelled')}
          disabled={isLoading}
        >
          Cancel
        </Button>
        <Button
          appearance="primary"
          onClick={() => void verify(pin)}
          disabled={pin.length !== 6 || isLoading || lockDeadline !== null}
        >
          {isLoading ? <Spinner size="tiny" /> : 'Verify'}
        </Button>
      </DialogActions>
    </DialogBody>
  );
}

export default StepUpModal;
