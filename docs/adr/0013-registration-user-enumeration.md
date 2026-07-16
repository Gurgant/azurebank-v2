# ADR-0013: Registration user-enumeration — bounded, documented acceptance

**Status**: Accepted

**Date**: 2026-07-15

**Decision Makers**: Vladislav Aleshaev

---

## Context

Self-registration (`POST /api/auth/register`, and `bff/auth/register` which forwards to
it) lets an anonymous caller learn whether an email or handle already exists. Two signals:

1. **A distinct error** — a duplicate previously returned `409` with `errorCode`
   `DUPLICATE_EMAIL` / `DUPLICATE_AZURE_TAG` and the plaintext detail "Email is already
   registered." / "AzureTag is already taken." A single `curl` reads customer existence.
2. **A structural oracle from auto-login-on-register** — `RegisterAsync` creates the user
   **and a funded account and returns a JWT** on success. So a *fresh* email yields
   `201 + token`; a *duplicate* yields an error. No error-message wording can hide this:
   the presence/absence of the created-account + token distinguishes the two cases, and an
   attacker isolates the email check simply by supplying a throwaway unique handle.

The sibling **login** endpoint is already enumeration-safe (ADR-0012: identical generic
`401` for unknown-user / wrong-password / locked, plus a timing equalizer). There is **no
email infrastructure** in the repository (`EmailConfirmed = true // Skip email verification
for MVP`, no `IEmailSender` / SMTP), so the standards-prescribed closure — an out-of-band
"check your email" that is identical for new and existing accounts — is not currently
buildable without a new subsystem and the loss of the auto-login-on-register UX.

### What the standards actually require (graduated, not absolute)

- **OWASP ASVS v5.0.0 §6.3.8** is the only requirement that explicitly names registration
  enumeration ("Registration and forgot password functionality must also have this
  protection") — and it is **Level 3** (high-assurance defense-in-depth), not L1/L2.
- **ASVS §6.3.1 (Level 1)** mandates anti-brute-force / credential-stuffing controls —
  i.e. **rate limiting is the baseline that IS required**; enumeration-hardening is the L3
  uplift on top.
- **OWASP Authentication Cheat Sheet** prescribes the out-of-band email pattern as the
  closure, *and* explicitly blesses **rate-limiting + CAPTCHA** as the accepted mitigation
  when a helpful message is retained ("prevents an attacker from applying the enumeration
  at scale").
- **NIST SP 800-63B** does **not** mandate registration-enumeration protection at all (do
  not cite it as the source of this requirement).
- **Industry is split and mostly reveals**: GitHub / Google / Microsoft / Facebook show
  "email already registered" at signup; AWS Cognito's `SignUp` always throws
  `UsernameExistsException`. Hiding it *requires* the email-confirmation pattern.

## Decision

Adopt a proportionate, documented posture ("Option 1.5") rather than either leaving the
loud label or building a full email flow now:

1. **Rate limiting (the mandated L1 control), per client IP, at the BFF edge** — a generous
   `GlobalLimiter` baseline over all traffic (including the YARP-proxied `/api/*` bypass)
   plus a tight `auth` policy on `bff/auth/login`, `bff/auth/register`, and the dedicated
   YARP `/api/auth/login` + `/api/auth/register` routes. Rejections return `429` +
   `Retry-After` + `ProblemDetails(errorCode = RATE_LIMIT_EXCEEDED)`. Limits are
   configurable (`RateLimiting` section).
2. **Genericise the client-facing register response** — both duplicate-email and
   duplicate-handle now return an identical neutral `409` ("Registration could not be
   completed.", `errorCode = REGISTRATION_FAILED`). The specific reason is logged
   server-side only, as a structured `SecurityEvent = DuplicateRegistration` warning.
3. **Detective control** — that structured event lets an operator alert on bursts of
   duplicate-registration attempts (enumeration-pattern detection). Threshold wiring is an
   ops concern, deliberately out of the app.
4. **Login parity** is already delivered (ADR-0012); this ADR records it as part of the
   cross-endpoint consistency posture.
5. **Accept and document the residual; defer full closure.**

## Honest residuals (this ADR does not pretend to close the oracle)

- **The structural oracle remains.** Genericising *which field* collided is cosmetic:
  auto-login-on-register still yields `201 + token` for a free email versus a `409` for a
  taken one, and an attacker isolates the email check with a throwaway handle. **Only the
  deferred email-confirmation flow closes this.**
- **Rate limiting bounds MASS enumeration, not TARGETED lookups.** A single "is person X a
  customer?" probe needs one request; per-email throttling is near-useless (each address is
  a fresh key) and per-IP is defeatable via rotating IPs / botnets. The detective control
  partially compensates; it is not full prevention.
- **Bigger latent finding — unverified-email account provisioning.** Registration
  provisions a *funded* account and issues a session for an email the caller never proved
  they own (`EmailConfirmed = true`). For a bank this impersonation / KYC exposure is more
  serious than the enumeration wording, and the same email-confirmation flow closes **both**.
  Tracked as a separate follow-up.
- **Assurance level, honestly.** A bank should target ASVS L2/L3 for authentication, so
  §6.3.8 is a genuine **L3 gap that is consciously accepted** here with compensating
  controls and a named deferral trigger — not a control that "doesn't apply."

## Deferral trigger

Build the out-of-band email-confirmation flow (generic "check your email" for new and
existing accounts, no auto-login, confirm-token endpoint, `RequireConfirmedEmail`) **when
email infrastructure lands for its first real use cases** (password reset, transaction
alerts). Enumeration hardening should ride on that infrastructure rather than justify
standing it up on its own. That flow closes both the enumeration oracle and the
unverified-provisioning finding.

## Alternatives considered

- **Keep the explicit `DUPLICATE_EMAIL` 409 (Option 1).** Best signup UX and what many
  large apps ship, but for a *banking* app the plaintext customer-existence disclosure is
  the single most audit-flaggable line; rejected in favor of the cheap genericisation.
- **Genericise the message only, change nothing else (Option 2).** Rejected as security
  theater: it pays a (here-nonexistent) UX cost while leaving the structural token oracle
  fully open.
- **Full email-confirmation flow now (Option 3).** The only true closure and the
  OWASP-prescribed technique, but disproportionate for an MVP with no email infrastructure,
  and it would forfeit the auto-login-on-register demo. Deferred, not discarded.

## Consequences

**Positive** — the mandated L1 control (rate limiting) is now actually enforced (it was
previously dead config); the casual plaintext leak is removed; a detective signal exists;
the posture is documented and standards-accurate; login/register are consistent.

**Negative** — the registration existence oracle is **not** eliminated, only bounded and
made noisier to exploit at scale; a determined targeted lookup still succeeds. This is a
knowingly accepted, time-boxed residual with a concrete deferral trigger.

## Implementation notes (review hardening)

- **Rate-limit partition key & proxies.** The limiter partitions on the connection IP.
  Behind a proxy/LB that is the proxy's IP, which would collapse all clients into one
  partition (a DoS). Trusting `X-Forwarded-For` from *any* source is worse — an attacker
  rotates fake IPs and gets a fresh partition each time. So forwarded-header trust is
  **opt-in and fail-safe**: `X-Forwarded-For` is honoured only when the real proxy IPs are
  listed in `ForwardedHeaders:KnownProxies` (loopback defaults cleared; `UseForwardedHeaders`
  runs before the limiter). With none configured (the BFF is the edge) the header is ignored
  and the direct connection IP is used. **Any proxied deployment must set `KnownProxies`.**
- **Security config fails fast.** Both controls are validated at startup
  (`IValidateOptions` + `ValidateOnStart`, mirroring the pepper validator in ADR-0011): a
  non-positive rate-limit value or an unparseable `KnownProxies` entry stops the app. Both
  would otherwise fail *invisibly* — the limiter builds its windows lazily inside the
  partition factory (so a bad value throws per-request, not at boot), and a typo'd proxy IP
  is silently skipped, leaving `X-Forwarded-For` untrusted and collapsing every client into
  one partition. A log warning is not enough for a control whose failure mode is invisible.
- **Registration neutrality under concurrency.** The pre-checks are advisory. A duplicate
  that slips past them under a race surfaces either as a `Duplicate*` `IdentityResult` or a
  `DbUpdateException` at write time — **both** are neutralised to the same
  `409 REGISTRATION_FAILED` as the pre-check path, and Identity's error descriptions are
  logged server-side, never returned to the client. Without this, the race path re-opened the
  very oracle this ADR closes.
- **What is actually authoritative (corrected).** For the **handle**, a unique index on
  `AzureTag` (plus Identity's unique `NormalizedUserName` index) is the authoritative
  write-time guard. For the **email** there is **no unique index** — `NormalizedEmail` is
  indexed but *not* uniquely — so email uniqueness rests solely on Identity's in-process
  `RequireUniqueEmail` validator, which is **advisory**: under a genuine race two accounts
  could share an email, and the `DbUpdateException` path is therefore unreachable for email.
  This does not weaken the enumeration posture (every path still returns the identical 409),
  but it is a real data-integrity gap. Closing it (unique index on `NormalizedEmail` +
  migration, and making registration transactional — `AddToRoleAsync`'s result is currently
  discarded and the account insert sits outside the guard) is a tracked follow-up.

## Known limits of the rate limiter (accepted for this scope)

All real, all acceptable for a single-instance demo — written down so they read as decisions
rather than oversights:

1. **It is in-process.** Counters live in this process's memory, so they reset on restart and
   are **per-replica**: running N instances multiplies every advertised limit by N. The
   scale-out path is a distributed store behind the same `AddRateLimiter` API (e.g. a Redis
   backplane) or moving enforcement to a shared edge (WAF / API gateway). Not built here — a
   demo runs one instance, and the wrong lesson to take from a portfolio is that an MVP needs
   Redis.
2. **The API has no limiter of its own.** Everything here rests on the backend API (`:7215`)
   being reachable only via the BFF. If it is ever exposed directly, the whole control is
   bypassed.
3. **One shared `auth` bucket.** A single policy instance serves `bff/auth/{login,register}`
   and the YARP `/api/auth/{login,register}` routes, so all four share one per-IP budget.
   Deliberate: that shared budget *is* the per-IP enumeration/brute-force allowance.
4. **A rejected auth request still consumes a global permit** (fixed-window leases are not
   refunded on dispose). So an abusive IP burns its own global budget and eventually locks
   itself out of every BFF endpoint. Acceptable — arguably desirable.
5. **Only `/api/auth/login` and `/api/auth/register` carry the tight policy** on the proxy
   path; siblings such as `/api/auth/pin/verify` match the catch-all and get only the global
   baseline (the PIN has its own lockout, ADR-0010, so it is still bounded).
6. **The exact-path YARP policy is safe only because the BFF and the API share ASP.NET Core's
   path normalisation** — literal segments outrank `{**catch-all}`, and variants that dodge
   the guarded route (e.g. `/api//auth/login`) are 404'd by the API rather than reaching a
   login handler. Revisit if a non-ASP.NET service is ever placed behind the catch-all.
7. **IPv6 is keyed on the /64 prefix**, not the full address, because an end site is normally
   handed a whole /64 — keying per-address would let an attacker rotate within their own
   allocation for free. A determined attacker with multiple /64s (or a botnet) still splits
   across partitions; per-IP limiting bounds cost, it does not eliminate the attack.

## References

- OWASP ASVS v5.0.0 §6.3.1 (L1 anti-brute-force), §6.3.8 (L3 enumeration).
- OWASP Authentication Cheat Sheet; Forgot Password Cheat Sheet; WSTG-IDNT-04.
- ADR-0012 (login attempt-limiting + timing equalizer, enumeration-safe login).
