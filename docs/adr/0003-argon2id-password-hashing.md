# ADR-0003: Argon2id Password Hashing

**Status:** Accepted · **Date:** 2026-01-12 · **Amended:** 2026-09-11 (the correction: Argon2id is
built for PINs, passwords use Identity's PBKDF2), 2026-09-17 (one profile left) ·
**Decision Makers:** Vladislav Aleshaev

## Context

A stored secret must be hashed with an algorithm that resists brute force and GPU or ASIC
acceleration, is memory-hard, is recommended by OWASP, and still verifies in under 500 ms. The
title names account passwords; what is built is narrower: the API calls `AddIdentity` and
registers no custom `IPasswordHasher<ApplicationUser>`, so every password goes through Identity's
own hasher, and Argon2id hashes the six-digit PIN. That is where it matters most: six digits are
exhausted instantly, so the PIN is also peppered (ADR-0011).

## Decision

1. **Argon2id hashes the PIN and nothing else, with one profile: 19 MiB of memory (`m=19456`), 2
   iterations, parallelism 4, a 16-byte salt and a 32-byte hash**, through
   `Konscious.Security.Cryptography.Argon2` 1.3.1, because Argon2id is memory-hard, which makes GPU
   attacks expensive, combines Argon2i's side-channel resistance with Argon2d's GPU resistance, and
   is OWASP's first choice. OWASP's minimum for it is 19 MiB, 2 iterations and 1 degree of
   parallelism (Password Storage Cheat Sheet); RFC 9106 specifies the function.
2. **Account passwords are hashed by ASP.NET Core Identity's default: PBKDF2 with HMAC-SHA512 and
   100,000 iterations, the Identity V3 format**, because no custom password hasher is registered.
   Measured on a seeded store: every `PasswordHash` starts `AQAAAAIAAYag`, whose header decodes to
   format `0x01` (V3), PRF `2` (HMACSHA512) and `100000` iterations, and every `PinHash` starts
   `$argon2id$v=19$`.
3. **The password iteration count is not raised by this record**, because the login path's timing
   defence is calibrated to this hasher's cost (ADR-0012) and must move with it. Raising it is a
   setting, `PasswordHasherOptions.IterationCount`: login goes through
   `UserManager.CheckPasswordAsync`, which rehashes a stored hash weaker than the setting at the
   next successful sign-in.

## Rejected

- Rejected: bcrypt, because it is not memory-hard and its GPU resistance is moderate.
- Rejected: scrypt, because it is memory-hard but OWASP ranks it below Argon2id.
- Rejected: PBKDF2 for the PIN, because it is not memory-hard and is vulnerable to GPU
  acceleration.

## Consequences

- `PasswordHasher` hashes PINs only; account passwords never reach it.
- It costs an external NuGet package, more memory for each hash, and a hash slower than PBKDF2,
  which is intended. The parameters can be raised later.
- Not covered: password hashing is below OWASP's recommendation. 100,000 iterations of
  PBKDF2-HMAC-SHA512 stand against a recommended 220,000 (Password Storage Cheat Sheet, as of
  2026-09-11), and PBKDF2 is not memory-hard.

## Verified by

- `PasswordHasherTests` and `PasswordHasherPepperTests`: the PIN hash, its format and its pepper.

## Related

ADR-0001, ADR-0011, ADR-0012.
