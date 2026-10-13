/**
 * The API's success envelope (C# `ApiResponse<T>`): `{ data, message }`. Two endpoints
 * are BARE by contract and must never pass through here: T1 `GET /api/transactions`
 * (paginated) and B5 `GET /bff/auth/session-status`.
 */
export interface ApiEnvelope<T> {
  data?: T | null;
  message?: string | null;
}

import type { ZodType } from 'zod';

/**
 * Unwrap the API success envelope. Pass a Zod `schema` to ALSO runtime-validate `data` at the
 * trust boundary: a shape mismatch throws here, so the query rejects rather than caching a
 * mis-typed body. A BFF response always passes one, because no contract document stands behind
 * that surface. On `/api/*` the responses that must fail closed pass one in production too, and
 * the others pass one in development and test only (ADR-0023). Without a schema the data is
 * trusted as `T`: what then holds `/api/*` to its types is the OpenAPI drift gate (ADR-0053) and
 * the Schemathesis conformance job in CI, which checks the running API's responses against that
 * document on every pull request.
 */
export function unwrap<T>(envelope: ApiEnvelope<T>, schema?: ZodType<T>): T {
  if (envelope.data == null) {
    // A 2xx with an empty envelope is a contract violation — fail loud rather than
    // let undefined leak into the RTK Query cache.
    throw new Error('API success envelope carried no data.');
  }
  return schema ? schema.parse(envelope.data) : envelope.data;
}
