import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useTheme } from '../theme/themeContext';
import type { ThemePreference } from '../theme/themePreference';
import {
  makeStyles,
  Text,
  Button,
  Badge,
  Radio,
  RadioGroup,
  tokens,
} from '@fluentui/react-components';
import {
  LockClosed24Regular,
  Alert24Regular,
  Globe24Regular,
  Link24Regular,
  SignOut24Regular,
} from '@fluentui/react-icons';
import { colors, shadows, gradients } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import { SIGN_OUT_FAILED } from '../api/problemMessages';
import { useAppSelector } from '../app/hooks';
import { useProblemToast } from '../components/feedback';
import { selectCurrentUser } from '../features/auth/authSlice';
import { useLogoutMutation } from '../features/api/apiSlice';
import { ChangePinDialog, RenameAzureTagDialog } from '../components';

// Features with a designed home here but no backend yet — shown as disabled "Coming soon" rows so
// the page is honest about the roadmap instead of pretending dead controls work. This used to end
// "The UI/UX overhaul turns these real", written over five rows. One has become real since: dark
// mode, at U7 (2026-07-30), and it shipped without a backend change, because the theme preference
// lives in localStorage. The four below still have no endpoint in the API contract.
const COMING_SOON = [
  {
    id: 'security',
    icon: <LockClosed24Regular />,
    title: 'Password & two-factor',
    subtitle: 'Change your password, enable 2FA',
  },
  {
    id: 'notifications',
    icon: <Alert24Regular />,
    title: 'Notifications',
    subtitle: 'Push, email, and SMS preferences',
  },
  {
    id: 'language',
    icon: <Globe24Regular />,
    title: 'Language',
    subtitle: 'Choose your language',
  },
  {
    id: 'linked',
    icon: <Link24Regular />,
    title: 'Linked accounts',
    subtitle: 'Connect external bank accounts',
  },
] as const;

const useStyles = makeStyles({
  container: {
    width: '100%',
    maxWidth: '760px',
    margin: '0 auto',
    padding: '24px 16px 48px',
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
  },

  pageTitle: {
    fontSize: '24px',
    fontWeight: 700,
    color: colors.neutral[800],
  },

  card: {
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: '16px',
    boxShadow: shadows.sm,
    overflow: 'hidden',
  },

  cardHeader: {
    padding: '18px 24px',
    borderBottom: `1px solid ${colors.neutral[200]}`,
  },

  cardTitle: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  cardBody: {
    padding: '24px',
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
  },

  // ===== Profile header =====
  profileHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
  },

  avatar: {
    width: '72px',
    height: '72px',
    borderRadius: '50%',
    background: gradients.primary,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
  },

  avatarInitials: {
    fontSize: '28px',
    fontWeight: 600,
    color: colors.brand[60],
  },

  /**
   * The same run-together defect as the sidebar, and `as="p"` is why it hid.
   *
   * Fluent's `Text` styles its root `display: inline` regardless of the element it renders, so
   * `as="p"` changed the TAG without changing the layout: name and email ran together as
   * "Demo Userdemo@azurebank.dev". The class is merged after Fluent's, so declaring the display
   * here is what actually settles it.
   */
  profileName: {
    display: 'block',
    fontSize: '20px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  // Beside the avatar, in a flex row: without a minimum width of zero this block is as wide as
  // its longest word, and an email address is one word.
  profileText: { minWidth: 0 },

  profileEmail: {
    display: 'block',
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
    // An address has no space to wrap at. A demo copy's is 39 characters, and at 375 px it ran
    // out of the card and past the screen's edge. `BreakableEmail` gives it one good place to
    // break; this is for an address that is too long even so.
    overflowWrap: 'anywhere',
  },

  // ===== Read-only identity grid =====
  fieldGrid: {
    display: 'grid',
    gridTemplateColumns: '1fr 1fr',
    gap: '16px',
    '@media (max-width: 599px)': {
      gridTemplateColumns: '1fr',
    },
  },

  field: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
  },

  fieldLabel: {
    fontSize: '13px',
    fontWeight: 500,
    color: colors.neutral[500],
  },

  fieldValue: {
    minHeight: '44px',
    backgroundColor: colors.neutral[50],
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '8px',
    padding: '0 14px',
    display: 'flex',
    alignItems: 'center',
    fontSize: '15px',
    color: colors.neutral[800],
    // The email is shown here too, and under 375 px it ran out of this box as it did above.
    overflowWrap: 'anywhere',
  },

  // ===== Handle row (editable) =====
  handleRow: {
    display: 'flex',
    alignItems: 'flex-end',
    justifyContent: 'space-between',
    gap: '16px',
  },

  handleValue: {
    fontFamily: 'Consolas, "Courier New", monospace',
  },

  handleHint: {
    fontSize: '13px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  // ===== Coming-soon rows =====
  comingRow: {
    display: 'flex',
    alignItems: 'center',
    gap: '14px',
    padding: '14px 0',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    ':last-child': {
      borderBottom: 'none',
    },
  },

  comingIcon: {
    width: '40px',
    height: '40px',
    borderRadius: '10px',
    backgroundColor: colors.neutral[100],
    color: colors.neutral[400],
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
  },

  comingText: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },

  comingTitle: {
    fontSize: '15px',
    fontWeight: 500,
    color: colors.neutral[500],
  },

  comingSubtitle: {
    fontSize: '13px',
    fontWeight: 400,
    color: colors.neutral[400],
  },

  // ===== Action rows (security, danger zone) =====
  actionRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '16px',
  },

  actionInfo: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  actionTitle: {
    fontSize: '15px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  actionSubtitle: {
    fontSize: '13px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  // On the canvas, not on a card: there the grey of the page's other sentences, neutral[500],
  // is 4.39 to 1, so this is the step above it. It was neutral[400], 2.31 to 1.
  version: {
    textAlign: 'center',
    fontSize: '12px',
    color: colors.neutral[600],
  },
});

/**
 * An email address that may break after its `@`. It has no space, so without this a browser
 * breaks it only where it has to, which can be one letter from the end.
 */
function BreakableEmail({ address }: { address: string }) {
  const afterAt = address.indexOf('@') + 1;
  return (
    <>
      {address.slice(0, afterAt)}
      <wbr />
      {address.slice(afterAt)}
    </>
  );
}

/**
 * Account settings. Identity (name / email) comes from the session and is read-only — the only
 * editable field is the public AzureTag handle (a payment @tag, not legal identity; ADR-0015).
 * Everything not yet backed by an endpoint is an honest "Coming soon" row rather than a dead
 * control. Logout is a real server-side session revocation.
 */
export function SettingsPage() {
  const styles = useStyles();
  const { preference, resolved, setPreference } = useTheme();
  const navigate = useNavigate();
  const [logout] = useLogoutMutation();
  const showProblem = useProblemToast();
  const [renameOpen, setRenameOpen] = useState(false);
  const [changePinOpen, setChangePinOpen] = useState(false);

  // Identity comes from the session — the shell shows the same user, and the two must never
  // disagree on one screen.
  const user = useAppSelector(selectCurrentUser);
  const displayName = user ? `${user.firstName} ${user.lastName}` : '';
  const displayEmail = user?.email ?? '';
  const displayInitials = user
    ? `${user.firstName.charAt(0)}${user.lastName.charAt(0)}`.toUpperCase()
    : '';

  const handleLogout = async () => {
    try {
      // Real server-side logout: revokes the BFF session and deletes the cookie. Navigation
      // happens ONLY on success — a failed revocation must never masquerade as a logout.
      await logout().unwrap();
      navigate('/login', { replace: true });
    } catch (caught) {
      // A 401 says the session was already gone, which sessionMiddleware takes from here. Any
      // other failure left the visitor signed in, and the toast says so before saying why.
      const problem = caught as ApiProblem;
      showProblem(problem, problem.status === 401 ? undefined : SIGN_OUT_FAILED);
    }
  };

  return (
    <div className={styles.container}>
      <Text as="h1" className={styles.pageTitle}>
        Settings
      </Text>

      {/* ===== Profile ===== */}
      <section className={styles.card}>
        <div className={styles.cardHeader}>
          <Text className={styles.cardTitle}>Profile</Text>
        </div>
        <div className={styles.cardBody}>
          <div className={styles.profileHeader}>
            <div className={styles.avatar}>
              <Text className={styles.avatarInitials}>{displayInitials}</Text>
            </div>
            <div className={styles.profileText}>
              <Text as="p" className={styles.profileName}>
                {displayName}
              </Text>
              <Text as="p" className={styles.profileEmail}>
                <BreakableEmail address={displayEmail} />
              </Text>
            </div>
          </div>

          <div className={styles.fieldGrid}>
            <div className={styles.field}>
              <Text className={styles.fieldLabel}>First name</Text>
              <div className={styles.fieldValue}>{user?.firstName ?? ''}</div>
            </div>
            <div className={styles.field}>
              <Text className={styles.fieldLabel}>Last name</Text>
              <div className={styles.fieldValue}>{user?.lastName ?? ''}</div>
            </div>
          </div>

          <div className={styles.field}>
            <Text className={styles.fieldLabel}>Email address</Text>
            <div className={styles.fieldValue}>
              {/* One element, so that the box, a flex row, has one item to lay out and not two. */}
              <span>
                <BreakableEmail address={displayEmail} />
              </span>
            </div>
          </div>

          {/* The one editable field: the public payment handle. */}
          <div className={styles.handleRow}>
            <div className={styles.field} style={{ flex: 1 }}>
              <Text className={styles.fieldLabel}>Public handle</Text>
              <div className={`${styles.fieldValue} ${styles.handleValue}`}>
                {`@${user?.azureTag ?? ''}`}
              </div>
              <Text className={styles.handleHint}>How other people find you to send money.</Text>
            </div>
            <Button appearance="secondary" onClick={() => setRenameOpen(true)} disabled={!user}>
              Change
            </Button>
          </div>
        </div>
      </section>

      {/* ===== Security ===== */}
      <section className={styles.card}>
        <div className={styles.cardHeader}>
          <Text className={styles.cardTitle}>Security</Text>
        </div>
        <div className={styles.cardBody}>
          {/*
            Change needs the current PIN; a user with none has nothing to change, and the dialog's
            request would be an enrolment without the password that enrolment costs (422
            PASSWORD_REQUIRED). So that user goes to the page that asks for it, and comes back.
          */}
          <div className={styles.actionRow}>
            <div className={styles.actionInfo}>
              <Text className={styles.actionTitle}>PIN</Text>
              <Text className={styles.actionSubtitle}>
                {user?.hasPin
                  ? 'Used to confirm withdrawals, transfers and account closures, and to show full account numbers.'
                  : 'Not set up yet.'}
              </Text>
            </div>
            {user?.hasPin ? (
              <Button appearance="secondary" onClick={() => setChangePinOpen(true)}>
                Change PIN
              </Button>
            ) : (
              <Button
                appearance="secondary"
                onClick={() => navigate('/pin-setup?returnTo=/settings')}
                disabled={!user}
              >
                Set up PIN
              </Button>
            )}
          </div>
        </div>
      </section>

      {/* ===== Appearance ===== */}
      <section className={styles.card}>
        <div className={styles.cardHeader}>
          <Text className={styles.cardTitle}>Appearance</Text>
        </div>
        <div className={styles.cardBody}>
          {/*
            Three options, not a switch. "Follow the system" is a real answer rather than the absence
            of one — a user who has never chosen wants their OS honoured, and a user who picked light
            on a dark machine wants that kept. A two-state toggle cannot tell those apart, and the
            one that loses is the person whose OS switches at sunset.
          */}
          <RadioGroup
            layout="horizontal"
            value={preference}
            onChange={(_, data) => setPreference(data.value as ThemePreference)}
            aria-label="Theme"
          >
            <Radio value="system" label="System" />
            <Radio value="light" label="Light" />
            <Radio value="dark" label="Dark" />
          </RadioGroup>
          {/* A sentence to read, so the grey of the page's other sentences, and not the fainter
              one of the disabled "Coming soon" rows it used to wear: 2.54 to 1 on the card. */}
          <Text className={styles.actionSubtitle}>
            {preference === 'system'
              ? `Following your device, which is currently ${resolved}.`
              : 'This device will stay on your choice.'}
          </Text>
        </div>
      </section>

      {/* ===== Coming soon ===== */}
      <section className={styles.card}>
        <div className={styles.cardHeader}>
          <Text className={styles.cardTitle}>More settings</Text>
        </div>
        <div className={styles.cardBody} style={{ gap: 0 }}>
          {COMING_SOON.map((item) => (
            <div key={item.id} className={styles.comingRow} aria-disabled="true">
              <div className={styles.comingIcon}>{item.icon}</div>
              <div className={styles.comingText}>
                <Text className={styles.comingTitle}>{item.title}</Text>
                <Text className={styles.comingSubtitle}>{item.subtitle}</Text>
              </div>
              <Badge appearance="outline" color="informative">
                Coming soon
              </Badge>
            </div>
          ))}
        </div>
      </section>

      {/* ===== Danger zone ===== */}
      <section className={styles.card}>
        <div className={styles.cardHeader}>
          <Text className={styles.cardTitle}>Danger zone</Text>
        </div>
        <div className={styles.cardBody}>
          <div className={styles.actionRow}>
            <div className={styles.actionInfo}>
              <Text className={styles.actionTitle}>Log out</Text>
              <Text className={styles.actionSubtitle}>
                Sign out of your account on this device.
              </Text>
            </div>
            <Button
              appearance="secondary"
              // The label in the red for words: `error.main`, which the border keeps, measured
              // 3.92 to 1 as 14 px text on the card in the light theme.
              style={{ borderColor: colors.semantic.error.main, color: colors.semantic.error.dark }}
              icon={<SignOut24Regular />}
              onClick={() => {
                void handleLogout();
              }}
            >
              Log out
            </Button>
          </div>
        </div>
      </section>

      <Text className={styles.version}>AzureBank v1.0.0</Text>

      {renameOpen && user && (
        <RenameAzureTagDialog currentTag={user.azureTag} onClose={() => setRenameOpen(false)} />
      )}
      {changePinOpen && <ChangePinDialog onClose={() => setChangePinOpen(false)} />}
    </div>
  );
}

export default SettingsPage;
