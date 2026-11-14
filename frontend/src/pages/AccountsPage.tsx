import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Button,
  makeStyles,
  Menu,
  MenuDivider,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
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
import { SectionTitle } from '../components/shared/SectionTitle';
import type { ApiProblem } from '../api/problemBaseQuery';
import type { AccountType } from '../api/enums';
import type { AccountResponse } from '../features/api/apiSlice';
import { useGetAccountsQuery, useSetPrimaryAccountMutation } from '../features/api/apiSlice';
import { useFocusWhenLost } from '../hooks/useFocusWhenLost';
import { usePageRead } from '../hooks/usePageRead';
import { formatCurrency, maskAccountNumber } from '../utils/format';
import { AccountNumberField } from '../components/AccountNumberField';
import {
  CreateAccountDialog,
  DeleteAccountDialog,
  DepositDialog,
  RenameAccountDialog,
  WithdrawDialog,
} from '../components';
import {
  AlertSlot,
  ReadErrorBar,
  StatusSlot,
  useProblemToast,
  WaitHint,
} from '../components/feedback';

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
    ':active': { backgroundColor: colors.brandFill.pressed },
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

  // A column, so that a slow load's words sit under the spinner rather than beside it.
  stateContainer: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '12px',
    padding: '48px 0',
  },
  // After the wait: no box, so that the hint left inside for its "Loaded." takes no room either.
  afterWait: { display: 'contents' },

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

  // Fluent recolours a menu item's text on hover and on a press (and on nothing else: keyboard
  // focus keeps the rest colour), so the red is set for both. `error.dark` is the red for words:
  // 5.80:1 on the menu in light, 7.93:1 in dark. A press keeps the hover fill, where the red reads
  // at 5.32:1 in light; on Fluent's pressed grey it would be 4.39:1.
  deleteMenuItem: {
    color: colors.semantic.error.dark,
    ':hover': { color: colors.semantic.error.dark },
    ':hover:active': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      color: colors.semantic.error.dark,
    },
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

  // A1 — the first page on REAL data. Loading/error/empty are first-class states;
  // the list refreshes through tag invalidation, never hand-patching.
  const accountsQuery = useGetAccountsQuery();
  const { data: accounts = [], requestId } = accountsQuery;
  const showProblem = useProblemToast();

  // The spinner and the bar follow the wait, not `isLoading` and `error`: a Retry keeps the old
  // error while it runs, so the bar goes away for the new wait and comes back, announced, only if
  // that wait fails too.
  const { waiting, failed, problem, landingRef, stopWaiting, retry } = usePageRead(accountsQuery, {
    endpoint: 'getAccounts',
    arg: undefined,
  });

  const [setPrimaryAccount] = useSetPrimaryAccountMutation();

  const [isCreateOpen, setIsCreateOpen] = useState(false);
  const [isDepositOpen, setIsDepositOpen] = useState(false);
  const [isWithdrawOpen, setIsWithdrawOpen] = useState(false);
  const [selectedAccount, setSelectedAccount] = useState<LegacyDialogAccount | null>(null);
  const [renameTarget, setRenameTarget] = useState<AccountResponse | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<AccountResponse | null>(null);
  // One sentence for the last action that finished. The next action clears it as it starts.
  const [sentence, setSentence] = useState<string | null>(null);
  // A dialog's sentence, held until the dialog has gone and said in a task after that commit's.
  // Written in the commit that removes the dialog, the status is still under the aria-hidden that
  // tabster lifts from the page only after that commit, and it is shown with the page, not said
  // as a change.
  const [heldSentence, setHeldSentence] = useState<string | null>(null);
  // The account the last action deleted, by id: two accounts may share a name.
  const [deletedId, setDeletedId] = useState<string | null>(null);
  // Counts the actions started, so that an answer which arrives after another action began says
  // nothing over it.
  const actionsStarted = useRef(0);
  // The action count when the Create or Rename dialog opened: its answer is said only while that
  // is still the latest action, as set-primary's is. Closing either dialog counts as an action,
  // so that an answer which arrives after a Cancel says nothing; on a save the sentence is held
  // before the dialog closes. A dialog cancelled while its request was out still calls its close
  // when the answer comes, after it has gone: that close counts only while its dialog is still
  // the latest action, or it would silence an action started after the Cancel.
  const [createOpenedAt, setCreateOpenedAt] = useState(0);
  const [renameOpenedAt, setRenameOpenedAt] = useState(0);
  // The opening that is current, one per dialog. A late close that names an older opening is
  // dropped, or it would shut the dialog the visitor has opened since. Each moves only as a new
  // dialog opens, in the handler that sets the state above, so a dialog on screen always holds
  // the current value and can close itself.
  const currentCreateOpening = useRef(0);
  const currentRenameOpening = useRef(0);

  const startAction = () => {
    actionsStarted.current += 1;
    setSentence(null);
    setHeldSentence(null);
    setDeletedId(null);
  };

  const dialogOpen = isCreateOpen || renameTarget !== null || deleteTarget !== null;
  useEffect(() => {
    if (heldSentence === null || dialogOpen) return;
    const timer = setTimeout(() => {
      setSentence(heldSentence);
      setHeldSentence(null);
    });
    return () => clearTimeout(timer);
  }, [heldSentence, dialogOpen]);

  // As the dialog unmounts, useReturnFocus gives focus back to the card's menu button, still in
  // the page until the list read answers. Focus falls to body when the card leaves, and that is
  // the moment to land on the heading. The deletion on its own is earlier than that. A list read
  // that fails takes every card away for its error bar, while the list kept from before still
  // holds the account.
  const headingRef = useFocusWhenLost<HTMLHeadingElement>(
    deletedId !== null &&
      (problem !== undefined || !accounts.some((account) => account.id === deletedId)),
  );

  const totalBalance = accounts.reduce((sum, account) => sum + account.balance, 0);
  // Honest headers: never assert "€0.00" while loading, nor a stale total beside the
  // error bar (RTK Query keeps the last data when a refetch fails).
  const totalDisplay = waiting || problem ? '—' : formatCurrency(totalBalance);

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
    startAction();
    setSelectedAccount(toLegacy(account));
    setIsDepositOpen(true);
  };

  const handleWithdraw = (account: AccountResponse, e: React.MouseEvent) => {
    e.stopPropagation();
    startAction();
    setSelectedAccount(toLegacy(account));
    setIsWithdrawOpen(true);
  };

  // The account goes in the history entry's state, not the address: an account id in a URL
  // would reach the browser's history, the server's logs and wherever the link is pasted.
  const handleTransfer = (account: AccountResponse, e: React.MouseEvent) => {
    e.stopPropagation();
    startAction();
    navigate('/transfer', { state: { fromAccountId: account.id } });
  };

  const handleAddAccount = () => {
    startAction();
    currentCreateOpening.current = actionsStarted.current;
    setCreateOpenedAt(actionsStarted.current);
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
    startAction();
    const thisAction = actionsStarted.current;
    setPrimaryAccount(account.id)
      .unwrap()
      .then(() => {
        if (actionsStarted.current === thisAction) {
          setSentence(`"${account.name}" is now your primary account.`);
        }
      })
      .catch((caught) => showProblem(caught as ApiProblem));
  };

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
      <PageHeader headingRef={headingRef} />

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

        {/* Loading / error are first-class states. The box is the spinner's while the read
            waits, and no box at all afterwards: the hint stays inside it for its "Loaded.". */}
        <div className={waiting ? styles.stateContainer : styles.afterWait}>
          {waiting && <Spinner size="large" aria-label="Loading accounts" />}
          <WaitHint active={waiting} kind="read" failed={failed} onStopWaiting={stopWaiting} />
        </div>

        {/* Keyed by the request that failed, so that a Retry which fails again before its wait is
            ever drawn still puts a new bar into the alert, and it is announced again. */}
        <AlertSlot>
          {problem && (
            <ReadErrorBar
              key={requestId}
              problem={problem}
              fallback="Could not load your accounts."
              onRetry={retry}
              retryRef={landingRef}
            />
          )}
        </AlertSlot>

        <StatusSlot>
          {sentence && (
            <MessageBar intent="success">
              <MessageBarBody>{sentence}</MessageBarBody>
            </MessageBar>
          )}
        </StatusSlot>

        {/* Accounts Grid — cards are intentionally non-clickable: no /accounts/:id route
            exists yet (it previously bounced off the catch-all); management flows come later. */}
        {!waiting && !problem && (
          <div className={styles.accountsGrid}>
            {accounts.map((account) => (
              <div key={account.id} className={styles.accountCard}>
                <div className={styles.accountHeader}>
                  <div className={getIconContainerClass(account.type)}>
                    {getAccountIcon(account.type)}
                  </div>
                  <div className={styles.accountInfo}>
                    {/* A level-2 heading: the page's `<h1>` is the one heading above it. */}
                    <SectionTitle>{account.name}</SectionTitle>
                    <AccountNumberField account={account} onActionStart={startAction} />
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
                        <MenuItem
                          onClick={() => {
                            startAction();
                            currentRenameOpening.current = actionsStarted.current;
                            setRenameOpenedAt(actionsStarted.current);
                            setRenameTarget(account);
                          }}
                        >
                          Rename
                        </MenuItem>
                        {!account.isPrimary && (
                          <MenuItem onClick={() => handleSetPrimary(account)}>
                            Set as primary
                          </MenuItem>
                        )}
                        <MenuDivider />
                        <MenuItem
                          className={styles.deleteMenuItem}
                          onClick={() => {
                            startAction();
                            setDeleteTarget(account);
                          }}
                        >
                          Delete
                        </MenuItem>
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
                    onClick={(e) => handleTransfer(account, e)}
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
          their own Done button closes them, so the success screen stays reachable. */}
      {/* Mount-on-open like every other dialog on this page: a persistent Fluent
          Dialog instance re-opened after a close can race its own exit presence
          under load (the surface never re-mounts — seen as a CI-only flake); a
          fresh instance per open has virgin presence state and starts clean by
          construction. */}
      {isCreateOpen && (
        <CreateAccountDialog
          open
          onDone={(name) => {
            if (actionsStarted.current === createOpenedAt) {
              setHeldSentence(`Your new account "${name}" is open.`);
            }
          }}
          onClose={() => {
            if (createOpenedAt !== currentCreateOpening.current) return;
            if (actionsStarted.current === createOpenedAt) actionsStarted.current += 1;
            setIsCreateOpen(false);
          }}
        />
      )}

      {renameTarget && (
        <RenameAccountDialog
          account={{ id: renameTarget.id, name: renameTarget.name }}
          onDone={(name) => {
            if (actionsStarted.current === renameOpenedAt) {
              setHeldSentence(`"${renameTarget.name}" is now called "${name}".`);
            }
          }}
          onClose={() => {
            if (renameOpenedAt !== currentRenameOpening.current) return;
            if (actionsStarted.current === renameOpenedAt) actionsStarted.current += 1;
            setRenameTarget(null);
          }}
        />
      )}

      {/* ADR-0049 — mount-on-open like the rename dialog, so the PIN step's state cannot
          outlive an open. The dialog mints and deletes itself and refreshes the list by tag; the
          422 business rules stay INLINE in it. */}
      {deleteTarget && (
        <DeleteAccountDialog
          account={{ id: deleteTarget.id, name: deleteTarget.name }}
          onDeleted={(name) => {
            setDeletedId(deleteTarget.id);
            setHeldSentence(`"${name}" was deleted.`);
          }}
          onAlreadyClosed={() => setDeletedId(deleteTarget.id)}
          onClose={() => setDeleteTarget(null)}
        />
      )}

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
