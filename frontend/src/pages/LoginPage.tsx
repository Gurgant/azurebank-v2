import { useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import {
  makeStyles,
  tokens,
  Text,
  Input,
  Button,
  Checkbox,
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
import { useAppDispatch, useAppSelector } from '../app/hooks';
import { login, selectAuthLoading, selectAuthError, clearError } from '../features/auth';

// Validation schema
const loginSchema = z.object({
  email: z.string().email('Please enter a valid email address'),
  password: z.string().min(1, 'Password is required'),
  rememberMe: z.boolean().optional(),
});

type LoginFormData = z.infer<typeof loginSchema>;

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
  const dispatch = useAppDispatch();
  const isLoading = useAppSelector(selectAuthLoading);
  const authError = useAppSelector(selectAuthError);

  const [showPassword, setShowPassword] = useState(false);

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<LoginFormData>({
    resolver: zodResolver(loginSchema),
    defaultValues: {
      email: '',
      password: '',
      rememberMe: false,
    },
  });

  const onSubmit = async (data: LoginFormData) => {
    dispatch(clearError());
    const result = await dispatch(
      login({
        email: data.email,
        password: data.password,
      }),
    );

    if (login.fulfilled.match(result)) {
      navigate('/dashboard');
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

          {/* Error Message */}
          {authError && (
            <MessageBar intent="error" className={styles.errorMessage}>
              <MessageBarBody>{authError}</MessageBarBody>
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

            <div className={styles.rememberRow}>
              <Checkbox label="Remember me" {...register('rememberMe')} />
              <FluentLink href="/forgot-password">Forgot password?</FluentLink>
            </div>

            <Button
              appearance="primary"
              size="large"
              className={styles.submitButton}
              type="submit"
              disabled={isLoading}
            >
              {isLoading ? <Spinner size="tiny" /> : 'Sign in'}
            </Button>
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
