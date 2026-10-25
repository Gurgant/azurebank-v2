import { Link } from 'react-router-dom';
import { useId } from 'react';
import { makeStyles, Text, tokens } from '@fluentui/react-components';
import { ArrowLeft20Regular, Open16Regular } from '@fluentui/react-icons';
import { atMedia } from '../theme/breakpoints';
import { colors, surfaces } from '../theme/tokens';
import { Logo } from '../components/shared/Logo';
import { SectionTitle } from '../components/shared/SectionTitle';
import { useAppSelector } from '../app/hooks';
import { selectAuthStatus } from '../features/auth/authSlice';

/**
 * Who I am and how to reach me, then what the project is.
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
 * spam controls; a mailto here would just be a harvested inbox. A security problem goes to GitHub's
 * private vulnerability reporting, the channel SECURITY.md names, and never to a public issue.
 *
 * This page is also where the app stops pretending. Everywhere else AzureBank behaves like a bank —
 * it has to, or it would not demonstrate anything — and the honest place to say so is the one
 * screen a person reaches when they want to know who is behind it.
 */

const LINKS = [
  { label: 'LinkedIn profile', href: 'https://www.linkedin.com/in/vladislav-aleshaev/' },
  { label: 'GitHub profile', href: 'https://github.com/Gurgant' },
  { label: 'Source code', href: 'https://github.com/Gurgant/azurebank-v2' },
] as const;

const SECURITY_HREF = 'https://github.com/Gurgant/azurebank-v2/security';

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

  // A column, because a card now holds more than one paragraph and the reset in `index.css` gives
  // a `<p>` no margin of its own. The gap spaces the title too, so the title takes no margin: one
  // would add to the gap.
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${surfaces.border}`,
    borderRadius: '14px',
    padding: '20px',
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
    marginTop: tokens.spacingVerticalS,
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

  // A link inside a sentence is underlined, which the reset in `index.css` takes off every link:
  // by colour alone it measured 1.71:1 against the words around it, and WCAG 1.4.1 asks for 3:1
  // or a mark that is not colour. Fluent's link colour, not the brand's: this line sits outside
  // the cards, on the shell's grey canvas when signed in, where the brand blue measured 4.42:1.
  // Hover and focus as on the not-found page's link.
  inlineLink: {
    color: tokens.colorBrandForegroundLink,
    textDecorationLine: 'underline',
    ':hover': { color: tokens.colorBrandForegroundLinkHover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '2px' },
  },
});

export function ContactPage() {
  const styles = useStyles();
  const isAuthenticated = useAppSelector(selectAuthStatus) === 'authenticated';
  const meId = useId();
  const projectId = useId();

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
        Contact
      </Text>

      <section className={styles.card} aria-labelledby={meId}>
        <SectionTitle id={meId}>Vladislav Aleshaev</SectionTitle>
        <Text as="p" className={styles.body}>
          Full-stack engineer (.NET, React, Azure)
        </Text>
        <Text as="p" className={styles.body}>
          I built AzureBank as my portfolio project. I designed the system, wrote part of the code
          myself and direct the AI coding agents that write the rest. I read the code I merge and I
          merge every pull request myself.
        </Text>
        <Text as="p" className={styles.body}>
          The quickest way to reach me is LinkedIn.
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

      <section className={styles.card} aria-labelledby={projectId}>
        <SectionTitle id={projectId}>About this project</SectionTitle>
        <Text as="p" className={styles.lede}>
          AzureBank is a portfolio project, not a real bank. No money moves, no account here belongs
          to anyone, and every balance on the screen is generated. It exists to demonstrate how a
          banking application is built end to end.
        </Text>
        <Text as="p" className={styles.body}>
          A .NET API and a BFF that holds the session so the browser never sees a token, with a
          React front end. The parts that are usually skipped in a demo are the ones it takes most
          seriously: idempotent money operations, step-up authentication before a transfer, and
          guards that stop a form being submitted twice.
        </Text>
      </section>

      <Text as="p" className={styles.body}>
        Found a security problem? Please report it privately through GitHub&apos;s{' '}
        <a
          className={styles.inlineLink}
          href={SECURITY_HREF}
          target="_blank"
          rel="noreferrer noopener"
        >
          private vulnerability reporting
        </a>
        .
      </Text>
    </div>
  );
}

export default ContactPage;
