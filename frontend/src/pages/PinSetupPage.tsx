import { useEffect, useId, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useSelector } from 'react-redux';
import {
  makeStyles,
  Text,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import { LockClosed24Regular, CheckmarkCircle24Filled } from '@fluentui/react-icons';
import { colors, transitions } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import { useSetPinMutation } from '../features/api/apiSlice';
import { selectCurrentUser } from '../features/auth/authSlice';
import { PinInput } from '../components/PinInput';

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  page: {
    minHeight: '100vh',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    padding: '20px',
    backgroundColor: colors.neutral[50],
  },
  card: {
    width: '100%',
    maxWidth: '420px',
    backgroundColor: '#FFFFFF',
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
  title: {
    fontSize: '22px',
    fontWeight: 700,
    color: colors.neutral[800],
    textAlign: 'center',
  },
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
 * the credential the withdraw flow (D1) verifies in-body, so a PIN-less user is routed
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

  const [setPinTrigger, { isLoading }] = useSetPinMutation();

  const [step, setStep] = useState<Step>('enter');
  const [firstPin, setFirstPin] = useState('');
  const [confirmPin, setConfirmPin] = useState('');
  const [error, setError] = useState<string | null>(null);

  // A user who already has a PIN has nothing to do here — bounce them to where they were
  // headed. `settled` suppresses this once WE flip hasPin via a successful set (so the
  // success screen isn't skipped by the refreshed session).
  const settled = useRef(false);
  useEffect(() => {
    if (!settled.current && user?.hasPin) {
      navigate(target, { replace: true });
    }
  }, [user?.hasPin, navigate, target]);

  const handleConfirmComplete = async (confirmed: string) => {
    if (confirmed !== firstPin) {
      setError('PINs do not match. Please try again.');
      setConfirmPin('');
      return;
    }
    setError(null);
    settled.current = true;
    try {
      await setPinTrigger({ pin: confirmed }).unwrap();
      setStep('success');
    } catch (caught) {
      settled.current = false;
      const problem = caught as ApiProblem;
      if (problem.errorCode === 'VALIDATION_ERROR') {
        const firstFieldError = Object.values(problem.errors ?? {})[0]?.[0];
        setError(firstFieldError ?? 'That PIN is not allowed. Please choose another.');
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        setError("Couldn't reach the server — check your connection and try again.");
      } else {
        setError(problem.detail || 'Could not set your PIN. Please try again.');
      }
      // Restart the confirm step so the boxes are usable again.
      setConfirmPin('');
    }
  };

  if (step === 'success') {
    return (
      <div className={styles.page}>
        <div className={styles.card}>
          <div className={styles.successIcon}>
            <CheckmarkCircle24Filled style={{ width: '40px', height: '40px' }} />
          </div>
          <Text className={styles.title}>PIN Setup Complete!</Text>
          <Text className={styles.subtitle}>
            Your 6-digit PIN is set. You&apos;ll use it to confirm withdrawals.
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
      <div className={styles.card}>
        <div className={styles.icon}>
          <LockClosed24Regular style={{ width: '36px', height: '36px' }} />
        </div>
        <Text className={styles.title}>{onEnter ? 'Create your PIN' : 'Confirm your PIN'}</Text>
        <Text className={styles.subtitle}>
          {onEnter
            ? 'Choose a 6-digit PIN. You’ll enter it to confirm withdrawals.'
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
          />
        ) : (
          <PinInput
            key="confirm"
            value={confirmPin}
            onChange={(next) => {
              setConfirmPin(next);
              setError(null);
            }}
            onComplete={handleConfirmComplete}
            error={!!error}
            disabled={isLoading}
            autoFocus
            ariaLabel="Confirm your PIN"
            ariaDescribedBy={error ? errorId : undefined}
          />
        )}

        {error && (
          <div role="alert" id={errorId} className={styles.fullError}>
            <MessageBar intent="error">
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          </div>
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
              disabled={confirmPin.length !== 6 || isLoading}
            >
              {isLoading ? <Spinner size="tiny" /> : 'Set PIN'}
            </Button>
          )}
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
