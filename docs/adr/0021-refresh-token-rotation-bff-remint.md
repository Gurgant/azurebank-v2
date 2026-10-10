# ADR-0021: Refresh-token rotation with reuse-detection (+ BFF silent re-mint)

**Status:** Accepted; the rotation and its reuse detection (decisions 3, 5 and 6) are superseded by
ADR-0057 · **Date:** 2026-07-22 · **Amended:** 2026-08-04, 2026-08-08 (ADR-0034), 2026-09-25,
2026-09-28 (ADR-0057), 2026-09-30 (ADR-0058) · **Decision Makers:** Vladislav Aleshaev

## Context

Access tokens are 15-minute JWTs with no clock skew. Before this decision the API minted only that
token, and the BFF ended a session when its access token expired, so a user at work was signed out
every 15 minutes. The `RefreshToken` entity and its table were already in the schema, unused. The
BFF is a confidential client, for which RFC 9700 §2.2.2 does not mandate rotation. OWASP's OAuth2
cheat sheet asks for a stored hash, never the token, and for at least 256 random bits; RFC 10017
§6.1.2\.2 describes a BFF that typically renews while it handles an API call.

## Decision

**The API issues a refresh token at sign-in and renews access tokens from it, and the BFF renews
silently, so the browser sees neither a token nor an expiry.** The API's part is decisions 1 to 7:

1. **Issue on login and register.** A refresh token is 256 bits from a CSPRNG in URL-safe Base64,
   returned once; only its SHA-256 hash is stored (`ValidationRules.TokenHashLength = 44`), because
   a stored token is a credential at rest. Its lifetime is ADR-0057 §4.1's. A failed issuance fails
   a login; at registration it is best-effort (a null token), because the user is already committed.
2. **`POST /api/auth/refresh` mints a fresh access token for the same user.** It is
   `[AllowAnonymous]`, because the refresh token is the credential and the access token may already
   have expired. What a renewal reads and writes is ADR-0057 §4.3.
3. **Superseded, see ADR-0057 §4.3 and F3:** a grant presented after its session ended is recorded
   and revokes nothing, where a replayed token once revoked every token of its user, so the
   amendment of 2026-08-04 (a failing family revoke keeps the 401) no longer applies.
4. **Uniform failure.** Every refused renewal answers the same 401 `REFRESH_TOKEN_INVALID`, in
   status, code and body, so the answer never says why; the reason is logged as a security event.
5. **Superseded, see ADR-0057:** nothing rotates, so there is no chain to fork and no grace window.
6. **Superseded, see ADR-0057 §4.4 and §4.6:** signing out revokes the grant of that one session.
7. **Hosted cleanup.** `RefreshTokenCleanupService` deletes expired rows every
   `Jwt:RefreshTokenCleanupInterval` (6 h unless set): hygiene, because every read filters on
   expiry. The table references itself (`ReplacedByTokenId`), so the sweep nulls those links first.

The BFF's part, the silent re-mint (the code cites it as PR-2):

- **Where.** The YARP transform that puts the bearer token on every proxied `/api/**` call, and
  controller paths that bypass it such as verify-pin and set-pin, call
  `ITokenRefresher.GetAccessTokenAsync` first. When and how it renews is ADR-0057 §4.5.
- **Validity decoupling.** `InMemoryTokenStore.IsSessionValid` no longer ends a session at
  access-token expiry while it holds a refresh token: it slides within the inactivity and absolute
  limits (15 and 60 minutes, ADR-0057 §4.1 and §4.8). A session without one keeps the hard stop.
- **Raw-refresh block.** A browser's `POST /api/auth/refresh` through the proxy answers 404 in
  `AuthLevelMiddleware`, because only the BFF drives renewal; ADR-0057 §4.2 widens the block.
- **No frontend change.** The SPA's global 401 handler already covers a session the BFF ends because
  the API called its grant invalid.

## Rejected

- Rejected: failing a registration whose token cannot be stored, because the user already exists.
- Rejected: renewing from `/bff/auth/me`, because `/me` is deliberately not a keep-alive (ADR-0018).
- Rejected: DPoP or mutual TLS, because the BFF never shows the browser a token (RFC 9700 §4.14
  leaves it optional for a confidential client); ADR-0057 turns that topology into a precondition.

## Consequences

- A user at work is no longer signed out every 15 minutes, and a stored token is only a hash.
- Not covered: the BFF keeps sessions in memory, so a restart signs everyone out, and a second
  instance needs a shared session store first.
- Not covered: a killed BFF leaves its grants live, with no session, until expiry (ADR-0057 §4.6).
- Not covered: a grant revoked with no stamp raised, or while the BFF cannot read the stamps, is
  found only at the session's next renewal, up to 7.5 minutes later (ADR-0057 §5.3); until then
  `/bff/auth/me` reports the session as signed in, and no data is reachable with a dead grant.

## Verified by

- The API: `RefreshTokenServiceTests`, `AuthEndpointTests`, `RefreshTokenRotationSqlServerTests`.
- The BFF: `TokenRefreshTests`. The sweep: `RefreshTokenCleanupServiceTests`.

## Related

ADR-0018, ADR-0034, ADR-0057, ADR-0058.
