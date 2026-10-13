# ADR-0003: Argon2id Password Hashing

**Status:** Accepted · **Date:** 2026-01-12 · **Amended:** 2026-09-11 (the correction: Argon2id is
built for PINs, passwords use Identity's PBKDF2), 2026-09-17 (one profile left), 2026-10-13
(decisions 2 and 3: passwords at 220,000 iterations) · **Decision Makers:** Vladislav Aleshaev

## Context

A stored secret must be hashed with an algorithm that resists brute force and GPU or ASIC
acceleration, is memory-hard, is recommended by OWASP, and still verifies in under 500 ms. The
title names account passwords; what is built is narrower: every password goes through ASP.NET
Core Identity's PBKDF2, and Argon2id hashes the six-digit PIN. That is where it matters most: six
digits are exhausted instantly, so the PIN is also peppered (ADR-0011).

## Decision

1. **Argon2id hashes the PIN and nothing else, with one profile: 19 MiB of memory (`m=19456`), 2
   iterations, parallelism 4, a 16-byte salt and a 32-byte hash**, through
   `Konscious.Security.Cryptography.Argon2` 1.3.1, because Argon2id is memory-hard, which makes GPU
   attacks expensive, combines Argon2i's side-channel resistance with Argon2d's GPU resistance, and
   is OWASP's first choice. OWASP's minimum for it is 19 MiB, 2 iterations and 1 degree of
   parallelism (Password Storage Cheat Sheet); RFC 9106 specifies the function.
2. **Account passwords are hashed by ASP.NET Core Identity's PBKDF2 with HMAC-SHA512 at 220,000
   iterations, the Identity V3 format**, because that is the count OWASP gives for this function
   (Password Storage Cheat Sheet, as of 2026-09-11). `AccountPasswordHasher` holds the count, and
   the API and the Seeder both register it, so the two cannot differ. Measured on the running API:
   a registered `PasswordHash` starts `AQAAAAIAA1tg`, whose header decodes to format `0x01` (V3),
   PRF `2` (HMACSHA512) and `220000` iterations. On a seeded store every `PinHash` starts
   `$argon2id$v=19$`.
3. **A hash made at a lower count still signs in, is stored again at 220,000 when it does, and
   costs 220,000 iterations to verify until then.** Identity verifies a hash at the count it
   carries. Once the password is right, the request stores the new hash with one statement that
   sets the hash and the row's update time, and only over the hash it verified: Identity's own
   rehash writes the whole user row as the request loaded it, and would put back a lock, a count of
   wrong attempts or a newer password stored since. `AccountPasswordHasher` spends the iterations a
   stored hash lacks on every verification, because a cheaper check for exactly the accounts that
   have not signed in since the count was raised would tell a guesser which emails exist
   (ADR-0012).

## Rejected

- Rejected: bcrypt, because it is not memory-hard and its GPU resistance is moderate.
- Rejected: scrypt, because it is memory-hard but OWASP ranks it below Argon2id.
- Rejected: PBKDF2 for the PIN, because it is not memory-hard and is vulnerable to GPU
  acceleration.

## Consequences

- `PasswordHasher` hashes PINs only; account passwords never reach it.
- It costs an external NuGet package, more memory for each hash, and a hash slower than PBKDF2,
  which is intended. The parameters can be raised later.
- Raising the count again is one constant, `AccountPasswordHasher.IterationCount`, and no
  migration: each stored hash follows at its user's next sign-in.
- Not covered: PBKDF2 is not memory-hard, and a user who never signs in again keeps a hash at
  the count it was made with.

## Verified by

- `PasswordHasherTests` and `PasswordHasherPepperTests`: the PIN hash, its format and its pepper.
- `AccountPasswordHasherTests`, `PasswordRoundsTests`: the count, and equal iterations to verify.
- `PasswordRoundsSqlServerTests`, `PasswordRehashWriteSqlServerTests`: the new hash and its write.

## Related

ADR-0001, ADR-0011, ADR-0012.
