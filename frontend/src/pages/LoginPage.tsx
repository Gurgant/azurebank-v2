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
  Text,
} from '@fluentui/react-components';
import { Eye24Regular, EyeOff24Regular } from '@fluentui/react-icons';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import type { ApiProblem } from '../api/problemBaseQuery';
import { RetryCountdown, WaitHint, retryDeadline } from '../components/feedback';
import { AuthCrossLink, AuthDivider, AuthLayout } from '../components/layout/AuthLayout';
import { useClaimDemoCopyMutation, useLoginMutation } from '../features/api/apiSlice';
import { DemoEntry } from '../features/demo';
import { isDemoMode } from '../features/demo/demoMode';
import {
  CLAIM_DAILY_LIMIT,
  CLAIM_POOL_EMPTY,
  DEMO_SUBTITLE,
  HAVE_A_COPY_SIGN_IN,
} from '../features/demo/demoWords';

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

  // The line above the form on the demo, in the size and colour of the line under the form off it
  // (AuthLayout's cross-link). `display: block` because Fluent's `Text` renders inline whatever
  // element it is asked for.
  haveACopy: {
    display: 'block',
    margin: '0 0 16px',
    fontSize: '14px',
    color: tokens.colorNeutralForeground2,
  },
});

/** Which of the page's controls sent a request: the form's "Sign in", or "Try the demo". */
type LoginControl = 'form' | 'claim';

export function LoginPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const location = useLocation();
  // Asked at each render, not once: the page says whether it is the demo with a tag, and the
  // answer is the tag's (src/features/demo/demoMode.ts).
  const demo = isDemoMode();
  const [login, { isLoading: signingIn, error: signInError }] = useLoginMutation();
  const [claim, { isLoading: claiming, error: claimError }] = useClaimDemoCopyMutation();

  /*
    ONE PROBLEM ON THE PAGE, whichever control was refused.

    The page sends two kinds of request, a sign-in and, on the demo, a claim, and each mutation
    keeps its own last refusal: a sign-in refused and then a claim refused leaves two. Every
    banner below reads one `problem`, so that they exclude one another as they did when there was
    one request. Which one: the refusal of the request the last-pressed control sent. Each attempt
    records its control here, and from then on the other request's refusal is not read.
    src/pages/LoginPage.test.tsx refuses a sign-in and a claim, in both orders, and counts.

    Off the demo nothing sends a claim, and `problem` is the sign-in's, as before.
  */
  const [control, setControl] = useState<LoginControl | null>(null);
  const error = control === 'claim' ? claimError : signInError;
  // One flag over both requests: while either runs, every button that sends one waits for it.
  const busy = signingIn || claiming;
  // The form's own sign-in, in flight. Its button's spinner and its hint follow the control that
  // sent the request, as the banners do: the form's button spins for the request the form sent.
  const formSigningIn = control === 'form' && signingIn;

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
    setControl('form');
    try {
      await login({ email: data.email, password: data.password }).unwrap();
      // returnTo: land where the guard interrupted, not always the dashboard.
      navigate(navState.from?.pathname ?? '/dashboard', { replace: true });
    } catch {
      // Surfaced through the mutation's error state below.
    }
  };

  /*
    "Try the demo": claim a copy and go to it.

    The claim is all this sends. What a claim that succeeded does to the visitor is done where its
    answer arrives, for every caller of the claim alike: the new owner is signed in
    (src/features/auth/authSlice.ts), the copy's sign-in details are kept in the browser and the
    cache is dropped (src/features/auth/sessionMiddleware.ts). Nothing here reads the answer,
    which holds the copy's password: that the promise resolved is all this goes on.

    It lands on the dashboard whatever page the guard interrupted: a sign-in goes back to the page
    its visitor was sent away from, and a new copy starts at its dashboard.
  */
  const tryTheDemo = async () => {
    setControl('claim');
    try {
      await claim().unwrap();
      navigate('/dashboard', { replace: true });
    } catch {
      // Surfaced through the mutation's error state below.
    }
  };

  /*
    The limiter's refusal (`RATE_LIMIT_EXCEEDED`), from whichever request it answered.

    Off the demo it is said under "Sign in", inside the form, as before. On the demo it is said
    with the page's other alerts, above the demo's block, whichever control was refused, and
    every button that sends a request waits until its countdown ends.
  */
  const limiterBanner = rateLimited && lockDeadline !== null && (
    <>
      <MessageBar intent="warning" role="alert" className={demo ? styles.errorMessage : undefined}>
        <MessageBarBody>Too many attempts from your connection.</MessageBarBody>
      </MessageBar>
      {/* Sibling, not child — see the account-lock banner below. */}
      <RetryCountdown deadline={lockDeadline} onElapsed={() => setElapsedDeadline(lockDeadline)} />
    </>
  );

  return (
    <AuthLayout
      headline="Banking Made Simple, Secure, and Smart"
      intro="Manage your finances with confidence. Experience modern banking with powerful tools designed for your success."
      title="Welcome back"
      subtitle={demo ? DEMO_SUBTITLE : 'Sign in to your account to continue'}
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
        <>
          <MessageBar intent="error" role="alert" className={styles.errorMessage}>
            <MessageBarBody>
              Too many failed sign-in attempts — your account is temporarily locked.
            </MessageBarBody>
          </MessageBar>
          {/* Sibling, not child: role="alert" implies aria-atomic, so a nested timer would
              re-announce the whole banner every second. It carries its own polite region. */}
          <RetryCountdown
            deadline={lockDeadline}
            onElapsed={() => setElapsedDeadline(lockDeadline)}
          />
        </>
      )}

      {/* A claim's two refusals of its own, worded here by their code and never in the server's
          words (backend/src/AzureBank.Shared/Exceptions/DemoRefusalException.cs): the server's
          sentence for an empty pool does not say that copies come back, and its sentence for the
          day's limit ends on "later". Neither is counted down, although the day's limit names a
          wait: the countdowns on this page belong to the lock and to the limiter, by their
          codes. */}
      {problem?.errorCode === 'DEMO_POOL_EMPTY' && (
        <MessageBar intent="error" role="alert" className={styles.errorMessage}>
          <MessageBarBody>{CLAIM_POOL_EMPTY}</MessageBarBody>
        </MessageBar>
      )}

      {problem?.errorCode === 'DEMO_DAILY_LIMIT' && (
        <MessageBar intent="error" role="alert" className={styles.errorMessage}>
          <MessageBarBody>{CLAIM_DAILY_LIMIT}</MessageBarBody>
        </MessageBar>
      )}

      {/* Whatever has no banner of its own above. The list is what keeps a refusal to one alert:
          a code that has its own banner and is missing here is said twice. */}
      {problem &&
        ![
          'INVALID_CREDENTIALS',
          'ACCOUNT_LOCKED',
          'RATE_LIMIT_EXCEEDED',
          'DEMO_POOL_EMPTY',
          'DEMO_DAILY_LIMIT',
        ].includes(problem.errorCode) && (
          <MessageBar intent="error" role="alert" className={styles.errorMessage}>
            <MessageBarBody>
              {problem.detail || 'Something went wrong. Please try again.'}
            </MessageBarBody>
          </MessageBar>
        )}

      {demo && limiterBanner}

      {/* On the demo the page leads with the demo, and the form is the second way in: for whoever
          has a copy's email and password. The line above the form is words, not a link or a
          button: the form's own button is the page's one control named "Sign in". */}
      {demo && (
        <>
          <DemoEntry
            pending={claiming ? 'claim' : null}
            disabled={busy || rateLimited}
            onTry={() => void tryTheDemo()}
          />
          <AuthDivider />
          <Text as="p" className={styles.haveACopy}>
            {HAVE_A_COPY_SIGN_IN}
          </Text>
        </>
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
            disabled={busy || rateLimited}
          >
            {formSigningIn ? <Spinner size="tiny" /> : 'Sign in'}
          </Button>
        )}
        {/* Under the button that started the sign-in, and outside every alert. */}
        <WaitHint active={formSigningIn} kind="write" />

        {!demo && limiterBanner}
      </form>

      {/* On the demo this page offers no registration: a visitor gets an account by claiming a
          copy. */}
      {!demo && (
        <>
          <AuthDivider />

          <AuthCrossLink prompt="Don't have an account?" to="/register" label="Create account" />
        </>
      )}
    </AuthLayout>
  );
}

export default LoginPage;
