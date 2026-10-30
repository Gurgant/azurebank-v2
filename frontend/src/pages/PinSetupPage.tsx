import { useEffect, useId, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useSelector } from 'react-redux';
import {
  makeStyles,
  mergeClasses,
  Text,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
  Field,
  Input,
  tokens,
} from '@fluentui/react-components';
import { LockClosed24Regular, CheckmarkCircle24Filled } from '@fluentui/react-icons';
import { colors, surfaces, transitions } from '../theme/tokens';
import { landingRing } from '../theme/landingRing';
import { useFocusWhenLost } from '../hooks/useFocusWhenLost';
import { isServiceOutage, type ApiProblem } from '../api/problemBaseQuery';
import { useAppDispatch } from '../app/hooks';
import { apiSlice, useSetPinMutation } from '../features/api/apiSlice';
import { selectCurrentUser } from '../features/auth/authSlice';
import { PIN_PURPOSE } from '../features/auth/pinWords';
import { PinInput } from '../components/PinInput';
import { RetryCountdown, WaitHint, retryDeadline } from '../components/feedback';
import { CONNECTION_FAILED, SAVE_OUTCOME_UNKNOWN, WRONG_PASSWORD } from '../api/problemMessages';

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  // Full-screen flow, outside the app shell: it owns its canvas, from the shell's token.
  page: {
    minHeight: '100dvh',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '20px',
    backgroundColor: surfaces.canvas,
  },
  card: {
    width: '100%',
    maxWidth: '420px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: '16px',
    border: `1px solid ${colors.neutral[200]}`,
    padding: '32px 28px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '20px',
  },
  icon: {
    width: '72px',
    height: '72px',
    borderRadius: '50%',
    backgroundColor: colors.brand[130],
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.brand[60],
  },
  successIcon: {
    width: '72px',
    height: '72px',
    borderRadius: '50%',
    backgroundColor: colors.semantic.success.light,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.semantic.success.main,
  },
  // Each view's title is its `h1`. index.css also takes a heading's margin off; the title keeps
  // its own 0 so the boxes stay where they were when it was a `span`, whatever that sheet says.
  title: {
    fontSize: '22px',
    fontWeight: 700,
    color: colors.neutral[800],
    textAlign: 'center',
    margin: 0,
  },
  // The finished view's title takes the focus the save lost, and draws the app's landing ring.
  landing: landingRing,
  subtitle: {
    fontSize: '15px',
    color: colors.neutral[500],
    textAlign: 'center',
    lineHeight: '1.5',
  },
  dots: { display: 'flex', gap: '8px' },
  dot: {
    width: '8px',
    height: '8px',
    borderRadius: '50%',
    backgroundColor: colors.neutral[300],
    transition: `all ${transitions.fast}`,
  },
  dotActive: { backgroundColor: colors.brand[60], width: '24px', borderRadius: '4px' },
  actions: { width: '100%', display: 'flex', flexDirection: 'column', gap: '12px' },
  fullError: { width: '100%' },
});

// ============================================
// HELPERS
// ============================================

type Step = 'enter' | 'confirm' | 'success';

/**
 * What the page says when the save is refused, and which field the refusal is about: the
 * password field for a refusal of the password, the PIN boxes for everything else.
 */
interface SetupError {
  message: string;
  field: 'pin' | 'password';
}

/** What the page says while the login's lock holds the save, on the password field. */
const LOCKED: SetupError = { message: 'Your account is locked.', field: 'password' };

/**
 * What the page says when the server refuses because a PIN already exists, and the session could
 * not be read. One surface says this, so the sentence stays here.
 */
const ALREADY_HAS_PIN = 'Your account already has a PIN. To change it, go to Settings.';

/** A lock the answer said the length of: the instant, on this browser's clock, that it ends. */
interface Lock {
  deadline: number;
}

/**
 * Only same-origin absolute paths are honored — never an open redirect (CWE-601). Rejects
 * protocol-relative `//host` AND the backslash variant `/\host` (and `/\/host`), which the
 * WHATWG URL parser normalizes to `//host` → a cross-origin target.
 */
function safeReturnTo(raw: string | null): string {
  return raw && /^\/(?![/\\])/.test(raw) ? raw : '/dashboard';
}

// ============================================
// COMPONENT
// ============================================

/**
 * PR-10 — the dedicated PIN onboarding wizard (enter → confirm → done). A 6-digit PIN is
 * the credential the withdraw flow verifies in-body, so a PIN-less user is routed
 * here (from the withdraw gate, or the post-register handoff) with a `?returnTo=` so they
 * land back where they came from. Setting the PIN invalidates the session tag, so the
 * `hasPin` the withdraw gate reads refreshes on the way back.
 */
export function PinSetupPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const user = useSelector(selectCurrentUser);
  const target = safeReturnTo(params.get('returnTo'));
  const errorId = useId();

  const dispatch = useAppDispatch();
  const [setPinTrigger, { isLoading }] = useSetPinMutation();
  /*
    A failed read of the session waits before its one retry, and a second initiate while that one
    is still out is not sent. The refusal cannot wait: it is painted at once, and a read that is
    still waiting is stopped so the next one is actually sent. A failed read leaves the session
    as it was.
  */
  const sessionRead = useRef<({ abort: () => void } & PromiseLike<unknown>) | null>(null);
  const readSession = async () => {
    const previous = sessionRead.current;
    if (previous) {
      previous.abort();
      sessionRead.current = null;
      try {
        await previous;
      } catch {
        // The stopped read leaves the session as it was.
      }
    }
    const request = dispatch(
      apiSlice.endpoints.getMe.initiate(undefined, { forceRefetch: true, subscribe: false }),
    );
    sessionRead.current = request;
    try {
      await request;
    } catch {
      // A failed read leaves the session as it was.
    }
  };

  const [step, setStep] = useState<Step>('enter');
  const [firstPin, setFirstPin] = useState('');
  const [confirmPin, setConfirmPin] = useState('');
  /*
    The account password, proving that whoever is choosing this PIN is the account holder and not
    merely the holder of a session cookie (T7/#201). Measured before this existed: a cookie alone
    was enough to pick a PIN, reach authLevel 2 and empty the account.

    It lives on the CONFIRM step rather than in a step of its own — the commitment happens here, so
    the proof belongs here, and adding a fourth screen to a three-screen wizard buys nothing.
  */
  const [password, setPassword] = useState('');
  const [error, setError] = useState<SetupError | null>(null);
  /*
    The lock's countdown, kept apart from `error`: every edit of the boxes or of the password
    clears `error`, and a lock that went with it would give the save back at the first keystroke.
    A refusal of the lock sets a new one, so a lock answered again with the same seconds counts
    from its own answer; only the countdown reaching zero clears it. Until then the page says the
    account is locked and sends nothing.
  */
  const [lock, setLock] = useState<Lock | null>(null);
  const [saveUnknown, setSaveUnknown] = useState(false);
  const shownError = lock ? LOCKED : error;
  const passwordRef = useRef<HTMLInputElement>(null);
  const formCard = useRef<HTMLDivElement>(null);
  const pinOwnsError = shownError?.field === 'pin';
  const passwordOwnsError = shownError?.field === 'password';
  /*
    The boxes and the password are disabled while the save is out, so a refusal mostly arrives with
    focus on `body`; the PIN's Show button stays enabled and may still hold it. A refusal moves
    focus to the field it is about, the password or the first PIN box, once that field can take
    it. A box the save has disabled cannot, so the move waits until the save has ended. The first
    box is the first input of the form's card: the finished view is a different card.
  */
  useEffect(() => {
    if (!isLoading && passwordOwnsError) passwordRef.current?.focus();
  }, [isLoading, passwordOwnsError]);
  useEffect(() => {
    if (!isLoading && pinOwnsError) {
      formCard.current?.querySelector<HTMLInputElement>('input')?.focus();
    }
  }, [isLoading, pinOwnsError]);
  /*
    The finished view mounts with its words in it, which a screen reader does not read, and the
    control that sent is gone with the confirm step: its title takes that lost focus, as the
    money receipts' titles do. Called here, above the early return of the finished view.
  */
  const finished = step === 'success' || (saveUnknown && user?.hasPin === true);
  const successTitle = useFocusWhenLost<HTMLHeadingElement>(finished);

  // A visitor who arrived already holding a PIN is sent on. `settled` holds that off after a
  // save this page completed, so the finished view survives the session refresh. `saveUnknown`
  // holds it off after a save whose answer was lost: that session is the finished view.
  const settled = useRef(false);
  useEffect(() => {
    if (!settled.current && !saveUnknown && user?.hasPin) {
      navigate(target, { replace: true });
    }
  }, [saveUnknown, user?.hasPin, navigate, target]);

  const handleConfirmComplete = async (confirmed: string) => {
    // The lock is held here as well as at the two controls that call this, so that a way of
    // sending added later is held without having to know about the lock.
    if (lock) return;
    if (confirmed !== firstPin) {
      setError({ message: 'PINs do not match. Please try again.', field: 'pin' });
      setConfirmPin('');
      return;
    }
    setError(null);
    settled.current = true;
    try {
      await setPinTrigger({ pin: confirmed, password }).unwrap();
      setStep('success');
    } catch (caught) {
      settled.current = false;
      const problem = caught as ApiProblem;
      const pinError = (message: string): SetupError => ({ message, field: 'pin' });
      const passwordError = (message: string): SetupError => ({ message, field: 'password' });
      let refusal: SetupError;
      if (problem.errorCode === 'VALIDATION_ERROR') {
        const firstFieldError = Object.values(problem.errors ?? {})[0]?.[0];
        refusal = pinError(firstFieldError ?? 'That PIN is not allowed. Please choose another.');
      } else if (problem.errorCode === 'PASSWORD_REQUIRED') {
        refusal = passwordError('Enter your account password to set a PIN.');
      } else if (problem.errorCode === 'INVALID_CREDENTIALS') {
        // Named, not generic: the PIN boxes are fine and re-entering them would be busywork.
        refusal = passwordError(WRONG_PASSWORD);
      } else if (problem.errorCode === 'ACCOUNT_LOCKED') {
        // Wrong passwords here count toward the SAME login lockout — say so, rather than leaving
        // the user to discover it at the login screen. The wait is counted down from the answer's
        // seconds, never from its `lockedUntil` (a skewed clock); an answer that names no wait, or
        // none left, is said without a countdown and holds nothing.
        const seconds = problem.retryAfterSeconds;
        if (seconds !== undefined && seconds > 0) {
          setLock({ deadline: retryDeadline(seconds) });
          refusal = LOCKED;
        } else {
          refusal = passwordError(
            'Too many incorrect passwords. Your account is locked; try again later.',
          );
        }
      } else if (problem.errorCode === 'PIN_REQUIRED') {
        // The page sends no current PIN, so this refusal is the session's to settle. The server's
        // sentence names a box this page does not have, and is not shown.
        void readSession();
        refusal = pinError(ALREADY_HAS_PIN);
      } else if (isServiceOutage(problem)) {
        // Before the transport branch: a request with no answer in 65 s is `status: 'NETWORK'`
        // too, and the connection was not the problem. The PIN may have been saved, and the
        // request carries no key, so this cannot say it failed.
        setSaveUnknown(true);
        void readSession();
        refusal = pinError(SAVE_OUTCOME_UNKNOWN);
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        refusal = pinError(CONNECTION_FAILED);
      } else {
        refusal = pinError(problem.detail || 'Could not set your PIN. Please try again.');
      }
      setError(refusal);
      /*
        A refusal of the password keeps the confirmed PIN: it was not the problem, and the save is
        sent again only by the visitor, from "Set PIN" or an edit of the boxes. Every other refusal
        empties the boxes, so that the PIN is typed again. The password is KEPT either way:
        retyping a correct password because the PIN needed re-entering is the kind of friction
        that teaches people to choose shorter passwords.
      */
      if (refusal.field === 'pin') setConfirmPin('');
    }
  };

  if (finished) {
    return (
      <div className={styles.page}>
        <div className={styles.card}>
          <div className={styles.successIcon}>
            <CheckmarkCircle24Filled style={{ width: '40px', height: '40px' }} />
          </div>
          <Text
            as="h1"
            className={mergeClasses(styles.title, styles.landing)}
            ref={successTitle}
            tabIndex={-1}
          >
            PIN Setup Complete!
          </Text>
          <Text className={styles.subtitle}>
            {`Your 6-digit PIN is set. You'll use it to ${PIN_PURPOSE}.`}
          </Text>
          <div className={styles.actions}>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => navigate(target, { replace: true })}
            >
              Continue
            </Button>
          </div>
        </div>
      </div>
    );
  }

  const onEnter = step === 'enter';

  return (
    <div className={styles.page}>
      <div className={styles.card} ref={formCard}>
        <div className={styles.icon}>
          <LockClosed24Regular style={{ width: '36px', height: '36px' }} />
        </div>
        <Text as="h1" className={styles.title}>
          {onEnter ? 'Create your PIN' : 'Confirm your PIN'}
        </Text>
        <Text className={styles.subtitle}>
          {onEnter
            ? `Choose a 6-digit PIN. You’ll enter it to ${PIN_PURPOSE}.`
            : 'Re-enter your PIN to make sure it matches.'}
        </Text>

        <div className={styles.dots}>
          <span className={`${styles.dot} ${onEnter ? styles.dotActive : ''}`} />
          <span className={`${styles.dot} ${!onEnter ? styles.dotActive : ''}`} />
        </div>

        {onEnter ? (
          <PinInput
            key="enter"
            value={firstPin}
            onChange={(next) => {
              setFirstPin(next);
              setError(null);
            }}
            autoFocus
            ariaLabel="Create your PIN"
            purpose="new"
          />
        ) : (
          <PinInput
            key="confirm"
            value={confirmPin}
            onChange={(next) => {
              setConfirmPin(next);
              setError(null);
            }}
            /*
              Guarded: PinInput fires this the instant the sixth digit lands, which on this step
              would submit before the password has been typed and spend a refusal to say so. The
              button below is the deliberate commit; auto-submit only survives for the case where
              the password is already in hand. A lock holds it as it holds the button.
            */
            onComplete={(value) => {
              if (password.length > 0 && !lock) void handleConfirmComplete(value);
            }}
            error={pinOwnsError}
            disabled={isLoading}
            autoFocus
            ariaLabel="Confirm your PIN"
            ariaDescribedBy={pinOwnsError ? errorId : undefined}
            purpose="new"
          />
        )}

        {!onEnter && (
          <Field
            label="Account password"
            hint="Proves it is you setting this PIN, not just someone using your open session."
            style={{ width: '100%' }}
            validationState={passwordOwnsError ? 'error' : undefined}
          >
            {/*
              Field puts its hint's id before this one, and sets `aria-invalid` from
              `validationState`.
            */}
            <Input
              ref={passwordRef}
              aria-describedby={passwordOwnsError ? errorId : undefined}
              type="password"
              value={password}
              onChange={(_, data) => {
                setPassword(data.value);
                setError(null);
              }}
              disabled={isLoading}
              autoComplete="current-password"
              placeholder="Your account password"
            />
          </Field>
        )}

        {shownError && (
          <div role="alert" id={errorId} className={styles.fullError}>
            <MessageBar intent="error">
              <MessageBarBody>{shownError.message}</MessageBarBody>
            </MessageBar>
          </div>
        )}
        {/* A sibling of the alert, not inside it: an alert is read whole, and the timer would have
            it read again every second. It carries its own polite region. */}
        {lock && (
          <RetryCountdown
            deadline={lock.deadline}
            onElapsed={() => {
              setLock(null);
              setError(null);
            }}
          />
        )}

        <div className={styles.actions}>
          {onEnter ? (
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => setStep('confirm')}
              disabled={firstPin.length !== 6}
            >
              Continue
            </Button>
          ) : (
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => void handleConfirmComplete(confirmPin)}
              disabled={confirmPin.length !== 6 || password.length === 0 || isLoading || !!lock}
            >
              {isLoading ? <Spinner size="tiny" /> : 'Set PIN'}
            </Button>
          )}
          {/* Under the button, outside the alert above and the boxes' described-by target. */}
          <WaitHint active={isLoading} kind="write" />
          <Button
            appearance="subtle"
            size="large"
            style={{ width: '100%', height: '44px' }}
            onClick={() => {
              if (onEnter) {
                navigate(target, { replace: true });
              } else {
                setStep('enter');
                setConfirmPin('');
                setError(null);
              }
            }}
            disabled={isLoading}
          >
            {onEnter ? 'Skip for now' : 'Back'}
          </Button>
        </div>
      </div>
    </div>
  );
}

export default PinSetupPage;
