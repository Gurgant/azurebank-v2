import { tokens, typographyStyles } from '@fluentui/react-components';

/**
 * Type named by the job it does, for a style to spread: `title: { ...typography.pageTitle }`.
 *
 * Each role is one of Fluent's `typographyStyles`, spread rather than written out again, so it
 * carries Fluent's family, size, weight and line height as Fluent's own variables. The two amounts
 * are set in bold: the one typed in a money dialog is Fluent's `largeTitle` step, 40px on 52px,
 * and the balance on the dashboard grows with the viewport, which no Fluent step does. `mono` is
 * the face for an account number.
 */
export const typography = {
  pageTitle: { ...typographyStyles.title3 },
  authTitle: { ...typographyStyles.title2 },
  sectionTitle: { ...typographyStyles.subtitle2 },
  dialogTitle: { ...typographyStyles.subtitle1 },
  statusTitle: { ...typographyStyles.subtitle1 },
  outcomeTitle: { ...typographyStyles.title3 },
  body: { ...typographyStyles.body1 },
  bodyStrong: { ...typographyStyles.body1Strong },
  caption: { ...typographyStyles.caption1 },
  tag: { ...typographyStyles.caption2Strong },
  // Grows with the viewport from 36px to 52px: the size of the dashboard's balance.
  amountDisplay: {
    fontSize: 'clamp(2.25rem, 1.6rem + 3.2vw, 3.25rem)',
    lineHeight: 1.05,
    fontWeight: tokens.fontWeightBold,
  },
  amountInput: { ...typographyStyles.largeTitle, fontWeight: tokens.fontWeightBold },
  mono: { fontFamily: tokens.fontFamilyMonospace },
} as const;
