# ADR-0013: Registration user-enumeration — bounded, documented acceptance

**Status:** Accepted · **Date:** 2026-07-15 · **Amended:** 2026-09-03 (ADR-0045), 2026-09-17
(ADR-0037), 2026-10-06 and 2026-10-07 (decisions 6 to 8) · **Decision Makers:** Vladislav Aleshaev

## Context

Self-registration (`POST /api/auth/register`, and `bff/auth/register`, which forwards to it) creates
the user and a funded account and returns a token. A free email therefore answers `201` with a token
and a taken one an error, whatever the error says, and a throwaway handle isolates the email check.
The closure the OWASP Authentication Cheat Sheet prescribes is an out-of-band "check your email",
identical for new and existing accounts, and nothing here sends a registrant a message (ADR-0045).
OWASP ASVS v5.0.0 §6.3.1 (Level 1) requires anti-brute-force controls; §6.3.8, which names
registration enumeration, is Level 3; NIST SP 800-63B does not require it.

## Decision

1. **Rate limiting per client IP at the BFF edge**, because ASVS §6.3.1 requires it: a generous
   `GlobalLimiter` over all traffic, the proxied `/api/*` included, and a tight `auth` policy on
   `bff/auth/login`, `bff/auth/register` and the proxied `/api/auth/login` and `/api/auth/register`.
   A refusal is `429` with `Retry-After` and `errorCode = RATE_LIMIT_EXCEEDED`; the limits are
   settings (`RateLimiting`); IPv6 is keyed on the /64 prefix, which an end site is handed whole.
2. **A duplicate email and a duplicate handle answer the same neutral `409`** ("Registration could
   not be completed.", `errorCode = REGISTRATION_FAILED`), because a code per field tells one
   request that a customer exists. Only the log has the reason (`DuplicateRegistration` event).
3. **Detective control**: bursts of that event can be alerted on; thresholds are operations' to set.
4. **Login parity** (ADR-0012): one generic `401` for an unknown user, a wrong password and a lock.
5. **The residual is accepted and documented; full closure is deferred** (deferral trigger below).
6. **`X-Forwarded-For` is believed only on a connection from a listed proxy**, by exact address
   (`ForwardedHeaders:KnownProxies`) or CIDR network (`ForwardedHeaders:KnownIPNetworks`), because
   believed from anyone it gives each invented address a fresh partition, and an exact address stops
   matching in silence when a platform moves its proxy. The caller is the last entry (`ForwardLimit`
   stays 1); with both lists empty the header is ignored; a connection with no address is no proxy.
7. **The header is made a list of addresses and nothing else before the framework reads it**
   (`StrictForwardedFor`), because the framework's own reading lets a caller name any address.
8. **Security configuration is validated at startup**: a non-positive limit, an unparseable
   `KnownProxies` entry, or a network that is malformed, `/0`, wider than `/8`, IPv4-mapped IPv6 or
   read as another network than written stops the app, because both controls otherwise fail unseen.
9. **Registration stays neutral under concurrency**: the pre-checks are advisory, and a duplicate
   that passes them fails at write time into the same `409`, because the race would otherwise reopen
   the oracle. Unique indexes on `AzureTag` and (NULL-filtered) `NormalizedEmail` are the authority.

## Rejected

- Rejected: the explicit `DUPLICATE_EMAIL` / `DUPLICATE_AZURE_TAG` 409 (Option 1), because for a
  banking app a plaintext customer-existence disclosure is the most audit-flaggable line.
- Rejected: a generic message and nothing else (Option 2), because the token oracle stays open.
- Rejected for now: email confirmation (Option 3), because no relay exists and auto-login would end.
- Rejected: a reader of the header written in place of the framework's, because it would carry its
  own mistakes; and leaving the header to the platform's ingress, because that is not measured.

## Consequences

- Rate limiting is enforced, and no response says in plain text that a customer exists.
- Not covered: the structural oracle (`201` with a token against `409`), a consciously accepted
  ASVS §6.3.8 Level 3 gap, and a funded account for an unproven email. The deferred flow ends both.
- Not covered: a targeted lookup needs one request, and rotating addresses defeat a per-IP limit.
- Not covered: the counters are in-process (a restart resets them, N replicas multiply each limit
  by N), and the API has no limiter of its own: it must be reachable only through the BFF.
- Not covered: all routes under `auth` share one per-IP budget, on purpose; a refused auth request
  still spends a global permit; `/api/auth/pin/verify` has the global baseline only (ADR-0010).
- Not covered: any address in a listed network can name a caller's address, and a public range is
  not refused; one hop is believed, so behind two proxies in a row all visitors are one client.
- Measured on Azure for one hop: a caller is counted by its own address whatever its header names.

## Revisit when

- Deferral trigger: email infrastructure lands for its first real uses (password reset, transaction
  alerts); the confirmation flow then closes the oracle and the unverified provisioning together.
- A second instance, or a non-ASP.NET service behind the proxy's catch-all route.

## Verified by

- `RateLimiterTests`, `TrustedProxyNetworkTests`, `ForwardedForOnARealConnectionTests`,
  `StrictForwardedForTests`, `ProxyOptionsValidatorTests`.
- `RegistrationEmailRaceSqlServerTests`, `RegistrationAtomicitySqlServerTests` (ADR-0037).

## Related

ADR-0010, ADR-0012, ADR-0037, ADR-0045.
