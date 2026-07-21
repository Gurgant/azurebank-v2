import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { makeStyles, Text, Button, Switch } from '@fluentui/react-components';
import {
  Person24Regular,
  LockClosed24Regular,
  Link24Regular,
  Alert24Regular,
  WeatherMoon24Regular,
  Fingerprint24Regular,
  Globe24Regular,
  QuestionCircle24Regular,
  Chat24Regular,
  Shield24Regular,
  Document24Regular,
  SignOut24Regular,
  ChevronRight20Regular,
  Camera20Regular,
  ArrowDownload20Regular,
} from '@fluentui/react-icons';
import { colors, shadows, gradients, transitions } from '../theme/tokens';
import type { ApiProblem } from '../api/problemBaseQuery';
import { useAppSelector } from '../app/hooks';
import { useProblemToast } from '../components/feedback';
import { selectCurrentUser } from '../features/auth/authSlice';
import { useLogoutMutation } from '../features/api/apiSlice';

// ============================================
// TYPES
// ============================================

type SettingsSection = 'profile' | 'security' | 'notifications' | 'preferences' | 'linked';

interface SettingsItem {
  id: string;
  title: string;
  subtitle: string;
  icon: React.ReactNode;
  iconColor: 'blue' | 'green' | 'purple' | 'orange' | 'red';
  type: 'link' | 'toggle';
  value?: boolean;
}

// ============================================
// MOCK DATA
// ============================================

// FICTION ONLY — fields with no backend counterpart, scheduled for pruning in the
// settings-content rewrite. Identity (name/email/initials) comes from the session.
const mockUser = {
  phone: '+1 (555) 123-4567',
  dateOfBirth: 'March 15, 1990',
  country: 'United States',
  memberSince: 'January 2024',
};

// ============================================
// STYLES
// ============================================

const useStyles = makeStyles({
  container: {
    minHeight: '100vh',
    backgroundColor: colors.neutral[50],
    display: 'flex',
    flexDirection: 'column',
  },

  // ========== MOBILE HEADER ==========
  mobileHeader: {
    display: 'flex',
    alignItems: 'center',
    padding: '12px 16px',
    backgroundColor: '#FFFFFF',
    borderBottom: `1px solid ${colors.neutral[200]}`,
    '@media (min-width: 1024px)': {
      display: 'none',
    },
  },

  headerTitle: {
    fontSize: '18px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  // ========== MAIN LAYOUT ==========
  mainLayout: {
    flex: 1,
    display: 'flex',
    '@media (min-width: 1024px)': {
      flexDirection: 'row',
    },
  },

  // ========== DESKTOP SIDEBAR ==========
  desktopSidebar: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      flexDirection: 'column',
      width: '280px',
      backgroundColor: '#FFFFFF',
      borderRight: `1px solid ${colors.neutral[200]}`,
      padding: '24px 16px',
      gap: '8px',
    },
  },

  sidebarTitle: {
    fontSize: '12px',
    fontWeight: 600,
    color: colors.neutral[500],
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    padding: '8px 12px',
  },

  sidebarItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '12px',
    borderRadius: '8px',
    cursor: 'pointer',
    border: 'none',
    background: 'none',
    width: '100%',
    textAlign: 'left',
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  sidebarItemActive: {
    backgroundColor: colors.brand[130],
  },

  sidebarIcon: {
    width: '20px',
    height: '20px',
    color: colors.neutral[500],
  },

  sidebarIconActive: {
    color: colors.brand[60],
  },

  sidebarLabel: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  sidebarLabelActive: {
    color: colors.brand[60],
  },

  // ========== MOBILE CONTENT ==========
  mobileContent: {
    flex: 1,
    padding: '16px',
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    overflowY: 'auto',
    '@media (min-width: 1024px)': {
      display: 'none',
    },
  },

  // ========== PROFILE SECTION (Mobile) ==========
  profileSection: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    padding: '20px',
    display: 'flex',
    alignItems: 'center',
    gap: '16px',
    cursor: 'pointer',
  },

  profileAvatar: {
    width: '64px',
    height: '64px',
    borderRadius: '50%',
    background: gradients.primary,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },

  profileAvatarInitials: {
    fontSize: '24px',
    fontWeight: 600,
    color: colors.brand[60],
  },

  profileInfo: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  profileName: {
    fontSize: '18px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  profileEmail: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  profileChevron: {
    color: colors.neutral[400],
  },

  // ========== SETTINGS GROUP ==========
  settingsGroup: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    overflow: 'hidden',
  },

  groupTitle: {
    fontSize: '12px',
    fontWeight: 600,
    color: colors.neutral[500],
    textTransform: 'uppercase',
    letterSpacing: '0.5px',
    padding: '16px 16px 8px 16px',
  },

  settingsItem: {
    display: 'flex',
    alignItems: 'center',
    padding: '16px',
    cursor: 'pointer',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    ':last-child': {
      borderBottom: 'none',
    },
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  itemIconContainer: {
    width: '40px',
    height: '40px',
    borderRadius: '10px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    marginRight: '12px',
  },

  iconBlue: {
    backgroundColor: colors.brand[130],
    color: colors.brand[60],
  },

  iconGreen: {
    backgroundColor: colors.semantic.success.light,
    color: colors.semantic.success.main,
  },

  iconPurple: {
    backgroundColor: '#F3E8FF',
    color: '#8B5CF6',
  },

  iconOrange: {
    backgroundColor: colors.semantic.warning.light,
    color: colors.semantic.warning.main,
  },

  iconRed: {
    backgroundColor: colors.semantic.error.light,
    color: colors.semantic.error.main,
  },

  itemContent: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },

  itemTitle: {
    fontSize: '15px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  itemSubtitle: {
    fontSize: '13px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  itemChevron: {
    color: colors.neutral[400],
  },

  // ========== LOGOUT BUTTON ==========
  logoutButton: {
    width: '100%',
    height: '52px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.semantic.error.main}`,
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    gap: '10px',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.semantic.error.light,
    },
  },

  logoutText: {
    fontSize: '15px',
    fontWeight: 600,
    color: colors.semantic.error.main,
  },

  versionInfo: {
    textAlign: 'center',
    padding: '16px',
  },

  versionText: {
    fontSize: '12px',
    fontWeight: 400,
    color: colors.neutral[400],
  },

  // ========== DESKTOP CONTENT AREA ==========
  desktopContentArea: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'block',
      flex: 1,
      padding: '32px',
      overflowY: 'auto',
    },
  },

  pageTitle: {
    fontSize: '24px',
    fontWeight: 700,
    color: colors.neutral[800],
    marginBottom: '24px',
  },

  settingsGrid: {
    display: 'grid',
    gridTemplateColumns: '1fr 400px',
    gap: '24px',
  },

  settingsMain: {
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
  },

  settingsCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    boxShadow: shadows.sm,
    overflow: 'hidden',
  },

  cardHeader: {
    padding: '20px 24px',
    borderBottom: `1px solid ${colors.neutral[200]}`,
  },

  cardTitle: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  cardContent: {
    padding: '24px',
  },

  // ========== PROFILE HEADER (Desktop) ==========
  profileHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '24px',
    paddingBottom: '24px',
    borderBottom: `1px solid ${colors.neutral[200]}`,
    marginBottom: '24px',
  },

  profileAvatarLarge: {
    width: '96px',
    height: '96px',
    borderRadius: '50%',
    background: gradients.primary,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },

  avatarInitialsLarge: {
    fontSize: '36px',
    fontWeight: 600,
    color: colors.brand[60],
  },

  profileDetails: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },

  profileNameLarge: {
    fontSize: '24px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  profileEmailLarge: {
    fontSize: '16px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  profileMemberSince: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[400],
  },

  changeAvatarBtn: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '10px 16px',
    backgroundColor: '#FFFFFF',
    border: `1px solid ${colors.neutral[300]}`,
    borderRadius: '8px',
    cursor: 'pointer',
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  changeAvatarText: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  // ========== FORM ==========
  formRow: {
    display: 'flex',
    gap: '24px',
    marginBottom: '20px',
    ':last-child': {
      marginBottom: 0,
    },
  },

  formField: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },

  formLabel: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  formInput: {
    width: '100%',
    height: '48px',
    backgroundColor: colors.neutral[50],
    border: `1px solid ${colors.neutral[200]}`,
    borderRadius: '8px',
    padding: '0 16px',
    display: 'flex',
    alignItems: 'center',
  },

  inputValue: {
    fontSize: '15px',
    fontWeight: 400,
    color: colors.neutral[800],
  },

  // ========== SETTING ROW ==========
  settingRow: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '16px 0',
    borderBottom: `1px solid ${colors.neutral[100]}`,
    ':last-child': {
      borderBottom: 'none',
    },
  },

  settingInfo: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },

  settingTitle: {
    fontSize: '15px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  settingDescription: {
    fontSize: '13px',
    fontWeight: 400,
    color: colors.neutral[500],
  },

  // ========== CARD ACTIONS ==========
  cardActions: {
    display: 'flex',
    gap: '12px',
    padding: '16px 24px',
    borderTop: `1px solid ${colors.neutral[200]}`,
    backgroundColor: colors.neutral[50],
  },

  // ========== SIDEBAR RIGHT ==========
  settingsSidebar: {
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
  },

  quickActionsCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: '16px',
    boxShadow: shadows.sm,
    padding: '24px',
  },

  quickActionsTitle: {
    fontSize: '16px',
    fontWeight: 600,
    color: colors.neutral[800],
    marginBottom: '16px',
  },

  quickActionList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },

  quickActionItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    padding: '12px',
    borderRadius: '8px',
    cursor: 'pointer',
    border: 'none',
    background: 'none',
    width: '100%',
    textAlign: 'left',
    transition: `all ${transitions.fast}`,
    ':hover': {
      backgroundColor: colors.neutral[50],
    },
  },

  quickActionIcon: {
    width: '36px',
    height: '36px',
    borderRadius: '8px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },

  quickActionLabel: {
    flex: 1,
    fontSize: '14px',
    fontWeight: 500,
    color: colors.neutral[800],
  },

  quickActionChevron: {
    color: colors.neutral[400],
  },

  // ========== HELP CARD ==========
  helpCard: {
    backgroundColor: colors.brand[140],
    border: `1px solid ${colors.brand[120]}`,
    borderRadius: '12px',
    padding: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },

  helpHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
  },

  helpIcon: {
    width: '24px',
    height: '24px',
    color: colors.brand[60],
  },

  helpTitle: {
    fontSize: '15px',
    fontWeight: 600,
    color: colors.neutral[800],
  },

  helpText: {
    fontSize: '14px',
    fontWeight: 400,
    color: colors.neutral[500],
    lineHeight: '1.5',
  },

  helpLink: {
    fontSize: '14px',
    fontWeight: 500,
    color: colors.brand[60],
    cursor: 'pointer',
    ':hover': {
      textDecoration: 'underline',
    },
  },
});

// ============================================
// SETTINGS DATA
// ============================================

const accountSettings: SettingsItem[] = [
  {
    id: 'personal',
    title: 'Personal Information',
    subtitle: 'Name, email, phone',
    icon: <Person24Regular />,
    iconColor: 'blue',
    type: 'link',
  },
  {
    id: 'security',
    title: 'Security',
    subtitle: 'Password, biometrics, 2FA',
    icon: <LockClosed24Regular />,
    iconColor: 'green',
    type: 'link',
  },
  {
    id: 'linked',
    title: 'Linked Accounts',
    subtitle: 'External bank accounts',
    icon: <Link24Regular />,
    iconColor: 'purple',
    type: 'link',
  },
];

const preferenceSettings: SettingsItem[] = [
  {
    id: 'notifications',
    title: 'Notifications',
    subtitle: 'Push, email, SMS',
    icon: <Alert24Regular />,
    iconColor: 'orange',
    type: 'link',
  },
  {
    id: 'darkMode',
    title: 'Dark Mode',
    subtitle: 'Reduce eye strain',
    icon: <WeatherMoon24Regular />,
    iconColor: 'blue',
    type: 'toggle',
    value: false,
  },
  {
    id: 'biometrics',
    title: 'Face ID / Touch ID',
    subtitle: 'Quick login',
    icon: <Fingerprint24Regular />,
    iconColor: 'green',
    type: 'toggle',
    value: true,
  },
  {
    id: 'language',
    title: 'Language',
    subtitle: 'English (US)',
    icon: <Globe24Regular />,
    iconColor: 'purple',
    type: 'link',
  },
];

const supportSettings: SettingsItem[] = [
  {
    id: 'help',
    title: 'Help Center',
    subtitle: 'FAQs and guides',
    icon: <QuestionCircle24Regular />,
    iconColor: 'blue',
    type: 'link',
  },
  {
    id: 'contact',
    title: 'Contact Us',
    subtitle: 'Chat, call, or email',
    icon: <Chat24Regular />,
    iconColor: 'green',
    type: 'link',
  },
  {
    id: 'privacy',
    title: 'Privacy Policy',
    subtitle: 'How we use your data',
    icon: <Shield24Regular />,
    iconColor: 'orange',
    type: 'link',
  },
  {
    id: 'terms',
    title: 'Terms of Service',
    subtitle: 'Legal agreements',
    icon: <Document24Regular />,
    iconColor: 'purple',
    type: 'link',
  },
];

// ============================================
// COMPONENT
// ============================================

export function SettingsPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const [logout] = useLogoutMutation();
  const showProblem = useProblemToast();

  // IDENTITY comes from the session (the shell shows the same user — the two must
  // never disagree on one screen). The remaining mockUser fields (phone, date of
  // birth, country, member-since) are fabrications scheduled for pruning in the
  // settings-content rewrite.
  const user = useAppSelector(selectCurrentUser);
  const displayName = user ? `${user.firstName} ${user.lastName}` : '';
  const displayEmail = user?.email ?? '';
  const displayInitials = user
    ? `${user.firstName.charAt(0)}${user.lastName.charAt(0)}`.toUpperCase()
    : '';

  const [activeSection, setActiveSection] = useState<SettingsSection>('profile');
  const [darkMode, setDarkMode] = useState(false);
  const [biometrics, setBiometrics] = useState(true);
  const [showBalances, setShowBalances] = useState(true);
  const [compactView, setCompactView] = useState(false);

  const handleLogout = async () => {
    try {
      // Real server-side logout: revokes the BFF session and deletes the cookie.
      // Navigation happens ONLY on success — a failed revocation must never
      // masquerade as a logout (the cookie would still be alive behind a login
      // screen). Mirrors ProtectedShell.
      await logout().unwrap();
      navigate('/login', { replace: true });
    } catch (caught) {
      showProblem(caught as ApiProblem);
    }
  };

  const getIconClass = (color: string) => {
    switch (color) {
      case 'blue':
        return styles.iconBlue;
      case 'green':
        return styles.iconGreen;
      case 'purple':
        return styles.iconPurple;
      case 'orange':
        return styles.iconOrange;
      case 'red':
        return styles.iconRed;
      default:
        return styles.iconBlue;
    }
  };

  const renderSettingsItem = (item: SettingsItem) => (
    <div key={item.id} className={styles.settingsItem}>
      <div className={`${styles.itemIconContainer} ${getIconClass(item.iconColor)}`}>
        {item.icon}
      </div>
      <div className={styles.itemContent}>
        <Text className={styles.itemTitle}>{item.title}</Text>
        <Text className={styles.itemSubtitle}>{item.subtitle}</Text>
      </div>
      {item.type === 'toggle' ? (
        <Switch
          checked={item.id === 'darkMode' ? darkMode : biometrics}
          onChange={(_, data) => {
            if (item.id === 'darkMode') setDarkMode(data.checked);
            else setBiometrics(data.checked);
          }}
        />
      ) : (
        <ChevronRight20Regular className={styles.itemChevron} />
      )}
    </div>
  );

  return (
    <div className={styles.container}>
      {/* Mobile PAGE-TITLE bar — NOT a shell duplicate: the shared shell renders no
          mobile header (ProtectedShell passes no `header` prop to AppLayout), so this
          is the page's own title, the mobile counterpart of desktop page headings. */}
      <div className={styles.mobileHeader}>
        <Text className={styles.headerTitle}>Settings</Text>
      </div>

      {/* Main Layout — the app shell (nav) is provided by ProtectedShell */}
      <div className={styles.mainLayout}>
        {/* Desktop Sidebar */}
        <div className={styles.desktopSidebar}>
          <Text className={styles.sidebarTitle}>Settings</Text>
          <button
            className={`${styles.sidebarItem} ${activeSection === 'profile' ? styles.sidebarItemActive : ''}`}
            onClick={() => setActiveSection('profile')}
          >
            <Person24Regular
              className={`${styles.sidebarIcon} ${activeSection === 'profile' ? styles.sidebarIconActive : ''}`}
            />
            <Text
              className={`${styles.sidebarLabel} ${activeSection === 'profile' ? styles.sidebarLabelActive : ''}`}
            >
              Profile
            </Text>
          </button>
          <button
            className={`${styles.sidebarItem} ${activeSection === 'security' ? styles.sidebarItemActive : ''}`}
            onClick={() => setActiveSection('security')}
          >
            <LockClosed24Regular
              className={`${styles.sidebarIcon} ${activeSection === 'security' ? styles.sidebarIconActive : ''}`}
            />
            <Text
              className={`${styles.sidebarLabel} ${activeSection === 'security' ? styles.sidebarLabelActive : ''}`}
            >
              Security
            </Text>
          </button>
          <button
            className={`${styles.sidebarItem} ${activeSection === 'notifications' ? styles.sidebarItemActive : ''}`}
            onClick={() => setActiveSection('notifications')}
          >
            <Alert24Regular
              className={`${styles.sidebarIcon} ${activeSection === 'notifications' ? styles.sidebarIconActive : ''}`}
            />
            <Text
              className={`${styles.sidebarLabel} ${activeSection === 'notifications' ? styles.sidebarLabelActive : ''}`}
            >
              Notifications
            </Text>
          </button>
          <button
            className={`${styles.sidebarItem} ${activeSection === 'preferences' ? styles.sidebarItemActive : ''}`}
            onClick={() => setActiveSection('preferences')}
          >
            <WeatherMoon24Regular
              className={`${styles.sidebarIcon} ${activeSection === 'preferences' ? styles.sidebarIconActive : ''}`}
            />
            <Text
              className={`${styles.sidebarLabel} ${activeSection === 'preferences' ? styles.sidebarLabelActive : ''}`}
            >
              Preferences
            </Text>
          </button>
          <button
            className={`${styles.sidebarItem} ${activeSection === 'linked' ? styles.sidebarItemActive : ''}`}
            onClick={() => setActiveSection('linked')}
          >
            <Link24Regular
              className={`${styles.sidebarIcon} ${activeSection === 'linked' ? styles.sidebarIconActive : ''}`}
            />
            <Text
              className={`${styles.sidebarLabel} ${activeSection === 'linked' ? styles.sidebarLabelActive : ''}`}
            >
              Linked Accounts
            </Text>
          </button>

          <Text className={styles.sidebarTitle} style={{ marginTop: '16px' }}>
            Support
          </Text>
          <button className={styles.sidebarItem}>
            <QuestionCircle24Regular className={styles.sidebarIcon} />
            <Text className={styles.sidebarLabel}>Help Center</Text>
          </button>
          <button className={styles.sidebarItem}>
            <Chat24Regular className={styles.sidebarIcon} />
            <Text className={styles.sidebarLabel}>Contact Us</Text>
          </button>
          <button className={styles.sidebarItem}>
            <Shield24Regular className={styles.sidebarIcon} />
            <Text className={styles.sidebarLabel}>Privacy & Terms</Text>
          </button>
        </div>

        {/* Mobile Content */}
        <div className={styles.mobileContent}>
          {/* Profile Section */}
          <div className={styles.profileSection}>
            <div className={styles.profileAvatar}>
              <Text className={styles.profileAvatarInitials}>{displayInitials}</Text>
            </div>
            <div className={styles.profileInfo}>
              <Text className={styles.profileName}>{displayName}</Text>
              <Text className={styles.profileEmail}>{displayEmail}</Text>
            </div>
            <ChevronRight20Regular className={styles.profileChevron} />
          </div>

          {/* Account Settings */}
          <div className={styles.settingsGroup}>
            <Text className={styles.groupTitle}>Account</Text>
            {accountSettings.map(renderSettingsItem)}
          </div>

          {/* Preferences */}
          <div className={styles.settingsGroup}>
            <Text className={styles.groupTitle}>Preferences</Text>
            {preferenceSettings.map(renderSettingsItem)}
          </div>

          {/* Support */}
          <div className={styles.settingsGroup}>
            <Text className={styles.groupTitle}>Support</Text>
            {supportSettings.map(renderSettingsItem)}
          </div>

          {/* Logout */}
          <button
            className={styles.logoutButton}
            onClick={() => {
              void handleLogout();
            }}
          >
            <SignOut24Regular style={{ color: colors.semantic.error.main }} />
            <Text className={styles.logoutText}>Log Out</Text>
          </button>

          {/* Version */}
          <div className={styles.versionInfo}>
            <Text className={styles.versionText}>AzureBank v1.0.0</Text>
          </div>
        </div>

        {/* Desktop Content Area */}
        <div className={styles.desktopContentArea}>
          <Text className={styles.pageTitle}>Profile Settings</Text>

          <div className={styles.settingsGrid}>
            <div className={styles.settingsMain}>
              {/* Profile Card */}
              <div className={styles.settingsCard}>
                <div className={styles.cardContent}>
                  <div className={styles.profileHeader}>
                    <div className={styles.profileAvatarLarge}>
                      <Text className={styles.avatarInitialsLarge}>{displayInitials}</Text>
                    </div>
                    <div className={styles.profileDetails}>
                      <Text className={styles.profileNameLarge}>{displayName}</Text>
                      <Text className={styles.profileEmailLarge}>{displayEmail}</Text>
                      <Text className={styles.profileMemberSince}>
                        Member since {mockUser.memberSince}
                      </Text>
                    </div>
                    <button className={styles.changeAvatarBtn}>
                      <Camera20Regular style={{ color: colors.neutral[500] }} />
                      <Text className={styles.changeAvatarText}>Change Photo</Text>
                    </button>
                  </div>

                  <div className={styles.formRow}>
                    <div className={styles.formField}>
                      <Text className={styles.formLabel}>First Name</Text>
                      <div className={styles.formInput}>
                        <Text className={styles.inputValue}>{user?.firstName ?? ''}</Text>
                      </div>
                    </div>
                    <div className={styles.formField}>
                      <Text className={styles.formLabel}>Last Name</Text>
                      <div className={styles.formInput}>
                        <Text className={styles.inputValue}>{user?.lastName ?? ''}</Text>
                      </div>
                    </div>
                  </div>

                  <div className={styles.formRow}>
                    <div className={styles.formField}>
                      <Text className={styles.formLabel}>Email Address</Text>
                      <div className={styles.formInput}>
                        <Text className={styles.inputValue}>{displayEmail}</Text>
                      </div>
                    </div>
                    <div className={styles.formField}>
                      <Text className={styles.formLabel}>Phone Number</Text>
                      <div className={styles.formInput}>
                        <Text className={styles.inputValue}>{mockUser.phone}</Text>
                      </div>
                    </div>
                  </div>

                  <div className={styles.formRow}>
                    <div className={styles.formField}>
                      <Text className={styles.formLabel}>Date of Birth</Text>
                      <div className={styles.formInput}>
                        <Text className={styles.inputValue}>{mockUser.dateOfBirth}</Text>
                      </div>
                    </div>
                    <div className={styles.formField}>
                      <Text className={styles.formLabel}>Country</Text>
                      <div className={styles.formInput}>
                        <Text className={styles.inputValue}>{mockUser.country}</Text>
                      </div>
                    </div>
                  </div>
                </div>
                <div className={styles.cardActions}>
                  <Button appearance="primary">Save Changes</Button>
                  <Button appearance="secondary">Cancel</Button>
                </div>
              </div>

              {/* Display Preferences */}
              <div className={styles.settingsCard}>
                <div className={styles.cardHeader}>
                  <Text className={styles.cardTitle}>Display Preferences</Text>
                </div>
                <div className={styles.cardContent}>
                  <div className={styles.settingRow}>
                    <div className={styles.settingInfo}>
                      <Text className={styles.settingTitle}>Dark Mode</Text>
                      <Text className={styles.settingDescription}>
                        Switch to dark theme to reduce eye strain
                      </Text>
                    </div>
                    <Switch checked={darkMode} onChange={(_, data) => setDarkMode(data.checked)} />
                  </div>
                  <div className={styles.settingRow}>
                    <div className={styles.settingInfo}>
                      <Text className={styles.settingTitle}>Compact View</Text>
                      <Text className={styles.settingDescription}>
                        Show more items with smaller spacing
                      </Text>
                    </div>
                    <Switch
                      checked={compactView}
                      onChange={(_, data) => setCompactView(data.checked)}
                    />
                  </div>
                  <div className={styles.settingRow}>
                    <div className={styles.settingInfo}>
                      <Text className={styles.settingTitle}>Show Account Balances</Text>
                      <Text className={styles.settingDescription}>
                        Display balances on dashboard by default
                      </Text>
                    </div>
                    <Switch
                      checked={showBalances}
                      onChange={(_, data) => setShowBalances(data.checked)}
                    />
                  </div>
                </div>
              </div>

              {/* Danger Zone */}
              <div className={styles.settingsCard}>
                <div className={styles.cardHeader}>
                  <Text className={styles.cardTitle}>Danger Zone</Text>
                </div>
                <div className={styles.cardContent}>
                  <div className={styles.settingRow}>
                    <div className={styles.settingInfo}>
                      <Text className={styles.settingTitle}>Log Out</Text>
                      <Text className={styles.settingDescription}>
                        Sign out from your account on this device
                      </Text>
                    </div>
                    <Button
                      appearance="secondary"
                      style={{
                        borderColor: colors.semantic.error.main,
                        color: colors.semantic.error.main,
                      }}
                      icon={<SignOut24Regular />}
                      onClick={() => {
                        void handleLogout();
                      }}
                    >
                      Log Out
                    </Button>
                  </div>
                </div>
              </div>
            </div>

            {/* Sidebar */}
            <div className={styles.settingsSidebar}>
              <div className={styles.quickActionsCard}>
                <Text className={styles.quickActionsTitle}>Quick Actions</Text>
                <div className={styles.quickActionList}>
                  <button className={styles.quickActionItem}>
                    <div className={`${styles.quickActionIcon} ${styles.iconBlue}`}>
                      <LockClosed24Regular />
                    </div>
                    <Text className={styles.quickActionLabel}>Change Password</Text>
                    <ChevronRight20Regular className={styles.quickActionChevron} />
                  </button>
                  <button className={styles.quickActionItem}>
                    <div className={`${styles.quickActionIcon} ${styles.iconGreen}`}>
                      <Shield24Regular />
                    </div>
                    <Text className={styles.quickActionLabel}>Enable 2FA</Text>
                    <ChevronRight20Regular className={styles.quickActionChevron} />
                  </button>
                  <button className={styles.quickActionItem}>
                    <div className={`${styles.quickActionIcon} ${styles.iconOrange}`}>
                      <ArrowDownload20Regular />
                    </div>
                    <Text className={styles.quickActionLabel}>Download My Data</Text>
                    <ChevronRight20Regular className={styles.quickActionChevron} />
                  </button>
                </div>
              </div>

              <div className={styles.helpCard}>
                <div className={styles.helpHeader}>
                  <QuestionCircle24Regular className={styles.helpIcon} />
                  <Text className={styles.helpTitle}>Need Help?</Text>
                </div>
                <Text className={styles.helpText}>
                  If you have questions about your account settings or need assistance, our support
                  team is available 24/7.
                </Text>
                <Text className={styles.helpLink}>Contact Support →</Text>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

export default SettingsPage;
