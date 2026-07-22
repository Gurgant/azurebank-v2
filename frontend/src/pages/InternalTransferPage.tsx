import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  makeStyles,
  Text,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import {
  ChevronLeft24Regular,
  Dismiss24Regular,
  CheckmarkCircle24Filled,
  Warning24Regular,
  ArrowSwap24Regular,
} from '@fluentui/react-icons';
import { colors, transitions } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import {
  useGetAccountsQuery,
  useTransferInternalMutation,
  type AccountResponse,
} from '../features/api/apiSlice';
import { useIdempotentMutation } from '../hooks/useIdempotentMutation';
import { formatCurrency, maskAccountNumber } from '../utils/format';

const QUICK_AMOUNTS = [10, 25, 50, 100, 250];
const MIN_AMOUNT = 0.01;
const MAX_AMOUNT = 100_000;

type Step = 'form' | 'review';

interface SuccessData {
  amount: number;
  fromName: string;
  toName: string;
  fromNewBalance: number;
  transactionNumber: string;
  replayed: boolean;
}

const useStyles = makeStyles({
  page: { minHeight: '100vh', backgroundColor: colors.neutral[50] },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    height: '56px',
    padding: '0 12px',
    backgroundColor: '#FFFFFF',
    borderBottom: `1px solid ${colors.neutral[200]}`,
  },
  headerBtn: {
    width: '40px',
    height: '40px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    borderRadius: '8px',
    color: colors.neutral[800],
    ':hover': { backgroundColor: colors.neutral[100] },
    ':disabled': { opacity: 0.4, cursor: 'not-allowed' },
  },
  headerTitle: { fontSize: '16px', fontWeight: 600, color: colors.neutral[800] },
  body: {
    maxWidth: '480px',
    margin: '0 auto',
    padding: '20px 16px 32px 16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
  },
  sectionLabel: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
    marginBottom: '8px',
  },
  card: {
    width: '100%',
    padding: '14px 16px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    cursor: 'pointer',
    marginBottom: '8px',
    transition: `all ${transitions.fast}`,
    ':hover': { backgroundColor: colors.neutral[50] },
    ':disabled': { opacity: 0.45, cursor: 'not-allowed' },
  },
  cardSelected: { border: `2px solid ${colors.brand[60]}`, backgroundColor: colors.brand[130] },
  accountInfo: { display: 'flex', flexDirection: 'column', gap: '2px', textAlign: 'left' },
  accountName: { fontSize: '15px', fontWeight: 500, color: colors.neutral[800] },
  accountNumber: {
    fontSize: '13px',
    fontFamily: 'Consolas, monospace',
    color: colors.neutral[500],
  },
  accountBalance: { fontSize: '15px', fontWeight: 600, color: colors.neutral[800] },
  amountSection: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '16px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '12px',
  },
  amountWrapper: { display: 'flex', alignItems: 'baseline', gap: '4px' },
  amountCurrency: { fontSize: '30px', fontWeight: 300, color: colors.neutral[800] },
  amountInput: {
    fontSize: '44px',
    fontWeight: 700,
    color: colors.neutral[800],
    border: 'none',
    outline: 'none',
    background: 'transparent',
    textAlign: 'center',
    width: '170px',
    '::placeholder': { color: colors.neutral[300] },
  },
  hint: { fontSize: '13px', fontWeight: 500, color: colors.semantic.error.main },
  subtle: { fontSize: '13px', color: colors.neutral[500] },
  quickAmounts: { display: 'flex', gap: '8px', flexWrap: 'wrap', justifyContent: 'center' },
  quickBtn: {
    minWidth: '60px',
    height: '34px',
    padding: '0 14px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '8px',
    cursor: 'pointer',
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
    ':hover': { backgroundColor: colors.neutral[50] },
    ':disabled': { opacity: 0.5, cursor: 'not-allowed' },
  },
  quickBtnSelected: {
    backgroundColor: colors.brand[120],
    border: `1px solid ${colors.brand[60]}`,
    color: colors.brand[60],
  },
  reviewCard: {
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '12px',
    padding: '4px 16px',
  },
  reviewRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    padding: '14px 0',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    ':last-child': { borderBottom: 'none' },
  },
  reviewLabel: { fontSize: '14px', color: colors.neutral[500] },
  reviewValue: { fontSize: '14px', fontWeight: 600, color: colors.neutral[800] },
  actions: { display: 'flex', flexDirection: 'column', gap: '12px', marginTop: '4px' },
  linkBtn: {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '6px',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    color: colors.brand[60],
    fontSize: '14px',
    fontWeight: 500,
    padding: '8px',
  },
  centeredView: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '16px',
    padding: '40px 20px',
    textAlign: 'center',
  },
  successIcon: {
    width: '80px',
    height: '80px',
    backgroundColor: colors.semantic.success.light,
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.semantic.success.main,
  },
  warningIcon: {
    width: '80px',
    height: '80px',
    backgroundColor: '#FEF3E2',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: '#B45309',
  },
  successTitle: { fontSize: '24px', fontWeight: 700, color: colors.semantic.success.main },
  stateTitle: { fontSize: '20px', fontWeight: 700, color: colors.neutral[800] },
  stateBody: { fontSize: '15px', color: colors.neutral[500], lineHeight: '1.5' },
  successAmount: { fontSize: '32px', fontWeight: 700, color: colors.neutral[800] },
});

/**
 * PR-11b — move money between the caller's OWN accounts. Rides the same step-up interceptor
 * as the external transfer (level-2 gated → the root StepUpModal pops on Send, invisible to
 * this page) and the same idempotency spine + keyLive money-safety guards. The difference is
 * two account pickers (source + destination, which can't be the same) instead of a recipient
 * lookup.
 */
export function InternalTransferPage() {
  const styles = useStyles();
  const navigate = useNavigate();

  const { data: accounts = [], isLoading: accountsLoading } = useGetAccountsQuery();
  const [transferTrigger] = useTransferInternalMutation();
  const { submit, resetIntent, verifyRequired, keyRetained } =
    useIdempotentMutation(transferTrigger);

  const [step, setStep] = useState<Step>('form');
  const [fromId, setFromId] = useState<string | null>(null);
  const [toId, setToId] = useState<string | null>(null);
  const [amount, setAmount] = useState(0);
  const [amountInput, setAmountInput] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [inFlight, setInFlight] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<SuccessData | null>(null);

  const fromAccount =
    accounts.find((a) => a.id === fromId) ??
    accounts.find((a) => a.isPrimary) ??
    accounts[0] ??
    null;
  const toAccount = accounts.find((a) => a.id === toId) ?? null;
  const availableBalance = fromAccount?.balance ?? 0;
  const keyLive = isSubmitting || keyRetained;

  useEffect(() => {
    if (!keyLive) return;
    const warn = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [keyLive]);

  // Blocked while a key is LIVE (submitting OR retained after IN_FLIGHT/NETWORK/5xx): nulling
  // a retained key then resending mints a fresh key = a double-spend.
  const onBodyEdit = () => {
    if (keyLive) return;
    resetIntent();
    setInFlight(false);
    setError(null);
  };

  const selectFrom = (account: AccountResponse) => {
    if (keyLive) return;
    setFromId(account.id);
    if (account.id === toId) setToId(null); // can't send to the same account
    onBodyEdit();
  };

  const selectTo = (account: AccountResponse) => {
    if (keyLive) return;
    if (account.id === fromAccount?.id) return; // same-account is not selectable
    setToId(account.id);
    onBodyEdit();
  };

  const handleAmountChange = (value: string) => {
    if (keyLive) return;
    const digitsAndDots = value.replace(/[^0-9.]/g, '');
    const firstDot = digitsAndDots.indexOf('.');
    const singleDot =
      firstDot === -1
        ? digitsAndDots
        : digitsAndDots.slice(0, firstDot + 1) +
          digitsAndDots.slice(firstDot + 1).replace(/\./g, '');
    const dot = singleDot.indexOf('.');
    const cleaned = dot === -1 ? singleDot : singleDot.slice(0, dot + 3);
    setAmountInput(cleaned);
    setAmount(parseFloat(cleaned) || 0);
    onBodyEdit();
  };

  const isAmountValid = amount >= MIN_AMOUNT && amount <= MAX_AMOUNT && amount <= availableBalance;
  const canReview =
    !!fromAccount && !!toAccount && fromAccount.id !== toAccount.id && isAmountValid;
  const fromNewBalance = availableBalance - amount;

  const amountHint =
    amount > 0 && !isAmountValid
      ? amount > availableBalance
        ? `Exceeds available balance of ${formatCurrency(availableBalance)}.`
        : amount > MAX_AMOUNT
          ? 'Maximum transfer is €100,000.'
          : 'Minimum transfer is €0.01.'
      : null;

  const handleSubmit = async () => {
    // Mirror canReview exactly (incl. from !== to) — belt-and-suspenders against a fromAccount
    // fallback converging on toAccount if the accounts list ever changed under the review step.
    if (!fromAccount || !toAccount || fromAccount.id === toAccount.id || !isAmountValid) return;
    setError(null);
    setInFlight(false);
    setIsSubmitting(true);
    try {
      const result = await submit({
        fromAccountId: fromAccount.id,
        toAccountId: toAccount.id,
        amount,
      });
      setSuccess({
        amount,
        fromName: fromAccount.name,
        toName: toAccount.name,
        fromNewBalance: result.fromAccountNewBalance,
        transactionNumber: result.transactionNumber,
        replayed: result.replayed,
      });
    } catch (caught) {
      const problem = caught as ApiProblem;
      if (problem.errorCode === 'IDEMPOTENCY_RESULT_UNKNOWN') {
        // hook latched verifyRequired; the verify view renders.
      } else if (problem.errorCode === 'STEP_UP_CANCELLED') {
        // The user dismissed the PIN modal — benign. Stay on review; Send re-triggers it.
      } else if (problem.errorCode === 'STEP_UP_REQUIRED') {
        setError("Verification didn't complete. Please tap Send and try again.");
      } else if (problem.errorCode === 'IDEMPOTENCY_IN_FLIGHT') {
        setInFlight(true);
      } else if (problem.errorCode === 'SAME_ACCOUNT_TRANSFER') {
        setError('Choose two different accounts.');
      } else if (problem.errorCode === 'ACCOUNT_NOT_FOUND') {
        setError('One of the accounts could not be found. Please re-check.');
      } else if (problem.errorCode === 'INSUFFICIENT_FUNDS') {
        setError('Insufficient funds for this transfer.');
      } else if (problem.errorCode === 'VALIDATION_ERROR') {
        const firstFieldError = Object.values(problem.errors ?? {})[0]?.[0];
        setError(firstFieldError ?? 'Please check the details and try again.');
      } else if (
        problem.errorCode === 'IDEMPOTENCY_KEY_REUSE' ||
        problem.errorCode === 'IDEMPOTENCY_KEY_MISSING' ||
        problem.errorCode === 'IDEMPOTENCY_KEY_INVALID'
      ) {
        setError('Something went wrong. Please try again.');
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        setError("Couldn't reach the server — check your connection and try again.");
      } else {
        setError(problem.detail || 'Transfer failed. Please try again.');
      }
    } finally {
      setIsSubmitting(false);
    }
  };

  const requestLeave = (to: string) => {
    if (!keyLive) navigate(to);
  };

  if (success) {
    return (
      <div className={styles.page}>
        <div className={styles.header}>
          <span style={{ width: '40px' }} />
          <Text className={styles.headerTitle}>Transfer Complete</Text>
          <span style={{ width: '40px' }} />
        </div>
        <div className={styles.body}>
          <div className={styles.centeredView}>
            <div className={styles.successIcon}>
              <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
            </div>
            <Text className={styles.successTitle}>Transfer Complete!</Text>
            <Text className={styles.successAmount}>{formatCurrency(success.amount)}</Text>
            {success.replayed && (
              <MessageBar intent="info">
                <MessageBarBody>
                  This transfer was already processed — showing the existing result.
                </MessageBarBody>
              </MessageBar>
            )}
            <div className={styles.reviewCard} style={{ width: '100%' }}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>From</Text>
                <Text className={styles.reviewValue}>{success.fromName}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>{success.toName}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Reference</Text>
                <Text className={styles.reviewValue}>{success.transactionNumber}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>{success.fromName} balance</Text>
                <Text className={styles.reviewValue}>{formatCurrency(success.fromNewBalance)}</Text>
              </div>
            </div>
          </div>
          <div className={styles.actions}>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => navigate('/dashboard')}
            >
              Done
            </Button>
          </div>
        </div>
      </div>
    );
  }

  if (verifyRequired) {
    return (
      <div className={styles.page}>
        <div className={styles.header}>
          <span style={{ width: '40px' }} />
          <Text className={styles.headerTitle}>Move Money</Text>
          <span style={{ width: '40px' }} />
        </div>
        <div className={styles.body}>
          <div className={styles.centeredView}>
            <div className={styles.warningIcon}>
              <Warning24Regular style={{ width: '40px', height: '40px' }} />
            </div>
            <Text className={styles.stateTitle}>We couldn&apos;t confirm your transfer</Text>
            <Text className={styles.stateBody}>
              The request may or may not have gone through. Check your recent transactions before
              trying again — retrying blindly could move the money twice.
            </Text>
          </div>
          <div className={styles.actions}>
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => navigate('/history')}
            >
              Check recent transactions
            </Button>
            <Button
              appearance="secondary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={() => {
                resetIntent();
                setStep('form');
              }}
            >
              It didn&apos;t go through — start over
            </Button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <button
          className={styles.headerBtn}
          aria-label="Back"
          disabled={keyLive}
          onClick={() => (step === 'review' ? setStep('form') : requestLeave('/dashboard'))}
        >
          <ChevronLeft24Regular />
        </button>
        <Text className={styles.headerTitle}>
          {step === 'review' ? 'Review Transfer' : 'Move Money'}
        </Text>
        <button
          className={styles.headerBtn}
          aria-label="Close"
          disabled={keyLive}
          onClick={() => requestLeave('/dashboard')}
        >
          <Dismiss24Regular />
        </button>
      </div>

      <div className={styles.body}>
        {error && (
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
        {inFlight && (
          <MessageBar intent="info">
            <MessageBarBody>Still processing — tap Send again to check.</MessageBarBody>
          </MessageBar>
        )}

        {step === 'form' ? (
          <>
            <div>
              <Text className={styles.sectionLabel}>From</Text>
              {accounts.map((account) => (
                <button
                  key={account.id}
                  className={`${styles.card} ${fromAccount?.id === account.id ? styles.cardSelected : ''}`}
                  aria-label={`From ${account.name}`}
                  aria-pressed={fromAccount?.id === account.id}
                  onClick={() => selectFrom(account)}
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

            <div>
              <Text className={styles.sectionLabel}>To</Text>
              {!accountsLoading && accounts.length < 2 && (
                <Text className={styles.subtle} style={{ display: 'block', marginBottom: '8px' }}>
                  You need a second account to transfer between your own accounts.
                </Text>
              )}
              {accounts.map((account) => {
                const isFrom = account.id === fromAccount?.id;
                return (
                  <button
                    key={account.id}
                    className={`${styles.card} ${toAccount?.id === account.id ? styles.cardSelected : ''}`}
                    aria-label={`To ${account.name}`}
                    aria-pressed={toAccount?.id === account.id}
                    onClick={() => selectTo(account)}
                    disabled={isFrom}
                  >
                    <div className={styles.accountInfo}>
                      <Text className={styles.accountName}>{account.name}</Text>
                      <Text className={styles.accountNumber}>
                        {isFrom ? 'Source account' : maskAccountNumber(account.accountNumber)}
                      </Text>
                    </div>
                    <Text className={styles.accountBalance}>{formatCurrency(account.balance)}</Text>
                  </button>
                );
              })}
            </div>

            <div className={styles.amountSection}>
              <Text className={styles.subtle}>Amount</Text>
              <div className={styles.amountWrapper}>
                <span className={styles.amountCurrency}>€</span>
                <input
                  type="text"
                  inputMode="decimal"
                  placeholder="0"
                  aria-label="Transfer amount"
                  className={styles.amountInput}
                  value={amountInput}
                  onChange={(e) => handleAmountChange(e.target.value)}
                />
              </div>
              <Text className={styles.subtle}>Available: {formatCurrency(availableBalance)}</Text>
              {amountHint && (
                <Text role="alert" className={styles.hint}>
                  {amountHint}
                </Text>
              )}
            </div>
            <div className={styles.quickAmounts}>
              {QUICK_AMOUNTS.map((quickAmount) => (
                <button
                  key={quickAmount}
                  className={`${styles.quickBtn} ${amount === quickAmount ? styles.quickBtnSelected : ''}`}
                  onClick={() => handleAmountChange(String(quickAmount))}
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
                onClick={() => setStep('review')}
                disabled={!canReview}
              >
                Review Transfer
              </Button>
              <button className={styles.linkBtn} onClick={() => requestLeave('/transfer')}>
                <ArrowSwap24Regular style={{ width: '18px', height: '18px' }} />
                Send to someone else
              </button>
            </div>
          </>
        ) : (
          <>
            <div className={styles.reviewCard}>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>From</Text>
                <Text className={styles.reviewValue}>{fromAccount?.name}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>To</Text>
                <Text className={styles.reviewValue}>{toAccount?.name}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>Amount</Text>
                <Text className={styles.reviewValue}>{formatCurrency(amount)}</Text>
              </div>
              <div className={styles.reviewRow}>
                <Text className={styles.reviewLabel}>{fromAccount?.name} balance</Text>
                <Text className={styles.reviewValue}>{formatCurrency(fromNewBalance)}</Text>
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
                onClick={handleSubmit}
                disabled={isSubmitting}
              >
                {isSubmitting ? <Spinner size="tiny" /> : `Send ${formatCurrency(amount)}`}
              </Button>
              <Button
                appearance="secondary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => setStep('form')}
                disabled={keyLive}
              >
                Back
              </Button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

export default InternalTransferPage;
