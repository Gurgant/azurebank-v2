# Runbook — the audit chain is unavailable

**Symptom:** deposits, withdrawals and transfers are failing. Reads still work. Sign-in still works.

**Why this runbook exists:** ADR-0044 D1 makes an audit row atomic with the money movement it
describes, so an audit store that cannot be written stops the bank moving money. That is deliberate —
a movement that was never recorded is one that cannot afterwards be accounted for to the customer
whose money moved. Every source that endorses fail-closed pairs it with a runbook. This is ours.

---

## Confirm it in one call

```bash
curl -s -w "\n%{http_code}\n" https://<host>/health/ready
```

Measured on the running API, with the `AuditEvents` table renamed away underneath it:

```json
{"status":"Unhealthy","checks":[
  {"name":"database","status":"Healthy","description":null},
  {"name":"audit-chain","status":"Unhealthy",
   "description":"audit store unreadable — money movements will be refused (ADR-0044 D1)"}]}
```

It answers rather than hanging, which is not free: every readiness check is registered with
`Audit:TailTimeoutSeconds` as its timeout. Unbounded, this probe took **36,800 ms** against an
unreachable store — long past the point where anything asking had given up.

**That bound is cooperative, not enforced, so a hang does not mean somebody removed it.** The
framework links a `CancellationTokenSource` and calls `CancelAfter`, which only SIGNALS a token:
nothing abandons the running check, and the service awaits it unconditionally. A check that ignores
the token it was handed is bounded by nothing and takes `/health/ready` down with it, while the
registration still reads as correctly configured. So on a hang look at both — that
`Audit:TailTimeoutSeconds` still reaches every registration tagged `ready`, AND that each of those
checks threads its `CancellationToken` into every await. The two registered today do; one added
later inherits the bound and none of the obligation.

**The body is the point, and it is why the `curl` above does not discard it.** It names WHICH check
failed — exactly the distinction step 1 asks you to make. Note what that observation shows: the
database was perfectly healthy while the audit store was not, so a bare `503` would have sent you
hunting a database outage that was not happening.

`503` with `audit-chain` reporting **unhealthy** does NOT mean the store is unreadable. It means the
probe could not certify that an audit row can be appended, and it reports on three axes of which
reading is only one — **so read the description, which names the axis.**

- `audit store unreadable` is the read failure, and unreadable is not unreachable: a database that
  is down, an `AuditEvents` table that was never migrated, a disabled or corrupt index, or
  credentials that can no longer read it. Step 6 if it is the table, step 3b if it is the login.
- `audit store readable but NOT writable` is a store that answers every read and refuses every
  append. Step 3, and not an outage at all.
- `A timeout occurred while running check.` is the framework's wording, and is step 2.

`200` means the probe read the table AND found an append permitted, so the cause is one of the
narrower ones below.

## What you will see in the log

```
SecurityEvent AuditChainUnavailable: the audit chain tail could not be read within 5s,
so 1 pending audit row(s) and the action they describe are refused ON THIS ATTEMPT.
A transient fault may be retried by the execution strategy and then succeed, so a
single line is a blip and repetition is an outage
```

**Read the count before you read the line.** The audit chain runs inside EF's retrying execution
strategy, which re-runs the whole save on a transient fault. So ONE of these, followed by the
customer's movement succeeding, is a retried blip and not an incident — the alert belongs on
repetition. A steady stream of them, with movements failing, is this runbook.

That line is logged and writes **no audit row**, which is not an oversight. Every other refusal in
this system reaches `RecordRefusalAsync`, which opens its own connection so a rollback cannot erase
it — but that writes to `AuditEvents` and therefore takes the very lock that just failed. **For a
chain failure the chain cannot be where you report it.** The log line and the health check are the
whole signal.

---

## Triage, in order

**1. Is the database reachable at all?** If the body you just printed also reports `database`
unhealthy, this is not an audit problem — it is the database, and the audit chain is simply the
first thing to notice. Treat it as a database outage.

**2. Does it say `A timeout occurred while running check.`?** That wording is the framework's, not
ours, and it means the check was cancelled rather than that it found anything. Usually the readiness
budget (`Audit:TailTimeoutSeconds`) elapsed — but **it does not prove that**: `DefaultHealthCheckService`
reports that same description for ANY cancellation escaping a check, including on a registration with
no timeout at all. Treat it as "the probe did not finish".

**Steps 4 and 5 are not where to look, and this step has to say so.** The probe reads the tail
`WITH (READUNCOMMITTED)`: it takes no lock and waits on none, so the writer's `UPDLOCK, HOLDLOCK` on
the tail row is invisible to it. A locked tail and a stuck transaction report `200` — that is step
4's case, the opposite of this one — so sending a timeout down there ends in a `KILL` on a session
that cannot have caused it. What is left, in order: whatever polls this endpoint may carry a shorter
deadline than the budget, which is not a store fault at all; then the store, unreachable or simply
answering slower than `Audit:TailTimeoutSeconds` (step 1); then the table (step 6). A permission
problem does not time out.

**3. Does it say `readable but NOT writable`?** Then the store answers every read and refuses every
audit row, so D1 refuses every money movement while the database looks perfectly healthy. **Those
five words are the shared prefix of TWO verdicts that need opposite fixes, so read the rest of the
sentence before you touch anything.** The probe tests
`DATABASEPROPERTYEX(DB_NAME(), 'Updateability')` BEFORE it asks `HAS_PERMS_BY_NAME`, so on a
read-only database the permission question is never asked at all — and a `GRANT` hunt there finds a
clean permission graph while every movement stays refused.

- `... — the database is not READ_WRITE, so money movements will be refused` → **3a**.
- `... Check INSERT permission on AuditEvents for this login` → **3b**.

**3a. The database is not READ_WRITE.** Run this through the API's OWN connection string, not from a
window pointed at the listener with default intent: a default-intent session lands on the primary
and reports `READ_WRITE`, which answers about a different replica than the probe asked.

```sql
SELECT DB_NAME()                                      AS database_name,
       @@SERVERNAME                                   AS answering_instance,
       DATABASEPROPERTYEX(DB_NAME(), 'Updateability') AS updateability,
       d.state_desc, d.is_read_only, d.is_in_standby, d.replica_id
FROM sys.databases d
WHERE d.database_id = DB_ID();
```

Three states, three remedies, none of them a `GRANT`:

- **`is_read_only = 1`, `is_in_standby = 0`, `replica_id` NULL** — somebody ran
  `ALTER DATABASE [db] SET READ_ONLY`: a release script, a maintenance job. `SET READ_WRITE`
  reverses it and needs exclusive access. Find out who set it, or it is set again an hour later.
- **`is_in_standby = 1`** — a restore finished `WITH STANDBY`: a log-shipping secondary, or one
  nobody brought online. `RESTORE DATABASE [db] WITH RECOVERY` does make it writable and **ends the
  restore sequence**, so no further log backup can ever be applied to it. If this is the DR copy,
  that command spends the DR copy to end a short outage — and if the API is pointed at a DR copy at
  all, the connection string is the fault. Confirm what it is first:
  `SELECT TOP 5 restore_date, restore_type FROM msdb.dbo.restorehistory
  WHERE destination_database_name = DB_NAME() ORDER BY restore_date DESC;`
- **`replica_id` NOT NULL** — an availability-group replica. Ask which role it holds:

```sql
SELECT ars.role_desc, ar.replica_server_name
FROM sys.databases d
JOIN sys.dm_hadr_availability_replica_states ars ON ars.replica_id = d.replica_id
JOIN sys.availability_replicas ar ON ar.replica_id = ars.replica_id
WHERE d.database_id = DB_ID();
```

`SECONDARY` separates two different incidents. If the primary MOVED, point the API back at the
listener. If it never moved, read-only routing sent the API here — its connection string carries
`ApplicationIntent=ReadOnly`; drop it. **Do not force failover to make this node writable.** Those
two DMVs need `VIEW SERVER STATE` and do not exist on Azure SQL Database, where the link state is
`sys.dm_geo_replication_link_status` — and where this same verdict also appears when a database
reaches its size limit, which is worth ruling out first: `AuditEvents` is the fastest-growing table
here and nothing ever deletes from it.

A restore left `WITH NORECOVERY` never reaches this description: that database is `RESTORING` and
cannot be read, so the probe says `audit store unreadable` and you are in step 6.

**Re-read `/health/ready` after the fix instead of assuming it is over.** Updateability is tested
first, so it hides the permission answer completely: a denied INSERT can be queued behind it and
appears only on the next probe.

**3b. The login cannot INSERT.** `HAS_PERMS_BY_NAME` honours role membership and `DENY`, but it
answers for the CURRENT security context — so an incident responder connected as `sysadmin` or
`db_owner` is asking about the wrong principal and gets `can_insert = 1` for a login that is denied.
Impersonate, and make the answer carry the principal it answered for:

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
an impersonation context is a silent no-op, so it is safe to leave in. The object name is resolved
rather than typed as `dbo.AuditEvents` for the same reason the probe resolves it: under a non-`dbo`
schema the hardcoded form asks about a table that does not exist, returns NULL, and puts you and the
health check in disagreement.

Then find the `DENY` itself:

```sql
SELECT dp.name, dp.type_desc, p.permission_name, p.state_desc
FROM sys.database_permissions p
JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
WHERE p.major_id = OBJECT_ID('AuditEvents');
```

That lists only EXPLICIT object-level permissions, so a `DENY` inherited from a role —
`db_denydatawriter` above all — does not appear in it at all; read the API user's role membership
too. A `DENY` beats any `GRANT`, including one inherited from `db_datawriter`; that is the usual
cause, and it is invisible from the grant side alone. **Do not fix this by granting the API more
than INSERT on `AuditEvents`**: the chain is append-only by design, and a login holding UPDATE or
DELETE there is a larger problem than the outage you are ending.

**4. Is the table locked rather than unreachable?** The health check reads with `READUNCOMMITTED`, so
it stays healthy while the tail is merely locked by a slow writer. That is the case where readiness
says `200` and money movements still fail.

```sql
SELECT r.session_id, r.blocking_session_id, r.wait_type, r.wait_time, t.text
FROM sys.dm_exec_requests r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE r.blocking_session_id <> 0;
```

**If that returns no rows, nothing is blocked right now: go back to step 2 rather than on to step
5.** It only sees a blocker while a movement happens to be queued at the instant you ran it, and
step 5 will hand you a session to kill whether or not one exists. If it does return rows, carry
`blocking_session_id` forward — that is the session actually holding the tail.

Look for sessions blocked on `AuditEvents`. The chain reads its tail with `UPDLOCK, HOLDLOCK`, so
**one** stuck transaction blocks every money movement in the system — measured: a three-second stall
delayed a deposit on an unrelated account, by an unrelated user, by 3,073–3,089 ms across three runs.
The unrelated movement waits out essentially the entire hold, so the number to find below is how long
the blocker has held it, not how many sessions are queued.

**5. Which session is holding it, and for how long?** Ask what holds a lock on `AuditEvents`
first — unlike step 4 this answers whether or not anything is queued behind it:

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

⚠️ **The time comes from `dm_tran_active_transactions`, and it did not used to.** This query read
`dt.database_transaction_begin_time`, and that column is **NULL until the transaction writes a log
record** — while the tail is read under `UPDLOCK, HOLDLOCK` **before** anything is inserted
(`AuditChain.cs:356`). So on exactly the blocked writer step 4 constructs, the age column was blank,
`held_seconds` was blank, and `ORDER BY` ordered nothing. Measured 2026-08-28, one session holding
that same statement in an open transaction:

```
dm_tran_database_transactions.begin_time = *** NULL ***   log_records = 0
dm_tran_active_transactions.begin_time   = 2026-08-28 19:58:36.663
```

`log_records` is kept in the output because it is the tell: **0 means the session is holding the tail
without having written anything yet**, which is the shape this runbook is about, and it is also why
the old column had nothing to say.

**Age says how long a transaction has been open. It does not say what it is holding.** This runbook
used to run the second query alone, unfiltered, and call the oldest row the usual culprit — but
`sys.dm_tran_database_transactions` is instance-wide and returns a row per transaction PER DATABASE,
`tempdb` included, so at three in the morning the top row is routinely a nightly job in another
database that has nothing to do with this table. Kill only a session that appears in the FIRST query
or as a `blocking_session_id` in step 4, and only then use the age to decide.

**`KILL` does not release the tail when you press
enter** — it starts a rollback, and the locks are held until that rollback FINISHES. Undoing the work
can take as long as doing it took, or longer. This runbook used to say "killing it releases the tail
and the queue drains", which would have had you standing over a queue that looked stuck after you had
already fixed it.

End it, then watch the rollback rather than guessing at it — two commands, and only the first one
does anything:

```sql
KILL <session_id>;                   -- starts the rollback; returns at once, releases nothing yet
KILL <session_id> WITH STATUSONLY;   -- reports on that rollback; performs no action of its own
```

That reports estimated completion and seconds remaining. It performs no action of its own — it only
reports on a rollback already under way. **Measured**, so the error does not surprise you mid-incident:

```
Msg 6120, Level 16, State 1
Status report cannot be obtained. Rollback operation for Process ID 56 is not in progress.
```

That is not a failure. It means the rollback is not running — either it already finished (the tail is
free, and the queue is draining now) or it never started, which points at the wrong `session_id`.

**Do not kill more sessions because the first kill "did nothing".** A rollback in progress cannot be
cancelled, so a second kill buys nothing and a third takes out sessions that were only ever queued
behind the first. Money movements stay refused until the tail is free; that is D1 working, not a
second fault appearing.

**6. Is the table itself intact?** A missing table, a broken index on `IX_AuditEvents_Sequence`, or a
failed migration all present as an unreadable store:

```sql
SELECT COUNT(*) FROM sys.tables WHERE name = 'AuditEvents';
SELECT name, is_disabled FROM sys.indexes WHERE object_id = OBJECT_ID('AuditEvents');
```

If the dev or test database is simply behind on migrations, see the note in
`docs/engineering-traps.md` — that failure has bitten twice and does not look like what it is.

**On an environment that HAS been migrated, a missing `AuditEvents` is a fourth thing, and it is not
an accident.** Dropping or renaming the table is the most complete tamper available to anyone
holding write access: there is no chain left to verify, and the verifier reports it as exit 3 — no
verdict — rather than CHAIN BROKEN, so nothing routes you to the evidence rules further down this
page. **Do not re-run migrations to bring it back.** That recreates an empty table and erases the
fact that it was ever gone, which is the one piece of evidence there is. Establish first whether this
environment was ever migrated, which `__EFMigrationsHistory` answers directly:

```sql
SELECT COUNT(*) AS audit_table_present FROM sys.tables WHERE name = 'AuditEvents';
SELECT TOP 3 MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC;
```

A migration history that includes the audit table's migration, with no `AuditEvents` to show for it,
is not a deployment that failed halfway. Preserve the database and escalate.

---

## If the verifier reports CHAIN BROKEN

**A break has two families of cause and they need opposite responses, so classify before you act.**
Either somebody with write access changed or removed an audit row, or something entirely ordinary
produced the same verdict: an `Audit:ChainKey` that is not the one those rows were written with, or
a deletion of the OLDEST rows from outside the application — an archival job, a manual cleanup, a
restore of a partial backup. Both are measured below and neither involves an attacker, but they are
not equally likely. A wrong key happens on any deploy or rotation that exports the wrong value, and
it is the first thing to rule out. The other is not routine here: this application never deletes an
audit row, and ADR-0044 **D6** records retention as an unsolved problem rather than a scheduled job —
so it is benign only if you can name the job or the restore that did it. *(Was "D5" until 2026-08-29;
D5 is the no-personal-data decision and D6 is retention, added the day before this correction.)*

⚠️ **A WRONG KEY NOW HAS A REMEDY AND NOT ONLY A DIAGNOSIS.** The verifier holds a RING of chain keys
and picks the one each row names, so a rotation no longer strands history — provided the retired key
was added to `Audit:RetiredChainKeys`. The message says which case you are in: it names the row's key
id and reports how many keys the ring holds. If the ring has no retired keys and the row names
something else, the key that wrote it was never retired into the configuration, and adding it is the
fix. **Adding a key you cannot account for is not** — the ring is how an honest rotation stays
verifiable, never a way to make a verdict go green.

⚠️ **RETIRING A KEY TAKES THREE VALUES, NOT ONE, AND THE PROCESS REFUSES TO START WITHOUT THEM.** An
earlier version of this paragraph said "adding it is the fix" and stopped there; following that
literally produces a service that will not construct. All three:

- `Audit:RetiredChainKeys:N:Key` — the retired key material.
- `Audit:RetiredChainKeys:N:LastSequence` — the **highest `AuditEvents.Sequence` that key legitimately
  wrote**, which is the tail at the moment the new key took over. Rows above it that name this key
  are refused even when their hash is correct, because a valid hash under a key that had stopped
  writing is what MINTING looks like rather than history. Too high re-opens that window by the
  difference; too low refuses real rows, which is loud and correctable. **Err low.**
- `Audit:FoundingChainKey` — required as soon as anything is retired. Rows older than the key-identity
  column record no key, so something has to say which key wrote them; it must name material already
  in the ring. **It INHERITS that entry's `LastSequence`**, and there is nothing to set separately:
  it is a designation of a key in the ring, so the epoch bounding that key bounds it here too. A
  `v2` row above the boundary is refused for the same reason a keyed one is.

Through the environment each `:` becomes `__` and `N` is a zero-based index, so the first retired key
is `Audit__RetiredChainKeys__0__Key` and `Audit__RetiredChainKeys__0__LastSequence`. The recovery
blocks under **After recovery** read all three; this is the same list stated as configuration.

```bash
dotnet run --project backend/tools/AzureBank.AuditVerifier -- verify
```

Run it after configuring, before believing it. A ring that will not construct fails at startup with
the reason in the message.

⚠️ **A RING THAT CONSTRUCTS BUT IS WRONG DOES NOT ALWAYS COME BACK AS `UnknownScheme`, AND THIS
PARAGRAPH SAID IT DID.** That was the comfortable version and it is the dangerous one, because the
worst of the four outcomes is the one that reports nothing at all. Which you get depends on HOW the
ring is wrong:

| what is wrong | verdict | where it breaks |
| --- | --- | --- |
| the key that wrote a row is not in the ring | `UnknownScheme` | lowest row that key wrote |
| a `LastSequence` is too LOW | `UnknownScheme` | first row above the recorded boundary |
| `FoundingChainKey` names the wrong ring member | `HashMismatch` | lowest `v2` row |
| a `LastSequence` is too HIGH | **`CHAIN INTACT`** | nowhere — nothing is reported |

*(All four rows were run, not reasoned about — the same method that found the exit-1 defect further
down this page. Row three is the only one whose verdict names a configuration setting on its own;
the other three have to be read positionally.)*

**The last row is why "run it and see" is not a check on the ring.** Too high does not fail; it
silently admits rows a retired key had no business writing, which is the whole hazard the boundary
exists to catch. Nothing in the verdict distinguishes it from an honest one. Only the rotation
record does — see the boundary guidance below.

**The third row is the one that reads like tampering.** A founding key that the ring HOLDS is
applied to the identity-less rows and its hash is recomputed, so a wrong designation reaches a hash
comparison and fails it. It is a `HashMismatch` at the lowest `v2` row and every one after — which
is positionally identical to a write, so check the designation before escalating.

⚠️ **AND THE TWO `UnknownScheme` VERDICTS LOOK ALIKE AND NEED OPPOSITE RESPONSES. READ WHICH ONE
YOU HAVE BEFORE TOUCHING ANYTHING.**

- *"…and no key in this verification's ring has that id"* — the key that wrote the row was never
  retired into the configuration. Adding it, with its boundary, is the fix.
- *"…which this verification DOES hold — but that key was retired at sequence N and this row is
  sequence M"* — the ring HAS the key. The row sits above the sequence that key stopped writing at,
  so its hash is correct under a key that had no business writing by then.
- *"…is a `v2` row, which records no key identity, so it is checked under `Audit:FoundingChainKey`
  — and that key was retired at sequence N"* — the same boundary, reached WITHOUT a key id. Treat
  minting as the leading reading rather than the alternative: a `v2` row records no key, so
  labelling a new row `v2` is the one way to reach the founding key without naming it, and the
  boundary is the only thing that sees it.

**The second one has two readings and they are not equally benign.** Either the recorded
`LastSequence` is too LOW and the row is genuine history written before the rotation, or the row was
**minted with a retired key after the rotation** — which is the attack the boundary exists to catch.

🔒 **RAISING `LastSequence` TURNS THAT VERDICT GREEN IN BOTH CASES, WHICH IS EXACTLY WHY IT IS NOT
THE FIRST MOVE.** If the row was minted, raising the boundary completes the attack and the trail then
attests to it. Establish which reading is true from something outside this database — the change
record for the rotation, the deployment that carried it, the ticket that ordered it — and only then
correct the configuration. If nothing outside can say when the key was retired, the honest position
is that this row cannot be verified, and that is a finding rather than a configuration task.

**Classify first — it costs one command, and the tool already prints the two things that decide
it:** the KIND of break, and `Rows verified before the break`. The verifier only ever reads, so
running it again destroys no evidence and moves nothing: three rows read with the right key, then a
wrong one, then the right key again reported `CHAIN INTACT`, `CHAIN BROKEN`, `CHAIN INTACT`, and the
row count never moved.

- `does not match its own hash` with **`Rows verified before the break: 0`**, on a row that records
  NO key identity (`PayloadVersion` = `v2`) — suspect the key before an attacker, and settle it by
  re-running with the key this deployment actually keeps. A wrong key is well-formed, so nothing
  rejects it, and it mismatches on the first row it reads — which is always sequence **1**, for the
  reason in the note under *After recovery*. Measured: three unaltered rows read with a valid but
  wrong key reported `CHAIN BROKEN at sequence 1`.
- ⚠️ **the same verdict on a row that DOES name its key (`PayloadVersion` = `v3`) is a WRITE, and
  the key is already ruled out.** Such a row is checked against the identity it records BEFORE its
  hash is recomputed, so a wrong key cannot reach the hash there at all — it reports an unchecked
  row instead. Preserve the table and escalate. Do not spend the incident re-testing a key the tool
  has just proved correct; the verifier says so itself on the line beneath the verdict.
- the same verdict at **any sequence above 1** — NOT the key. A row numbered above 1 that records
  no predecessor got there by a write, which is the cheapest way to hide a deleted prefix.
  Measured: removing the oldest row and clearing the survivor's `PreviousHash` produces exactly
  this, with the CORRECT key. Treat it as the real thing.
- `does not match its own hash` with a **non-zero** count — an alteration at that row. ⚠️ **This
  used to carry an "unless more than one key has written this table" escape, and that escape is
  gone**: every row now records the non-secret identity of the key that wrote it, inside its hashed
  payload, and a row naming a key this verification does not hold is refused BEFORE its hash is
  recomputed. So a hash mismatch on such a row is not ambiguous any more — the key behind it has
  already been confirmed. Rows written before key identity existed (`PayloadVersion` = `v2`) keep
  the old reading, and only those.
- `declares payload version ... which this build cannot render` or `was written under key id ...` —
  the row was **NOT CHECKED**, which is never the same as checked and found good. Three readings:
  you hold a different key than the one that wrote it, this build is older than the row, or the
  column was overwritten — and that column is inside the hashed payload, so overwriting it is a
  modification. **The discriminator is positional, not textual:** the first two fail at the LOWEST
  row of that scheme and at every one after it, while a single row failing among verified siblings
  is a write. Exit code is 1. ⚠️ **Never triage this as a configuration note.**
- `expected to follow ... A row was deleted, reordered, or inserted` with **`Rows verified before
  the break: 0`** — the first row read names a predecessor that is not there. **The sequence it
  broke at says which of two things happened, and they are not the same incident.** At **sequence
  1** nothing was removed: this row IS the start of the chain, so the predecessor it records was
  WRITTEN onto it, and only an update does that. Above sequence 1 the rows BELOW it are gone.
  Measured, on the same intact chain of three: writing a `PreviousHash` onto row 1 gives `CHAIN
  BROKEN at sequence 1 ... Sequences read: 1 to 1` with all three rows still present, while
  deleting row 1 gives `CHAIN BROKEN at sequence 2 ... Sequences read: 2 to 2`. Preserve the table
  either way. Only the second one can be housekeeping, and only if you can name the job — an
  archival job and an attacker print the identical line, so a WRONG key against that same table
  prints it too: the link is checked before the hash, so on a chain with its head gone the key
  never enters into it. With a NON-ZERO count a row is missing or out of place in the MIDDLE, which
  removing the oldest rows cannot do — that one is the real thing.
- `could not be read at all` — a stored value contradicts the schema, which is itself a
  modification. Neither a wrong key nor a deletion can cause this.

**From the moment it is none of those, the table is evidence: do not repair it and do not write to
it.** A repair destroys the only record of what was done to it, and the chain cannot be "fixed" —
recomputing hashes requires the key, which is exactly what an attacker who reached the database is
assumed not to have.

**Capture, in this order, before anyone touches the database.** The verifier's full output including
the exit code **and the UNCOVERED WINDOW block under it**; `export <path>` run to a file OUTSIDE this
machine, which is the only step here that survives the machine; the sequence it names and the rows on
either side of it (`SELECT * FROM AuditEvents WHERE Sequence BETWEEN <n>-2 AND <n>+2`); the total row
count; and the SQL Server default trace or audit for recent writes to `AuditEvents` if it is enabled.

`export` is safe to run here — it READS the anchor table and writes a file, it refuses to overwrite an
existing one, and it touches nothing in the database. `anchor` is the verb to leave alone; the section
below says why, and says it too late to help if you have already run it.

**Then escalate rather than continue.** Deciding whether to keep taking traffic on an instance whose
audit trail is proven altered is not an operational call — it is the decision ADR-0044 D1 exists to
make possible, and it belongs to whoever owns the incident. This runbook stops here on purpose.

---

## What NOT to do

**Do not disable the audit chain to restore service.** The bank would then move money it cannot
account for, which is the exact state D1 exists to prevent — and unlike an outage, it leaves no trace
that it happened. An outage is recoverable; unaudited movements are not reconstructible.

**Do not raise `Audit:TailTimeoutSeconds` to push the failures away.** The bound is what turns a
thirty-second queue into a fast refusal; raising it makes every movement wait longer for the same
eventual failure, and holds a connection while it does. If the queue is legitimate rather than stuck
— a genuine burst — the fix is capacity, not patience.

**Do not run `anchor` during an incident. Run `export` instead, and run it FIRST.** The tool has
three verbs and only two of them are safe here, which the rest of this page could not tell you
because it was written when there was one.

- `verify` READS. Safe, and it is the verdict everything below hangs on.
- `export <path>` READS the anchor table and writes a FILE. Safe against the database, and it is the
  one thing on this page that gets evidence somewhere the incident cannot reach. Run it before you
  touch anything: it refuses to overwrite an existing file, so it cannot destroy an earlier copy, and
  a chain that has stopped verifying is exactly when an off-machine copy is worth the most.
- `anchor` WRITES a record into `AuditAnchors`. **That is the one to leave alone.** Two paragraphs
  below, this page tells you the table is evidence and not to write to it; `anchor` is a write. It
  will also record a GAP MARKER over a chain it cannot vouch for, which is honest and is still a new
  row in the evidence, dated during your incident.

Neither `export` nor the window below is a substitute for the number you wrote down elsewhere. They
are cheaper to produce and they are not testimony from anybody else.

Same three environment variables as the recovery block below — **both keys, or it exits 3** — and a
destination that is not this machine:

```bash
dotnet run --project backend/tools/AzureBank.AuditVerifier -- export /mnt/evidence/anchors-$(date +%FT%H%M%S).jsonl
```

It refuses to overwrite: an existing file exits 6 and the copy already there is untouched. That is
why the timestamp is in the name rather than a fixed `anchors.jsonl` you would have to argue with
during an incident. `docs/audit/anchors.sample.jsonl` shows the shape of what lands.

**Do not delete rows to "unstick" the table.** Two different things happen, and the one that sounds
safer is the dangerous one.

Deleting an **interior** row is caught: the row after it records a predecessor that is no longer
there, and `VerifyAsync` reports the break at that sequence number. Deleting from the **end** is
~~not caught at all~~ **not caught by the CHAIN** *(narrowed 2026-08-28)*. The surviving prefix links
perfectly and hashes perfectly, because verification only ever looks backwards and has nothing to
compare the end of the table against.

What changed is that the chain is no longer the only thing looking. An anchor records how deep a
walk reached, so a table that has since become shorter than an anchor claims produces a NEGATIVE
uncovered window — read below — and that is an arithmetic disagreement, not a hash one. It catches
the deletion done by somebody who did not know `AuditAnchors` existed. It does not catch the
deletion done by somebody who did: removing the covering anchors along with the rows is consistent
and silent, which
`AuditAnchorSqlServerTests.ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit`
asserts on purpose. **So the sentence is now about effort, not impossibility** — and an operator who
reads the old absolute is entitled to stop looking, which is why it is corrected rather than left.

Measured, not argued — `AuditChainTests.TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit` writes
three rows, deletes the last one, and the chain still reports itself intact; the only trace is that
the count fell from three to two, which is evidence to somebody who wrote the old count down
elsewhere and to nobody who did not.

So truncation is the cheapest attack on this table — it needs **no key at all**, only write access —
and it is also the easiest thing to do by accident while trying to clear a stuck table at three in
the morning. ADR-0044 states the same limit and the honest claim it leaves: this chain detects
tampering by someone holding the database but not the key, **except at the end of the table**.

~~Until the tail is anchored outside the system, the only witness to how many rows there should be is
somebody who wrote the number down.~~ *(Corrected 2026-08-28: there are now three witnesses, and only
the last of them is a person.)* The `AuditAnchors` table records how far each verification walk
reached and how many rows it held; `verify` prints the UNCOVERED WINDOW, which is how far the table
runs past the deepest sequence any anchor claims; and `export` writes the anchor chain to a file that
can leave the machine. **None of them closes the gap** — a truncation that deletes the covering anchor
records too is still silent, which
`AuditAnchorSqlServerTests.ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit`
asserts on purpose. What they buy is that the LAZY version of the attack, done by somebody who did not
know the anchor table was there, now leaves a number that does not add up.

---

## After recovery

- **Verify the chain. Run this FROM THE REPOSITORY ROOT** — the `--project` path is relative to it,
  and from anywhere else `dotnet run` fails to find the project and exits **1**, which this document
  defines under **Exit codes, for scripting it** below as CHAIN BROKEN. (That pointer used to count
  paragraphs -- "four paragraphs below" -- and the edit that added the second key and the uncovered
  window pushed the definition eighty lines further down without touching the sentence that pointed
  at it. A cross-reference by COUNT is one an unrelated edit can silently break; by NAME it
  survives.) That failure happens before the tool starts, so nothing inside it can translate the
  code. Confirm with `pwd` if you are not sure.

  The key and the connection string travel through the environment rather
  than as command arguments, because on Linux `/proc/<pid>/cmdline` is world-readable while
  `/proc/<pid>/environ` is not.

  **That does not make them private from your own shell**, which is a distinction worth stating
  because it is easy to assume otherwise. An `export` line is a command, and your shell records it:
  bash writes it to
  `HISTFILE`, and PowerShell's PSReadLine writes it to `ConsoleHost_history.txt` — its
  sensitive-word filter matches `password|token|apikey|secret`, none of which is `ChainKey`. A
  history file outlives the process, which makes it the WORSE exposure of the two. Read BOTH values
  instead of typing them — the connection string carries database credentials, so it is no more
  printable than the key:

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

  `LastSequence` is read with `-rp` rather than `-rsp` on purpose: it is not key material, and
  getting it wrong is the hazard the value exists to bound, so it should be on screen to be checked.
  The founding key IS material — it names which key wrote the rows that record none — so it is read
  like the others even though it is a designation of a key already in the ring.

  ⚠️ **TWO KEYS, AND THIS PROCEDURE STOPPED WORKING WHEN THE SECOND ONE ARRIVED.** The tool validates
  both at startup and refuses to run without either. Running the version of this block that read only
  `Audit:ChainKey`, measured:

  ```
  EXIT=3
  CANNOT VERIFY: this tool is not configured to read the chain.
    Audit:AnchorKey must be configured with at least 32 characters.
  ```

  3 is the code this document tells you to alert on as "the store could not be read" — so an operator
  following the old text during an incident was sent to check a database that was fine, by the page
  written to stop exactly that. `Audit:AnchorKey` authenticates the anchor records, and `verify` needs
  it because it reads them now: see the uncovered window below.

  ⚠️ **AND `verify` WAS THE ONLY VERB THAT ANSWERED LIKE THAT.** Running the same misconfiguration
  through the other two, on the build shipped before this page was corrected:

  ```
  verify  -> EXIT=3   CANNOT VERIFY: this tool is not configured to read the chain.
  export  -> EXIT=4   Unhandled exception: OptionsValidationException: Audit:AnchorKey must be ...
  anchor  -> EXIT=4   Unhandled exception: OptionsValidationException: Audit:AnchorKey must be ...
  ```

  4 is **"the command line was wrong"**, and the command line was right — so a script keyed to these
  codes, or an operator reading them, was pointed at their own typing instead of at the configuration.
  `anchor` even had a guard written for this exact case, with better prose than the exception; it read
  `options.Value` to reach it, which is what triggers the validation, so it threw one line before the
  guard could run and printed its sentence zero times. Both now answer **3**, like `verify`.

  This was not found by a test. It was found by running the commands on this page, which is the only
  method that would have found it: the tool's own suite passes either way, because no test ran a verb
  against a missing key and asserted the NUMBER an operator would see.

  ⚠️ **AND IT HAPPENED A SECOND TIME, THE SAME WAY, WHEN THE KEY RING ARRIVED.** The ring lines above
  are not optional decoration on a rotated deployment: without them this procedure accuses an intact
  chain. Three rows written under one key, the key then rotated, each configuration run against the
  same untouched database:

  ```
  ChainKey (the NEW key) + AnchorKey, exactly as this block read before the ring:
    EXIT=1   CHAIN BROKEN at sequence 1.
             Row ... was written under key id '7057da02943bb1e6' and no key in this
             verification's ring has that id -- it holds 'e7bca259f5837226' and no
             retired keys.

  + Audit__RetiredChainKeys__0__Key and __LastSequence, founding key still unset:
    EXIT=3   CANNOT VERIFY: the audit store could not be read, so there is no verdict.
             InvalidOperationException: Audit:FoundingChainKey is required once a key
             has been retired.

  + Audit__FoundingChainKey:
    EXIT=0   CHAIN INTACT: 3 rows verified.
  ```

  **1 is the code this document tells you to alert on as CHAIN BROKEN**, and the middle run shows
  what the same procedure answers when the ring is merely INCOMPLETE: 3, which says the store could
  not be read and names the setting to add. Two exits three lines apart, sending an operator to
  opposite places — 3 to the configuration, 1 to an incident. The anchor-key omission a release
  earlier also answered 3, which is what makes this one the worse of the two: the first rotation
  would have paged somebody, from the page written to stop exactly that, and the verdict would have
  named a key id as evidence.

  *(That sentence read "worse than the exit 3 the missing anchor key produced" until review. True of
  the earlier incident, and wrong where it sits: the 3 in the fence six lines above is a FOUNDING-key
  3, so it sent a reader to check `Audit:AnchorKey`, which is set and fine. A page read under
  pressure is read locally.)*

  That refusal is deliberate: the ring will not construct rather than guess which key wrote the
  identity-less rows, so a HALF-configured ring cannot silently verify history under the wrong key.
  It is why `Audit:FoundingChainKey` is listed above as required rather than recommended.

  PowerShell, since it is the history file this paragraph cites. **`-AsPlainText` on
  `ConvertFrom-SecureString` is PowerShell 7 or later**; on Windows PowerShell 5.1 it does not exist
  and both assignments end up EMPTY, which the verifier then reports as a missing key. Check with
  `$PSVersionTable.PSVersion` first.

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

  ```powershell
  # Windows PowerShell 5.1
  $key = Read-Host 'Audit:ChainKey' -AsSecureString
  $env:Audit__ChainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
      [Runtime.InteropServices.Marshal]::SecureStringToBSTR($key))
  $anchorKey = Read-Host 'Audit:AnchorKey' -AsSecureString
  $env:Audit__AnchorKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
      [Runtime.InteropServices.Marshal]::SecureStringToBSTR($anchorKey))
  $cs = Read-Host 'Connection string' -AsSecureString
  $env:ConnectionStrings__DefaultConnection = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
      [Runtime.InteropServices.Marshal]::SecureStringToBSTR($cs))

  # Only if a key has ever been retired. Repeat, incrementing the 0, for each one.
  $retired = Read-Host 'Retired chain key #0' -AsSecureString
  $env:Audit__RetiredChainKeys__0__Key = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
      [Runtime.InteropServices.Marshal]::SecureStringToBSTR($retired))
  $env:Audit__RetiredChainKeys__0__LastSequence = Read-Host '  its LastSequence'
  $founding = Read-Host 'Audit:FoundingChainKey' -AsSecureString
  $env:Audit__FoundingChainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
      [Runtime.InteropServices.Marshal]::SecureStringToBSTR($founding))

  dotnet run --project backend/tools/AzureBank.AuditVerifier -- verify
  ```

  ⚠️ **AND IT PRINTS AN UNCOVERED WINDOW, which is new and is the line most likely to be misread.**
  It says how far the table runs past the deepest sequence any anchor claims to cover — "at least N
  rows are outside every anchor". Three readings of it are wrong and worth naming before you see it
  at three in the morning:

  - **Zero does NOT mean you are covered.** It means the deepest claim reaches the tail AT THAT
    INSTANT, which one row written a moment later undoes. The line says "at least" for that reason.
  - **It is not a freshness measure.** Nothing here schedules an anchor, so the window has no ceiling
    and a small number now says nothing about tomorrow. A missing anchor is not evidence of anything.
  - **NEGATIVE is the one to stop for.** It means the anchors claim coverage through a sequence that
    no longer exists, and nothing legitimate produces it. Preserve the database, `export`, and
    escalate before running anything that writes.

  It is counted in SEQUENCE numbers. That is a row count unless somebody holding `Audit:ChainKey`
  removed rows and recomputed the links behind them — the walk checks the links, never the
  contiguity — so **a `SELECT COUNT(*)` that disagrees with the span is the finding, not a fault in
  the tool.** It is the only trace that particular deletion leaves.

  It prints the verdict WITH the row count and the sequence range, because "intact" on its own is
  not an answer — a chain of zero rows links perfectly, and a table truncated to nothing reports
  exactly what a fresh one does. Compare the count against what you expected: a chain that verifies
  40 rows where yesterday it had 40,000 is intact and catastrophic, and only the numbers say so.

  Exit codes, for scripting it: **0** intact, **1** broken, **2** nothing to verify, **3** no verdict
  (the store could not be read), **4** the command line was wrong, **5** interrupted, **6** there WAS
  a verdict but nothing could be recorded from it. Only 0, 1 and 2 are statements about the CHAIN.

  ⚠️ **This list stopped at 5 until 2026-08-28, and 6 had existed since the `anchor` mode shipped.**
  A script written from it treated 6 as an unknown code. The list lives in FOUR places —
  `VerifyCommand`'s constants, `AnchorCommand`'s, the header comment in `Program.cs`, and here — and
  this is the copy that goes stale unnoticed, because it is the only one no compiler reads. **6 means
  the chain was read and the WRITE failed**: a refused `anchor`, an `export` to a path that already
  exists or cannot be written. It is a statement about the operation you asked for, not the bank.

  ⚠️ **AND "STATEMENTS ABOUT THE CHAIN" NOW MEANS TWO CHAINS, WHICH THESE CODES DO NOT SEPARATE.**
  There is the audit chain in `AuditEvents` and the anchor chain in `AuditAnchors`, and since the
  anchors shipped the codes have been overloaded in both directions:

  - **`0` from `verify` does not mean the anchor chain verified.** `VerifyCommand` returns `Intact`
    on the strength of the row walk alone, and prints `UNCOVERED WINDOW: not computed -- the anchor
    chain did not verify.` underneath it. So an anchor record failing its own MAC — the one event
    that table exists to make loud — reaches a script keyed on the exit code as **green**. **Read the
    UNCOVERED WINDOW block, not just the verdict line.** Anything other than a number there is
    something to act on.
  - **`1` from `export` is about the ANCHOR chain, not this one.** `ExportCommand` takes its code
    from the anchor verification it just wrote out, so it can exit 1 while `verify` on the same
    database exits 0. Read `1` as "the chain THIS VERB is about is broken", and the verb decides
    which.

  Both are confirmed in `VerifyCommand.Report` and at `ExportCommand.cs:516`, and neither is a defect
  in this page — the page is where they become visible, because it is the only document that treats
  the codes as one vocabulary shared by every verb. **Whether the tool should instead give the anchor
  chain its own code is a change to what alerts fire, and is not decided here.** Until it is, an
  alert on `verify` returning non-zero does not cover the anchor chain, and nothing else does either.

  **`3` is the one to wire an alert on separately from `1`.** It covers everything that stopped the
  walk before it could reach a verdict: a MISSING or too-short `Audit:ChainKey` **or
  `Audit:AnchorKey` — both are validated at startup and either alone stops all three verbs, which is
  the failure this page's own recovery procedure produced until 2026-08-28** — an unreachable
  server, a connection string that is malformed or absent, **and the states step 6 is about — the
  `AuditEvents` table renamed, dropped or never migrated, or a login that cannot SELECT from it.**
  That last group is why the tool no longer stops at "check the connection string and the key": it
  now says that a vanished `AuditEvents` exits the same way and is the most complete tamper there
  is, and that the remedy step 6 points at — re-running migrations — would recreate the table and
  erase the evidence it ever went. Read the exception line above the advice; it names the cause.

  ⚠️ **THERE ARE NOW TWO SUCH TABLES, AND STEP 6 ONLY KNOWS ABOUT ONE.** `verify` reads
  `AuditAnchors` before it walks a row, so that table missing stops the walk exactly the same way.
  Measured 2026-08-28 by renaming it away:

  ```
  EXIT=3
  CANNOT VERIFY: the audit store could not be read, so there is no verdict.
    SqlException: Invalid object name 'AuditAnchors'.
  ```

  **Do not read that as the lesser of the two.** A vanished `AuditAnchors` is the second half of the
  one attack this system cannot see: removing rows from the end of `AuditEvents` is silent only if
  the anchors covering them go too, which
  `AuditAnchorSqlServerTests.ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit`
  pins on purpose. So the table being absent has two readings — never migrated on this deployment, or
  removed — and they are the same exception. **Which one it is depends on whether this deployment
  ever anchored**, and re-running migrations answers it forever in the wrong direction: it recreates
  an empty table, and an empty `AuditAnchors` is indistinguishable from one that was cleared. Check
  the migration history and any exported copy before anybody runs a migration.

  **A WRONG key is not among them, and this runbook previously said it was.** A well-formed key that
  is simply not the one the chain was written with passes every check the tool can make, so the walk
  runs: that exits **1**. ⚠️ **What it exits 1 AS now depends on the row.** On a row recording no
  key identity the hashes mismatch and it is indistinguishable from a tamper — no way around it,
  which is why the next paragraph exists. On a row that names its key the tool refuses it BY NAME
  before recomputing anything and reports an UNCHECKED row instead, so there the two ARE told apart,
  and a mismatch rules the key out rather than implicating it.

  **`5` usually is not an incident, and it is never evidence that nothing is wrong.** Somebody
  stopped the walk — Ctrl+C, a killed job, a shutdown — so part of the chain was checked and the
  rest was not, and there is no verdict. What it does NOT establish is that the store is healthy:
  the branch is selected by the cancellation token, not by what threw, so a store that failed while
  the token was already signalled is reported as an interruption too. **If it was stopped because it
  appeared to hang, the hang is the finding**, and the triage list above is exactly what applies. A
  walk stopped for any other reason can simply be re-run. It is separate from `3` for a different
  reason: an alert wired to `3` would otherwise hand whoever answers it a list of environment
  failures to chase after a colleague pressed Ctrl+C. `e2fsck` keeps the same distinction (32
  "canceled by user request" against 8 "operational error"), as does AIDE (25 for SIGINT against 18
  and 24). Re-run it to get an answer.

  **`4` exists because the framework collided with this vocabulary.** System.CommandLine reports
  every parse failure as exit 1, so running the tool with no arguments at all — the likeliest
  mistake there is — used to report a tampered audit trail. Measured, and now translated.

  **If it reports a HASH MISMATCH before any row verifies ON A ROW RECORDING NO KEY IDENTITY,
  suspect the key before you suspect an attacker — and read the sequence as well as the count.**
  ⚠️ The scoping is not decoration: on a row that names its key the same verdict means the opposite,
  because the identity was checked first and matched. A wrong key mismatches on the first row
  it reads, and that row is sequence **1**. It cannot be anything else: the walk checks the LINK
  before the hash, so the only row that can reach the hash check first is one recording no
  predecessor, and `AuditChain.Link` writes that only into an empty table, where the row it writes
  is sequence 1. `Rows verified before the break: 0` says the key is worth checking, and `Sequences
  read: 1 to ...` on the next line confirms it.

  **The same verdict above sequence 1 rules the key OUT** — a row numbered above 1 recording no
  predecessor got there by a write.

  ⚠️ **DO NOT WIDEN THIS TO THE COUNT ALONE.** It was written that way once, and the wide version
  is the one that helps an attacker. The argument for it reads well: a purged chain begins at
  5,001, so gating on sequence 1 would stop the hint firing on the oldest tables. That case is
  unreachable, and the tool was gated on it anyway. On a chain whose head is gone the first row
  read records a predecessor that is missing, so the walk reports a LINK break and never reaches
  the hash check at all: measured, a wrong key against a decapitated chain prints output identical
  to the correct key. What the loosened gate did produce was the dangerous direction — an attacker
  who removed the oldest rows and cleared the survivor's `PreviousHash` was met with "usually means
  the wrong Audit:ChainKey ... Confirm the key before escalating".

  The row hash is an HMAC over `Audit:ChainKey`; a wrong key is well-formed, so nothing rejects it,
  and it mismatches on the first row it reads. It used to be the ONLY break a wrong key could
  produce — it cannot make a row unreadable and it cannot change what a row records as its
  predecessor, so on those two the tool deliberately stays silent about the key. ⚠️ **That sentence
  is now true only of rows recording no key identity.** A row that names its key is refused by name
  before any hash is recomputed, so there a wrong key produces an UNCHECKED row rather than a
  mismatch — which is why a mismatch on such a row exonerates the key instead of implicating it.

  What it still cannot tell you is whether rows were removed from the END. ⚠️ **That sentence used to
  continue "that needs an anchor outside the system", and this page struck the same claim fourteen
  paragraphs above on 2026-08-28 while leaving this copy standing** — so the last thing the operator
  read contradicted the correction they had already been given. The anchor now exists: `AuditAnchors`
  records how deep each walk reached, and `verify` prints the UNCOVERED WINDOW under every verdict.
  Neither closes the gap — a truncation that removes the covering anchor records too is still silent
  — but the version of the attack carried out by somebody who did not know the anchor table was there
  now leaves a number that does not add up. See ADR-0044.

  _That correction was made by searching for the WORDING of the struck sentence, which does not
  appear here: this copy said the same thing in different words and survived the search. When a claim
  is withdrawn or narrowed, search for what it SAID, not for how it said it._
- The refused movements were refused, not lost: no money moved, and no audit row claims it did.
  Customers can simply retry.
- If the cause was contention rather than an outage, the number worth capturing is how long the tail
  was held, and by what. That is the input to deciding whether the chain's single global tail needs
  to stop being single — a question ADR-0044 leaves open.
