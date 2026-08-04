import { configureStore } from '@reduxjs/toolkit';
import { apiSlice } from '../features/api/apiSlice';
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

export function makeStore() {
  return configureStore({
    reducer: { [apiSlice.reducerPath]: apiSlice.reducer },
    middleware: (getDefault) => getDefault().concat(apiSlice.middleware),
  });
}

export type IntegrationStore = ReturnType<typeof makeStore>;

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
  const unsubscribe = subscribeStepUp(() => {
    if (!getStepUpSnapshot()) return; // the unmount notification
    requests += 1;
    void store
      .dispatch(apiSlice.endpoints.verifyPin.initiate({ pin: FIXTURES.pin }))
      .then((result) => {
        settleStepUp('error' in result && result.error ? 'cancelled' : 'elevated');
      });
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

/** Decline every step-up, to exercise the STEP_UP_CANCELLED branch against the real 403. */
export function autoCancel() {
  const unsubscribe = subscribeStepUp(() => {
    if (getStepUpSnapshot()) settleStepUp('cancelled');
  });
  return {
    dispose() {
      unsubscribe();
      __resetStepUpController();
    },
  };
}

export function idempotencyKey(): string {
  return crypto.randomUUID();
}
