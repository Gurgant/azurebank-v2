# ADR-0012: Password/login attempt-limiting (account lockout)

**Status:** Accepted · **Date:** 2026-07-15 · **Amended:** 2026-09-23 (decision 4), 2026-10-13
(decision 6), 2026-10-16 (decisions 2 and 3) · **Decision Makers:** Vladislav Aleshaev

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
2. **An attempt is counted before its password is compared, in one atomic statement**, as the
   PIN's is (ADR-0010). Increment-and-maybe-lock runs as a set-based `ExecuteUpdateAsync` that
   evaluates the threshold against the row's current value, because a read-modify-write loses
   updates under a parallel burst, and only an attempt it counted has its password compared. It
   carries `WHERE (LockoutEnd IS NULL OR LockoutEnd < now)`: the fifth attempt of a window is
   counted and latches the lock, and an attempt that lands after that updates zero rows, is refused
   without its password being read and leaves no residual count, so the next window gets a full
   budget. A right password clears the count and any lock latched while it was compared. The
   writers bypass Identity's `ConcurrencyStamp` on purpose and bump `UpdatedAt` themselves, because
   SaveChanges interceptors do not run for `ExecuteUpdate`. No Identity save of the same tracked
   instance follows them, but one: at a first PIN's password proof the new PIN is saved through
   Identity next, so the reset is first copied onto the instance that save writes. They run on the
   request's own `DbContext`: login has no idempotency or money transaction to isolate, unlike the
   PIN path, which has the same guard.
3. **While an account is locked, the answer does not depend on the password**, because an answer
   that did would tell a guesser which password is right. An unknown user, a wrong password and a
   locked account, whatever password it is sent, are all 401 `INVALID_CREDENTIALS` with one text:
   `Invalid email or password. After 5 failed attempts, sign-in is paused for 15 minutes.` So the
   answer is no oracle for the account's existence, for its lock or for its password, and a wrong
   password does not extend a lock. A locked account's password is not compared: the equaliser of
   decision 6 spends the cost of a comparison instead. The second sentence of the text is how the
   holder of the right password learns that waiting is the way in. Behind a session, where the
   account is already known (the password proof of a first PIN, ADR-0040), a lock is answered 429
   `ACCOUNT_LOCKED` with `Retry-After`, `retryAfterSeconds` and `lockedUntil`, for every password and
   without reading it, as a locked PIN is (ADR-0010).
4. **A token is returned with its exact expiry**: `IJwtService.GenerateToken` gives `ExpiresAt` as
   read back from the token's `exp`, because a lifetime computed again can drift from the token.
   `ExpiresIn` is what is left of it when the one `TokenResponse` is built, in whole seconds.
5. **The policy is `ValidationRules.MaxLoginAttempts` (5) and `LoginLockoutMinutes` (15)**, the
   same constants `IdentityOptions.Lockout` is configured from, so the two cannot differ.
6. **An unknown email spends the cost of a real password check**: `ILoginTimingEqualizer` runs a
   dummy verification through the request's own hasher, which spends the same PBKDF2 iterations on
   any stored hash (ADR-0003), because the hash latency is otherwise a larger oracle than the body.
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
- Rejected: 429 `ACCOUNT_LOCKED` at sign-in whenever the account is locked, because it tells a
  guesser that the email exists and is locked.
- Rejected: 429 `ACCOUNT_LOCKED` at sign-in for the right password during a lock, because the answer
  then tells a guesser which password is right. Measured on SQL Server: after five wrong passwords
  the right one was answered 429 and a wrong one 401, so the lock bounded nothing.
- Rejected: counting an attempt after its comparison, because requests sent together are then all
  compared before any count lands. Measured on SQL Server: twenty wrong passwords sent together were
  all compared, and with the right one sent among them it signed in, in five rounds of five.

## Consequences

- Online password guessing is bounded at 5 compared passwords per 15 minutes, requests sent
  together included, with no schema change, and a guesser sees the same 401 whether the account
  exists, does not, or is locked, whatever password was sent.
- A counted attempt whose request then dies stays counted: the limiter fails closed.
- Every sign-in on a lockout-enabled account writes its row twice: the claim, and on a right
  password the clearing. A right password clears too what wrong passwords counted while it was
  compared, so a sign-in that succeeds starts a new window.
- Sign-ins of one account sent at the same moment are compared five at most: a sixth, the right
  password included, meets the lock the fifth latched and is refused, until a right password among
  the five has been compared and has cleared it.
- Not covered: whoever knows a victim's email can lock that account with wrong passwords. It is
  intrinsic to any lockout, and mitigated by the per-IP limiter at the BFF's edge (ADR-0013).
- Not covered: the holder of the right password is not told that the account is locked, nor how
  long is left; the text says how long a pause lasts.
- Not covered: the time taken still separates three paths by their database work, though not by
  the hash, which all three spend. An attempt on an unlocked account writes the claim; on a locked
  account the claim matches no row and writes nothing; an unknown user runs no claim at all. A fine
  timing analysis can still tell them apart. The signal is much weaker than the hash oracle; no
  response-time floor is set.

## Verified by

- `AuthServiceTests` ("Login lockout Tests"), `LoginTimingEqualizerTests`, `PasswordRoundsTests`.
- `AuthEndpointTests.Login_AfterTooManyWrongPasswords_LocksAccount_AndAnswersTheRightPasswordAlike`.
- `LoginLockoutConcurrencySqlServerTests`: a parallel burst locks, and leaves no residual count.
- `LoginGuessBoundSqlServerTests`: a burst of wrong passwords has five compared, at sign-in and at a
  first PIN's password proof; a burst holding the right one signs in only when it was among the first
  five compared; and a locked account answers every password as it answers an email nobody has.

## Related

ADR-0003, ADR-0010, ADR-0013.
