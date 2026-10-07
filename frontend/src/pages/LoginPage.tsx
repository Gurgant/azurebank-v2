import { useEffect, useMemo, useReducer, useState, useSyncExternalStore } from 'react';
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
import { DemoEntry, StartOverDialog } from '../features/demo';
import {
  forgetDemoCopy,
  getDemoCopySnapshot,
  subscribeDemoCopy,
} from '../features/demo/demoCopyStorage';
import { isDemoMode } from '../features/demo/demoMode';
import {
  CLAIM_DAILY_LIMIT,
  CLAIM_POOL_EMPTY,
  COPY_NO_LONGER_AVAILABLE,
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

/**
 * Which of the page's controls sent a request: the form's "Sign in", "Continue with my copy", or
 * "Try the demo". The first two send the same request, a sign-in, and are told apart only here.
 */
type LoginControl = 'form' | 'continue' | 'claim';

export function LoginPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const location = useLocation();
  // Asked here, in the component, and not when this module loads: the page says whether it is the
  // demo with a tag, and a test puts that tag on the page after the modules are in
  // (src/features/demo/demoMode.ts).
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

    The control also says WHOSE sign-in was refused. The form and "Continue with my copy" send
    the same request and get the same answers, and two of those answers mean something else for a
    pair the browser kept than for a pair somebody typed: see `continueWithMyCopy` below.

    Off the demo nothing sends a claim and nothing but the form signs in, and `problem` is the
    form's sign-in's, as before.
  */
  const [control, setControl] = useState<LoginControl | null>(null);
  const error = control === 'claim' ? claimError : signInError;
  // One flag over both requests: while either runs, every button that sends one waits for it.
  const busy = signingIn || claiming;
  // The form's own sign-in, in flight. Its button's spinner and its hint follow the control that
  // sent the request, as the banners do: the form's button spins for the request the form sent,
  // and not for the sign-in "Continue with my copy" sent through the same hook.
  const formSigningIn = control === 'form' && signingIn;
  const continuing = control === 'continue' && signingIn;

  /*
    THE COPY THIS BROWSER KEEPS, on the demo: the sign-in details a claim left in its storage
    (src/features/demo/demoCopyStorage.ts). The page offers it in the place of "Try the demo".

    Read from the storage at every render, through the storage module's snapshot. Nothing tells
    this tab what another tab did, so a copy the other tab forgot or replaced is gone, or is the
    other one, the next time this page is drawn, and not before. Off the demo the snapshot is
    `null` and the storage is not touched. In a tab whose browser refused to store its copy the
    snapshot is the copy that tab holds in memory, whatever another tab did since: the storage
    module says why.

    A COPY PAST ITS END IS NOT OFFERED, AND IS FORGOTTEN. The end is the kept copy's own
    `expiresAt`, compared with this browser's clock as it stood when the page opened. The clock
    is read once, in a state initialiser, and that instant is kept: every later render compares
    with it, so a copy the page offered when it opened is still offered after its end has passed
    under the open page. That comparison is the browser's alone and it removes the copy: a browser
    whose clock is ahead of the server's by more than the copy has left forgets a living copy
    here, with nothing said, and offers "Try the demo". One whose clock is behind offers a copy
    that has ended, and so does a page left open past the end: there the server's answer to
    "Continue with my copy" is what says so.
  */
  const stored = useSyncExternalStore(subscribeDemoCopy, getDemoCopySnapshot);
  const [openedAt] = useState(() => Date.now());
  const ended = stored !== null && Date.parse(stored.expiresAt) <= openedAt;
  const copy = ended ? null : stored;
  useEffect(() => {
    if (ended) forgetDemoCopy();
  }, [ended]);
  // "Forget this copy" was pressed here: the demo's block says so while it offers no copy. It
  // stops being said at the next "Continue with my copy", which is a press on a copy that came
  // back.
  const [forgotten, setForgotten] = useState(false);
  /*
    "Try the demo" was pressed here and its claim has not been refused: the page is waiting for
    the answer, or has it and is on its way to the dashboard.

    The answer puts the new copy under the key (src/features/auth/sessionMiddleware.ts) and this
    page is drawn again at once, before the dashboard is: the router changes the address and
    draws the next page when it is ready. Left to `copy` above, the page would be, for that
    moment, the page of a browser that "remembers a demo copy", with "Continue with my copy",
    "Get a new copy" and "Forget this copy" all ready to be pressed. So from the press until a
    refusal the demo's block stays as it was pressed, waiting, and so does the form's button.
  */
  const [claimSent, setClaimSent] = useState(false);
  // The copy the page offers: the one the browser keeps, and none from that press until a refusal.
  // The demo's block and the page's title both go by it, so they cannot say two things.
  const offeredCopy = claimSent ? null : copy;
  // The dialog that asks before a new copy takes the kept one's place.
  const [startingOver, setStartingOver] = useState(false);
  // A press that found nothing to send draws the page again, and nothing else: see below.
  const [, drawAgain] = useReducer((draws: number) => draws + 1, 0);

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
  // Whose lock it is. Met by the form, it takes the form's submit away, as before. Met by
  // "Continue with my copy", it is the kept copy's: that button waits its countdown out, and the
  // form keeps its submit, because the form may be for another address.
  const formLocked = accountLocked && control === 'form';
  const continueLocked = accountLocked && control === 'continue';

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

    THE KEY IS READ AT THE PRESS, as "Continue with my copy" reads it below. The button is drawn
    for a browser that keeps no copy, and another tab may have claimed one since. A claim sent
    then would put a second copy in that one's place with nobody asked, and end the session the
    other tab is signed in with. So a copy the page would offer is offered: nothing is sent, and
    the page is drawn again with "Continue with my copy" and "Get a new copy", which asks first.
    "Would offer" is the rule above, the copy's end against the instant the page opened: a copy
    past its end that the browser would not remove is still under the key, and is not in the way.
  */
  const tryTheDemo = async () => {
    const kept = getDemoCopySnapshot();
    if (kept !== null && Date.parse(kept.expiresAt) > openedAt) {
      drawAgain();
      return;
    }
    setControl('claim');
    setClaimSent(true);
    try {
      await claim().unwrap();
      navigate('/dashboard', { replace: true });
    } catch {
      // Surfaced through the mutation's error state below. The page is the one to press again.
      setClaimSent(false);
    }
  };

  /*
    "Continue with my copy": sign in with the pair this browser keeps, and land as the form's
    sign-in lands.

    THE PAIR IS READ FROM THE BROWSER AT THE PRESS, not taken from the copy the page was last
    drawn with. Another tab may have started over or forgotten the copy since: the copy to sign in
    to is the one the browser keeps now. If it keeps none any more, nothing is sent and the page
    is drawn again, which then offers "Try the demo".

    Two answers mean something else here than they do for the form, and the control recorded
    above is how the page tells them apart:

    - `INVALID_CREDENTIALS`. Nobody typed this pair, so it is not a typing mistake: a copy that
      has ended, or was handed to someone else, answers exactly as a wrong password does. The copy
      is forgotten, and the page says the copy is gone in the place of "Invalid email or
      password.", and offers the demo again.
    - `ACCOUNT_LOCKED`. The lock is the kept copy's: see `continueLocked` above.
  */
  const continueWithMyCopy = async () => {
    const kept = getDemoCopySnapshot();
    if (kept === null) {
      drawAgain();
      return;
    }
    setControl('continue');
    // The page offers a copy again, and this press is about that copy: what was forgotten here
    // before it is no longer the news. Without this, a copy found gone below would also be said
    // to have been forgotten, on a page where "Forget this copy" was once pressed.
    setForgotten(false);
    try {
      await login({ email: kept.email, password: kept.password }).unwrap();
      navigate(navState.from?.pathname ?? '/dashboard', { replace: true });
    } catch (refusal) {
      // Every other refusal is surfaced through the mutation's error state below, and so is this
      // one: forgetting the copy is what it has beside its sentence.
      if ((refusal as Partial<ApiProblem> | undefined)?.errorCode === 'INVALID_CREDENTIALS') {
        forgetDemoCopy();
      }
    }
  };

  // "Forget this copy": the browser keeps nothing of it from here on, and the page says so.
  const forgetThisCopy = () => {
    forgetDemoCopy();
    setForgotten(true);
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
      // "Back" is for somebody who has been here. On the demo that is a browser the page offers
      // a copy to; one it offers "Try the demo" has not been, and until 2026-10-06 was greeted
      // "Welcome back" all the same. Off the demo the page is the sign-in page it was.
      title={demo && offeredCopy === null ? 'Welcome' : 'Welcome back'}
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
          <MessageBarBody>
            {control === 'continue' ? COPY_NO_LONGER_AVAILABLE : 'Invalid email or password.'}
          </MessageBarBody>
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
            copy={offeredCopy}
            pending={claimSent || claiming ? 'claim' : continuing ? 'continue' : null}
            disabled={busy || rateLimited || claimSent}
            continueLocked={continueLocked}
            forgotten={forgotten}
            onTry={() => void tryTheDemo()}
            onContinue={() => void continueWithMyCopy()}
            onGetNew={() => setStartingOver(true)}
            onForget={forgetThisCopy}
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
            carries the countdown. The form's own lock, that is: a lock "Continue with my copy"
            met leaves this button where it is. */}
        {!formLocked && (
          <Button
            appearance="primary"
            size="large"
            className={styles.submitButton}
            type="submit"
            disabled={busy || rateLimited || claimSent}
          >
            {formSigningIn ? <Spinner size="tiny" /> : 'Sign in'}
          </Button>
        )}
        {/* Under the button that started the sign-in, and outside every alert. */}
        <WaitHint active={formSigningIn} kind="write" />

        {!demo && limiterBanner}
      </form>

      {/* "Get a new copy" asks first, in the one dialog that asks before a claim replaces a kept
          copy. Kept in the page and opened by its flag, as the dialog under it is used
          (src/components/shared/ConfirmDialog.tsx): focus goes back to the button that opened it
          when it closes. A new copy starts at its dashboard, as one from "Try the demo" does. On
          the demo only: off it the page has no dialog, open or closed. */}
      {demo && (
        <StartOverDialog
          isOpen={startingOver}
          onClose={() => setStartingOver(false)}
          onStartedOver={() => navigate('/dashboard', { replace: true })}
        />
      )}

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
