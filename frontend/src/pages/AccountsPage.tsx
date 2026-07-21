import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Button,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Spinner,
  Text,
} from '@fluentui/react-components';
import {
  ArrowSwap24Regular,
  Add24Regular,
  ArrowDownload20Regular,
  ArrowUpload20Regular,
  CreditCardToolbox24Regular,
  MoneyHand24Regular,
  CurrencyDollarEuro24Regular,
  MoreHorizontal20Regular,
} from '@fluentui/react-icons';
import { colors, shadows, gradients, transitions } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import type { AccountType } from '../api/enums';
import type { AccountResponse } from '../features/api/apiSlice';
import {
  useDeleteAccountMutation,
  useGetAccountsQuery,
  useSetPrimaryAccountMutation,
} from '../features/api/apiSlice';
import { formatCurrency, maskAccountNumber } from '../utils/format';
import {
  ConfirmDialog,
  CreateAccountDialog,
  DepositDialog,
  RenameAccountDialog,
  WithdrawDialog,
} from '../components';
import { useProblemToast } from '../components/feedback';

// D17: business-rule 422s render INLINE at the owning surface, mapped by errorCode.
const DELETE_RULES: Record<string, string> = {
  NON_ZERO_BALANCE: 'Only accounts with a zero balance can be deleted.',
  PRIMARY_ACCOUNT_DELETE: 'This is your primary account — set another account as primary first.',
};

// The legacy money dialogs (mock flow until their own PRs) take this minimal shape.
interface LegacyDialogAccount {
  id: string;
  name: string;
  accountNumber: string;
  balance: number;
}

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

  badgeRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
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

  primaryBadge: {
    padding: '4px 10px',
    backgroundColor: colors.brand[130],
    borderRadius: '12px',
  },

  primaryBadgeText: {
    fontSize: '12px',
    fontWeight: 600,
    color: colors.brand[60],
  },

  stateContainer: {
    display: 'flex',
    justifyContent: 'center',
    padding: '48px 0',
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
});

// ============================================
// HELPER FUNCTIONS
// ============================================

function getAccountIcon(type: AccountType) {
  switch (type) {
    case 'Savings':
      return <MoneyHand24Regular />;
    case 'Investment':
      return <CurrencyDollarEuro24Regular />;
    default:
      return <CreditCardToolbox24Regular />;
  }
}

// ============================================
// COMPONENT
// ============================================

export function AccountsPage() {
  const styles = useStyles();
  const navigate = useNavigate();

  // A1 — the first page on REAL data. Loading/error/empty are first-class states
  // (D22); the list refreshes through D7 tag invalidation, never hand-patching.
  const { data: accounts = [], isLoading, error, refetch } = useGetAccountsQuery();
  const problem = error as ApiProblem | undefined;
  const showProblem = useProblemToast();

  const [setPrimaryAccount] = useSetPrimaryAccountMutation();
  const [deleteAccount, { isLoading: isDeleting }] = useDeleteAccountMutation();

  const [isCreateOpen, setIsCreateOpen] = useState(false);
  const [isDepositOpen, setIsDepositOpen] = useState(false);
  const [isWithdrawOpen, setIsWithdrawOpen] = useState(false);
  const [selectedAccount, setSelectedAccount] = useState<LegacyDialogAccount | null>(null);
  const [renameTarget, setRenameTarget] = useState<AccountResponse | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<AccountResponse | null>(null);
  const [deleteProblem, setDeleteProblem] = useState<ApiProblem | null>(null);

  const totalBalance = accounts.reduce((sum, account) => sum + account.balance, 0);
  // Honest headers: never assert "€0.00" while loading, nor a stale total beside the
  // error bar (RTK Query keeps the last data when a refetch fails).
  const totalDisplay = isLoading || problem ? '—' : formatCurrency(totalBalance);

  // Adapter for the legacy money dialogs (their real flows arrive in their own PRs):
  // they only need id/name/number/balance — and they get the MASKED number.
  const toLegacy = (account: AccountResponse): LegacyDialogAccount => ({
    id: account.id,
    name: account.name,
    accountNumber: maskAccountNumber(account.accountNumber),
    balance: account.balance,
  });
  const legacyAccounts = accounts.map(toLegacy);

  const handleDeposit = (account: AccountResponse, e: React.MouseEvent) => {
    e.stopPropagation();
    setSelectedAccount(toLegacy(account));
    setIsDepositOpen(true);
  };

  const handleWithdraw = (account: AccountResponse, e: React.MouseEvent) => {
    e.stopPropagation();
    setSelectedAccount(toLegacy(account));
    setIsWithdrawOpen(true);
  };

  const handleTransfer = (e: React.MouseEvent) => {
    e.stopPropagation();
    navigate('/transfer');
  };

  const handleAddAccount = () => {
    setIsCreateOpen(true);
  };

  // Closing a money dialog also drops the card selection, so the next open always
  // re-scopes from the click that caused it.
  const closeDeposit = () => {
    setIsDepositOpen(false);
    setSelectedAccount(null);
  };

  const closeWithdraw = () => {
    setIsWithdrawOpen(false);
    setSelectedAccount(null);
  };

  // A6 — no dialog: the badge simply moves via the blanket 'Account' invalidation
  // (two rows flip, so the whole tag family refetches). Failures have no owning
  // surface here — they go through the problem-toast pipeline.
  const handleSetPrimary = (account: AccountResponse) => {
    setPrimaryAccount(account.id)
      .unwrap()
      .catch((caught) => showProblem(caught as ApiProblem));
  };

  const closeDelete = () => {
    setDeleteTarget(null);
    setDeleteProblem(null);
  };

  // A7 — the 422 business rules keep the dialog OPEN with the mapped reason inline.
  const handleConfirmDelete = () => {
    if (!deleteTarget) {
      return;
    }
    setDeleteProblem(null);
    deleteAccount(deleteTarget.id)
      .unwrap()
      .then(closeDelete)
      .catch((caught) => setDeleteProblem(caught as ApiProblem));
  };

  const deleteErrorText = deleteProblem
    ? (DELETE_RULES[deleteProblem.errorCode ?? ''] ??
      deleteProblem.detail ??
      'Could not delete the account.')
    : null;

  const getIconContainerClass = (type: AccountType) => {
    const base = styles.accountIconContainer;
    switch (type) {
      case 'Savings':
        return `${base} ${styles.iconContainerSavings}`;
      case 'Investment':
        return `${base} ${styles.iconContainerInvestment}`;
      default:
        return `${base} ${styles.iconContainerChecking}`;
    }
  };

  return (
    <div className={styles.container}>
      {/* Mobile Header */}
      <div className={styles.mobileHeader}>
        <div className={styles.headerTop}>
          <Text className={styles.headerTitle}>My Accounts</Text>
          <button className={styles.addButton} aria-label="Add account" onClick={handleAddAccount}>
            <Add24Regular />
          </button>
        </div>
        <div className={styles.totalBalanceSection}>
          <Text className={styles.totalLabel}>Total Balance</Text>
          <Text className={styles.totalValue}>{totalDisplay}</Text>
        </div>
      </div>

      {/* Main Content — the app shell (nav/header) is provided by ProtectedShell */}
      <div className={styles.mainContent}>
        {/* Desktop Page Header */}
        <div className={styles.desktopPageHeader}>
          <div className={styles.pageHeaderLeft}>
            <Text className={styles.pageTitle}>My Accounts</Text>
            <div className={styles.desktopTotalBalance}>
              <Text className={styles.desktopTotalLabel}>Total Balance:</Text>
              <Text className={styles.desktopTotalValue}>{totalDisplay}</Text>
            </div>
          </div>
          <button className={styles.desktopAddButton} onClick={handleAddAccount}>
            <Add24Regular />
            <Text className={styles.desktopAddButtonText}>Add Account</Text>
          </button>
        </div>

        {/* Loading / error are first-class states (D22) */}
        {isLoading && (
          <div className={styles.stateContainer}>
            <Spinner size="large" aria-label="Loading accounts" />
          </div>
        )}

        {problem && (
          <MessageBar intent="error">
            <MessageBarBody>
              {problem.detail || 'Could not load your accounts.'}
              {problem.traceId ? ` Support code: ${problem.traceId}` : ''}
            </MessageBarBody>
            <MessageBarActions>
              <Button appearance="transparent" onClick={() => void refetch()}>
                Retry
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}

        {/* Accounts Grid — cards are intentionally non-clickable: no /accounts/:id route
            exists yet (it previously bounced off the catch-all); management flows come later. */}
        {!isLoading && !problem && (
          <div className={styles.accountsGrid}>
            {accounts.map((account) => (
              <div key={account.id} className={styles.accountCard}>
                <div className={styles.accountHeader}>
                  <div className={getIconContainerClass(account.type)}>
                    {getAccountIcon(account.type)}
                  </div>
                  <div className={styles.accountInfo}>
                    <Text className={styles.accountName}>{account.name}</Text>
                    <Text
                      className={styles.accountNumber}
                      aria-label={`Account ending in ${account.accountNumber.slice(-2)}`}
                    >
                      {maskAccountNumber(account.accountNumber)}
                    </Text>
                  </div>
                  <Menu>
                    <MenuTrigger disableButtonEnhancement>
                      <Button
                        appearance="subtle"
                        icon={<MoreHorizontal20Regular />}
                        aria-label={`Account actions for ${account.name}`}
                      />
                    </MenuTrigger>
                    <MenuPopover>
                      <MenuList>
                        <MenuItem onClick={() => setRenameTarget(account)}>Rename</MenuItem>
                        {!account.isPrimary && (
                          <MenuItem onClick={() => handleSetPrimary(account)}>
                            Set as primary
                          </MenuItem>
                        )}
                        <MenuItem onClick={() => setDeleteTarget(account)}>Delete</MenuItem>
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                </div>

                <div className={styles.accountBalanceSection}>
                  <div className={styles.balanceInfo}>
                    <Text className={styles.balanceLabel}>Available Balance</Text>
                    <Text className={styles.balanceValue}>{formatCurrency(account.balance)}</Text>
                  </div>
                  <div className={styles.badgeRow}>
                    {account.isPrimary && (
                      <div className={styles.primaryBadge}>
                        <Text className={styles.primaryBadgeText}>Primary</Text>
                      </div>
                    )}
                    <div className={styles.accountTypeBadge}>
                      <Text className={styles.badgeText}>{account.type}</Text>
                    </div>
                  </div>
                </div>

                <div className={styles.accountActions}>
                  <button
                    className={styles.accountActionBtn}
                    aria-label={`Deposit to ${account.name}`}
                    onClick={(e) => handleDeposit(account, e)}
                  >
                    <ArrowDownload20Regular className={styles.actionBtnIcon} />
                    <Text className={styles.actionBtnText}>Deposit</Text>
                  </button>
                  <button
                    className={styles.accountActionBtn}
                    aria-label={`Withdraw from ${account.name}`}
                    onClick={(e) => handleWithdraw(account, e)}
                  >
                    <ArrowUpload20Regular className={styles.actionBtnIcon} />
                    <Text className={styles.actionBtnText}>Withdraw</Text>
                  </button>
                  <button
                    className={styles.accountActionBtn}
                    aria-label={`Transfer from ${account.name}`}
                    onClick={handleTransfer}
                  >
                    <ArrowSwap24Regular className={styles.actionBtnIcon} />
                    <Text className={styles.actionBtnText}>Transfer</Text>
                  </button>
                </div>
              </div>
            ))}

            {/* Add Account Card — a styled div, so it needs the button semantics by hand */}
            <div
              className={styles.addAccountCard}
              role="button"
              tabIndex={0}
              onClick={handleAddAccount}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault();
                  handleAddAccount();
                }
              }}
            >
              <div className={styles.addAccountIcon}>
                <Add24Regular />
              </div>
              <Text className={styles.addAccountText}>Add New Account</Text>
            </div>
          </div>
        )}
      </div>

      {/* Dialogs. The legacy money dialogs mount ON open: they capture `accounts` in a
          lazy useState initializer, so a persistent instance would pre-select from a
          stale (or, at page load, empty) list — remounting re-reads the CURRENT
          selection and unmounting on close drops all internal state. No onSuccess:
          their own Done button closes them, so the success screen stays reachable.
          (They still format USD — they die in the deposit/withdraw PRs.) */}
      {/* Mount-on-open like every other dialog on this page: a persistent Fluent
          Dialog instance re-opened after a close can race its own exit presence
          under load (the surface never re-mounts — seen as a CI-only flake); a
          fresh instance per open has virgin presence state and starts clean by
          construction. */}
      {isCreateOpen && <CreateAccountDialog open onClose={() => setIsCreateOpen(false)} />}

      {renameTarget && (
        <RenameAccountDialog
          account={{ id: renameTarget.id, name: renameTarget.name }}
          onClose={() => setRenameTarget(null)}
        />
      )}

      <ConfirmDialog
        isOpen={deleteTarget !== null}
        onClose={closeDelete}
        onConfirm={handleConfirmDelete}
        title="Delete account?"
        message={
          deleteTarget ? `You're about to delete "${deleteTarget.name}". This can't be undone.` : ''
        }
        confirmText="Delete"
        variant="danger"
        isLoading={isDeleting}
        errorText={deleteErrorText}
      />

      {isDepositOpen && (
        <DepositDialog
          isOpen
          onClose={closeDeposit}
          accounts={selectedAccount ? [selectedAccount] : legacyAccounts}
        />
      )}

      {isWithdrawOpen && (
        <WithdrawDialog
          isOpen
          onClose={closeWithdraw}
          accounts={selectedAccount ? [selectedAccount] : legacyAccounts}
        />
      )}
    </div>
  );
}

export default AccountsPage;
