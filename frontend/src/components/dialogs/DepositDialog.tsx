import { useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Text,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
  mergeClasses,
} from '@fluentui/react-components';
import {
  ArrowDownload24Regular,
  CheckmarkCircle24Filled,
  Warning24Regular,
} from '@fluentui/react-icons';
import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { isServiceOutage, type ApiProblem } from '../../api/problemBaseQuery';
import { isUnconfirmedSend } from '../../api/moneyProblem';
import { useDepositMutation } from '../../features/api/apiSlice';
import { useIdempotentMutation } from '../../hooks/useIdempotentMutation';
import { useFocusWhenLost } from '../../hooks/useFocusWhenLost';
import { useMovementCheck } from '../../hooks/useMovementCheck';
import { useSettledPress } from '../../hooks/useSettledPress';
import { formatCurrency, formatDateTime } from '../../utils/format';
import { useAccountClosedToast, WaitHint } from '../feedback';
import { MovementCheckStatus } from '../shared/MovementCheckStatus';
import { MoneyDialogShell } from './MoneyDialogShell';
import { useMoneyDialogStyles } from './moneyDialogStyles';
import {
  depositFormSchema,
  parseAmountInput,
  type DepositFormOutput,
  type DepositFormValues,
} from '../../forms/moneySchemas';
import { AmountField } from '../form/AmountField';
import { DescriptionField } from '../form/DescriptionField';
import {
  CHECK_AGAIN,
  CONNECTION_FAILED,
  DEPOSIT_FOUND,
  DEPOSIT_OUTCOME_UNKNOWN,
  DEPOSIT_WENT_THROUGH,
  SERVICE_UNAVAILABLE_NOTHING_CHANGED,
} from '../../api/problemMessages';

// ============================================
// TYPES
// ============================================

interface Account {
  id: string;
  name: string;
  accountNumber: string;
  balance: number;
}

export interface DepositDialogProps {
  isOpen: boolean;
  onClose: () => void;
  accounts: Account[];
  onSuccess?: () => void;
}

interface SuccessData {
  amount: number;
  accountName: string;
  newBalance: number;
  transactionId: string;
  replayed: boolean;
}

// ============================================
// STYLES
// ============================================

// ============================================
// CONSTANTS
// ============================================

const QUICK_AMOUNTS = [50, 100, 200, 500];

// ============================================
// COMPONENT
// ============================================

/**
 * T3 — the first production idempotent mutation (PR-9), now on RHF+Zod (the money-forms
 * rewrite): the form state (account/amount/description) lives in react-hook-form with
 * `depositFormSchema` as the resolver — the SAME #33 bounds, exact legacy copy — while the
 * idempotency spine is untouched: useIdempotentMutation keeps the key across KEEP outcomes
 * (IN_FLIGHT / network / 5xx) so Retry re-sends the SAME key + body, and a body edit rotates it
 * while NO key is held (an edited body with the old key is a 422 KEY_REUSE); with one RETAINED,
 * the edit latches verify-first instead, as WithdrawDialog's does. A replayed 2xx surfaces
 * a polite note; RESULT_UNKNOWN latches a verify-first flow, or, when the answer
 * says `applied: true`, the went-through view, which offers the history and nothing that sends
 * (ADR-0009). No step-up —
 * deposit is auth level 1. The shell is a Fluent Dialog now: focus trap, Escape and
 * aria-modal come from the platform, and BOTH dismissal paths (Esc/backdrop and the X)
 * funnel through the keyLive guard.
 *
 * The dialog does not outlive its account: when the deposit answers ACCOUNT_NOT_FOUND it closes
 * and the visitor is told in the app's toast outlet, as the rename dialog does.
 */
export function DepositDialog({ isOpen, onClose, accounts, onSuccess }: DepositDialogProps) {
  const styles = useMoneyDialogStyles();
  const navigate = useNavigate();
  const sayAccountClosed = useAccountClosedToast();

  const [depositTrigger] = useDepositMutation();
  const {
    submit,
    resetIntent,
    verifyRequired,
    wentThrough,
    keyRetained,
    requireVerify,
    lastSent,
    confirmWentThrough,
  } = useIdempotentMutation(depositTrigger);
  /*
    Under the check view, the listing is asked by the key that was sent, on the account that was
    sent: not the form's, which an edit has changed. A movement found there is the deposit going
    through: the went-through view below, with its amount, reference and time.
  */
  const check = useMovementCheck(
    lastSent,
    (sent) => ({ first: { accountId: sent.accountId, type: 'Deposit' } }),
    () => confirmWentThrough(),
  );
  const checkLanding = check.landingRef;
  /*
    The went-through sentence takes the focus the send lost. The Deposit button was disabled for
    the send and the browser handed its focus to `body`; a view drawn then is not read by a screen
    reader, and the dialog has no description of its own (`MoneyDialogShell` clears it). The dialog
    was mounted long before, so the landing is on the flag, not on the mount.
  */
  const wentThroughSentence = useFocusWhenLost<HTMLParagraphElement>(wentThrough);
  /*
    Under both views a press on their actions waits until what they draw has settled: each
    verdict, the went-through block and the new-key action at the bound move the buttons
    (`useSettledPress` has what was measured). And once more as the form comes back after "It
    didn't go through — try again": the form keeps its values, so its Deposit is enabled at once,
    and at 320×568 the second press of a double click on "try again" landed on it and sent a
    second deposit under a new key, with the form never seen. The form's content waits with its
    footer (`content`): its amounts and accounts change what that Deposit sends, and at 360×800
    the same second press landed on the description field and took focus there.
  */
  const settledPress = useSettledPress(
    verifyRequired,
    `${wentThrough} ${check.verdict} ${check.canCheckAgain} ${check.pastLive}`,
    { afterLeaving: true },
  );

  const schema = useMemo(() => depositFormSchema(), []);
  const { control, handleSubmit, setValue, getValues, watch, formState } = useForm<
    DepositFormValues,
    unknown,
    DepositFormOutput
  >({
    resolver: zodResolver(schema),
    mode: 'onChange',
    defaultValues: {
      accountId: accounts.length > 0 ? accounts[0].id : '',
      amount: '',
      description: '',
    },
  });

  const [isSubmitting, setIsSubmitting] = useState(false);
  const [inFlight, setInFlight] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<SuccessData | null>(null);
  // The receipt's title takes the focus the send lost, for the went-through sentence's reason:
  // with focus on `body` the receipt is not read and Escape reaches no dialog.
  const receiptTitle = useFocusWhenLost<HTMLParagraphElement>(!!success);
  /*
    The check view's title takes lost focus, as the went-through sentence does and for its
    reason: the control that sent, or the one an edit was made on, goes with the form, and a view
    drawn while focus is on `body` is not read. From `body` Escape reaches no dialog either. It
    lands as the view appears, before the listing by key has answered, so each verdict is said
    by the view's status region (`MovementCheckStatus`).
  */
  const checkTitle = useFocusWhenLost<HTMLParagraphElement>(
    !success && verifyRequired && !wentThrough,
  );
  // The form as it stood at the last send: what a retained key was sent with.
  const sentValues = useRef<DepositFormValues | null>(null);

  const accountId = watch('accountId');
  const amount = watch('amount');
  const amountNumber = parseAmountInput(amount);
  const selectedAccount = accounts.find((account) => account.id === accountId) ?? null;

  /*
    A tap or keystroke that leaves the form as it was sent is no edit: the amount already chosen
    tapped again, the selected account chosen again, a character the amount field drops. With a key
    retained, re-sending that key is still the way to find out what happened, and the words on
    screen say so; treating the tap as an edit would latch the check view and take both away.
  */
  const unchangedSinceSend = () => {
    const sent = sentValues.current;
    const now = getValues();
    return (
      sent !== null &&
      now.accountId === sent.accountId &&
      now.amount === sent.amount &&
      now.description === sent.description
    );
  };

  // A body-affecting edit rotates the key when NO key is held: the old key + a new body is a
  // raw-byte fingerprint mismatch → 422 KEY_REUSE. Also clears transient in-flight/error state.
  // Blocked while a request is in flight: rotating/nulling the key out from under a pending
  // submit would defeat the retained-key guard (a subsequent NETWORK/5xx/IN_FLIGHT could
  // then close/resubmit into a NEW intent while the original still settles).
  const onBodyEdit = () => {
    if (isSubmitting) return;
    /*
      A RETAINED key is answered by verify-first, not released — WithdrawDialog's rule, for its
      reason. Outside a submit a key is held only after IN_FLIGHT, a lost answer or a 5xx, when
      the first deposit may still land; rotating then made the edited amount a second deposit.
      The SPA's own 65 s abort keeps the key too, so without this every slow deposit would open
      that path. Latching drops the key (the dialog can close again) and shows the view that says
      to check the transactions first.

      The words the failed attempt left — "tap Deposit again", the in-flight note — go with it:
      the verify view has no Deposit button to tap.
    */
    if (keyRetained) {
      if (unchangedSinceSend()) return;
      requireVerify();
      setInFlight(false);
      setError(null);
      return;
    }
    resetIntent();
    setInFlight(false);
    setError(null);
  };

  const handleQuickAmount = (value: number) => {
    setValue('amount', value.toString(), { shouldValidate: true, shouldDirty: true });
    onBodyEdit();
  };

  const amountValid = amountNumber > 0 && !formState.errors.amount;
  const newBalance = selectedAccount ? selectedAccount.balance + amountNumber : 0;

  const onValid = async (data: DepositFormOutput) => {
    const account = accounts.find((a) => a.id === data.accountId);
    if (!account) return;
    const sent = getValues();
    sentValues.current = {
      accountId: sent.accountId,
      amount: sent.amount,
      description: sent.description,
    };
    setError(null);
    setInFlight(false);
    setIsSubmitting(true);
    try {
      const result = await submit({
        accountId: data.accountId,
        amount: data.amount,
        description: data.description,
      });
      setSuccess({
        amount: data.amount,
        accountName: account.name,
        newBalance: result.newBalance,
        transactionId: result.transaction.id,
        replayed: result.replayed,
      });
      onSuccess?.();
    } catch (caught) {
      const problem = caught as ApiProblem;
      // ADR-0022 decision 5: route on errorCode, never a blanket toast. RESULT_UNKNOWN is
      // handled by the hook (latches verifyRequired) — we just render that view. So are a
      // rejection with no HTTP status (an answer whose body failed its schema) and a 409 that
      // named no code, or a limiter refusal of a resend: the deposit may have landed, and
      // "Deposit failed" under the verify view would say it had not. The hook's own test,
      // using whether a key was held before this send, so the two cannot disagree.
      if (isUnconfirmedSend(problem, keyRetained)) {
        // hook set verifyRequired, and wentThrough when the server said it was committed; the
        // went-through view or the verify view renders below.
      } else if (problem.errorCode === 'IDEMPOTENCY_IN_FLIGHT') {
        setInFlight(true);
      } else if (problem.errorCode === 'VALIDATION_ERROR') {
        // Field-agnostic: surface the first server message rather than assuming 'amount'.
        const firstFieldError = Object.values(problem.errors ?? {})[0]?.[0];
        setError(firstFieldError ?? 'Please check the amount and try again.');
      } else if (
        problem.errorCode === 'IDEMPOTENCY_KEY_REUSE' ||
        problem.errorCode === 'IDEMPOTENCY_KEY_MISSING' ||
        problem.errorCode === 'IDEMPOTENCY_KEY_INVALID'
      ) {
        // Client protocol bug — never surface the raw code.
        setError('Something went wrong. Please try again.');
      } else if (problem.errorCode === 'ACCOUNT_NOT_FOUND') {
        /*
          The account was closed elsewhere. A 404 is a refusal: nothing was deposited, and the
          hook has dropped the key, so closing abandons no send. `onClose` and not
          `requestClose`: that one still sees this send as running. The list is read again by
          the mutation's own tags (apiSlice), so the account leaves the page behind the dialog.
        */
        sayAccountClosed();
        onClose();
      } else if (isServiceOutage(problem)) {
        // A 503 or no answer in 65 s — before the NETWORK branch, which the abort would otherwise
        // take. "Nothing was changed" only when the API said so (`applied: false`); otherwise the
        // deposit may have landed, and Deposit again re-sends the same key to find out. Never
        // "won't charge you twice": nothing is charged for money arriving.
        setError(
          problem.applied === false ? SERVICE_UNAVAILABLE_NOTHING_CHANGED : DEPOSIT_OUTCOME_UNKNOWN,
        );
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        // Transport failure — a raw "TypeError: Failed to fetch" would leak. The
        // key is KEPT (shouldKeepKey) so tapping Deposit again is a safe same-key retry.
        setError(CONNECTION_FAILED);
      } else {
        setError(problem.detail || 'Deposit failed. Please try again.');
      }
    } finally {
      setIsSubmitting(false);
    }
  };

  if (!isOpen) return null;

  const showForm = !success && !verifyRequired;

  // CRITICAL: never dismiss while an idempotency key is still LIVE. `keyRetained` covers
  // submitting AND every KEEP outcome (IN_FLIGHT / network / 5xx) — the dialog is
  // mount-on-open, so unmounting with a retained key loses it, and reopening mints a fresh
  // one so the same amount becomes a NEW intent = a real double-deposit. Esc and backdrop
  // dismissal (Fluent onOpenChange) funnel through the same guard as the X button.
  const keyLive = isSubmitting || keyRetained;
  const requestClose = () => {
    if (!keyLive) {
      onClose();
    }
  };

  return (
    <MoneyDialogShell
      open={isOpen}
      title={success || wentThrough ? 'Deposit Complete' : 'Deposit Money'}
      icon={<ArrowDownload24Regular />}
      tone="credit"
      onClose={requestClose}
      closeDisabled={keyLive}
      // Both views stand where the form was, and the second press of a double click on Deposit must
      // not take their words away unread. Where it lands depends on the size and on the phase
      // (`e2e/wentThrough.spec.ts` has what was measured). The went-through view, measured at
      // 1280×900, is shorter than the form, and the press lands outside it. Before the listing
      // answers, the check view leaves the point on the backdrop at 1280×900, 375×812 and 360×800,
      // and on "Check recent transactions" at 320×568. Once it says "not yet", the point is in its
      // footer's padding at 1280×900 and 375×812, on "Check recent transactions" at 360×800 and
      // 375×640, and in the view's words at 320×568, where the view keeps its height and the
      // surface scrolls. So a press outside closes nothing here, and the footer drops a press on
      // its actions until the view has settled (`useSettledPress`), whatever is under the point.
      // A check view closed by a press outside leaves the visitor on the dashboard, which has the
      // Deposit tile to press for a second deposit (the tile is read in the dashboard's code; the
      // run that measured the closing recorded no dialog and focus on `body`). `verifyRequired` is
      // true under both views and under nothing else, so the receipt and the form close as they
      // did. The hook latches it in two places: on the answer, where `wentThrough` is set with it
      // when the payment is proven, and in `requireVerify`, which this dialog calls when the body
      // is edited while a key is retained. The check view that edit brings up is kept the same way.
      keepOnOutsidePress={verifyRequired}
    >
      {/* Success */}
      {success && (
        <div className={styles.centeredView}>
          <div className={styles.successIcon}>
            <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
          </div>
          {/* A paragraph that takes focus, in no alert or status: one landing, one reading. */}
          <Text as="p" className={styles.successTitle} ref={receiptTitle} tabIndex={-1}>
            Deposit Successful!
          </Text>
          <Text className={styles.successAmount}>+{formatCurrency(success.amount)}</Text>
          {success.replayed && (
            <MessageBar intent="info" role="status" layout="multiline" className={styles.note}>
              <MessageBarBody>
                This deposit was already processed — showing the existing result.
              </MessageBarBody>
            </MessageBar>
          )}
          <div className={styles.detailsCard}>
            <div className={styles.detailRow}>
              <Text className={styles.detailLabel}>To</Text>
              <Text className={styles.detailValue}>{success.accountName}</Text>
            </div>
            <div className={styles.detailRow}>
              <Text className={styles.detailLabel}>New balance</Text>
              <Text className={styles.detailValue}>{formatCurrency(success.newBalance)}</Text>
            </div>
          </div>
        </div>
      )}

      {/* The server said the deposit was committed and could not return its receipt (409
          RESULT_UNKNOWN with `applied: true`, ADR-0009). Before the check view, and instead of
          it: the hook sets `verifyRequired` with `wentThrough`, and "it didn't go through" over a
          committed deposit is how a second one is made. The receipt's title and icon, and one
          sentence in place of the amount and the balance the answer does not carry. A paragraph
          that takes focus, in no alert or status: the landing is there so that a screen reader
          reads it, and one landing is meant to be one reading. What was heard is in ADR-0059's
          Validation. */}
      {!success && wentThrough && (
        <div className={styles.centeredView}>
          <div className={styles.successIcon}>
            <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
          </div>
          <Text as="p" className={styles.outcomeSentence} ref={wentThroughSentence} tabIndex={-1}>
            {check.movement ? DEPOSIT_FOUND : DEPOSIT_WENT_THROUGH}
          </Text>
          {/* The listing found it by the key: what the receipt would have shown of it. */}
          {check.movement && (
            <>
              <Text className={styles.successAmount}>+{formatCurrency(check.movement.amount)}</Text>
              <div className={styles.detailsCard}>
                <div className={styles.detailRow}>
                  <Text className={styles.detailLabel}>Reference</Text>
                  <Text className={styles.detailValue}>{check.movement.transactionNumber}</Text>
                </div>
                <div className={styles.detailRow}>
                  <Text className={styles.detailLabel}>Date &amp; time</Text>
                  <Text className={styles.detailValue}>
                    {formatDateTime(check.movement.createdAt)}
                  </Text>
                </div>
              </div>
            </>
          )}
        </div>
      )}

      {/* RESULT_UNKNOWN — verify-first (ADR-0022 decision 4) */}
      {!success && verifyRequired && !wentThrough && (
        <div className={styles.centeredView}>
          <div className={styles.warningIcon}>
            <Warning24Regular style={{ width: '40px', height: '40px' }} />
          </div>
          <Text as="p" className={styles.outcomeSentence} ref={checkTitle} tabIndex={-1}>
            {check.verdict === 'none' ? (
              <>Your deposit didn&apos;t go through</>
            ) : (
              <>We couldn&apos;t confirm your deposit</>
            )}
          </Text>
          {/* Hidden once the listing says it did not go through: "may or may not" is no longer
              true. */}
          {check.verdict !== 'none' && (
            <Text className={styles.stateBody}>
              The request may or may not have gone through. Check your recent transactions before
              trying again — retrying blindly could deposit twice.
            </Text>
          )}
          {check.verdict !== 'off' && <MovementCheckStatus check={check} />}
        </div>
      )}

      {/* Form */}
      {showForm && (
        <div className={styles.content} {...settledPress.content}>
          <div>
            <Text className={styles.sectionLabel}>Select Account</Text>
            <Controller
              control={control}
              name="accountId"
              render={({ field }) => (
                <>
                  {accounts.map((account) => {
                    const selectAccount = () => {
                      // A div can't be `disabled` — guard the mid-flight edit here.
                      if (isSubmitting) return;
                      field.onChange(account.id);
                      onBodyEdit();
                    };
                    return (
                      // Styled div, so the button semantics are wired by hand
                      // (same pattern as the AccountsPage add-card).
                      <div
                        key={account.id}
                        className={`${styles.accountCard} ${
                          field.value === account.id ? styles.accountCardSelected : ''
                        }`}
                        role="button"
                        tabIndex={0}
                        aria-pressed={field.value === account.id}
                        onClick={selectAccount}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            selectAccount();
                          }
                        }}
                        style={{ marginBottom: '8px' }}
                      >
                        <div className={styles.accountInfo}>
                          <Text className={styles.accountName}>{account.name}</Text>
                          <Text className={styles.accountNumber}>{account.accountNumber}</Text>
                        </div>
                        <Text className={styles.accountBalance}>
                          {formatCurrency(account.balance)}
                        </Text>
                      </div>
                    );
                  })}
                </>
              )}
            />
          </div>

          <div className={styles.amountSection}>
            <Text className={styles.amountLabel}>Amount</Text>
            <AmountField
              control={control}
              name="amount"
              ariaLabel="Deposit amount"
              disabled={isSubmitting}
              onBodyEdit={onBodyEdit}
              classNames={{
                wrapper: styles.amountInputWrapper,
                currency: styles.amountCurrency,
                input: mergeClasses(styles.amountInput, amount.length > 6 && styles.amountLong),
                hint: styles.amountHint,
              }}
              belowSlot={
                selectedAccount && amountValid ? (
                  <Text className={styles.newBalance}>
                    New balance: {formatCurrency(newBalance)}
                  </Text>
                ) : null
              }
            />
          </div>

          <div className={styles.quickAmounts}>
            {QUICK_AMOUNTS.map((quickAmount) => (
              <button
                key={quickAmount}
                className={`${styles.quickBtn} ${
                  amountNumber === quickAmount ? styles.quickBtnSelected : ''
                }`}
                onClick={() => handleQuickAmount(quickAmount)}
                disabled={isSubmitting}
              >
                €{quickAmount}
              </button>
            ))}
          </div>

          <DescriptionField
            control={control}
            name="description"
            disabled={isSubmitting}
            onBodyEdit={onBodyEdit}
            className={styles.descriptionInput}
          />
        </div>
      )}

      {/* Footer */}
      <div className={styles.footer} {...settledPress.actions}>
        {error && (
          <MessageBar intent="error" role="alert" className={styles.errorMessage}>
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
        {inFlight && (
          <MessageBar intent="info" role="status" className={styles.errorMessage}>
            <MessageBarBody>Still processing — tap Deposit again to check.</MessageBarBody>
          </MessageBar>
        )}

        {success ? (
          <>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => void navigate(`/transactions/${success.transactionId}`)}
            >
              View Transaction
            </Button>
            <Button
              appearance="secondary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={onClose}
            >
              Done
            </Button>
          </>
        ) : wentThrough ? (
          /*
            The one action, and the shell's X beside it: nothing here can send again. It carries a
            `key` because the form's arm below is a bare button in this same slot: without one
            React would keep that node and only change its words, under a focus that never left
            it, so a second press meant for "Deposit" would land on "View History" and the
            sentence would never be reached.
          */
          <Button
            key="view-history"
            appearance="primary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={() => void navigate('/history')}
          >
            View History
          </Button>
        ) : verifyRequired ? (
          /*
            The check view's actions follow what the listing said (`useMovementCheck`): "Check
            again" leads while asking can still tell more, and "try again", a new key, is drawn
            only once nothing sent under the old one can commit any more. With nothing to ask
            about (`off`) they are the two they always were.
          */
          <>
            {check.checkAgainFirst && (
              <Button
                key="check-again"
                ref={checkLanding}
                appearance="primary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                disabled={check.checking}
                onClick={check.checkAgain}
              >
                {CHECK_AGAIN}
              </Button>
            )}
            <Button
              key="check-transactions"
              ref={check.checkAgainFirst ? undefined : checkLanding}
              appearance={check.checkAgainFirst ? 'secondary' : 'primary'}
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => void navigate('/history')}
            >
              Check recent transactions
            </Button>
            {!check.checkAgainFirst && check.offerCheckAgain && (
              <Button
                key="check-again"
                appearance="secondary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                disabled={check.checking}
                onClick={check.checkAgain}
              >
                {CHECK_AGAIN}
              </Button>
            )}
            {(check.verdict === 'off' || check.offerNewKey) && (
              <Button
                key="try-again"
                appearance="secondary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={resetIntent}
              >
                It didn&apos;t go through — try again
              </Button>
            )}
          </>
        ) : (
          <Button
            appearance="primary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={() => void handleSubmit(onValid)()}
            disabled={isSubmitting || !formState.isValid}
          >
            {isSubmitting ? (
              <Spinner size="tiny" />
            ) : (
              `Deposit ${amountNumber > 0 ? formatCurrency(amountNumber) : ''}`
            )}
          </Button>
        )}
        {/* Under the button that started the send, inside the dialog (a modal hides everything
            outside it), and promising nothing: during the wait nothing here can re-send it. From
            20 s it asks the visitor to keep the page open: a reload would send it with a new key. */}
        <WaitHint active={isSubmitting} kind="moneySend" />
      </div>
    </MoneyDialogShell>
  );
}

export default DepositDialog;
