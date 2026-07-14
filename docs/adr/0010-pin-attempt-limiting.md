# ADR-0010: API-side PIN attempt-limiting (lockout)

**Status**: Accepted

**Date**: 2026-07-14

**Decision Makers**: Development team (R2 follow-up), read-only discovery of the auth/PIN surface

---

## Context

Withdrawals require a step-up **6-digit PIN** (Argon2id-hashed, ADR-0003). The
PIN is verified at two independent server-side sites — `POST /api/auth/pin/verify`
and `POST /api/transactions/withdraw` — with **no attempt limit**. Anyone holding
a valid JWT could brute-force the 10^6 PIN space online.

An honest finding from discovery: the password/login lockout is **configured but
dormant** — `IdentityOptions.Lockout` is set (5 attempts / 15 min) but
`AuthService.LoginAsync` uses `UserManager.CheckPasswordAsync`, which never
increments `AccessFailedCount` or checks `LockoutEnd`. So there was **no working
lockout to mirror**; this ADR builds the first DB-backed one, matching the
intent already declared (but unused) in the BFF `SecurityOptions`
(`MaxPinAttempts = 3`, `LockoutMinutes = 15`).

## Decision Drivers

- Bound the online PIN brute-force space (defense-in-depth; assumes a stolen JWT).
- Cover **both** PIN-verification paths — a limiter on only one leaves the other
  brute-forceable.
- Do not conflate PIN lockout with password lockout.
- Must not weaken the idempotency guarantees (ADR-0009).

## Considered Options

1. **Reuse Identity's `AccessFailedCount` / `LockoutEnd`** — rejected: those are
   password-scoped; a wrong PIN would lock password login and vice-versa.
2. **Dedicated PIN columns + a shared verifier** (chosen).
3. BFF-session-level limiting only — rejected: the API stays open to a direct
   (non-BFF) JWT caller.

## Decision

- **Two dedicated columns** on `ApplicationUser`: `PinAccessFailedCount` (int)
  and `PinLockoutEnd` (`DateTimeOffset?`) — migration `AddPinLockout`.
- **Policy**: after **`ValidationRules.MaxPinAttempts` (3)** consecutive wrong
  PINs, lock for **`PinLockoutMinutes` (15)**. A correct PIN resets the counter;
  an expired lock starts a fresh window at attempt #1.
- **One choke point**: a narrow **`IPinVerifier`** (ISP) implemented by
  **`PinService`**. `AuthService.VerifyPinAsync` delegates to it, and
  `TransactionService.WithdrawAsync` depends on it — so both PIN gates share the
  same limiter and neither can be bypassed.
- **Isolation from the caller's transaction (crucial for ADR-0009)**: the lockout
  counter is security bookkeeping that must persist even when the caller fails
  or rolls back, and must **not** finalize the caller's pending idempotency
  record. So `PinService` reads and writes the lockout state in **its own
  `DbContext` scope** (`IServiceScopeFactory`), never the request's context. A
  wrong PIN during a withdrawal therefore records the failed attempt **without**
  committing the withdrawal's pending idempotency flip — the key is released and
  stays reusable, exactly as ADR-0009 intends.
- **Wire contract**: while locked (or on the attempt that crosses the threshold),
  the verifier throws `PinLockedException` → **HTTP 423 Locked**, `errorCode
  PIN_LOCKED`, with `retryAfterSeconds` + `lockedUntil` in the ProblemDetails
  `Details`. A wrong PIN under the threshold keeps its existing contract
  (`/pin/verify` → 200 `{verified:false}`; withdraw → 401 `INVALID_PIN`).

## Consequences

### Positive
- The online PIN brute-force is bounded (3 tries per 15 min) on **both** paths.
- Lockout state is independent of the business transaction — correct even for a
  wrong PIN on a rolled-back withdrawal, with no idempotency-key corruption.
- PIN and password lockouts are fully separate.

### Negative
- One extra small write per failed attempt (in its own scope).
- 423 is a new status on `/pin/verify` and `/withdraw` (documented in the spec).

### Neutral
- New migration `AddPinLockout` (two nullable/defaulted columns; no data change).
- A locked account is refused **before** Argon2id runs, so lock state does not
  leak via verify latency.

## Validation

- **Unit** (`PinServiceTests`): increment on wrong PIN, lock + throw on the
  crossing attempt, refuse-while-locked without hashing, reset on success,
  expired-lock fresh window.
- **Integration**: `/pin/verify` locks after the threshold and refuses even the
  correct PIN (423 `PIN_LOCKED`); a locked-PIN withdrawal returns 423 and
  **creates no transaction / moves no money**. Green on InMemory and SQL Server.

## Related

- ADR-0003 (Argon2id) — the PIN hashing this protects.
- ADR-0009 (idempotency) — the reason lockout state is persisted in its own scope.
- Follow-up (out of scope): wiring the dormant **password/login** lockout
  (`SignInManager.CheckPasswordSignInAsync(lockoutOnFailure: true)`).
