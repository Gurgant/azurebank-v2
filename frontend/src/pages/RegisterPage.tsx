import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import {
  makeStyles,
  tokens,
  Input,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
  Field,
  Link as FluentLink,
} from '@fluentui/react-components';
import { Eye24Regular, EyeOff24Regular } from '@fluentui/react-icons';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { emailField } from '../forms/emailField';
import type { ApiProblem } from '../api/problemBaseQuery';
import { toFieldName } from '../api/validationErrors';
import {
  AuthCrossLink,
  AuthDemoNote,
  AuthDivider,
  AuthLayout,
} from '../components/layout/AuthLayout';
import { RetryCountdown, WaitHint, retryDeadline } from '../components/feedback';
import { useRegisterMutation } from '../features/api/apiSlice';

// Validation schema — mirrors the backend contract (ValidationRules): AzureTag pattern
// and the full password policy (upper + lower + digit + special, printable ASCII, 8-128).
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
    email: emailField,
    password: z
      .string()
      .min(8, 'Password must be at least 8 characters')
      .max(128, 'Password is too long')
      .regex(/[A-Z]/, 'Password must contain at least one uppercase letter')
      .regex(/[a-z]/, 'Password must contain at least one lowercase letter')
      .regex(/[0-9]/, 'Password must contain at least one number')
      .regex(/[^a-zA-Z0-9]/, 'Password must contain at least one special character')
      .regex(
        /^[\x20-\x7E]*$/,
        'Use only letters A to Z, numbers and the symbols on a standard keyboard.',
      ),
    confirmPassword: z.string().min(1, 'Please confirm your password'),
  })
  .refine((data) => data.password === data.confirmPassword, {
    error: "Passwords don't match",
    path: ['confirmPassword'],
  });

type RegisterFormData = z.infer<typeof registerSchema>;

const REGISTER_FIELDS = ['firstName', 'lastName', 'azureTag', 'email', 'password'] as const;

const useStyles = makeStyles({
  // What is left after AuthLayout took the frame. Everything else — panel, column, heading,
  // footer, divider — is declared once there.
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
  },

  // Top, not the default stretch: a message under one name field would otherwise stretch the
  // other field's input to the taller field's height.
  nameRow: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '12px',
    width: '100%',
  },

  // A flex item and Fluent's Input root both keep `min-width: auto`, so without these the inner
  // input's built-in size (20 characters: 217 px, 219 px with the root's borders) holds each name
  // field wide and the row runs past the column: 470 px of page on a 320 px phone.
  nameField: {
    flex: 1,
    minWidth: 0,
  },

  nameInput: {
    width: '100%',
    minWidth: 0,
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

  errorMessage: {
    marginBottom: '16px',
  },

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
          // PascalCase (model-state) or camelCase (FluentValidation) — see `toFieldName`.
          const field = toFieldName(key);
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
    <AuthLayout
      headline="Start banking in a minute"
      intro="Open an account, then deposit, withdraw and send money in a working demo."
      title="Create account"
      subtitle="Join AzureBank today"
      footer={<AuthDemoNote />}
    >
      {/* D15: tag-collision-blind dual-path banner — FORM-level, never field-attached,
          neutral copy, "Sign in" pre-fills the typed email via route state. */}
      {problem?.errorCode === 'REGISTRATION_FAILED' && (
        <MessageBar intent="error" className={styles.errorMessage} ref={bannerRef} tabIndex={-1}>
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
        <MessageBar intent="warning" role="alert" className={styles.errorMessage}>
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
        <MessageBar intent="error" role="alert" className={styles.errorMessage}>
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
      {/* noValidate: the browser's own check would hide the sentence this form gives for a
          value that is not an email address. */}
      <form className={styles.form} noValidate onSubmit={handleSubmit(onSubmit)}>
        {/* Name Row */}
        <div className={styles.nameRow}>
          <Field
            label="First name"
            validationState={errors.firstName ? 'error' : 'none'}
            validationMessage={errors.firstName?.message}
            className={styles.nameField}
          >
            <Input
              placeholder="John"
              size="large"
              className={styles.nameInput}
              autoComplete="given-name"
              {...register('firstName')}
              aria-invalid={errors.firstName ? 'true' : 'false'}
            />
          </Field>

          <Field
            label="Last name"
            validationState={errors.lastName ? 'error' : 'none'}
            validationMessage={errors.lastName?.message}
            className={styles.nameField}
          >
            <Input
              placeholder="Doe"
              size="large"
              className={styles.nameInput}
              autoComplete="family-name"
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
            // NOT `username`. Sign-in accepts an email and nothing else (`z.email()`), so a manager
            // that saved this handle as the username would offer it into a field that rejects it.
            // The credential identity belongs to whatever sign-in accepts.
            autoComplete="nickname"
            {...register('azureTag')}
            aria-invalid={errors.azureTag ? 'true' : 'false'}
          />
        </Field>

        {/* Email */}
        <Field
          label="Email address"
          validationState={errors.email ? 'error' : 'none'}
          validationMessage={errors.email?.message}
        >
          <Input
            type="email"
            placeholder="john.doe@example.com"
            size="large"
            // The credential identity, matching LoginPage — this is what sign-in accepts.
            autoComplete="username"
            {...register('email')}
            aria-invalid={errors.email ? 'true' : 'false'}
          />
        </Field>

        {/* Password */}
        <Field
          label="Password"
          validationState={errors.password ? 'error' : 'none'}
          validationMessage={errors.password?.message}
          hint="At least 8 characters, with an uppercase letter, a lowercase letter, a number and one character that is not a letter or a digit, such as ! or #."
        >
          <div className={styles.passwordWrapper}>
            <Input
              type={showPassword ? 'text' : 'password'}
              placeholder="Enter your password"
              size="large"
              className={styles.passwordInput}
              autoComplete="new-password"
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
          label="Confirm password"
          validationState={errors.confirmPassword ? 'error' : 'none'}
          validationMessage={errors.confirmPassword?.message}
        >
          <div className={styles.passwordWrapper}>
            <Input
              type={showConfirmPassword ? 'text' : 'password'}
              placeholder="Confirm your password"
              size="large"
              className={styles.passwordInput}
              autoComplete="new-password"
              {...register('confirmPassword')}
              aria-invalid={errors.confirmPassword ? 'true' : 'false'}
            />
            <Button
              appearance="transparent"
              className={styles.passwordToggle}
              onClick={() => setShowConfirmPassword(!showConfirmPassword)}
              type="button"
              // Its own name: two toggles both called "Show password" could not be told apart in a
              // screen reader's list of buttons.
              aria-label={showConfirmPassword ? 'Hide confirm password' : 'Show confirm password'}
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
          {isLoading ? <Spinner size="tiny" /> : 'Create account'}
        </Button>
        <WaitHint active={isLoading} kind="write" />
      </form>

      <AuthDivider />

      <AuthCrossLink prompt="Already have an account?" to="/login" label="Sign in" />
    </AuthLayout>
  );
}

export default RegisterPage;
