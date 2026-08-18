import { useEffect, useRef, useState } from 'react';
import {
  makeStyles,
  Text,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
  MessageBarActions,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircle24Filled, ArrowSwap24Regular } from '@fluentui/react-icons';
import { Controller, useForm, type FieldErrors } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { colors } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import { useTransferWizardStyles } from './transferWizardStyles';
import { ConfirmDialog } from '../components/shared/ConfirmDialog';
import { ResultUnknownView } from '../components/shared/ResultUnknownView';
import { PageHeader } from '../components/layout/PageHeader';
import {
  useGetAccountsQuery,
  useLazyLookupRecipientQuery,
  useAuthoriseTransferMutation,
  useTransferMutation,
  type AccountResponse,
} from '../features/api/apiSlice';
import { useMoneyWizard } from '../hooks/useMoneyWizard';
import { formatCurrency, formatLockHorizon, maskAccountNumber } from '../utils/format';
import { RetryCountdown, retryDeadline } from '../components/feedback';
import {
  insufficientFundsMessage,
  normalizeAzureTag,
  parseAmountInput,
  transferFormSchema,
  type TransferFormOutput,
  type TransferFormValues,
} from '../forms/moneySchemas';
import { AmountField } from '../components/form/AmountField';
import { availableBalanceOf } from '../utils/availableBalance';
import { useFundsGate } from '../hooks/useFundsGate';
import { PinInput } from '../components/PinInput';
import { CONNECTION_FAILED } from '../api/problemMessages';

// ============================================
// CONSTANTS
// ============================================

const QUICK_AMOUNTS = [10, 25, 50, 100, 250];

const PIN_LENGTH = 6;
const DEFAULT_PIN_LOCK_SECONDS = 15 * 60;

interface Recipient {
  azureTag: string;
  displayName: string;
}

interface SuccessData {
  amount: number;
  recipientName: string;
  recipientAzureTag: string;
  newBalance: number;
  transactionNumber: string;
  replayed: boolean;
}

function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  return (parts[0][0] + (parts[1]?.[0] ?? '')).toUpperCase();
}

// ============================================
// STYLES
// ============================================

/**
 * The six keys that belong to the recipient step — the part of this flow internal transfer does not
 * have. Everything else comes from `useTransferWizardStyles`, which both wizards share.
 */
const useRecipientStyles = makeStyles({
  recipientRow: { display: 'flex', gap: '8px' },
  input: {
    flex: 1,
    padding: '12px',
    borderRadius: '8px',
    border: `1px solid ${colors.neutral[300]}`,
    fontSize: '15px',
    fontFamily: 'inherit',
    color: colors.neutral[800],
    outline: 'none',
    ':focus': { border: `1px solid ${colors.brand[60]}` },
    ':disabled': { backgroundColor: colors.neutral[100] },
  },
  recipientCard: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '14px 16px',
    backgroundColor: colors.semantic.success.light,
    borderRadius: '12px',
  },
  avatar: {
    width: '40px',
    height: '40px',
    borderRadius: '50%',
    backgroundColor: colors.brandFill.rest,
    color: tokens.colorNeutralForegroundOnBrand,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '15px',
    fontWeight: 600,
    flexShrink: 0,
  },
  recipientName: { fontSize: '15px', fontWeight: 600, color: colors.neutral[800] },
  recipientTag: { fontSize: '13px', color: colors.neutral[500] },
});

// ============================================
// COMPONENT
// ============================================

/**
 * PR-11 — the real external transfer (to another user's primary account by AzureTag), now on
 * RHF+Zod (the money-forms rewrite): step-1's fields (from-account, recipient handle, amount)
 * live in react-hook-form with the balance-capped `transferFormSchema` as resolver, while the
 * VERIFIED-recipient truth stays async component state (the exact-match lookup IS the
 * validator, ADR-0014 — a schema cannot own server truth).
 *
 * ADR-0041 changed where the PIN comes from. It used to be collected by the ROOT step-up modal:
 * the BFF answered 403 on a level-1 session, the base-query interceptor popped the modal, the
 * session elevated, and the same request replayed. That made the PIN a property of the SESSION —
 * one entry then covered every transfer for five minutes, and any caller reaching the API directly
 * skipped it entirely. The PIN is now collected HERE, as the last step before sending, and travels
 * in the request body for the API to verify. The review screen's promise that you confirm with your
 * PIN on the next step is, as of this change, true.
 */
export function TransferPage() {
  // Merged so the markup keeps addressing one `styles` object.
  const styles = { ...useTransferWizardStyles(), ...useRecipientStyles() };

  const {
    data: accounts = [],
    error: accountsError,
    refetch: refetchAccounts,
  } = useGetAccountsQuery();
  const accountsProblem = accountsError as ApiProblem | undefined;
  const [lookup, lookupState] = useLazyLookupRecipientQuery();
  const [transferTrigger] = useTransferMutation();
  const wizard = useMoneyWizard(transferTrigger, {
    // This flow's OWN codes. Everything protocol-shaped — step-up, in-flight, key reuse, network —
    // is the wizard's, and the type of this table makes naming one of those here a compile error.
    // ACCOUNT_NOT_FOUND lives here rather than in the shared tail because its wording is
    // flow-specific: "re-check the handle" is meaningless on a page with no handle.
    messages: {
      SELF_TRANSFER_NOT_ALLOWED: "You can't send money to yourself.",
      ACCOUNT_NOT_FOUND: 'That recipient could not be found. Please re-check the handle.',
    },
    fallback: 'Transfer failed. Please try again.',
  });
  // Destructured under the names the markup already used, so this change is confined to the
  // machine: not one element below moves.
  const { step, isSubmitting, inFlight, error, verifyRequired, keyLive, onBodyEdit, requestLeave } =
    wizard;

  // ===== PIN step (ADR-0041) =====
  const [authoriseTransfer, { isLoading: isMinting }] = useAuthoriseTransferMutation();

  /*
    Every exit, held for BOTH phases of the submit.

    `keyLive` is `isSubmitting || keyRetained` (useMoneyWizard.ts:142), and neither is set until
    `wizard.run` starts — so for the whole MINT round trip `keyLive` is false and every control it
    guards is live. The PIN input was taught the two-phase guard when the mint landed; the exits
    were not, so a user could press Back mid-mint, watch the wizard step backwards, and have the
    transfer complete underneath them.

    One name for the rule, used by the header and by the PIN step's own Back, so the two cannot
    answer differently.
  */
  const exitLocked = keyLive || isMinting;

  /*
    The PIN the sixth digit just completed.

    A REF, not the state, and for the reason `lastProblem` is one: `onComplete` fires from inside
    `onChange`, so `pin` has not re-rendered yet when the submit begins. Reading state here would be
    one render stale — invisible in a hand test, a flake in CI. `setPin` still drives the display.
  */
  const enteredPin = useRef('');

  /*
    The authorisation minted for the intent currently in flight.

    A retry of a RETAINED key is the same intent, so it must not mint a second authorisation: the
    first one may already have been consumed by the request whose answer never arrived, and if it
    was not, this one is still the authorisation for these exact fields. Minting again would leave
    an orphan row and tell the server a different story than the first attempt did.
  */
  const lastAuthorization = useRef<string | null>(null);

  /*
    A reactive mirror of "we are holding an authorisation", because a ref cannot gate a render.

    The ref stays the source of truth — `onValid` reads it synchronously between the mint and the
    send, and turning it into state would reintroduce exactly the stale read `useMoneyWizard`
    documents. This is the same split `useIdempotentMutation` already uses for the key itself.
  */
  const [authorizationHeld, setAuthorizationHeld] = useState(false);

  /*
    When to offer a re-send, and why it is not `inFlight`.

    `inFlight` is set only for 409 IDEMPOTENCY_IN_FLIGHT. But the case this control was built for —
    a response that never arrived — is `status: 'NETWORK'`, which `classifyMoneyProblem` routes to a
    plain message. So for the exact scenario it exists to serve, the button did not render, and with
    every exit held by `exitLocked` the user had no control at all: not a loop, a dead end.

    The honest condition is "we are holding a key AND an authorisation to re-present". That also
    makes the two states mutually exclusive by construction rather than by invariant: holding an
    authorisation offers the re-send, and having had it refused empties the PIN boxes and asks for
    six digits instead.
  */
  const canResend = keyLive && authorizationHeld && !isSubmitting && !isMinting;

  // `pinNonce` remounts PinInput so a cleared retry refocuses box 1 — the same device WithdrawDialog
  // uses, for the same reason.
  const [pin, setPin] = useState('');
  const [pinError, setPinError] = useState(false);
  const [pinNonce, setPinNonce] = useState(0);
  /*
    An ABSOLUTE deadline (D13), shared with the withdraw dialog and the step-up modal.

    This page used to drive its own one-second decrement, and so did the sibling transfer page, and
    the two dialogs drove nothing at all — three implementations of one requirement, two of which
    were a frozen countdown. `RetryCountdown` is the single primitive; storing the deadline rather
    than a duration is what makes a second lock with the identical retryAfterSeconds mint a fresh one
    instead of reviving an elapsed number.
  */
  const [pinLockDeadline, setPinLockDeadline] = useState<number | null>(null);

  const [recipient, setRecipient] = useState<Recipient | null>(null);
  const [recipientError, setRecipientError] = useState<string | null>(null);
  const [success, setSuccess] = useState<SuccessData | null>(null);

  // ===== RHF step-1 form =====
  // The balance bound tracks the SELECTED account; defaults resolve once accounts load.
  const [balanceBound, setBalanceBound] = useState(0);
  const { control, handleSubmit, setValue, watch, formState, trigger } = useForm<
    TransferFormValues,
    unknown,
    TransferFormOutput
  >({
    resolver: zodResolver(transferFormSchema(balanceBound)),
    mode: 'onChange',
    defaultValues: { fromAccountId: '', recipientTag: '', amount: '' },
  });

  const watchedAccountId = watch('fromAccountId');
  const watchedTag = watch('recipientTag');
  const amountNumber = parseAmountInput(watch('amount'));

  const selectedAccount =
    accounts.find((a) => a.id === watchedAccountId) ??
    accounts.find((a) => a.isPrimary) ??
    accounts[0] ??
    null;
  const availableBalance = selectedAccount ? availableBalanceOf(selectedAccount) : 0;

  // See `useFundsGate`: the Zod bound is built from a CACHED balance, and reaching the PIN step
  // with a stale one costs a PIN attempt on a request that cannot succeed.
  const confirmFunds = useFundsGate();
  const [checkingFunds, setCheckingFunds] = useState(false);

  // Auto-select the legacy default (primary ?? first) into the form once accounts load,
  // and keep the schema's balance bound in lockstep with the selected account.
  useEffect(() => {
    if (selectedAccount && watchedAccountId !== selectedAccount.id) {
      setValue('fromAccountId', selectedAccount.id, { shouldValidate: true });
    }
  }, [selectedAccount, watchedAccountId, setValue]);
  useEffect(() => {
    setBalanceBound(availableBalance);
  }, [availableBalance]);
  // Re-run amount validation once the bound (and thus the resolver) has actually updated:
  // mode 'onChange' only revalidates the field that changed, so an account switch alone
  // would leave the amount's cached validity (and canReview) on the PREVIOUS balance.
  useEffect(() => {
    void trigger('amount');
  }, [balanceBound, trigger]);

  const handleSelectAccount = (account: AccountResponse) => {
    if (keyLive) return;
    setValue('fromAccountId', account.id, { shouldValidate: true });
    onBodyEdit();
  };

  const handleQuickAmount = (value: number) => {
    if (keyLive) return;
    setValue('amount', value.toString(), { shouldValidate: true, shouldDirty: true });
    onBodyEdit();
  };

  const handleVerifyRecipient = async () => {
    const tag = normalizeAzureTag(watchedTag);
    if (!tag) return;
    setRecipient(null);
    setRecipientError(null);
    onBodyEdit();
    try {
      const result = await lookup(tag).unwrap();
      if (result.exists) {
        setRecipient({ azureTag: result.azureTag, displayName: result.displayName });
      } else {
        setRecipientError(`We couldn't find @${tag}. Check the handle and try again.`);
      }
    } catch (caught) {
      /*
        A bare catch here told every failure the same story — "check your connection" — and this
        endpoint has a DEDICATED rate limiter: /api/users/* runs a tight per-user sliding window
        (ADR-0014) and rejects with 429 + Retry-After. So the one failure a user is most likely to
        provoke, by tapping Verify repeatedly, sent them to check a connection that was fine.

        Branching on STATUS rather than errorCode is deliberate: the BFF's rejection body is a bare
        ProblemDetails with no errorCode, so the client synthesises HTTP_429 and there is no code to
        match on. `retryAfterSeconds` is read from the Retry-After header the limiter always sets.
      */
      const problem = caught as ApiProblem;
      if (problem.status === 429) {
        setRecipientError(
          problem.retryAfterSeconds !== undefined
            ? `Too many lookups. Try again in ${formatLockHorizon(problem.retryAfterSeconds)}.`
            : 'Too many lookups. Please wait a moment and try again.',
        );
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        setRecipientError(CONNECTION_FAILED);
      } else {
        setRecipientError(problem.detail || "We couldn't check that handle. Please try again.");
      }
    }
  };

  // The schema owns account/tag-format/amount validity; the VERIFIED recipient is the
  // extra, server-truth gate that Zod cannot own (D6).
  const canReview = formState.isValid && !!selectedAccount && !!recipient;
  const newBalance = availableBalance - amountNumber;

  /**
   * Review -> PIN, but only once the SERVER has confirmed the amount still fits.
   *
   * The form's validity was decided against a cached balance, possibly a page load ago. Asking the
   * server here — before the PIN screen exists — is what keeps a strong-authentication ceremony
   * from being spent on a transfer that is already known to fail, and what keeps a mistyped PIN on
   * such an attempt from counting toward the fifteen-minute lockout.
   */
  const onReviewed = async () => {
    if (!selectedAccount || !recipient) return;
    setCheckingFunds(true);
    try {
      const verdict = await confirmFunds(selectedAccount.id, amountNumber);
      if (verdict.status === 'insufficient') {
        // Back to the form, where the amount is editable — and `fail` is 'input'-scoped, so the
        // banner survives that transition rather than being cleared by it.
        wizard.toForm();
        wizard.fail(insufficientFundsMessage(verdict.available));
        return;
      }
      // 'unknown' proceeds on purpose: a courtesy check must not become an outage. The server
      // still refuses, and `INSUFFICIENT_FUNDS` is already handled below.
      setPin('');
      setPinError(false);
      setPinNonce((n) => n + 1);
      wizard.toPin();
    } finally {
      setCheckingFunds(false);
    }
  };

  /**
   * `handleSubmit`'s invalid branch. Without it, a form that went invalid while the PIN step was
   * open made Send do nothing at all — no request, no message. The errors arrive as an argument so
   * this reports what actually failed rather than guessing.
   */
  const onInvalid = (errors: FieldErrors<TransferFormValues>) => {
    wizard.toForm();
    wizard.fail(errors.amount?.message ?? 'Please check the details and try again.');
  };

  const onValid = async (data: TransferFormOutput) => {
    if (!selectedAccount || !recipient) return;
    if (enteredPin.current.length !== PIN_LENGTH || pinLockDeadline !== null) return;
    // Narrowed to a const BEFORE the await, and the receipt is built from that same const. The
    // review screen and the request therefore cannot name two different people.
    const confirmed = recipient;

    /*
      Last look before the request leaves. The funds gate took a round trip at Continue; this one
      is free — the freshest CACHED balance, which a mutation elsewhere in this tab may have moved
      since. An over-balance transfer is therefore not merely refused by the server: it is never
      sent.
    */
    const stillAvailable = availableBalanceOf(selectedAccount);
    if (data.amount > stillAvailable) {
      wizard.toForm();
      wizard.fail(insufficientFundsMessage(stillAvailable));
      return;
    }

    /*
      MINT, then SEND (ADR-0042). Two calls, one user action — the sixth digit starts both, so the
      two-minute window is normally milliseconds wide and the user never learns it exists.

      The mint carries no idempotency key by design, so it cannot go through `wizard.run`. Its
      refusals are nevertheless the SAME ones the send already handles — 401 INVALID_PIN, 429
      PIN_LOCKED, 422 PIN_REQUIRED — so both paths funnel into `handleRefusal` rather than growing a
      second copy free to drift.
    */
    /*
      `keyLive` means the idempotency hook is holding a key from an attempt whose outcome is
      unknown — IN_FLIGHT, a network failure, a 5xx. The only sanctioned forward action there is to
      re-send the SAME intent, so we re-present the SAME authorisation rather than minting a new one.
    */
    let authorizationId: string;
    if (keyLive && lastAuthorization.current) {
      authorizationId = lastAuthorization.current;
    } else {
      try {
        const minted = await authoriseTransfer({
          fromAccountId: data.fromAccountId,
          recipientAzureTag: confirmed.azureTag,
          amount: data.amount,
          pin: enteredPin.current,
        }).unwrap();
        authorizationId = minted.authorizationId;
        lastAuthorization.current = authorizationId;
        setAuthorizationHeld(true);
      } catch (caught) {
        // Not a `run` failure, so the wizard has not classified it — hand it the same classifier.
        wizard.failFrom(caught as ApiProblem);
        handleRefusal(caught as ApiProblem);
        return;
      }
    }

    const result = await wizard.run(
      {
        fromAccountId: data.fromAccountId,
        recipientAzureTag: confirmed.azureTag,
        amount: data.amount,
      },
      // A HEADER at the wire, never a body field: the server fingerprints the body alone.
      { stepUpAuthorizationId: authorizationId },
    );
    // `run` resolves to undefined when it failed — and it has already set the banner, the in-flight
    // note or the verify view. Under `strict` this early return is not optional: reading
    // `result.newBalance` without it does not compile.
    if (!result) {
      /*
        The PIN outcomes, keyed on the CODE rather than the rendered sentence (the wizard exposes
        `lastProblem` as a REF precisely so this does not have to match on prose, and so the read is not
        one render stale).

        A wrong PIN is safe to retry in place: 401 is exempt from the global logout, and the
        idempotency hook has already dropped the key — so the corrected-PIN retry mints a fresh one
        rather than replaying the refused body.
      */
      handleRefusal(wizard.lastProblem.current);
      return;
    }

    setPin('');
    enteredPin.current = '';
    lastAuthorization.current = null;
    setAuthorizationHeld(false);

    setSuccess({
      amount: data.amount,
      recipientName: confirmed.displayName,
      recipientAzureTag: confirmed.azureTag,
      newBalance: result.newBalance,
      transactionNumber: result.transactionNumber,
      replayed: result.replayed,
    });
  };

  /**
   * What the PIN step does about a refusal, shared by the mint and the send so the two cannot drift.
   * The banner itself is the wizard's; this owns only the PIN box and the lock.
   */
  function handleRefusal(refusal: ApiProblem | null) {
    if (refusal?.errorCode === 'INVALID_PIN') {
      setPin('');
      enteredPin.current = '';
      setPinError(true);
      setPinNonce((n) => n + 1);
    } else if (
      refusal?.errorCode === 'AUTHORIZATION_EXPIRED' ||
      refusal?.errorCode === 'AUTHORIZATION_INVALID'
    ) {
      /*
          The user STAYS on this step, and the form keeps the amount and the payee.

          Not a courtesy: WCAG 2.2 SC 3.3.7 Redundant Entry is LEVEL A, and its exception covers
          security information only. The PIN is inside it; the amount and payee are not, so
          discarding them would be a failure of the criterion rather than a UX preference.

          No `setPinError` — an expiry is not a wrong PIN. It costs no attempt server-side, and
          styling the boxes red would tell the user the lock is closer when it is not.
        */
      setPin('');
      enteredPin.current = '';
      setPinNonce((n) => n + 1);
      /*
          AND DROP THE AUTHORISATION — the line this whole PR exists for.

          `onValid` re-presents `lastAuthorization.current` whenever a key is live, which is right
          for a lost response (re-send the same intent) and catastrophic here: the authorisation the
          server just refused would be re-sent forever, and the six digits the user keeps typing
          would never reach a mint. Dropping it sends the next completion down the mint branch,
          while `submit`'s `keyRef.current ??=` reuses the SAME idempotency key and the body is
          rebuilt from the same unchanged form values — byte-identical, so no 422.

          MEASURED end to end on the running API: key K with an expired authorisation answers 401
          AUTHORIZATION_EXPIRED and releases the record; key K with a freshly minted one answers
          201, no `Idempotency-Replayed`. One payment, one key, two authorisations.
        */
      lastAuthorization.current = null;
      setAuthorizationHeld(false);
    } else if (refusal?.errorCode === 'PIN_LOCKED') {
      setPin('');
      enteredPin.current = '';
      setPinLockDeadline(retryDeadline(refusal.retryAfterSeconds ?? DEFAULT_PIN_LOCK_SECONDS));
    } else if (refusal?.errorCode === 'PIN_REQUIRED') {
      // No PIN enrolled. Nothing on this page can fix that, so send them where it can be.
      // `requestLeave`, not a bare navigate: this page deliberately owns no destinations of
      // its own, and the wizard refuses any exit while an idempotency key is live. A 422 is
      // a key-DROP class, so this one goes through.
      requestLeave('/pin-setup?returnTo=/transfer');
    }
  }

  // ===== Success receipt =====
  if (success) {
    return (
      <div className={styles.page}>
        {/* No exits: the money has moved, so there is nothing to abandon, and the body's two
            buttons are the way on. The bare `<span />` on the left used to leave this title 40px
            off centre against the 40px spacer on the right. */}
        <PageHeader title="Transfer Complete" />
        <div className={styles.body}>
          <div className={styles.centeredView}>
            <div className={styles.successIcon}>
              <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
            </div>
            <Text className={styles.successTitle}>Transfer Sent!</Text>
            <Text className={styles.successAmount}>-{formatCurrency(success.amount)}</Text>
            {success.replayed && (
              <MessageBar intent="info" role="status">
                <MessageBarBody>
                  This transfer was already processed — showing the existing result.
                </MessageBarBody>
              </MessageBar>
            )}
            <div className={styles.reviewCard} style={{ width: '100%' }}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>
                  {success.recipientName} (@{success.recipientAzureTag})
                </Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Reference</Text>
                <Text className={styles.reviewValue}>{success.transactionNumber}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>New balance</Text>
                <Text className={styles.reviewValue}>{formatCurrency(success.newBalance)}</Text>
              </div>
            </div>
          </div>
          <div className={styles.actions}>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => requestLeave('/history')}
            >
              View History
            </Button>
            <Button
              appearance="secondary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => requestLeave('/dashboard')}
            >
              Done
            </Button>
          </div>
        </div>
      </div>
    );
  }

  // ===== RESULT_UNKNOWN verify view =====
  if (verifyRequired) {
    return (
      <ResultUnknownView
        title="Send Money"
        repeatWarning="send twice"
        onCheckTransactions={() => requestLeave('/history')}
        onStartOver={wizard.startOver}
      />
    );
  }

  return (
    <div className={styles.page}>
      {/* THE GUARDED HEADER. `keyLive` now comes from the wizard, but the rule is unchanged and the
          guard is doubled rather than moved: `disabled={keyLive}` still bars the control, and
          `requestLeave` / `toForm` re-check it themselves, so deleting one JSX attribute no longer
          opens the exit. PageHeader takes handlers and calls them; it never decides a destination,
          which is why the shared bar cannot quietly delete the guard — and the wizard likewise
          takes a destination and refuses it, rather than choosing one. */}
      <PageHeader
        title={
          step === 'pin' ? 'Confirm with PIN' : step === 'review' ? 'Review Transfer' : 'Send Money'
        }
        onBack={() =>
          step === 'pin'
            ? wizard.toReview()
            : step === 'review'
              ? wizard.toForm()
              : requestLeave('/dashboard')
        }
        backDisabled={exitLocked}
        onClose={() => requestLeave('/dashboard')}
        closeDisabled={exitLocked}
      />

      <div className={styles.body}>
        {/* Loading/error/empty are first-class states (D22) — the convention AccountsPage and
            DashboardPage already follow and both transfer wizards did not. Without this a failed
            accounts load left the page standing with empty pickers and no explanation, so a
            transient network fault silently blocked transfers. */}
        {accountsProblem && (
          <MessageBar intent="error" role="alert">
            <MessageBarBody>
              {accountsProblem.detail || 'Could not load your accounts.'}
              {accountsProblem.traceId ? ` Support code: ${accountsProblem.traceId}` : ''}
            </MessageBarBody>
            <MessageBarActions>
              <Button appearance="transparent" onClick={() => void refetchAccounts()}>
                Retry
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}
        {error && (
          <MessageBar intent="error" role="alert">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
        {canResend && (
          /*
            The retained-key state, and the ONLY place a deliberate re-send control belongs.

            Removing the Send button left this state with no way out: the page does not clear the
            PIN on IN_FLIGHT (only PIN-specific refusals clear it), so the boxes stay full,
            `onComplete` cannot fire again because the value never changes, and the banner asked the
            user to tap a control that no longer existed. Found by audit, not by a test — every test
            that reaches this state used to click Send.

            This re-sends the SAME key, the SAME body and the SAME authorisation. It is a check, not
            a second payment, which is why it is worded as one — and the wording splits, because the
            two ways to get here are not equally knowable. A 409 IN_FLIGHT means the server told us
            it is working on it. A lost response means we do not know whether anything happened, and
            saying "still processing" there would assert something nobody has been told.
          */
          <MessageBar intent="info" role="status">
            <MessageBarBody>
              {inFlight
                ? 'Still processing — check again to see whether it went through.'
                : "We couldn't reach the bank. Your transfer may or may not have gone through — check again."}
            </MessageBarBody>
            <MessageBarActions>
              <Button
                appearance="primary"
                size="small"
                /*
                  BOTH phases, exactly as the PIN input above. Today this control cannot actually
                  reach the mint — `onValid` re-presents `lastAuthorization.current` whenever a key
                  is live, and a live key implies a send, which implies a mint that already
                  succeeded. The guard does not depend on that invariant on purpose: it is one
                  identifier, and the invariant is three files away from anyone editing this button.
                */
                disabled={isMinting || isSubmitting}
                onClick={() => void handleSubmit(onValid, onInvalid)()}
              >
                Check again
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}

        {/* Nothing to pick from, so nothing to fill in. AccountsPage takes the same line — it
            hides its grid while a problem is showing — and without this the amount field stays
            editable over an empty account list, stacking "Available: €0.00" and "Exceeds available
            balance of €0.00" underneath the real error.

            Scoped to `accounts.length === 0` rather than to the problem alone, because RTK Query
            keeps the last data when a REFETCH fails: in that case the picker still has real
            accounts, and blanking a half-filled money form would be the worse bug. */}
        {accountsProblem && accounts.length === 0 ? null : step === 'form' ? (
          <>
            {/* From account */}
            <div>
              <Text className={styles.sectionLabel}>From</Text>
              <div
                style={{ display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '8px' }}
              >
                {accounts.map((account) => (
                  <button
                    key={account.id}
                    className={`${styles.card} ${
                      selectedAccount?.id === account.id ? styles.cardSelected : ''
                    }`}
                    // Drift, not a design choice: the internal wizard's identical cards have
                    // carried both of these since it was written, so a screen-reader user picking a
                    // source account heard "pressed" on one page and an unnamed button with no
                    // selected state on the other. `aria-pressed` is what makes the SELECTION
                    // audible at all — the blue border says it to sighted users only.
                    //
                    // `aria-label` REPLACES the contents-derived name, so this DOES drop the masked
                    // number and the balance from the announcement. A review proposed folding both
                    // into the label; measured, that breaks six assertions — including pre-existing
                    // ones on the internal page, which queries its cards by exact accessible name —
                    // and makes every card verbose. Declined, because the balance that governs the
                    // transfer is already announced as "Available: …" beside the amount field on
                    // both pages, and the number is masked. The short form also keeps the two
                    // wizards identical, which is the point of this PR.
                    aria-label={`From ${account.name}`}
                    aria-pressed={selectedAccount?.id === account.id}
                    onClick={() => handleSelectAccount(account)}
                  >
                    <div className={styles.accountInfo}>
                      <Text className={styles.accountName}>{account.name}</Text>
                      <Text className={styles.accountNumber}>
                        {maskAccountNumber(account.accountNumber)}
                      </Text>
                    </div>
                    <Text className={styles.accountBalance}>{formatCurrency(account.balance)}</Text>
                  </button>
                ))}
              </div>
            </div>

            {/* Recipient */}
            <div>
              <Text className={styles.sectionLabel}>To (recipient&apos;s @handle)</Text>
              <div className={styles.recipientRow} style={{ marginTop: '8px' }}>
                <Controller
                  control={control}
                  name="recipientTag"
                  render={({ field }) => (
                    <input
                      className={styles.input}
                      placeholder="@handle"
                      aria-label="Recipient handle"
                      ref={field.ref}
                      name={field.name}
                      value={field.value}
                      disabled={keyLive}
                      onBlur={field.onBlur}
                      onChange={(e) => {
                        field.onChange(e.target.value);
                        setRecipient(null);
                        setRecipientError(null);
                        onBodyEdit();
                      }}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') void handleVerifyRecipient();
                      }}
                    />
                  )}
                />
                <Button
                  appearance="secondary"
                  onClick={() => void handleVerifyRecipient()}
                  disabled={!watchedTag.trim() || lookupState.isFetching}
                >
                  {lookupState.isFetching ? <Spinner size="tiny" /> : 'Verify'}
                </Button>
              </div>
              {recipient && (
                <div className={styles.recipientCard} style={{ marginTop: '10px' }}>
                  <div className={styles.avatar}>{initials(recipient.displayName)}</div>
                  <div>
                    <Text className={styles.recipientName}>{recipient.displayName}</Text>
                    <br />
                    <Text className={styles.recipientTag}>@{recipient.azureTag}</Text>
                  </div>
                </div>
              )}
              {recipientError && (
                <Text
                  role="alert"
                  className={styles.hint}
                  style={{ marginTop: '8px', display: 'block' }}
                >
                  {recipientError}
                </Text>
              )}
            </div>

            {/* Amount */}
            <div className={styles.amountSection}>
              <Text className={styles.subtle}>Amount</Text>
              <AmountField
                control={control}
                name="amount"
                ariaLabel="Transfer amount"
                disabled={keyLive}
                onBodyEdit={onBodyEdit}
                classNames={{
                  wrapper: styles.amountWrapper,
                  currency: styles.amountCurrency,
                  input: styles.amountInput,
                  hint: styles.hint,
                  invalid: styles.amountInvalid,
                }}
                belowSlot={
                  <div className={styles.availableRow}>
                    <Text className={styles.subtle}>
                      Available: {formatCurrency(availableBalance)}
                    </Text>
                    {/* The constructive half of the balance cap: reaching the maximum without
                        retyping it is what stops the over-balance typo at the source. */}
                    <button
                      type="button"
                      className={styles.useMaxBtn}
                      onClick={() => handleQuickAmount(availableBalance)}
                      disabled={keyLive || availableBalance <= 0}
                      aria-label={`Use maximum, ${formatCurrency(availableBalance)}`}
                    >
                      Use max
                    </button>
                  </div>
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
                  disabled={quickAmount > availableBalance}
                >
                  €{quickAmount}
                </button>
              ))}
            </div>

            <div className={styles.actions}>
              <Button
                appearance="primary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => wizard.toReview()}
                disabled={!canReview}
              >
                Review Transfer
              </Button>
              <button className={styles.linkBtn} onClick={() => requestLeave('/transfer/internal')}>
                <ArrowSwap24Regular style={{ width: '18px', height: '18px' }} />
                Between your own accounts
              </button>
            </div>
          </>
        ) : step === 'pin' ? (
          <>
            {/* PIN — ADR-0041. The credential travels in THIS request; the API verifies it. */}
            <div className={styles.reviewCard}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Sending</Text>
                <Text className={styles.reviewValue}>{formatCurrency(amountNumber)}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>
                  {recipient?.displayName} (@{recipient?.azureTag})
                </Text>
              </div>
            </div>
            {/* Says what the missing button used to: with no Send control, the behaviour has to be
                discoverable BEFORE the last digit, not discovered by it. */}
            <Text style={{ textAlign: 'center' }}>
              Enter your 6-digit PIN. The transfer sends as soon as the last digit is in.
            </Text>
            <PinInput
              key={pinNonce}
              length={PIN_LENGTH}
              value={pin}
              onChange={(next) => {
                setPin(next);
                setPinError(false);
              }}
              /*
                The sixth digit sends (ADR-0042). Not a new pattern: `PinInput` documents
                `onComplete` as "an Enter-less submit affordance" and `StepUpModal` has wired it
                since PR-10 — this adopts what already shipped rather than inventing a second way
                to confirm. Review has already discharged WCAG 2.2 SC 3.3.4 (Level AA: "reviewing,
                confirming, and correcting"), so a second confirm buys nothing but a click.

                The completed value goes to the REF first: `onValid` runs before `pin` re-renders.
              */
              onComplete={(entered) => {
                enteredPin.current = entered;
                void handleSubmit(onValid, onInvalid)();
              }}
              // `isMinting` as well as `isSubmitting`: the submit has TWO phases now, and
              // `isSubmitting` only covers the second. Without this the boxes stay live for the
              // whole mint round trip — a window in which a second completion starts a second
              // mint AND a second send.
              disabled={isMinting || isSubmitting || pinLockDeadline !== null}
              error={pinError}
            />
            {pinLockDeadline !== null && (
              <>
                <MessageBar intent="error" role="alert">
                  <MessageBarBody>Too many incorrect PIN attempts.</MessageBarBody>
                </MessageBar>
                {/* Sibling, not child: role="alert" implies aria-atomic, so a nested timer would
                    re-announce the whole banner every second. It carries its own polite region. */}
                <RetryCountdown
                  deadline={pinLockDeadline}
                  onElapsed={() => setPinLockDeadline(null)}
                />
              </>
            )}
            {/* No Send button: the sixth digit is the send. A spinner still has to exist, or the
                one thing the user cannot see is the request they just started. */}
            {isSubmitting && (
              <div style={{ display: 'flex', justifyContent: 'center' }}>
                <Spinner size="tiny" label={`Sending ${formatCurrency(amountNumber)}`} />
              </div>
            )}
            <div className={styles.actions}>
              <Button
                appearance="secondary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => wizard.toReview()}
                disabled={exitLocked}
              >
                Back
              </Button>
            </div>
          </>
        ) : (
          <>
            {/* Review */}
            <div className={styles.reviewCard}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>From</Text>
                <Text className={styles.reviewValue}>{selectedAccount?.name}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>
                  {recipient?.displayName} (@{recipient?.azureTag})
                </Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Amount</Text>
                <Text className={styles.reviewValue}>{formatCurrency(amountNumber)}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>New balance</Text>
                <Text className={styles.reviewValue}>{formatCurrency(newBalance)}</Text>
              </div>
            </div>
            <Text className={styles.subtle} style={{ textAlign: 'center' }}>
              You&apos;ll confirm with your PIN on the next step.
            </Text>
            <div className={styles.actions}>
              <Button
                appearance="primary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => void onReviewed()}
                disabled={isSubmitting || checkingFunds}
              >
                {checkingFunds ? <Spinner size="tiny" /> : 'Continue'}
              </Button>
              <Button
                appearance="secondary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => wizard.toForm()}
                disabled={keyLive}
              >
                Back
              </Button>
            </div>
          </>
        )}
      </div>

      {/* The browser's Back, held. Every other exit refuses while a key is live; this one ASKS,
          because after a KEEP-class failure it is the only exit left (ADR-0028). */}
      <ConfirmDialog
        isOpen={wizard.exitPrompt !== null}
        onClose={() => wizard.exitPrompt?.stay()}
        onConfirm={() => wizard.exitPrompt?.leave()}
        title="Leave without finishing?"
        message="This transfer has not been confirmed. If it did reach the bank, leaving now means you will not see the result here — check your history before sending it again."
        confirmText="Leave anyway"
        cancelText="Stay on this page"
      />
    </div>
  );
}
