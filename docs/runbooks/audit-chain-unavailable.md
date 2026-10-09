# Runbook — the audit chain is unavailable

**Symptom:** deposits, withdrawals and transfers are failing. Reads still work. Sign-in still works.

**Why:** ADR-0044 D1 makes an audit row atomic with the money movement it describes, so an audit
store that cannot be written stops the bank moving money. That is deliberate: a movement that was
never recorded is one that cannot afterwards be accounted for to the customer whose money moved.

The first half of this page is the outage: confirm it, read the log, triage, and what not to do.
The second half is `AzureBank.AuditVerifier`, the operator tool for the audit trail: how to run it,
what its exit codes mean, what to do when it reports `CHAIN BROKEN`, and what to run after recovery.

---

## Confirm it in one call

```bash
curl -s -w "\n%{http_code}\n" https://<host>/health/ready
```

The body names which check failed. When the `AuditEvents` table is renamed away and the database
is healthy, the running API answers `503` and:

```json
{"status":"Unhealthy","checks":[
  {"name":"database","status":"Healthy","description":null},
  {"name":"audit-chain","status":"Unhealthy",
   "description":"audit store unreadable — money movements will be refused (ADR-0044 D1)"}]}
```

`503` with `audit-chain` unhealthy does not mean the store is unreadable. The probe reports on
three axes, and the description names the axis:

- `audit store unreadable` is the read failure, and unreadable is not unreachable: a database that
  is down, an `AuditEvents` table that was never migrated, a disabled or corrupt index, or
  credentials that can no longer read it. Step 6 if it is the table, step 3b if it is the login.
- `audit store readable but NOT writable` is a store that answers every read and refuses every
  append. Step 3, and not an outage at all.
- `A timeout occurred while running check.` is the framework's wording, and is step 2.

`200` means the probe read the table and found an append permitted: start at step 4.

The endpoint answers instead of hanging because every readiness check is registered with
`Audit:TailTimeoutSeconds` (5 by default) as its timeout. **That bound is cooperative, not
enforced**: the framework signals a cancellation token and still awaits the check, so a check that
ignores its token is bounded by nothing. On a hang look at both: that the setting still reaches
every registration tagged `ready`, and that each of those checks passes its `CancellationToken` to
every await. The two registered today do.

## What you will see in the log

```
SecurityEvent AuditChainUnavailable: the audit chain tail could not be read within 5s,
so 1 pending audit row(s) and the action they describe are refused ON THIS ATTEMPT.
A transient fault may be retried by the execution strategy and then succeed, so a
single line is a blip and repetition is an outage
```

**Read the count before you read the line.** The audit chain runs inside EF's retrying execution
strategy, so one of these lines followed by the customer's movement succeeding is a retried blip
and not an incident: alert on repetition. A steady stream of them, with movements failing, is this
runbook.

The line writes **no audit row**: that row would need the very lock that just failed. The log line
and the health check are the whole signal. A container running as Production prints one JSON
object per line (`@m` the message, `@l` the level, `@x` the exception), and every value is also a
field of its own, so match the field and not the sentence:
`"SecurityEvent":"AuditChainUnavailable"`.

---

## Triage, in order

**1. Is the database reachable at all?** If the body also reports `database` unhealthy, it is a
database outage and not an audit-chain fault: restore the database first. A key ring that cannot be
built is not seen here: the API then stops during startup and nothing answers this endpoint (*When
the key ring is wrong*, below).

**2. Does it say `A timeout occurred while running check.`?** The check was cancelled; it found
nothing. Usually the readiness budget (`Audit:TailTimeoutSeconds`) elapsed, but the framework
reports the same description for any cancellation escaping a check. Treat it as "the probe did not
finish". Steps 4 and 5 are not where to look: the probe reads the tail `WITH (READUNCOMMITTED)`,
takes no lock and waits on none, so a locked tail reports `200`, and sending a timeout down there
ends in a `KILL` on a session that cannot have caused it. What is left, in order: whatever polls
this endpoint may carry a shorter deadline than the budget, which is not a store fault; then the
store, unreachable or answering slower than the budget (step 1); then the table (step 6). A
permission problem does not time out.

**3. Does it say `readable but NOT writable`?** The store answers every read and refuses every
audit row, so D1 refuses every money movement while the database looks perfectly healthy. **Those
words are the shared prefix of two verdicts that need opposite fixes: read the rest of the
sentence.** The probe tests `DATABASEPROPERTYEX(DB_NAME(), 'Updateability')` before it asks
`HAS_PERMS_BY_NAME`, so on a read-only database the permission question is never asked, and a
`GRANT` hunt there finds a clean permission graph while every movement stays refused.

- `... — the database is not READ_WRITE, so money movements will be refused` → **3a**.
- `... Check INSERT permission on AuditEvents for this login` → **3b**.

**3a. The database is not READ_WRITE.** Run this through the API's own connection string. A
default-intent session pointed at the listener lands on the primary and reports `READ_WRITE`, which
answers about a different replica than the probe asked.

```sql
SELECT DB_NAME()                                      AS database_name,
       @@SERVERNAME                                   AS answering_instance,
       DATABASEPROPERTYEX(DB_NAME(), 'Updateability') AS updateability,
       d.state_desc, d.is_read_only, d.is_in_standby, d.replica_id
FROM sys.databases d
WHERE d.database_id = DB_ID();
```

Three states, three remedies, none of them a `GRANT`:

- **`is_read_only = 1`, `is_in_standby = 0`, `replica_id` NULL**: somebody ran
  `ALTER DATABASE [db] SET READ_ONLY`, a release script or a maintenance job. `SET READ_WRITE`
  reverses it and needs exclusive access. Find out who set it, or it is set again an hour later.
- **`is_in_standby = 1`**: a restore finished `WITH STANDBY`. `RESTORE DATABASE [db] WITH RECOVERY`
  makes it writable and **ends the restore sequence**: no further log backup can ever be
  applied. On the DR copy, that command spends the copy to end a short outage, and if the API is
  pointed at a DR copy at all, the connection string is the fault. Confirm what it is first:

  ```sql
  SELECT TOP 5 restore_date, restore_type FROM msdb.dbo.restorehistory
  WHERE destination_database_name = DB_NAME() ORDER BY restore_date DESC;
  ```

- **`replica_id` NOT NULL**: an availability-group replica. Ask which role it holds:

```sql
SELECT ars.role_desc, ar.replica_server_name
FROM sys.databases d
JOIN sys.dm_hadr_availability_replica_states ars ON ars.replica_id = d.replica_id
JOIN sys.availability_replicas ar ON ar.replica_id = ars.replica_id
WHERE d.database_id = DB_ID();
```

`SECONDARY` separates two incidents. If the primary moved, point the API back at the listener. If
it never moved, read-only routing sent the API here: drop `ApplicationIntent=ReadOnly` from its
connection string. **Do not force failover to make this node writable.** Those two DMVs need
`VIEW SERVER STATE` and do not exist on Azure SQL Database, where the link state is
`sys.dm_geo_replication_link_status`, and where this same verdict also appears when a database
reaches its size limit. Rule that out first: `AuditEvents` is the fastest-growing table here and
nothing ever deletes from it.

A restore left `WITH NORECOVERY` is `RESTORING` and cannot be read: the probe then says
`audit store unreadable`, step 6. **Re-read `/health/ready` after the fix**: updateability is
tested first, so a denied INSERT can be queued behind it and appears only on the next probe.

**3b. The login cannot INSERT** (`AuditWritePermissionSqlServerTests` holds the probe to this
case). `HAS_PERMS_BY_NAME` answers for the current security context, so a session connected as
`sysadmin` or `db_owner` gets `can_insert = 1` for a login that is denied. Impersonate, and make the
answer carry the principal it answered for:

```sql
EXECUTE AS USER = '<database_user>';   -- the user the API connects as

DECLARE @object sysname =
    QUOTENAME(OBJECT_SCHEMA_NAME(OBJECT_ID('AuditEvents'))) + N'.' +
    QUOTENAME(OBJECT_NAME(OBJECT_ID('AuditEvents')));

SELECT USER_NAME()                                    AS answered_for,
       @object                                        AS object_asked_about,
       HAS_PERMS_BY_NAME(@object, 'OBJECT', 'INSERT') AS can_insert,
       HAS_PERMS_BY_NAME(@object, 'OBJECT', 'SELECT') AS can_select;  -- 0 reads as "unreadable"

REVERT;
```

`EXECUTE AS USER` needs `IMPERSONATE` on that user, which an admin session has, and `REVERT` outside
an impersonation context is a silent no-op. The object name is resolved, as the probe resolves it:
under a non-`dbo` schema a typed `dbo.AuditEvents` asks about a table that does not exist and
returns NULL. Then find the `DENY` itself:

```sql
SELECT dp.name, dp.type_desc, p.permission_name, p.state_desc
FROM sys.database_permissions p
JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
WHERE p.major_id = OBJECT_ID('AuditEvents');
```

That lists only explicit object-level permissions: a `DENY` inherited from a role,
`db_denydatawriter` above all, does not appear in it, so read the API user's role membership too. A
`DENY` beats any `GRANT`, including one inherited from `db_datawriter`. **Do not grant the API more
than INSERT on `AuditEvents`**: the chain is append-only by design, and a login that can update
or delete there is a larger problem than the outage you are ending.

**4. Is the table locked, not unreachable?** The health check stays healthy while the tail is
merely locked by a slow writer: readiness says `200` and money movements still fail.

```sql
SELECT r.session_id, r.blocking_session_id, r.wait_type, r.wait_time, t.text
FROM sys.dm_exec_requests r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE r.blocking_session_id <> 0;
```

**No rows means nothing is blocked right now: go back to step 2, not on to step 5.** The query sees
a blocker only while a movement is queued at that instant, and step 5 hands you a session to kill
whether or not one exists. If it returns rows, carry `blocking_session_id` forward.

The chain reads its tail with `UPDLOCK, HOLDLOCK`, so **one** stuck transaction blocks every money
movement in the system, and each waits out essentially the whole hold: a three-second stall delayed
a deposit on an unrelated account by 3,073 to 3,089 ms
(`AuditChainContentionSqlServerTests.OneSlowAuditWrite_DelaysAnUnrelatedAccountsDeposit`). The
number to find is how long the blocker has held the tail, not how many sessions are queued.

**5. Which session is holding it, and for how long?** Ask what holds a lock on `AuditEvents` first.
Unlike step 4 this answers whether or not anything is queued behind it:

```sql
SELECT l.request_session_id, l.resource_type, l.request_mode, l.request_status
FROM sys.dm_tran_locks l
LEFT JOIN sys.partitions p ON p.hobt_id = l.resource_associated_entity_id
WHERE l.resource_database_id = DB_ID()
  AND ( (l.resource_type = 'OBJECT'
         AND l.resource_associated_entity_id = OBJECT_ID('AuditEvents'))
     OR (l.resource_type IN ('PAGE', 'KEY', 'RID', 'HOBT')
         AND p.object_id = OBJECT_ID('AuditEvents')) );
```

Then how long those sessions have held their transaction:

```sql
SELECT st.session_id, dt.transaction_id, at.transaction_begin_time,
       DATEDIFF(second, at.transaction_begin_time, GETDATE()) AS held_seconds,
       dt.database_transaction_log_record_count AS log_records
FROM sys.dm_tran_database_transactions dt
JOIN sys.dm_tran_session_transactions st ON st.transaction_id = dt.transaction_id
JOIN sys.dm_tran_active_transactions at ON at.transaction_id = dt.transaction_id
WHERE dt.database_id = DB_ID()
ORDER BY at.transaction_begin_time;
```

The time comes from `dm_tran_active_transactions` because `dt.database_transaction_begin_time` is
NULL until the transaction writes a log record, and the tail is read before anything is inserted.
`log_records = 0` is the tell: the session is holding the tail without having written anything yet.

**Age says how long a transaction has been open, not what it is holding.** The second query lists
every open transaction in this database, whatever it touched, so its oldest row can be a job that
has nothing to do with `AuditEvents`. Kill only a session that appears in the first query or as a
`blocking_session_id` in step 4, and only then use the age to decide. Two commands, and only the
first one does anything:

```sql
KILL <session_id>;                   -- starts the rollback; returns at once, releases nothing yet
KILL <session_id> WITH STATUSONLY;   -- reports on that rollback; performs no action of its own
```

**`KILL` does not release the tail when it returns.** It starts a rollback, and the locks are held
until that rollback finishes, which can take as long as the work took, or longer. The second
command reports estimated completion and seconds remaining, or answers:

```
Msg 6120, Level 16, State 1
Status report cannot be obtained. Rollback operation for Process ID 56 is not in progress.
```

That is not a failure: the rollback is not running. Either it already finished (the tail is free
and the queue is draining) or it never started, which points at the wrong `session_id`.

**Do not kill more sessions because the first kill "did nothing".** A rollback in progress cannot
be cancelled, and a further kill takes out sessions that were only ever queued behind the first.
Money movements stay refused until the tail is free: that is D1 working, not a second fault.

**6. Is the table itself intact?** A missing table, a broken index on `IX_AuditEvents_Sequence`, or
a failed migration all present as an unreadable store:

```sql
SELECT COUNT(*) FROM sys.tables WHERE name = 'AuditEvents';
SELECT name, is_disabled FROM sys.indexes WHERE object_id = OBJECT_ID('AuditEvents');
```

A dev or test database that is simply behind on migrations fails the same way: see "The dev
database goes stale and EVERY money endpoint answers 500" in `docs/engineering-traps.md`.

**On an environment that has been migrated, a missing `AuditEvents` is not an accident.** Dropping
or renaming the table is the most complete tamper available to anyone holding write access: there
is no chain left to verify, and the verifier reports exit 3, no verdict, not `CHAIN BROKEN`. **Do
not re-run migrations to bring it back.** That recreates an empty table and erases the fact that it
was ever gone, which is the one piece of evidence there is. Establish first whether this
environment was ever migrated:

```sql
SELECT COUNT(*) AS audit_table_present FROM sys.tables WHERE name = 'AuditEvents';
SELECT TOP 3 MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC;
```

A migration history that includes the audit table's migration, with no `AuditEvents` to show for
it, is not a deployment that failed halfway. Preserve the database and escalate.

**The same holds for `AuditAnchors`**, which `verify` reads before it walks a row: without it the
tool exits 3 with `SqlException: Invalid object name 'AuditAnchors'.` Never migrated and removed
are the same exception, and removed is one half of the truncation that nothing here detects
(*What NOT to do*). Re-running migrations recreates an empty table that is indistinguishable from
one that was cleared: check the migration history and any exported copy before anybody runs a
migration.

---

## What NOT to do

**Do not disable the audit chain to restore service.** The bank would then move money it cannot
account for, which is the exact state D1 exists to prevent, and unlike an outage it leaves no trace
that it happened. An outage is recoverable; unaudited movements are not reconstructible.

**Do not raise `Audit:TailTimeoutSeconds` to push the failures away.** The bound is what turns a
thirty-second queue into a fast refusal; raising it makes every movement wait longer for the same
failure, and holds a connection while it does. If the queue is a genuine burst, the fix is
capacity, not patience.

**Do not run `anchor` during an incident. Run `export` instead, and run it first.** `anchor` writes
a row into a table that is evidence; `export` reads it and writes a file (*The five verbs*, below).

**Do not delete rows to "unstick" the table.** Deleting an **interior** row is caught: the next row
records a predecessor that is no longer there. Deleting from the **end** is not caught by the
chain: the surviving prefix links and hashes perfectly, because verification only ever looks
backwards (`AuditChainTests.TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit`). So truncation
is the cheapest attack on this table, it needs **no key at all**, only write access, and it is
also the easiest thing to do by accident while trying to clear a stuck table at three in the
morning.

Three things say how many rows there should be: the `AuditAnchors` table records how far each
verification walk reached and how many rows it held; `verify` prints the UNCOVERED WINDOW, which
goes NEGATIVE when the table is shorter than an anchor claims; `export` writes the anchor chain to
a file that can leave the machine. **None of them closes the gap.** A truncation that deletes the
covering anchor records too is consistent and silent, which
`AuditAnchorSqlServerTests.ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit`
asserts on purpose. They catch the deletion done by somebody who did not know the anchor table was
there, and none is a substitute for a number written down somewhere this machine cannot reach. The
claim ADR-0044 leaves is narrow: the chain detects tampering by someone who holds the database but
not **the key whose epoch that row falls in**, **except at the end of the table**.

---

## Running the verifier

**Run it from the repository root.** The `--project` path is relative to it, and from anywhere else
`dotnet run` fails to find the project and exits **1**, which below means `CHAIN BROKEN`. That
failure happens before the tool starts, so nothing inside it can translate the code.

### The five verbs

- `verify` reads. Safe during an incident, and it is the verdict everything else hangs on.
- `export <path>` reads the anchor table and writes a file: the one step on this page that gets
  evidence somewhere the incident cannot reach, so run it before you touch anything. It prints
  `EXPORTED n anchor records to <path>` and refuses to overwrite: an existing file exits 6 and the
  copy already there is untouched, which is why the name below carries a timestamp. Its exit code
  speaks for the **anchor** chain; it does not verify the audit chain.
  `docs/audit/anchors.sample.jsonl` shows the shape of what lands.
- `anchor` writes a record into `AuditAnchors`: `ANCHOR n recorded` on an intact chain, and a
  `GAP MARKER` over a chain it cannot vouch for, which is honest and is still a new row in the
  evidence, dated during your incident. Leave it alone until the incident is over.
- `evidence <transactionNumber>` reads. Safe. See *The evidence pack*.
- `notify <directory> --contact "<text>"` reads `Users`, `SubscriberNotices` and `AuditEvents`, and
  writes: one file per waiting notice, and `DeliveredAt` on a row once its file is published. It
  touches no audit row and walks no chain, but a run during an incident renders notices from rows
  nothing has vouched for yet. Run it after recovery, after `verify`. See *The PIN notices*.

The export, to a destination that is not this machine:

```bash
dotnet run --project backend/tools/AzureBank.AuditVerifier -- export /mnt/evidence/anchors-$(date +%FT%H%M%S).jsonl
```

### The environment

Every verb needs `Audit:ChainKey`, `Audit:AnchorKey` and the connection string, and after a
rotation the whole ring (next section). Without either key the tool stops before it reads a row and
exits 3, and the line under the headline names the missing setting. From `verify` the headline is
`CANNOT VERIFY: this tool is not configured to read the chain.`

The values travel through the environment and not as command arguments, because on Linux
`/proc/<pid>/cmdline` is world-readable while `/proc/<pid>/environ` is not. **That does not make
them private from your own shell**, which records an `export` line: bash in `HISTFILE`, PSReadLine
in `ConsoleHost_history.txt`, where the sensitive-word filter matches
`password|token|apikey|secret`, none of which is `ChainKey`. Read the values instead of typing
them, the connection string included: it carries database credentials.

### Retiring a key takes three values

The tool and the API hold a **ring** of chain keys and select the one each row names, so a rotation
does not strand history, provided the retired key is in `Audit:RetiredChainKeys`. All three:

- `Audit:RetiredChainKeys:N:Key`: the retired key **material**, not the id a verdict prints. The id
  is 16 hex characters derived one-way from the material: it says which key to fetch and cannot be
  pasted back.
- `Audit:RetiredChainKeys:N:LastSequence`: the **highest `AuditEvents.Sequence` that key
  legitimately wrote**, the tail at the moment the new key took over. Rows above it that name this
  key are refused even when their hash is correct, because that is what minting looks like. There
  is no safe direction to err in: too high re-opens that window and pushes the next key's epoch
  start above rows that key genuinely wrote; too low refuses this key's own rows. Take the number
  from the rotation record. Left out, it binds to `0`, which is refused.
- `Audit:FoundingChainKey`: required as soon as anything is retired. Rows older than the
  key-identity column (`PayloadVersion` = `v2`) record no key, so this says which key wrote them.
  It must name material already in the ring, and it inherits that entry's `LastSequence`.

Through the environment each `:` becomes `__` and `N` is a zero-based index:
`Audit__RetiredChainKeys__0__Key`, `Audit__RetiredChainKeys__0__LastSequence`. **Adding a key you
cannot account for is not a fix**: the ring is how an honest rotation stays verifiable, never a way
to make a verdict go green.

**`Audit:AnchorKey` has no ring: pass the key that wrote the anchors.** Rotating it stops the
anchor chain: under the new key `anchor` exits 6 with `NOT RECORDED ... Broke at anchor: 1`, while
`verify` still exits 0 and prints `UNCOVERED WINDOW: not computed`. The remedy is the old key
(ADR-0044 D7).

Bash, every value read and none typed on the command line:

```bash
read -rsp 'Audit:ChainKey: ' Audit__ChainKey && export Audit__ChainKey && echo
read -rsp 'Audit:AnchorKey: ' Audit__AnchorKey && export Audit__AnchorKey && echo
read -rsp 'Connection string: ' ConnectionStrings__DefaultConnection && export ConnectionStrings__DefaultConnection && echo

# ONLY IF A KEY HAS EVER BEEN RETIRED -- see "Retiring a key takes three values" above.
# Repeat the two RetiredChainKeys lines, incrementing the 0, for each key retired.
read -rsp 'Retired chain key #0: ' Audit__RetiredChainKeys__0__Key && export Audit__RetiredChainKeys__0__Key && echo
read -rp  '  its LastSequence: ' Audit__RetiredChainKeys__0__LastSequence && export Audit__RetiredChainKeys__0__LastSequence
read -rsp 'Audit:FoundingChainKey: ' Audit__FoundingChainKey && export Audit__FoundingChainKey && echo

dotnet run --project backend/tools/AzureBank.AuditVerifier -- verify
```

`LastSequence` is read with `-rp` and not `-rsp` on purpose: it is not key material, and it should
be on screen to be checked.

PowerShell: **`-AsPlainText` on `ConvertFrom-SecureString` is PowerShell 7 or later.** On Windows
PowerShell 5.1 it does not exist and the assignments end up empty, which the verifier reports as a
missing key. Check `$PSVersionTable.PSVersion` first.

```powershell
# PowerShell 7+
$env:Audit__ChainKey = (Read-Host 'Audit:ChainKey' -AsSecureString | ConvertFrom-SecureString -AsPlainText)
$env:Audit__AnchorKey = (Read-Host 'Audit:AnchorKey' -AsSecureString | ConvertFrom-SecureString -AsPlainText)
$env:ConnectionStrings__DefaultConnection = (Read-Host 'Connection string' -AsSecureString |
ConvertFrom-SecureString -AsPlainText)

# Only if a key has ever been retired. Repeat the pair, incrementing the 0, for each one.
$env:Audit__RetiredChainKeys__0__Key = (Read-Host 'Retired chain key #0' -AsSecureString |
ConvertFrom-SecureString -AsPlainText)
$env:Audit__RetiredChainKeys__0__LastSequence = Read-Host '  its LastSequence'
$env:Audit__FoundingChainKey = (Read-Host 'Audit:FoundingChainKey' -AsSecureString |
ConvertFrom-SecureString -AsPlainText)

dotnet run --project backend/tools/AzureBank.AuditVerifier -- verify
```

On Windows PowerShell 5.1, `SecureStringToBSTR` hands back plaintext in unmanaged memory and nobody
frees it. `ZeroFreeBSTR` overwrites the buffer before releasing it, in a `finally` so that an
interrupted `Read-Host` does not leave the allocation behind:

```powershell
# Windows PowerShell 5.1
function ConvertFrom-SecureStringPlain {
    param([Security.SecureString]$Secure)
    $bstr = [IntPtr]::Zero
    try {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
        [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    }
    finally {
        if ($bstr -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
        }
    }
}

$env:Audit__ChainKey = ConvertFrom-SecureStringPlain (Read-Host 'Audit:ChainKey' -AsSecureString)
$env:Audit__AnchorKey = ConvertFrom-SecureStringPlain (Read-Host 'Audit:AnchorKey' -AsSecureString)
$env:ConnectionStrings__DefaultConnection = ConvertFrom-SecureStringPlain (
    Read-Host 'Connection string' -AsSecureString)

# Only if a key has ever been retired. Repeat, incrementing the 0, for each one.
$env:Audit__RetiredChainKeys__0__Key = ConvertFrom-SecureStringPlain (
    Read-Host 'Retired chain key #0' -AsSecureString)
$env:Audit__RetiredChainKeys__0__LastSequence = Read-Host '  its LastSequence'
$env:Audit__FoundingChainKey = ConvertFrom-SecureStringPlain (
    Read-Host 'Audit:FoundingChainKey' -AsSecureString)

dotnet run --project backend/tools/AzureBank.AuditVerifier -- verify
```

**None of this makes the value private from the process**: an environment variable is readable by
anything running as you, and `-AsPlainText` returns a managed string that the GC may copy.

**On a rotated deployment the ring lines are not optional: without them this procedure accuses an
intact chain.** Given only the new `ChainKey` and the `AnchorKey`, `verify` exits 1 on untouched
rows: `CHAIN BROKEN at sequence 1`, the row "was written under key id ... and no key in this
verification's ring has that id". Add the retired key and its `LastSequence` without
`Audit__FoundingChainKey` and it exits 3, `CANNOT PROCEED`, naming the setting to add. Once all
three are set it exits 0.

### What a verdict says

`verify` prints `CHAIN INTACT` with the row count and the sequence range, `NOTHING TO VERIFY` for
an empty table, or `CHAIN BROKEN at sequence n`. "Intact" alone is not an answer: a chain of zero
rows links perfectly, which is why an empty table has a verdict and an exit code of its own.
**Compare the count against what you expected.** A chain that verifies 40 rows where yesterday it
had 40,000 is intact and catastrophic, and only the numbers say so.

Under the verdict it prints an **UNCOVERED WINDOW**: how far the table runs past the deepest
sequence any anchor claims to cover. Three readings of it are wrong:

- **Zero does NOT mean you are covered.** The deepest claim reaches the tail at that instant, which
  one row written a moment later undoes. The line says "at least" for that reason.
- **It is not a freshness measure.** Nothing here schedules an anchor, so the window has no ceiling
  and a small number now says nothing about tomorrow. A missing anchor is not evidence of anything.
- **NEGATIVE is the one to stop for.** The anchors claim coverage through a sequence that no longer
  exists, and nothing legitimate produces it. Preserve the database, `export`, and escalate before
  running anything that writes.

It is counted in sequence numbers. That is a row count unless somebody holding **the keys covering
a stretch** removed rows from it and recomputed the links behind them: the walk checks the links,
never the contiguity. So a `SELECT COUNT(*)` that disagrees with the span is the finding, not a
fault in the tool.

### Exit codes, for scripting it

**0** intact, **1** broken, **2** nothing to verify, **3** no verdict (the store could not be
read), **4** the command line was wrong, **5** interrupted, **6** there was a verdict but nothing
could be recorded from it. Only 0, 1 and 2 are statements about the chain.

- **`6` means the chain was read and the write failed**: a refused `anchor`, an `export` to a path
  that already exists or cannot be written. A statement about the operation, not the bank.
- **`evidence` and `notify` add no code.** `evidence` exits with the chain's verdict, and a number
  the store does not hold is 4. `notify` walks no chain: from it 0 and 2 are statements about
  notices, 6 means at least one is still owed, and 1 never occurs.
- **The codes do not separate the audit chain from the anchor chain.** `0` from `verify` does not
  mean the anchor chain verified: `verify` returns intact on the row walk alone and prints
  `UNCOVERED WINDOW: not computed -- the anchor chain did not verify.` under it, so an anchor
  record failing its own MAC reaches a script as green. Read that block, not just the verdict
  line: anything other than a number there is something to act on. `1` from `export` is about the
  anchor chain, and `THE ANCHOR CHAIN DID NOT VERIFY` on an `export` run is a verdict, not a note
  about the file: the copy is written anyway, as evidence of the broken state, and the kind and
  the anchor sequence under that line are the finding. No alert on `verify` covers the anchor
  chain.
- **`3` is the one to wire an alert on separately from `1`.** It covers everything that stopped
  the walk before a verdict: a missing or too-short `Audit:ChainKey` or `Audit:AnchorKey`; an
  unreachable server; a malformed or absent connection string; the states of step 6; and every way
  the key ring refuses to build, ten guards: a blank or under-32-character `Audit:ChainKey`; an
  `Audit:RetiredChainKeys:N` entry that is null, blank, or under 32 characters; a `LastSequence`
  below 1, at the top of the range, or equal to the entry before it; the same key listed twice or
  equal to the current one; and `Audit:FoundingChainKey` missing once anything is retired or naming
  material the ring does not hold. All five verbs answer a ring refusal with
  `CANNOT PROCEED: this tool is not configured to read the chain`; a per-entry refusal names
  `Audit:RetiredChainKeys:N` by its **configuration** index, not its position after the boundary
  sort. An unreadable store is `CANNOT VERIFY` from `verify` and `NO VERDICT` from `anchor` and
  `export`, with the exception on the next line: read it, it names the cause.
- **A wrong key is not a `3`.** A well-formed key that is not the one the chain was written with
  passes every check the tool can make, so the walk runs and exits **1**: see *If the verifier
  reports CHAIN BROKEN*.
- **`5` is never evidence that nothing is wrong.** Somebody stopped the walk, so there is no
  verdict; and the branch is selected by the cancellation token, not by what threw, so a store
  that failed while the token was already signalled reads as an interruption too. **If it was
  stopped because it appeared to hang, the hang is the finding.** Otherwise re-run it.
- **`4` covers every parse failure**: running the tool with no arguments answers 4, not 1.

### When the key ring is wrong

**The API refuses to start on a ring it cannot build.** `AuditKeyRingStartupCheck` resolves the
chain during startup, so with a retired key and no `Audit__FoundingChainKey` the log shows
`Hosting failed to start` with an `AuditKeyRingException` that names the setting, then
`Application terminated unexpectedly`. There is no `Now listening on` and no `Application started`,
the port never opens, and the process exits **1**, as for any refused start
(`FailedStartupExitCodeTests.AHostThatRefusesToStart_EndsTheProcessWithOne`). Alert on the exit
code or on the absence of `Application started`; the second also catches a start that hangs.

**A ring that builds can still be wrong, and a clean startup is not evidence that it is right.**
Run `verify` after every change to it. "Not checked" below is `CHAIN BROKEN` on a row whose hash
was never recomputed (`UnknownScheme` in the code):

| What is wrong | `verify` answers | Where it breaks |
| --- | --- | --- |
| the key that wrote a row is not in the ring | not checked | lowest row that key wrote |
| a `LastSequence` is too LOW | not checked | first row above the recorded boundary |
| `FoundingChainKey` names the wrong ring member | not checked | lowest `v2` row |
| a `LastSequence` is too HIGH, over-claimed range still empty | **`CHAIN INTACT`** | nowhere |
| a `LastSequence` is too HIGH, newer key wrote in that range | not checked | that key's first row |
| two retired keys share a `LastSequence` | refused at construction | before any row is read |

The two `too HIGH` rows are one misconfiguration: the next key's epoch starts at this one's
`LastSequence + 1`, so over-claiming pushes it above rows the newer key genuinely wrote. Which
answer you get depends on whether the range above the real boundary is still empty:

```
retired key truly stopped at 2, boundary recorded 4
  the newer key has already written rows 3 and 4  ->  IsIntact=False  UnknownScheme  breaks at 3
  nothing written above 2                         ->  IsIntact=True   nothing reported
```

**The silent state is not the harmless one.** It is the window an attacker needs: the range is
empty, so a holder of the retired key can mint into it and the walk accepts every row, and a
deployment is in that state for as long as it takes the new key to write. The loud state is the
same mistake once the system is in use: rows the current key wrote are refused, which reads like
tampering and is not. So "run it and see" is not a check on the ring before anything has been
written under it. Only the rotation record is.

---

## If the verifier reports CHAIN BROKEN

**A break has two families of cause and they need opposite responses, so classify before you
act.** Either somebody with write access changed or removed an audit row, or something ordinary
produced the same verdict: a key or a ring that is not the one those rows were written with, or a
deletion of the oldest rows from outside the application: an archival job, a manual cleanup, a
partial backup restored. A wrong key happens on any deploy or rotation that exports the wrong
value: rule it out first. The deletion is not routine: this application never deletes an audit row
(ADR-0044 D6), so it is benign only if you can name the job or the restore that did it. It is loud
by design: `AuditChainTests.DeletingTheOLDESTRows_IsLOUD_WhichIsWhyRetentionCannotPurgeThisTable`.

**Classifying costs one command.** The verifier only reads, so running it again destroys no
evidence. The line under `CHAIN BROKEN` says what kind of break it is, and the next two give
`Rows verified before the break` and `Sequences read`.

- `does not match its own hash`, **0 rows verified, on a row that records no key identity
  (`PayloadVersion` = `v2`)**: suspect the key before an attacker. A wrong key is well-formed, so
  nothing rejects it, and it mismatches on the first row it reads, which is always sequence **1**.
  The key applied to such a row is `Audit:FoundingChainKey`, which is `Audit:ChainKey` only while
  nothing has been retired. Settle it by re-running with the key this deployment actually keeps.
- the same verdict **on a row that names its key (`v3`)**: a write, and the key is already ruled
  out, because such a row is checked against the identity it records before its hash is
  recomputed. Preserve the table and escalate.
- the same verdict **at any sequence above 1** with 0 rows verified: not the key. A row above 1
  that records no predecessor got there by a write, the cheapest way to hide a deleted prefix.
- the same verdict **with a non-zero count**: an alteration at that row. Only on a `v2` row can it
  also be different key material, and the tool cannot tell the two apart there.
- `expected to follow ... A row was deleted, reordered, or inserted`, **0 rows verified**: at
  **sequence 1** nothing was removed: the row is the start of the chain, so the predecessor it
  records was written onto it, and only an update does that. Above sequence 1 the rows below it
  are gone. An archival job, an attacker and a wrong key all print that identical line, so it is
  housekeeping only if you can name the job. Preserve the table either way.
- the same verdict **with a non-zero count**: a row is missing or out of place in the middle,
  which removing the oldest rows cannot do. That one is the real thing.
- `could not be read at all`: a stored value contradicts the schema, which is itself a
  modification. Neither a wrong key nor a deletion can cause this.
- `declares payload version ... which this build cannot render`, `was written under key id ...`,
  or a boundary verdict: the row was **NOT CHECKED**, which is never the same as checked and found
  good. The exit code is still 1.

**An unchecked row has seven causes, and the verdict line says which.** The report lists them, and
how wide the damage is depends on the cause:

- **Row-local**: this build cannot render the version the row declares, or the identity column
  contradicts the version (a `v2` row carrying a key id, a `v3` row carrying none). No epoch and no
  key are involved, and the rows around it are untouched. A `v3` row with no key id is a
  modification: do not look for a missing ring entry.
- **A whole epoch**: no key in the ring has this row's id, so the walk stops at the first row that
  key wrote. When the missing key is the oldest, that is row 1 with nothing verified: the ordinary
  shape of "rotated once and forgot to retire the old key". **A configuration mistake and a write
  can look identical in one run.** What separates them is a second run with that key in the ring
  and all three values above: supply fewer and the ring refuses to build, exit 3, with neither
  reading confirmed. A configuration miss clears. A write does not.
- **Outside an epoch**, the four boundary causes: the row sits above or below the epoch of the key
  it names, or of `Audit:FoundingChainKey` when it names none. The epoch in the verdict says where
  the key was valid, never where the damage is.

**Two of the seven are fixed in configuration: a missing ring entry, and the founding designation.
None of it makes the row proved good.** An overwritten `PayloadVersion` or `KeyId` is not an eighth
cause: both are inside the hashed payload, so changing either surfaces as one of the seven. For the
boundary causes, which edge was crossed decides which edit is even available:

- **Above an epoch** ("retired at sequence N"): either the recorded `LastSequence` is too LOW and
  the row is genuine history, or the row was **minted with a retired key after the rotation**,
  which is the attack the boundary exists to catch. On a `v2` row minting is the leading reading:
  labelling a new row `v2` is the one way to reach the founding key without naming it.
  **Raising `LastSequence` can turn the verdict green under either reading, and going green is
  not evidence that the benign reading was the true one.** If the row was minted, raising the
  boundary completes the attack and the trail then attests to it. Establish which reading is true
  from something outside this database (the change record for the rotation, the deployment that
  carried it, the ticket that ordered it) and only then correct the configuration. If nothing
  outside can say when the key was retired, the row cannot be verified, and that is a finding, not
  a configuration task.
- **Below the epoch of the key it names** ("whose epoch begins at N"): an earlier key wrote that
  stretch. Either the earlier key's `LastSequence` is recorded too HIGH, or the rows were
  **re-authored by whoever holds the later key**. Epochs are derived from the boundaries, so moving
  one moves two: establish the rotation points from outside this database before editing any.
- **An identity-less row below the founding epoch**: `Audit:FoundingChainKey` designates a ring
  member that is not the oldest. Re-point the designation; no `LastSequence` edit is the fix,
  because the epoch's start cannot fall below the designation's position in boundary order (**2**
  for the second key, **3** for the third), and if an edit does clear the verdict the trail does
  not begin at sequence 1, which is a second finding. A row stored **below sequence 1** has its own
  verdict text and no configuration cause: preserve the table and escalate.

**From the moment it is none of the ordinary causes, the table is evidence: do not repair it and
do not write to it.** A repair destroys the only record of what was done to it, and the chain
cannot be "fixed": recomputing hashes requires the key, which an attacker who reached the database
is assumed not to have. **Capture, in this order, before anyone touches the database:**

1. the verifier's full output, with the exit code **and the UNCOVERED WINDOW block under it**;
2. `export <path>` to a file outside this machine, the only step here that survives the machine;
3. the sequence the verdict names and the rows on either side of it (the query below);
4. the total row count;
5. the SQL Server default trace or audit for recent writes to `AuditEvents`, if it is enabled.

```sql
DECLARE @n bigint = 0; -- the sequence the verifier names
SELECT * FROM AuditEvents WHERE Sequence BETWEEN @n - 2 AND @n + 2;
```

**Then escalate; do not continue.** Whether to keep taking traffic on an instance whose audit
trail is proven altered is not an operational call. It is the decision ADR-0044 D1 exists to make
possible, and it belongs to whoever owns the incident. This runbook stops here on purpose.

---

## After recovery

- **Verify the chain** (*Running the verifier*) and compare the count against your own.
- **The refused movements were refused, not lost**: no money moved, and no audit row claims it
  did. Customers can simply retry.
- **If the cause was contention and not an outage**, capture how long the tail was held, and by
  what. That is the input to deciding whether the chain's single global tail needs to stop being
  single, a question ADR-0044 leaves open.

### The evidence pack (PSD2 Art. 72)

For one movement, `evidence` answers two questions: was it strongly authenticated, and is the
record of it intact?

```bash
dotnet run --project backend/tools/AzureBank.AuditVerifier -- evidence TXN-20260902-0000000101X
```

The argument is the `TXN-…` number the movement response returned, for a transfer or a withdrawal.
It prints one pack: the ledger row, the step-up authorisation that paid for it, the audit rows
that name it, and the chain verdict `verify` would print. `EVIDENCE PACK for <number>` is the first
line of every successful assembly. **The exit code is the chain's**: a pack over a broken chain
prints the pack and exits **1**. `evidence` does not read the anchor table: take the
UNCOVERED WINDOW from `verify`, not from the block under the pack's verdict.

What the second section of the pack can say:

- `STRONGLY AUTHENTICATED, BOUND IN THE CHAIN`: what a movement prints when nothing is wrong, for
  transfers since 2026-09-14 and withdrawals since ADR-0056. The chained `MoneyTransferred`,
  `MoneyTransferredInternally` or `MoneyWithdrawn` row names the authorisation it consumed
  (`Detail` is `{"authorizationId":"<id>"}`, under the hash) and the authorisation row agrees: it
  is `Consumed` with an instant of spending, it points back at this movement, and it was minted by
  this account's owner for this operation. The name is inside the chain; the instants under the
  line are read from the unchained table.
- `BOUND AUTHORISATION MISSING`: the chained row names an authorisation and no such row exists.
  Something paid, and the record of when the PIN was proved and spent is gone: a write around the
  application, or a purge. The chain verdict stays intact, because that table was never inside it.
  A finding. If a different row claims the movement, the chained name is the one to believe.
- `BOUND AUTHORISATION DOES NOT MATCH`: the named row exists but says it paid for another
  movement, records no movement, is not `Consumed`, was minted by another user or for another
  operation, or is marked spent with no instant. The lines under it say which. A finding.
- `STRONGLY AUTHENTICATED`, with no "bound": a consumed authorisation names this transaction and
  the movement's success row names none. **That authorisation row is not inside the chain**: a
  successful mint writes no audit row, so the second factor is vouched for by a mutable table.
  Printed only for a pre-binding row (written before the dates above), for a `Detail` the tool
  cannot read (`Bound authorisation: UNREADABLE`, a finding), or when no Succeeded movement row
  names the movement. The pack says which under the line.
- `NOT STRONGLY AUTHENTICATED`: in those same three cases, no consumed authorisation names the
  movement. Neither a transfer (ADR-0042) nor a withdrawal (ADR-0056) is accepted without one, so
  either the movement predates the rule or the row that paid for it is gone, and because that
  table is unchained its absence leaves no break for `verify` to find. A finding.
- `NO AUTHORISATION APPLIES`: a deposit, or the incoming leg of a transfer. Ask for the outgoing
  leg's number.
- `NO AUDIT ROW`: a ledger row with no audit row naming it. Every money movement writes one in the
  same transaction as the ledger row (D1), so this is a movement recorded without its record. A
  finding whatever the chain verdict says.

An intact verdict under the pack says the rows are in a chain that verified at that instant. **It
is not an inclusion proof**: the anchor is a tail hash, not a tree, so proving to a third party
that one row is in the set means handing over the range
(`docs/audit-trail-against-real-practice.md`).

Refusals, none of them about the chain: `NOT ASSEMBLED` is **4**, a blank argument or a number the
store does not hold; `CANNOT ASSEMBLE` is **3**, the same causes as `CANNOT VERIFY`; `INTERRUPTED`
is **5**.

### The PIN notices (NIST SP 800-63B-4 §4.1.2.1, ASVS 4.0.3 2.5.5)

When a PIN is enrolled (ADR-0045) or changed (ADR-0047), the API records, in the same save as the
action and its audit row, that the account holder is owed a notice. Nothing in the API sends one.
A hosted runner delivers them to a pickup directory when `Notices:Runner` names it, the API's
relay (ADR-0048) or the Function (ADR-0051), and this verb then steps aside for every row held
under a live lease and says how many. Run it when no runner is live, or when one is down: a lapsed
lease is free to the verb. It renders every owed notice as one RFC 5322 `.eml` file, addressed to
the email held on the account, into a directory you name, and marks the row delivered:

```bash
dotnet run --project backend/tools/AzureBank.AuditVerifier -- notify ../azurebank-notices \
  --contact "security@your-bank.example, +00 000 0000"
```

The directory must exist (the verb never creates one) and must be **outside any git repository**,
where it sits and where a link on its path points: a spool of addresses is one commit away from
being published. `--contact` is what a recipient uses to say "this was not me"; NIST §4.6 makes it
mandatory content, so nothing is rendered without it. What that contact can do is
`docs/runbooks/pin-enrolment-repudiated.md`.

**A file in a pickup directory has reached the edge of this machine and nobody else.** Nothing
here sends: a collector pointed at the directory, or a person, is what moves it, and neither exists
in this deployment (`docs/deferred/relaying-the-enrolment-notice.md`). The address never appears on
the console, in a receipt or in a file name; it is in the file, in the `To:` header. After the
demonstration, delete the spool: it holds addresses in clear.

The headlines, and what each means from this verb:

- `NOTIFIED n of m waiting notices into <directory>`: the summary line, first. Exit **0** when
  every waiting notice was written and marked; **6** when at least one is still owed, with a line
  per notice saying why. A marked notice is never rewritten: fix what the line names and run again.
- `NOTHING TO NOTIFY`: exit **2**, and the line says which of two readings: no notice is owed, or
  every owed notice is held under another runner's live lease. The verb names the kind of holder
  from `LeasedBy`: `api`, `func` or `verb` (ADR-0051). **The count is complete; the names are
  not.** A holder whose name begins with none of those three is counted and not echoed, which
  means somebody wrote that row by hand. Read the column yourself:

  ```sql
  SELECT LeasedBy, LeasedUntil FROM SubscriberNotices
  WHERE DeliveredAt IS NULL AND LeasedUntil > SYSUTCDATETIME();
  ```

  **A live lease is not a live process.** A runner that claimed and then died keeps its rows
  until the lease lapses, and looks identical to one mid-delivery. The remedy is the lapse.
- `NO ADDRESS`: that notice's account holds no email, or one that cannot head a message because it
  contains a line break. Nothing is rendered for it and it stays owed.
- `NO AUDIT ROW backs notice …`: the audit row the notice belongs to could not be found, and the
  line says which question was asked (ADR-0052), naming the notice's own event. For a change, the
  exact one is "the `PinChanged` row it names is gone, or is not this notice's". The weaker one is
  "no `PinChanged` row exists for that user at all", asked only of a notice that names no row. The
  notice is rendered anyway, and the absence is the finding. Read the notice row first, then the
  `AuditEvents` row by its id when the notice names one and by the `(ActorUserId, Event)` pair
  otherwise, never by the actor alone: `docs/runbooks/pin-enrolment-repudiated.md` §1 has the
  statements. Run `verify` when the named row is gone; a row that is there and does not match is
  the notice's problem, and `verify` comes back clean. `evidence` does not apply: a PIN event has
  no `TXN-…` number.
- `NOT NOTIFIED`: before the store is touched, exit **4**: no contact, not a directory, a
  directory that does not exist, or one inside a git working tree. Per notice, counted toward
  **6**: the file could not be written (the exception type is named, never its message, which can
  echo the address), or the build cannot render that event. `NOT NOTIFIED by this run` means
  another run marked the row while this one wrote the file: that file is a duplicate.
- **An orphan file**: a complete `.eml` whose row is still owed, left when a run is interrupted or
  the store refuses the mark after the file was published. Every later run refuses to overwrite it
  (`NOT NOTIFIED … (IOException)`, exit **6**). The row is the truth: delete or move the orphan and
  run again, or mark the row by hand, as §2 of `docs/runbooks/pin-enrolment-repudiated.md` clears
  a PIN by hand. A file is staged as `<reference>.eml.<run>.partial` and moved into place only
  when complete, so an orphan is never truncated, and a leftover `.partial` can be deleted.
- `LEASE LAPSED`: the verb claims what it renders, under its own name and a two-minute lease
  (ADR-0048), batch after batch. A run too slow to finish inside that lease stops delivering and
  says how many it did not reach; those rows are free to the next claim. Exit **6**. Run again.
- `CANNOT NOTIFY`: exit **3**: the tool is not configured, or the store could not be read or
  written. Not a statement about any notice. A ring that will not build is `CANNOT PROCEED` here
  too.
- `INTERRUPTED`: exit **5**. Notices written and marked before the interruption stay marked.
