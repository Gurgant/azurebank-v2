import { BASE_URL, FIXTURES } from './target';

/**
 * A deliberately dumb HTTP client for the contract suite.
 *
 * It does NOT go through RTK Query or `problemBaseQuery`, and that is the point: those layers
 * normalise the wire (an absent `errorCode` becomes `HTTP_<status>`, an empty body becomes `null`
 * rather than a parse failure), so testing through them would assert what the app BELIEVES rather
 * than what the server SENT. Drift hides in exactly that gap. Everything here is raw status,
 * headers and parsed body.
 */

export interface Wire {
  status: number;
  headers: Headers;
  /** Parsed JSON, or the raw text when the body is not JSON, or null when it is empty. */
  body: unknown;
}

/**
 * A cookie jar, because the two targets authenticate differently and the assertions must not care.
 *
 * The real BFF issues an HttpOnly session cookie; node's `fetch` has no jar, so one is kept here by
 * hand. The mock authenticates from in-memory state and ignores cookies entirely — sending them is
 * harmless there. Neither fact reaches a test.
 */
let jar = '';

export function resetJar(): void {
  jar = '';
}

export async function call(
  path: string,
  init: RequestInit & { anonymous?: boolean } = {},
): Promise<Wire> {
  const { anonymous, headers, ...rest } = init;

  /*
    Built through `Headers` rather than by spreading into an object literal. `RequestInit.headers`
    legitimately accepts a Headers instance or an array of pairs as well as a record, and spreading
    either of those yields `{}` — the header would vanish silently. On a suite whose whole job is to
    notice small differences, a dropped `Idempotency-Key` would read as a backend bug.
  */
  const merged = new Headers(headers);
  if (rest.body && !merged.has('Content-Type')) {
    merged.set('Content-Type', 'application/json');
  }
  // `anonymous` is how the "this endpoint must reject an unauthenticated caller" tests are written
  // without having to tear the session down and rebuild it.
  if (!anonymous && jar) {
    merged.set('cookie', jar);
  }

  const response = await fetch(`${BASE_URL}${path}`, { ...rest, headers: merged });

  const setCookie = response.headers.getSetCookie?.() ?? [];
  if (setCookie.length > 0) {
    jar = setCookie.map((c) => c.split(';')[0]).join('; ');
  }

  const text = await response.text();
  let body: unknown = null;
  if (text.length > 0) {
    try {
      body = JSON.parse(text);
    } catch {
      body = text;
    }
  }

  return { status: response.status, headers: response.headers, body };
}

/**
 * Turn a puzzling red into an actionable one rather than letting it read as contract drift.
 *
 * The BFF applies an `auth` rate-limiter policy of 10 requests per 60s per IP to the login and PIN
 * routes. The first draft of this suite signed in in `beforeEach` — eleven logins across four files
 * — and the run tripped it, failing two tests with a 429 that had nothing to do with any contract.
 * The mock has no limiter at all, so this is a constraint only the real target could have taught,
 * and it is why `login()` is called once per FILE.
 *
 * Shared by BOTH auth calls on purpose: `verify-pin` sits under the same policy, so a rate-limited
 * step-up would otherwise surface as a bare 429 while login got the explanation.
 */
function rejectIfRateLimited(result: Wire): Wire {
  if (result.status === 429) {
    throw new Error(
      'Auth request was rate-limited (429). The BFF allows 10 auth requests per 60s per IP ' +
        '(RateLimiting:AuthPermitLimit), and login + verify-pin both count. ' +
        'Wait about a minute and re-run.',
    );
  }
  return result;
}

export async function login(): Promise<Wire> {
  return rejectIfRateLimited(
    await call('/bff/auth/login', {
      method: 'POST',
      anonymous: true,
      body: JSON.stringify({ email: FIXTURES.email, password: FIXTURES.password }),
    }),
  );
}

/** Raise the session to AuthLevel 2, which the money and reveal endpoints demand. */
export async function elevate(): Promise<Wire> {
  return rejectIfRateLimited(
    await call('/bff/auth/verify-pin', {
      method: 'POST',
      body: JSON.stringify({ pin: FIXTURES.pin }),
    }),
  );
}

/** The caller's first account id. Both targets seed at least one. */
export async function firstAccountId(): Promise<string> {
  const { status, body } = await call('/api/accounts');
  if (status !== 200) throw new Error(`GET /api/accounts returned ${status}`);
  const data = (body as { data?: { id: string }[] }).data;
  if (!data?.length) throw new Error('No seeded accounts to test against.');
  return data[0].id;
}

/** A fresh idempotency key. The money endpoints reject a request without one. */
export function idempotencyKey(): string {
  return crypto.randomUUID();
}

/** Narrowing helper: the ProblemDetails-ish members these assertions care about. */
export function asProblem(body: unknown): {
  type?: string;
  title?: string;
  detail?: string;
  status?: number;
  errorCode?: string;
  /** The requested path, echoed back. Emitted by the API's own problem responses, not the BFF's. */
  instance?: string;
  errors?: Record<string, string[]>;
} {
  return (body ?? {}) as Record<string, never>;
}
