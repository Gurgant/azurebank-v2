import { Link } from 'react-router-dom';
import { makeStyles, Text, tokens } from '@fluentui/react-components';
import { ArrowLeft20Regular, Open16Regular } from '@fluentui/react-icons';
import { atMedia } from '../theme/breakpoints';
import { colors, surfaces } from '../theme/tokens';
import { Logo } from '../components/shared/Logo';
import { useAppSelector } from '../app/hooks';
import { selectAuthStatus } from '../features/auth/authSlice';

/**
 * What this project is, and who wrote it.
 *
 * ONE route, reached from two places that look different but are the same page:
 *
 *  - Signed in, from the avatar sheet / sidebar → renders inside the app shell, so the navigation
 *    is still there and there is no dead end.
 *  - Signed out, from the sign-in page's footer → renders standalone, because there is nothing to
 *    navigate; a "back to sign in" link is the way out.
 *
 * The shell wrapping happens at the route (App.tsx), not here — this file only decides whether to
 * offer the standalone back-link, which is the one thing that differs.
 *
 * **No email address and no phone number, deliberately.** This repository is public, so anything on
 * this page is scraped the day it ships. LinkedIn is the professional channel and it has its own
 * spam controls; a mailto here would just be a harvested inbox.
 *
 * This page is also where the app stops pretending. Everywhere else AzureBank behaves like a bank —
 * it has to, or it would not demonstrate anything — and the honest place to say so is the one
 * screen a person reaches when they want to know who is behind it.
 */

const LINKS = [
  { label: 'LinkedIn', href: 'https://www.linkedin.com/in/vladislav-aleshaev/' },
  { label: 'GitHub', href: 'https://github.com/Gurgant' },
] as const;

const useStyles = makeStyles({
  page: {
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
    padding: '24px 16px',
    maxWidth: '720px',
    marginLeft: 'auto',
    marginRight: 'auto',

    [atMedia.md]: {
      padding: '48px 32px',
    },
  },

  backLink: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    color: colors.brand[60],
    textDecoration: 'none',
    fontSize: '14px',
    width: 'fit-content',
    ':hover': { textDecoration: 'underline' },
  },

  brandRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },

  wordmark: {
    fontSize: '22px',
    fontWeight: 700,
    color: tokens.colorBrandForeground1,
  },

  title: {
    display: 'block',
    fontSize: '28px',
    fontWeight: 700,
    color: colors.neutral[900],
  },

  lede: {
    display: 'block',
    fontSize: '16px',
    lineHeight: 1.6,
    color: colors.neutral[700],
  },

  card: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${surfaces.border}`,
    borderRadius: '14px',
    padding: '20px',
  },

  cardTitle: {
    display: 'block',
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
    marginBottom: '8px',
  },

  body: {
    display: 'block',
    fontSize: '14px',
    lineHeight: 1.6,
    color: colors.neutral[700],
  },

  linkRow: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '12px',
    marginTop: '16px',
  },

  extLink: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    padding: '10px 16px',
    borderRadius: '10px',
    border: `1px solid ${surfaces.border}`,
    color: colors.brand[60],
    textDecoration: 'none',
    fontSize: '14px',
    fontWeight: 600,
    ':hover': { backgroundColor: colors.neutral[50] },
    ':focus-visible': { outline: `2px solid ${colors.brand[60]}`, outlineOffset: '2px' },
  },
});

export function AboutPage() {
  const styles = useStyles();
  const isAuthenticated = useAppSelector(selectAuthStatus) === 'authenticated';

  return (
    <div className={styles.page}>
      {/* Only when standalone. Signed in, the shell's own navigation is the way back, and a second
          back-link beside a live sidebar is noise. */}
      {!isAuthenticated && (
        <Link to="/login" className={styles.backLink}>
          <ArrowLeft20Regular />
          Back to sign in
        </Link>
      )}

      <div className={styles.brandRow}>
        <Logo size={40} />
        <Text className={styles.wordmark}>AzureBank</Text>
      </div>

      <Text as="h1" className={styles.title}>
        About this project
      </Text>

      <Text as="p" className={styles.lede}>
        AzureBank is a portfolio project, not a real bank. No money moves, no account here belongs
        to anyone, and every balance on the screen is generated. It exists to demonstrate how a
        banking application is built end to end.
      </Text>

      <section className={styles.card}>
        <Text className={styles.cardTitle}>What it is made of</Text>
        <Text as="p" className={styles.body}>
          A .NET API and a BFF that holds the session so the browser never sees a token, with a
          React front end. The parts that are usually skipped in a demo are the ones it takes most
          seriously: idempotent money operations, step-up authentication before a transfer, and
          guards that stop a form being submitted twice.
        </Text>
      </section>

      <section className={styles.card}>
        <Text className={styles.cardTitle}>Who wrote it</Text>
        <Text as="p" className={styles.body}>
          Vladislav Aleshaev — sole developer. The code is public and so is the history, including
          the mistakes and what they cost.
        </Text>
        <div className={styles.linkRow}>
          {LINKS.map((link) => (
            <a
              key={link.label}
              className={styles.extLink}
              href={link.href}
              target="_blank"
              rel="noreferrer noopener"
            >
              {link.label}
              <Open16Regular />
            </a>
          ))}
        </div>
      </section>
    </div>
  );
}

export default AboutPage;
