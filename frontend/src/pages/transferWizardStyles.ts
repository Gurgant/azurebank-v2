import { makeStyles, tokens } from '@fluentui/react-components';
import { colors, surfaces, transitions } from '../theme/tokens';

/**
 * The chrome both transfer wizards draw, in one place.
 *
 * `TransferPage` and `InternalTransferPage` each carried their own `makeStyles` block. Measured
 * with comments stripped and whitespace normalised: **31 keys shared, 29 of them byte-identical**,
 * and InternalTransferPage had not a single key TransferPage lacked. Two eight-hundred-line files
 * were styling the same wizard twice.
 *
 * Only two keys differed, and neither was a decision:
 *
 *  - **`card`'s `:disabled` opacity, 0.5 vs 0.45.** Resolved to 0.45, and it moves zero pixels:
 *    TransferPage's account card has no `disabled` prop at all, so its `:disabled` rule could never
 *    match. Internal's To-list card carries `disabled={isFrom}`, so 0.45 is the value that has
 *    always rendered.
 *  - **A stray `marginBottom: '8px'` on `card` and on `sectionLabel`.** Gone. See below — this one
 *    is NOT a no-op and the difference is stated rather than glossed.
 *
 * **The spacing delta, measured rather than assumed.** The two pages achieved similar spacing by
 * different means: external wraps its card list in a `gap: 8px, marginTop: 8px` flex container,
 * internal put `marginBottom: 8px` on each card inside a bare `<div>`. Unifying on the container:
 *
 *  - `sectionLabel`'s margin was **already dead** on both. It is applied to a Fluent `<Text>`, whose
 *    root class ships as `.f1w7gpdv{display:inline}` (read from
 *    `@fluentui/react-text/lib/components/Text/useTextStyles.styles.js`), and vertical margins do
 *    not apply to non-replaced inline elements. Dropping it changes nothing.
 *  - `card`'s margin WAS live — the key sets `display: flex`. Internal therefore **gains 8px above
 *    each account list and loses 8px below it**; the From→To gap goes 28px → 20px. External is
 *    unchanged. That is a real, visible difference on one page, and it is the reason this PR is
 *    separately revertible.
 *
 * The six recipient-only keys (`recipientRow`, `input`, `recipientCard`, `avatar`,
 * `recipientName`, `recipientTag`) stay in `TransferPage`: they belong to a step internal does not
 * have, and moving them here would make this module a place where things are put rather than a
 * place where shared things live.
 */
export const useTransferWizardStyles = makeStyles({
  // A full-screen wizard sits deliberately OUTSIDE the app shell (see App.tsx), so unlike a shell
  // page it does own its canvas — but it takes the shell's value rather than inventing one.
  page: { minHeight: '100dvh', backgroundColor: surfaces.canvas },
  body: {
    maxWidth: '480px',
    margin: '0 auto',
    padding: '20px 16px 32px 16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
  },
  sectionLabel: { fontSize: '14px', fontWeight: 500, color: colors.neutral[500] },
  card: {
    width: '100%',
    padding: '14px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    cursor: 'pointer',
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
    backgroundColor: tokens.colorNeutralBackground1,
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
    // `outline: 'none'` with nothing in its place is a WCAG 2.4.7 (AA) failure, and it was
    // identical on both pages — a shared defect rather than drift, which is why it is fixed in the
    // commit that extracts the key rather than deferred to the drift PR. `:focus-visible` and not
    // `:focus`, so the ring appears for keyboard users without boxing a mouse click. The offset
    // keeps it clear of the 48px numerals.
    outline: 'none',
    ':focus-visible': {
      outline: `2px solid ${colors.brand[60]}`,
      outlineOffset: '4px',
    },
    background: 'transparent',
    textAlign: 'center',
    width: '170px',
    '::placeholder': { color: colors.neutral[300] },
  },
  hint: { fontSize: '13px', fontWeight: 500, color: colors.semantic.error.main },
  // Composed ON TOP of amountCurrency/amountInput, so it only needs to restate the colour.
  amountInvalid: { color: colors.semantic.error.main },
  subtle: { fontSize: '13px', color: colors.neutral[500] },
  availableRow: { display: 'flex', alignItems: 'baseline', justifyContent: 'center', gap: '8px' },
  useMaxBtn: {
    background: 'none',
    border: 'none',
    padding: '0 2px',
    // `fontFamily`, NOT the `font` shorthand — Griffel emits `font` verbatim and it then beats the
    // longhands below on insertion order. Same fix as `moneyDialogStyles`, same measurement.
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
    minWidth: '60px',
    height: '34px',
    padding: '0 14px',
    backgroundColor: tokens.colorNeutralBackground1,
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
    backgroundColor: tokens.colorNeutralBackground1,
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
    backgroundColor: colors.semantic.warning.light,
    borderRadius: '50%',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: colors.semantic.warning.dark,
  },
  successTitle: { fontSize: '24px', fontWeight: 700, color: colors.semantic.success.main },
  stateTitle: { fontSize: '20px', fontWeight: 700, color: colors.neutral[800] },
  stateBody: { fontSize: '15px', color: colors.neutral[500], lineHeight: '1.5' },
  successAmount: { fontSize: '32px', fontWeight: 700, color: colors.neutral[800] },
});
