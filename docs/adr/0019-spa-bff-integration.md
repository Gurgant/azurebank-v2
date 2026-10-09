# ADR-0019: SPA–BFF integration architecture for the frontend build-out

**Status:** Accepted · **Date:** 2026-07-20 · **Amended:** 2026-09-05 (decision 3), and by ADR-0023
(decision 6) · **Decision Makers:** Vladislav Aleshaev

## Context

Before this decision the SPA was a complete mock prototype: its RTK Query endpoints had no consumer,
its auth was a bearer token behind a `DEV_BYPASS_AUTH` flag, the inverse of the BFF's cookie, and
its hand-written types disagreed with the real contract. The backend contract has sharp edges the
client must encode structurally: two envelope exceptions, a step-up 403 that is not ProblemDetails,
two PIN models, a five-state idempotency protocol that fingerprints raw body bytes, an authenticated
401 (`INVALID_PIN`) and three sources of 429.

## Decision

1. **One data client, one error channel.** RTK Query only: `problemBaseQuery` turns every failure
   into a typed `ApiProblem` routed on `errorCode`, so that the contract's edges are encoded once
   (`VALIDATION_ERROR` synthesized for a 400 with `errors`, the step-up 403 recognized from
   `X-Auth-Level-Required` before the body, `retryAfterSeconds` from the body first). Retry is
   structural: queries only, transport and gateway failures only, never mutations.
2. **No API token ever reaches the browser.** The access and refresh tokens stay in the BFF's
   session; transport auth is the `__Host-` session cookie (ADR-0018), an opaque session id.
   Client auth state is `unknown | anonymous | authenticated | expired`, resolved by the one
   bootstrap probe (`GET /bff/auth/me`) and kept by string matchers on the RTK Query action shape,
   because the wire shape is the stable contract.
3. **The global 401 rule routes on `errorCode`, never on endpoint identity** (D3). The codes of
   `IN_FLOW_401_CODES` in `sessionMiddleware.ts` stay with the surface that asked: `INVALID_PIN` in
   the calling form, `INVALID_CREDENTIALS` on login, and ADR-0042's `AUTHORIZATION_REQUIRED`,
   `AUTHORIZATION_EXPIRED` and `AUTHORIZATION_INVALID` in the transfer, because those are thrown
   after the cookie was accepted. The boot probe's 401 resolves to `anonymous`, with no banner.
   Every other 401 dispatches `sessionExpired()` and resets the RTK Query cache, because financial
   data must not outlive the session it was fetched under.
4. **No polling, explicit keep-alive** (D6/D14). `session-status` is the safe probe, because the BFF
   excludes it from activity (ADR-0018); `/bff/auth/me` is the deliberate refresher behind the "Stay
   signed in" warning, two minutes before the end, driven by a client mirror of LastActivity. No
   financial intent is ever parked in Web Storage: a session loss loses unsubmitted form state.
5. **`DEV_BYPASS_AUTH` is deleted, a one-way door** (D20), because the auth behind it inverted the
   BFF's cookie architecture. A run with no backend returns only as the labelled MSW-worker mode.
   Development runs against the real BFF through the Vite proxy (D18); production is the BFF serving
   the built SPA (ADR-0054).
6. **Superseded by ADR-0023.** BFF response shapes are Zod schemas in `src/api/bffSchemas.ts`,
   validated at runtime, and `bffTypes.ts` is `z.infer` of them. Hand-written mirrors of
   `BffResponses.cs` are not restored, because that would delete a live runtime guard on the weakest
   boundary in the system. Request bodies reuse the generated API types: the BFF forwards them.
7. **One form system.** react-hook-form and zod with Fluent `Field` and `Input` and the `register()`
   spread, with Zod schemas that mirror the backend's validation contract.

## Rejected

- Rejected: `endpoints.X.matchFulfilled`, because it couples the auth slice to the api-slice module.
- Rejected: routing a 401 by its endpoint, because some 401s come after the cookie was accepted.
- Rejected: a hand-rolled `FormField` or a Controller adapter, because the pages bind natively.

## Consequences

- Built on this contract: the route guard with `returnTo`, registration's dual-path banner (D15)
  and a countdown for each source of 429 (D13).
- The session cookie is HttpOnly: no script reads it, and a reload is answered on the cookie alone.
- Not covered: the BFF's own responses are not in the OpenAPI document (decision 6).
- Not covered: a draft that was not submitted is lost with the session (decision 4).

## Verified by

- `frontend/src/features/auth/auth.test.tsx`: the probe, the 401 rule, the guard, registration.
- `frontend/src/api/policies.test.tsx`: the data-layer policies.

## Related

ADR-0018, ADR-0023, ADR-0042, ADR-0054.
