import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import {
  makeStyles,
  tokens,
  Text,
  Input,
  Button,
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
import { Link as FluentLink } from '@fluentui/react-components';
import type { ApiProblem } from '../api/problemBaseQuery';
import { RetryCountdown, retryDeadline } from '../components/feedback';
import { useRegisterMutation } from '../features/api/apiSlice';

// Validation schema — mirrors the backend contract (ValidationRules): AzureTag pattern
// and the full password policy (upper + lower + digit + special, 8-128).
const registerSchema = z
  .object({
    firstName: z.string().min(1, 'First name is required').max(50, 'First name is too long'),
    lastName: z.string().min(1, 'Last name is required').max(50, 'Last name is too long'),
    azureTag: z
      .string()
      .regex(
        /^[a-z][a-z0-9_]{2,19}$/,
        'AzureTag must start with a letter and contain only lowercase letters, numbers, and underscores (3-20 characters)',
      ),
    email: z.email('Please enter a valid email address'),
    password: z
      .string()
      .min(8, 'Password must be at least 8 characters')
      .max(128, 'Password is too long')
      .regex(/[A-Z]/, 'Password must contain at least one uppercase letter')
      .regex(/[a-z]/, 'Password must contain at least one lowercase letter')
      .regex(/[0-9]/, 'Password must contain at least one number')
      .regex(/[^a-zA-Z0-9]/, 'Password must contain at least one special character'),
    confirmPassword: z.string().min(1, 'Please confirm your password'),
  })
  .refine((data) => data.password === data.confirmPassword, {
    message: "Passwords don't match",
    path: ['confirmPassword'],
  });

type RegisterFormData = z.infer<typeof registerSchema>;

const REGISTER_FIELDS = ['firstName', 'lastName', 'azureTag', 'email', 'password'] as const;

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
    color: '#FFFFFF',
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
    color: '#FFFFFF',
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
      maxWidth: '440px',
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

  // ========== FORM HEADER ==========
  formHeader: {
    marginBottom: '28px',
  },

  formTitle: {
    display: 'block',
    fontSize: '26px',
    fontWeight: 600,
    lineHeight: '1.2',
    color: tokens.colorNeutralForeground1,
    marginBottom: '8px',
    '@media (min-width: 480px)': {
      fontSize: '28px',
    },
  },

  formSubtitle: {
    display: 'block',
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
    gap: '16px',
  },

  nameRow: {
    display: 'flex',
    gap: '12px',
    width: '100%',
  },

  nameField: {
    flex: 1,
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

  submitButton: {
    width: '100%',
    height: '48px',
    marginTop: '8px',
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

  // ========== LOGIN LINK ==========
  loginLink: {
    textAlign: 'center',
  },

  loginText: {
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

  // ========== PASSWORD REQUIREMENTS ==========
  passwordHint: {
    fontSize: '12px',
    color: tokens.colorNeutralForeground3,
    marginTop: '4px',
  },
});

export function RegisterPage() {
  const styles = useStyles();
  const navigate = useNavigate();

  const [registerUser, { isLoading, error }] = useRegisterMutation();

  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [elapsedDeadline, setElapsedDeadline] = useState<number | null>(null);
  const bannerRef = useRef<HTMLDivElement>(null);

  const problem = error as ApiProblem | undefined;

  // Shared login/register 429 bucket (D13/D15): absolute deadline derived from the
  // error object — fresh identity per rejection, so a repeat 429 with the identical
  // retryAfterSeconds still mints a fresh deadline (see LoginPage).
  const lockDeadline = useMemo(() => {
    const seconds = (error as ApiProblem | undefined)?.retryAfterSeconds;
    return seconds !== undefined ? retryDeadline(seconds) : null;
  }, [error]);
  const rateLimited =
    problem?.errorCode === 'RATE_LIMIT_EXCEEDED' &&
    lockDeadline !== null &&
    elapsedDeadline !== lockDeadline;

  const {
    register,
    handleSubmit,
    setError,
    getValues,
    formState: { errors },
  } = useForm<RegisterFormData>({
    resolver: zodResolver(registerSchema),
    defaultValues: {
      firstName: '',
      lastName: '',
      azureTag: '',
      email: '',
      password: '',
      confirmPassword: '',
    },
  });

  // D15: field values stay, the dual-path banner takes focus.
  useEffect(() => {
    if (problem?.errorCode === 'REGISTRATION_FAILED') {
      bannerRef.current?.focus();
    }
  }, [problem]);

  const onSubmit = async (data: RegisterFormData) => {
    try {
      const result = await registerUser({
        azureTag: data.azureTag,
        email: data.email,
        password: data.password,
        firstName: data.firstName,
        lastName: data.lastName,
      }).unwrap();
      // The 201 IS a login: the BFF set the session cookie and the auth slice is already
      // 'authenticated'. A brand-new user has no PIN yet, so hand off to the PIN-setup wizard
      // (it returns to the dashboard when done or skipped); an already-PIN'd user goes straight in.
      navigate(result.user.hasPin ? '/dashboard' : '/pin-setup?returnTo=/dashboard', {
        replace: true,
      });
    } catch (caught) {
      const rejected = caught as ApiProblem;
      if (rejected.errorCode === 'VALIDATION_ERROR' && rejected.errors) {
        for (const [key, messages] of Object.entries(rejected.errors)) {
          if (messages.length === 0) {
            continue;
          }
          const field = key.charAt(0).toLowerCase() + key.slice(1);
          if ((REGISTER_FIELDS as readonly string[]).includes(field)) {
            setError(field as (typeof REGISTER_FIELDS)[number], {
              type: 'server',
              message: messages[0],
            });
          } else {
            // Contract drift (a server rule with no client field): surface at the form
            // root instead of swallowing it — the user must never submit into silence.
            setError('root', { type: 'server', message: messages[0] });
          }
        }
      }
      // Everything else renders from the mutation error state below.
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

        <h1 className={styles.tagline}>Start Your Financial Journey Today</h1>

        <p className={styles.taglineSubtext}>
          Join thousands of customers who trust AzureBank for their everyday banking needs. Quick
          setup, powerful features.
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

          {/* Form Header */}
          <div className={styles.formHeader}>
            <Text as="h1" className={styles.formTitle}>
              Create Account
            </Text>
            <Text as="p" className={styles.formSubtitle}>
              Join AzureBank today
            </Text>
          </div>

          {/* D15: tag-collision-blind dual-path banner — FORM-level, never field-attached,
              neutral copy, "Sign in" pre-fills the typed email via route state. */}
          {problem?.errorCode === 'REGISTRATION_FAILED' && (
            <MessageBar
              intent="error"
              className={styles.errorMessage}
              ref={bannerRef}
              tabIndex={-1}
            >
              <MessageBarBody>
                We couldn't create an account with these details. If you already have an account,{' '}
                <Link
                  to="/login"
                  state={{ prefillEmail: getValues('email') }}
                  style={{ color: 'inherit' }}
                >
                  <FluentLink as="span">sign in with this email</FluentLink>
                </Link>{' '}
                — or try a different AzureTag.
              </MessageBarBody>
            </MessageBar>
          )}

          {rateLimited && lockDeadline !== null && (
            <MessageBar intent="warning" className={styles.errorMessage}>
              <MessageBarBody>
                Too many attempts from your connection.{' '}
                <RetryCountdown
                  deadline={lockDeadline}
                  onElapsed={() => setElapsedDeadline(lockDeadline)}
                />
              </MessageBarBody>
            </MessageBar>
          )}

          {errors.root?.message && (
            <MessageBar intent="error" className={styles.errorMessage}>
              <MessageBarBody>{errors.root.message}</MessageBarBody>
            </MessageBar>
          )}

          {problem &&
            !['REGISTRATION_FAILED', 'RATE_LIMIT_EXCEEDED', 'VALIDATION_ERROR'].includes(
              problem.errorCode,
            ) && (
              <MessageBar intent="error" className={styles.errorMessage}>
                <MessageBarBody>
                  {problem.detail || 'Registration failed. Please try again.'}
                </MessageBarBody>
              </MessageBar>
            )}

          {/* Register Form */}
          <form className={styles.form} onSubmit={handleSubmit(onSubmit)}>
            {/* Name Row */}
            <div className={styles.nameRow}>
              <Field
                label="First Name"
                validationState={errors.firstName ? 'error' : 'none'}
                validationMessage={errors.firstName?.message}
                className={styles.nameField}
              >
                <Input
                  placeholder="John"
                  size="large"
                  {...register('firstName')}
                  aria-invalid={errors.firstName ? 'true' : 'false'}
                />
              </Field>

              <Field
                label="Last Name"
                validationState={errors.lastName ? 'error' : 'none'}
                validationMessage={errors.lastName?.message}
                className={styles.nameField}
              >
                <Input
                  placeholder="Doe"
                  size="large"
                  {...register('lastName')}
                  aria-invalid={errors.lastName ? 'true' : 'false'}
                />
              </Field>
            </div>

            {/* AzureTag — the public handle other users send money to */}
            <Field
              label="AzureTag"
              hint="Your public handle for receiving transfers — lowercase letters, numbers, underscores"
              validationState={errors.azureTag ? 'error' : 'none'}
              validationMessage={errors.azureTag?.message}
            >
              <Input
                placeholder="john_doe"
                size="large"
                autoComplete="username"
                {...register('azureTag')}
                aria-invalid={errors.azureTag ? 'true' : 'false'}
              />
            </Field>

            {/* Email */}
            <Field
              label="Email"
              validationState={errors.email ? 'error' : 'none'}
              validationMessage={errors.email?.message}
            >
              <Input
                type="email"
                placeholder="john.doe@example.com"
                size="large"
                {...register('email')}
                aria-invalid={errors.email ? 'true' : 'false'}
              />
            </Field>

            {/* Password */}
            <Field
              label="Password"
              validationState={errors.password ? 'error' : 'none'}
              validationMessage={errors.password?.message}
              hint="Min 8 characters, 1 uppercase, 1 lowercase, 1 number"
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

            {/* Confirm Password */}
            <Field
              label="Confirm Password"
              validationState={errors.confirmPassword ? 'error' : 'none'}
              validationMessage={errors.confirmPassword?.message}
            >
              <div className={styles.passwordWrapper}>
                <Input
                  type={showConfirmPassword ? 'text' : 'password'}
                  placeholder="Confirm your password"
                  size="large"
                  className={styles.passwordInput}
                  {...register('confirmPassword')}
                  aria-invalid={errors.confirmPassword ? 'true' : 'false'}
                />
                <Button
                  appearance="transparent"
                  className={styles.passwordToggle}
                  onClick={() => setShowConfirmPassword(!showConfirmPassword)}
                  type="button"
                  aria-label={showConfirmPassword ? 'Hide password' : 'Show password'}
                >
                  {showConfirmPassword ? <EyeOff24Regular /> : <Eye24Regular />}
                </Button>
              </div>
            </Field>

            <Button
              appearance="primary"
              size="large"
              className={styles.submitButton}
              type="submit"
              disabled={isLoading || rateLimited}
            >
              {isLoading ? <Spinner size="tiny" /> : 'Create Account'}
            </Button>
          </form>

          {/* Divider */}
          <div className={styles.divider}>
            <div className={styles.dividerLine} />
            <span className={styles.dividerText}>or</span>
            <div className={styles.dividerLine} />
          </div>

          {/* Login Link */}
          <div className={styles.loginLink}>
            <Text className={styles.loginText}>
              Already have an account?{' '}
              <Link to="/login" style={{ color: 'inherit' }}>
                <FluentLink as="span">Sign In</FluentLink>
              </Link>
            </Text>
          </div>

          {/* Footer */}
          <div className={styles.footer}>
            <Text className={styles.footerText}>
              By creating an account, you agree to our Terms of Service and Privacy Policy
            </Text>
          </div>
        </div>
      </div>
    </div>
  );
}

export default RegisterPage;
