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

*(Narrowed 2026-09-03, ADR-0045: the operator tool can now render a message to the account's
email and write it to a pickup directory, so "no email infrastructure" is no longer the whole
sentence — "no relay" is. The conclusion stands: an out-of-band confirmation needs a message the
registrant actually receives, and nothing here sends one.)*

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
  and it would forfeit the auto-login-on-register demo. Deferred, not discarded. *(Still deferred
  after ADR-0045, which renders but does not send; see the note above.)*

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
  and the direct connection IP is used. ~~**Any proxied deployment must set `KnownProxies`.**~~
  *(struck 2026-10-06: a proxied deployment must list its proxy, by exact address or by network.
  The Azure deployment stands behind the platform's ingress, and an exact address stops matching,
  in silence, the day the platform moves its proxy inside a range it owns: every visitor is then
  one partition again, and nothing says so. So a second list stands beside the first,
  `ForwardedHeaders:KnownIPNetworks`, networks in CIDR form, named as the framework names its
  own. The rule is the one above, for both: the header is honoured only on a connection that
  comes from a listed address or from inside a listed network, the caller is the last entry, the
  one that proxy appended (`ForwardLimit` stays 1), and with both lists empty nothing reads the
  header. Decided with it:*

  - *A connection with no address is not a listed proxy. Once anything is listed the framework's
    middleware believes the header on such a connection (measured on .NET 10 that day, on the
    test server, whose connections have none, with the condition taken out; a Unix socket and a
    named pipe have none either), so it now runs only on a connection that has an address. That
    holds for exact addresses too.*
  - *A network is refused at startup, in a sentence that names it, when it is not
    `address/prefix-length`; when its prefix length is outside its family's range; when it is
    `/0`, which trusts everybody; when it is wider than a `/8`, in either family, the width of
    the widest private blocks (`10.0.0.0/8`, `fd00::/8`): a shorter prefix reaches into
    somebody else's addresses and is far more likely a slip than a network; when it is an
    IPv4-mapped IPv6 network, ~~which matched no address in the measurement and would fail in
    silence~~ (corrected later that day: measured on the pipeline with the refusal taken out,
    `::ffff:10.0.0.0/104` was a listed network for a connection the socket reports as
    `::ffff:10.0.0.5` and not for one it reports as `10.0.0.5`, and `::ffff:0:0/96` for
    neither, so such an entry would work or fail, in silence, by how the socket is bound; the
    IPv4 network holds both forms); and when the framework reads it as another network than it
    shows. The last was measured: `System.Net.IPNetwork.TryParse` reads `10.0.0.1/8` as
    `10.0.0.0/8`, `010.0.0.0/8` as `8.0.0.0/8` and `10/8` as `0.0.0.0/8`.*
  - *The header is made a list of addresses and nothing else before the framework reads it
    (added later that day, `StrictForwardedFor`). "The caller is the last entry" was not true of
    the framework's reading alone. It splits the header as a list of quoted strings, and its
    address parser drops whatever follows a `%` as an IPv6 zone: a caller that wrote
    `::ffff:198.51.100.200%"` before the proxy's entry was taken for `198.51.100.200`, and it
    could name another address in every request, each a new budget at both limits and a new
    day's allowance of copies; a quotation mark alone made the caller the proxy itself
    (measured on the BFF's pipeline and on a loopback socket, .NET 10). Against anybody who
    knew it, listing a proxy would then have been worse than listing none. So the
    header's lines are split at every comma, and an entry that holds any character an address
    is not written with becomes the word `unknown`; which entries are believed stays the
    framework's. Refused: writing a reader of the header in place of the framework's, which
    would have carried its own mistakes; and leaving it to the platform's ingress, which
    nothing here has measured.*
  - *A public range is not refused. A proxy may have public addresses, and whether a range is
    the proxy's cannot be read from the range: that is measured on the deployment and proved
    there. `KnownProxies` is checked as it was: the same lenient parser reads its entries, and a
    slip there trusts one other address, not a network of them.*
  - *On Azure nothing is set by it. `infra/main.bicep` takes the networks as a parameter that is
    empty by default, on the `bff` container alone, and no network of the deployment is written
    in any file; `infra/README.md`, "Turn the demo on", says how the address is read, how the
    networks are chosen and set, and what proves them. None of that has run.*

  *What it leaves open: every address inside a listed network can name a caller's address, so
  the list is as good as the measurement behind it; one hop is believed, and behind two proxies
  in a row every visitor would have the address of the one in front, one client as before; and
  that the ingress appends the visitor's own address after whatever the visitor wrote is
  expected, not seen, until the runbook's proof has run. On the road to the API nothing reads a
  forwarded header: the proxy writes `X-Forwarded-For` itself, from the address the BFF holds by
  then, the browser's own `X-Real-IP` goes on as sent, and with a proxy listed the framework's
  `X-Original-For` goes on too, holding the proxy's address. Held by
  `TrustedProxyNetworkTests`, `ForwardedForOnARealConnectionTests`, `StrictForwardedForTests`
  and `ProxyOptionsValidatorTests`.)*
  *(2026-10-07: "None of that has run" is no longer so. Microsoft's page on the networking of a
  Container Apps environment names the ranges an environment reserves for its own
  infrastructure; the two addresses the limiter had named the day before, neither of them the
  caller's, were both inside one of them, and one run of the template named that one on the
  deployed `bff` container. After it, from one connection, twelve sign-ins in under four seconds
  were answered ten times and refused at the eleventh and at the twelfth, four times over, two
  of the four with an `X-Forwarded-For` header that named another address in each request; and
  the limiter's 52 warnings, as many as that caller's refusals up to then, all named that
  caller's own public address, none an address of the ingress and none an address a header had
  named. So the entry the BFF believed, the last one, was the caller's own, after whatever the
  caller wrote: what this note left as "expected, not seen" is seen for one hop. Two proxies in
  a row are still not seen. The proof's own line, a caller on a second network answered while
  the first is held to its ten, was read in a fifth run: with one sign-in a second from that
  connection, the limiter let eleven requests through inside 13 seconds, the connection's ten
  and, in the middle of them, a copy claimed from a phone on a mobile network; the
  connection's next request was refused, and that run's 127 warnings named the connection's
  own address as the 52 had. No network of the deployment is written in any file, as before
  (`infra/README.md`, "Measured on Azure").)*
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
- **What is actually authoritative.** For the **handle**, a unique index on `AzureTag` (plus
  Identity's unique `NormalizedUserName` index) is the authoritative write-time guard. For the
  **email**, a unique NULL-filtered index on `NormalizedEmail` is now the authoritative guard
  too (migration `AddUniqueEmailIndex`) — so a genuine race loses the unique-index write and
  hits the same `DbUpdateException` → neutral `409` path, and two accounts can no longer share
  an email. Identity's in-process `RequireUniqueEmail` validator remains only an advisory
  fast-path. Proven by a SQL-Server parallel-burst test (`RegistrationEmailRaceSqlServerTests`:
  N concurrent same-email registrations → exactly one `201`, the rest `409`, exactly one row).
  Making the multi-step registration fully atomic (user + role + default account committed under
  one transaction, wrapped in the retry execution strategy) ~~remains a tracked follow-up.~~
  *(struck 2026-09-17: delivered 2026-08-09 by [ADR-0037](0037-atomic-registration.md), PR #94.
  The user, its role and the starter account commit in one `ExecuteInTransactionAsync` run through
  the execution strategy, and `RegistrationAtomicitySqlServerTests` proves on real SQL Server that
  a failed account write leaves no user behind while a real duplicate still gets the neutral `409`
  described above.)*

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
