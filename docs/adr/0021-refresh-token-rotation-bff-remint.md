# ADR-0021: Refresh-token rotation with reuse-detection (+ BFF silent re-mint)

**Status**: Accepted (PR-1: API rotation). BFF silent re-mint is planned as a follow-up PR (PR-2).

**Date**: 2026-07-22

**Decision Makers**: Vladislav Aleshaev

---

## Context

Access tokens are 15-minute JWTs (`Jwt.ExpirationMinutes = 15`, `ClockSkew = Zero`). Before
this ADR the API minted **only** the access token — `AuthService.LoginAsync`/`RegisterAsync`
returned a JWT and nothing else, `LogoutAsync` was a no-op, and there was no `/refresh`
endpoint. The BFF's `InMemoryTokenStore.IsSessionValid` treats access-token expiry as a
session-KILL condition, so an **actively working user is hard-logged-out every 15 minutes** —
the session cannot outlive one access token.

The refresh apparatus was fully **scaffolded but 100 % dormant**: a `RefreshToken` entity
(SHA-256 `TokenHash`, `ExpiresAt`, `RevokedAt`, `ReplacedByTokenId` rotation-chain self-ref,
`IpAddress`/`UserAgent`, `IsActive`/`IsExpired`/`IsRevoked`), its EF configuration + unique
index + `IX_RefreshTokens_UserId_Active`, the `RefreshTokens` table already shipped in
`InitialCreate`, `JwtOptions.RefreshTokenExpirationDays = 7`, and even a dead
`SessionService.RefreshSession(...)` on the BFF — none of it read or written anywhere. So this
work is **wiring, not designing from scratch**.

What the standards actually say, verified against primary sources:

- **RFC 9700 (OAuth 2.0 Security BCP, Jan 2025) §4.14.2** — refresh-token rotation: "the
  authorization server issues a new refresh token with every access token refresh response.
  The previous refresh token is invalidated, but information about the relationship is
  retained." Reuse detection: replay of an invalidated token ⇒ revoke the active token /
  family. Idle expiry (§4.14.2): refresh tokens SHOULD expire after inactivity.
- **RFC 9700 §2.2.2** scopes the *mandatory* rotation requirement to **public** clients. Our
  BFF is a **confidential** client (holds tokens server-side), so rotation here is
  **defense-in-depth — recommended, not strictly mandated**. Duende's BFF, for a confidential
  client, deliberately *reuses* refresh tokens by default, judging rotation's marginal gain
  outweighed by DB churn + network-retry breakage.
- **OWASP OAuth2 Cheat Sheet** — store a **hash** of the token, never plaintext; use
  cryptographically-secure random values (256-bit+), not GUIDs; on reuse, revoke the family +
  force re-auth.
- **IETF "OAuth 2.0 for Browser-Based Applications" BCP §6.1.2.2** — the BFF performs re-mint
  **inline, on-demand** when handling an API call (not on a background timer); refresh-token
  lifetime SHOULD track the session lifetime; cookie MUST be `Secure`+`HttpOnly`, SHOULD be
  `SameSite` + `__Host-` (already true here, ADR-0018).

## Decision

Wire the dormant apparatus into a rotate-on-use refresh flow with reuse-detection, delivered
in two PRs so the auth-critical surface stays reviewable:

**PR-1 (this ADR — API):**

1. **Issue on login/register.** A refresh token = **256 bits of CSPRNG entropy**, URL-safe
   Base64 (same scheme as the BFF session id), returned to the caller **once**; only its
   **SHA-256 → Base64** hash is persisted (`ValidationRules.TokenHashLength = 44`). Lifetime =
   `now + RefreshTokenExpirationDays` (7 days).
2. **`POST /api/auth/refresh`** (`[AllowAnonymous]` — the refresh token *is* the credential;
   the access token being refreshed may already be expired). Rotates: the presented token is
   revoked, a chained successor is minted (`ReplacedByTokenId`), and a fresh access token is
   minted for the same user.
3. **Reuse-detection = theft response.** Replaying an already-revoked token revokes **every
   active refresh token for that user** (matches the entity's documented "revoke ALL user
   tokens" intent + the existing `IX_RefreshTokens_UserId_Active` index) and returns 401.
4. **Uniform failure.** Unknown / expired / reused all return **401 `REFRESH_TOKEN_INVALID`** —
   an identical status + code + body, so the response is never an oracle for *why* a refresh
   was rejected (the specific reason is logged server-side as a `SecurityEvent`).
5. **Un-forkable rotation.** The presented token carries an optimistic-concurrency **rowversion**,
   so two concurrent rotations of the same token cannot both commit — the loser's UPDATE matches
   zero rows and EF rolls the whole unit back (the loser gets a benign 401). Without this, a
   concurrent double-rotation would *fork* the chain and silently defeat reuse-detection. A
   just-rotated token replayed within a short (10 s) **grace window** is treated as a benign
   lost-response retry (RFC 9700), rejected with 401 but *without* revoking the family.
6. **Logout revokes.** `LogoutAsync` now revokes the user's active refresh tokens.
7. **Hosted cleanup.** `RefreshTokenCleanupService` sweeps expired rows every 6 h (hygiene —
   reads are already expiry-filtered). Because the table self-references itself
   (`ReplacedByTokenId`, `DeleteBehavior.Restrict`), the sweep first NULLs intra-set links,
   then deletes.

**PR-2 (planned — BFF silent re-mint):** add the refresh token to `UserSession`; a
single-flight `EnsureFreshAccessToken` used by the YARP bearer-injection transform **and** the
verify-pin/set-pin paths; decouple `IsSessionValid` from access-token expiry so the 15-minute
token is re-minted inline while the session slides within its inactivity(30 m)/absolute(60 m)
budget. **No mandatory FE change** — the existing client-activity sliding-window warning
becomes *correct* under re-mint, and the global-401 handler already covers reuse-detection
revocation.

### Key design decisions

| Decision | Choice | Rationale |
|---|---|---|
| Rotate vs. reuse | **Rotate-on-use + reuse-detection** | Schema is purpose-built for it (`ReplacedByToken` chain); RFC 9700/OWASP-recommended; strong portfolio signal. (Duende's confidential-client *reuse* default noted as the lighter alternative.) |
| Reuse response | **Revoke ALL the user's active tokens** | Matches the entity's documented intent + the existing user-active index; no migration. Per-family `FamilyId` revocation (multi-device precision) is a future refinement. |
| Concurrency | **Optimistic-concurrency rowversion on the presented token + a short (10 s) grace window** | Rotation is un-forkable *regardless of caller* — concurrent rotations of the same token can't both commit (loser → benign 401), so reuse-detection can't be silently bypassed. A just-rotated token replayed within the window is a benign lost-response retry, not theft. (The BFF still adds single-flight in PR-2 to avoid the wasted round-trip, but correctness no longer depends on it.) |
| Failure signalling | **Uniform 401 `REFRESH_TOKEN_INVALID`** | No unknown-vs-expired-vs-reuse oracle. |
| Token lifetime | **7-day sliding** (bounded in practice by the BFF session's 60 m absolute cap) | Aligns with RFC 9700 idle-expiry; the session store is the real bound. |

## Consequences

**Positive**

- The 15-minute hard-kill of active users disappears once PR-2 lands; sessions slide.
- Tokens are useless at rest (hash-only); theft is contained (reuse ⇒ family revocation);
  logout genuinely ends re-mint ability.
- The `RefreshToken` self-ref chain gives a per-user audit trail of rotations.

**Residuals (honest)**

- **In-memory BFF session store ⇒ BFF restart = logout, and multi-instance needs a shared store.**
  The session (and its cached refresh token) lives only in process memory; a restart drops it
  (the cookie's session id maps to nothing → forced re-login), and a second BFF instance can't
  see sessions created on the first. Production scale needs a distributed session store (Redis).
  Note: rotation *correctness* is already multi-instance-safe — the rowversion guard + grace
  window make it caller-independent — so only the session cache, not the refresh flow, needs Redis.
- **No `FamilyId` column.** Reuse revokes *all* of a user's active tokens, not just the
  affected lineage — on multi-device this logs out every device. Acceptable (and arguably
  correct) as a theft response; `FamilyId` is the future precise-revocation refinement.
- **Orphan refresh rows until PR-2.** PR-1 issues a refresh token on every login/register that
  the current BFF ignores; those rows are inert and reaped by the cleanup sweep.
- **Sender-constraining (DPoP/mTLS) is out of scope** — not required for a confidential BFF;
  a future option, not a gap.

## Verification

- Unit (`RefreshTokenServiceTests`): issue (hash-at-rest), rotate (revoke + chain), reuse
  **after** the grace window ⇒ revoke-all, reuse **within** the grace window ⇒ benign (no
  family revoke), expired, unknown, bulk-revoke scoping.
- Integration (`AuthEndpointTests`): login+register issue; rotate returns a new pair; an
  immediate replay within grace ⇒ 401 but leaves the successor usable; unknown ⇒ 401;
  refresh-after-logout ⇒ 401.
- SQL-gated (`RefreshTokenRotationSqlServerTests`): the self-referencing FK rotation-chain
  write and the set-based family-revoke; **8 concurrent rotations of the same token ⇒ exactly
  one successor, no fork, no false reuse-revoke** (the rowversion guard); and a correct login
  after a failed attempt issues a refresh token **without re-inserting the detached principal**.
  InMemory only approximates the relational + concurrency paths. Full suite green on InMemory +
  SQL Server (2×).
