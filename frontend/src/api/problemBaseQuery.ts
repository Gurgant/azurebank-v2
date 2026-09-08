import { fetchBaseQuery, retry } from '@reduxjs/toolkit/query/react';
import type {
  BaseQueryFn,
  FetchArgs,
  FetchBaseQueryError,
  FetchBaseQueryMeta,
} from '@reduxjs/toolkit/query';
import { z } from 'zod';

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
 *  3. transport failures normalize to status 'NETWORK'; unparseable bodies to 'PARSE'.
 */
export interface ApiProblem {
  status: number | 'NETWORK' | 'PARSE';
  /** Stable branch key. Synthetic codes: VALIDATION_ERROR, STEP_UP_REQUIRED, NETWORK_ERROR, PARSE_ERROR. */
  errorCode: string;
  title?: string;
  detail?: string;
  /** Bare 32-hex — pastes straight into Tempo/Grafana. Shown to users as a support code. */
  traceId?: string;
  /** Validation 400s: field -> messages (camelCased field names, as the API emits). */
  errors?: Record<string, string[]>;
  /** 429s: body-first (the BFF forwards bodies but drops upstream Retry-After headers, D13). */
  retryAfterSeconds?: number;
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
    `{"available": 300.0, "requested": 400}`, which contradicts schema.d.ts:2061's
    "DAILY_LIMIT_EXCEEDED only" prose. The document UNDER-DECLARES INSUFFICIENT_FUNDS's members;
    the code is right. Any consumer inferring the daily refusal from `problem.requested !== undefined`
    is wrong.

    `available` stays UNTYPED on purpose: ADR-0050's review-round-1 note keeps INSUFFICIENT_FUNDS's
    `{available, requested}` as its own small PR, and declaring it here would quietly do that PR's work.
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
  // The daily-limit four. Not needed to CARRY the values — this is a `z.looseObject`, so they
  // already survive `safeParse` into `bodyResult.data` — but declaring them buys the same
  // "a wrong-typed known field fails safeParse" property the docblock above states as the reason
  // this schema exists at all.
  limit: z.number().optional(),
  used: z.number().optional(),
  requested: z.number().optional(),
  resetsAt: z.string().optional(),
});

function parseRetryAfterSeconds(
  body: ProblemDetailsBody | undefined,
  headers?: Headers,
): number | undefined {
  // Body first (D13): ACCOUNT_LOCKED / PIN_LOCKED bodies carry retryAfterSeconds and the
  // BFF drops the upstream Retry-After header; the header is the fallback for the BFF's
  // own rate limiter, which sets it and has no body field.
  if (typeof body?.retryAfterSeconds === 'number') return body.retryAfterSeconds;
  const header = headers?.get('Retry-After');
  if (header) {
    const seconds = Number.parseInt(header, 10);
    if (Number.isFinite(seconds)) return seconds;
  }
  return undefined;
}

export function toApiProblem(error: FetchBaseQueryError, response?: Response): ApiProblem {
  // Transport-level failures: no response at all.
  if (error.status === 'FETCH_ERROR' || error.status === 'TIMEOUT_ERROR') {
    return { status: 'NETWORK', errorCode: 'NETWORK_ERROR', detail: String(error.error ?? '') };
  }
  if (error.status === 'PARSING_ERROR') {
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
    detail: body.detail,
    traceId: body.traceId,
    ...(body.errors ? { errors: body.errors } : {}),
    ...(retryAfterSeconds !== undefined ? { retryAfterSeconds } : {}),
    // The ACTUAL drop site: an allow-list, so a member typed on the interface and not spread here
    // is silently absent at runtime. Same conditional style as the line above.
    ...(body.limit !== undefined ? { limit: body.limit } : {}),
    ...(body.used !== undefined ? { used: body.used } : {}),
    ...(body.requested !== undefined ? { requested: body.requested } : {}),
    ...(body.resetsAt !== undefined ? { resetsAt: body.resetsAt } : {}),
  };
}

const rawBaseQuery = fetchBaseQuery({
  // Same-origin with explicit /api and /bff paths per endpoint (D5): a '/api' baseUrl
  // would rewrite every BFF call to /api/bff/* and 404. The explicit origin (instead of
  // baseUrl '') keeps URLs absolute for runtimes whose Request cannot resolve relative
  // ones (undici under the jsdom test environment); in the browser it is identical.
  baseUrl: window.location.origin,
  credentials: 'same-origin',
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
 * Retry policy (research decision): QUERIES only, transport/gateway failures only
 * (NETWORK, 502/503/504), max 3 attempts. Mutations are NEVER auto-retried — a retry of
 * a monetary POST is a user decision that must reuse the same Idempotency-Key
 * (useIdempotentMutation owns that), and a 429 is never retried automatically.
 * The invariant is structural (type-gated here), not per-endpoint discipline.
 */
export const problemBaseQuery = retry(problemQuery, {
  retryCondition: (error, _args, { attempt, baseQueryApi }) => {
    if (baseQueryApi.type !== 'query') return false;
    const problem = error as unknown as ApiProblem;
    const retriable =
      problem.status === 'NETWORK' ||
      problem.status === 502 ||
      problem.status === 503 ||
      problem.status === 504;
    return retriable && attempt < 3;
  },
});
