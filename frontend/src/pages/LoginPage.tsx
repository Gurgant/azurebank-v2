import { useMemo, useState } from 'react';
import { useLocation, useNavigate, Link } from 'react-router-dom';
import {
  makeStyles,
  tokens,
  Text,
  Input,
  Button,
  Link as FluentLink,
  Spinner,
  MessageBar,
  MessageBarBody,
  Field,
} from '@fluentui/react-components';
import {
  Eye24Regular,
  EyeOff24Regular,
  ShieldCheckmark24Regular,
  LockClosed24Regular,
  Globe24Regular,
} from '@fluentui/react-icons';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import type { ApiProblem } from '../api/problemBaseQuery';
import { RetryCountdown, retryDeadline } from '../components/feedback';
import { useLoginMutation } from '../features/api/apiSlice';

// Validation schema
const loginSchema = z.object({
  email: z.email('Please enter a valid email address'),
  password: z.string().min(1, 'Password is required'),
});

type LoginFormData = z.infer<typeof loginSchema>;

/** Navigation state this page understands (guard redirects + register dual-path). */
interface LoginNavState {
  from?: { pathname?: string };
  reason?: 'expired';
  prefillEmail?: string;
}

const useStyles = makeStyles({
  // ========== CONTAINER ==========
  container: {
    display: 'flex',
    minHeight: '100vh',
    width: '100%',
    flexDirection: 'column',
    '@media (min-width: 1024px)': {
      flexDirection: 'row',
    },
  },

  // ========== LEFT PANEL - BRANDING (Desktop Only) ==========
  leftPanel: {
    display: 'none',
    '@media (min-width: 1024px)': {
      display: 'flex',
      flexDirection: 'column',
      justifyContent: 'center',
      alignItems: 'flex-start',
      width: '50%',
      minWidth: '480px',
      maxWidth: '720px',
      backgroundColor: tokens.colorBrandBackground,
      padding: '64px 80px',
    },
  },

  brandLogo: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    marginBottom: '48px',
  },

  logoIcon: {
    width: '48px',
    height: '48px',
    backgroundColor: 'rgba(255, 255, 255, 0.2)',
    borderRadius: '12px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: '#FFFFFF', // Explicit white for visibility
    fontSize: '24px',
  },

  logoText: {
    fontSize: '32px',
    fontWeight: 700,
    color: '#FFFFFF',
  },

  tagline: {
    fontSize: '44px',
    fontWeight: 700,
    lineHeight: '1.15',
    marginBottom: '20px',
    maxWidth: '480px',
    color: '#FFFFFF',
  },

  taglineSubtext: {
    fontSize: '17px',
    opacity: 0.9,
    marginBottom: '48px',
    maxWidth: '440px',
    lineHeight: '1.6',
    color: '#FFFFFF',
  },

  features: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },

  featureItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '14px',
  },

  featureIcon: {
    width: '42px',
    height: '42px',
    backgroundColor: 'rgba(255, 255, 255, 0.15)',
    borderRadius: '10px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: '#FFFFFF', // Explicit white for icon visibility
    fontSize: '20px',
    flexShrink: 0,
  },

  featureText: {
    fontSize: '15px',
    fontWeight: 500,
    color: '#FFFFFF',
  },

  // ========== RIGHT PANEL - FORM ==========
  rightPanel: {
    flex: 1,
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'center',
    alignItems: 'center',
    padding: '32px 20px',
    backgroundColor: tokens.colorNeutralBackground1,
    minHeight: '100vh',
    '@media (min-width: 480px)': {
      padding: '40px 32px',
    },
    '@media (min-width: 1024px)': {
      padding: '64px 48px',
      minHeight: 'auto',
    },
  },

  formContainer: {
    width: '100%',
    maxWidth: '380px',
    '@media (min-width: 1024px)': {
      maxWidth: '400px',
    },
  },

  // ========== MOBILE LOGO ==========
  mobileLogo: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    marginBottom: '28px',
    '@media (min-width: 1024px)': {
      display: 'none',
    },
  },

  mobileLogoIcon: {
    width: '36px',
    height: '36px',
    backgroundColor: tokens.colorBrandBackground,
    borderRadius: '8px',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: '#FFFFFF',
    fontSize: '18px',
  },

  mobileLogoText: {
    fontSize: '22px',
    fontWeight: 700,
    color: tokens.colorBrandForeground1,
  },

  // ========== FORM HEADER (Title + Subtitle) ==========
  formHeader: {
    marginBottom: '28px',
  },

  formTitle: {
    display: 'block', // CRITICAL: Force block display
    fontSize: '26px',
    fontWeight: 600,
    lineHeight: '1.2',
    color: tokens.colorNeutralForeground1,
    marginBottom: '8px', // Space between title and subtitle
    '@media (min-width: 480px)': {
      fontSize: '28px',
    },
  },

  formSubtitle: {
    display: 'block', // CRITICAL: Force block display
    fontSize: '14px',
    lineHeight: '1.5',
    color: tokens.colorNeutralForeground2,
    '@media (min-width: 480px)': {
      fontSize: '15px',
    },
  },

  // ========== FORM ==========
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: '18px',
  },

  passwordWrapper: {
    position: 'relative',
    display: 'flex',
    alignItems: 'center',
  },

  passwordInput: {
    width: '100%',
    paddingRight: '44px',
  },

  passwordToggle: {
    position: 'absolute',
    right: '8px',
    minWidth: 'auto',
    padding: '4px',
    color: tokens.colorNeutralForeground3,
    ':hover': {
      color: tokens.colorNeutralForeground1,
      backgroundColor: 'transparent',
    },
  },

  rememberRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '8px',
  },

  submitButton: {
    width: '100%',
    height: '44px',
    marginTop: '4px',
    fontWeight: 600,
  },

  // ========== DIVIDER ==========
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

  dividerText: {
    color: tokens.colorNeutralForeground3,
    fontSize: '13px',
  },

  // ========== REGISTER LINK ==========
  registerLink: {
    textAlign: 'center',
  },

  registerText: {
    fontSize: '14px',
    color: tokens.colorNeutralForeground2,
  },

  // ========== FOOTER ==========
  footer: {
    marginTop: '40px',
    textAlign: 'center',
    '@media (min-width: 1024px)': {
      marginTop: '48px',
    },
  },

  footerText: {
    fontSize: '12px',
    color: tokens.colorNeutralForeground3,
  },

  // ========== ERROR MESSAGE ==========
  errorMessage: {
    marginBottom: '16px',
  },
});

export function LoginPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const location = useLocation();
  const [login, { isLoading, error }] = useLoginMutation();

  const [showPassword, setShowPassword] = useState(false);
  const [elapsedDeadline, setElapsedDeadline] = useState<number | null>(null);

  const navState = (location.state ?? {}) as LoginNavState;
  const problem = error as ApiProblem | undefined;

  // D13: one ABSOLUTE deadline per lock/limit RESPONSE. Derived from the error object —
  // fresh identity on EVERY rejection — so a repeat lockout with the identical
  // retryAfterSeconds (fixed windows are common) still mints a fresh deadline instead
  // of staying pinned to the first, already-elapsed one.
  const lockDeadline = useMemo(() => {
    const seconds = (error as ApiProblem | undefined)?.retryAfterSeconds;
    return seconds !== undefined ? retryDeadline(seconds) : null;
  }, [error]);
  const countdownActive = lockDeadline !== null && elapsedDeadline !== lockDeadline;
  // The login form branches RATE_LIMIT_EXCEEDED vs ACCOUNT_LOCKED explicitly — never
  // identical copy (D13): the first is per-IP throttling, the second is the credential
  // lockout, and only the second replaces the submit entirely.
  const accountLocked = problem?.errorCode === 'ACCOUNT_LOCKED' && countdownActive;
  const rateLimited = problem?.errorCode === 'RATE_LIMIT_EXCEEDED' && countdownActive;

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<LoginFormData>({
    resolver: zodResolver(loginSchema),
    defaultValues: {
      email: navState.prefillEmail ?? '',
      password: '',
    },
  });

  const onSubmit = async (data: LoginFormData) => {
    try {
      await login({ email: data.email, password: data.password }).unwrap();
      // returnTo: land where the guard interrupted, not always the dashboard.
      navigate(navState.from?.pathname ?? '/dashboard', { replace: true });
    } catch {
      // Surfaced through the mutation's error state below.
    }
  };

  return (
    <div className={styles.container}>
      {/* ========== LEFT PANEL - Desktop Only ========== */}
      <div className={styles.leftPanel}>
        <div className={styles.brandLogo}>
          <div className={styles.logoIcon}>
            <Globe24Regular />
          </div>
          <span className={styles.logoText}>AzureBank</span>
        </div>

        <h1 className={styles.tagline}>Banking Made Simple, Secure, and Smart</h1>

        <p className={styles.taglineSubtext}>
          Manage your finances with confidence. Experience modern banking with powerful tools
          designed for your success.
        </p>

        <div className={styles.features}>
          <div className={styles.featureItem}>
            <div className={styles.featureIcon}>
              <ShieldCheckmark24Regular />
            </div>
            <span className={styles.featureText}>Bank-grade security protection</span>
          </div>

          <div className={styles.featureItem}>
            <div className={styles.featureIcon}>
              <LockClosed24Regular />
            </div>
            <span className={styles.featureText}>Instant transfers & payments</span>
          </div>

          <div className={styles.featureItem}>
            <div className={styles.featureIcon}>
              <Globe24Regular />
            </div>
            <span className={styles.featureText}>24/7 account access</span>
          </div>
        </div>
      </div>

      {/* ========== RIGHT PANEL - Form ========== */}
      <div className={styles.rightPanel}>
        <div className={styles.formContainer}>
          {/* Mobile Logo */}
          <div className={styles.mobileLogo}>
            <div className={styles.mobileLogoIcon}>
              <Globe24Regular />
            </div>
            <span className={styles.mobileLogoText}>AzureBank</span>
          </div>

          {/* Form Header - Title and Subtitle on SEPARATE lines */}
          <div className={styles.formHeader}>
            <Text as="h1" className={styles.formTitle}>
              Welcome back
            </Text>
            <Text as="p" className={styles.formSubtitle}>
              Sign in to your account to continue
            </Text>
          </div>

          {/* Session-expiry note: only ever set by a post-boot 401 (D3/D6) */}
          {navState.reason === 'expired' && !problem && (
            <MessageBar intent="warning" className={styles.errorMessage}>
              <MessageBarBody>Your session has expired. Please sign in again.</MessageBarBody>
            </MessageBar>
          )}

          {problem?.errorCode === 'INVALID_CREDENTIALS' && (
            <MessageBar intent="error" className={styles.errorMessage}>
              <MessageBarBody>Invalid email or password.</MessageBarBody>
            </MessageBar>
          )}

          {accountLocked && lockDeadline !== null && (
            <MessageBar intent="error" className={styles.errorMessage}>
              <MessageBarBody>
                Too many failed sign-in attempts — your account is temporarily locked.{' '}
                <RetryCountdown
                  deadline={lockDeadline}
                  onElapsed={() => setElapsedDeadline(lockDeadline)}
                />
              </MessageBarBody>
            </MessageBar>
          )}

          {problem &&
            !['INVALID_CREDENTIALS', 'ACCOUNT_LOCKED', 'RATE_LIMIT_EXCEEDED'].includes(
              problem.errorCode,
            ) && (
              <MessageBar intent="error" className={styles.errorMessage}>
                <MessageBarBody>
                  {problem.detail || 'Something went wrong. Please try again.'}
                </MessageBarBody>
              </MessageBar>
            )}

          {/* Login Form */}
          <form className={styles.form} onSubmit={handleSubmit(onSubmit)}>
            <Field
              label="Email address"
              validationState={errors.email ? 'error' : 'none'}
              validationMessage={errors.email?.message}
            >
              <Input
                type="email"
                placeholder="name@example.com"
                size="large"
                {...register('email')}
                aria-invalid={errors.email ? 'true' : 'false'}
              />
            </Field>

            <Field
              label="Password"
              validationState={errors.password ? 'error' : 'none'}
              validationMessage={errors.password?.message}
            >
              <div className={styles.passwordWrapper}>
                <Input
                  type={showPassword ? 'text' : 'password'}
                  placeholder="Enter your password"
                  size="large"
                  className={styles.passwordInput}
                  {...register('password')}
                  aria-invalid={errors.password ? 'true' : 'false'}
                />
                <Button
                  appearance="transparent"
                  className={styles.passwordToggle}
                  onClick={() => setShowPassword(!showPassword)}
                  type="button"
                  aria-label={showPassword ? 'Hide password' : 'Show password'}
                >
                  {showPassword ? <EyeOff24Regular /> : <Eye24Regular />}
                </Button>
              </div>
            </Field>

            {/* ACCOUNT_LOCKED replaces the submit entirely (D13); the banner above
                carries the countdown. */}
            {!accountLocked && (
              <Button
                appearance="primary"
                size="large"
                className={styles.submitButton}
                type="submit"
                disabled={isLoading || rateLimited}
              >
                {isLoading ? <Spinner size="tiny" /> : 'Sign in'}
              </Button>
            )}

            {rateLimited && lockDeadline !== null && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  Too many attempts from your connection.{' '}
                  <RetryCountdown
                    deadline={lockDeadline}
                    onElapsed={() => setElapsedDeadline(lockDeadline)}
                  />
                </MessageBarBody>
              </MessageBar>
            )}
          </form>

          {/* Divider */}
          <div className={styles.divider}>
            <div className={styles.dividerLine} />
            <span className={styles.dividerText}>or</span>
            <div className={styles.dividerLine} />
          </div>

          {/* Register Link */}
          <div className={styles.registerLink}>
            <Text className={styles.registerText}>
              Don't have an account?{' '}
              <Link to="/register" style={{ color: 'inherit' }}>
                <FluentLink as="span">Create account</FluentLink>
              </Link>
            </Text>
          </div>

          {/* Footer */}
          <div className={styles.footer}>
            <Text className={styles.footerText}>© 2024 AzureBank. All rights reserved.</Text>
          </div>
        </div>
      </div>
    </div>
  );
}

export default LoginPage;
