import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  makeStyles,
  Text,
  Button,
  Spinner,
} from '@fluentui/react-components';
import {
  ChevronLeft24Regular,
  Dismiss24Regular,
  Search24Regular,
  Add24Regular,
  Checkmark24Filled,
  ArrowRight24Regular,
  Info24Regular,
  Share24Regular,
  CheckmarkCircle24Filled,
} from '@fluentui/react-icons';
import { colors, shadows, transitions } from '../theme/tokens';
import { Avatar } from '../components/shared/Avatar';

// ============================================
// TYPES
// ============================================

interface Account {
  id: string;
  name: string;
  accountNumber: string;
  balance: number;
}

interface Recipient {
  id: string;
  name: string;
  accountNumber: string;
  initials: string;
}

interface TransferData {
  fromAccount: Account | null;
  toRecipient: Recipient | null;
  amount: number;
}

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100vh',
    backgroundColor: '#F7F8FA',
  },

  // ========== HEADER ==========
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    height: '56px',
    padding: '0 16px',
    backgroundColor: '#FFFFFF',
    borderBottom: `1px solid ${colors.neutral[200]}`,
  },

  headerButton: {
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
    ':hover': {
      backgroundColor: colors.neutral[100],
    },
  },

  headerTitle: {
    fontSize: '18px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  headerPlaceholder: {
    width: '40px',
  },

  // ========== PROGRESS INDICATOR ==========
  progressIndicator: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    height: '60px',
    backgroundColor: '#FFFFFF',
    gap: '0px',
  },

  stepCircle: {
    width: '32px',
    height: '32px',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '14px',
    fontWeight: 600,
    flexShrink: 0,
  },

  stepActive: {
    backgroundColor: colors.brand[60],
    color: '#FFFFFF',
  },

  stepCompleted: {
    backgroundColor: colors.brand[60],
    color: '#FFFFFF',
  },

  stepInactive: {
    backgroundColor: colors.neutral[200],
    color: colors.neutral[400],
  },

  stepLine: {
    width: '60px',
    height: '2px',
    backgroundColor: colors.neutral[200],
  },

  stepLineCompleted: {
    backgroundColor: colors.brand[60],
  },

  // ========== CONTENT ==========
  content: {
    flex: 1,
    padding: '24px 16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },

  contentCentered: {
    alignItems: 'center',
  },

  sectionTitle: {
    fontSize: '20px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  sectionLabel: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
  },

  // ========== ACCOUNT CARD ==========
  accountCard: {
    width: '100%',
    height: '80px',
    backgroundColor: '#FFFFFF',
    borderRadius: '12px',
    border: `1px solid ${colors.neutral[200]}`,
    padding: '0 16px',
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
    gap: '4px',
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
    fontSize: '18px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  // ========== SEARCH FIELD ==========
  searchField: {
    width: '100%',
    height: '48px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[300]}`,
    borderRadius: '8px',
    display: 'flex',
    alignItems: 'center',
    padding: '0 16px',
    gap: '12px',
  },

  searchIcon: {
    color: colors.neutral[400],
    flexShrink: 0,
  },

  searchInput: {
    flex: 1,
    border: 'none',
    outline: 'none',
    fontSize: '16px',
    backgroundColor: 'transparent',
    '::placeholder': {
      color: colors.neutral[400],
    },
  },

  // ========== RECIPIENT CARD ==========
  recipientSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },

  recipientCard: {
    width: '100%',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '12px',
    padding: '16px',
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  recipientCardSelected: {
    border: `2px solid ${colors.brand[60]}`,
    backgroundColor: colors.brand[130],
  },

  recipientInfo: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },

  recipientName: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  recipientAccount: {
    fontSize: '14px',
    fontFamily: 'Consolas, monospace',
    color: colors.neutral[500],
  },

  recipientCheck: {
    color: colors.brand[60],
    flexShrink: 0,
  },

  // ========== NEW RECIPIENT CARD ==========
  newRecipientCard: {
    width: '100%',
    backgroundColor: '#FFFFFF',
    border: `1px dashed ${colors.neutral[300]}`,
    borderRadius: '12px',
    padding: '16px',
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    cursor: 'pointer',
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  newRecipientIcon: {
    width: '48px',
    height: '48px',
    borderRadius: '50%',
    backgroundColor: colors.neutral[100],
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.neutral[500],
  },

  newRecipientText: {
    fontSize: '16px',
    fontWeight: 500,
    color: colors.brand[60],
  },

  // ========== AMOUNT SECTION ==========
  amountSection: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    width: '100%',
    padding: '24px 16px',
  },

  amountLabel: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  amountDisplay: {
    display: 'flex',
    alignItems: 'baseline',
  },

  amountCurrency: {
    fontSize: '48px',
    fontWeight: 300,
    color: colors.neutral[800],
  },

  amountValue: {
    fontSize: '48px',
    fontWeight: 700,
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
    width: '200px',
    '::placeholder': {
      color: colors.neutral[300],
    },
  },

  availableBalance: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  // ========== QUICK AMOUNTS ==========
  quickAmounts: {
    display: 'flex',
    gap: '12px',
    justifyContent: 'center',
    flexWrap: 'wrap',
  },

  quickBtn: {
    minWidth: '70px',
    height: '36px',
    padding: '0 16px',
    backgroundColor: colors.neutral[100],
    borderRadius: '8px',
    border: 'none',
    cursor: 'pointer',
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.neutral[200],
    },
  },

  quickBtnSelected: {
    backgroundColor: colors.brand[120],
    color: colors.brand[60],
  },

  // ========== SUMMARY CARD ==========
  summaryCard: {
    width: '100%',
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    boxShadow: shadows.lg,
    padding: '24px',
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
  },

  summaryAmountSection: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '4px',
    paddingBottom: '20px',
    borderBottom: `1px solid ${colors.neutral[200]}`,
  },

  summaryAmountLabel: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  summaryAmountValue: {
    fontSize: '40px',
    fontWeight: 700,
    color: colors.neutral[800],
  },

  // ========== TRANSFER FLOW ==========
  transferFlow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '16px 0',
  },

  flowAccount: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
  },

  flowName: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
    textAlign: 'center',
  },

  flowType: {
    fontSize: '12px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  flowArrow: {
    color: colors.brand[60],
  },

  // ========== DETAIL ROWS ==========
  transferDetails: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },

  detailRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'flex-start',
  },

  detailLabel: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  detailValue: {
    fontSize: '14px',
    fontWeight: 600,
    color: colors.neutral[800],
    textAlign: 'right',
  },

  detailValueAccount: {
    fontSize: '12px',
    fontWeight: 400,
    fontFamily: 'Consolas, monospace',
    color: colors.neutral[500],
  },

  accountBlock: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-end',
    gap: '2px',
  },

  // ========== FEE NOTICE ==========
  feeNotice: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '8px',
    padding: '12px',
    backgroundColor: colors.brand[130],
    borderRadius: '8px',
  },

  feeIcon: {
    color: colors.brand[60],
  },

  feeText: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.brand[60],
  },

  // ========== SUCCESS SCREEN ==========
  successIconContainer: {
    width: '120px',
    height: '120px',
    backgroundColor: colors.semantic.success.light,
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },

  successIcon: {
    width: '64px',
    height: '64px',
    color: colors.semantic.success.main,
  },

  successMessage: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
  },

  successTitle: {
    fontSize: '24px',
    fontWeight: 700,
    color: colors.semantic.success.main,
    textAlign: 'center',
  },

  successSubtitle: {
    fontSize: '16px',
    fontWeight: 400,
    color: colors.neutral[500],
    textAlign: 'center',
  },

  divider: {
    width: '100%',
    height: '1px',
    backgroundColor: colors.neutral[200],
  },

  referenceSection: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '4px',
    paddingTop: '8px',
  },

  referenceLabel: {
    fontSize: '12px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  referenceValue: {
    fontSize: '14px',
    fontWeight: 600,
    fontFamily: 'Consolas, monospace',
    color: colors.neutral[800],
    letterSpacing: '0.05em',
  },

  amountHighlight: {
    fontSize: '20px',
    fontWeight: 700,
    color: colors.semantic.success.main,
  },

  // ========== FOOTER ==========
  footer: {
    backgroundColor: '#FFFFFF',
    borderTop: `1px solid ${colors.neutral[200]}`,
    padding: '16px 16px 24px 16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },

  footerSingle: {
    padding: '12px 16px 32px 16px',
  },

  // ========== LOADING ==========
  loadingOverlay: {
    position: 'fixed',
    inset: 0,
    backgroundColor: 'rgba(255, 255, 255, 0.9)',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    zIndex: 1000,
  },
});

// ============================================
// MOCK DATA
// ============================================

const mockAccounts: Account[] = [
  {
    id: '1',
    name: 'Primary Account',
    accountNumber: '**** 4521',
    balance: 12450.0,
  },
  {
    id: '2',
    name: 'Savings Account',
    accountNumber: '**** 7832',
    balance: 8230.5,
  },
];

const mockRecipients: Recipient[] = [
  {
    id: '1',
    name: 'John Doe',
    accountNumber: '****4567',
    initials: 'JD',
  },
  {
    id: '2',
    name: 'Sarah Miller',
    accountNumber: '****8901',
    initials: 'SM',
  },
  {
    id: '3',
    name: 'Michael Johnson',
    accountNumber: '****2345',
    initials: 'MJ',
  },
];

const quickAmounts = [50, 100, 250, 500];

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

function generateReferenceNumber(): string {
  const date = new Date();
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');
  const random = String(Math.floor(Math.random() * 100000)).padStart(5, '0');
  return `TXN-${year}-${month}${day}-${random}`;
}

// ============================================
// COMPONENT
// ============================================

export function TransferPage() {
  const styles = useStyles();
  const navigate = useNavigate();

  // Wizard state
  const [currentStep, setCurrentStep] = useState(1);
  const [isSuccess, setIsSuccess] = useState(false);
  const [isLoading, setIsLoading] = useState(false);

  // Form data
  const [transferData, setTransferData] = useState<TransferData>({
    fromAccount: null,
    toRecipient: null,
    amount: 0,
  });

  const [searchQuery, setSearchQuery] = useState('');
  const [amountInput, setAmountInput] = useState('');
  const [referenceNumber, setReferenceNumber] = useState('');

  // Navigation handlers
  const handleBack = () => {
    if (currentStep > 1) {
      setCurrentStep(currentStep - 1);
    } else {
      navigate(-1);
    }
  };

  const handleClose = () => {
    navigate('/');
  };

  const handleContinue = () => {
    if (currentStep < 4) {
      setCurrentStep(currentStep + 1);
    }
  };

  const handleConfirmTransfer = async () => {
    setIsLoading(true);

    // Simulate API call
    await new Promise((resolve) => setTimeout(resolve, 1500));

    setReferenceNumber(generateReferenceNumber());
    setIsLoading(false);
    setIsSuccess(true);
  };

  // Selection handlers
  const handleSelectAccount = (account: Account) => {
    setTransferData((prev) => ({ ...prev, fromAccount: account }));
  };

  const handleSelectRecipient = (recipient: Recipient) => {
    setTransferData((prev) => ({ ...prev, toRecipient: recipient }));
  };

  const handleAmountChange = (value: string) => {
    // Remove non-numeric characters except decimal
    const cleaned = value.replace(/[^0-9.]/g, '');
    setAmountInput(cleaned);
    setTransferData((prev) => ({ ...prev, amount: parseFloat(cleaned) || 0 }));
  };

  const handleQuickAmount = (amount: number) => {
    setAmountInput(amount.toString());
    setTransferData((prev) => ({ ...prev, amount }));
  };

  // Check if can continue
  const canContinue = () => {
    switch (currentStep) {
      case 1:
        return transferData.fromAccount !== null;
      case 2:
        return transferData.toRecipient !== null;
      case 3:
        return transferData.amount > 0 && transferData.amount <= (transferData.fromAccount?.balance || 0);
      default:
        return true;
    }
  };

  // Filter recipients by search
  const filteredRecipients = mockRecipients.filter(
    (r) =>
      r.name.toLowerCase().includes(searchQuery.toLowerCase()) ||
      r.accountNumber.includes(searchQuery)
  );

  // Render progress indicator
  const renderProgressIndicator = () => (
    <div className={styles.progressIndicator}>
      {[1, 2, 3, 4].map((step, index) => (
        <>
          <div
            key={step}
            className={`${styles.stepCircle} ${
              step === currentStep
                ? styles.stepActive
                : step < currentStep
                ? styles.stepCompleted
                : styles.stepInactive
            }`}
          >
            {step}
          </div>
          {index < 3 && (
            <div
              className={`${styles.stepLine} ${
                step < currentStep ? styles.stepLineCompleted : ''
              }`}
            />
          )}
        </>
      ))}
    </div>
  );

  // Render Step 1: Select Source Account
  const renderStep1 = () => (
    <div className={styles.content}>
      <Text className={styles.sectionTitle}>From which account?</Text>

      {mockAccounts.map((account) => (
        <div
          key={account.id}
          className={`${styles.accountCard} ${
            transferData.fromAccount?.id === account.id ? styles.accountCardSelected : ''
          }`}
          onClick={() => handleSelectAccount(account)}
          role="button"
          tabIndex={0}
        >
          <div className={styles.accountInfo}>
            <Text className={styles.accountName}>{account.name}</Text>
            <Text className={styles.accountNumber}>{account.accountNumber}</Text>
          </div>
          <Text className={styles.accountBalance}>{formatCurrency(account.balance)}</Text>
        </div>
      ))}
    </div>
  );

  // Render Step 2: Select Destination
  const renderStep2 = () => (
    <div className={styles.content}>
      <Text className={styles.sectionTitle}>Select Destination</Text>

      <div className={styles.searchField}>
        <Search24Regular className={styles.searchIcon} />
        <input
          type="text"
          placeholder="Search by name or account"
          className={styles.searchInput}
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
        />
      </div>

      <div className={styles.recipientSection}>
        <Text className={styles.sectionLabel}>Recent Recipients</Text>

        {filteredRecipients.map((recipient) => (
          <div
            key={recipient.id}
            className={`${styles.recipientCard} ${
              transferData.toRecipient?.id === recipient.id ? styles.recipientCardSelected : ''
            }`}
            onClick={() => handleSelectRecipient(recipient)}
            role="button"
            tabIndex={0}
          >
            <Avatar initials={recipient.initials} size="md" />
            <div className={styles.recipientInfo}>
              <Text className={styles.recipientName}>{recipient.name}</Text>
              <Text className={styles.recipientAccount}>{recipient.accountNumber}</Text>
            </div>
            {transferData.toRecipient?.id === recipient.id && (
              <Checkmark24Filled className={styles.recipientCheck} />
            )}
          </div>
        ))}
      </div>

      <div className={styles.newRecipientCard} role="button" tabIndex={0}>
        <div className={styles.newRecipientIcon}>
          <Add24Regular />
        </div>
        <Text className={styles.newRecipientText}>Add New Recipient</Text>
      </div>
    </div>
  );

  // Render Step 3: Enter Amount
  const renderStep3 = () => (
    <div className={`${styles.content} ${styles.contentCentered}`}>
      <div className={styles.amountSection}>
        <Text className={styles.amountLabel}>Enter amount</Text>
        <div className={styles.amountDisplay}>
          <span className={styles.amountCurrency}>$</span>
          <input
            type="text"
            inputMode="decimal"
            placeholder="0"
            className={styles.amountInput}
            value={amountInput}
            onChange={(e) => handleAmountChange(e.target.value)}
            autoFocus
          />
        </div>
        <Text className={styles.availableBalance}>
          Available: {formatCurrency(transferData.fromAccount?.balance || 0)}
        </Text>
      </div>

      <div className={styles.quickAmounts}>
        {quickAmounts.map((amount) => (
          <button
            key={amount}
            className={`${styles.quickBtn} ${
              transferData.amount === amount ? styles.quickBtnSelected : ''
            }`}
            onClick={() => handleQuickAmount(amount)}
          >
            ${amount}
          </button>
        ))}
      </div>
    </div>
  );

  // Render Step 4: Confirm Transfer
  const renderStep4 = () => (
    <div className={styles.content}>
      <Text className={styles.sectionTitle} style={{ textAlign: 'center' }}>
        Confirm Transfer
      </Text>

      <div className={styles.summaryCard}>
        <div className={styles.summaryAmountSection}>
          <Text className={styles.summaryAmountLabel}>You're sending</Text>
          <Text className={styles.summaryAmountValue}>
            {formatCurrency(transferData.amount)}
          </Text>
        </div>

        <div className={styles.transferFlow}>
          <div className={styles.flowAccount}>
            <Avatar initials="ME" size="md" />
            <Text className={styles.flowName}>{transferData.fromAccount?.name}</Text>
            <Text className={styles.flowType}>From</Text>
          </div>

          <ArrowRight24Regular className={styles.flowArrow} />

          <div className={styles.flowAccount}>
            <Avatar initials={transferData.toRecipient?.initials || ''} size="md" />
            <Text className={styles.flowName}>{transferData.toRecipient?.name}</Text>
            <Text className={styles.flowType}>To</Text>
          </div>
        </div>

        <div className={styles.transferDetails}>
          <div className={styles.detailRow}>
            <span className={styles.detailLabel}>From Account</span>
            <div className={styles.accountBlock}>
              <span className={styles.detailValue}>{transferData.fromAccount?.name}</span>
              <span className={styles.detailValueAccount}>
                {transferData.fromAccount?.accountNumber}
              </span>
            </div>
          </div>

          <div className={styles.detailRow}>
            <span className={styles.detailLabel}>To Account</span>
            <div className={styles.accountBlock}>
              <span className={styles.detailValue}>{transferData.toRecipient?.name}</span>
              <span className={styles.detailValueAccount}>
                {transferData.toRecipient?.accountNumber}
              </span>
            </div>
          </div>

          <div className={styles.detailRow}>
            <span className={styles.detailLabel}>Amount</span>
            <span className={styles.detailValue}>{formatCurrency(transferData.amount)}</span>
          </div>

          <div className={styles.detailRow}>
            <span className={styles.detailLabel}>Date</span>
            <span className={styles.detailValue}>
              Today, {new Date().toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })}
            </span>
          </div>
        </div>

        <div className={styles.feeNotice}>
          <Info24Regular className={styles.feeIcon} />
          <span className={styles.feeText}>No fees for internal transfers</span>
        </div>
      </div>
    </div>
  );

  // Render Success Screen
  const renderSuccess = () => (
    <div className={`${styles.content} ${styles.contentCentered}`} style={{ gap: '32px', paddingTop: '48px' }}>
      <div className={styles.successIconContainer}>
        <CheckmarkCircle24Filled style={{ width: '64px', height: '64px', color: colors.semantic.success.main }} />
      </div>

      <div className={styles.successMessage}>
        <Text className={styles.successTitle}>Transfer Successful!</Text>
        <Text className={styles.successSubtitle}>Your money has been sent successfully</Text>
      </div>

      <div className={styles.summaryCard}>
        <div className={styles.detailRow}>
          <span className={styles.detailLabel}>Amount Sent</span>
          <span className={styles.amountHighlight}>{formatCurrency(transferData.amount)}</span>
        </div>

        <div className={styles.divider} />

        <div className={styles.detailRow}>
          <span className={styles.detailLabel}>From</span>
          <div className={styles.accountBlock}>
            <span className={styles.detailValue}>{transferData.fromAccount?.name}</span>
            <span className={styles.detailValueAccount}>
              {transferData.fromAccount?.accountNumber}
            </span>
          </div>
        </div>

        <div className={styles.detailRow}>
          <span className={styles.detailLabel}>To</span>
          <div className={styles.accountBlock}>
            <span className={styles.detailValue}>{transferData.toRecipient?.name}</span>
            <span className={styles.detailValueAccount}>
              {transferData.toRecipient?.accountNumber}
            </span>
          </div>
        </div>

        <div className={styles.detailRow}>
          <span className={styles.detailLabel}>Date & Time</span>
          <span className={styles.detailValue}>
            {new Date().toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })} at{' '}
            {new Date().toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}
          </span>
        </div>

        <div className={styles.divider} />

        <div className={styles.referenceSection}>
          <span className={styles.referenceLabel}>Reference Number</span>
          <span className={styles.referenceValue}>{referenceNumber}</span>
        </div>
      </div>
    </div>
  );

  // Render current step content
  const renderStepContent = () => {
    if (isSuccess) return renderSuccess();

    switch (currentStep) {
      case 1:
        return renderStep1();
      case 2:
        return renderStep2();
      case 3:
        return renderStep3();
      case 4:
        return renderStep4();
      default:
        return null;
    }
  };

  // Render footer
  const renderFooter = () => {
    if (isSuccess) {
      return (
        <div className={styles.footer}>
          <Button
            appearance="primary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={() => navigate('/')}
          >
            Done
          </Button>
          <Button
            appearance="outline"
            size="large"
            style={{ width: '100%', height: '48px' }}
            icon={<Share24Regular />}
          >
            Share Receipt
          </Button>
        </div>
      );
    }

    if (currentStep === 4) {
      return (
        <div className={styles.footer}>
          <Button
            appearance="primary"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={handleConfirmTransfer}
            disabled={isLoading}
          >
            {isLoading ? <Spinner size="tiny" /> : 'Confirm Transfer'}
          </Button>
          <Button
            appearance="outline"
            size="large"
            style={{ width: '100%', height: '48px' }}
            onClick={handleClose}
          >
            Cancel
          </Button>
        </div>
      );
    }

    return (
      <div className={`${styles.footer} ${styles.footerSingle}`}>
        <Button
          appearance="primary"
          size="large"
          style={{ width: '100%', height: '48px' }}
          onClick={handleContinue}
          disabled={!canContinue()}
        >
          Continue
        </Button>
      </div>
    );
  };

  return (
    <div className={styles.container}>
      {/* Loading overlay */}
      {isLoading && (
        <div className={styles.loadingOverlay}>
          <Spinner size="large" label="Processing transfer..." />
        </div>
      )}

      {/* Header */}
      <div className={styles.header}>
        {!isSuccess ? (
          <button className={styles.headerButton} onClick={handleBack}>
            <ChevronLeft24Regular />
          </button>
        ) : (
          <div className={styles.headerPlaceholder} />
        )}
        <Text className={styles.headerTitle}>
          {isSuccess ? 'Transfer Complete' : 'Transfer Money'}
        </Text>
        {!isSuccess ? (
          <button className={styles.headerButton} onClick={handleClose}>
            <Dismiss24Regular />
          </button>
        ) : (
          <div className={styles.headerPlaceholder} />
        )}
      </div>

      {/* Progress Indicator (hide on success) */}
      {!isSuccess && renderProgressIndicator()}

      {/* Content */}
      {renderStepContent()}

      {/* Footer */}
      {renderFooter()}
    </div>
  );
}

export default TransferPage;
