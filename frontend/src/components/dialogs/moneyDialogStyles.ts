import { makeStyles, tokens } from '@fluentui/react-components';
import { colors, transitions } from '../../theme/tokens';

/**
 * The chrome and interior both money dialogs draw, in one place.
 *
 * Deposit and withdraw each carried their own `makeStyles` block: 37 keys and 40 keys. Measured
 * with comments stripped and whitespace normalised, **deposit had not a single key withdraw did
 * not, and 34 of the 37 were byte-identical**. Two files of eight hundred lines were styling the
 * same dialog twice.
 *
 * Three keys genuinely differed, and only one of them was a decision:
 *
 *  - `headerIcon` — green on deposit, brand blue on withdraw. That one IS meant: money arriving
 *    and money leaving should not look the same. It became the `tone` prop on `MoneyDialogShell`
 *    rather than two copies of a style block.
 *  - `closeButton` and `quickBtn` — withdraw styled `:disabled`, deposit did not. **Not a
 *    decision.** Deposit disables five controls while a deposit is in flight and styled none of
 *    them, so during the one moment a user is most likely to jab at a button — the request that
 *    moves their money — the close button and the amount chips looked completely live. Withdraw's
 *    version wins here, and deposit inherits it by having nowhere else to look.
 *
 * The PIN step's three keys stay in `WithdrawDialog`: they belong to a step deposit does not have,
 * and moving them here would make this module a place where things are put rather than a place
 * where shared things live.
 */
export const useMoneyDialogStyles = makeStyles({
  surface: {
    width: '100%',
    maxWidth: '480px',
    maxHeight: '90vh',
    padding: 0,
    borderRadius: '16px',
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
  },
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

  /** Shape only. The colour pair arrives from `tone` — see the note above. */
  headerIcon: {
    width: '32px',
    height: '32px',
    borderRadius: '8px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  headerIconCredit: {
    backgroundColor: colors.semantic.success.light,
    color: colors.semantic.success.main,
  },
  headerIconDebit: {
    backgroundColor: colors.brand[130],
    color: colors.brand[60],
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
    ':hover': { backgroundColor: colors.neutral[100] },
    ':disabled': { opacity: 0.5, cursor: 'not-allowed' },
  },
  content: {
    flex: 1,
    padding: '24px 20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
    overflowY: 'auto',
  },
  sectionLabel: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[500],
    marginBottom: '8px',
  },
  accountCard: {
    width: '100%',
    padding: '16px',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    ':hover': { backgroundColor: colors.neutral[50] },
  },
  accountCardSelected: {
    border: `2px solid ${colors.brand[60]}`,
    backgroundColor: colors.brand[130],
  },
  accountInfo: { display: 'flex', flexDirection: 'column', gap: '2px' },
  accountName: { fontSize: '16px', fontWeight: 500, color: colors.neutral[800] },
  accountNumber: {
    fontSize: '14px',
    fontFamily: 'Consolas, monospace',
    color: colors.neutral[500],
  },
  accountBalance: { fontSize: '16px', fontWeight: 600, color: colors.neutral[800] },
  amountSection: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '16px',
    backgroundColor: colors.neutral[50],
    borderRadius: '12px',
  },
  amountLabel: { fontSize: '14px', fontWeight: 400, color: colors.neutral[500] },
  amountInputWrapper: { display: 'flex', alignItems: 'baseline', gap: '4px' },
  amountCurrency: { fontSize: '32px', fontWeight: 300, color: colors.neutral[800] },
  amountInput: {
    fontSize: '48px',
    fontWeight: 700,
    color: colors.neutral[800],
    border: 'none',
    outline: 'none',
    background: 'transparent',
    textAlign: 'center',
    width: '180px',
    '::placeholder': { color: colors.neutral[300] },
  },
  newBalance: { fontSize: '13px', color: colors.neutral[500] },
  amountHint: { fontSize: '13px', fontWeight: 500, color: colors.semantic.error.main },
  // Composed ON TOP of amountCurrency/amountInput, so it only needs to restate the colour.
  amountInvalid: { color: colors.semantic.error.main },
  availableRow: { display: 'flex', alignItems: 'baseline', justifyContent: 'center', gap: '8px' },
  useMaxBtn: {
    background: 'none',
    border: 'none',
    padding: '0 2px',
    // `fontFamily`, NOT the `font` shorthand. Griffel does not expand `font` — measured, it emits
    // it verbatim as `.f15dniw2 { font: inherit; }` — so it competes with the longhands below as
    // an ordinary same-specificity atom and wins on stylesheet insertion order. Measured result:
    // the button's computed font-size came out as the INHERITED base, not the 13px declared here.
    fontFamily: 'inherit',
    fontSize: '13px',
    fontWeight: 600,
    color: colors.brand[60],
    textDecoration: 'underline',
    cursor: 'pointer',
    ':disabled': { color: colors.neutral[300], cursor: 'not-allowed', textDecoration: 'none' },
  },
  quickAmounts: { display: 'flex', gap: '8px', flexWrap: 'wrap', justifyContent: 'center' },
  quickBtn: {
    minWidth: '70px',
    height: '36px',
    padding: '0 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '8px',
    cursor: 'pointer',
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
    transition: `all ${transitions.fast}`,
    ':hover': { backgroundColor: colors.neutral[50] },
    ':disabled': { opacity: 0.5, cursor: 'not-allowed' },
  },
  quickBtnSelected: {
    backgroundColor: colors.brand[120],
    border: `1px solid ${colors.brand[60]}`,
    color: colors.brand[60],
  },
  descriptionInput: {
    width: '100%',
    padding: '12px',
    borderRadius: '8px',
    border: `1px solid ${colors.neutral[200]}`,
    fontSize: '14px',
    fontFamily: 'inherit',
    color: colors.neutral[800],
    outline: 'none',
    ':focus': { border: `1px solid ${colors.brand[60]}` },
  },

  // ===== STATE VIEWS =====
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
    backgroundColor: colors.semantic.warning.light,
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.semantic.warning.dark,
  },
  successTitle: { fontSize: '24px', fontWeight: 700, color: colors.semantic.success.main },
  stateTitle: { fontSize: '20px', fontWeight: 700, color: colors.neutral[800] },
  successAmount: { fontSize: '32px', fontWeight: 700, color: colors.neutral[800] },
  stateBody: { fontSize: '15px', color: colors.neutral[500], lineHeight: '1.5' },
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
    ':last-child': { borderBottom: 'none' },
  },
  detailLabel: { fontSize: '14px', color: colors.neutral[500] },
  detailValue: { fontSize: '14px', fontWeight: 600, color: colors.neutral[800] },
  footer: {
    padding: '16px 20px 24px 20px',
    borderTop: `1px solid ${colors.neutral[200]}`,
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  errorMessage: { marginBottom: '4px' },
});
