# ADR-0019: SPA–BFF integration architecture for the frontend build-out

**Status**: Accepted

**Date**: 2026-07-20

**Decision Makers**: Vladislav Aleshaev

---

## Context

Before this chapter the SPA was a visually complete, 100% mock prototype: its RTK Query
endpoints had zero consumers, auth was a Bearer-token inversion of the shipped BFF-cookie
architecture behind a `DEV_BYPASS_AUTH` flag, and the hand-written types disagreed with
the real contract on every axis. Step-5 is therefore a first-time integration, not a mock
swap. The backend contract has sharp edges the client must encode structurally: two
envelope exceptions, a non-ProblemDetails 403 step-up body, two PIN models, a five-state
idempotency protocol fingerprinting raw body bytes, an authenticated 401 (`INVALID_PIN`),
and three 429 sources.

## Decision

1. **One data client, one error channel.** RTK Query only; `problemBaseQuery` normalizes
   every failure to a typed `ApiProblem` (errorCode-first routing; `VALIDATION_ERROR`
   synthesized on 400+errors; step-up 403 recognized from the `X-Auth-Level-Required`
   header before body parsing; `retryAfterSeconds` body-first). Retry is structural:
   queries only, transport/gateway failures only, never mutations.
2. **No token ever reaches the browser.** Transport auth is the `__Host-` session cookie
   (ADR-0018) via `credentials: 'same-origin'`. Client auth state is a four-state
   machine — `unknown | anonymous | authenticated | expired` — resolved by the ONE
   bootstrap probe (`GET /bff/auth/me`) and maintained by string-based Redux matchers on
   the RTK Query action shape (deliberately not `endpoints.X.matchFulfilled`: the wire
   shape is the stable contract and avoids coupling the auth slice to the api-slice
   module instance).
3. **The global 401 rule routes on errorCode, never endpoint identity** (D3):
   `INVALID_PIN` stays in the calling form; `INVALID_CREDENTIALS` stays on login; the
   boot probe's 401 resolves to `anonymous` (no banner); every other 401 dispatches
   `sessionExpired()` AND resets the RTK Query cache — financial data must not outlive
   the session it was fetched under.
   *Correction (2026-09-05): the exempt list is `IN_FLOW_401_CODES` in `sessionMiddleware.ts`,
   and it also holds ADR-0042's three authorisation codes — `AUTHORIZATION_REQUIRED`,
   `AUTHORIZATION_EXPIRED`, `AUTHORIZATION_INVALID` — 401s about a transfer's authorisation,
   thrown after the cookie was accepted. `EXPIRED` and `INVALID` joined on 2026-08-17
   (`e4973da`); `REQUIRED` on 2026-09-05, when a headerless transfer built at the store against
   the real stack took the session from `authenticated` to `expired` with the cookie alive.*
4. **No polling, explicit keep-alive** (D6/D14): `session-status` is the safe probe (the
   BFF excludes it from activity, ADR-0018); `/bff/auth/me` is the deliberate refresher
   behind the T-2min "Stay signed in" warning, driven by a client mirror of
   LastActivity. No financial intent is ever parked in Web Storage — a session loss
   loses unsubmitted form state by policy.
5. **`DEV_BYPASS_AUTH` is deleted — a one-way door** (D20). The zero-backend static demo
   no longer exists; the demo capability returns only as the labeled MSW-worker
   portfolio mode. Dev runs against the real BFF through the Vite proxy (D18); prod will
   be the BFF serving `dist/` (BE-3).
6. **BFF response shapes are hand-written mirrors** (`src/api/bffTypes.ts`) of
   `BffResponses.cs` — the OpenAPI spec covers the API surface only. Request bodies
   reuse the generated API types (the BFF forwards them verbatim). The mirror is the
   accepted cost until the BFF surface joins the spec (BE-2 backlog).
   *(Superseded: ADR-0023 inverts the direction of truth — `bffSchemas.ts` holds Zod
   schemas validated at runtime, and `bffTypes.ts` is now `z.infer` of them, so the type
   and the validator cannot disagree. Do not "restore" hand-written types here: that
   deletes a live runtime guard on the weakest boundary in the system.)*
7. **One form system.** react-hook-form + zod with Fluent `Field`/`Input` and
   `register()` spread; the unused hand-rolled `FormField` is deleted, and the
   previously planned Controller→Field adapter is dropped as unnecessary — the pages
   already bind natively. Zod schemas mirror the backend validation contract (AzureTag
   pattern, full password policy).

## Consequences

- PR-4 wires login/register/logout/me/session-status/verify-pin/set-pin, the guard with
  `returnTo`, register's dual-path banner (D15) and per-source 429 countdowns (D13).
- The auth flows are pinned by MSW-stateful tests (bootstrap x2, global-expiry with
  cache reset, guard matrix, returnTo login, register 429 + dual-path) on top of the
  §2.4 flagship policy tests.
- Verified live against the real local stack: register → 201 sets the session cookie
  (HttpOnly — invisible to JS), guarded dashboard reached, post-refresh `/bff/auth/me`
  → 200 on the cookie alone.
