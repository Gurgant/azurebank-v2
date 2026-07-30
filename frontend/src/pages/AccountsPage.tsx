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
  tokens,
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
import { atMedia } from '../theme/breakpoints';
import { colors, shadows, gradients, transitions } from '../theme/tokens';
import { PageHeader } from '../components/layout/PageHeader';
import type { ApiProblem } from '../api/problemBaseQuery';
import type { AccountType } from '../api/enums';
import type { AccountResponse } from '../features/api/apiSlice';
import {
  useDeleteAccountMutation,
  useGetAccountsQuery,
  useSetPrimaryAccountMutation,
} from '../features/api/apiSlice';
import { formatCurrency, maskAccountNumber } from '../utils/format';
import { AccountNumberField } from '../components/AccountNumberField';
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
  // The canvas belongs to the shell (AppLayout), not to each page. No `flex: 1` here, unlike
  // History and TransactionDetail: this page has no state that centres itself in the viewport,
  // so it sizes to its content.
  container: {
    display: 'flex',
    flexDirection: 'column',
  },

  // ========== MAIN CONTENT ==========
  mainContent: {
    flex: 1,
    padding: '16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    [atMedia.lg]: {
      padding: '32px',
      gap: '24px',
    },
  },

  // ========== TOTAL ==========
  summary: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '16px',
  },

  // The dashboard's label idiom — 12px uppercase with letter-spacing, so it reads as a field name
  // rather than prose and the number below it stays the loudest thing here.
  //
  // One deliberate departure: `neutral[600]`, not the dashboard's `neutral[500]`. The dashboard's
  // label sits on a white CARD (4.83:1, passing); this one sits on the canvas, which is darker, and
  // the same colour measures **4.39:1** there — under the 4.5:1 AA floor for 12px text. Copying a
  // colour without its ground is how a passing token becomes a failing one; `neutral[600]` is
  // 6.87:1 on the canvas.
  summaryLabel: {
    display: 'block',
    fontSize: '12px',
    fontWeight: 600,
    letterSpacing: '0.06em',
    textTransform: 'uppercase',
    color: colors.neutral[600],
  },

  // `clamp` rather than a breakpoint ramp, for the reason the dashboard's hero gives: Griffel does
  // not sort its media buckets (measured: 1024, 1366, 480, 640), so a ramp is exposed to an `sm`
  // rule beating an `lg` one. One declaration has no ordering to get wrong.
  summaryValue: {
    display: 'block',
    marginTop: '2px',
    fontSize: 'clamp(1.75rem, 1.45rem + 1.1vw, 2.25rem)',
    lineHeight: 1.1,
    fontWeight: 700,
    color: colors.neutral[900],
    fontVariantNumeric: 'tabular-nums',
  },

  addButton: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexShrink: 0,
    height: '40px',
    padding: '0 16px',
    backgroundColor: colors.brandFill.rest,
    border: 'none',
    borderRadius: '8px',
    cursor: 'pointer',
    color: tokens.colorNeutralForegroundOnBrand,
    transition: `all ${transitions.fast}`,
    ':hover': { backgroundColor: colors.brandFill.hover },
    ':focus-visible': { outline: `2px solid ${colors.brand[60]}`, outlineOffset: '2px' },
  },

  addButtonText: {
    fontSize: '14px',
    fontWeight: 500,
    color: tokens.colorNeutralForegroundOnBrand,
    // Below `md` the icon carries it alone: at 375px a labelled button and a five-figure total
    // compete for the same row. The accessible name is on the button either way, so nothing is
    // lost when the word is.
    display: 'none',
    [atMedia.md]: { display: 'inline' },
  },

  // ========== ACCOUNTS GRID ==========
  accountsGrid: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    [atMedia.lg]: {
      display: 'grid',
      gridTemplateColumns: 'repeat(auto-fill, minmax(400px, 1fr))',
      gap: '24px',
    },
  },

  // ========== ACCOUNT CARD ==========
  accountCard: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: '16px',
    padding: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    boxShadow: shadows.sm,
    [atMedia.lg]: {
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
    backgroundColor: tokens.colorNeutralBackground1,
    border: `2px dashed ${colors.neutral[300]}`,
    borderRadius: '16px',
    padding: '24px',
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    cursor: 'pointer',
    // A `<button>` does not inherit the page's font, and it had none of its own: the text inside is
    // a `Text` with its own size, but the button's line box was still Arial 13.33px.
    font: 'inherit',
    transition: `all ${transitions.fast}`,
    ':hover': {
      border: `2px dashed ${colors.brand[60]}`,
      backgroundColor: colors.brand[140],
    },
    ':focus-visible': { outline: `2px solid ${colors.brand[60]}`, outlineOffset: '2px' },
    [atMedia.lg]: {
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
      {/*
        The title comes from the nav table, like every other place in the app — so the tab and the
        page cannot come to call this somewhere different. It also brings the page's `<h1>`: measured
        before this change, `/accounts` exposed NO heading at any level, the only authenticated page
        that did not, because both of its titles were `<Text>` (an inline `<span>`).

        There were two headers, and they had drifted the way two copies do: a brand-blue hero on
        mobile, a white bar on desktop, "Total Balance" against "Total Balance:", a round icon button
        against a labelled one — the same page wearing two faces, neither of them the app's. The blue
        block was also the last page-sized brand fill left in the signed-in app; the shell paints the
        canvas now, and the dashboard's hero is a white card.
      */}
      <PageHeader />

      {/* Main Content — the app shell (nav/header) is provided by ProtectedShell */}
      <div className={styles.mainContent}>
        <div className={styles.summary}>
          <div>
            <Text className={styles.summaryLabel}>Total balance</Text>
            <Text className={styles.summaryValue}>{totalDisplay}</Text>
          </div>
          {/* One add affordance up here and one at the end of the grid — down from three, which is
              what the two headers plus the dashed card added up to at ≥1024px. */}
          <button className={styles.addButton} aria-label="Add account" onClick={handleAddAccount}>
            <Add24Regular />
            <Text className={styles.addButtonText}>Add account</Text>
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
                    <AccountNumberField account={account} />
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

            {/* A real `<button>`. It was a `<div role="button" tabIndex={0}>` re-implementing
                Enter and Space by hand — four lines to get back what the element gives free, and
                still missing the rest of what a button is (form semantics, `:disabled`, the
                platform's own focus and activation behaviour). */}
            <button type="button" className={styles.addAccountCard} onClick={handleAddAccount}>
              <div className={styles.addAccountIcon}>
                <Add24Regular />
              </div>
              <Text className={styles.addAccountText}>Add New Account</Text>
            </button>
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
