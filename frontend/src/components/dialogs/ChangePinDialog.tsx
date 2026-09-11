import { useId, useState } from 'react';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
} from '@fluentui/react-components';
import { colors } from '../../theme/tokens';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { CONNECTION_FAILED } from '../../api/problemMessages';
import { useSetPinMutation } from '../../features/api/apiSlice';
import { RetryCountdown, retryDeadline } from '../feedback';
import { PinInput } from '../PinInput';

const PIN_LENGTH = 6;
const DEFAULT_PIN_LOCK_SECONDS = 15 * 60;

type PinField = 'current' | 'next' | 'confirm';

const useStyles = makeStyles({
  content: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
  },
  group: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  label: {
    fontSize: '14px',
    fontWeight: 600,
    color: colors.neutral[800],
  },
});

export interface ChangePinDialogProps {
  onClose: () => void;
}

/**
 * Change the PIN of a user who already has one — the dialog behind Settings' PIN row.
 *
 * Every layer below this already supported it (`POST /bff/auth/set-pin` with `currentPin`, then
 * `AuthService.SetPinAsync`'s change branch), but the only page that sent it was /pin-setup, which
 * bounces a user whose `hasPin` is true. So the PinChanged notice of ADR-0047 could be triggered
 * with curl and not from the app.
 *
 * One form, three PIN groups, and ONE deliberate submit. No group submits on its sixth digit, as
 * the money dialogs do, because here a completed box is not a confirmation: the current PIN is
 * checked against the SAME lockout as every other PIN gate (IPinVerifier), so an attempt should be
 * spent only when the user says so. The new PIN and its confirmation are compared here, before
 * anything is sent — a mismatch costs no attempt.
 *
 * Mount-on-open (SettingsPage), so every PIN held here dies with the surface. None of them is ever
 * written to a URL, router state or Web Storage.
 */
export function ChangePinDialog({ onClose }: ChangePinDialogProps) {
  const styles = useStyles();
  const errorId = useId();
  const [setPin, { isLoading }] = useSetPinMutation();

  const [currentPin, setCurrentPin] = useState('');
  const [newPin, setNewPin] = useState('');
  const [confirmPin, setConfirmPin] = useState('');
  // Bumped to remount a group, which refocuses its first box (PinInput's autoFocus is mount-only).
  const [nonce, setNonce] = useState({ current: 0, confirm: 0 });
  const [focusField, setFocusField] = useState<PinField>('current');
  const [invalidField, setInvalidField] = useState<PinField | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [lockDeadline, setLockDeadline] = useState<number | null>(null);
  const [changed, setChanged] = useState(false);

  const locked = lockDeadline !== null;
  const ready =
    currentPin.length === PIN_LENGTH &&
    newPin.length === PIN_LENGTH &&
    confirmPin.length === PIN_LENGTH;

  const requestClose = () => {
    if (!isLoading) {
      onClose();
    }
  };

  const edit = (field: PinField, setter: (value: string) => void) => (next: string) => {
    setter(next);
    setError(null);
    if (invalidField === field) {
      setInvalidField(null);
    }
  };

  const restart = (field: 'current' | 'confirm') => {
    setNonce((n) => ({ ...n, [field]: n[field] + 1 }));
    setFocusField(field);
  };

  /**
   * The server's answers to a change, measured through the real BFF on 2026-09-11 against a fresh
   * user whose PIN was set:
   *
   *   wrong currentPin        -> 401 INVALID_PIN "Invalid PIN.", instance "/api/auth/pin"
   *   the third wrong one     -> 429 PIN_LOCKED, retryAfterSeconds 900, NO Retry-After header
   *   correct, but locked     -> 429 PIN_LOCKED (the lock is checked before the comparison)
   *   no currentPin           -> 422 PIN_REQUIRED "The current PIN is required to change it."
   *   '12345' as the new PIN  -> 400, errors {"Pin":["PIN must be exactly 6 digits."]}
   *
   * GET /bff/auth/me answered 200 after the 401: that 401 is about the PIN, not the session, and
   * sessionMiddleware keeps the user signed in because it routes on the INVALID_PIN code.
   */
  const handleRefusal = (problem: ApiProblem) => {
    const code = problem.errorCode;
    if (code === 'INVALID_PIN') {
      // Named, not the server's "Invalid PIN.": three PINs are on screen and only one was checked.
      setCurrentPin('');
      setInvalidField('current');
      restart('current');
      setError('That is not your current PIN.');
    } else if (code === 'PIN_LOCKED') {
      // The deadline comes from the BODY: through the BFF this 429 carries no Retry-After header.
      setCurrentPin('');
      setLockDeadline(retryDeadline(problem.retryAfterSeconds ?? DEFAULT_PIN_LOCK_SECONDS));
    } else if (code === 'VALIDATION_ERROR') {
      // Unreachable from these boxes, which accept six digits and nothing else; kept so a new
      // server rule still reaches the user in the server's words.
      const firstFieldError = Object.values(problem.errors ?? {})[0]?.[0];
      setError(firstFieldError ?? 'That PIN is not allowed. Please choose another.');
    } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
      setError(CONNECTION_FAILED);
    } else {
      // PIN_REQUIRED cannot come from here, because the submit needs six current digits; it and
      // anything else the endpoint can answer render in the server's own words.
      setError(problem.detail || 'Could not change your PIN. Please try again.');
    }
  };

  const submit = async () => {
    if (!ready || isLoading || locked) return;
    if (newPin !== confirmPin) {
      // PinSetupPage's sentence, and the same remedy: only the confirmation is re-entered.
      setConfirmPin('');
      setInvalidField('confirm');
      restart('confirm');
      setError('PINs do not match. Please try again.');
      return;
    }
    setError(null);
    setInvalidField(null);
    try {
      await setPin({ pin: newPin, currentPin }).unwrap();
      setChanged(true);
    } catch (caught) {
      handleRefusal(caught as ApiProblem);
    }
  };

  const describedBy = (field: PinField) =>
    (error && invalidField === field) || (locked && field === 'current') ? errorId : undefined;

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && requestClose()}>
      <DialogSurface>
        {changed ? (
          <DialogBody>
            <DialogTitle>Change your PIN</DialogTitle>
            <DialogContent>
              <MessageBar intent="success">
                <MessageBarBody>Your PIN has been changed.</MessageBarBody>
              </MessageBar>
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={onClose}>
                Done
              </Button>
            </DialogActions>
          </DialogBody>
        ) : (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              void submit();
            }}
          >
            <DialogBody>
              <DialogTitle>Change your PIN</DialogTitle>
              <DialogContent className={styles.content}>
                <div className={styles.group}>
                  <Text className={styles.label}>Current PIN</Text>
                  <PinInput
                    key={`current-${nonce.current}`}
                    value={currentPin}
                    onChange={edit('current', setCurrentPin)}
                    length={PIN_LENGTH}
                    disabled={isLoading || locked}
                    error={invalidField === 'current'}
                    autoFocus={focusField === 'current'}
                    ariaLabel="Current PIN"
                    ariaDescribedBy={describedBy('current')}
                  />
                </div>
                <div className={styles.group}>
                  <Text className={styles.label}>New PIN</Text>
                  <PinInput
                    value={newPin}
                    onChange={edit('next', setNewPin)}
                    length={PIN_LENGTH}
                    disabled={isLoading || locked}
                    ariaLabel="New PIN"
                  />
                </div>
                <div className={styles.group}>
                  <Text className={styles.label}>Confirm new PIN</Text>
                  <PinInput
                    key={`confirm-${nonce.confirm}`}
                    value={confirmPin}
                    onChange={edit('confirm', setConfirmPin)}
                    length={PIN_LENGTH}
                    disabled={isLoading || locked}
                    error={invalidField === 'confirm'}
                    autoFocus={focusField === 'confirm'}
                    ariaLabel="Confirm new PIN"
                    ariaDescribedBy={describedBy('confirm')}
                  />
                </div>

                {/* The banner and the countdown are siblings, as in the money dialogs: role="alert"
                    is atomic, so a timer inside it would re-announce the whole banner every
                    second. */}
                {(error || locked) && (
                  <div id={errorId}>
                    {error && (
                      <MessageBar intent="error" role="alert">
                        <MessageBarBody>{error}</MessageBarBody>
                      </MessageBar>
                    )}
                    {lockDeadline !== null && (
                      <>
                        <MessageBar intent="warning" role="alert">
                          <MessageBarBody>Too many incorrect PIN attempts.</MessageBarBody>
                        </MessageBar>
                        <RetryCountdown
                          deadline={lockDeadline}
                          onElapsed={() => setLockDeadline(null)}
                        />
                      </>
                    )}
                  </div>
                )}
              </DialogContent>
              <DialogActions>
                <Button
                  appearance="secondary"
                  type="button"
                  onClick={requestClose}
                  disabled={isLoading}
                >
                  Cancel
                </Button>
                <Button appearance="primary" type="submit" disabled={!ready || isLoading || locked}>
                  {isLoading ? <Spinner size="tiny" /> : 'Change PIN'}
                </Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}
      </DialogSurface>
    </Dialog>
  );
}
