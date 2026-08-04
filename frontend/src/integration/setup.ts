import { afterAll, beforeAll } from 'vitest';
import { BFF_ORIGIN } from './harness';

/**
 * Phase 2 harness. Two jobs, and only two — neither of them "being a backend".
 *
 * 1. Refuse to run when the stack is down, rather than skipping. A skipped integration suite
 *    reports success without having asked the server anything, which is the exact failure this
 *    layer exists to prevent.
 * 2. Give `fetch` a cookie jar, because the runtime has none.
 *
 * Nothing here fakes a response, and nothing branches on a target. If a test in this directory
 * passes, a real API + BFF + SQL Server answered it.
 */

/**
 * MEASURED, not assumed. Under vitest's jsdom environment `fetch` is node's (undici), which has no
 * cookie jar at all, and the BFF's session cookie is HttpOnly so jsdom's own jar would not hold it
 * either. Probed against the running stack:
 *
 *   login              -> 200, set-cookie: .AzureBank.Session=...; path=/; samesite=strict; httponly
 *   document.cookie    -> "" (empty)
 *   GET /api/accounts  -> 401 AUTH_TOKEN_MISSING
 *
 * So without this shim EVERY authenticated request in this suite would 401, and the suite would be
 * a very elaborate way of testing the signed-out path. The shim restores exactly ONE browser
 * behaviour the runtime is missing — store `Set-Cookie`, send it back to the same origin — and does
 * nothing else. It never invents a status, a header or a body.
 */
let jar = new Map<string, string>();

export function resetCookieJar(): void {
  jar = new Map();
}

/** The cookie header the jar would send, for tests that need to assert on the session directly. */
export function currentCookieHeader(): string {
  return [...jar].map(([name, value]) => `${name}=${value}`).join('; ');
}

const realFetch = globalThis.fetch;

async function jarFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  /*
    Normalise through `Request` FIRST. RTK Query's fetchBaseQuery calls its fetchFn with a fully
    built `Request` and no init at all, so reading `init?.headers` sees nothing and passing a
    `headers` option back overrides the request's own — which silently dropped `Content-Type:
    application/json` and turned every login into `415 Unsupported Media Type`. Measured, not
    reasoned: the first run of this suite failed exactly that way.
  */
  const request = new Request(input as RequestInfo, init);
  const headers = new Headers(request.headers);
  const cookie = currentCookieHeader();
  if (cookie && !headers.has('cookie')) headers.set('cookie', cookie);

  // A GET/HEAD Request has no body to read back; anything else is buffered so the clone can send it.
  const hasBody = request.method !== 'GET' && request.method !== 'HEAD';
  const response = await realFetch(request.url, {
    method: request.method,
    headers,
    body: hasBody ? await request.arrayBuffer() : undefined,
    redirect: request.redirect,
    signal: request.signal,
  });

  for (const raw of response.headers.getSetCookie?.() ?? []) {
    const [pair] = raw.split(';');
    const index = pair.indexOf('=');
    if (index <= 0) continue;
    const name = pair.slice(0, index).trim();
    const value = pair.slice(index + 1).trim();
    /*
      An expiry in the past is a DELETION, and treating it as a value is how a "logged out" test
      quietly keeps its session. The BFF clears the session cookie exactly this way on logout.
    */
    const expired = /;\s*expires=([^;]+)/i.exec(raw);
    if (value === '' || (expired && Date.parse(expired[1]) <= Date.now())) {
      jar.delete(name);
    } else {
      jar.set(name, value);
    }
  }

  return response;
}

beforeAll(async () => {
  let reachable = false;
  try {
    // Any answer at all proves the BFF is listening; 401 is the expected one for an anonymous call.
    const probe = await realFetch(`${BFF_ORIGIN}/api/accounts`, {
      signal: AbortSignal.timeout(5_000),
    });
    reachable = probe.status > 0;
  } catch {
    reachable = false;
  }

  if (!reachable) {
    throw new Error(
      `The integration suite needs the REAL stack and nothing answered at ${BFF_ORIGIN}.\n` +
        'Start azurebank-api (:7215) and azurebank-bff (:5000) with the database migrated and\n' +
        'seeded, then re-run. This suite has no mock target by design — it FAILS rather than\n' +
        'skipping, because a skipped run would report success without asking the server anything.',
    );
  }

  globalThis.fetch = jarFetch as typeof fetch;
});

afterAll(() => {
  globalThis.fetch = realFetch;
});
