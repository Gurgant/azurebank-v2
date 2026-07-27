import { makeStyles, Text, tokens } from '@fluentui/react-components';
import {
  Globe24Regular,
  LockClosed24Regular,
  ShieldCheckmark24Regular,
} from '@fluentui/react-icons';
import { atMedia } from '../../theme/breakpoints';
import { gradients } from '../../theme/tokens';
import { Logo } from '../shared/Logo';

/**
 * The brand half of the auth screens — everything left of the form.
 *
 * It was written twice, once in `LoginPage` and once in `RegisterPage`, and the two copies had
 * already begun to disagree. Only the headline and the sentence under it ever differed; the logo,
 * the three feature rows and the frame were identical, so those are props and the rest is here.
 *
 * **It does not exist below 1024px**, and that single fact decides more than it looks like — see
 * the heading note below.
 */

const FEATURES = [
  { Icon: ShieldCheckmark24Regular, label: 'Bank-grade security protection' },
  { Icon: LockClosed24Regular, label: 'Instant transfers & payments' },
  { Icon: Globe24Regular, label: '24/7 account access' },
] as const;

const useStyles = makeStyles({
  /**
   * `gradients.brand` rather than the flat `colorBrandBackground` this shipped with. Both stops of
   * that gradient are brand-ramp values chosen so white text clears contrast on either end, and a
   * 64px-tall panel is one of the few surfaces in the app big enough for a gradient to read as
   * anything at all.
   */
  panel: {
    display: 'none',

    [atMedia.lg]: {
      display: 'flex',
      flexDirection: 'column',
      justifyContent: 'center',
      alignItems: 'flex-start',
      width: '50%',
      minWidth: '480px',
      maxWidth: '720px',
      background: gradients.brand,
      padding: '64px 80px',
    },
  },

  brandRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    marginBottom: '48px',
  },

  wordmark: {
    fontSize: '32px',
    fontWeight: 700,
    color: tokens.colorNeutralForegroundOnBrand,
  },

  /**
   * A `<p>`, deliberately, where both pages had an `<h1>`.
   *
   * This panel is `display: none` below 1024px, so an `<h1>` here is a heading that exists on
   * desktop and vanishes on a phone — and both pages ALSO marked their form title as `<h1>`, which
   * gave the desktop two level-one headings and made neither of them the page's subject. The form
   * is what the page is for and it renders at every width, so it takes the `<h1>`; this is a
   * marketing line in decorative chrome, and it is styled like a headline rather than declared as
   * one. (The original component this is modelled on made the opposite call, which would leave a
   * phone with no `<h1>` at all.)
   */
  headline: {
    display: 'block',
    fontSize: '44px',
    fontWeight: 700,
    lineHeight: '1.15',
    marginBottom: '20px',
    maxWidth: '480px',
    color: tokens.colorNeutralForegroundOnBrand,
  },

  intro: {
    display: 'block',
    fontSize: '17px',
    opacity: 0.9,
    marginBottom: '48px',
    maxWidth: '440px',
    lineHeight: '1.6',
    color: tokens.colorNeutralForegroundOnBrand,
  },

  features: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },

  featureRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '14px',
  },

  // The one translucent tint in the app, and it has to be one: a solid token cannot sit on a
  // gradient without banding against one of its stops. Two copies of this literal became one here.
  featureTile: {
    width: '42px',
    height: '42px',
    backgroundColor: 'rgba(255, 255, 255, 0.15)',
    borderRadius: '10px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: tokens.colorNeutralForegroundOnBrand,
    flexShrink: 0,
  },

  featureLabel: {
    display: 'block',
    fontSize: '15px',
    fontWeight: 500,
    color: tokens.colorNeutralForegroundOnBrand,
  },

  footer: {
    marginTop: 'auto',
    paddingTop: '48px',
  },

  copyright: {
    display: 'block',
    fontSize: '12px',
    opacity: 0.6,
    color: tokens.colorNeutralForegroundOnBrand,
  },
});

export interface AuthBrandingPanelProps {
  /** The headline. Styled like a heading; deliberately not marked up as one. */
  headline: string;
  /** The sentence beneath it. */
  intro: string;
}

export function AuthBrandingPanel({ headline, intro }: AuthBrandingPanelProps) {
  const styles = useStyles();

  return (
    <aside className={styles.panel}>
      <div className={styles.brandRow}>
        <Logo size={48} />
        <Text className={styles.wordmark}>AzureBank</Text>
      </div>

      <Text as="p" className={styles.headline}>
        {headline}
      </Text>
      <Text as="p" className={styles.intro}>
        {intro}
      </Text>

      <div className={styles.features}>
        {FEATURES.map(({ Icon, label }) => (
          <div key={label} className={styles.featureRow}>
            <div className={styles.featureTile}>
              <Icon />
            </div>
            <Text className={styles.featureLabel}>{label}</Text>
          </div>
        ))}
      </div>

      {/* The year is read, not typed. It said 2024 on the sign-in page — a date that was wrong
          within months of being written, on the first screen anyone sees. It also lives here now
          rather than under the form, which frees that slot for what each page actually needs
          there: sign-in had the copyright, registration has the terms line. */}
      <div className={styles.footer}>
        <Text className={styles.copyright}>
          © {new Date().getFullYear()} AzureBank. All rights reserved.
        </Text>
      </div>
    </aside>
  );
}

export default AuthBrandingPanel;
