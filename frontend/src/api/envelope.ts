/**
 * The API's success envelope (C# `ApiResponse<T>`): `{ data, message }`. Two endpoints
 * are BARE by contract and must never pass through here: T1 `GET /api/transactions`
 * (paginated) and B5 `GET /bff/auth/session-status`.
 */
export interface ApiEnvelope<T> {
  data?: T | null;
  message?: string | null;
}

export function unwrap<T>(envelope: ApiEnvelope<T>): T {
  if (envelope.data == null) {
    // A 2xx with an empty envelope is a contract violation — fail loud rather than
    // let undefined leak into the RTK Query cache.
    throw new Error('API success envelope carried no data.');
  }
  return envelope.data;
}
