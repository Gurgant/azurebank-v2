# Runbook — the demo pool

**Symptom:** a run of `recycle` ended with a code from 10 to 15, or its line shows `hardStop` or
`failed` above 0, or it ended with no line at all, with exit 1 or 2. Or a visitor is refused a
copy with 429 `DEMO_POOL_EMPTY` or `DEMO_DAILY_LIMIT` (section 1), or is told their copy reached
its limit of changes, 429 `DEMO_COPY_LIMIT` (section 9). Or the PIN of a fresh copy is not taken
although every run ended well (section 10). Or the demo's database is filling up (section 7).

**Why this runbook exists:** a run decides everything from the rows as they are, and says what it
found in one line and one exit code
([ADR-0062](../adr/0062-demo-visitors-get-private-copies-from-a-prepared-pool.md), decision 12):
the code names one signal, the line carries every count. This page says what each one means and
gives the SQL that looks behind it. How a visitor takes a copy and what a copy may change is
[ADR-0063](../adr/0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md).

**Starting a run.** The pool's job is `recycle`; a first fill, or a refill by hand, is
`seed-pool`, or `seed-pool <N>` to top up to N free copies. Each needs `Demo__Enabled=true`, the
API's PIN pepper (section 10) and the app's database user, never the migration's (ADR-0060,
decision 5): [the Seeder's README](../../backend/tools/AzureBank.Seeder/README.md) has every
variable and exit code, and `compose.demo.yaml` runs both on one machine. On Azure the job is
`azurebank-pool`: `recycle` every four hours, at minute 0 of the hours 0, 4, 8, 12, 16 and 20
UTC, one run at a time, never retried, and the first fill is a `recycle` too. How a run is read
there is in [`infra/README.md`](../../infra/README.md), "Reading the logs"; its commands for
the pool:

```powershell
python infra/deploy.py --pool-run             # one run beside the schedule: the first fill, or a refill by hand
python infra/deploy.py --pool-log             # the latest run's verdict, then what it printed; or --pool-log '<execution>'
python infra/deploy.py --check                # moves nothing; says whether the job's pepper is the app's
```

Run them from a terminal, signed in with the Azure CLI and with `AZURE_SUBSCRIPTION_ID` and
`AZURE_RESOURCE_GROUP` set (`infra/README.md`, "Before you start"): inside GitHub Actions each is
refused. On Azure read a run's exit code first and its line second: the line is console text in a
capped log workspace, arrives minutes after it was written, and on a day the cap was reached does
not arrive at all.

**Running the SQL.** Every statement here reads and none writes. They run against the demo's
database, and use the defaults where they need a copy's lifetime (24 hours,
`Demo:CopyLifetimeHours`) or the age a free copy is kept to (44 hours,
`Demo:Pool:MaxFreeAgeHours`): put in the deployment's values if it sets others. On Azure none of
them has been run, and they have no road yet: the server's administrator runs nothing in that
database but `infra/sql-principals.sql` (ADR-0061, decision 5), and `azurebank_app`, the user a
throwaway job would send them as, lacks the `VIEW DATABASE STATE` that section 7's first
statement needs.

---

## The line

```text
pool: free=50 was=23 claimed=7 claims24h=12 clientsAtCap=0 seeded=38 deleted(expired=4 hardStop=0 staleFree=11 failed=0) swept(idempotency=120 grants=31) tombstones=412 foreignUsers=0 ceiling=no result=PoolOk
```

| Count | What it counts |
|---|---|
| `free` | Fresh free copies at the end of the run: free, and younger than `Demo:Pool:MaxFreeAgeHours` |
| `was` | Free copies when the run started, however old, before it changed anything: what visitors met |
| `claimed` | Claimed copies whose users still exist, at the end |
| `claims24h` | Claims in the 24 hours before the run started, deleted copies included |
| `clientsAtCap` | Client keys that made at least `Demo:Claim:MaxPerClientPerDay` claims in those 24 hours |
| `seeded` | Copies the run built |
| `expired` | Claimed copies deleted: their time was over and no session was running in them |
| `hardStop` | Claimed copies deleted 48 hours past their time while a session was still live in them (section 5) |
| `staleFree` | Free copies deleted because they were too old to hand out; only a run whose top-up reached its target deletes them (section 2) |
| `failed` | Copies whose delete threw (section 4) |
| `idempotency`, `grants` | Expired idempotency records and grants removed, whoever they belonged to |
| `tombstones` | Records of deleted copies, at the end |
| `foreignUsers` | Users that belong to no copy, when the run started (section 3) |
| `ceiling` | `yes` when the day's claims held the top-up below its target |
| `result` | The exit code's name |

**A run that printed no line did not finish**, and exited 1. Its last line is `recycle failed: …`
when something that is no copy's failed (a count, the roles the top-up creates first, a sweep),
or `recycle was cancelled`. What it had deleted stays deleted, no copy is left half built or half
deleted, and the next run starts again from the rows as they are. Read that last line before
starting anything again: on a first fill, a login that cannot write ends this way, SQL Server's
229 in the exception, and not as 12, because the top-up creates the two roles before its first
copy (read in the code, not measured).

**A run that exited 2 did nothing.** Its one Error line, `recycle refused: …`, says why: the demo
is off where the job runs (`Demo__Enabled`), a demo setting is out of range or unreadable (the
line names the key), the pepper is missing or short, or the connection string is missing,
unreadable or names no database. Change the job's configuration: running it again as it is
changes nothing.

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
at all, is read as a first fill and exits 0, not 11, although visitors may have been turned away
since the run before. The run before says so, if it printed its line: `free=0` and `claimed=0`.

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

Every copy that is not a record has three users. This lists any that has not:

```sql
SELECT c.Id, c.CreatedAt, c.ClaimedAt, COUNT(u.Id) AS Users
FROM DemoCopies c
LEFT JOIN AspNetUsers u ON u.DemoCopyId = c.Id
WHERE c.DeletedAt IS NULL
GROUP BY c.Id, c.CreatedAt, c.ClaimedAt
HAVING COUNT(u.Id) <> 3;
```

## 1. Short, empty, or held at the ceiling (10, 11, 15): a drain or a busy day

The claims of the last 24 hours, by client. `ClientKey` is a keyed hash of the address a copy was
claimed from: it tells one client from another and cannot be read back into an address.

```sql
SELECT CONVERT(varchar(64), c.ClientKey, 2) AS ClientKey, COUNT(*) AS Claims,
       MIN(c.ClaimedAt) AS FirstClaim, MAX(c.ClaimedAt) AS LastClaim
FROM DemoCopies c
WHERE c.ClaimedAt > DATEADD(HOUR, -24, SYSUTCDATETIME())
GROUP BY c.ClientKey
ORDER BY Claims DESC;
```

- **A few keys hold most of the claims:** a drain, or many visitors behind one address. A key is
  an IPv4 address in full, an IPv6 address by its /64 prefix, or the one key of every connection
  that shows no address. Behind a proxy the BFF is not told to trust
  (`ForwardedHeaders:KnownProxies`, or its network in `ForwardedHeaders:KnownIPNetworks`) every
  visitor has the proxy's key, as under `compose.demo.yaml`.
- **Many keys, one or two claims each:** a busy day. A run tops the pool up to
  `Demo:Pool:TargetFree`.
- **A row that has no key** counts claims whose copies were deleted already, which inside 24
  hours happens only when `Demo:CopyLifetimeHours` is below 24.

**What a visitor reads.** At `Demo:Claim:MaxPerClientPerDay` claims in those 24 hours, a client's
next claim is refused: 429 `DEMO_DAILY_LIMIT`, whose `retryAfterSeconds` and `Retry-After` header
are the seconds until that client is back under its cap. When no copy is free, however old, the
answer is 429 `DEMO_POOL_EMPTY`, naming no wait: the next run's top-up ends it, unless the
ceiling holds it back. Give a changed `Demo__Claim__MaxPerClientPerDay` to the API, which refuses
by it, and to the job, which counts by it.

**15 is the ceiling doing its job.** A run builds no more than `Demo:Pool:MaxClaimsPerDay` minus
the day's claims, so a pool drained again and again cannot grow the database without end.

**More free copies than `Demo:Pool:TargetFree`** means two runs overlapped, each topping up from
its own count. The extra copies are deleted when they grow too old to count. On Azure
`--pool-run` starts nothing beside a run in progress, and does not see a run the schedule starts
after it has looked.

## 2. A copy could not be built (12)

The log names each failed copy by its id, and SQL Server's error number when the database refused
it. Nothing of a failed copy was written: a copy is built in one transaction. One whose handle,
account number or transaction number was taken already (2601 or 2627) is drawn again three times
before it counts as failed, and three failures in a row end the top-up. The next run tries
again. Until a run reaches its target it deletes no free copy too old to count (`staleFree` is
0): a copy older than `Demo:Pool:MaxFreeAgeHours` is still one a visitor can be given.

## 3. Users outside every copy (13)

First, whether this is the demo's database at all:

```sql
SELECT COUNT(*) AS PoolRows FROM DemoCopies;
```

**0 pool rows:** the run was pointed at a database that is not the demo's, one `seed` filled or
one people registered on, and wrote nothing, not even a sweep. Check the connection string it was
given. **Pool rows and users outside them:** these users are not the pool's:

```sql
SELECT u.Id, u.AzureTag, u.CreatedAt
FROM AspNetUsers u
WHERE u.DemoCopyId IS NULL
ORDER BY u.CreatedAt;
```

Registration answers 403 `REGISTRATION_CLOSED` when the `api` or the `bff` container has
`Demo__Enabled=true` (ADR-0063), so a user that registered beside the pool says the job had the
flag on while neither container did: compare it on the three. On Azure one run of the template
sets it on both containers and builds the job (`infra/README.md`, "Turn the demo on"). No
statement that deletes a copy matches these users; only the two sweeps of expired grants and
idempotency records take anyone's. Every `recycle` exits 13 while one of them exists, and
nothing removes them: a person decides what they are. `seed-pool` exits 13 only where there is
no pool row at all; beside a pool it fills it, exits 0 and counts them on its line.

## 4. A copy could not be deleted (14)

The log names the copy: `Demo copy <id> could not be deleted (error <number>)`. The copy is left
exactly as it was, and the next run tries it again.

- **547:** a foreign key refused the delete, and the message names it. The likely cause is a table
  that holds a copy's rows and that `DemoCopyRecycler.TablesOfACopy` does not name; every run will
  fail on this copy, and on every copy that wrote to that table, until the recycler names it.
- **-2:** a statement ran past the 120 seconds a run gives it: the copy holds more than a run can
  remove in that time.

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
less than five minutes ago. These are the ones waiting, each until the first run more than five
minutes after its `LiveUntil`:

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

**`hardStop` above 0** means a copy got 48 hours past its time while a grant was still live, and
was deleted anyway. A grant lives 24 hours at most (`Jwt:RefreshTokenLifetimeMinutes`, 60 minutes
by default), so sign-in went on being accepted after the copy's time was over. The API refuses
that sign-in when it has the flag (ADR-0063, decision 10), so it ran without the flag, or with
another lifetime than the job's: compare `Demo__Enabled` and `Demo__CopyLifetimeHours` on the
`api` container and on the job. The session of that grant ends at its next renewal, which is
answered 401 and writes a `RefreshTokenUnknown` audit row that names no actor.

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
runs `recycle` alike. **Add** the new pepper and its key id under `Security:PreviousPinPeppers`
everywhere first. Only then **activate** it, as `Security:PinPepper` and
`Security:PinPepperKeyId`, and move the old one under `Security:PreviousPinPeppers`: a job that
activates a key id the API does not hold yet builds copies whose PIN the API cannot verify. Old
hashes are replaced as their PINs are used, and old copies leave as the pool turns over. **The
old pepper is removed when its key id's count here is 0, and not before:** a pepper removed while
a hash still carries its key id makes that PIN unusable.

On Azure the template writes the `api` container and the pool job one pepper, from one value and
without a key id (so 1), and no file of the deployment rotates it (`infra/README.md`, "What is
not here"). `python infra/deploy.py --check` says whether the two are still the same.

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

A run deletes a copy's users and everything of theirs, and never an audit row (ADR-0044 D6): the
rows a visitor's actions wrote stay after the copy is gone. These are the audit rows of deleted
copies:

```sql
SELECT COUNT_BIG(*) AS AuditRowsOfDeletedCopies
FROM AuditEvents e
WHERE EXISTS (
    SELECT 1 FROM DemoCopies c WHERE c.DeletedAt IS NOT NULL AND c.OwnerUserId = e.ActorUserId);
```

When `AuditEvents` is what fills the database, nothing in the application gives that space back.
It comes back only by giving the database more, or by recreating the demo's database, which starts
a new chain and loses every row. On Azure the database is 2 GB at most (ADR-0061, decision 3)
and no alert watches its size (ADR-0064, decision 13). Recreating it there follows the order
`infra/README.md` gives for turning the demo back ("Operating it"): the pool job goes first.

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
**The record is a claim, not proof:** anyone who can write to the database can add a
record that takes a row off this list (ADR-0062, decision 11).

## 9. A copy at its limit of changes (429 `DEMO_COPY_LIMIT`)

The copies that have made the most changes, and how many:

```sql
SELECT TOP (20) c.Id, c.ClaimedAt, c.Writes
FROM DemoCopies c
WHERE c.DeletedAt IS NULL
ORDER BY c.Writes DESC, c.ClaimedAt;
```

`Writes` is what the API has counted on the copy, and the request that finds it at
`Demo:Copy:MaxWrites` (200 unless the `api` container sets `Demo__Copy__MaxWrites`) is answered
429 `DEMO_COPY_LIMIT`. Reads still answer, and so does signing out everywhere. Nothing resets the
count: the visitor claims another copy, and this one ends at its time.

- **It counts requests, not changes:** every request of the copy's signed-in user that is not a
  GET, HEAD, OPTIONS or TRACE, whatever the API then answers (a refusal, or a retry answered
  from the idempotency store), and each reveal of an account number. Sign-in, renewal, sign-out
  and the claim itself are not counted.
- **Many copies at the limit under one `ClientKey`** (section 1's statement) is one client
  working through its copies of the day: the per-client cap is what bounds it.
- **A signed-in caller who belongs to no copy**, or to the record of a deleted one, gets the same
  answer: a session opened before the `api` container was given the flag, or a copy deleted at
  the backstop while a session was still live in it (section 5).

## 10. Whether a run built copies a visitor can use

A run's line and its exit code say that copies were built, not that their PIN is taken: the job
hashes each copy's PIN with the pepper it was given, and the API verifies with its own. When the
two differ every status stays good: the claim answers, the dashboard loads, and a PIN that is not
taken is answered 200 with `verified` false. No exit code, alert or status shows it.

**First, without spending a copy.** On Azure:

```powershell
python infra/deploy.py --check
```

It prints "The pool job's PIN pepper and connection string are the app's", or which of the two
differs. It shows no value and moves nothing, so run it after anything that could have changed
either side: a run of the template, a deployment, a start of the job by hand, a stop and a start
of the app.

**Then, after any change of the job's secrets, or after a line that says they differ: from a
browser.** Claim a copy and make one transfer to a contact of the copy with its PIN. Where a PIN
is checked by itself, what counts is what the screen says and `data.verified` in the answer's
body, never the status. This spends a copy and leaves its audit rows, so it is not the first
check.

**If the PIN is not taken,** no copy that job built is usable. On Azure, stop the app first;
`infra/README.md`, "When something fails", has the row for the line that says the two differ.

## Afterwards

Nothing on this page changes a row. A change made by hand on the demo's database is not audited:
write down who made it, when and why, somewhere the database cannot revise.
