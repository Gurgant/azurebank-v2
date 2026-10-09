# ADR-0011: PIN-hash pepper (keyed hashing) with self-describing rehash-on-use

**Status:** Accepted · **Date:** 2026-07-15 · **Amended:** 2026-09-17, 2026-10-01 (ADR-0060),
2026-10-03 (ADR-0062) · **Decision Makers:** Vladislav Aleshaev

## Context

The step-up PIN has six digits. It is stored as an Argon2id hash (ADR-0003) and rate-limited on the
wire (ADR-0010), but six digits are only about 10^6 values: from a stolen database a `PinHash` is
brute-forced offline (10^6 candidates at about 50 ms each, in parallel), and the online lockout
does nothing against that. A pepper, a high-entropy secret kept outside the database and mixed into
the hash, makes the dump alone insufficient; OWASP and NIST recommend one for low-entropy secrets.
Existing users must not be forced to reset their PIN, and the pepper must be changeable without a
flag day. Account passwords are out of scope: ASP.NET Core Identity hashes them, not this hasher.

## Decision

1. **The pepper is the Argon2id secret value `K`** (the RFC 9106 secret parameter, Konscious
   `KnownSecret`), for PIN hashes only, because it is one standard primitive and the secret never
   appears in the stored hash. It is a different secret from `Idempotency:HashKey` (ADR-0009).
2. **A PIN hash describes itself**: a new one carries `keyid=N` in the PHC parameter block
   (`$argon2id$v=19$m=..,t=..,p=..,keyid=N$salt$hash`), because `K` leaves no trace in the string
   and a peppered and an un-peppered hash are otherwise byte-identical. A hash with a `keyid` is
   verified with the matching pepper; one without is a legacy hash, verified without a pepper.
3. **Peppers are held in a keyring**: the active one (`Security:PinPepper`, `PinPepperKeyId`) and a
   map of retained ones (`Security:PreviousPinPeppers`, key id to pepper), because one key for
   writes and every non-retired key for reads lets a pepper change with no flag day (the JWT `kid`
   and ASP.NET Data Protection pattern). `HashPin` stamps the active id; `ResolvePepper` looks the
   hash's `keyid` up in the ring and fails closed for a key it does not hold.
4. **The peppers are `Security:*` secrets, never committed and never in the database, and the ring
   is validated before any work.** The whole ring sits in a single secret provider, because
   configuration merges dictionaries across providers: a previous pepper left in `appsettings.json`
   could never be removed. One `PinHashingOptionsValidator`, shared by the API and the Seeder,
   requires 32 characters or more per pepper, key ids of 1 or more, the active id absent from the
   retained map, and distinct values. It refuses a `PreviousPinPeppers` key that is not a whole
   number, has surrounding whitespace, names an id another key names (`1` and `01`) or does not hold
   exactly one value, because the binder silently drops what it cannot convert; the refusal names
   the key, or only its length when it could be a pepper, and never a value. The API validates with
   `ValidateOnStart`. The Seeder never starts the host, so `seed`, `reset`, `seed-pool` and
   `recycle` validate at their start and exit 2 before any connection is opened; `--help` and
   `migrate` need no pepper. Seeded PINs verify only if the Seeder holds the API's active pepper.
5. **Rehash on use**: `IPasswordHasher.PinNeedsRehash` reports a hash that predates the active key,
   and on a successful verify `PinService` re-hashes the PIN with the active pepper and persists it
   in its own DbContext scope (ADR-0010), because existing hashes then migrate with no forced reset
   and no downtime.
6. **Rotation is add → activate → drain → retire**: add the new pepper to `PreviousPinPeppers` on
   every node, so that all can verify it; activate it as `PinPepper` / `PinPepperKeyId`, the old one
   moving into `PreviousPinPeppers`; drain, as old hashes verify with the retained pepper and
   upgrade on next use; retire the old pepper once no stored hash carries its key id. A `keyid` the
   ring no longer holds is logged by `PinService` as a distinct diagnostic, because it is otherwise
   indistinguishable from a wrong PIN and accrues lockout.

## Rejected

- Rejected: an HMAC pre-hash, `Argon2id(HMAC-SHA256(pin, pepper))`, because it has more moving parts
  and the library supports the secret parameter cleanly.
- Rejected: a forced reset of every PIN, because it is hostile to users.
- Rejected: re-seeding only, because it is no migration for real data. It stays the demo shortcut.
- Rejected, as out of scope: encrypting the hash (`AEAD_pepper(argon2(pin))`) so that a background
  job can re-wrap every row offline, the known way to retire a pepper instantly.

## Consequences

- A stolen `PinHash` is useless without the pepper: offline brute force of the 10^6 space fails.
- No schema change: the key id lives in the hash string, which keeps its six `$`-segments.
- It costs a required secret: the API and the Seeder refuse to work without `Security:PinPepper`.
- The job that runs `recycle` holds the API's pepper and key id, and rotates in the same order, on
  the count `docs/runbooks/demo-pool.md` reads.
- Not covered: rotation drains on use. A dormant account keeps its old `keyid` until its next
  correct PIN, so a retired pepper stays in the ring until its rows reach zero, and a compromised
  pepper cannot be retired instantly.

## Verified by

- `PasswordHasherPepperTests`, `PinHashingOptionsValidatorTests`, `PinServiceTests`.
- `PinPepperMigrationSqlServerTests`: a legacy row verifies and is upgraded in place on SQL Server.

## Related

ADR-0003, ADR-0009, ADR-0010, ADR-0060, ADR-0062.
