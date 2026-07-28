import { useMemo, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import {
  makeStyles,
  tokens,
  Input,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
  Field,
} from '@fluentui/react-components';
import { Eye24Regular, EyeOff24Regular } from '@fluentui/react-icons';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import type { ApiProblem } from '../api/problemBaseQuery';
import { RetryCountdown, retryDeadline } from '../components/feedback';
import { AuthCrossLink, AuthDivider, AuthLayout } from '../components/layout/AuthLayout';
import { useLoginMutation } from '../features/api/apiSlice';

// Validation schema
const loginSchema = z.object({
  email: z.email('Please enter a valid email address'),
  password: z.string().min(1, 'Password is required'),
});

type LoginFormData = z.infer<typeof loginSchema>;

/**
 * Navigation state this page understands (guard redirects + register dual-path). Validated at
 * runtime with Zod: `location.state` is developer-set (low risk) but otherwise an untrusted cast —
 * a wrong shape falls back to {} rather than feeding e.g. a bogus `reason`/`from` into the UI.
 */
const loginNavStateSchema = z.object({
  from: z.object({ pathname: z.string().optional() }).optional(),
  reason: z.literal('expired').optional(),
  prefillEmail: z.email().optional(),
});
type LoginNavState = z.infer<typeof loginNavStateSchema>;

const useStyles = makeStyles({
  // What is left after AuthLayout took the frame: the form itself and the two things that hang
  // off it. Everything else — panel, column, heading, footer, divider — is declared once there.
  form: {
    display: 'flex',
    flexDirection: 'column',
    // 16px, matching registration. This page said 18px and nobody could say why.
    gap: '16px',
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
    height: '44px',
    marginTop: '4px',
    fontWeight: 600,
  },

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

  const navStateResult = loginNavStateSchema.safeParse(location.state ?? {});
  const navState: LoginNavState = navStateResult.success ? navStateResult.data : {};
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
    <AuthLayout
      headline="Banking Made Simple, Secure, and Smart"
      intro="Manage your finances with confidence. Experience modern banking with powerful tools designed for your success."
      title="Welcome back"
      subtitle="Sign in to your account to continue"
      footer={
        <>
          Protected by bank-grade encryption. We never share your details.
          <br />
          {/* The first screen anyone sees, which is the honest place to say what this is. The two
              claims above are defensible — HTTPS, hashed passwords, and no third party to share
              with — so they stay; what was never defensible was promising a support team. */}
          Demo project — not a real bank.{' '}
          <Link to="/about" style={{ color: 'inherit', textDecoration: 'underline' }}>
            About the developer →
          </Link>
        </>
      }
    >
      {/* Session-expiry note: only ever set by a post-boot 401 (D3/D6) */}
      {navState.reason === 'expired' && !problem && (
        <MessageBar intent="warning" className={styles.errorMessage}>
          <MessageBarBody>Your session has expired. Please sign in again.</MessageBarBody>
        </MessageBar>
      )}

      {problem?.errorCode === 'INVALID_CREDENTIALS' && (
        <MessageBar intent="error" role="alert" className={styles.errorMessage}>
          <MessageBarBody>Invalid email or password.</MessageBarBody>
        </MessageBar>
      )}

      {/* `role="alert"` because nothing else announces this. Fluent's MessageBar is `role="group"`,
          not a live region, and the submit button unmounts when the lock lands (D13) — so focus
          falls to <body> and a screen reader is told nothing at all. Making the button `disabled`
          instead does NOT help: measured in Chrome, disabling a focused button also drops focus to
          <body>. The announcement is the fix; the button's behaviour is not the problem. */}
      {accountLocked && lockDeadline !== null && (
        <MessageBar intent="error" role="alert" className={styles.errorMessage}>
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
          <MessageBar intent="error" role="alert" className={styles.errorMessage}>
            <MessageBarBody>
              {problem.detail || 'Something went wrong. Please try again.'}
            </MessageBarBody>
          </MessageBar>
        )}

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
            // `username`, not `email`: this is the identifier the credential pair is keyed on, and
            // registration must agree — see RegisterPage's email field.
            autoComplete="username"
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
              autoComplete="current-password"
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
          <MessageBar intent="warning" role="alert">
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

      <AuthDivider />

      <AuthCrossLink prompt="Don't have an account?" to="/register" label="Create account" />
    </AuthLayout>
  );
}

export default LoginPage;
