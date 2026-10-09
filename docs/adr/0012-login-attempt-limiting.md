# ADR-0012: Password/login attempt-limiting (account lockout)

**Status:** Accepted · **Date:** 2026-07-15 · **Amended:** 2026-09-23 (decision 4) ·
**Decision Makers:** Vladislav Aleshaev

## Context

`AuthService.LoginAsync` verifies the password with `UserManager.CheckPasswordAsync`, which only
compares the hash: it never increments `AccessFailedCount`, never checks or sets `LockoutEnd` and
never consults `LockoutEnabled`. `IdentityOptions.Lockout` is configured (5 attempts, 15 minutes)
and, with nothing calling Identity's lockout API, inert: online password guessing is unbounded.
Login also has a property to keep: an unknown user and a wrong password are answered with the
identical `401 Invalid email or password.`

## Decision

1. **`AuthService.LoginAsync` counts failures and locks on Identity's native `AccessFailedCount`
   and `LockoutEnd`**, because they are already in the schema: no new column, no migration. The
   PIN lockout (ADR-0010) stays a separate mechanism, on its own columns.
2. **Each write is one atomic statement**: increment-and-maybe-lock, and reset on success, run as
   a set-based `ExecuteUpdateAsync` that evaluates the threshold against the row's current value,
   because a read-modify-write loses updates under a parallel burst. The increment carries
   `WHERE (LockoutEnd IS NULL OR LockoutEnd < now)`, so an attempt that lands after a peer has
   latched the lock updates zero rows and leaves no residual count: the next window gets a full
   budget. The writers bypass Identity's `ConcurrencyStamp` on purpose, are followed by no Identity
   save on the same tracked instance, and bump `UpdatedAt` themselves, because SaveChanges
   interceptors do not run for `ExecuteUpdate`. They run on the request's own `DbContext`: login has
   no idempotency or money transaction to isolate, unlike the PIN path, which has the same guard.
3. **The lock is shown only to a caller who has proved the password**, because any other answer
   makes the lock an oracle for account existence. An unknown user or a wrong password is 401
   `INVALID_CREDENTIALS`, `Invalid email or password.`, identical in every case, a locked account
   included, and a wrong password does not extend a lock. The correct password on a locked account
   is 429 `ACCOUNT_LOCKED` with `Retry-After`, `retryAfterSeconds` and `lockedUntil` in the details
   (`AccountLockedException`, the shape of `PinLockedException`), and no token.
4. **A token is returned with its exact expiry**: `IJwtService.GenerateToken` gives `ExpiresAt` as
   read back from the token's `exp`, because a lifetime computed again can drift from the token.
   `ExpiresIn` is what is left of it when the one `TokenResponse` is built, in whole seconds.
5. **The policy is `ValidationRules.MaxLoginAttempts` (5) and `LoginLockoutMinutes` (15)**, the
   same constants `IdentityOptions.Lockout` is configured from, so the two cannot differ.
6. **An unknown email spends the cost of a real password check**: `ILoginTimingEqualizer` runs a
   dummy verification through the request's own `IPasswordHasher<ApplicationUser>` (PBKDF2, so the
   real verifier's cost), because the hash latency is otherwise a larger oracle than the body.
7. **The lockout honours Identity's per-user `LockoutEnabled` flag**, as `IsLockedOutAsync` does:
   an exempt account is never treated as locked and accrues no lock state. Every registered user
   has it `true` through `AllowedForNewUsers`; the PIN lockout is not governed by it.

## Rejected

- Rejected: `SignInManager.CheckPasswordSignInAsync(lockoutOnFailure: true)`, because this is a
  JWT-only API and `SignInManager` pulls in cookie schemes, it uses Identity's read-modify-write,
  and it evaluates the lockout before the password, which leaks that the account exists.
- Rejected: `UserManager.AccessFailedAsync` and `IsLockedOutAsync`, because the count is a
  read-modify-write under the `ConcurrencyStamp`, and EF's `UserStore` swallows the conflict into
  `IdentityResult.ConcurrencyFailure`: a silent lost update, so a parallel burst may never lock.
- Rejected: 429 `ACCOUNT_LOCKED` whenever the account is locked, because it tells a guesser that
  the email exists and is locked.
- Rejected: the generic 401 always, even when locked, because the holder of the correct password
  is then told the credentials are invalid and gets no signal to wait.

## Consequences

- Online password guessing is bounded at 5 attempts per 15 minutes, with no schema change, and a
  guesser sees the same 401 whether the account exists, does not, or is locked.
- Not covered: whoever knows a victim's email can lock that account with wrong passwords. It is
  intrinsic to any lockout, and mitigated by the per-IP limiter at the BFF's edge (ADR-0013).
- Not covered: a locked account still runs one password hash per attempt, which is what keeps the
  timing of a wrong password uniform. The same limiter bounds it.
- Not covered: a wrong password on an existing, unlocked account performs one database write, the
  increment, that the unknown-user path does not, so a fine timing analysis can still separate the
  two. The signal is much weaker than the hash oracle; no response-time floor is set.

## Verified by

- `AuthServiceTests` (the region "Login lockout Tests") and `LoginTimingEqualizerTests`.
- `AuthEndpointTests.Login_AfterTooManyWrongPasswords_LocksAccount_Returns429`.
- `LoginLockoutConcurrencySqlServerTests`: a parallel burst locks, and leaves no residual count.

## Related

ADR-0003, ADR-0010, ADR-0013.
