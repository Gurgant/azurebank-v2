import type { ReactNode } from 'react';
import { Link as FluentLink, makeStyles, Text, tokens } from '@fluentui/react-components';
import { Link } from 'react-router-dom';
import { atMedia } from '../../theme/breakpoints';
import { Logo } from '../shared/Logo';
import { AuthBrandingPanel } from './AuthBrandingPanel';

/**
 * The frame both auth screens sit in: brand panel, form column, heading, footer slot.
 *
 * `LoginPage` and `RegisterPage` each declared their own copy. Of the 28 style keys they shared,
 * **23 were byte-identical and 5 had drifted** — and the drift is the part that showed: the form
 * column was capped at 400px on one page and 440px on the other, and the gap between fields was
 * 18px against 16px. Two screens one click apart, 40px different in width, for no reason anyone
 * wrote down. Extracting the frame is what makes that unrepresentable rather than merely tidy.
 *
 * The two survivors of the 5 are resolved on purpose, not averaged:
 *
 *  - **440px**, the wider of the two. Registration puts first and last name side by side, and a
 *    cramped name row is visible where 40px of extra breathing room on a two-field sign-in form
 *    is not.
 *  - **16px** between fields, the tighter. That was the value tuned on the page with five fields;
 *    18px was tuned on the page with two.
 *
 * `100dvh` replaces `100vh` here as it did in the app shell: on a phone `vh` is the viewport with
 * the URL bar hidden, so the column was always fractionally scrollable even when it fitted.
 */

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    minHeight: '100dvh',
    width: '100%',

    [atMedia.lg]: {
      flexDirection: 'row',
    },
  },

  formPanel: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'center',
    alignItems: 'center',
    padding: '32px 20px',
    backgroundColor: tokens.colorNeutralBackground1,
    minHeight: '100dvh',

    [atMedia.sm]: {
      padding: '40px 32px',
    },
    [atMedia.lg]: {
      padding: '64px 48px',
      minHeight: 'auto',
    },
  },

  formColumn: {
    width: '100%',
    maxWidth: '380px',

    [atMedia.lg]: {
      maxWidth: '440px',
    },
  },

  // The brand panel is hidden below lg, so this is the only place the mark appears on a phone.
  mobileBrand: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    marginBottom: '28px',

    [atMedia.lg]: {
      display: 'none',
    },
  },

  mobileWordmark: {
    fontSize: '22px',
    fontWeight: 700,
    color: tokens.colorBrandForeground1,
  },

  header: {
    marginBottom: '28px',
  },

  // `display: block` is load-bearing on both: Fluent's `Text` renders inline whatever element it
  // is asked for, so without it the title and the subtitle run together on one line.
  title: {
    display: 'block',
    fontSize: '26px',
    fontWeight: 600,
    lineHeight: '1.2',
    color: tokens.colorNeutralForeground1,
    marginBottom: '8px',

    [atMedia.sm]: {
      fontSize: '28px',
    },
  },

  subtitle: {
    display: 'block',
    fontSize: '14px',
    lineHeight: '1.5',
    color: tokens.colorNeutralForeground2,

    [atMedia.sm]: {
      fontSize: '15px',
    },
  },

  // The size and colour live here rather than in each page, so the slot's contents inherit them
  // and neither page needs a `footerText` class of its own.
  footer: {
    marginTop: '40px',
    textAlign: 'center',
    fontSize: '12px',
    color: tokens.colorNeutralForeground3,

    [atMedia.lg]: {
      marginTop: '48px',
    },
  },

  crossLink: {
    textAlign: 'center',
    fontSize: '14px',
    color: tokens.colorNeutralForeground2,
  },

  // ===== The divider between the form and its cross-link =====
  divider: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
    margin: '24px 0',
  },

  dividerLine: {
    flex: 1,
    height: '1px',
    backgroundColor: tokens.colorNeutralStroke2,
  },

  dividerLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: '13px',
  },
});

export interface AuthLayoutProps {
  /** The brand panel's headline. Desktop only — the panel does not render below 1024px. */
  headline: string;
  /** The sentence under the headline. */
  intro: string;
  /** The form's heading. This is the page's ONE `<h1>`, and the only one at every width. */
  title: string;
  /** The line under the heading. */
  subtitle: string;
  /**
   * Fine print beneath the form.
   *
   * A slot rather than a fixed string because the two pages put different KINDS of thing here —
   * sign-in a copyright, registration its terms line — which is why it could not simply be hoisted
   * with the rest. The copyright itself moved into the brand panel.
   */
  footer?: ReactNode;
  /** Banners, the form, and whatever follows it. */
  children: ReactNode;
}

export function AuthLayout({
  headline,
  intro,
  title,
  subtitle,
  footer,
  children,
}: AuthLayoutProps) {
  const styles = useStyles();

  return (
    <div className={styles.container}>
      <AuthBrandingPanel headline={headline} intro={intro} />

      <main className={styles.formPanel}>
        <div className={styles.formColumn}>
          <div className={styles.mobileBrand}>
            <Logo size={36} />
            <Text className={styles.mobileWordmark}>AzureBank</Text>
          </div>

          <div className={styles.header}>
            <Text as="h1" className={styles.title}>
              {title}
            </Text>
            <Text as="p" className={styles.subtitle}>
              {subtitle}
            </Text>
          </div>

          {children}

          {footer && <div className={styles.footer}>{footer}</div>}
        </div>
      </main>
    </div>
  );
}

/** The "or" rule between the form and its cross-link. Identical on both pages; declared once. */
export function AuthDivider({ label = 'or' }: { label?: string }) {
  const styles = useStyles();

  return (
    <div className={styles.divider}>
      <div className={styles.dividerLine} />
      <Text className={styles.dividerLabel}>{label}</Text>
      <div className={styles.dividerLine} />
    </div>
  );
}

/**
 * The link from one auth page to the other — "Don't have an account? Create account".
 *
 * Its two style keys were byte-identical in both pages, which is the same shape this module exists
 * to remove. The prompt and the link text differ; the box does not.
 */
export function AuthCrossLink({
  prompt,
  to,
  label,
}: {
  prompt: string;
  to: string;
  label: string;
}) {
  const styles = useStyles();

  // One element, not a styled box wrapping a styled `Text` — the size and colour inherit, so the
  // second copy of the class was doing nothing but claiming otherwise.
  return (
    <div className={styles.crossLink}>
      {prompt}{' '}
      <Link to={to} style={{ color: 'inherit' }}>
        <FluentLink as="span">{label}</FluentLink>
      </Link>
    </div>
  );
}

export default AuthLayout;
