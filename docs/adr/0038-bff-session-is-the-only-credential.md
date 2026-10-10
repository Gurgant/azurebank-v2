# ADR-0038: The session is the only credential the BFF will accept

**Status:** Accepted · **Date:** 2026-08-10 · **Amended:** 2026-10-06 (decision 4). Supersedes
nothing. Corrects an assumption of ADR-0020 and closes the proxied half of its caveat.

## Context

The BFF exists so that the browser never holds a JWT (ADR-0019): tokens live server-side, keyed by a
session cookie, and a YARP transform injects one on the way to the API. The PIN step-up (ADR-0008)
is enforced by `AuthLevelMiddleware`, the only level-2 gate in the system: the API has no auth-level
concept and the JWT carries no level claim. Neither fact makes the session the only way to be
authenticated. A bearer token presented to a proxied route with no cookie read the unmasked account
number and reached `/api/transfers`, because two defects compounded. The gate's no-cookie branch
fell through to "let the API handle 401", and the transform set `Authorization` only inside the
cookie branch, so with no session YARP's default header copy carried the client's own header on.

## Decision

1. **The inbound `Authorization` header is stripped unconditionally, then the session's token is
   injected**: before the cookie branch, not inside it. `BearerTokenTransformProvider` is registered
   with `AddTransforms<T>()`, which applies it to every route, so the strip covers every proxied
   route, the auth ones included. A per-route fix would not.
2. **The step-up gate fails closed.** No resolvable session on a PIN-protected route is a refusal
   the BFF issues itself, not a question forwarded to the API. The strip already makes the API's 401
   real; the gate refuses anyway, because a gate whose correctness depends on another file getting
   something right is not a gate.
3. **401 for no session, 403 for level 1**, because they are different states and the SPA treats
   them differently: level 1 opens the PIN modal, no session routes to login. The 401 is
   byte-identical to the API's own missing-token problem response, so nothing downstream learns a
   new shape, and a caller probing for the gated paths cannot tell whether the BFF or the API
   answered.
4. **The browser's `Cookie` header is taken off the outbound request** in the same place and the
   same way: whole, on every proxied route, whether or not a session resolves, because the API reads
   no cookie and the session id is the BFF's own secret. Only the outbound copy goes: the BFF reads
   the session from the browser's request as before.

## Rejected

- Rejected: removing the proxied `/api/auth/login` route, because `api-route` matches
  `/api/{**catch-all}`, so the path stays proxied and only loses its tighter `auth` rate-limit
  policy. It also treats the symptom: what must not work is presenting a token.
- Rejected: fixing only the middleware, because it gates the PIN-protected paths alone. Every other
  proxied endpoint would stay reachable with a self-obtained token, and the server-side session
  model would be decorative.
- Rejected: fixing only the transform, because it is the stronger half but leaves the gate's logic
  wrong, and a future auth path that is not proxied would reopen it.
- Rejected here: an auth-level claim in the JWT, because it is a token-format change with a
  migration. It is the real fix for the direct-API residual below.

## Consequences

- The session is the only route to an authenticated call through the BFF, on every proxied path.
  ADR-0041 builds on it: every proxied request needs a live session (decision 4), and the proxied
  login and register answer 404 (decision 5).
- `RequireAuthLevelAttribute` is deleted. It was a plain attribute with no filter behaviour, applied
  to nothing: where the only gate is a middleware path list, a marker that looks like enforcement
  and is not is the same class of defect.
- The refusals are in the `SecurityEvent` series: `StepUpRequired` and `StepUpWithoutSession`,
  beside `RawRefreshBlocked` and `CrossSiteRequestBlocked`, so a dashboard filtered on that property
  sees them.
- Not covered: the direct-API residual of ADR-0020. The level lives in the BFF session, so the API
  itself answers `/full-number` to a JWT with no PIN. What stands in front of it is ADR-0055: the
  API refuses a caller without the BFF's service credential. Transfers no longer share the residual,
  because the API verifies their PIN (ADR-0041).

## Verified by

- `AuthLevelMiddlewareTests`: a client bearer with no session is not proxied, a client's
  `Authorization` is replaced by the session's token, no session is 401 and level 1 is 403.
- `BrowserCookieStaysInTheBffTests`: no cookie reaches the API.

## Related

ADR-0008, ADR-0019, ADR-0020, ADR-0041, ADR-0055.
