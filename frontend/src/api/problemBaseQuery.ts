import { fetchBaseQuery } from '@reduxjs/toolkit/query/react';
import type {
  BaseQueryFn,
  FetchArgs,
  FetchBaseQueryError,
  FetchBaseQueryMeta,
} from '@reduxjs/toolkit/query';
import { z } from 'zod';
import { CONNECTION_FAILED, SERVICE_UNAVAILABLE } from './problemMessages';

/**
 * The ONE typed error channel of the data layer. Every RTK Query hook surfaces
 * `error: ApiProblem | undefined` — components never touch raw fetch errors.
 *
 * Sources, in normalization order:
 *  1. step-up 403s are recognized from the X-Auth-Level-Required HEADER before any body
 *     parsing — AuthLevelMiddleware's 403 body is a bare shape, not ProblemDetails, and
 *     must never reach the ProblemDetails path (Decision D2);
 *  2. RFC 9457 ProblemDetails + errorCode + bare 32-hex traceId (the API's envelope);
 *     validation 400s carry an `errors` dict but no errorCode — synthesized here as
 *     VALIDATION_ERROR so consumers always have a code to branch on (Decision D5);
 *  3. transport failures normalize to status 'NETWORK' (`TIMEOUT_ERROR` when the SPA gave up
 *     waiting, `NETWORK_ERROR` otherwise); unparseable bodies to 'PARSE' — except a 503's, which
 *     is the outage whatever its body says (an ingress page is not JSON).
 *
 * A 503's `detail` is always the SPA's own sentence (`SERVICE_UNAVAILABLE`), never the server's:
 * the servers' 503 sentences promise a time ("Try again shortly.") or name a session, and the SPA
 * decides the words once, here, for every surface that renders `detail`. The `title` and the
 * support code stay the server's.
 */
export interface ApiProblem {
  status: number | 'NETWORK' | 'PARSE';
  /**
   * Stable branch key. Synthetic codes: VALIDATION_ERROR, STEP_UP_REQUIRED, NETWORK_ERROR,
   * TIMEOUT_ERROR, PARSE_ERROR, and `HTTP_<status>` for an answer that named no code of its own
   * (`HTTP_503`: a 503 that neither server wrote — both put a code on every 503 they send).
   */
  errorCode: string;
  title?: string;
  detail?: string;
  /** Bare 32-hex — pastes straight into Tempo/Grafana. Shown to users as a support code. */
  traceId?: string;
  /** Validation 400s: field -> messages (camelCased field names, as the API emits). */
  errors?: Record<string, string[]>;
  /**
   * 429s, locks and the outage 503: the body first; the Retry-After header is the fallback.
   * (Until 2026-10-01 this said the BFF drops upstream Retry-After headers.)
   */
  retryAfterSeconds?: number;
  /**
   * The money sends' 503 only (ADR-0058): `false` when the API knows it changed nothing; absent
   * whenever it cannot know. Read only as `applied === false`, and only to choose the words — a
   * retry keeps its idempotency key either way.
   */
  applied?: boolean;
  /** Step-up 403s (D2): the level the endpoint demands, read from the header. */
  requiredAuthLevel?: number;
  /*
    The daily outgoing-transfer bound (ADR-0050). All four ride DAILY_LIMIT_EXCEEDED on
    POST /api/transfers/authorizations and POST /api/transfers, as top-level JSON numbers with
    `resetsAt` a string — measured 2026-09-07T14:17:53Z (and the 14:46:26Z A4 re-run) on PR #156's
    working tree at 3c30122, merged as fda7ff7, BFF :5000 -> API :7215, AzureBankDev,
    DailyLimit:Amount default.

    BRANCH ON `errorCode`, NEVER ON MEMBER PRESENCE. `requested` is NOT exclusive to
    DAILY_LIMIT_EXCEEDED: A4.4 measured INSUFFICIENT_FUNDS on POST /api/transfers carrying
    `{"available": 300.0, "requested": 400}`. The document called it "DAILY_LIMIT_EXCEEDED only"
    there until 2026-09-11; it now names both codes, and INSUFFICIENT_FUNDS carries it on the
    withdrawal and the internal transfer as well. Any consumer inferring the daily refusal from
    `problem.requested !== undefined` is wrong.

    `available` stays UNTYPED here because nothing in the SPA reads it. The document declares it
    since 2026-09-11, but the funds gate (useFundsGate) re-reads the balance rather than trusting
    the refusal's figure, and money.contract.test.ts asserts it on the raw body.
  */
  limit?: number;
  used?: number;
  requested?: number;
  resetsAt?: string;
}

interface ProblemDetailsBody {
  status?: number;
  title?: string;
  detail?: string;
  errorCode?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  retryAfterSeconds?: number;
  applied?: boolean;
  /*
    LOAD-BEARING, not a mirror kept for tidiness. Read through the `[key: string]: unknown` index
    signature below, `body.limit` narrows to `{} | null | undefined` after a `!== undefined` guard,
    and the return literal's spread fails TS2322. Measured on a scratch patch by the data-layer lens
    (reverted; tree clean).
  */
  limit?: number;
  used?: number;
  requested?: number;
  resetsAt?: string;
  [key: string]: unknown;
}

/**
 * Runtime shape-guard for the error body (RFC 9457 ProblemDetails + our errorCode). Extra keys
 * pass through; a wrong-typed known field fails safeParse and we fall back to {} rather than trust
 * a garbled error payload. Low risk (it's our own server's format) but keeps the error path honest.
 */
const problemDetailsBodySchema = z.looseObject({
  status: z.number().optional(),
  title: z.string().optional(),
  detail: z.string().optional(),
  errorCode: z.string().optional(),
  traceId: z.string().optional(),
  errors: z.record(z.string(), z.array(z.string())).optional(),
  retryAfterSeconds: z.number().optional(),
  applied: z.boolean().optional(),
  // The daily-limit four. Not needed to CARRY the values — this is a `z.looseObject`, so they
  // already survive `safeParse` into `bodyResult.data` — but declaring them buys the same
  // "a wrong-typed known field fails safeParse" property the docblock above states as the reason
  // this schema exists at all.
  limit: z.number().optional(),
  used: z.number().optional(),
  requested: z.number().optional(),
  resetsAt: z.string().optional(),
});

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const MONTH = `(${MONTHS.join('|')})`;
const DAY_NAME = '(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)';
const LONG_DAY_NAME = '(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)';
const TIME_OF_DAY = '(\\d{2}):(\\d{2}):(\\d{2})';

/** `Sun, 06 Nov 1994 08:49:37 GMT`, the form a sender uses. */
const IMF_FIXDATE = new RegExp(`^${DAY_NAME}, (\\d{2}) ${MONTH} (\\d{4}) ${TIME_OF_DAY} GMT$`);
/** `Sunday, 06-Nov-94 08:49:37 GMT`, obsolete. */
const RFC850_DATE = new RegExp(`^${LONG_DAY_NAME}, (\\d{2})-${MONTH}-(\\d{2}) ${TIME_OF_DAY} GMT$`);
/** `Sun Nov  6 08:49:37 1994`, obsolete, and GMT although it does not say so. */
const ASCTIME_DATE = new RegExp(`^${DAY_NAME} ${MONTH} ([ \\d]\\d) ${TIME_OF_DAY} (\\d{4})$`);

/**
 * An HTTP-date as a time, or `undefined` for anything else. RFC 9110 §5.6.7 has a recipient accept
 * all three forms, and all three are GMT. Not `Date.parse`: it reads the asctime form in the
 * visitor's zone, hours off anywhere but UTC, and takes "1.5" or "-5" for dates long past.
 */
function parseHttpDate(value: string): number | undefined {
  const at = (year: number, month: string, day: string, hh: string, mm: string, ss: string) =>
    Date.UTC(year, MONTHS.indexOf(month), Number(day), Number(hh), Number(mm), Number(ss));

  let parts = IMF_FIXDATE.exec(value);
  if (parts) return at(Number(parts[3]), parts[2], parts[1], parts[4], parts[5], parts[6]);
  parts = ASCTIME_DATE.exec(value);
  if (parts) return at(Number(parts[6]), parts[1], parts[2], parts[3], parts[4], parts[5]);
  parts = RFC850_DATE.exec(value);
  if (!parts) return undefined;
  // Two digits of the year: one that would put the date more than 50 years ahead is the most
  // recent such year in the past.
  const thisYear = new Date().getUTCFullYear();
  let year = thisYear - (thisYear % 100) + Number(parts[3]);
  if (year > thisYear + 50) year -= 100;
  return at(year, parts[2], parts[1], parts[4], parts[5], parts[6]);
}

function parseRetryAfterSeconds(
  body: ProblemDetailsBody | undefined,
  headers?: Headers,
): number | undefined {
  // Body first (D13): ACCOUNT_LOCKED / PIN_LOCKED bodies carry retryAfterSeconds and the
  // outage 503 does too; the header is the fallback, for the BFF's own rate limiter, which
  // sets it and has no body field. (Until 2026-10-01 this said the BFF drops the upstream
  // Retry-After header.)
  if (typeof body?.retryAfterSeconds === 'number') return body.retryAfterSeconds;
  const header = headers?.get('Retry-After')?.trim();
  if (!header) return undefined;
  // RFC 9110 §10.2.3: a number of seconds, or an HTTP-date. A date is rounded up to the whole
  // second, so the wait is never shorter than the one asked for; a date already past is no wait.
  // Anything else names no wait, and the default for the answer's kind applies.
  if (/^\d+$/.test(header)) return Number.parseInt(header, 10);
  const at = parseHttpDate(header);
  if (at === undefined) return undefined;
  return Math.max(0, Math.ceil((at - Date.now()) / 1000));
}

export function toApiProblem(error: FetchBaseQueryError, response?: Response): ApiProblem {
  // Transport-level failures: no response at all. The raw error ("TypeError: Failed to fetch",
  // "TimeoutError: signal timed out") is the runtime's, never a sentence for a visitor.
  if (error.status === 'TIMEOUT_ERROR') {
    // The SPA gave up after REQUEST_TIMEOUT_MS, past the BFF's worst answer: the service did not
    // answer, which is the outage seen from this side.
    return { status: 'NETWORK', errorCode: 'TIMEOUT_ERROR', detail: SERVICE_UNAVAILABLE };
  }
  if (error.status === 'FETCH_ERROR') {
    return { status: 'NETWORK', errorCode: 'NETWORK_ERROR', detail: CONNECTION_FAILED };
  }
  if (error.status === 'PARSING_ERROR') {
    // A 503 is the outage whatever its body: an ingress in front of both servers answers one as
    // a plain-text page. Only a 503 — a non-JSON 502 or 504 stays PARSE. With no body to read,
    // its Retry-After header is the only wait it can name, and the read's retry honours it.
    if (error.originalStatus === 503) {
      const retryAfterSeconds = parseRetryAfterSeconds(undefined, response?.headers);
      return {
        status: 503,
        errorCode: 'HTTP_503',
        detail: SERVICE_UNAVAILABLE,
        ...(retryAfterSeconds !== undefined ? { retryAfterSeconds } : {}),
      };
    }
    // A non-JSON body (crash page, empty 500). originalStatus preserves the HTTP status
    // for logging, but consumers branch on the synthetic code.
    return {
      status: 'PARSE',
      errorCode: 'PARSE_ERROR',
      detail: `Unparseable ${error.originalStatus} response.`,
    };
  }
  if (error.status === 'CUSTOM_ERROR') {
    return { status: 'NETWORK', errorCode: 'NETWORK_ERROR', detail: error.error };
  }

  const status = error.status;
  const bodyResult = problemDetailsBodySchema.safeParse(error.data ?? {});
  const body = (bodyResult.success ? bodyResult.data : {}) as ProblemDetailsBody;
  const retryAfterSeconds = parseRetryAfterSeconds(body, response?.headers);

  // Validation 400s have an errors dict but NO errorCode — synthesize one (D5).
  const errorCode =
    body.errorCode ?? (status === 400 && body.errors ? 'VALIDATION_ERROR' : `HTTP_${status}`);

  return {
    status,
    errorCode,
    title: body.title,
    detail: status === 503 ? SERVICE_UNAVAILABLE : body.detail,
    traceId: body.traceId,
    ...(body.errors ? { errors: body.errors } : {}),
    ...(retryAfterSeconds !== undefined ? { retryAfterSeconds } : {}),
    // The ACTUAL drop site: an allow-list, so a member typed on the interface and not spread here
    // is silently absent at runtime. Same conditional style as the line above.
    ...(body.applied !== undefined ? { applied: body.applied } : {}),
    ...(body.limit !== undefined ? { limit: body.limit } : {}),
    ...(body.used !== undefined ? { used: body.used } : {}),
    ...(body.requested !== undefined ? { requested: body.requested } : {}),
    ...(body.resetsAt !== undefined ? { resetsAt: body.resetsAt } : {}),
  };
}

/**
 * Whether a problem is the service being down rather than a refusal or a broken connection: a 503
 * from whoever answered, or a request the SPA stopped waiting for at `REQUEST_TIMEOUT_MS`. Every
 * surface that words an outage checks this BEFORE its `'NETWORK'` branch, because a timeout is
 * `status: 'NETWORK'` too.
 */
export function isServiceOutage(problem: ApiProblem): boolean {
  return problem.status === 503 || problem.errorCode === 'TIMEOUT_ERROR';
}

/**
 * How long any request may wait for its answer: 65 s. The BFF's worst answer is 5 + 55 = 60 s —
 * the session renewal may wait 5 s (`TokenRefresher.ForegroundWait`) before the request goes on to
 * the API, which the BFF gives 55 s (`BackendApi:TimeoutSeconds`; ADR-0058). Aborting later than
 * that means a late 503, which may say `applied: false`, still reaches the visitor instead of
 * turning into an unknown; the 5 s are the margin. It stays far below the 240 s after which
 * Azure's ingress answers on its own. `timeoutChain.test.ts` reads both backend numbers from the
 * files that set them.
 */
export const REQUEST_TIMEOUT_MS = 65_000;

/** A read's whole budget, its one retry included: two of the BFF's worst answers. */
export const READ_BUDGET_MS = 120_000;

/** A read's wait before its retry when a connection failed and nothing named a wait. */
const CONNECTION_RETRY_AFTER_S = 1;

/**
 * A read's wait before its retry when a 502, 503 or 504 names none. Such an answer says that the
 * service behind it is down, not that a connection blinked, and a second later it most likely
 * still is. Microsoft's guidance for clients of its identity platform puts the first retry at
 * least 5 s after the answer.
 */
const GATEWAY_RETRY_AFTER_S = 5;

/**
 * Up to how much later than asked a read's retry goes, as a fraction of the wait. Every reader in
 * one outage hears the same `Retry-After`; without a spread they all come back in the same second.
 * Never earlier than asked: the spread only adds.
 */
const RETRY_SPREAD = 0.2;

/** A retry left with less than this of the budget is not sent: it could only time out. */
const MIN_RETRY_ATTEMPT_MS = 5_000;

const rawBaseQuery = fetchBaseQuery({
  // Same-origin with explicit /api and /bff paths per endpoint (D5): a '/api' baseUrl
  // would rewrite every BFF call to /api/bff/* and 404. The explicit origin (instead of
  // baseUrl '') keeps URLs absolute for runtimes whose Request cannot resolve relative
  // ones (undici under the jsdom test environment); in the browser it is identical.
  baseUrl: window.location.origin,
  credentials: 'same-origin',
  // Every request, reads and writes alike. RTK surfaces the abort as TIMEOUT_ERROR, which
  // `toApiProblem` words as the outage; a money send keeps its key on it (`status: 'NETWORK'`).
  timeout: REQUEST_TIMEOUT_MS,
});

const problemQuery: BaseQueryFn<
  string | FetchArgs,
  unknown,
  ApiProblem,
  object,
  FetchBaseQueryMeta
> = async (args, api, extraOptions) => {
  const result = await rawBaseQuery(args, api, extraOptions);

  if (result.error) {
    const response = result.meta?.response;

    // D2: the step-up 403 is recognized from the HEADER, before body normalization —
    // its bare body must never be interpreted as ProblemDetails.
    const requiredLevel = response?.headers.get('X-Auth-Level-Required');
    if (response?.status === 403 && requiredLevel) {
      return {
        error: {
          status: 403,
          errorCode: 'STEP_UP_REQUIRED',
          detail: 'This operation requires PIN verification.',
          requiredAuthLevel: Number.parseInt(requiredLevel, 10),
        },
        meta: result.meta,
      };
    }

    return { error: toApiProblem(result.error, response), meta: result.meta };
  }

  return result;
};

/**
 * How long a failed read waits before its one retry, or `null` when it is never retried: the wait
 * the answer named, or the default for its kind, plus up to `RETRY_SPREAD` of it.
 */
function queryRetryDelayMs(problem: ApiProblem, random: () => number): number | null {
  // The service had REQUEST_TIMEOUT_MS and did not answer: a second wait that long would only
  // double the visitor's.
  if (problem.errorCode === 'TIMEOUT_ERROR') return null;
  const gateway = problem.status === 502 || problem.status === 503 || problem.status === 504;
  // PARSE, a 500 and every 4xx (a 429 included) are answers, not a service that could not give one.
  if (problem.status !== 'NETWORK' && !gateway) return null;
  const asked =
    problem.retryAfterSeconds ?? (gateway ? GATEWAY_RETRY_AFTER_S : CONNECTION_RETRY_AFTER_S);
  return Math.max(0, asked) * 1000 * (1 + RETRY_SPREAD * random());
}

/**
 * What the base query takes besides the request. Only a test sets it: `random` fixes the spread
 * of a read's retry, which is `Math.random` otherwise.
 */
export interface ProblemQueryOptions {
  random?: () => number;
}

/** Resolves `true` after `ms`, or `false` at once if the request is aborted first. */
function sleepUnlessAborted(ms: number, signal: AbortSignal): Promise<boolean> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve(false);
      return;
    }
    const onAbort = () => {
      clearTimeout(timer);
      resolve(false);
    };
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort);
      resolve(true);
    }, ms);
    signal.addEventListener('abort', onAbort, { once: true });
  });
}

function withTimeout(args: string | FetchArgs, timeout: number): FetchArgs {
  return typeof args === 'string' ? { url: args, timeout } : { ...args, timeout };
}

/**
 * Retry policy (ADR-0059): QUERIES only, and only when the service could not answer — a transport
 * failure that is not the SPA's own timeout, a 503 whatever its body (`toApiProblem` makes an
 * unreadable one the outage), or a 502 or 504 whose body is JSON or empty (an unreadable one is
 * PARSE, an answer). ONE retry, after the answer's `retryAfterSeconds` — 5 s when a 502, 503 or
 * 504 names none, 1 s after a failed connection — plus up to a fifth of it at random, and only if
 * that wait still fits the read's `READ_BUDGET_MS`, counted from the first attempt; the retry's
 * own abort is cut to what is left of it. A read the visitor stops (an abort of its signal) sends
 * nothing more, even from the wait before the retry.
 *
 * Mutations are NEVER auto-retried — a retry of a monetary POST is a user decision that must reuse
 * the same Idempotency-Key (useIdempotentMutation owns that), and a 429 is never retried
 * automatically. The invariant is structural (type-gated here), not per-endpoint discipline.
 *
 * Not RTK's `retry()`: its backoff is handed the attempt number, never the error, so it cannot
 * wait what the answer asked for.
 */
export const problemBaseQuery: BaseQueryFn<
  string | FetchArgs,
  unknown,
  ApiProblem,
  ProblemQueryOptions,
  FetchBaseQueryMeta
> = async (args, api, extraOptions) => {
  const startedAt = Date.now();
  const first = await problemQuery(args, api, extraOptions);
  if (!first.error || api.type !== 'query') return first;

  // RTK hands on the endpoint's `extraOptions`, which no endpoint sets: undefined, whatever the
  // type says.
  const random = (extraOptions as ProblemQueryOptions | undefined)?.random ?? Math.random;
  const delayMs = queryRetryDelayMs(first.error, random);
  if (delayMs === null) return first;
  // What the retry may use is worked out BEFORE the wait, its spread included, never after it. A
  // wait can end late — a phone suspends a page in the background for minutes — and a limit
  // counted after it could fall under the 5 s floor, to zero (which RTK reads as no limit at all)
  // or below it (an abort before the retry is answered, so a service that is back still reads as
  // down).
  const leftMs = READ_BUDGET_MS - (Date.now() - startedAt) - delayMs;
  if (leftMs < MIN_RETRY_ATTEMPT_MS) return first;
  if (!(await sleepUnlessAborted(delayMs, api.signal))) return first;

  return problemQuery(withTimeout(args, Math.min(REQUEST_TIMEOUT_MS, leftMs)), api, extraOptions);
};
