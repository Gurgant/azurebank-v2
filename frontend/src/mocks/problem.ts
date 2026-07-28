import { HttpResponse } from 'msw';

/**
 * Builds the API's real error envelope: RFC 9457 ProblemDetails + `errorCode` + a bare
 * 32-hex `traceId`, exactly as the backend handlers emit it (AppExceptionHandler /
 * GlobalExceptionHandler / ValidationExceptionHandler). Two contract quirks preserved on
 * purpose:
 *  - validation 400s carry an `errors` dictionary but NO errorCode;
 *  - extra members (available, retryAfterSeconds, lockedUntil...) are spread at top level.
 */
export interface ProblemInit {
  status: number;
  errorCode?: string;
  title?: string;
  detail?: string;
  /** field -> messages, as the ValidationExceptionHandler emits (400 only, no errorCode). */
  errors?: Record<string, string[]>;
  /** Extra top-level members (available, retryAfterSeconds, lockedUntil, ...). */
  extensions?: Record<string, unknown>;
  headers?: Record<string, string>;
}

const DEFAULT_TITLES: Record<number, string> = {
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  409: 'Conflict',
  413: 'Payload Too Large',
  422: 'Unprocessable Entity',
  429: 'Too Many Requests',
  500: 'Internal Server Error',
};

/** Deterministic fake trace id: bare 32-hex, like Activity.TraceId.ToString(). */
export function fakeTraceId(): string {
  return Array.from({ length: 32 }, () => Math.floor(Math.random() * 16).toString(16)).join('');
}

export function problemBody(init: ProblemInit) {
  const { status, errorCode, title, detail, errors, extensions } = init;
  /*
    A validation 400 is NOT a generic 400. `ValidationExceptionHandler` writes its own
    ProblemDetails with `Title = "Validation Failed"` and
    `Detail = "One or more validation errors occurred."`, where `AppExceptionHandler` would have
    said "Bad Request" and the exception's own message. The mock was giving every validation
    failure the generic title and an EMPTY detail, so any surface that renders `problem.detail`
    showed nothing at all for the one class of error that always has something to say.

    Keyed off `errors` rather than off the status, because that dictionary is exactly what
    distinguishes the two handlers.
  */
  const isValidation = status === 400 && errors !== undefined;
  return {
    type: `https://httpstatuses.com/${status}`,
    title: title ?? (isValidation ? 'Validation Failed' : (DEFAULT_TITLES[status] ?? 'Error')),
    status,
    detail: detail ?? (isValidation ? 'One or more validation errors occurred.' : ''),
    traceId: fakeTraceId(),
    ...(errorCode ? { errorCode } : {}),
    ...(errors ? { errors } : {}),
    ...extensions,
  };
}

export function problem(init: ProblemInit) {
  return HttpResponse.json(problemBody(init), {
    status: init.status,
    headers: { 'Content-Type': 'application/problem+json', ...init.headers },
  });
}
