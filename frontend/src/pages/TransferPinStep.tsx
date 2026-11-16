import type { ReactNode } from 'react';
import { Button, MessageBar, MessageBarBody, Spinner, Text } from '@fluentui/react-components';
import { PinInput } from '../components/PinInput';
import { RetryCountdown, WaitHint } from '../components/feedback';
import { formatCurrency } from '../utils/format';
import { useTransferWizardStyles } from './transferWizardStyles';

/**
 * The PIN step of both transfer pages: the summary, the six boxes, the lock, the wait and Back.
 * The two pages drew it twice, line for line but for two words; each now hands in what is its own.
 *
 * It holds no state and no effect. Every value the pages' hooks own comes in as a prop under the
 * name the page uses for it, and each page's own ids (`pinHintId`, `pinErrorId`, `pinLockId`) stay
 * where they are made, so the boxes are described by the same elements as before.
 */
export interface TransferPinStepProps {
  /** The summary's first label and the spinner's verb. */
  verb: 'Sending' | 'Moving';
  amountNumber: number;
  /** The value of the summary's "To" row, as the page draws it. */
  to: ReactNode;
  pinLength: number;
  pin: string;
  /** The boxes' `key`: a new value remounts them, with the caret in box 1. */
  pinNonce: number;
  pinError: boolean;
  pinLockDeadline: number | null;
  /** The wizard's banner: while it is set, its id describes the boxes. */
  error: string | null;
  pinHintId: string;
  pinErrorId: string;
  pinLockId: string;
  isMinting: boolean;
  isSubmitting: boolean;
  keyLive: boolean;
  /** The page's one name for the exit rule, which its header reads too. */
  exitLocked: boolean;
  confirming: boolean;
  onPinChange: (next: string) => void;
  onPinComplete: (entered: string) => void;
  onLockElapsed: () => void;
  onBack: () => void;
}

export function TransferPinStep({
  verb,
  amountNumber,
  to,
  pinLength,
  pin,
  pinNonce,
  pinError,
  pinLockDeadline,
  error,
  pinHintId,
  pinErrorId,
  pinLockId,
  isMinting,
  isSubmitting,
  keyLive,
  exitLocked,
  confirming,
  onPinChange,
  onPinComplete,
  onLockElapsed,
  onBack,
}: TransferPinStepProps) {
  const styles = useTransferWizardStyles();

  return (
    <>
      {/*
        The PIN goes to the mint, and the transfer carries the authorisation it returns in the
        `Step-Up-Authorization` header (ADR-0042).
      */}
      <div className={styles.reviewCard}>
        <div className={styles.reviewRow}>
          <Text className={styles.reviewLabel}>{verb}</Text>
          <Text className={styles.reviewValue}>{formatCurrency(amountNumber)}</Text>
        </div>
        <div className={styles.reviewRow}>
          <Text className={styles.reviewLabel}>To</Text>
          <Text className={styles.reviewValue}>{to}</Text>
        </div>
      </div>
      {/* Says what the missing button used to: with no Send control, the behaviour has to be
          discoverable BEFORE the last digit, not discovered by it. */}
      <Text id={pinHintId} style={{ textAlign: 'center' }}>
        Enter your 6-digit PIN. The transfer sends as soon as the last digit is in.
      </Text>
      <PinInput
        key={pinNonce}
        length={pinLength}
        value={pin}
        onChange={onPinChange}
        /*
          The sixth digit sends (ADR-0042). Not a new pattern: `PinInput` documents
          `onComplete` as "an Enter-less submit affordance" and `StepUpModal` has wired it
          since PR-10 — this adopts what already shipped rather than inventing a second way
          to confirm. Review has already discharged WCAG 2.2 SC 3.3.4 (Level AA: "reviewing,
          confirming, and correcting"), so a second confirm buys nothing but a click.
        */
        onComplete={onPinComplete}
        // `isMinting` as well as `isSubmitting`: the submit has TWO phases now, and
        // `isSubmitting` only covers the second. Without this the boxes stay live for the
        // whole mint round trip — a window in which a second completion starts a second
        // mint AND a second send.
        disabled={isMinting || isSubmitting || pinLockDeadline !== null}
        error={pinError}
        // Named for what it asks and described by the instruction and by whatever refused
        // the last attempt, as WithdrawDialog's boxes are; autoFocus is what lets the
        // `pinNonce` remount put the caret back in box 1 for a retry.
        autoFocus
        ariaLabel="Enter your PIN"
        ariaDescribedBy={[
          pinHintId,
          error ? pinErrorId : null,
          pinLockDeadline !== null ? pinLockId : null,
        ]
          .filter(Boolean)
          .join(' ')}
      />
      {pinLockDeadline !== null && (
        <>
          <MessageBar id={pinLockId} intent="error" role="alert">
            <MessageBarBody>Too many incorrect PIN attempts.</MessageBarBody>
          </MessageBar>
          {/* Sibling, not child: role="alert" implies aria-atomic, so a nested timer would
              re-announce the whole banner every second. It carries its own polite region. */}
          <RetryCountdown deadline={pinLockDeadline} onElapsed={onLockElapsed} />
        </>
      )}
      {/* No Send button: the sixth digit is the send. A spinner still has to exist, or the
          one thing the user cannot see is the request they just started. */}
      {isSubmitting && (
        <div style={{ display: 'flex', justifyContent: 'center' }}>
          <Spinner size="tiny" label={`${verb} ${formatCurrency(amountNumber)}`} />
        </div>
      )}
      {/* Under the boxes and the spinner, and outside the boxes' described-by targets. It
          promises nothing: during the wait no control here can re-send the same key. From
          20 s it asks the visitor to keep the page open, since a reload would send the
          transfer again with a new key — once there is a key: while the PIN alone is being
          checked none exists yet, nothing can be sent twice, and it says only that it is
          still trying. The kind changes the words, never the wait's clock. */}
      <WaitHint active={confirming} kind={isMinting && !keyLive ? 'write' : 'moneySend'} />
      <div className={styles.actions}>
        <Button
          appearance="secondary"
          size="large"
          style={{ width: '100%', height: '48px' }}
          onClick={onBack}
          disabled={exitLocked}
        >
          Back
        </Button>
      </div>
    </>
  );
}
