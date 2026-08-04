import { configureStore } from '@reduxjs/toolkit';
import { apiSlice } from '../features/api/apiSlice';
import { authReducer } from '../features/auth/authSlice';
import { sessionMiddleware } from '../features/auth/sessionMiddleware';
import {
  __resetStepUpController,
  settleStepUp,
  subscribeStepUp,
  getStepUpSnapshot,
} from '../features/auth/stepUpController';

/**
 * The real store, the real endpoints, the real base query — pointed at the real backend.
 *
 * The only thing this file constructs is a Redux store; every layer under it is production code
 * imported unmodified. `problemBaseQuery` pins its baseUrl to `window.location.origin`, and the
 * vitest config sets the jsdom document URL to the BFF, so the data layer reaches the real stack
 * without a single production line being aware of it.
 */

export const BFF_ORIGIN = 'http://localhost:5000';

/** The dev database's seeded user. Fixtures are not contract — only shapes and codes are. */
export const FIXTURES = {
  email: 'admin@azurebank.dev',
  password: 'Test123!',
  pin: '123456',
} as const;

/**
 * Mirrors `src/app/store.ts`, and the parity is load-bearing rather than tidiness.
 *
 * An earlier version wired `apiSlice` alone. That store cannot observe the global 401 rule (D3),
 * which lives entirely in `sessionMiddleware`: a 401 while AUTHENTICATED dispatches
 * `sessionExpired()` and resets the whole RTK Query cache, so financial data never outlives the
 * session it was fetched under. Omitting it meant the suite could pass while that rule was broken —
 * precisely the "green and wrong" state this whole layer exists to prevent. `sessionMiddleware`
 * also READS `getState().auth.status`, so the two are not independent: adding the middleware
 * without the reducer would throw on the first 401.
 *
 * `devTools` is the one deliberate omission — there is no extension to talk to in node, and the
 * production setting exists to keep a withdraw's PIN out of it.
 */
export function makeStore() {
  return configureStore({
    reducer: {
      auth: authReducer,
      [apiSlice.reducerPath]: apiSlice.reducer,
    },
    middleware: (getDefault) => getDefault().concat(apiSlice.middleware, sessionMiddleware),
  });
}

export type IntegrationStore = ReturnType<typeof makeStore>;

/** The production auth status, for tests that assert on session lifecycle. */
export function authStatus(store: IntegrationStore): string {
  return (store.getState() as { auth: { status: string } }).auth.status;
}

/** Run an endpoint and return RTK Query's settled result, errors included. */
export async function run<T>(promise: {
  unwrap(): Promise<T>;
}): Promise<{ ok: true; data: T } | { ok: false; error: unknown }> {
  try {
    return { ok: true, data: await promise.unwrap() };
  } catch (error) {
    return { ok: false, error };
  }
}

export async function signIn(store: IntegrationStore): Promise<void> {
  const result = await store.dispatch(
    apiSlice.endpoints.login.initiate({
      email: FIXTURES.email,
      password: FIXTURES.password,
    }),
  );

  if ('error' in result && result.error) {
    const problem = result.error as { status?: unknown; errorCode?: string };
    if (problem.status === 429) {
      throw new Error(
        'Login was rate-limited (429). The BFF allows 10 auth requests per 60s per IP ' +
          '(RateLimiting:AuthPermitLimit) and verify-pin counts against the same budget. ' +
          'Wait about a minute and re-run.',
      );
    }
    throw new Error(`Login failed: ${JSON.stringify(result.error)}`);
  }
}

/**
 * Stand in for <StepUpModal/> — the ONE piece of the flow that is genuinely a UI component.
 *
 * This is not a mock of the backend. When the interceptor asks for elevation, this performs the
 * SAME real `verify-pin` call the modal performs and settles the same controller promise, so what
 * runs underneath is the production path end to end: real 403, real PIN verification, real replay.
 * Returns a disposer plus a counter, because "how many times was elevation requested" is itself
 * one of the properties worth asserting.
 */
export function autoElevate(store: IntegrationStore) {
  let requests = 0;
  let verifyFailure: string | null = null;

  const unsubscribe = subscribeStepUp(() => {
    if (!getStepUpSnapshot()) return; // the unmount notification
    requests += 1;
    void store
      .dispatch(apiSlice.endpoints.verifyPin.initiate({ pin: FIXTURES.pin }))
      .then((result) => {
        /*
          A REJECTED PIN IS A 200, NOT AN ERROR — the single most dangerous thing to get wrong in
          this file. `BffAuthController.VerifyPin` answers:

              Ok(new ApiResponse<BffPinVerificationResponse> {
                  Data = new BffPinVerificationResponse {
                      Verified = false, AuthLevel = 1, PinExpiresAt = null },
                  Message = "Invalid PIN" });

          so an error check alone takes the success branch and settles 'elevated' for a PIN the
          server just refused. The interceptor then replays against a session still at level 1, gets
          another 403, and — since `baseQueryWithStepUp` deliberately does not re-intercept its own
          replay — the test fails as STEP_UP_REQUIRED, which reads as "elevation didn't stick"
          rather than "the PIN was wrong".

          The real cost is not the confusing message. `ValidationRules.MaxPinAttempts` is 3 and
          `PinLockoutMinutes` is 15, and this harness calls verify-pin from two places, so a fixture
          PIN that ever drifts from the seeded value burns two attempts per run and LOCKS the shared
          dev account on the second — poisoning every later run for a quarter of an hour. A
          successful verification resets the counter (`PinService.ResetLockoutAsync`), which is why
          the healthy path never accumulates.
        */
        if ('error' in result && result.error) {
          verifyFailure = `verify-pin failed: ${JSON.stringify(result.error)}`;
        } else if (!result.data?.verified) {
          verifyFailure =
            'verify-pin answered 200 but verified=false — the fixture PIN is wrong. ' +
            'Each attempt counts against MaxPinAttempts (3); the account locks for 15 minutes.';
        }

        /*
          Settled either way, because the interceptor's promise must resolve or the run hangs, and
          'cancelled' is the only other outcome `StepUpOutcome` allows. The recorded cause is what
          keeps that from impersonating a deliberate user cancellation.
        */
        settleStepUp(verifyFailure ? 'cancelled' : 'elevated');
      });
  });

  return {
    get requests() {
      return requests;
    },
    /** Non-null when verify-pin itself failed. Assert it is null BEFORE reading the outcome. */
    get verifyFailure() {
      return verifyFailure;
    },
    dispose() {
      unsubscribe();
      __resetStepUpController();
    },
  };
}

/**
 * Decline every step-up, to exercise the STEP_UP_CANCELLED branch against the real 403.
 *
 * Counts prompts for the same reason `autoElevate` does. `requestStepUp`'s `inflight` mutex means
 * concurrent 403s share one prompt, and `baseQueryWithStepUp` deliberately does not re-intercept
 * its own replay — so "exactly one" is the current contract, and a counter is what would notice if
 * either of those two guarantees were lost.
 */
export function autoCancel() {
  let requests = 0;
  const unsubscribe = subscribeStepUp(() => {
    if (!getStepUpSnapshot()) return;
    requests += 1;
    settleStepUp('cancelled');
  });
  return {
    get requests() {
      return requests;
    },
    dispose() {
      unsubscribe();
      __resetStepUpController();
    },
  };
}

export function idempotencyKey(): string {
  return crypto.randomUUID();
}
