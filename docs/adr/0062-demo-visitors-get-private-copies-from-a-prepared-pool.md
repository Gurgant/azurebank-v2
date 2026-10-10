# ADR-0062: Demo visitors get private copies from a prepared pool

**Status:** Accepted · **Date:** 2026-10-03 · **Amended:** 2026-10-03 (ADR-0061), 2026-10-04
(ADR-0063), 2026-10-05 (ADR-0064) · **Amends:** ADR-0011, ADR-0042, ADR-0044 (D6), ADR-0060
(decisions 4 and 5) · **Decision Makers:** Vladislav Aleshaev

## Context

The fixed demo is one set of users everybody shares: `seed` writes John Smith with two months of
history, the two people that history pays and an administrator, and their password and PIN are in
the committed settings, so anyone can sign in as anyone. A public demo needs a copy of the
visitor's own at once, no password anybody else knows, and an end to what a visitor leaves behind.
One user cannot be deleted alone: a transfer is a row on each side joined by a restricting key,
and `EnforceTransactionImmutability` refuses a tracked delete of a ledger row (ADR-0044 D6).

## Decision

1. **The copy is the unit**: the fixed demo, private to one visitor (John Smith, Jane Smith, Mike
   Brown and the 26 ledger rows between them, built by `DemoLedger` as `seed`'s are), is seeded,
   claimed, kept apart and deleted as one, because the transfers make one user alone undeletable.
2. **One table, `DemoCopies`, and one column, `AspNetUsers.DemoCopyId`, in one migration.** The
   column alone says which users are a copy's: no name pattern, no age. The row has no foreign key
   to its owner, because it outlives the user. It holds the claim's `ClaimId`, `ClientKey` (a
   keyed hash of the client's address) and `Writes` (the requests that could change something,
   refused ones and reveals included), which ADR-0063 decides, and `DeletedAt`, set on a record.
3. **Off unless a deployment turns it on**: `Demo:Enabled` is false by default, the builder and
   the recycler refuse in their own code to run without it, and `DemoOptionsValidator` checks
   every range in the Seeder, the API and the BFF, with the demo on or off, so that a bad value is
   refused the day it is written: `Demo:CopyLifetimeHours` 24 (1 to 168), `Demo:Pool:TargetFree`
   50 (1 to 500), `Demo:Pool:LowMark` 20 (0 to `TargetFree`), `Demo:Pool:MaxFreeAgeHours` 44 (1 to
   720), `Demo:Pool:MaxClaimsPerDay` 150 (`TargetFree` to 10,000),
   `Demo:Claim:MaxPerClientPerDay` 10 (1 to 1,000), `Demo:Copy:MaxWrites` 200 (10 to 100,000).
4. **A free copy has no password**: its users are created with none, so no hash exists for a
   sign-in to match, and the claim sets one (ADR-0063). Every user of a copy has the PIN `123456`
   (`DemoCopyDefaults.Pin`), because what keeps a copy private is its owner's sign-in.
5. **A copy's identifiers come from the cryptographic generator**: three handles with one suffix
   (`john_k7m2`, `jane_k7m2`, `mike_k7m2`) and a sign-in name of `demo-` plus 16 characters, 82
   bits, because a predictable one would hand a visitor the names of the copies seeded beside it.
6. **A copy is built whole or not at all**, in one transaction, and a unique violation draws the
   whole copy again, three times at most. A run tops the pool up to its target, never by it, so it
   can be run again at any time. It writes no audit row: the Seeder holds no audit chain key.
7. **A handle is resolved only inside the caller's copy**: the recipient lookup and the payee
   resolver compare `DemoCopyId`, so a handle another copy holds is answered as one nobody holds.
   No flag is read: outside the demo both sides are null and every user finds and pays the others.
8. **`recycle` tops up first, then deletes, then sweeps.** It tops the fresh free copies up to
   `min(TargetFree, max(0, MaxClaimsPerDay - claims in the last 24 h))`, first because free copies
   are what visitors wait for; deletes each claimed copy whose time is over; deletes the free copies
   older than `MaxFreeAgeHours`, all or none and only once the top-up reached its target, because an
   old copy is still one a visitor can be given; then sweeps expired idempotency records and grants.
   It takes a too-old free copy with the claim's own conditional `UPDATE`, so of a visitor and the
   run only one has it. Each copy has its own transaction, and each statement 120 s, because one
   that times out is not sent again. On a database with users and no pool row it writes nothing.
9. **A claimed copy is deleted when its time is over and no session is running in it**: five
   minutes after `ClaimedAt + CopyLifetimeHours`, unless a grant of one of its users is live or
   ended under five minutes ago. 48 hours past its lifetime it goes all the same, counted apart
   (`hardStop`), because a grant lives 24 hours at most.
10. **A copy is deleted by six statements in one transaction, around the ledger's guard**: its
    users' authorisations, idempotency records, ledger rows (one statement, so both halves of a
    transfer leave together), accounts and users, then its pool row, kept as a record if the copy
    was claimed (`DeletedAt` set, `ClientKey` removed). They are set-based, so the guard does not
    see them, on purpose: a demo copy's ledger is invented data, and no request reaches the Seeder.
11. **What stays: every audit row, and a record of the copy that is not proof.** The chain
    verifies after a delete as before, and a claimed copy's pool row stays to say that the actor
    its audit rows name was a demo copy. It is an operator's record: anyone who can write to the
    database can write or remove one. The evidence pack of a deleted copy's transfer is gone.
12. **A run ends with a code and one line.** `PoolExitCodes` reads the code from the run's counts:
    0 `PoolOk` (a run that found no copy, or only records, is a first fill); 10 `PoolLow`, fewer
    free copies than `LowMark`, however old, and at least one; 11 `PoolEmpty`, no free copy of any
    age and at least one copy that is not a record; 12 `TopUpIncomplete`, a copy could not be
    built and the pool ended below its target; 13 `ForeignUsers`, a user that belongs to no copy
    exists; 14 `DeleteFailed`, a copy's delete threw; 15 `ClaimCeiling`, the day's claims held the
    top-up below `TargetFree`. Several at once exit with the first of 13, 14, 12, 15, 11, 10. The
    line carries every count and never an id or an address ([runbook](../runbooks/demo-pool.md)).
13. **Two commands run the pool, and `seed` and `reset` stay out of it.** `seed-pool [copies]` is
    the top-up alone, without the daily ceiling (exit 0, 12 or 13); `recycle` is decision 8 whole,
    the job a deployment schedules. Both refuse a bad setting or the demo off before anything is
    opened (exit 2), and run on an Azure SQL name as the app's database user, never the
    migration's (ADR-0060, decision 5); the Azure job holds a copy of the app's PIN pepper, as key
    1 (ADR-0064). `seed` and `reset` refuse demo mode and, whatever the flag, a database that
    holds a pool row, because a job that lost the flag still points at the demo's database.

## Rejected

- Rejected: handles unique per copy, because a resolver that forgot the copy would pay a stranger.
- Rejected: choosing what to delete by age or name, because one day it matches a user of no copy.
- Rejected: deleting a claimed copy's record too, because its audit rows would then name nobody.
- Rejected: flagging a copy deleted, because the rows would stay and fill the database.
- Rejected: deleting row by row through the change tracker, because the ledger's guard refuses it.
- Rejected: deleting a copy's audit rows, because it breaks the chain (ADR-0044 D6).
- Rejected: deleting every copy to rotate the PIN pepper, because the pepper ring re-hashes on use.

## Consequences

- No copy's password is in the repository, and a failed delete costs one copy, never the run.
- Not covered: the audit rows of every deleted copy stay for ever, and `recycle` cannot give them
  back when storage runs short: the Azure database is 2 GB at most and no alert watches its size.
- Not covered: copies share the pool, the limits keyed on an address, the database and the audit
  chain's single tail, and a rename to a handle another copy holds answers 409 `AZURE_TAG_TAKEN`.
- Not covered: a pool of records only exits 0, although visitors may have been turned away.
- Not covered: a caller whose copy is gone is a user outside every copy until its token expires.
- Not covered: a run whose top-up came up short keeps every free copy too old to count, so a
  visitor can be given a copy whose history ends more than `MaxFreeAgeHours` ago.
- Not covered: with `Demo:CopyLifetimeHours` below 24 a copy and its client key can go inside the
  24 hours its claim is counted over, and that claim stops counting towards its client's cap.
- Not covered: two runs at once can build up to twice the target: no lock serialises them.

## Revisit when

- The job's deletes must be told from the app's, the job revoked alone, or a pool command needs a
  right the app's database user lacks: a database user of the job's own, never the migration's.
- A `hardStop` above 0: sign-in was accepted past a copy's end, and is fixed where it is decided.

## Verified by

- `DemoPoolSeedSqlServerTests`, `DemoPoolRecycleSqlServerTests`, `DemoPoolCommandSqlServerTests`,
  `DemoIsolationSqlServerTests`, `DemoCopySchemaSqlServerTests`, `DemoCopyBoundaryTests`.

## Related

ADR-0011, ADR-0037, ADR-0042, ADR-0044, ADR-0057, ADR-0060, ADR-0061, ADR-0063, ADR-0064.
