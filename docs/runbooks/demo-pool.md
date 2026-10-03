# Runbook — the demo pool

**Symptom:** a run of `recycle` ended with a code from 10 to 15, or its line shows `hardStop` or
`failed` above 0, or it ended with no line at all, with exit 1 or 2. Or the demo's database is
filling up: on Azure it is Basic, 2 GB at most (`infra/main.bicep`), and none of the
deployment's four alerts watches its size (ADR-0061, decision 10).

**Why this runbook exists:** a run decides everything from the rows as they are, and says what it
found in one line and one exit code
([ADR-0062](../adr/0062-demo-visitors-get-private-copies-from-a-prepared-pool.md), decision 12).
The code names one signal; the line carries every count. This page says what each one means, and
gives the SQL that looks behind it.

**How a run is started.** The pool's job is the tools image with the argument `recycle`; a first
fill, or a refill by hand, is `seed-pool`, or `seed-pool <N>` to top up to N free copies. Each
needs `Demo__Enabled=true`, the connection string, and the API's `Security__PinPepper` and
`Security__PinPepperKeyId`. Where a deployment gives the app and its migration database users of
their own, it signs in as the app's, never the migration's (ADR-0060, decision 5's note). On the
Azure deployment (ADR-0061), which has no pool job yet, that job is to carry the identity
`azurebank-app` and sign in as `azurebank_app`, never with `azurebank-migrate`; ADR-0062,
decision 13, says what else adding it changes. `backend/tools/AzureBank.Seeder/README.md` has
every variable and code; on one machine, `compose.demo.yaml` runs both.

**Running the SQL.** Every statement here reads and none writes. They run against the demo's
database. Where a statement needs a copy's lifetime or the age a free copy is kept to, it uses the
defaults, 24 hours (`Demo:CopyLifetimeHours`) and 44 hours (`Demo:Pool:MaxFreeAgeHours`): put in
the deployment's values if it sets others.

On the Azure deployment (ADR-0061) the server takes Microsoft Entra sign-ins only and admits
Azure services alone, and the two database users belong to managed identities that exist only
there. The one person who can sign in, the Microsoft Entra administrator, runs nothing in that
database but `infra/sql-principals.sql` (ADR-0061, decision 5; `infra/README.md` names the
migration run by hand as its one exception). So these statements need a road there before they
are used: a throwaway job that carries `azurebank-app`, like the sign-in probe of
`infra/README.md` step 7, would send them as `azurebank_app`. All but one: section 7's first
statement reads `sys.dm_db_partition_stats`, which Microsoft's page (read 2026-10-03) says needs
`VIEW DATABASE STATE` and `VIEW DEFINITION`, or on SQL Server 2022 and later their performance
and security forms. `azurebank_app` holds neither, and `infra/sql-principals.sql` refuses any
permission of the two users but `CONNECT`, so on Azure that statement has no road yet. None of
them has been run as that user, and running them as the administrator would be a new exception
to that rule, to be written there first.

---

## The line

```text
pool: free=50 was=23 claimed=7 claims24h=12 clientsAtCap=0 seeded=38 deleted(expired=4 hardStop=0 staleFree=11 failed=0) swept(idempotency=120 grants=31) tombstones=412 foreignUsers=0 ceiling=no result=PoolOk
```

| Count | What it counts |
|---|---|
| `free` | Fresh free copies at the end of the run: free, and younger than `Demo:Pool:MaxFreeAgeHours` |
| `was` | Free copies when the run started, however old, before it changed anything: what visitors met. One too old to count towards the target is still free until the run that replaces it deletes it |
| `claimed` | Claimed copies whose users still exist, at the end |
| `claims24h` | Claims in the 24 hours before the run started, deleted copies included |
| `clientsAtCap` | Client keys with at least `Demo:Claim:MaxPerClientPerDay` claims in those 24 hours |
| `seeded` | Copies the run built |
| `expired` | Claimed copies deleted: their time was over and no session was running in them |
| `hardStop` | Claimed copies deleted 48 hours past their time with a session still live in them (section 5) |
| `staleFree` | Free copies deleted because they were too old to hand out. Only a run whose top-up reached its target deletes them, the lowered target under the ceiling; one that came up short leaves them free (section 2) |
| `failed` | Copies whose delete threw (section 4) |
| `idempotency`, `grants` | Expired idempotency records and grants removed, whoever they belonged to |
| `tombstones` | Records of deleted copies, at the end |
| `foreignUsers` | Users that belong to no copy, when the run started (section 3) |
| `ceiling` | `yes` when the day's claims held the top-up below its target |
| `result` | The exit code's name |

**A run with no line did not finish.** It exited 1. Its last line is `recycle failed: …` when
something that is no copy's failed, such as a count, the roles the top-up creates first, or a
sweep, and the log says which; or `recycle was cancelled` when the job was stopped. Whatever the
run had deleted stays deleted, a copy is never left half built or half deleted, and the next run
starts again from the rows as they are.

**A run that exited 2 did nothing.** It refused before it opened anything, and its one Error line,
`recycle refused: …`, says why: the demo is off where the job runs (`Demo__Enabled`), a demo
setting is out of range or holds a value that cannot be read as its type (the line names the key),
the pepper is missing or short, or the connection string is missing, unreadable or names no
database. Change the job's configuration; running it again as it is changes nothing.

## The exit code

| Code | Name | What to do |
|---|---|---|
| 0 | `PoolOk` | Nothing, unless the run found no copy at all (below) |
| 1 | | The run did not finish (above) |
| 2 | | The run refused, and did nothing (above) |
| 10 | `PoolLow` | The run found fewer free copies than `Demo:Pool:LowMark`, and topped the pool up. Section 1 |
| 11 | `PoolEmpty` | The run found no free copy, and at least one copy that is not a record: visitors may have been turned away since the run before. Section 1 |
| 12 | `TopUpIncomplete` | A copy could not be built and the pool ended below its target. Section 2 |
| 13 | `ForeignUsers` | A user that belongs to no copy exists. Section 3 |
| 14 | `DeleteFailed` | A copy could not be deleted. Section 4 |
| 15 | `ClaimCeiling` | The day's claims held the top-up below `Demo:Pool:TargetFree`. Section 1 |

When several apply, the run exits with the first of 13, 14, 12, 15, 11, 10, and the line still
carries every count. 10, 11 and 15 mean the run finished and the pool was short; 12, 13 and 14 need
a look.

**A record is not a copy.** A run that found no copy, only the records of deleted copies or no row
at all, is read as the first fill of a database and exits 0, not 11. A pool whose every copy was
deleted is read the same way, so the run after it exits 0 although visitors may have been turned
away in between. The run before says so, if it printed its line: `free=0` and `claimed=0`.

## 0. The pool as it stands

```sql
SELECT
    COUNT(CASE WHEN c.ClaimedAt IS NULL AND c.CreatedAt >= DATEADD(HOUR, -44, SYSUTCDATETIME()) THEN 1 END) AS FreshFree,
    COUNT(CASE WHEN c.ClaimedAt IS NULL AND c.CreatedAt < DATEADD(HOUR, -44, SYSUTCDATETIME()) THEN 1 END) AS TooOldFree,
    COUNT(CASE WHEN c.ClaimedAt IS NOT NULL AND c.DeletedAt IS NULL THEN 1 END) AS Claimed,
    COUNT(CASE WHEN c.DeletedAt IS NOT NULL THEN 1 END) AS Records,
    COUNT(CASE WHEN c.ClaimedAt > DATEADD(HOUR, -24, SYSUTCDATETIME()) THEN 1 END) AS Claims24h
FROM DemoCopies c;
```

Every copy that is not a record has three users. This lists any that has not, which a copy built
in one transaction cannot be:

```sql
SELECT c.Id, c.CreatedAt, c.ClaimedAt, COUNT(u.Id) AS Users
FROM DemoCopies c
LEFT JOIN AspNetUsers u ON u.DemoCopyId = c.Id
WHERE c.DeletedAt IS NULL
GROUP BY c.Id, c.CreatedAt, c.ClaimedAt
HAVING COUNT(u.Id) <> 3;
```

## 1. Short, empty, or held at the ceiling (10, 11, 15): a drain or a busy day

The claims of the last 24 hours, by the client they came from. `ClientKey` is a keyed hash of the
address a copy was claimed from: it tells one client from another and cannot be read back into an
address.

```sql
SELECT CONVERT(varchar(64), c.ClientKey, 2) AS ClientKey, COUNT(*) AS Claims,
       MIN(c.ClaimedAt) AS FirstClaim, MAX(c.ClaimedAt) AS LastClaim
FROM DemoCopies c
WHERE c.ClaimedAt > DATEADD(HOUR, -24, SYSUTCDATETIME())
GROUP BY c.ClientKey
ORDER BY Claims DESC;
```

- **A few keys hold most of the claims:** a drain. A key is one address, so an office behind one
  address counts as one client.
- **Many keys with one or two claims each:** a busy day. The pool's size is
  `Demo:Pool:TargetFree`, and a run tops it up to that.
- **A row with no key** counts claims whose copies were deleted already: the key leaves with the
  copy. Inside 24 hours that happens only when `Demo:CopyLifetimeHours` is below 24.

**15 is the ceiling doing its job.** A run builds no more than `Demo:Pool:MaxClaimsPerDay` minus
the day's claims, so a pool drained again and again cannot grow the database without end.

**More free copies than `Demo:Pool:TargetFree`** means two runs overlapped: a `seed-pool` started
while `recycle` was building, or two runs of the job. Each topped up from its own count. Nothing is
wrong with the copies, and the extra ones are deleted when they grow too old to count; a schedule
whose runs end before the next starts keeps it from happening again.

## 2. A copy could not be built (12)

The log names each failed copy by its id, with SQL Server's error number when the database refused
it. A copy is built in one transaction, so nothing of a failed one was written. A copy whose
handle, account number or transaction number was taken already (2601 or 2627) has been drawn again
three times before it counts as failed, and three failures in a row end the top-up. The next run
tries again.

**A run whose top-up came up short deletes no free copy too old to count**, and its `staleFree` is
0. Until a run builds the pool back to its target, a copy older than `Demo:Pool:MaxFreeAgeHours` is
still one a visitor can be given; the first run that reaches its target deletes them. A pool whose
copies grew old together keeps them while nothing can be built, instead of being left with none.

## 3. Users outside every copy (13)

First, whether this is the demo's database at all:

```sql
SELECT COUNT(*) AS PoolRows FROM DemoCopies;
```

**0 pool rows:** the run was pointed at a database that is not the demo's, or at the demo's
before its first fill, after somebody registered through the app: on Azure the app goes live on
an empty database with its registration open (`infra/README.md`, "What is not here"). Either way
it wrote nothing, not even a sweep. Check the connection string the run was given, then the
users below. **Pool rows and users outside them:** these users are not the pool's.

```sql
SELECT u.Id, u.AzureTag, u.CreatedAt
FROM AspNetUsers u
WHERE u.DemoCopyId IS NULL
ORDER BY u.CreatedAt;
```

No statement that deletes a copy matches them: each is keyed on the copy. The two sweeps are not.
They remove the expired grants and idempotency records of anyone, these users' included, as the
API's own clean-ups do. Every run of `recycle` exits 13 while one of these users exists, and
nothing removes the users themselves: a person decides what they are. `seed-pool` fills the pool
beside them, exits 0 and counts them on its line; it exits 13 only on a database with no pool row
at all, where it writes nothing.

## 4. A copy could not be deleted (14)

The log names the copy: `Demo copy <id> could not be deleted (error <number>)`. The copy is left
exactly as it was, and the next run tries it again.

- **547:** a foreign key refused the delete, and the message names it. The likely cause is a table
  that holds a copy's rows and that `DemoCopyRecycler.TablesOfACopy` does not name; every run will
  fail on this copy, and on every copy that wrote to that table, until the recycler names it.
- **-2:** a statement ran past the 120 seconds a run gives it. The copy holds more than a run can
  delete in that time.

What the copy holds, table by table:

```sql
SELECT 'AspNetUsers' AS TableName, COUNT(*) AS RowsOfTheCopy
FROM AspNetUsers u WHERE u.DemoCopyId = '<copy id from the log>'
UNION ALL
SELECT 'Accounts, closed ones included', COUNT(*)
FROM Accounts a JOIN AspNetUsers u ON u.Id = a.UserId
WHERE u.DemoCopyId = '<copy id from the log>'
UNION ALL
SELECT 'Transactions', COUNT(*)
FROM Transactions t JOIN Accounts a ON a.Id = t.AccountId JOIN AspNetUsers u ON u.Id = a.UserId
WHERE u.DemoCopyId = '<copy id from the log>'
UNION ALL
SELECT 'StepUpAuthorizations', COUNT(*)
FROM StepUpAuthorizations s JOIN AspNetUsers u ON u.Id = s.UserId
WHERE u.DemoCopyId = '<copy id from the log>'
UNION ALL
SELECT 'IdempotencyRecords', COUNT(*)
FROM IdempotencyRecords r JOIN AspNetUsers u ON u.Id = r.UserId
WHERE u.DemoCopyId = '<copy id from the log>'
UNION ALL
SELECT 'RefreshTokens', COUNT(*)
FROM RefreshTokens g JOIN AspNetUsers u ON u.Id = g.UserId
WHERE u.DemoCopyId = '<copy id from the log>';
```

## 5. Copies past their time, held by a live session (and `hardStop`)

A claimed copy whose time is over is deleted only once no grant of its users is live, or ended
less than five minutes ago: a request accepted the moment before a grant ended may still be
running. These are the ones waiting:

```sql
SELECT c.Id, c.ClaimedAt, MAX(t.ExpiresAt) AS LiveUntil
FROM DemoCopies c
JOIN AspNetUsers u ON u.DemoCopyId = c.Id
JOIN RefreshTokens t ON t.UserId = u.Id
WHERE c.DeletedAt IS NULL
  AND c.ClaimedAt < DATEADD(MINUTE, -5, DATEADD(HOUR, -24, SYSUTCDATETIME()))
  AND t.RevokedAt IS NULL AND t.ExpiresAt > DATEADD(MINUTE, -5, SYSUTCDATETIME())
GROUP BY c.Id, c.ClaimedAt
ORDER BY c.ClaimedAt;
```

Each is deleted by the first run more than five minutes after its `LiveUntil`. **`hardStop` above 0** means a copy got 48
hours past its time with a grant still live, and was deleted anyway. A grant lives at most
`Jwt:RefreshTokenLifetimeMinutes` from sign-in (60 minutes by default, 24 hours at most), so that
grant was issued after the copy's time was over: sign-in went on being accepted for it. The cause
is wherever sign-in is decided, not the pool; the run only reports it. The session of that grant
ends at its next renewal, which is answered 401 and writes a `RefreshTokenUnknown` audit row with
no actor.

## 6. The PIN pepper: when the old one can go

PIN hashes by the key id of the pepper they were made with:

```sql
SELECT k.KeyId, COUNT(*) AS PinHashes
FROM AspNetUsers u
CROSS APPLY (SELECT CHARINDEX(',keyid=', u.PinHash) AS At) p
CROSS APPLY (SELECT CASE
        WHEN u.PinHash IS NULL THEN 'no PIN'
        WHEN p.At = 0 THEN 'no key id: not peppered'
        WHEN CHARINDEX('$', u.PinHash, p.At) = 0 THEN 'unreadable'
        ELSE SUBSTRING(u.PinHash, p.At + 7, CHARINDEX('$', u.PinHash, p.At) - p.At - 7)
    END AS KeyId) k
GROUP BY k.KeyId
ORDER BY k.KeyId;
```

A rotation goes in ADR-0011's order, add, activate, drain, retire, on the API and on the job that
runs `recycle` alike. **Add** the new pepper under `Security:PreviousPinPeppers`, with its key id,
everywhere first. Only then **activate** it, as `Security:PinPepper` and `Security:PinPepperKeyId`,
with the old one moved under `Security:PreviousPinPeppers`. The copies a run builds carry the job's
active key id, and the API verifies a PIN hash only with the pepper its ring holds under that id:
a job that activates a key id the API does not hold yet builds copies whose PIN the API cannot
verify, and refuses, until the API holds it. A hash made with the old pepper is replaced when its
PIN is next used, and the copies built before the rotation leave as the pool turns over. **The old
pepper is removed when its key id's count here is 0, and not before:** a pepper removed while a
hash still carries its key id makes that PIN unusable.

A `Security:PreviousPinPeppers` key that is not a whole number >= 1, has surrounding whitespace,
shares its id with another key (`1` and `01`) or does not hold exactly one value is refused at API
startup and at the start of the Seeder commands that write PINs, before any database work, naming
the key and never its pepper. A key of 32 characters or more, long enough to be a pepper, is named
by its length only.

On the Azure deployment (ADR-0061) the template gives the `api` container one pepper,
`Security__PinPepper`, with no key id (so 1) and no previous pepper, and `infra/README.md` says
rotating an application secret is not in its files. A rotation there starts with
`infra/main.bicep` and `infra/secrets.ps1` carrying `Security__PinPepperKeyId` and
`Security__PreviousPinPeppers__<id>`, for the `api` container and, once it exists, the pool's job.

## 7. Storage: what `recycle` cannot give back

Where the space is:

```sql
SELECT t.name AS TableName,
       SUM(CASE WHEN s.index_id IN (0, 1) THEN s.row_count ELSE 0 END) AS TableRows,
       SUM(s.reserved_page_count) * 8 / 1024 AS ReservedMB
FROM sys.dm_db_partition_stats s
JOIN sys.tables t ON t.object_id = s.object_id
GROUP BY t.name
ORDER BY ReservedMB DESC;
```

A run deletes a copy's users and everything of theirs: accounts, ledger, authorisations,
idempotency records, grants and notices. It never deletes an audit row (ADR-0044 D6), so the rows a
visitor's actions wrote stay after the copy is gone. These are the audit rows of deleted copies:

```sql
SELECT COUNT_BIG(*) AS AuditRowsOfDeletedCopies
FROM AuditEvents e
WHERE EXISTS (
    SELECT 1 FROM DemoCopies c WHERE c.DeletedAt IS NOT NULL AND c.OwnerUserId = e.ActorUserId);
```

When `AuditEvents` is what fills the database, nothing in the application gives that space back.
It comes back only by giving the database more, or by recreating the demo's database, which starts
a new chain and loses every row. On Azure the database is Basic, 5 DTU and 2 GB, set in
`infra/main.bicep` (more is a change to ADR-0061, decision 3); recreating it first needs the lock
`keep-the-database` removed, and the two database users made again with `infra/sql-principals.ps1`
before the migration and the app can sign in (`infra/README.md`).

## 8. An audit row whose actor is neither a user nor a deleted copy

```sql
SELECT e.Sequence, e.OccurredAt, e.Event, e.ActorUserId
FROM AuditEvents e
WHERE e.ActorUserId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM AspNetUsers u WHERE u.Id = e.ActorUserId)
  AND NOT EXISTS (SELECT 1 FROM DemoCopies c WHERE c.OwnerUserId = e.ActorUserId)
ORDER BY e.Sequence;
```

A run deletes only a copy's users, and keeps the copy's record, whose `OwnerUserId` is the actor
of the copy's audit rows. A row listed here names a user that something other than a run deleted.
**The record is the operator's word, not proof:** anyone who can write to the database can add a
record that takes a row off this list (ADR-0062, decision 11).

## Afterwards

Nothing on this page changes a row. A change made by hand on the demo's database is not audited:
write down who made it, when and why, somewhere the database cannot revise.
