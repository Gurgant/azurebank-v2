# ADR-0001: Backend-For-Frontend (BFF) Pattern

**Status:** Accepted · **Date:** 2026-01-12 · **Amended:** 2026-07-20 (ADR-0018, no CORS),
2026-09-19 (ADR-0055, the API serves the BFF only) · **Decision Makers:** Vladislav Aleshaev

## Context

AzureBank's frontend has to call the backend API with an authenticated identity. The traditional
approach hands the JWT to the browser, in localStorage or sessionStorage, where any script on the
page can read it: one XSS is a stolen token
([OWASP](https://cheatsheetseries.owasp.org/cheatsheets/JSON_Web_Token_for_Java_Cheat_Sheet.html#token-storage-on-client-side)
on token storage in the client). The architecture needs tokens that JavaScript cannot reach, session
handling in one place, request throttling at the gateway, one place for the cross-cutting concerns
(security headers, logging), and room for more than one frontend application.

## Decision

A dedicated ASP.NET Core service, `AzureBank.Bff`, sits between the browser and the API, in the
[Backends-for-Frontends](https://docs.microsoft.com/en-us/azure/architecture/patterns/backends-for-frontends)
pattern:

1. **It handles user authentication and stores the JWT server-side**, because a token that never
   reaches the browser cannot be stolen by script running there.
2. **It issues an HTTP-only session cookie to the client**, because the browser then holds only a
   reference to the session, which JavaScript cannot read.
3. **It proxies API requests and injects the token**, because the frontend then needs no token
   management, and token refresh is managed by the server.
4. **It provides rate limiting and security headers**, because one gateway is one place to apply
   them.

## Rejected

- Rejected: direct API access with the token in localStorage, because the token sits in the browser,
  where XSS token theft is a high risk, refresh is managed by the client and rate limiting is per
  endpoint.
- Rejected: an external API gateway (Kong, AWS) with token relay, because a custom BFF gives full
  control of the session logic, uses standard .NET with no vendor lock-in and no extra
  infrastructure cost, relies on .NET skills already available, and integrates with the existing
  authentication.

## Consequences

- JWTs are never exposed to browser JavaScript, and the frontend has no token management.
- Sessions are managed in one place, with configurable timeouts.
- Security headers and rate limiting have a single point, and a new cross-cutting concern has an
  obvious home.
- It costs one more service to deploy and maintain, a slight latency increase for the extra hop, and
  the learning curve of the YARP reverse proxy (ADR-0002).
- The browser reaches the BFF same-origin, so there is no CORS policy at all (ADR-0018).
- Session state is held in memory, so it has to be considered before scaling horizontally: a shared
  store, such as Redis, comes before a second instance (ADR-0057).
- This decision alone does not stop a caller that goes round the BFF. ADR-0055 completes it: the API
  refuses a request that does not carry the BFF's service credential.

## Verified by

- `SessionCookieTests`: the session cookie and its attributes. `SessionActivityTests`: the session's
  timeouts. `AuthLevelMiddlewareTests`: the token injected by the proxy.
- `RateLimiterTests` and `SecurityHeadersTests`: the limiter and every security header.

## Related

ADR-0002, ADR-0018, ADR-0019, ADR-0021, ADR-0038, ADR-0055, ADR-0057.
