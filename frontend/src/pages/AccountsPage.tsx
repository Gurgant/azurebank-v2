import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { makeStyles, Text } from '@fluentui/react-components';
import {
  Home24Regular,
  Wallet24Regular,
  ArrowSwap24Regular,
  Clock24Regular,
  MoreHorizontal24Regular,
  Add24Regular,
  ChevronRight20Regular,
  ArrowDownload20Regular,
  ArrowUpload20Regular,
  CreditCardToolbox24Regular,
  MoneyHand24Regular,
  CurrencyDollarEuro24Regular,
} from '@fluentui/react-icons';
import { colors, shadows, gradients, transitions } from '../theme/tokens';
import { DepositDialog, WithdrawDialog } from '../components';

// ============================================
// TYPES
// ============================================

type AccountType = 'checking' | 'savings' | 'investment';

interface Account {
  id: string;
  name: string;
  accountNumber: string;
  balance: number;
  type: AccountType;
}

// ============================================
// MOCK DATA
// ============================================

const mockAccounts: Account[] = [
  {
    id: '1',
    name: 'Main Account',
    accountNumber: '**** **** **** 4521',
    balance: 12450.0,
    type: 'checking',
  },
  {
    id: '2',
    name: 'Savings Account',
    accountNumber: '**** **** **** 7832',
    balance: 8200.0,
    type: 'savings',
  },
  {
    id: '3',
    name: 'Investment Account',
    accountNumber: '**** **** **** 9156',
    balance: 4200.0,
    type: 'investment',
  },
];

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  container: {
    minHeight: '100vh',
    backgroundColor: colors.neutral[50],
    display: 'flex',
    flexDirection: 'column',
  },

  // ========== MOBILE HEADER ==========
  mobileHeader: {
    background: colors.brand[60],
    padding: '0 16px 24px 16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
    '@media (min-width: 1024px)': {
      display: 'none',
    },
  },

  headerTop: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingTop: '16px',
  },

  headerTitle: {
    fontSize: '24px',
    fontWeight: 700,
    color: '#FFFFFF',
  },

  addButton: {
    width: '40px',
    height: '40px',
    background: 'rgba(255, 255, 255, 0.2)',
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    cursor: 'pointer',
    border: 'none',
    color: '#FFFFFF',
    transition: `all ${transitions.fast}`,
    ':hover': {
      background: 'rgba(255, 255, 255, 0.3)',
    },
  },

  totalBalanceSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  totalLabel: {
    fontSize: '14px',
    fontWeight: 400,
    color: 'rgba(255, 255, 255, 0.8)',
  },

  totalValue: {
    fontSize: '32px',
    fontWeight: 700,
    color: '#FFFFFF',
  },

  // ========== DESKTOP HEADER ==========
  desktopHeader: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between',
      padding: '0 32px',
      height: '64px',
      backgroundColor: '#FFFFFF',
      borderBottom: `1px solid ${colors.neutral[200]}`,
    },
  },

  desktopHeaderLeft: {
    display: 'flex',
    alignItems: 'center',
    gap: '48px',
  },

  logo: {
    fontSize: '24px',
    fontWeight: 700,
    color: colors.brand[60],
    cursor: 'pointer',
  },

  navMenu: {
    display: 'flex',
    gap: '32px',
  },

  navItem: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
    cursor: 'pointer',
    padding: '8px 0',
    borderBottom: '2px solid transparent',
    transition: `all ${transitions.fast}`,
    ':hover': {
      color: colors.neutral[800],
    },
  },

  navItemActive: {
    color: colors.brand[60],
    borderBottomColor: colors.brand[60],
  },

  desktopHeaderRight: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },

  userAvatar: {
    width: '40px',
    height: '40px',
    borderRadius: '50%',
    backgroundColor: colors.brand[130],
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },

  avatarInitials: {
    fontSize: '14px',
    fontWeight: 600,
    color: colors.brand[60],
  },

  userName: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  // ========== MAIN CONTENT ==========
  mainContent: {
    flex: 1,
    padding: '16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    marginTop: '-12px',
    '@media (min-width: 1024px)': {
      padding: '32px',
      marginTop: 0,
      gap: '24px',
    },
  },

  // ========== DESKTOP PAGE HEADER ==========
  desktopPageHeader: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between',
    },
  },

  pageHeaderLeft: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },

  pageTitle: {
    fontSize: '28px',
    fontWeight: 700,
    color: colors.neutral[800],
  },

  desktopTotalBalance: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '12px',
  },

  desktopTotalLabel: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  desktopTotalValue: {
    fontSize: '24px',
    fontWeight: 700,
    color: colors.neutral[800],
  },

  desktopAddButton: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '12px 20px',
    backgroundColor: colors.brand[60],
    border: 'none',
    borderRadius: '8px',
    cursor: 'pointer',
    color: '#FFFFFF',
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.brand[50],
    },
  },

  desktopAddButtonText: {
    fontSize: '14px',
    fontWeight: 500,
    color: '#FFFFFF',
  },

  // ========== ACCOUNTS GRID ==========
  accountsGrid: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    '@media (min-width: 1024px)': {
      display: 'grid',
      gridTemplateColumns: 'repeat(auto-fill, minmax(400px, 1fr))',
      gap: '24px',
    },
  },

  // ========== ACCOUNT CARD ==========
  accountCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    padding: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    boxShadow: shadows.sm,
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    ':hover': {
      boxShadow: shadows.md,
    },
    '@media (min-width: 1024px)': {
      padding: '24px',
    },
  },

  accountHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },

  accountIconContainer: {
    width: '48px',
    height: '48px',
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
  },

  iconContainerChecking: {
    background: gradients.primary,
    color: colors.brand[60],
  },

  iconContainerSavings: {
    background: gradients.success,
    color: colors.semantic.success.main,
  },

  iconContainerInvestment: {
    background: gradients.warning,
    color: colors.semantic.warning.main,
  },

  accountIcon: {
    width: '24px',
    height: '24px',
  },

  accountInfo: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },

  accountName: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  accountNumber: {
    fontSize: '13px',
    fontWeight: 400,
    fontFamily: 'Consolas, "Courier New", monospace',
    color: colors.neutral[500],
  },

  accountChevron: {
    color: colors.neutral[400],
  },

  accountBalanceSection: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'flex-end',
  },

  balanceInfo: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },

  balanceLabel: {
    fontSize: '12px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  balanceValue: {
    fontSize: '24px',
    fontWeight: 700,
    color: colors.neutral[800],
  },

  accountTypeBadge: {
    padding: '4px 10px',
    backgroundColor: colors.neutral[100],
    borderRadius: '12px',
  },

  badgeText: {
    fontSize: '12px',
    fontWeight: 500,
    color: colors.neutral[500],
  },

  accountActions: {
    display: 'flex',
    gap: '8px',
    paddingTop: '12px',
    borderTop: `1px solid ${colors.neutral[100]}`,
  },

  accountActionBtn: {
    flex: 1,
    height: '40px',
    backgroundColor: colors.neutral[50],
    border: 'none',
    borderRadius: '8px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '6px',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.brand[130],
    },
  },

  actionBtnIcon: {
    width: '18px',
    height: '18px',
    color: colors.brand[60],
  },

  actionBtnText: {
    fontSize: '13px',
    fontWeight: 500,
    color: colors.brand[60],
  },

  // ========== ADD ACCOUNT CARD ==========
  addAccountCard: {
    backgroundColor: '#FFFFFF',
    border: `2px dashed ${colors.neutral[300]}`,
    borderRadius: '16px',
    padding: '24px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    ':hover': {
      border: `2px dashed ${colors.brand[60]}`,
      backgroundColor: colors.brand[140],
    },
    '@media (min-width: 1024px)': {
      padding: '40px',
    },
  },

  addAccountIcon: {
    width: '48px',
    height: '48px',
    backgroundColor: colors.neutral[100],
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.neutral[500],
    transition: `all ${transitions.fast}`,
  },

  addAccountText: {
    fontSize: '15px',
    fontWeight: 500,
    color: colors.neutral[500],
    transition: `all ${transitions.fast}`,
  },

  // ========== BOTTOM NAV ==========
  bottomNav: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-around',
    padding: '8px 0 24px 0',
    backgroundColor: '#FFFFFF',
    borderTop: `1px solid ${colors.neutral[200]}`,
    '@media (min-width: 1024px)': {
      display: 'none',
    },
  },

  bottomNavItem: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '4px',
    cursor: 'pointer',
    background: 'none',
    border: 'none',
    padding: '8px',
  },

  bottomNavIcon: {
    width: '24px',
    height: '24px',
    color: colors.neutral[400],
  },

  bottomNavIconActive: {
    color: colors.brand[60],
  },

  bottomNavLabel: {
    fontSize: '10px',
    fontWeight: 500,
    color: colors.neutral[400],
  },

  bottomNavLabelActive: {
    color: colors.brand[60],
  },
});

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

function getAccountIcon(type: AccountType) {
  switch (type) {
    case 'checking':
      return <CreditCardToolbox24Regular />;
    case 'savings':
      return <MoneyHand24Regular />;
    case 'investment':
      return <CurrencyDollarEuro24Regular />;
  }
}

function getAccountTypeLabel(type: AccountType): string {
  switch (type) {
    case 'checking':
      return 'Checking';
    case 'savings':
      return 'Savings';
    case 'investment':
      return 'Investment';
  }
}

// ============================================
// COMPONENT
// ============================================

export function AccountsPage() {
  const styles = useStyles();
  const navigate = useNavigate();

  const [accounts] = useState<Account[]>(mockAccounts);
  const [isDepositOpen, setIsDepositOpen] = useState(false);
  const [isWithdrawOpen, setIsWithdrawOpen] = useState(false);
  const [selectedAccount, setSelectedAccount] = useState<Account | null>(null);

  const totalBalance = accounts.reduce((sum, account) => sum + account.balance, 0);

  const handleAccountClick = (accountId: string) => {
    // Navigate to account detail
    navigate(`/accounts/${accountId}`);
  };

  const handleDeposit = (account: Account, e: React.MouseEvent) => {
    e.stopPropagation();
    setSelectedAccount(account);
    setIsDepositOpen(true);
  };

  const handleWithdraw = (account: Account, e: React.MouseEvent) => {
    e.stopPropagation();
    setSelectedAccount(account);
    setIsWithdrawOpen(true);
  };

  const handleTransfer = (e: React.MouseEvent) => {
    e.stopPropagation();
    navigate('/transfer');
  };

  const handleAddAccount = () => {
    // TODO: Navigate to add account flow
    console.log('Add new account');
  };

  const getIconContainerClass = (type: AccountType) => {
    const base = styles.accountIconContainer;
    switch (type) {
      case 'checking':
        return `${base} ${styles.iconContainerChecking}`;
      case 'savings':
        return `${base} ${styles.iconContainerSavings}`;
      case 'investment':
        return `${base} ${styles.iconContainerInvestment}`;
    }
  };

  return (
    <div className={styles.container}>
      {/* Mobile Header */}
      <div className={styles.mobileHeader}>
        <div className={styles.headerTop}>
          <Text className={styles.headerTitle}>My Accounts</Text>
          <button className={styles.addButton} onClick={handleAddAccount}>
            <Add24Regular />
          </button>
        </div>
        <div className={styles.totalBalanceSection}>
          <Text className={styles.totalLabel}>Total Balance</Text>
          <Text className={styles.totalValue}>{formatCurrency(totalBalance)}</Text>
        </div>
      </div>

      {/* Desktop Header */}
      <div className={styles.desktopHeader}>
        <div className={styles.desktopHeaderLeft}>
          <Text className={styles.logo} onClick={() => navigate('/dashboard')}>
            AzureBank
          </Text>
          <div className={styles.navMenu}>
            <Text className={styles.navItem} onClick={() => navigate('/dashboard')}>
              Dashboard
            </Text>
            <Text className={`${styles.navItem} ${styles.navItemActive}`}>Accounts</Text>
            <Text className={styles.navItem} onClick={() => navigate('/history')}>
              Transactions
            </Text>
            <Text className={styles.navItem} onClick={() => navigate('/transfer')}>
              Transfers
            </Text>
          </div>
        </div>
        <div className={styles.desktopHeaderRight}>
          <div className={styles.userAvatar}>
            <Text className={styles.avatarInitials}>JD</Text>
          </div>
          <Text className={styles.userName}>John Doe</Text>
        </div>
      </div>

      {/* Main Content */}
      <div className={styles.mainContent}>
        {/* Desktop Page Header */}
        <div className={styles.desktopPageHeader}>
          <div className={styles.pageHeaderLeft}>
            <Text className={styles.pageTitle}>My Accounts</Text>
            <div className={styles.desktopTotalBalance}>
              <Text className={styles.desktopTotalLabel}>Total Balance:</Text>
              <Text className={styles.desktopTotalValue}>{formatCurrency(totalBalance)}</Text>
            </div>
          </div>
          <button className={styles.desktopAddButton} onClick={handleAddAccount}>
            <Add24Regular />
            <Text className={styles.desktopAddButtonText}>Add Account</Text>
          </button>
        </div>

        {/* Accounts Grid */}
        <div className={styles.accountsGrid}>
          {accounts.map((account) => (
            <div
              key={account.id}
              className={styles.accountCard}
              onClick={() => handleAccountClick(account.id)}
            >
              <div className={styles.accountHeader}>
                <div className={getIconContainerClass(account.type)}>
                  {getAccountIcon(account.type)}
                </div>
                <div className={styles.accountInfo}>
                  <Text className={styles.accountName}>{account.name}</Text>
                  <Text className={styles.accountNumber}>{account.accountNumber}</Text>
                </div>
                <ChevronRight20Regular className={styles.accountChevron} />
              </div>

              <div className={styles.accountBalanceSection}>
                <div className={styles.balanceInfo}>
                  <Text className={styles.balanceLabel}>Available Balance</Text>
                  <Text className={styles.balanceValue}>{formatCurrency(account.balance)}</Text>
                </div>
                <div className={styles.accountTypeBadge}>
                  <Text className={styles.badgeText}>{getAccountTypeLabel(account.type)}</Text>
                </div>
              </div>

              <div className={styles.accountActions}>
                <button
                  className={styles.accountActionBtn}
                  onClick={(e) => handleDeposit(account, e)}
                >
                  <ArrowDownload20Regular className={styles.actionBtnIcon} />
                  <Text className={styles.actionBtnText}>Deposit</Text>
                </button>
                <button
                  className={styles.accountActionBtn}
                  onClick={(e) => handleWithdraw(account, e)}
                >
                  <ArrowUpload20Regular className={styles.actionBtnIcon} />
                  <Text className={styles.actionBtnText}>Withdraw</Text>
                </button>
                <button className={styles.accountActionBtn} onClick={handleTransfer}>
                  <ArrowSwap24Regular className={styles.actionBtnIcon} />
                  <Text className={styles.actionBtnText}>Transfer</Text>
                </button>
              </div>
            </div>
          ))}

          {/* Add Account Card */}
          <div className={styles.addAccountCard} onClick={handleAddAccount}>
            <div className={styles.addAccountIcon}>
              <Add24Regular />
            </div>
            <Text className={styles.addAccountText}>Add New Account</Text>
          </div>
        </div>
      </div>

      {/* Bottom Navigation (Mobile) */}
      <div className={styles.bottomNav}>
        <button className={styles.bottomNavItem} onClick={() => navigate('/dashboard')}>
          <Home24Regular className={styles.bottomNavIcon} />
          <Text className={styles.bottomNavLabel}>Home</Text>
        </button>
        <button className={styles.bottomNavItem}>
          <Wallet24Regular className={`${styles.bottomNavIcon} ${styles.bottomNavIconActive}`} />
          <Text className={`${styles.bottomNavLabel} ${styles.bottomNavLabelActive}`}>
            Accounts
          </Text>
        </button>
        <button className={styles.bottomNavItem} onClick={() => navigate('/transfer')}>
          <ArrowSwap24Regular className={styles.bottomNavIcon} />
          <Text className={styles.bottomNavLabel}>Transfer</Text>
        </button>
        <button className={styles.bottomNavItem} onClick={() => navigate('/history')}>
          <Clock24Regular className={styles.bottomNavIcon} />
          <Text className={styles.bottomNavLabel}>History</Text>
        </button>
        <button className={styles.bottomNavItem} onClick={() => navigate('/settings')}>
          <MoreHorizontal24Regular className={styles.bottomNavIcon} />
          <Text className={styles.bottomNavLabel}>More</Text>
        </button>
      </div>

      {/* Dialogs */}
      <DepositDialog
        isOpen={isDepositOpen}
        onClose={() => setIsDepositOpen(false)}
        accounts={selectedAccount ? [selectedAccount] : accounts}
        onSuccess={() => {
          setIsDepositOpen(false);
          // TODO: Refresh account data
        }}
      />

      <WithdrawDialog
        isOpen={isWithdrawOpen}
        onClose={() => setIsWithdrawOpen(false)}
        accounts={selectedAccount ? [selectedAccount] : accounts}
        onSuccess={() => {
          setIsWithdrawOpen(false);
          // TODO: Refresh account data
        }}
      />
    </div>
  );
}

export default AccountsPage;
