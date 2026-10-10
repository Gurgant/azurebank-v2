# ADR-0018: BFF origin hardening — __Host- session cookie, Fetch-Metadata, no CORS

**Status:** Accepted · **Date:** 2026-07-20 · **Amended:** 2026-10-05 (decision 2) ·
**Decision Makers:** Vladislav Aleshaev

## Context

The BFF is the browser's single origin: in development Vite's `server.proxy` forwards `/api` and
`/bff` to it, and in production it serves the SPA bundle itself. Against that topology the BFF had
five gaps before this decision. Its production CORS policy let `http://localhost:5173` send
credentials, so a page served from that port could ride a victim's session cookie. The cookie
persisted to disk. Its name, `.AzureBank.Session`, could be set by any subdomain or insecure origin.
A non-JSON error body from the API escaped as a bare 500. And every cookie-bearing request, a status
poll included, pushed the inactivity timeout forward.

## Decision

1. **No CORS, by design.** Both policy registrations and `UseCors` are deleted, because the browser
   reaches the BFF same-origin only, so CORS grants nothing legitimate and the deleted policy was
   attack surface. Same-origin responses expose every header, so `Idempotency-Replayed` needs no
   exposed-header list.
2. **The session cookie carries the `__Host-` prefix outside Development**, because the browser then
   refuses it unless it is Secure, with `Path=/` and no `Domain`: no subdomain and no insecure
   origin can forge it. `PostConfigure<BffSessionOptions>` applies the prefix at runtime, so the
   configuration keeps one name and every reader takes the prefixed one through `IOptions`.
   Development stays unprefixed and non-Secure, because its loop runs on `http://localhost` and
   Safari refuses a Secure cookie there. Chromium does keep a `__Host-` cookie set over
   `http://localhost` and sends it back, measured on the compose stack's Production images.
3. **A session cookie, not a persistent one.** It has no `Expires` and no `Max-Age`. A browser that
   restores its session can keep such a cookie across a restart, so the lifetime is the server's:
   inactivity and absolute timeouts. The logout's deletion carries the same attributes, because a
   `__Host-` cookie is evicted only by a Secure `Path=/` expiry.
4. **Fetch-Metadata middleware is the origin-level CSRF backstop behind SameSite=Strict.** A request
   that is not GET or HEAD, whose `Sec-Fetch-Site` is present and neither `same-origin` nor `none`,
   is answered 403 `CROSS_SITE_REQUEST_BLOCKED` before it can refresh session activity, spend
   rate-limit budget or reach a controller or the proxy. An absent header lets the request through,
   because a non-browser client sends none; `same-site` is refused, because a sibling subdomain is
   not this host.
5. **Upstream error forwarding is guarded.** A JSON error body is forwarded verbatim with its
   status; a non-JSON body becomes a generic 502 ProblemDetails, because parsing it used to fail as
   a bare 500.
6. **`GET /bff/auth/session-status` does not count as activity**, because it is the SPA's way to ask
   how long is left without keeping the session alive. `/bff/auth/me` remains the deliberate "Stay
   signed in" refresher.

## Rejected

- Rejected: antiforgery tokens, because issuing and rotating them is heavier and redundant behind
  SameSite=Strict on one origin; Fetch-Metadata covers the same non-GET surface declaratively.
- Rejected: a loopback-only CORS policy for development, because the dev proxy makes it unnecessary
  and an empty policy invites re-widening.
- Rejected: `__Host-` in Development too, because it needs https for Safari, and https in the dev
  loop costs more than a cookie name that differs between the environments.

## Consequences

- The cross-origin development mode, the frontend on port 5173 calling the BFF's origin directly, no
  longer works: development goes through the Vite proxy, which keeps the cookie first-party.
- The cookie has two names, `__Host-AzureBank.Session` outside Development and `.AzureBank.Session`
  in it, and the tests pin both.
- Not covered: Safari's refusal of a Secure cookie over `http://localhost`, the reason for the plain
  cookie in Development, is not measured here.

## Verified by

`SessionCookieTests` (name, prefix and attributes per environment), `FetchMetadataTests` (the allow
and deny matrix, and no CORS header), `UpstreamErrorForwardingTests` (a non-JSON body is a 502),
`SessionActivityTests` (session-status against `/me`).

## Related

ADR-0009, ADR-0019, ADR-0021, ADR-0055.
