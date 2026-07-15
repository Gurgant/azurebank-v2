# ADR-0012: Password/login attempt-limiting (account lockout)

**Status**: Accepted

**Date**: 2026-07-15

**Decision Makers**: Vladislav Aleshaev

---

## Context

`AuthService.LoginAsync` verifies the password with `UserManager.CheckPasswordAsync`,
which only compares the hash — it never increments `AccessFailedCount`, never checks
or sets `LockoutEnd`, and never consults `LockoutEnabled`. So although
`IdentityOptions.Lockout` **is** configured (5 attempts / 15 min, `AllowedForNewUsers
= true`), it is **inert**: online password brute-force is unbounded. A repo-wide
search confirms **no** production code calls any Identity lockout API. This is the
"dormant password/login lockout" tracked as the ADR-0010 follow-up.

`ApplicationUser : IdentityUser<Guid>` already has the native `AccessFailedCount`,
`LockoutEnd`, `LockoutEnabled` columns (InitialCreate migration), distinct from the
PIN's dedicated `PinAccessFailedCount` / `PinLockoutEnd` (ADR-0010). LoginAsync also
has a **deliberate anti-enumeration** property: an unknown user and a wrong password
return the identical `401 Invalid email or password.` (guarded by a test).

## Decision Drivers

- Bound online password brute-force (defense-in-depth).
- **Preserve** the existing anti-enumeration guarantee — do not turn lockout into an
  account-existence oracle.
- Be **concurrency-correct** under a burst of parallel wrong passwords.
- Reuse Identity's **native** lockout fields (no new columns / migration).
- Keep the PIN lockout (ADR-0010) a separate concern.

## Considered Options

**Mechanism**
1. `SignInManager.CheckPasswordSignInAsync(lockoutOnFailure: true)` — one call, but
   (a) this is a JWT-only API (SignInManager pulls in cookie schemes), (b) it uses
   Identity's read-modify-write internally (see below), and (c) it evaluates lockout
   **before** the password, leaking account existence. Rejected.
2. `UserManager.AccessFailedAsync` / `IsLockedOutAsync` — a read-modify-write of
   `AccessFailedCount` guarded by the `ConcurrencyStamp`. Under a parallel burst the
   `DbUpdateConcurrencyException` is **swallowed** by EF's `UserStore` into
   `IdentityResult.ConcurrencyFailure` → a **silent lost update**, so the burst can
   stay under the threshold and never lock. Rejected.
3. **Atomic set-based `ExecuteUpdate` on the native columns (chosen)** — no
   read-modify-write, no lost updates; mirrors `PinService` (ADR-0010).

**Wire contract (the enumeration tension)**
1. `429 ACCOUNT_LOCKED` whenever the account is locked — rejected: regresses
   anti-enumeration (reveals the email exists and is locked).
2. Generic `401` always, even when locked — rejected: a legitimate user with the
   correct password gets a misleading "invalid credentials" and no back-off signal.
3. **Generic `401` for every wrong password; `429` only when the CORRECT password
   hits a locked account (chosen)** — the lock is revealed only to someone who has
   already proven knowledge of the password, so it is not an enumeration oracle.

## Decision

- **Wire the lockout in `AuthService.LoginAsync`** using Identity's **native**
  `AccessFailedCount` / `LockoutEnd` — no new columns, no migration.
- **Atomic writes**: increment-and-maybe-lock (and reset-on-success) run as a single
  set-based `ExecuteUpdateAsync` (relational; an InMemory fallback mirrors it),
  evaluating the threshold against the row's current value in one statement — so a
  parallel burst never loses updates and a just-applied lock is never cleared. The
  increment additionally carries a `WHERE (LockoutEnd IS NULL OR LockoutEnd < now)`
  guard, so a late concurrent attempt that lands after a peer already latched the lock
  updates zero rows and leaves **no residual count** — the next window gets a full
  budget. It runs on the request-scoped `DbContext` (login has no idempotency/monetary
  transaction to isolate, unlike the PIN path). These set-based writers deliberately
  bypass Identity's `ConcurrencyStamp` OCC token (which is what makes them race-free);
  no code path issues a subsequent Identity save on the same tracked instance, and each
  writer bumps the `UpdatedAt` audit column explicitly (SaveChanges interceptors don't
  run for `ExecuteUpdate`). The same guard + audit-bump were applied to the PIN lockout.
- **Token expiry**: `IJwtService.GenerateToken` returns the token together with its
  exact expiry (read back from the token's `exp`), so `LoginResponse`/`RegisterResponse`
  never recompute the lifetime — no config drift, no `UtcNow` skew.
- **Enumeration-safe contract**:
  - unknown user **or** wrong password → **401** `INVALID_CREDENTIALS`
    (`Invalid email or password.`), identical in all cases;
  - **correct** password on a **locked** account → **429** `ACCOUNT_LOCKED` +
    `Retry-After` (`AccountLockedException`, structurally identical to
    `PinLockedException`: `retryAfterSeconds` + `lockedUntil` in Details);
  - a wrong password while **already locked** does **not** extend the window (mirrors
    the PIN refusing before counting).
- **Policy**: `ValidationRules.MaxLoginAttempts` (5) / `LoginLockoutMinutes` (15),
  centralized and shared with the `IdentityOptions.Lockout` configuration.

## Consequences

### Positive
- Online password brute-force is bounded (5 / 15 min).
- The anti-enumeration guarantee is **preserved**: a password guesser always sees the
  same 401 whether the account exists, doesn't, or is locked.
- Concurrency-correct — proven by a `[SqlServerFact]` parallel-burst lock proof.
- No schema change (native Identity fields).

### Negative
- Account lockout inherently enables a **victim-lockout DoS**: an attacker who knows a
  victim's email can lock it with wrong passwords. This is intrinsic to any lockout;
  mitigate with per-IP rate limiting (future work), not by removing the lock.
- A locked account still runs a password hash per attempt — required so the response
  timing is uniform for wrong passwords (no timing-based lock detection). This makes a
  locked account an (attacker-locked) hashing endpoint; bound it with upstream per-IP
  rate limiting (future work).

### Neutral / known limitations
- The PIN lockout (ADR-0010) stays a separate mechanism (dedicated columns; it returns
  429 even on a wrong PIN, which is fine because that path is post-authentication and
  carries no enumeration concern).
- Anti-enumeration is **also hardened against timing**: the unknown-user path runs a
  dummy password verification (via the UserManager's own hasher, so the cost tracks the
  real verifier) to spend the same **dominant** PBKDF2 cost a real account would — the
  large hash-latency oracle is closed, not just the response body. A **smaller residual**
  remains: a wrong password on an *existing, unlocked* account performs one extra DB
  write (the failed-attempt increment) that the unknown-user path does not, so a fine
  timing analysis could still separate the two populations. This is a much weaker signal
  than the hash oracle and is bounded by the upstream per-IP rate limiting noted above;
  fully equalizing the write path (or a fixed response-time floor) is left as future work.
- **Per-user opt-out respected**: the lockout honors Identity's `LockoutEnabled` flag
  (matching `IsLockedOutAsync`) — an exempt account (e.g. a service account) is never
  treated as locked and never accrues lock state, so a future "disable lockout for user
  X" toggle takes effect. Every registered user has it `true` via `AllowedForNewUsers`,
  so this is defensive alignment, not a current behavior change. The PIN lockout is a
  separate mechanism and is intentionally *not* governed by this password-scoped flag.

## Validation

- **Unit** (`AuthServiceTests`): wrong password increments; the threshold attempt
  locks (while still returning 401); a locked account + correct password → 429
  `ACCOUNT_LOCKED` with a positive `retryAfterSeconds` and **no token**; a locked
  account + wrong password → generic 401 **without** extending the window; a correct
  password resets the counter; an expired lock starts a fresh window. The existing
  anti-enumeration test still holds.
- **Integration** (`AuthEndpointTests`): 5 wrong passwords each return 401, then the
  correct password returns 429 + `Retry-After` ≈ 15 min + `ACCOUNT_LOCKED`.
- **SQL Server** (`LoginLockoutConcurrencySqlServerTests`): exactly-threshold parallel
  wrong passwords lock the account, and a 3× burst stays locked with no race bypass —
  proving the atomic counter beats Identity's lost-update read-modify-write.

## Related

- ADR-0010 (PIN attempt-limiting) — the mirror; **this resolves its follow-up**. I
  deviate from its "use `SignInManager.CheckPasswordSignInAsync`" note for
  concurrency-correctness, enumeration control, and the JWT-only fit.
- ADR-0003 (Argon2id) — account passwords use Identity's own hasher (out of scope here).
