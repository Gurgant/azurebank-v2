import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { makeStyles, Text, Button, Badge } from '@fluentui/react-components';
import {
  LockClosed24Regular,
  Alert24Regular,
  WeatherMoon24Regular,
  Globe24Regular,
  Link24Regular,
  SignOut24Regular,
} from '@fluentui/react-icons';
import { colors, shadows, gradients } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import { useAppSelector } from '../app/hooks';
import { useProblemToast } from '../components/feedback';
import { selectCurrentUser } from '../features/auth/authSlice';
import { useLogoutMutation } from '../features/api/apiSlice';
import { RenameAzureTagDialog } from '../components';

// Features with a designed home here but no backend yet — shown as disabled "Coming soon" rows so
// the page is honest about the roadmap instead of pretending dead controls work. The UI/UX overhaul
// turns these real.
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
    id: 'appearance',
    icon: <WeatherMoon24Regular />,
    title: 'Dark mode',
    subtitle: 'Switch to a dark theme',
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
    backgroundColor: '#FFFFFF',
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

  profileName: {
    fontSize: '20px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  profileEmail: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
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

  // ===== Danger zone =====
  dangerRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '16px',
  },

  dangerInfo: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  dangerTitle: {
    fontSize: '15px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  dangerSubtitle: {
    fontSize: '13px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  version: {
    textAlign: 'center',
    fontSize: '12px',
    color: colors.neutral[400],
  },
});

/**
 * Account settings. Identity (name / email) comes from the session and is read-only — the only
 * editable field is the public AzureTag handle (a payment @tag, not legal identity; ADR-0015).
 * Everything not yet backed by an endpoint is an honest "Coming soon" row rather than a dead
 * control. Logout is a real server-side session revocation.
 */
export function SettingsPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const [logout] = useLogoutMutation();
  const showProblem = useProblemToast();
  const [renameOpen, setRenameOpen] = useState(false);

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
      showProblem(caught as ApiProblem);
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
            <div>
              <Text as="p" className={styles.profileName}>
                {displayName}
              </Text>
              <Text as="p" className={styles.profileEmail}>
                {displayEmail}
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
            <div className={styles.fieldValue}>{displayEmail}</div>
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
          <div className={styles.dangerRow}>
            <div className={styles.dangerInfo}>
              <Text className={styles.dangerTitle}>Log out</Text>
              <Text className={styles.dangerSubtitle}>
                Sign out of your account on this device.
              </Text>
            </div>
            <Button
              appearance="secondary"
              style={{ borderColor: colors.semantic.error.main, color: colors.semantic.error.main }}
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
    </div>
  );
}

export default SettingsPage;
