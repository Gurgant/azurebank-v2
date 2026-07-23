import { useState } from 'react';
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
  Dismiss24Regular,
  ArrowDownload24Regular,
  CheckmarkCircle24Filled,
  Warning24Regular,
} from '@fluentui/react-icons';
import { colors, transitions } from '../../theme/tokens';
import type { ApiProblem } from '../../api/problemBaseQuery';
import { useDepositMutation } from '../../features/api/apiSlice';
import { useIdempotentMutation } from '../../hooks/useIdempotentMutation';
import { formatCurrency } from '../../utils/format';
import { amountIsValid } from '../../utils/amountSchema';

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

const useStyles = makeStyles({
  overlay: {
    position: 'fixed',
    inset: 0,
    backgroundColor: 'rgba(0, 0, 0, 0.5)',
    display: 'flex',
    alignItems: 'flex-end',
    justifyContent: 'center',
    zIndex: 1000,
    '@media (min-width: 768px)': {
      alignItems: 'center',
    },
  },

  dialog: {
    width: '100%',
    maxHeight: '90vh',
    backgroundColor: '#FFFFFF',
    borderRadius: '24px 24px 0 0',
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
    '@media (min-width: 768px)': {
      maxWidth: '480px',
      borderRadius: '16px',
    },
  },

  // ========== HEADER ==========
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '20px 20px 16px 20px',
    borderBottom: `1px solid ${colors.neutral[200]}`,
  },

  headerTitle: {
    fontSize: '20px',
    fontWeight: 600,
    color: colors.neutral[800],
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },

  headerIcon: {
    width: '32px',
    height: '32px',
    borderRadius: '8px',
    backgroundColor: colors.semantic.success.light,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.semantic.success.main,
  },

  closeButton: {
    width: '40px',
    height: '40px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    borderRadius: '8px',
    color: colors.neutral[500],
    ':hover': {
      backgroundColor: colors.neutral[100],
    },
  },

  // ========== CONTENT ==========
  content: {
    flex: 1,
    padding: '24px 20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
    overflowY: 'auto',
  },

  // ========== ACCOUNT SELECTOR ==========
  sectionLabel: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
    marginBottom: '8px',
  },

  accountCard: {
    width: '100%',
    padding: '16px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  accountCardSelected: {
    border: `2px solid ${colors.brand[60]}`,
    backgroundColor: colors.brand[130],
  },

  accountInfo: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },

  accountName: {
    fontSize: '16px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  accountNumber: {
    fontSize: '14px',
    fontFamily: 'Consolas, monospace',
    color: colors.neutral[500],
  },

  accountBalance: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  // ========== AMOUNT INPUT ==========
  amountSection: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '16px',
    backgroundColor: colors.neutral[50],
    borderRadius: '12px',
  },

  amountLabel: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  amountInputWrapper: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '4px',
  },

  amountCurrency: {
    fontSize: '32px',
    fontWeight: 300,
    color: colors.neutral[800],
  },

  amountInput: {
    fontSize: '48px',
    fontWeight: 700,
    color: colors.neutral[800],
    border: 'none',
    outline: 'none',
    background: 'transparent',
    textAlign: 'center',
    width: '180px',
    '::placeholder': {
      color: colors.neutral[300],
    },
  },

  newBalance: {
    fontSize: '13px',
    color: colors.neutral[500],
  },

  amountHint: {
    fontSize: '13px',
    fontWeight: 500,
    color: colors.semantic.error.main,
  },

  // ========== QUICK AMOUNTS ==========
  quickAmounts: {
    display: 'flex',
    gap: '8px',
    flexWrap: 'wrap',
    justifyContent: 'center',
  },

  quickBtn: {
    minWidth: '70px',
    height: '36px',
    padding: '0 16px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '8px',
    cursor: 'pointer',
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  quickBtnSelected: {
    backgroundColor: colors.brand[120],
    border: `1px solid ${colors.brand[60]}`,
    color: colors.brand[60],
  },

  // ========== DESCRIPTION ==========
  descriptionInput: {
    width: '100%',
    padding: '12px',
    borderRadius: '8px',
    border: `1px solid ${colors.neutral[200]}`,
    fontSize: '14px',
    fontFamily: 'inherit',
    color: colors.neutral[800],
    outline: 'none',
    ':focus': {
      border: `1px solid ${colors.brand[60]}`,
    },
  },

  // ========== SUCCESS / STATE VIEWS ==========
  centeredView: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '16px',
    padding: '32px 20px',
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

  successTitle: {
    fontSize: '24px',
    fontWeight: 700,
    color: colors.semantic.success.main,
  },

  stateTitle: {
    fontSize: '20px',
    fontWeight: 700,
    color: colors.neutral[800],
  },

  successAmount: {
    fontSize: '32px',
    fontWeight: 700,
    color: colors.neutral[800],
  },

  stateBody: {
    fontSize: '15px',
    color: colors.neutral[500],
    lineHeight: '1.5',
  },

  detailsCard: {
    width: '100%',
    backgroundColor: colors.neutral[50],
    borderRadius: '12px',
    padding: '4px 16px',
    marginTop: '8px',
  },

  detailRow: {
    display: 'flex',
    justifyContent: 'space-between',
    padding: '12px 0',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    ':last-child': {
      borderBottom: 'none',
    },
  },

  detailLabel: { fontSize: '14px', color: colors.neutral[500] },
  detailValue: { fontSize: '14px', fontWeight: 600, color: colors.neutral[800] },

  // ========== FOOTER ==========
  footer: {
    padding: '16px 20px 24px 20px',
    borderTop: `1px solid ${colors.neutral[200]}`,
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },

  errorMessage: {
    marginBottom: '4px',
  },
});

// ============================================
// CONSTANTS
// ============================================

const QUICK_AMOUNTS = [50, 100, 200, 500];
const MIN_AMOUNT = 0.01;
const MAX_AMOUNT = 1_000_000;

// ============================================
// COMPONENT
// ============================================

/**
 * T3 — the first production idempotent mutation (PR-9). The deposit rides
 * useIdempotentMutation: a lazy in-memory Idempotency-Key that survives a KEEP error
 * (IN_FLIGHT / network / 5xx) so the user's Retry re-sends the SAME key + body, and
 * rotates on any body edit (an edited body with the old key is a 422 KEY_REUSE). A
 * replayed 2xx surfaces a polite note (D4); RESULT_UNKNOWN latches a verify-first flow
 * (§2.3). No step-up — deposit is auth level 1.
 */
export function DepositDialog({ isOpen, onClose, accounts, onSuccess }: DepositDialogProps) {
  const styles = useStyles();
  const navigate = useNavigate();

  const [depositTrigger] = useDepositMutation();
  const { submit, resetIntent, verifyRequired, keyRetained } =
    useIdempotentMutation(depositTrigger);

  const [selectedAccount, setSelectedAccount] = useState<Account | null>(
    accounts.length > 0 ? accounts[0] : null,
  );
  const [amount, setAmount] = useState(0);
  const [amountInput, setAmountInput] = useState('');
  const [description, setDescription] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [inFlight, setInFlight] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<SuccessData | null>(null);

  // Any body-affecting edit rotates the key: the old key + a new body is a raw-byte
  // fingerprint mismatch → 422 KEY_REUSE. Also clears transient in-flight/error state.
  // Blocked while a request is in flight: rotating/nulling the key out from under a pending
  // submit would defeat the retained-key guard (a subsequent NETWORK/5xx/IN_FLIGHT could
  // then close/resubmit into a NEW intent while the original still settles).
  const onBodyEdit = () => {
    if (isSubmitting) return;
    resetIntent();
    setInFlight(false);
    setError(null);
  };

  const handleAmountChange = (value: string) => {
    // Strip non-numerics AND collapse to a single decimal point — "1.2.3" would
    // desync the shown field from the parsed amount otherwise.
    const digitsAndDots = value.replace(/[^0-9.]/g, '');
    const firstDot = digitsAndDots.indexOf('.');
    const cleaned =
      firstDot === -1
        ? digitsAndDots
        : digitsAndDots.slice(0, firstDot + 1) +
          digitsAndDots.slice(firstDot + 1).replace(/\./g, '');
    setAmountInput(cleaned);
    setAmount(parseFloat(cleaned) || 0);
    onBodyEdit();
  };

  const handleQuickAmount = (value: number) => {
    setAmountInput(value.toString());
    setAmount(value);
    onBodyEdit();
  };

  const handleSelectAccount = (account: Account) => {
    if (isSubmitting) return; // a div can't be `disabled` — guard the mid-flight edit here
    setSelectedAccount(account);
    onBodyEdit();
  };

  const isAmountValid = amountIsValid(amount, { min: MIN_AMOUNT, max: MAX_AMOUNT });
  const newBalance = selectedAccount ? selectedAccount.balance + amount : 0;

  // Shown inline under the amount once the user has typed something invalid (the CTA
  // is also disabled) — a silently-disabled button leaves an over-limit amount
  // unexplained.
  const amountHint =
    amount > 0 && !isAmountValid
      ? amount > MAX_AMOUNT
        ? 'Maximum deposit is €1,000,000.'
        : 'Minimum deposit is €0.01.'
      : null;

  const handleSubmit = async () => {
    if (!selectedAccount || !isAmountValid) {
      return;
    }
    setError(null);
    setInFlight(false);
    setIsSubmitting(true);
    try {
      const result = await submit({
        accountId: selectedAccount.id,
        amount,
        description: description.trim() || undefined,
      });
      setSuccess({
        amount,
        accountName: selectedAccount.name,
        newBalance: result.newBalance,
        transactionId: result.transaction.id,
        replayed: result.replayed,
      });
      onSuccess?.();
    } catch (caught) {
      const problem = caught as ApiProblem;
      // D17 / §2.3: route on errorCode, never a blanket toast. RESULT_UNKNOWN is
      // handled by the hook (latches verifyRequired) — we just render that view.
      if (problem.errorCode === 'IDEMPOTENCY_RESULT_UNKNOWN') {
        // hook set verifyRequired; the verify view renders below.
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
        // Client protocol bug — never surface the raw code (D17).
        setError('Something went wrong. Please try again.');
      } else if (problem.status === 'NETWORK' || problem.status === 'PARSE') {
        // Transport failure — a raw "TypeError: Failed to fetch" would leak (D17). The
        // key is KEPT (shouldKeepKey) so tapping Deposit again is a safe same-key retry.
        setError("Couldn't reach the server — check your connection and try again.");
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
  // one so the same amount becomes a NEW intent = a real double-deposit.
  const keyLive = isSubmitting || keyRetained;
  const requestClose = () => {
    if (!keyLive) {
      onClose();
    }
  };

  return (
    <div className={styles.overlay} onClick={requestClose}>
      <div className={styles.dialog} onClick={(e) => e.stopPropagation()}>
        {/* Header */}
        <div className={styles.header}>
          <div className={styles.headerTitle}>
            <div className={styles.headerIcon}>
              <ArrowDownload24Regular />
            </div>
            {success ? 'Deposit Complete' : 'Deposit Money'}
          </div>
          <button
            className={styles.closeButton}
            aria-label="Close"
            onClick={requestClose}
            disabled={keyLive}
          >
            <Dismiss24Regular />
          </button>
        </div>

        {/* Success */}
        {success && (
          <div className={styles.centeredView}>
            <div className={styles.successIcon}>
              <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
            </div>
            <Text className={styles.successTitle}>Deposit Successful!</Text>
            <Text className={styles.successAmount}>+{formatCurrency(success.amount)}</Text>
            {success.replayed && (
              <MessageBar intent="info">
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

        {/* RESULT_UNKNOWN — verify-first (§2.3) */}
        {!success && verifyRequired && (
          <div className={styles.centeredView}>
            <div className={styles.warningIcon}>
              <Warning24Regular style={{ width: '40px', height: '40px' }} />
            </div>
            <Text className={styles.stateTitle}>We couldn&apos;t confirm your deposit</Text>
            <Text className={styles.stateBody}>
              The request may or may not have gone through. Check your recent transactions before
              trying again — retrying blindly could deposit twice.
            </Text>
          </div>
        )}

        {/* Form */}
        {showForm && (
          <div className={styles.content}>
            <div>
              <Text className={styles.sectionLabel}>Select Account</Text>
              {accounts.map((account) => (
                <div
                  key={account.id}
                  className={`${styles.accountCard} ${
                    selectedAccount?.id === account.id ? styles.accountCardSelected : ''
                  }`}
                  onClick={() => handleSelectAccount(account)}
                  style={{ marginBottom: '8px' }}
                >
                  <div className={styles.accountInfo}>
                    <Text className={styles.accountName}>{account.name}</Text>
                    <Text className={styles.accountNumber}>{account.accountNumber}</Text>
                  </div>
                  <Text className={styles.accountBalance}>{formatCurrency(account.balance)}</Text>
                </div>
              ))}
            </div>

            <div className={styles.amountSection}>
              <Text className={styles.amountLabel}>Enter amount</Text>
              <div className={styles.amountInputWrapper}>
                <span className={styles.amountCurrency}>€</span>
                <input
                  type="text"
                  inputMode="decimal"
                  placeholder="0"
                  aria-label="Deposit amount"
                  className={styles.amountInput}
                  value={amountInput}
                  disabled={isSubmitting}
                  onChange={(e) => handleAmountChange(e.target.value)}
                />
              </div>
              {selectedAccount && isAmountValid && (
                <Text className={styles.newBalance}>New balance: {formatCurrency(newBalance)}</Text>
              )}
              {amountHint && (
                <Text role="alert" className={styles.amountHint}>
                  {amountHint}
                </Text>
              )}
            </div>

            <div className={styles.quickAmounts}>
              {QUICK_AMOUNTS.map((quickAmount) => (
                <button
                  key={quickAmount}
                  className={`${styles.quickBtn} ${
                    amount === quickAmount ? styles.quickBtnSelected : ''
                  }`}
                  onClick={() => handleQuickAmount(quickAmount)}
                  disabled={isSubmitting}
                >
                  €{quickAmount}
                </button>
              ))}
            </div>

            <input
              type="text"
              placeholder="Description (optional)"
              aria-label="Description"
              maxLength={100}
              className={styles.descriptionInput}
              value={description}
              disabled={isSubmitting}
              onChange={(e) => {
                setDescription(e.target.value);
                onBodyEdit();
              }}
            />
          </div>
        )}

        {/* Footer */}
        <div className={styles.footer}>
          {error && (
            <MessageBar intent="error" className={styles.errorMessage}>
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}
          {inFlight && (
            <MessageBar intent="info" className={styles.errorMessage}>
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
          ) : verifyRequired ? (
            <>
              <Button
                appearance="primary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={() => void navigate('/history')}
              >
                Check recent transactions
              </Button>
              <Button
                appearance="secondary"
                size="large"
                style={{ width: '100%', height: '48px' }}
                onClick={resetIntent}
              >
                It didn&apos;t go through — try again
              </Button>
            </>
          ) : (
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={handleSubmit}
              disabled={isSubmitting || !isAmountValid}
            >
              {isSubmitting ? (
                <Spinner size="tiny" />
              ) : (
                `Deposit ${amount > 0 ? formatCurrency(amount) : ''}`
              )}
            </Button>
          )}
        </div>
      </div>
    </div>
  );
}

export default DepositDialog;
