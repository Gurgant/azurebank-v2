# ADR-0011: PIN-hash pepper (keyed hashing) with self-describing rehash-on-use

**Status**: Accepted

**Date**: 2026-07-15

**Decision Makers**: Vladislav Aleshaev

---

## Context

Withdrawals require a step-up **6-digit PIN**, stored as an Argon2id hash
(ADR-0003) and rate-limited on the wire (ADR-0010). A 6-digit PIN has only
**~10^6** possible values. Argon2id makes each guess expensive, but if the
**database is stolen** an attacker can brute-force a `PinHash` **offline**
(10^6 candidates × ~50 ms, and fully parallelizable) — the online lockout of
ADR-0010 does nothing against an offline attack.

A **pepper** — a high-entropy secret kept **outside** the database — mixed into
the PIN hash defeats this: the dump alone is no longer enough, because the
attacker also needs the pepper, which is not in it. OWASP and NIST recommend a
pepper precisely for low-entropy secrets like PINs.

> This is a different secret from the idempotency `Idempotency:HashKey`
> (ADR-0009), which keys the HMAC **request fingerprint** — not the stored PIN
> hash. Here the **PIN hash** itself is peppered.

Account **passwords are out of scope**: they are handled by ASP.NET Core Identity
with its own key-derivation, have higher entropy, and are not verified through
this hasher (the password profile has no non-test call site).

## Decision Drivers

- Make offline brute-force of a stolen `PinHash` infeasible (defense-in-depth
  against DB theft).
- **No forced PIN reset** for existing users — migrate transparently.
- Must not weaken idempotency (ADR-0009) or the PIN lockout (ADR-0010).
- Keep the account-password path untouched.
- Be rotation-ready (a pepper can be changed without a flag day).

## Considered Options

**Mechanism**
1. **Argon2id secret value `K` (chosen)** — supply the pepper as the RFC 9106
   secret parameter via Konscious `KnownSecret`. One standard primitive; the
   secret never appears in the stored hash.
2. HMAC pre-hash — `Argon2id(HMAC-SHA256(pin, pepper))`. Provider-agnostic but
   more moving parts; rejected because the library supports (1) cleanly.

**Migration of existing (un-peppered) hashes**
1. **Self-describing versioned hash + rehash-on-use (chosen)** — tag new PIN
   hashes with a `keyid`; verify legacy hashes without a pepper and re-hash them
   to the active pepper on the next successful verify. Zero downtime, progressive.
2. Forced reset — invalidate all PINs; simple but user-hostile.
3. Re-seed only — acceptable for the demo data set, but not a real migration
   story; kept only as the demo shortcut.

## Decision

- **Pepper mechanism**: the PIN pepper is mixed in as the Argon2id secret value
  `K` (Konscious `KnownSecret`), for the **PIN profile only**. Because `K` leaves
  **no trace** in the stored PHC string, a peppered and an un-peppered hash are
  otherwise byte-identical — so the hash must **self-describe** whether it was
  peppered (next point).
- **Self-describing versioned hash**: new PIN hashes carry a `keyid=N` field in
  the PHC parameter block
  (`$argon2id$v=19$m=..,t=..,p=..,keyid=N$salt$hash`). Verification is driven by
  the stored hash: a hash **with** a `keyid` is verified **with** the matching
  pepper (and a `keyid` the ring does not hold **fails closed**); a hash **without** one
  is a **legacy** hash verified **without** a pepper. Still six `$`-segments, so
  the format stays backward-compatible.
- **Pepper keyring (rotation)**: verification resolves the pepper by the hash's
  `keyid` from a **keyring** — the **active** pepper (`Security:PinPepper` /
  `PinPepperKeyId`) plus a map of **retained** peppers
  (`Security:PreviousPinPeppers` = `keyid → pepper`). `HashPin` always stamps the
  active id; `ResolvePepper` looks the incoming `keyid` up in the ring and **fails
  closed** for a key it does not hold. This is the JWT-`kid` / ASP.NET Data
  Protection key-ring pattern: one key for writes, all non-retired keys kept for
  reads. A single-pepper config (no `PreviousPinPeppers`) is the common case and
  needs no ring.
- **Config & fail-fast**: the peppers are `Security:*` secrets, ≥ 32 chars each,
  in **user-secrets** (dev) / **Key Vault** (prod), **never** committed and
  **never** stored in the DB. Keep the **whole ring** (active + previous) in a
  **single** secret provider — configuration merges dictionaries across providers,
  so a previous pepper left in `appsettings.json` could never be removed and would
  leak a live secret. One shared `PinHashingOptionsValidator` (used by the API and
  Seeder) enforces: each pepper ≥ 32, key ids ≥ 1, the active id absent from the
  retained map, and all pepper values distinct. The API validates with
  `ValidateOnStart`; the **Seeder** (a CLI that never starts the host, so
  `ValidateOnStart` alone would not fire) invokes the startup validator explicitly
  in `Program.cs` so **both `seed` and `reset` fail fast before any DB work**. The
  Seeder shares the same active pepper (else seeded PINs cannot be verified).
- **Rehash-on-use migration**: `IPasswordHasher.PinNeedsRehash` reports whether a
  stored hash predates the active key. On a **successful** verify, `PinService`
  re-hashes the PIN with the active pepper and persists it — in the service's
  **own DbContext scope**, consistent with the lockout isolation of ADR-0010.
- **Rotation procedure** (`add → activate → drain → retire`): (1) **add** the new
  pepper to `PreviousPinPeppers` on every node so all nodes can verify it; (2)
  **activate** it — promote it to `PinPepper` / `PinPepperKeyId`; the old pepper
  moves into `PreviousPinPeppers`; (3) **drain** — old-keyid hashes verify with the
  retained pepper and self-upgrade to the active key on next use; (4) **retire** —
  once no stored hash still carries the old key id, drop it from the ring. This is
  true zero-downtime rotation. If a `keyid` the keyring no longer holds is seen at
  verify time, `PinService` logs a distinct operator diagnostic (it is otherwise
  indistinguishable from a wrong PIN and would accrue lockout).

## Consequences

### Positive
- A stolen `PinHash` is useless without the pepper — offline brute-force of the
  10^6 PIN space is defeated.
- Progressive, zero-downtime migration; no forced PIN reset.
- **Genuinely** rotation-ready: retained peppers keep old hashes verifying while
  they drain to the active key (not just a version marker).

### Negative
- A **new required secret** (`Security:PinPepper`): the API and the Seeder both
  fail fast without it. Documented in the README and the example config.
- Rotation drains **on use**: dormant accounts keep an old `keyid` until they next
  authenticate, so a retired pepper must stay in the ring until its rows reach
  zero. To retire a **compromised** pepper *instantly* (without waiting for logins)
  one would need the **encrypt-the-hash** variant (store `AEAD_pepper(argon2(pin))`
  so a background job can re-wrap every row offline) — out of scope here, recorded
  as the known alternative.

### Neutral
- The account-password path is unchanged (never peppered).
- No schema change: the pepper key id lives only in the hash string's parameters.

## Validation

- **Unit** (`PasswordHasherPepperTests`): peppered hash carries `keyid` and
  round-trips; a **wrong pepper** fails; a peppered hash **cannot** be verified by
  a hasher that holds no pepper (**fail closed**); a legacy hash still verifies;
  `PinNeedsRehash` is true for legacy/older keys and false for the active key; a
  full legacy → peppered upgrade re-verifies. **Rotation**: a hash minted under
  pepper A / keyid 1 still verifies under a rotated hasher whose active is B / keyid
  2 and whose ring retains A, then rehash-on-use drains it to keyid 2; dropping A
  before draining **fails closed** (documenting retire-after-drain). Password hashes
  are never peppered; an absurd cost parameter is rejected.
- **Unit** (`PinHashingOptionsValidatorTests`): the keyring rules — length,
  key-id, active-not-in-retained, distinct values — accept valid rings and reject
  each violation.
- **Unit** (`PinServiceTests`): a correct PIN on a legacy hash triggers exactly
  one re-hash and persists; a current hash triggers none; an **unresolvable
  keyid** counts as a failed attempt and can lock (with the operator diagnostic).
- **SQL Server** (`PinPepperMigrationSqlServerTests`): a real legacy row verifies
  and is upgraded **in place** to a peppered hash via the relational
  `ExecuteUpdate` path, then keeps verifying.
- **Integration**: set-PIN → verify / withdraw exercise the peppered round trip
  end-to-end through the booted API (the test host supplies a test pepper).

## Related

- ADR-0003 (Argon2id) — the PIN hashing this hardens.
- ADR-0010 (PIN lockout) — the online counterpart; shares the own-scope
  persistence model used for rehash-on-use.
- ADR-0009 (idempotency) — `Idempotency:HashKey` is a **different** secret
  (request fingerprint, not the PIN hash).
