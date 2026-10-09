# Runbook — the demo pool

**Symptom:** a run of `recycle` ended with a code from 10 to 15, or its line shows `hardStop` or
`failed` above 0, or it ended with no line at all, with exit 1 or 2. Or a visitor is refused a
copy with 429 `DEMO_POOL_EMPTY` or `DEMO_DAILY_LIMIT` (section 1), or is told their copy reached
its limit of changes, 429 `DEMO_COPY_LIMIT` (section 9). Or the PIN of a fresh copy is not taken
although every run ended well (section 10). Or the demo's database is
filling up: on Azure it is Basic, 2 GB at most (`infra/main.bicep`), and none of the
deployment's alerts watches its size (ADR-0061, decision 10).

**Why this runbook exists:** a run decides everything from the rows as they are, and says what it
found in one line and one exit code
([ADR-0062](../adr/0062-demo-visitors-get-private-copies-from-a-prepared-pool.md), decision 12).
The code names one signal; the line carries every count. This page says what each one means, and
gives the SQL that looks behind it. How a visitor takes a copy, what the API refuses them with
and what each copy may change is
[ADR-0063](../adr/0063-a-visitor-claims-a-prepared-copy-instead-of-registering.md).

**How a run is started.** The pool's job is the tools image with the argument `recycle`; a first
fill, or a refill by hand, is `seed-pool`, or `seed-pool <N>` to top up to N free copies. Each
needs `Demo__Enabled=true`, the connection string, and the API's `Security__PinPepper` and
`Security__PinPepperKeyId`. Where a deployment gives the app and its migration database users of
their own, it signs in as the app's, never the migration's (ADR-0060, decision 5's note). On the
Azure deployment (ADR-0061) that job carries the identity `azurebank-app` and signs in as
`azurebank_app`, never with `azurebank-migrate`; ADR-0062, decision 13, says what else adding it
changes. `backend/tools/AzureBank.Seeder/README.md` has every variable and code; on one machine,
`compose.demo.yaml` runs both.

**On the Azure deployment, once the demo is on there.** Azure has no pool job until the demo
is turned on there (`infra/README.md`, "Turn the demo on";
[ADR-0064](../adr/0064-the-azure-deployment-runs-the-demo-from-a-scheduled-pool-job.md)). The
template then holds one job, `azurebank-pool`: `recycle` every four hours, at minute 0 of the
hours 0, 4, 8, 12, 16 and 20 UTC, one run at a time
and no retry, with the identity `azurebank-app` and the app's pepper with no key id. There is no
`seed-pool` job: the first fill is `recycle` too, which builds the pool on a database that holds
no copy and exits 0. On 2026-10-07 the demo was on there and the deployment's check read the
job, in shape and with the app's pepper and connection string (`infra/README.md`, "Measured on
Azure"). Each "expected" on this page is still an expectation, not a measurement. Run the
commands from your own terminal, with the two variables `infra/README.md` names:

```powershell
python infra/deploy.py --pool-run             # one run beside the schedule: the first fill, or a refill by hand
python infra/deploy.py --pool-log             # the latest run's verdict, then what it printed; or --pool-log '<execution>'
python infra/deploy.py --check                # moves nothing; says whether the job's pepper is the app's
```

**There the read-back of a run is its exit code first and its line second.** The code is Azure's
to report, when the run ends. The line is console text in a capped log workspace: it arrives
minutes after it was written, and on a day the cap was reached it does not arrive at all.
Neither is kept for ever. `--pool-run` ends by the code, never by the status Azure gives the
execution: well on 0, 10, 11 and 15, and failed on 1, 2, 12, 13, 14, on a code that was not
reported, and when the read of the code itself was refused or failed. Its last line names the
count of the line below that the code says to read (`infra/README.md`, "Reading the logs").

**Running the SQL.** Every statement here reads and none writes. They run against the demo's
database. Where a statement needs a copy's lifetime or the age a free copy is kept to, it uses the
defaults, 24 hours (`Demo:CopyLifetimeHours`) and 44 hours (`Demo:Pool:MaxFreeAgeHours`): put in
the deployment's values if it sets others.

On the Azure deployment (ADR-0061) the server takes Microsoft Entra sign-ins only and admits
Azure services alone, and the two database users belong to managed identities that exist only
there. The one person who can sign in, the Microsoft Entra administrator, runs nothing in that
database but `infra/sql-principals.sql` (ADR-0061, decision 5; `infra/README.md` names the
migration run by hand as its one exception). So these statements need a road there before they
are used: a throwaway job that carries `azurebank-app`, like the probe job that
`infra/README.md` names under "Create it once", would send them as `azurebank_app`. All but one: section
7's first statement reads `sys.dm_db_partition_stats`, which Microsoft's page (read 2026-10-03)
says needs `VIEW DATABASE STATE` and `VIEW DEFINITION`, or on SQL Server 2022 and later their
performance and security forms. `azurebank_app` holds neither, and `infra/sql-principals.sql`
refuses any permission of the two users but `CONNECT`, so on Azure that statement has no road
yet. None of them has been run as that user, and running them as the administrator would be a
new exception to that rule, to be written there first.

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
starts again from the rows as they are. **On a database where no role exists yet, a login that
cannot write shows here and not as 12:** the top-up creates the two roles first, outside every
copy, so the refusal is "no copy's". The run is expected to exit 1 with `recycle failed:` and
SQL Server's 229 in the exception printed with it. That is read in the tool's code
(`DemoCopyBuilder`, `RecycleCommand`); the 229 that was measured ended a `seed-pool` with 12, on
a server where the roles were there already (`backend/tools/AzureBank.Seeder/README.md`). So
after an exit 1 on a first fill, read the run's last line before starting anything again.

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

- **A few keys hold most of the claims:** a drain. A key is one client as the BFF counts
  clients: an IPv4 address in full, an IPv6 address by its /64 prefix, and one key for every
  connection that shows no address. So an office behind one address counts as one client. And
  behind a proxy the BFF is not told to trust (`ForwardedHeaders:KnownProxies`, or the proxy's
  network in `ForwardedHeaders:KnownIPNetworks`), every visitor
  has the proxy's address: one key for everybody. Under `compose.demo.yaml` on one machine it
  was so when measured (Docker Desktop on Windows, 2026-10-04): the BFF saw the compose network's
  gateway address for every request, and every claim carried one key.
- **Many keys with one or two claims each:** a busy day. The pool's size is
  `Demo:Pool:TargetFree`, and a run tops it up to that.
- **A row with no key** counts claims whose copies were deleted already: the key leaves with the
  copy. Inside 24 hours that happens only when `Demo:CopyLifetimeHours` is below 24.

**What a visitor reads.** A client with `Demo:Claim:MaxPerClientPerDay` claims or more in those
24 hours is refused the next one with 429 `DEMO_DAILY_LIMIT`. The answer's `retryAfterSeconds`,
and its `Retry-After` header, are the seconds until that client is back under its cap; the line's
`clientsAtCap` counts such clients by the same rule. When no copy is free, however old, the
answer is 429 `DEMO_POOL_EMPTY`, with no wait named: the next run's top-up is what ends it,
unless the day's ceiling holds that top-up back (15, below). Give
a changed `Demo__Claim__MaxPerClientPerDay` to the API, which refuses by it, and to the job,
which counts by it.

**15 is the ceiling doing its job.** A run builds no more than `Demo:Pool:MaxClaimsPerDay` minus
the day's claims, so a pool drained again and again cannot grow the database without end.

**More free copies than `Demo:Pool:TargetFree`** means two runs overlapped: a `seed-pool` started
while `recycle` was building, or two runs of the job. Each topped up from its own count. Nothing is
wrong with the copies, and the extra ones are deleted when they grow too old to count; a schedule
whose runs end before the next starts keeps it from happening again. On the Azure deployment
the job's runs are four hours apart and a run may take 14 minutes at most; `infra/deploy.py`
starts nothing beside a run that is in progress, and the resource group's policy is expected to
refuse the job a parallelism above 1, which is two replicas of one execution. No rule of that
policy refuses a second execution beside one that is running. A run started some other way than
`--pool-run` is seen only by `deploy.py`'s one read of the executions, before a deployment or a
`--pool-run`; a schedule or a timeout changed on the deployed job only by its shape check, at a
deployment, a `--check` or a `--pool-run` (`infra/README.md`, "What each identity can do"; none
of it seen on Azure yet).

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

**0 pool rows:** the run was pointed at a database that is not the demo's: one `seed` filled, or
one people registered on. It wrote nothing, not even a sweep. Check the connection string the
run was given, then the users below. The demo's own database before its first fill is not a
second cause: on a database that only `migrate` has touched nobody can register through the
app, because the role a new user is given does not exist yet and the registration fails.
Measured on a local stack on 2026-10-05: it answered 500 and left no user and no role, and the
same request answered 201 once the roles were there (`infra/README.md`, "Not measured yet", has
the run). On Azure the app does go live on such a database with its registration not closed;
the roles come with the pool's first run, which creates them before its first copy. **Pool rows
and users outside them:** these users are not the pool's.

**Where such a user comes from.** Where the API and the BFF run with `Demo__Enabled=true`,
registration answers 403 `REGISTRATION_CLOSED` and sign-in lets in only the owner of a claimed
copy (ADR-0063), so the app creates no user outside the pool. The flag is one setting per
container, and registration through the app is refused when either the `api` or the `bff` has
it on. A user outside every copy that registered beside the pool says the job had the flag on
while neither of the app's two containers did: compare `Demo__Enabled` on the three. On the
Azure deployment neither container has it on, and no job exists beside them, until the demo is
turned on there: one run of the template then turns on both containers and builds the job, the
job after the app (`infra/README.md`, "Turn the demo on", which also says what to do if that
run leaves the job beside an app not proved to be the demo).

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
is where sign-in is decided, not the pool; the run only reports it. With `Demo__Enabled=true` the
API's sign-in gate refuses the owner of a copy claimed `Demo:CopyLifetimeHours` ago or more, as
it refuses an email nobody has (ADR-0063, decision 10). So a `hardStop` says the API ran without
the gate, or with another lifetime than the job's: compare `Demo__Enabled` and
`Demo__CopyLifetimeHours` on the `api` container with the job's. The session of that grant
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
by its length only, and so is a key that is not a whole number and has a section under it: the
first part of a pepper that holds `:`, or `__` in a variable's name. A value set on
`Security__PreviousPinPeppers` itself, with no key id after it, is refused the same way.

On the Azure deployment (ADR-0061) the template gives the `api` container one pepper,
`Security__PinPepper`, with no key id (so 1) and no previous pepper, and `infra/README.md` says
rotating an application secret is not in its files. A rotation there starts with
`infra/main.bicep` and `infra/secrets.ps1` carrying `Security__PinPepperKeyId` and
`Security__PreviousPinPeppers__<id>`, for the `api` container and for the pool's job, which the
template has held since 2026-10-05 with the app's pepper and no key id. Until then, what keeps
the two the same is that the template writes both from one value, and
`python infra/deploy.py --check` says whether they still are (section 10).

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
before the migration and the app can sign in (`infra/README.md`). With the demo on there, the
order is the one that file gives for turning the demo back, and its first step is the pool
job's deletion: a run of the job on the new database, before the app's flags are settled, is
what that order keeps from happening.

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

The copies that have made the most changes, with how many:

```sql
SELECT TOP (20) c.Id, c.ClaimedAt, c.Writes
FROM DemoCopies c
WHERE c.DeletedAt IS NULL
ORDER BY c.Writes DESC, c.ClaimedAt;
```

`Writes` is what the API has counted on the copy, and the request that finds it at
`Demo:Copy:MaxWrites` (200 unless the `api` container sets `Demo__Copy__MaxWrites`) is answered
429 `DEMO_COPY_LIMIT`: "This demo copy has reached its limit of changes. Start over to get a
fresh copy." Reads still answer, and so does signing out everywhere. Nothing resets the count:
the visitor claims another copy, and this one ends at its time.

- **It counts requests, not changes.** Every request of the copy's signed-in user that is not a
  GET, HEAD, OPTIONS or TRACE spends one, whatever the API then answers: a wrong PIN, a refused
  transfer and a retry answered from the idempotency store are each counted. So is each reveal
  of an account number, which is a GET. Sign-in, renewal, sign-out and the claim itself are not.
  A copy at its limit has sent that many requests, not written that many rows.
- **One copy at the limit** is a visitor who used the demo a lot, or a script. **Many copies at
  the limit under one `ClientKey`** (section 1's statement) is one client working through its
  copies of the day: the per-client cap is what bounds it.
- **The same answer with no copy to count on.** A signed-in caller who belongs to no copy, or
  to the record of a deleted one, is refused with this code too. With the demo on, a session is
  opened only for the owner of a claimed copy, so this takes a session opened before the `api`
  container was given the flag, or a copy deleted at the backstop while a session was still
  live in it (section 5).

## 10. Whether a run built copies a visitor can use

A run's line and its exit code say that copies were built. They do not say that a PIN of those
copies is taken. The job hashes each copy's PIN with the pepper it was given, and the API
verifies with its own. With two that differ every status stays good: the claim answers, the
dashboard loads, and a PIN that is not taken is answered 200 with `verified` false, so no exit
code, alert or status shows it.

**First, without spending a copy.** On the Azure deployment:

```powershell
python infra/deploy.py --check
```

It lists the job's two secrets and the app's with the sign-in of the terminal it runs in (inside
GitHub Actions it is refused), compares them, and prints "The pool job's PIN pepper and
connection string are the app's", or which of the two differs. It shows no value. It moves
nothing, so run it after anything that could have changed either side: a run of the template, a
deployment, a start of the job by hand, a stop and a start of the app (`infra/README.md`,
"Reading the logs"). It was run on Azure once, on 2026-10-07, and passed: the line it printed
about the two secrets is the one above (`infra/README.md`, "Measured on Azure").

**Then, after any change of the job's secrets, or after a line that says they differ: from a
browser.** Claim a copy with the demo's button, open the dashboard, and make one transfer to a
contact of the copy with its PIN. Where a PIN is checked by itself, what counts is what the
screen says and `data.verified` in the answer's body, never the status. It spends a copy and
leaves its audit rows, which is why it is not the first check.

**If the PIN is not taken,** no copy that job built is usable, and the next visitor meets the
same. On Azure, stop the app first; `infra/README.md`, "When something fails", has the row for
the line that says the two differ.

## Afterwards

Nothing on this page changes a row. A change made by hand on the demo's database is not audited:
write down who made it, when and why, somewhere the database cannot revise.
