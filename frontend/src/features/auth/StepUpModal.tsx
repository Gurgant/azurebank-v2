import { useId, useRef, useState, useSyncExternalStore } from 'react';
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
import { useFocusWhenLost } from '../../hooks/useFocusWhenLost';
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
  // A refusal takes the column's width, and its sentence wraps inside it. `intro` centres what it
  // holds, and a centred bar is as wide as its one line: a long sentence runs past both edges.
  refusal: { alignSelf: 'stretch', textAlign: 'start' },
  // The lock's block takes the width too, but only its bar reads from the start edge: the countdown
  // under the bar keeps the centre it had, so at 320 px it is not pushed off the left with the bar.
  lock: { alignSelf: 'stretch' },
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
  // Bumped by each refusal that empties the boxes: PinInput is remounted under the new key, and its
  // autoFocus puts the caret back in box 1. The boxes are disabled during the check, so focus falls
  // to `body`, where digits go nowhere and the dialog does not hear Escape.
  const [pinNonce, setPinNonce] = useState(0);
  const [error, setError] = useState<string | null>(null);
  // Red boxes for a PIN the server said was wrong, and for nothing else: an outage or a lost
  // connection says nothing about the PIN, as on the transfer pages.
  const [pinError, setPinError] = useState(false);

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
  // On a lock the boxes and Verify are disabled and Cancel is the one control left, so focus lost
  // during the check lands there, where Escape is heard. Focus the visitor put somewhere stays.
  const cancelRef = useFocusWhenLost<HTMLButtonElement>(lockDeadline !== null);
  const contentRef = useRef<HTMLDivElement>(null);

  // A refusal that another PIN can answer: its sentence, and the boxes emptied and remounted.
  const askAgain = (words: string) => {
    setError(words);
    setPin('');
    setPinNonce((n) => n + 1);
  };

  const verify = async (candidate: string) => {
    if (candidate.length !== 6 || isLoading || lockDeadline !== null) return;
    setError(null);
    setPinError(false);
    try {
      const result = await verifyPin({ pin: candidate }).unwrap();
      if (result.verified) {
        settleStepUp('elevated'); // the controller closes this modal; the wrapper replays
      } else {
        // Wrong PIN is a 200 verified:false — NOT the error channel. The one answer that marks
        // the boxes as wrong.
        askAgain('Incorrect PIN. Please try again.');
        setPinError(true);
      }
    } catch (caught) {
      const problem = caught as ApiProblem;
      if (problem.errorCode === 'PIN_LOCKED') {
        setLockDeadline(retryDeadline(problem.retryAfterSeconds ?? DEFAULT_PIN_LOCK_SECONDS));
        setPin('');
        // At 320 px the row of boxes is wider than the content, and typing into box 6 scrolled it
        // sideways, so the bar's start edge and its icon sat past the left. After the other
        // refusals box 1 takes focus and the content scrolls back; the lock has no box to focus.
        if (contentRef.current) contentRef.current.scrollLeft = 0;
      } else if (isServiceOutage(problem)) {
        // Before the transport branch: a check with no answer in 65 s is `NETWORK` too, and it is
        // the service that did not answer, not the visitor's connection.
        askAgain(SERVICE_UNAVAILABLE);
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        // A transport blip must NOT silently abandon what the PIN was asked for — keep the modal
        // open so the user can retry the PIN without re-driving the whole flow.
        askAgain("Couldn't verify right now — check your connection and try again.");
      } else if (problem.status === 401) {
        // A real 401 = dead session (sessionMiddleware already logs out); abandon step-up.
        settleStepUp('cancelled');
      } else {
        // Any OTHER unexpected status (e.g. a 500) must be SURFACED, never masked as a
        // cancellation — keep the modal open with a visible error so the failure isn't silent.
        askAgain("Couldn't verify right now — please try again.");
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
      <DialogContent ref={contentRef}>
        <div className={styles.intro}>
          <Text className={styles.subtitle}>
            Enter your 6-digit PIN to show the full account number.
          </Text>
          <div className={styles.pinArea}>
            <PinInput
              key={pinNonce}
              value={pin}
              onChange={(next) => {
                setPin(next);
                setError(null);
                setPinError(false);
              }}
              onComplete={verify}
              disabled={isLoading || lockDeadline !== null}
              error={pinError}
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
            <MessageBar
              intent="error"
              role="alert"
              id={errorId}
              layout="multiline"
              className={styles.refusal}
            >
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
          {lockDeadline !== null && (
            <div id={errorId} className={styles.lock}>
              <MessageBar
                intent="warning"
                role="alert"
                layout="multiline"
                className={styles.refusal}
              >
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
          ref={cancelRef}
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
