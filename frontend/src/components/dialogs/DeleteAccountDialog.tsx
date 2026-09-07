import { useId, useState } from 'react';
import { useDispatch } from 'react-redux';
import { useNavigate } from 'react-router-dom';
import {
  Button,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
} from '@fluentui/react-components';
import { Delete24Regular } from '@fluentui/react-icons';
import { colors } from '../../theme/tokens';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { CONNECTION_FAILED } from '../../api/problemMessages';
import {
  apiSlice,
  useAuthoriseAccountDeletionMutation,
  useDeleteAccountMutation,
} from '../../features/api/apiSlice';
import { RetryCountdown, retryDeadline } from '../feedback';
import { PinInput } from '../PinInput';
import { MoneyDialogShell } from './MoneyDialogShell';
import { useMoneyDialogStyles } from './moneyDialogStyles';
import { DELETE_RULES } from './deleteRules';

// ============================================
// CONSTANTS
// ============================================

const PIN_LENGTH = 6;
const DEFAULT_PIN_LOCK_SECONDS = 15 * 60;

type Step = 'confirm' | 'pin';

// ============================================
// STYLES
// ============================================

/**
 * The PIN step's own keys, copied from WithdrawDialog rather than shared: `moneyDialogStyles.ts`
 * keeps them private to the dialog that owns a PIN step by design, and moving them into the
 * shared module would make it a place where things are put rather than where shared things live.
 */
const usePinStyles = makeStyles({
  pinStep: {
    flex: 1,
    padding: '24px 20px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '20px',
    overflowY: 'auto',
  },
  pinInstruction: {
    fontSize: '15px',
    color: colors.neutral[500],
    textAlign: 'center',
    lineHeight: '1.5',
  },
});

// ============================================
// COMPONENT
// ============================================

export interface DeleteAccountDialogProps {
  account: { id: string; name: string };
  onClose: () => void;
}

/**
 * Closing an account costs a PIN, on the transfer's authorisation rail (ADR-0049).
 *
 * Two steps: CONFIRM (the sentence the old ConfirmDialog showed, verbatim) then PIN. The sixth
 * digit mints a deletion authorisation (`POST /api/accounts/{id}/deletion-authorizations`, PIN in
 * the JSON body and nowhere else) and, with the minted id in `Step-Up-Authorization`, sends the
 * DELETE. The authorisation id is a local in `submit`: it goes out of scope on any exit, so a
 * refusal never leaves a held id behind and the next completion simply mints again — a DELETE
 * carries no Idempotency-Key (ADR-0049), so there is no intent to retain across attempts, and
 * that is why there is no useMoneyWizard / useIdempotentMutation here.
 *
 * The PIN lives in component state and in `onComplete`'s argument only — never a URL, router
 * state or Web Storage — and the dialog is mount-on-open (AccountsPage), so the state dies with
 * the surface. `/pin-setup?returnTo=/accounts` carries a PATH.
 *
 * The shell is MoneyDialogShell (role 'dialog', accessible name = title). Tone 'debit' with no
 * destructive styling and no success screen: the dialog closes and the list refetches through
 * the mutation's own tags. Visual/semantic polish is U8's (#165/#166), not this dialog's.
 */
export function DeleteAccountDialog({ account, onClose }: DeleteAccountDialogProps) {
  const styles = useMoneyDialogStyles();
  const pinStyles = usePinStyles();
  const navigate = useNavigate();
  const dispatch = useDispatch();
  const errorId = useId();

  const [authoriseAccountDeletion, { isLoading: isMinting }] =
    useAuthoriseAccountDeletionMutation();
  const [deleteAccount, { isLoading: isDeleting }] = useDeleteAccountMutation();

  const [step, setStep] = useState<Step>('confirm');
  const [pin, setPin] = useState('');
  const [pinNonce, setPinNonce] = useState(0); // bumped to remount PinInput (refocus box 1)
  const [pinError, setPinError] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // An ABSOLUTE deadline, as WithdrawDialog: RetryCountdown ticks and calls back at zero.
  const [lockDeadline, setLockDeadline] = useState<number | null>(null);

  // Both phases of the submit bar every exit — the mint round trip as much as the DELETE.
  const busy = isMinting || isDeleting;

  const requestClose = () => {
    if (!busy) {
      onClose();
    }
  };

  const handlePinChange = (next: string) => {
    setPin(next);
    setPinError(false);
  };

  /**
   * What the PIN step does about a refusal. One ladder serves both the mint and the DELETE: they
   * answer with the same codes at the same statuses (ADR-0049, the error-codes table), so the
   * two awaits in `submit` share one catch. Every sentence rendered here is either the server's
   * own `detail` (measured, user-facing) or a sentence the page already showed — no new copy.
   */
  const handleRefusal = (problem: ApiProblem) => {
    const code = problem.errorCode;
    if (code === 'INVALID_PIN') {
      // Measured M1: 401 INVALID_PIN "Invalid PIN." — clear the boxes, remount (refocus box 1),
      // red border. Safe: sessionMiddleware exempts the code from the global sign-out.
      setPin('');
      setPinError(true);
      setPinNonce((n) => n + 1);
      setError(problem.detail ?? 'Invalid PIN.');
    } else if (code === 'AUTHORIZATION_EXPIRED' || code === 'AUTHORIZATION_INVALID') {
      /*
        The DELETE refused the authorisation it was handed. Clear the boxes and stay: the next
        completion mints again (the id was a local — nothing to drop). NO setPinError — an expiry
        is not a wrong PIN, costs no attempt server-side (E2: PinAccessFailedCount 0/0), and a red
        border would tell the user the lock is closer when it is not. The detail is rendered
        verbatim because the measured sentences are already user-facing (E2: "This authorisation
        has expired. Enter your PIN again to confirm."; D5/D6: "This authorisation cannot be
        used."); classifyMoneyProblem's sentences say "transfer" and are NOT reused here.
      */
      setPin('');
      setPinNonce((n) => n + 1);
      setError(problem.detail || 'Could not delete the account.');
    } else if (code === 'PIN_LOCKED') {
      // 429 with retryAfterSeconds — computed client-side into a deadline (CONVENTIONS.md). Not
      // provoked on the deletion mint in the after-run; the shape is checkPinInBand's, measured
      // on the transfer mints 2026-08-16.
      setPin('');
      setLockDeadline(retryDeadline(problem.retryAfterSeconds ?? DEFAULT_PIN_LOCK_SECONDS));
    } else if (code === 'PIN_REQUIRED') {
      // Measured M0: 422 PIN_REQUIRED before anything is spent. Nothing here can fix it, so send
      // them where it can be; the query string carries a PATH, never the PIN.
      navigate('/pin-setup?returnTo=/accounts');
    } else if (code === 'NON_ZERO_BALANCE' || code === 'PRIMARY_ACCOUNT_DELETE') {
      // D17: the business rules render INLINE and the dialog stays open. The mint refuses these
      // BEFORE the PIN is consulted (M2/M3: counter unchanged); the DELETE refuses them before the
      // authorisation is examined (D7/D8, D12: nothing spent). No red — the PIN was not wrong.
      setPin('');
      setPinNonce((n) => n + 1);
      setError(DELETE_RULES[code]);
    } else if (code === 'ACCOUNT_NOT_FOUND') {
      /*
        Already closed (M4 on the mint; D10/D11 on the DELETE: 404 once the account is gone,
        spent header or none alike). Treat it as done — but the mutation invalidates NOTHING on
        error (apiSlice.ts: success-only), so the list is refreshed by hand, the way RESULT_UNKNOWN
        recovery does, or the card would stay on screen.
      */
      dispatch(apiSlice.util.invalidateTags([{ type: 'Account', id: 'LIST' }]));
      onClose();
    } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
      // A transport failure AFTER the mint leaves one Pending row server-side, and the next
      // completion mints again — harmless: D12/D16 show a Pending row that is never spent, or is
      // spent later, costs nothing.
      setPin('');
      setPinNonce((n) => n + 1);
      setError(CONNECTION_FAILED);
    } else {
      // Anything else the rail can answer (5xx, 403 ACCESS_DENIED per D14/D15, the BFF limiter's
      // 429). AUTHORIZATION_REQUIRED and the model-state 400 are unreachable from here (the dialog
      // always sends the header it just minted) and fall through deliberately. Clear the boxes and
      // remount as every other branch does: a full, enabled PinInput would otherwise re-submit a
      // hybrid PIN on the first overwrite keystroke (PinInput.handleChange, index < value.length).
      setPin('');
      setPinNonce((n) => n + 1);
      setError(problem.detail || 'Could not delete the account.');
    }
  };

  /**
   * The sixth digit. READ THE PIN FROM THE ARGUMENT, never from `pin` state: `onComplete` fires
   * inside `onChange` (PinInput), so the state is one render stale at this moment — invisible in
   * a hand test, a flake in CI (TransferPage's `enteredPin` ref and StepUpModal's `candidate`
   * exist for the same reason).
   */
  const submit = async (entered: string) => {
    if (busy || lockDeadline !== null) return;
    setError(null);
    try {
      const minted = await authoriseAccountDeletion({ id: account.id, pin: entered }).unwrap();
      await deleteAccount({
        id: account.id,
        stepUpAuthorizationId: minted.authorizationId,
      }).unwrap();
      // The mutation's own invalidatesTags refreshes the list; nothing to show here.
      onClose();
    } catch (caught) {
      handleRefusal(caught as ApiProblem);
    }
  };

  return (
    <MoneyDialogShell
      open
      title="Delete account?"
      icon={<Delete24Regular />}
      tone="debit"
      onClose={requestClose}
      closeDisabled={busy}
    >
      {step === 'confirm' && (
        <div className={styles.centeredView}>
          <Text className={styles.stateBody}>
            You&apos;re about to delete &quot;{account.name}&quot;. This can&apos;t be undone.
          </Text>
        </div>
      )}

      {step === 'pin' && (
        <div className={pinStyles.pinStep}>
          <Text className={pinStyles.pinInstruction}>Enter your 6-digit PIN to confirm.</Text>
          <PinInput
            key={pinNonce}
            value={pin}
            onChange={handlePinChange}
            onComplete={(entered) => void submit(entered)}
            length={PIN_LENGTH}
            disabled={busy || lockDeadline !== null}
            error={pinError}
            autoFocus
            ariaLabel="Enter your PIN"
            ariaDescribedBy={error || lockDeadline !== null ? errorId : undefined}
          />
          {/* No button on this step — the sixth digit is the submit — so the pending state has
              nowhere else to live (TransferPage does the same). */}
          {busy && <Spinner size="tiny" label="Deleting account" />}
        </div>
      )}

      {/* Footer */}
      <div className={styles.footer}>
        {/* An `aria-describedby` target, NOT a live region: each banner announces itself, and the
            countdown is a SIBLING of the alert (role="alert" implies aria-atomic, so a nested timer
            would re-announce the whole banner every second). Same shape as WithdrawDialog. */}
        {(error || lockDeadline !== null) && (
          <div id={errorId}>
            {error && (
              <MessageBar intent="error" role="alert" className={styles.errorMessage}>
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            {lockDeadline !== null && (
              <>
                <MessageBar intent="warning" role="alert" className={styles.errorMessage}>
                  <MessageBarBody>Too many incorrect PIN attempts.</MessageBarBody>
                </MessageBar>
                <RetryCountdown deadline={lockDeadline} onElapsed={() => setLockDeadline(null)} />
              </>
            )}
          </div>
        )}

        {step === 'confirm' ? (
          <>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => setStep('pin')}
            >
              Delete
            </Button>
            <Button
              appearance="secondary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={onClose}
            >
              Cancel
            </Button>
          </>
        ) : (
          <Button
            appearance="secondary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={onClose}
            disabled={busy}
          >
            Cancel
          </Button>
        )}
      </div>
    </MoneyDialogShell>
  );
}

export default DeleteAccountDialog;
