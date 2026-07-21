import { useState } from 'react';
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
} from '@fluentui/react-icons';
import { colors, transitions } from '../../theme/tokens';

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

  // ========== SUCCESS STATE ==========
  successContent: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '16px',
    padding: '32px 20px',
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

  successTitle: {
    fontSize: '24px',
    fontWeight: 700,
    color: colors.semantic.success.main,
    textAlign: 'center',
  },

  successAmount: {
    fontSize: '32px',
    fontWeight: 700,
    color: colors.neutral[800],
  },

  successSubtitle: {
    fontSize: '16px',
    fontWeight: 400,
    color: colors.neutral[500],
    textAlign: 'center',
  },

  // ========== FOOTER ==========
  footer: {
    padding: '16px 20px 24px 20px',
    borderTop: `1px solid ${colors.neutral[200]}`,
  },

  errorMessage: {
    marginBottom: '16px',
  },
});

// ============================================
// QUICK AMOUNTS
// ============================================

const quickAmounts = [50, 100, 250, 500, 1000];

// ============================================
// HELPER FUNCTIONS
// ============================================

function formatCurrency(amount: number): string {
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
  }).format(amount);
}

// ============================================
// COMPONENT
// ============================================

export function DepositDialog({ isOpen, onClose, accounts, onSuccess }: DepositDialogProps) {
  const styles = useStyles();

  const [selectedAccount, setSelectedAccount] = useState<Account | null>(
    accounts.length > 0 ? accounts[0] : null,
  );
  const [amount, setAmount] = useState(0);
  const [amountInput, setAmountInput] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [isSuccess, setIsSuccess] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleAmountChange = (value: string) => {
    const cleaned = value.replace(/[^0-9.]/g, '');
    setAmountInput(cleaned);
    setAmount(parseFloat(cleaned) || 0);
  };

  const handleQuickAmount = (value: number) => {
    setAmountInput(value.toString());
    setAmount(value);
  };

  const handleSubmit = async () => {
    if (!selectedAccount || amount <= 0) {
      setError('Please select an account and enter an amount');
      return;
    }

    setError(null);
    setIsLoading(true);

    try {
      // TODO: Replace with actual API call
      await new Promise((resolve) => setTimeout(resolve, 1500));

      setIsSuccess(true);
      onSuccess?.();
    } catch {
      setError('Deposit failed. Please try again.');
    } finally {
      setIsLoading(false);
    }
  };

  const handleClose = () => {
    // Reset state
    setSelectedAccount(accounts.length > 0 ? accounts[0] : null);
    setAmount(0);
    setAmountInput('');
    setIsLoading(false);
    setIsSuccess(false);
    setError(null);
    onClose();
  };

  if (!isOpen) return null;

  return (
    <div className={styles.overlay} onClick={handleClose}>
      <div className={styles.dialog} onClick={(e) => e.stopPropagation()}>
        {/* Header */}
        <div className={styles.header}>
          <div className={styles.headerTitle}>
            <div className={styles.headerIcon}>
              <ArrowDownload24Regular />
            </div>
            {isSuccess ? 'Deposit Complete' : 'Deposit Money'}
          </div>
          <button className={styles.closeButton} aria-label="Close" onClick={handleClose}>
            <Dismiss24Regular />
          </button>
        </div>

        {/* Content */}
        {isSuccess ? (
          <div className={styles.successContent}>
            <div className={styles.successIcon}>
              <CheckmarkCircle24Filled style={{ width: '48px', height: '48px' }} />
            </div>
            <Text className={styles.successTitle}>Deposit Successful!</Text>
            <Text className={styles.successAmount}>{formatCurrency(amount)}</Text>
            <Text className={styles.successSubtitle}>Deposited to {selectedAccount?.name}</Text>
          </div>
        ) : (
          <div className={styles.content}>
            {/* Account Selection */}
            <div>
              <Text className={styles.sectionLabel}>Select Account</Text>
              {accounts.map((account) => (
                <div
                  key={account.id}
                  className={`${styles.accountCard} ${
                    selectedAccount?.id === account.id ? styles.accountCardSelected : ''
                  }`}
                  onClick={() => setSelectedAccount(account)}
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

            {/* Amount Input */}
            <div className={styles.amountSection}>
              <Text className={styles.amountLabel}>Enter amount</Text>
              <div className={styles.amountInputWrapper}>
                <span className={styles.amountCurrency}>$</span>
                <input
                  type="text"
                  inputMode="decimal"
                  placeholder="0"
                  className={styles.amountInput}
                  value={amountInput}
                  onChange={(e) => handleAmountChange(e.target.value)}
                />
              </div>
            </div>

            {/* Quick Amounts */}
            <div className={styles.quickAmounts}>
              {quickAmounts.map((quickAmount) => (
                <button
                  key={quickAmount}
                  className={`${styles.quickBtn} ${
                    amount === quickAmount ? styles.quickBtnSelected : ''
                  }`}
                  onClick={() => handleQuickAmount(quickAmount)}
                >
                  ${quickAmount}
                </button>
              ))}
            </div>
          </div>
        )}

        {/* Footer */}
        <div className={styles.footer}>
          {error && (
            <MessageBar intent="error" className={styles.errorMessage}>
              <MessageBarBody>{error}</MessageBarBody>
            </MessageBar>
          )}

          {isSuccess ? (
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={handleClose}
            >
              Done
            </Button>
          ) : (
            <Button
              appearance="primary"
              size="large"
              style={{ width: '100%', height: '48px' }}
              onClick={handleSubmit}
              disabled={isLoading || amount <= 0}
            >
              {isLoading ? (
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
