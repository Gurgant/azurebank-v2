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
 * trust boundary (currently the BFF responses, whose types are hand-written): a shape mismatch
 * throws here, so the query rejects rather than caching a mis-typed body. Without a schema the
 * data is trusted as `T` (the /api/* surface is already covered by the OpenAPI drift gate +
 * Schemathesis — see the Zod scope decision doc).
 */
export function unwrap<T>(envelope: ApiEnvelope<T>, schema?: ZodType<T>): T {
  if (envelope.data == null) {
    // A 2xx with an empty envelope is a contract violation — fail loud rather than
    // let undefined leak into the RTK Query cache.
    throw new Error('API success envelope carried no data.');
  }
  return schema ? schema.parse(envelope.data) : envelope.data;
}
