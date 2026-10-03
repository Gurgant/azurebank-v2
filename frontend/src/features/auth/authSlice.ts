import { createSlice, type Action } from '@reduxjs/toolkit';
import type { BffLoginResponse, BffMeResponse, UserSessionInfo } from '../../api/bffTypes';
import type { ApiProblem } from '../../api/problemBaseQuery';

/**
 * Client auth state (D3/D6). There is NO token here — the JWT never reaches the
 * browser (BFF pattern, ADR-0001): transport auth is the __Host- session cookie,
 * carried automatically by fetchBaseQuery's credentials: 'same-origin'.
 *
 *  - 'unknown'       boot: the probe (GET /bff/auth/me) is in flight; guards hold. It stays
 *                    'unknown' when the probe fails without an answer about this visitor (no
 *                    answer, a 5xx), and ProtectedRoute says the service is unavailable.
 *  - 'anonymous'     no session — the probe answered 4xx at boot, or the user logged out.
 *                    Never shows an expiry banner (D6: reason 'expired' is only set by
 *                    401s arriving AFTER an authenticated boot).
 *  - 'authenticated' live session; `user` is populated.
 *  - 'expired'       a 401 arrived while authenticated — set by sessionMiddleware,
 *                    which also resets the RTK Query cache. Login shows the expiry note.
 */
export type AuthStatus = 'unknown' | 'anonymous' | 'authenticated' | 'expired';

interface RtkQueryAction extends Action {
  meta?: { arg?: { endpointName?: string }; requestStatus?: string; condition?: boolean };
  payload?: unknown;
}

function isAuthEndpointFulfilled(endpoints: string[]) {
  return (action: Action): action is RtkQueryAction & { payload: unknown } => {
    const a = action as RtkQueryAction;
    return a.type === 'api/executeQuery/fulfilled' || a.type === 'api/executeMutation/fulfilled'
      ? endpoints.includes(a.meta?.arg?.endpointName ?? '')
      : false;
  };
}

function isAuthEndpointRejected(endpoint: string) {
  return (action: Action): action is RtkQueryAction => {
    const a = action as RtkQueryAction;
    return (
      (a.type === 'api/executeQuery/rejected' || a.type === 'api/executeMutation/rejected') &&
      a.meta?.arg?.endpointName === endpoint
    );
  };
}

/**
 * Whether a failed boot probe says this visitor has no session, rather than saying nothing.
 *
 * A 4xx is the BFF answering about this visitor: a 401 has no session to find, and a 403 or a
 * 429 is still the BFF turning them away, so the sign-in page is where they belong. Anything else
 * is the service failing to answer — no answer (a failed fetch, or none within 65 s), an answer
 * that could not be read, a 5xx, a rejection with no status (a body that failed its schema) — and
 * says nothing about the session, which may well be alive: sending the visitor to sign in would
 * read as being signed out.
 *
 * A rejection with `meta.condition` set is not a probe at all: RTK refused to start a forced
 * `getMe` because one was already running, and that one will decide.
 */
function probeSaysSignedOut(action: RtkQueryAction): boolean {
  if (action.meta?.condition) return false;
  const status = (action.payload as Partial<ApiProblem> | undefined)?.status;
  return typeof status === 'number' && status >= 400 && status < 500;
}

interface AuthState {
  status: AuthStatus;
  user: UserSessionInfo | null;
}

const initialState: AuthState = {
  status: 'unknown',
  user: null,
};

const authSlice = createSlice({
  name: 'auth',
  initialState,
  reducers: {
    sessionExpired: (state) => {
      state.status = 'expired';
      state.user = null;
    },
    /**
     * A deliberate sign-out that could not be confirmed by a successful logout call.
     *
     * `'anonymous'` was reachable only through the logout-fulfilled matcher below, which means a
     * user who asked to sign out of a session the server had ALREADY destroyed had nowhere to go:
     * the call answers 401, the matcher never fires, and they stay on screen. A 401 there is the
     * outcome they wanted, not a failure, and this is how it gets recorded.
     *
     * It is NOT a substitute for the mutation. Anything other than success-or-401 leaves the
     * cookie's fate unknown, so it never records a sign-out: a sign-out the visitor asked for
     * leaves them signed in and says it could not be done, and one the deadline forced uses
     * `sessionExpired` instead — the honest description of a session whose end could not be
     * verified.
     */
    signedOut: (state) => {
      state.status = 'anonymous';
      state.user = null;
    },
  },
  extraReducers: (builder) => {
    // Matchers are STRING-based on the RTK Query action shape ('api/...' +
    // meta.arg.endpointName) rather than apiSlice.endpoints.X.matchFulfilled:
    // importing the slice instance here proved fragile in the Vite dev runtime
    // (module-instance identity), while the wire shape below is the stable contract.
    builder
      .addMatcher(
        isAuthEndpointFulfilled(['login', 'register', 'getMe', 'claimDemoCopy']),
        (state, action) => {
          // Registration IS a login: the BFF sets the session cookie on the 201.
          //
          // A demo claim is one too: its answer is a sign-in's with the copy added
          // (bffDemoClaimResponseSchema), so `user` is read the same way. The name in the list is
          // the endpoint's in apiSlice.ts, one word in two files; src/features/demo/claim.test.ts
          // fails if either moves alone.
          state.status = 'authenticated';
          state.user = (action.payload as BffLoginResponse | BffMeResponse).user;
        },
      )
      .addMatcher(isAuthEndpointRejected('getMe'), (state, action) => {
        // Only the BOOT probe's failure resolves here (D3): unknown -> anonymous, no
        // banner. Post-boot 401s are sessionMiddleware's business ('expired' must not
        // be downgraded; 'authenticated' flips there so the cache reset rides along).
        // A failure that is not an answer about the visitor leaves 'unknown' as it is.
        if (state.status === 'unknown' && probeSaysSignedOut(action)) {
          state.status = 'anonymous';
        }
      })
      .addMatcher(isAuthEndpointFulfilled(['logout']), (state) => {
        state.status = 'anonymous';
        state.user = null;
      });
  },
});

export const { sessionExpired, signedOut } = authSlice.actions;
export const authReducer = authSlice.reducer;

export const selectAuthStatus = (state: { auth: AuthState }) => state.auth.status;
export const selectCurrentUser = (state: { auth: AuthState }) => state.auth.user;
