# ADR-0010: API-side PIN attempt-limiting (lockout)

**Status:** Accepted · **Date:** 2026-07-14 · **Amended:** 2026-08-06, 2026-09-15 (ADR-0040) ·
**Decision Makers:** Vladislav Aleshaev

## Context

A 6-digit PIN, hashed with Argon2id (ADR-0003), is the step-up credential in front of money
operations. With no attempt limit on the server, anyone holding a valid JWT can try the 10^6 PIN
space online. The PIN is checked at more than one server-side site, and a limiter on one of them
leaves the others open. Identity's password lockout is a separate concern, configured (5 attempts,
15 minutes) and dormant until ADR-0012 wires it, so this record has no working lockout to mirror
and builds the first one backed by the database. Counting an attempt must not weaken the
idempotency guarantees of ADR-0009.

## Decision

1. **Two dedicated columns on `ApplicationUser`**, `PinAccessFailedCount` (int) and `PinLockoutEnd`
   (`DateTimeOffset?`), added by the migration `AddPinLockout`, because Identity's own
   `AccessFailedCount` and `LockoutEnd` are password-scoped.
2. **After `ValidationRules.MaxPinAttempts` (3) consecutive wrong PINs the PIN is locked for
   `PinLockoutMinutes` (15)**, in every environment. A correct PIN resets the counter, and an
   expired lock starts a fresh window at attempt 1.
3. **One choke point**: a narrow `IPinVerifier`, implemented by `PinService`, is the only code
   that checks a PIN, because every PIN gate then shares one limiter and none can be bypassed.
   `POST /api/auth/pin/verify`, the current PIN of a PIN change (ADR-0040) and the mint of an
   authorisation (ADR-0042) all go through it.
4. **The lockout state is isolated from the caller's transaction**: `PinService` reads and writes
   it in its own `DbContext` scope (`IServiceScopeFactory`), never the request's, because the count
   is security bookkeeping that must persist when the caller fails or rolls back, and must not
   finalise the caller's pending idempotency record (ADR-0009).
5. **The wrong-PIN transition is one atomic set-based `ExecuteUpdate`**, increment and
   threshold-lock together, because a parallel burst then loses no update and a late increment can
   never clear a lock that was just applied.
6. **While locked, and on the attempt that crosses the threshold, the answer is HTTP 429** with a
   standard `Retry-After` header (RFC 9110 §10.2.3), `errorCode` `PIN_LOCKED`, and
   `retryAfterSeconds` and `lockedUntil` in the problem details (`PinLockedException`). A wrong PIN
   under the threshold keeps the contract of the endpoint that asked: `/pin/verify` answers 200
   `{verified:false}`, a mint 401 `INVALID_PIN`.

## Rejected

- Rejected: Identity's `AccessFailedCount` and `LockoutEnd` for the PIN as well, because a wrong
  PIN would lock password login and a wrong password the PIN.
- Rejected: BFF-session-level limiting only, because
  the API stays open to a direct (non-BFF) JWT caller.
- Rejected: 423 for the locked answer, because it is WebDAV-specific and intermediaries downgrade
  it to 400.

## Consequences

- Online PIN guessing is bounded at 3 tries per 15 minutes on every path that checks a PIN.
- The lockout state is independent of the business transaction: a wrong PIN is counted even when
  the caller's own work rolls back, and no idempotency key is corrupted by it.
- The PIN lockout and the password lockout (ADR-0012) are fully separate.
- A locked PIN is refused before Argon2id runs, so the lock does not leak through verify latency.
- It costs one small write per failed attempt, in its own scope, and a 429 with `Retry-After` on
  every endpoint that checks a PIN, declared in the published document.
- The BFF holds no attempt limit. Its `SecurityOptions` keeps `PinValidityMinutes` alone; a
  `MaxPinAttempts` or `LockoutMinutes` key left in a local configuration file binds to nothing and
  is ignored.
- Not covered: replacing the PIN. The limiter counts guesses, and a PIN set with no proof of the
  current one is not a guess; ADR-0040 closes that by requiring the current credential.

## Verified by

- `PinServiceTests`: the increment, the lock on the crossing attempt, no hashing while locked.
- `PinLockoutConcurrencySqlServerTests`: parallel wrong PINs always end locked (decision 5).
- `AuthEndpointTests.VerifyPin_AfterMaxWrongAttempts_Returns429PinLocked` (decision 6).

## Related

ADR-0003, ADR-0009, ADR-0012, ADR-0040, ADR-0042.
