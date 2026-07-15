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

## References

- OWASP ASVS v5.0.0 §6.3.1 (L1 anti-brute-force), §6.3.8 (L3 enumeration).
- OWASP Authentication Cheat Sheet; Forgot Password Cheat Sheet; WSTG-IDNT-04.
- ADR-0012 (login attempt-limiting + timing equalizer, enumeration-safe login).
