import { useGetMeQuery } from '../api/apiSlice';

/**
 * The ONE bootstrap probe (D6): fires GET /bff/auth/me once at mount, resolving the
 * boot 'unknown' into 'authenticated' (cookie alive) or 'anonymous' (probe 401s — no
 * banner, no error surface). RTK Query dedupes StrictMode's double mount, and the live
 * subscription re-probes automatically after a cache reset.
 */
export function AuthBootstrap() {
  useGetMeQuery();
  return null;
}
