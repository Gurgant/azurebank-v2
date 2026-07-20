import { createSlice, type Action } from '@reduxjs/toolkit';
import type { BffLoginResponse, BffMeResponse, UserSessionInfo } from '../../api/bffTypes';

/**
 * Client auth state (D3/D6). There is NO token here — the JWT never reaches the
 * browser (BFF pattern, ADR-0001): transport auth is the __Host- session cookie,
 * carried automatically by fetchBaseQuery's credentials: 'same-origin'.
 *
 *  - 'unknown'       boot: the B3 probe (GET /bff/auth/me) is in flight; guards hold.
 *  - 'anonymous'     no session — the probe 401'd at boot, or the user logged out.
 *                    Never shows an expiry banner (D6: reason 'expired' is only set by
 *                    401s arriving AFTER an authenticated boot).
 *  - 'authenticated' live session; `user` is populated.
 *  - 'expired'       a 401 arrived while authenticated — set by sessionMiddleware,
 *                    which also resets the RTK Query cache. Login shows the expiry note.
 */
export type AuthStatus = 'unknown' | 'anonymous' | 'authenticated' | 'expired';

interface RtkQueryAction extends Action {
  meta?: { arg?: { endpointName?: string }; requestStatus?: string };
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
  return (action: Action): boolean => {
    const a = action as RtkQueryAction;
    return (
      (a.type === 'api/executeQuery/rejected' || a.type === 'api/executeMutation/rejected') &&
      a.meta?.arg?.endpointName === endpoint
    );
  };
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
  },
  extraReducers: (builder) => {
    // Matchers are STRING-based on the RTK Query action shape ('api/...' +
    // meta.arg.endpointName) rather than apiSlice.endpoints.X.matchFulfilled:
    // importing the slice instance here proved fragile in the Vite dev runtime
    // (module-instance identity), while the wire shape below is the stable contract.
    builder
      .addMatcher(isAuthEndpointFulfilled(['login', 'register', 'getMe']), (state, action) => {
        // Registration IS a login: the BFF sets the session cookie on the 201.
        state.status = 'authenticated';
        state.user = (action.payload as BffLoginResponse | BffMeResponse).user;
      })
      .addMatcher(isAuthEndpointRejected('getMe'), (state) => {
        // Only the BOOT probe's failure resolves here (D3): unknown -> anonymous, no
        // banner. Post-boot 401s are sessionMiddleware's business ('expired' must not
        // be downgraded; 'authenticated' flips there so the cache reset rides along).
        if (state.status === 'unknown') {
          state.status = 'anonymous';
        }
      })
      .addMatcher(isAuthEndpointFulfilled(['logout']), (state) => {
        state.status = 'anonymous';
        state.user = null;
      });
  },
});

export const { sessionExpired } = authSlice.actions;
export const authReducer = authSlice.reducer;

export const selectAuthStatus = (state: { auth: AuthState }) => state.auth.status;
export const selectCurrentUser = (state: { auth: AuthState }) => state.auth.user;
