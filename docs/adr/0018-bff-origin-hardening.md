# ADR-0018: BFF origin hardening — __Host- session cookie, Fetch-Metadata, no CORS

**Status**: Accepted

**Date**: 2026-07-20

**Decision Makers**: Vladislav Aleshaev

---

## Context

The frontend build-out (Step 5) makes the BFF the browser's single origin: in development
Vite's `server.proxy` forwards both `/api` and `/bff` to the BFF, and in production the BFF
will serve the SPA bundle itself (BE-3). Auditing the BFF against that topology found five
gaps, one of them a live hole:

1. The production CORS policy (`AllowFrontend`) credential-whitelisted
   `http://localhost:5173` — any page served from a developer-machine port could ride a
   victim's session cookie cross-origin. Not dead config: it was the active non-dev policy.
2. The session cookie persisted to disk (`Expires` = the JWT expiry). A bank session should
   not survive the browser session; server-side timeouts already bound its real lifetime.
3. The cookie name (`.AzureBank.Session`) carried no browser-enforced integrity: any
   subdomain or insecure origin could set a cookie with that name.
4. `BffAuthController` forwarded upstream error bodies through an unguarded
   `JsonDocument.Parse` — a non-JSON upstream body (proxy HTML, empty 500) escaped as an
   unhandled exception and surfaced as a naked 500.
5. `SessionActivityMiddleware` refreshed `LastActivity` on **every** cookie-bearing request,
   including `GET /bff/auth/session-status` — so any frontend status poll would silently
   neutralize the 30-minute inactivity timeout (the reason D6/the FE plan bans polling).

## Decision

1. **No CORS, by design.** Both policy registrations and `UseCors` are deleted. The browser
   only ever reaches the BFF same-origin (dev proxy, prod self-hosting), so CORS grants
   nothing legitimate and the deleted policy was pure attack surface. Same-origin also ends
   the `WithExposedHeaders(Idempotency-Replayed)` dependency — same-origin responses expose
   all headers.
2. **`__Host-` prefixed session cookie outside Development.** Applied at runtime via
   `PostConfigure<BffSessionOptions>` (config keeps one canonical name; every reader —
   controller, middleware, YARP transform, rate-limit partitioner — picks it up through
   `IOptions`). The browser then refuses the cookie unless Secure + `Path=/` + no `Domain`,
   making it unforgeable from subdomains or insecure origins. Development stays unprefixed
   and non-Secure: the dev loop runs on `http://localhost`, where prefixed cookies cannot be
   set at all (and Safari refuses Secure cookies there even unprefixed).
3. **Session cookie, not persistent cookie.** No `Expires`/`Max-Age`; gone when the browser
   closes. Lifetime is enforced server-side (inactivity + absolute timeouts). The logout
   deletion carries the same attributes — a `__Host-` cookie is only evicted by a Secure
   `Path=/` expiry.
4. **Fetch-Metadata middleware** as the origin-level CSRF backstop behind SameSite=Strict:
   non-GET/HEAD requests whose `Sec-Fetch-Site` is present and neither `same-origin` nor
   `none` are rejected 403 (ProblemDetails, `errorCode: CROSS_SITE_REQUEST_BLOCKED`) —
   before they can refresh session activity, consume rate-limit budget, or reach the
   controllers/proxy. An absent header allows the request (non-browser clients).
   `same-site` is rejected too: a sibling subdomain is not this host.
5. **Upstream error forwarding is guarded.** JSON error bodies forward verbatim with their
   status; a non-JSON body becomes a generic 502 ProblemDetails.
6. **`GET /bff/auth/session-status` no longer counts as activity.** It is the frontend's
   "check without keeping alive" probe; `/bff/auth/me` remains the deliberate
   "Stay signed in" refresher.

## Consequences

- The former cross-origin dev mode (frontend on :5173 calling the BFF origin directly) no
  longer works — dev goes through the Vite proxy (`launch.json` `frontend` + `bff` + `api`),
  which is what keeps the cookie first-party and SameSite=Strict honest anyway.
- `docs/adr/0009` §hardening mentions the loopback dev CORS policy and the CORS-exposed
  replay header; both are superseded by this ADR's deletion.
- Pinned by `AzureBank.Bff.Tests`: cookie posture per environment (name/prefix, Secure,
  no Expires, logout attribute match), Fetch-Metadata allow/deny matrix, CORS absence,
  non-JSON upstream → 502, and the session-status vs `/me` activity split.

## Alternatives considered

- **Antiforgery tokens** instead of Fetch-Metadata: heavier (token issuance/rotation,
  SPA plumbing) and redundant behind SameSite=Strict + same-origin topology; Fetch-Metadata
  is declarative and covers the same non-GET surface.
- **Keeping a loopback-only CORS policy for dev**: rejected — the dev proxy makes even that
  unnecessary, and an empty policy invites re-widening. Deletion is the honest posture.
- **`__Host-` in Development too**: impossible over `http://localhost` (prefix mandates
  Secure), and forcing https into the dev loop costs more than the dev/prod cookie-name
  asymmetry, which the tests pin explicitly.
