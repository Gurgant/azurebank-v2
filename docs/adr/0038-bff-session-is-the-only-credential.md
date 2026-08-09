# ADR-0038: The session is the only credential the BFF will accept

**Status:** Accepted · **Date:** 2026-08-10 · **Supersedes nothing.** Corrects an assumption made by
[ADR-0020](0020-account-number-reveal.md) and closes the proxied half of the caveat it recorded.

## Context

Before this ADR, the posture was already meant to be closed. The BFF exists so the browser never
holds a JWT ([ADR-0019](0019-spa-bff-integration.md)): tokens live server-side, keyed by a session
cookie, and a YARP transform injects one on the way to the API.
The PIN step-up ([ADR-0008](0008-step-up-authentication.md)) is enforced by `AuthLevelMiddleware`, which
is the **only** level-2 gate in the system — the API has no auth-level concept and the JWT carries no
level claim.

Both of those statements were true. What was not true is that a caller had to go through the session
to be authenticated.

**Measured on the running stack** (API `:7215`, BFF `:5000`, seeded dev database), before any code was
written:

```
POST /api/auth/login   (a PROXIED route)                     -> 200, raw JWT in the body
GET  /api/accounts/{id}/full-number + Bearer, NO cookie      -> 200  {"accountNumber":"AB-0000-0000-01"}
GET  same, with NO Authorization header                      -> 401
POST /api/transfers + Bearer, NO cookie, bogus recipient     -> 404 from /api/transfers
```

The third line is the control: the header was being honoured, not ignored. The fourth reached the
endpoint — a real recipient would have been paid with no PIN entered.

Two defects compounded, and neither is sufficient alone:

1. **`AuthLevelMiddleware` failed open.** The level-2 check lived inside
   `if (Request.Cookies.TryGetValue(cookieName, out var sessionId))`, and its `else` branch fell
   through with the comment *"No session cookie - let the API handle 401"*. That delegation holds
   only while the API cannot authenticate the caller.
2. **The transform never cleared an inbound `Authorization` header.** It *set* one inside the same
   cookie branch, so with a session the client's header was overwritten — but with no session there
   was nothing to overwrite, and YARP's default header copy had already placed the client's on the
   outbound request.

The token was obtainable because `/api/auth/login` is proxied. Removing that route would not have
helped: `api-route` matches `/api/{**catch-all}`, so the path stays proxied and only loses its
tighter `auth` rate-limit policy. `RateLimiterTests` already says so — *"the proxy path that bypasses
`BffAuthController` … guarded only because ASP.NET routing scores the literal `/api/auth/login` above
`/api/{**catch-all}`"*.

## Decision

**Strip the inbound `Authorization` header unconditionally, then inject from the session.** One line,
before the cookie branch rather than inside it. `BearerTokenTransformProvider` is registered with
`AddTransforms<T>()`, which applies it to **every** route, so this covers all four proxied routes
including the two auth ones — a per-route fix would not have.

**And make the step-up gate fail closed.** No resolvable session on a PIN-protected route is a
refusal the BFF issues itself, not a question forwarded to the API. The header strip already makes
the API's 401 real rather than hoped-for; the gate refuses anyway, because a gate whose correctness
depends on another file getting something right is not a gate.

**401 for no session, 403 for level 1.** These are different states and the SPA treats them
differently: level 1 opens the PIN modal, no session must route to login. The 401 is byte-identical
to the API's own missing-token problem response — observed on the running stack, not copied from a
doc — so nothing downstream learns a new shape, and a caller probing for which paths carry the gate
cannot tell whether the BFF short-circuited or the API answered.

## What this rejects

- **Removing the proxied `/api/auth/login` route.** It would not stop the path being proxied (the
  catch-all matches it) and would silently downgrade its rate limit. A `RequiresPinVerification`-style
  short-circuit, as `/api/auth/refresh` already has, would work — but it treats the symptom, since the
  token is obtainable from the real `/bff/auth/login` in any case: what must not work is *presenting*
  one.
- **Fixing only the middleware.** It gates two paths. Every other proxied endpoint would stay
  reachable with a self-obtained token, and the server-side session model would be decorative.
- **Fixing only the transform.** It is the stronger half, but it leaves the gate's logic wrong, and a
  future non-proxied auth path would reopen it.
- **Adding an auth-level claim to the JWT.** That is the real fix for the residual below, and it is a
  token-format change with a migration; out of scope here for the same reason ADR-0020 gave.

## Consequences

- **`RequireAuthLevelAttribute` is deleted.** It was a plain `Attribute` with no filter behaviour,
  applied to nothing, whose doc comment said it *"marks an endpoint as requiring a minimum
  authentication level"*. In a codebase whose only gate is a middleware path list, a marker that
  looks like enforcement and is not is the same class of defect as the one this ADR closes.
- **The direct-API residual from ADR-0020 stays open, and is now the whole of it.** Calling the API
  on `:7215` with a JWT still bypasses the PIN, because the level lives in the BFF session. ADR-0020
  recorded that as accepted and it remains so. What ADR-0020 did *not* contemplate — and asserted the
  opposite of, in `AccountController`'s own doc comment — is that the same bypass was reachable
  **through the BFF**. That half is what closed here.
- **The denial is now in the `SecurityEvent` series.** The step-up refusals log
  `SecurityEvent StepUpRequired` and `SecurityEvent StepUpWithoutSession`, matching
  `RawRefreshBlocked` and `CrossSiteRequestBlocked`, so a dashboard filtered on that property sees
  them. The previous message carried no such property and was invisible to it.
- **Four BFF tests pin the behaviour**, three of which failed before the change; the fourth passed
  and stays as a regression guard, because with a session the client's header was already being
  overwritten and that must not regress.
