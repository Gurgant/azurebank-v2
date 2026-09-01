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
unhealthy, that USED to settle it. ⚠️ **It no longer does.** Since the ring's rules moved into
`AuditChain`'s constructor, and `AzureBankDbContext` takes an `IAuditChain`, a refusal to build the
ring fails the `database` check too — both readiness checks resolve the context, so a typo in
`Audit__RetiredChainKeys__0__LastSequence` reports exactly like an outage. Before treating it as
one, read the failure text: a ring refusal names an `Audit:` setting and says so in prose, while a
real outage carries a connection or timeout error. If it names a setting, go to step 5 and leave the
database alone.

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
record** — while the tail is read under `UPDLOCK, HOLDLOCK` **before** anything is inserted (the
`TailSql` constant in `AuditChain`). So on exactly the blocked writer step 4 constructs, the age
column was blank,
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

⚠️ **RETIRING A KEY TAKES THREE VALUES, NOT ONE.** An earlier version of this paragraph said
"adding it is the fix" and stopped there; following that literally produces a deployment that
starts and then fails the first request that opens the database — measured below, and wider than an
audited write. All three:

- `Audit:RetiredChainKeys:N:Key` — the retired key material.
- `Audit:RetiredChainKeys:N:LastSequence` — the **highest `AuditEvents.Sequence` that key legitimately
  wrote**, which is the tail at the moment the new key took over. Rows above it that name this key
  are refused even when their hash is correct, because a valid hash under a key that had stopped
  writing is what MINTING looks like rather than history. Getting it wrong costs on BOTH sides: too high
  re-opens that window by the difference, and also pushes the NEXT key's epoch start above rows that
  key genuinely wrote, so it refuses real rows too — just later ones. Too low refuses this key's own
  real rows, at the first row above the boundary. There is no safe direction to err in; take the
  number from the rotation record. *(This read "Too high re-opens that window; too low refuses real
  rows, which is loud and correctable. **Err low.**" — the single-edge version of the advice. The
  same advice was standing in `RetiredChainKey.cs` too, one paragraph after that file withdrew the
  single-edge framing, and the footnote here claimed it had already been removed from there. It had
  not. Both are corrected now, and the reason "err low" is wrong at all is that too HIGH is SILENT
  while the range it over-claims is empty.)*
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

Run it after configuring, before believing it — and run it because **NOTHING ELSE WILL TELL YOU IN
TIME**.

⚠️ **THE API REFUSES TO START ON A RING IT CANNOT BUILD, AND THIS PAGE HAS NOW BEEN WRONG IN BOTH
DIRECTIONS.** It first claimed "the process refuses to start without them" while nothing checked;
that was corrected to "starts normally, then fails the first request", which was measured and true
until `AuditKeyRingStartupCheck` began resolving the ring during startup. Trust the transcript below
over the prose around it, and re-measure before believing either.

- **The API stops during startup and never listens.** Run with a retired key and no
  `Audit__FoundingChainKey`:

  ```
  [INF] Starting AzureBank API...
  [ERR] Microsoft.Extensions.Hosting.Internal.Host: Hosting failed to start
  AzureBank.Infrastructure.Data.AuditKeyRingException: Audit:FoundingChainKey is required once a
        key has been retired. ...
     at AzureBank.Infrastructure.Data.AuditChain..ctor(...) in AuditChain.cs:line 587
  [FTL] Application terminated unexpectedly
  ```

  No `Now listening on`, no `Application started`: the port never opens, so nothing reaches a login.
  `IAuditChain` is still `AddScoped` and `ValidateOnStart` still covers only the OPTIONS — what
  changed is that one hosted service resolves the chain once during startup, so the constructor
  guards fire there instead of on the first request that opens a `AzureBankDbContext`. That request
  was a LOGIN, before any authentication; and by **D1** an audited operation whose audit write fails
  takes the business action down with it, so past the login the same typo used to surface as failed
  money movements, at request time and however long after the deploy that caused it.

  ⚠️ **AND THE PROCESS STILL EXITS 0.** `Program.cs` catches every startup exception, logs
  `Application terminated unexpectedly` and falls off the end of `Main`, so a supervisor reading
  exit codes sees a clean shutdown rather than a crash-loop. This is older than the ring and not
  specific to it: the same holds for every `ValidateOnStart` failure, measured the same day against
  a missing `Audit:AnchorKey`. **Alert on the absence of `Application started`, never on the exit
  code.**
- **The verifier says so plainly, and all three verbs say the same thing.** A ring that will not
  construct answers `CANNOT PROCEED: this tool is not configured to read the chain` — **exit 3** —
  with the reason on the next line. Where the refusal is about a particular entry it names that
  entry by its CONFIGURATION index rather than its position after the boundary sort. Three of the
  refusals are not about an entry — a blank or short `Audit:ChainKey`, and either
  `Audit:FoundingChainKey` failure — and those name the setting instead. The scenario in this very
  bullet, a retired key with no `Audit__FoundingChainKey`, is one of the three.

  *(This bullet described something worse until it was measured. `verify` reported the refusal as
  `the audit store could not be read` — a sentence about the table, on a run that never opened the
  table — and `anchor` and `export` did not catch it at all: **exit 4**, which the list below defines
  as "the command line was wrong", with an unhandled stack trace. That is the identical incident
  recorded further down this page from an earlier release, same two verbs, closing with "Both now
  answer 3, like `verify`" — re-opened by the ring guards, because a constructor throw surfaces
  wherever a verb happens to resolve the chain. All three now share one verdict.)*

Neither is a reason to skip `verify`; both are reasons not to read a clean startup as evidence that
the ring is right. *(Making it refuse at startup in both roots is worth doing and is not this change:
it needs a place that covers both roots without stating the rules twice, and its own measurement.)*

⚠️ **A RING THAT CONSTRUCTS BUT IS WRONG DOES NOT ALWAYS COME BACK AS `UnknownScheme`, AND THIS
PARAGRAPH SAID IT DID.** That was the comfortable version and it is the dangerous one, because the
worst of the six outcomes is the one that reports nothing at all. Which you get depends on HOW the
ring is wrong:

| what is wrong | verdict | where it breaks |
| --- | --- | --- |
| the key that wrote a row is not in the ring | `UnknownScheme` | lowest row that key wrote |
| a `LastSequence` is too LOW | `UnknownScheme` | first row above the recorded boundary |
| `FoundingChainKey` names the wrong ring member | `UnknownScheme` | lowest `v2` row |
| a `LastSequence` is too HIGH, nothing written above the real boundary yet | **`CHAIN INTACT`** | nowhere — nothing is reported |
| a `LastSequence` is too HIGH, the newer key has already written above it | `UnknownScheme` | first row the newer key wrote |
| two retired keys share a `LastSequence` | refused at construction | before any row is read |

⚠️ **THE TWO `too HIGH` ROWS ARE ONE MISCONFIGURATION, AND THIS TABLE USED TO GIVE IT TWO
CONTRADICTORY ANSWERS.** It carried `too HIGH` → `CHAIN INTACT`, *nothing is reported*, and directly
beneath it `too LOW for the key BELOW it` → `UnknownScheme` at the *first row of the next epoch*. The
second row's cause was backwards. What breaks at **the first row the newer key wrote** is a boundary
recorded too **HIGH**: the next key's epoch STARTS at this one's `LastSequence + 1`, so
over-claiming here pushes the newer key's epoch above rows that key genuinely wrote. Too LOW breaks
somewhere else entirely — at the first row above the recorded boundary, which is row 2 of this
table. *(The
correction that replaced the backwards row said too HIGH breaks at "the next epoch's first row". It
does not: it breaks at the first row the NEWER key wrote, which sits BELOW the inflated epoch start.
The table says it correctly; this sentence did not.)*

The two rows were the same error seen from both sides, filed as different errors with opposite
verdicts, and an operator triaging by symptom would have read the wrong one.

What decides which answer you get is whether the over-claimed range is EMPTY. Measured, both states,
same ring:

```
retired key truly stopped at 2, boundary recorded 4
  the newer key has already written rows 3 and 4  ->  IsIntact=False  UnknownScheme  breaks at 3
  nothing written above 2                         ->  IsIntact=True   nothing reported
```

The silent state is not the harmless one. It is the window an attacker needs — the range is empty,
so a holder of the retired key can mint into it and the walk accepts every row — and it is the state
a deployment is in for exactly as long as it takes the new key to write. The loud state is how the
same mistake surfaces once the system is in use, and it surfaces as rows the CURRENT key wrote being
refused, which reads like tampering and is not.

*(The note here said "All six rows were run, not reasoned about". Two of them had not been, and that
is how one of them ended up backwards and the other unconditional. The two states above were run —
the block is the output. THREE of the six verdicts name the setting at fault on their own: the
shared-boundary refusal names the configuration index, the wrong-designation verdict names
`Audit:FoundingChainKey`, and the not-in-the-ring verdict names `Audit:RetiredChainKeys` twice and
prescribes the edit. The rest have to be read positionally.)*

**`LastSequence` too HIGH is why "run it and see" is not a check on the ring** — while the
over-claimed range is still empty. In that state it does not fail; it silently admits rows a retired
key had no business writing, which is the whole hazard the boundary exists to catch, and nothing in
the verdict distinguishes it from an honest one. Only the rotation record does — see **RAISING
`LastSequence` CAN TURN THAT VERDICT GREEN UNDER EITHER READING** below. Once the newer key has
written into
that range the same misconfiguration is loud, and running it and seeing DOES catch it; what it cannot
do is tell you the ring is right before anything has been written under it, which is precisely when
you are deciding to trust it. *(This paragraph stated the silence unconditionally, and this page's
own table now shows both states. It also opened "The last row is why…" and meant the too-high row,
which stopped being last when two more were appended below it.)*

**The third row USED to be the one that read like tampering, and no longer is.** While an epoch had
only an upper end, a founding key the ring HOLDS was applied to the identity-less rows and its hash
recomputed, so a wrong designation reached a hash comparison and failed it as a `HashMismatch` —
positionally identical to a write, and the reason this row told you to check the designation before
escalating.

**The epoch's lower end made that case unreachable.** Identity-less rows are the oldest rows there
are, so only the OLDEST ring entry's epoch contains them; designating anything else puts them BELOW
that key's epoch and the walk refuses them before any hash is recomputed. Measured: with two retired
keys and the designation pointed at the second, the verdict is `UnknownScheme` reading *"the epoch
that key opens begins at 3, while this row is sequence 1"*, and it names `Audit:FoundingChainKey` as
the fix. Strictly better — the verdict now says which setting is wrong instead of looking like an
attack.

*(A `HashMismatch` on the lowest `v2` row still means something, but something else: the ring entry
the designation names holds the wrong key MATERIAL. The epoch is right, so the walk gets as far as
the hash and the hash disagrees. That is a wrong retired key, not a wrong designation.)*

⚠️ **AND THE `UnknownScheme` VERDICTS LOOK ALIKE AND NEED DIFFERENT RESPONSES. READ WHICH ONE YOU
HAVE BEFORE TOUCHING ANYTHING.** The walk can print **nine** distinct `UnknownScheme` messages,
which the exit-code section below groups into **seven causes** because two PAIRS of them share an
action. Seven come out of the reason switch: the five below, which a ring misconfiguration can
produce, a sixth at the end of this section that nothing in the configuration causes, and a seventh
that shares the fifth's entry in the printed list — an identity-less row stored below sequence 1,
which is a planted row rather than a designation mistake and says so in its own text. The other two
are refused before the switch is reached — an unrenderable payload version, and a `v2` row carrying
a key id — and they are causes 1 and 2 in that list.

*(This heading said "THE TWO" while five bullets followed, then "six", which counted the switch arms
and no more, then "eight", which counted them before a seventh arm was added. FOUR censuses of the
same thing on one page is what this branch keeps producing; they are reconciled here rather than
replaced by a fifth. The number itself is derived from `AuditChain.cs` by a test — this page is the
copy that has to be moved by hand, and this is the fourth time it was not.)*

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
- *"…names key id 'X', whose epoch begins at N"* — the OTHER end of the same boundary, and it is
  not a variation on the three above. The row sits BELOW the epoch of the key it names, so an earlier
  key wrote that stretch. Two readings again: the EARLIER key's `LastSequence` is recorded too
  **HIGH** — it claims rows up to at least this one, which the named key actually wrote — or the rows
  were
  **re-authored by whoever holds the later key**. ⚠️ **Epochs are derived from the boundaries, so
  moving one moves two**: LOWERING the earlier key's boundary lowers the start of the next epoch, and
  the verdict changes for rows you did not think you were touching. Establish the rotation points
  from outside this database before editing any of them.

  *(This bullet gave the benign reading as "too LOW" and said raising a boundary lowers the next
  start. Both are backwards, and it is the one verdict where the direction decides which number an
  operator edits. Reasoned from the derivation: the epoch begins at the previous key's boundary plus
  one, so a start that is too high means the boundary beneath it is too high.)*
- *"…is a `v2` row … the epoch that key opens begins at N"* — the FOURTH boundary verdict, and the
  only one with no minting reading at all. An identity-less row is among the oldest in the table, so
  an epoch starting above it means `Audit:FoundingChainKey` designates a ring member that is **not
  the oldest**. ⚠️ **A row stored BELOW sequence 1 reaches this check too**, on a ring with one key
  and no designation at all, because the founding epoch starts at 1 there. That row gets a different
  verdict — *"below the first sequence this trail can have … Preserve the table and escalate"* — and
  nothing in the configuration causes it or can fix it.

  ⚠️ **For the designation reading, a `LastSequence` edit is not the fix either.** The founding
  epoch's start IS derived from the preceding entry's boundary, so lowering that boundary does move
  it — and it cannot move it far enough on an honest table. Boundaries are at least 1 and strictly
  increase, so the start cannot fall below the designation's POSITION in that order: **2** for the
  second key, **3** for the third. The edit clears this row only if its sequence reaches that
  number, and identity-less rows are the oldest there are — **so if it clears, the trail does not
  begin at sequence 1, and that is a second finding rather than a fix.** Re-point the
  designation.

  *(This said the edit gets you "a different break rather than a clear verdict … the walk fails on
  the link instead — measured". It was not measured and it is not true. A configuration edit cannot
  produce a link break at all: the link test compares this row's stored `PreviousHash` against the
  previous row's stored `RowHash` — two columns, no key, no epoch, no recomputed hash — and it runs
  before a key is ever selected. Handing a row to a key that did not write it produces a HASH
  MISMATCH, not a link break. And in the shape this verdict fires on, no row is handed to anybody:
  the floor above keeps it below the epoch.)*

⚠️ **THE SIXTH VERDICT IS NOT A CONFIGURATION PROBLEM AT ALL, AND THIS PAGE DID NOT LIST IT.**

- *"…declares payload version `v3`, which records the identity of the key that wrote it, and records
  none"* — there is nothing to select a key by, so the hash is not checked. Nothing this deployment
  writes leaves that column empty on this version, and the column is inside the hashed payload, so
  the value was removed after the fact. **Do not look for a missing ring entry.** It is the mirror of
  a `v2` row CARRYING an identity, and it is a modification. Treat it as a break.

**The second of the five, `retired at sequence N`, has two readings and they are not equally
benign.** Either the recorded `LastSequence` is too LOW and the row is genuine history written
before the rotation, or the row was
**minted with a retired key after the rotation** — which is the attack the boundary exists to catch.

🔒 **RAISING `LastSequence` CAN TURN THAT VERDICT GREEN UNDER EITHER READING, WHICH IS EXACTLY WHY IT
IS NOT THE FIRST MOVE — AND GOING GREEN IS NOT EVIDENCE THAT THE BENIGN READING WAS THE TRUE ONE.**
If the row was minted, raising the boundary completes the attack and the trail then attests to it.
*(This said "IN BOTH CASES", flatly. Measured, the minting case is conditional: raising this entry's
boundary also raises the NEXT key's epoch start, so it goes green only when nothing was written under
the newer key in the range you just handed back. When something was, the break MOVES DOWN to the
first row that key wrote — a lower sequence than the one you started from — which is the same pair of
states the triage table above measures. The reason not to raise it is unchanged; the reason given
was wrong.)* Establish which reading is true from something outside this database — the change
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
- `declares payload version ... which this build cannot render`, `was written under key id ...`, or
  any of the four boundary verdicts — the row was **NOT CHECKED**, which is never the same as checked
  and found good. **SEVEN causes produce it since the ring**, and the verdict line itself says which:

  1. this build cannot render the version the row declares;
  2. the identity column contradicts the version — a `v2` row carrying a key id, or a `v3` row
     carrying none;
  3. no key in the ring has this row's id;
  4. the ring HAS that key and the row sits **above** the epoch it closes;
  5. the ring HAS that key and the row sits **below** the epoch it opens;
  6. the row records no key id, so `Audit:FoundingChainKey` answers for it, and the row sits
     **above** that key's epoch;
  7. the same, **below** that key's epoch.

  ⚠️ **THE DISCRIMINATOR USED TO BE POSITIONAL AND THE RING BROKE IT.** How wide the damage is
  depends on WHICH of the seven you have, and they come in three shapes:

  - **Row-local** — causes 1 and 2, an unrenderable payload version and the identity column
    contradicting the version. No epoch and no key are involved: the row is refused on its own and
    the rows around it are untouched.
  - **A whole epoch** — cause 3, a key the ring does not hold. The rows it answers for ARE its
    stretch, so the walk stops at the first of them.
  - **Outside an epoch** — causes 4 to 7. The failing row is by definition NOT inside the epoch the
    verdict names, so **that epoch tells you where the key was valid, never where the damage is.**
    Scoping an incident from it scopes the wrong rows.

  So a key missing from `Audit:RetiredChainKeys` breaks at the first row of THAT key's epoch, with
  rows above it that would verify if the walk went on — the same signature a single overwritten row
  produces. When the missing key is the OLDEST, its epoch starts at 1, so the break is at row 1 with
  nothing verified: that is the ordinary shape of "we rotated once and forgot to retire the old
  key", not evidence of anything worse. **A configuration mistake and a write can look identical in
  one run.**

  What separates them is a second run **with that key in the ring**, and it takes **three** values.
  Supply fewer and the ring refuses to build — exit 3, with neither reading confirmed, which is the
  one outcome this run exists to avoid:

  - `Audit:RetiredChainKeys:N:Key` — the retired key's **material**. ⚠️ **Not the id the verdict
    prints.** The id is 16 hex characters derived one-way from the material, so it cannot be pasted
    back; it tells you WHICH key to fetch from wherever your keys are kept. Pasting it here fails
    the 32-character floor and you get a third error instead of an answer.
  - `Audit:RetiredChainKeys:N:LastSequence` — the sequence that key stopped writing at, **from the
    rotation record**. It is a plain `long` with no default worth having: leave it out and it binds
    to `0`, which the constructor refuses outright.
  - `Audit:FoundingChainKey` — **required as soon as anything is retired.** Add the first retired
    entry without it and the ring refuses to build for a different reason.

  *(This said "two settings, not one" and listed the material and the designation. The boundary is
  not optional and its absence is not benign: the procedure as written exited 3 every time.)*

  Then verify again. A configuration miss clears. A write does not.

  *(This page said "each fails at the LOWEST row it
  applies to and at EVERY ONE AFTER IT … a single row failing among verified siblings is a write" —
  the rule from before the ring, when one key answered for every `v3` row and a wrong key therefore
  failed at the first one. It also said the below-the-epoch causes apply to a PREFIX, "every row from
  the bottom of the table up to the previous key's boundary": they apply only to the rows naming that
  key, which is its epoch, not everything beneath it.)*

  ⚠️ **An overwritten column is not an eighth cause** — `PayloadVersion` and `KeyId` are both inside
  the hashed payload, so changing either is a modification that then surfaces as whichever of the
  seven it happens to trip. Exit code is 1.

  ⚠️ **TWO of the seven are fixed in configuration — a missing ring entry, and the DESIGNATION for
  cause 7. The rest are not, and none of it makes the row proved good.** *(This said "a missing ring
  entry is fixed in configuration; nothing else here is", two lines above naming the designation as
  the fix for cause 7. `Audit:FoundingChainKey` is configuration.)*

  **FOUR of the seven are boundary causes** — 4, 5, 6 and 7 — and **which edge was crossed decides
  which edit is even available**, so they do not share one remedy:

  - **4 and 6 sit ABOVE an epoch.** Raising that key's `LastSequence` admits the row, which is the
    dangerous edit and the one to read about first: see **RAISING `LastSequence` CAN TURN THAT
    VERDICT GREEN UNDER EITHER READING**, **ABOVE**, before changing anything.
  - **5 sits BELOW one.** Raising anything moves that epoch's start *later* and refuses the row
    harder; the number that could admit it is the PRECEDING entry's boundary, moved DOWN. The
    verdict says so itself — *"epochs are derived from the recorded boundaries, so moving one moves
    two"* — and the readings are a boundary recorded too HIGH for the earlier key, or a row
    re-authored by whoever holds the later one.
  - **7** is the designation, below.

  *(This grouped 5 with the raising advice, and the raising advice is the one paragraph on this page
  whose whole point is not to make the edit — so it pointed an operator at the wrong warning for the
  wrong edge. It also said "below" for a section some seventy lines earlier.)*

  **The fourth, cause 7, does not** — an identity-less row below the founding epoch has **two**
  causes and neither is minting. Either the designation is not the ring's oldest key, or the row is
  stored **below sequence 1**, which nothing this deployment writes can produce and no key is needed
  to insert. The second has its own verdict text and its own instruction — preserve and escalate,
  and edit nothing. For the first, no `LastSequence` edit clears it. The
  boundary before it does move this epoch's start, and moving it achieves nothing an operator can
  use on a trail that begins at sequence 1. Boundaries are at least 1 and strictly increase, so this
  epoch's start cannot fall below the designation's POSITION in that order — 2 for the second key,
  3 for the third — and the identity-less rows this fires on are the oldest in the table. The same
  verdict prints again with a smaller number in it. **If the edit DOES clear it, the trail does not
  start at sequence 1, and that is a second finding rather than a fix.** *(This said the start
  "floors at 2, and the identity-less row that gets here is sequence 1". The floor depends on where
  the designation sits in boundary order, and nothing in the code constrains the row to sequence 1 —
  only the shape of an honest table does. It also said moving it "trades this verdict for a link
  break"; no configuration value can produce a link break.)*
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

The same environment as the recovery block below, and a destination that is not this machine.
**Both keys, or it exits 3** — and after a rotation, the whole ring too: every
`Audit__RetiredChainKeys__N__Key` with its `__LastSequence`, and `Audit__FoundingChainKey`.

An incomplete ring
stops `export` for the same reason it stops `verify` — the ring is built when the chain is RESOLVED,
before either verb reads anything — so a retired key without the founding designation refuses at the
same point in both. *(What `export` does NOT do is verify the audit chain: it copies the ANCHOR
chain, and its exit code speaks for that one. A sentence here said it "verifies the chain before it
copies anything", which is true of the anchor chain and false of the one this page is about.)*

*(This line said "same three environment variables" and was written before the ring existed. It kept
pointing at a block that has since grown to six.)*

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
tampering by someone holding the database but not **the key whose epoch that row falls in**,
**except at the end of the table**. *(Singular "the key" until 2026-08-30, then "any key in the ring"
for as long as an epoch had one end; a retired key recomputes its own epoch and no other, so naming
the epoch is the honest form.)*

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
             verification's ring has that id — it holds 'e7bca259f5837226' and no
             retired keys.

  + Audit__RetiredChainKeys__0__Key and __LastSequence, founding key still unset:
    EXIT=3   CANNOT PROCEED: this tool is not configured to read the chain.
             Audit:FoundingChainKey is required once a key has been retired. ...
             (verify, anchor and export all answer this, identically)

  + Audit__FoundingChainKey:
    EXIT=0   CHAIN INTACT: 3 rows verified.
  ```

  **1 is the code this document tells you to alert on as CHAIN BROKEN**, and the middle run shows
  what the same procedure answers when the ring is merely INCOMPLETE: 3, which says the tool is not
  configured to read the chain and names the setting to add. The same procedure, two runs apart,
  sending an operator to opposite places — 3 to the configuration, 1 to an incident. The anchor-key
  omission a release
  earlier also answered 3, which is what makes this one the worse of the two: the first rotation
  would have paged somebody, from the page written to stop exactly that, and the verdict would have
  named a key id as evidence.

  *(That sentence read "worse than the exit 3 the missing anchor key produced" until review. True of
  the earlier incident, and wrong where it sits: the 3 in the block above is the middle run's, a
  FOUNDING-key 3, so it sent a reader to check `Audit:AnchorKey`, which that run supplies and which
  is fine. A page read under pressure is read locally. The first correction of this sentence said
  "six lines above" and counted wrong — which is why it now names the run instead of counting lines,
  the same rule this page states for the exit-code cross-reference further up.)*

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

  ⚠️ **`SecureStringToBSTR` HANDS YOU PLAINTEXT IN UNMANAGED MEMORY AND NOBODY FREES IT FOR YOU.**
  Every version of this block above called it and threw the pointer away, so each key stayed in
  cleartext in the process's unmanaged heap for as long as the session lived — which on this page is
  a shell somebody keeps open through an incident. `ZeroFreeBSTR` is the documented counterpart: it
  overwrites the buffer before releasing it. It belongs in a `finally`, so an interrupted `Read-Host`
  does not leave the allocation behind, and once it is in a `finally` it is worth writing once:

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

  Run, not reasoned about: the function round-trips a 42-character key on **Windows PowerShell
  5.1.26100.9278**, which is the edition this block exists for, and on PowerShell 7.6.5 beside it.
  **None of this makes the value private from the process** — an environment variable is readable by
  anything running as you, and the PowerShell 7 block above hands `-AsPlainText` a managed string
  that the GC may copy. What it removes is the one copy nothing ever reclaims.

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

  It is counted in SEQUENCE numbers. That is a row count unless somebody holding **the keys covering
  a stretch** removed rows from it and recomputed the links behind them — the walk checks the links,
  never the contiguity — so **a `SELECT COUNT(*)` that disagrees with the span is the finding, not a
  fault in the tool.** It is the only trace that particular deletion leaves. *(This named
  `Audit:ChainKey` alone. Since the ring a retired key answers for its own epoch, so its holder can
  do the same inside that epoch — the same narrowing the intact verdict already carries.)*

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
  - **`THE ANCHOR CHAIN DID NOT VERIFY` on an `export` run is a verdict, not a note about the
    file.** `export` writes the copy anyway and says so underneath: a chain that has stopped
    verifying is when an off-machine copy is worth the most, and copying asserts nothing about what
    it copies. **Read the `Kind` and the anchor sequence printed below that line** — that is the
    finding. The file is evidence of the broken state, not a repair of it.
  - **`1` from `export` is about the ANCHOR chain, not this one.** `ExportCommand` takes its code
    from the anchor verification it just wrote out, so it can exit 1 while `verify` on the same
    database exits 0. Read `1` as "the chain THIS VERB is about is broken", and the verb decides
    which.

  Both are confirmed in `VerifyCommand.Report` and in the return `ExportCommand.RunAsync` makes once
  it has walked the anchor chain — not its LAST return, which is the exit-3 refusal for an audit
  store it could not read. Neither is a defect
  in this page — the page is where they become visible, because it is the only document that treats
  the codes as one vocabulary shared by every verb. **Whether the tool should instead give the anchor
  chain its own code is a change to what alerts fire, and is not decided here.** Until it is, an
  alert on `verify` returning non-zero does not cover the anchor chain, and nothing else does either.

  **`3` is the one to wire an alert on separately from `1`.** It covers everything that stopped the
  walk before it could reach a verdict: a MISSING or too-short `Audit:ChainKey` **or
  `Audit:AnchorKey` — either alone stops all three verbs, which is the failure this page's own
  recovery procedure produced until 2026-08-28** — an unreachable server, a connection string that is
  malformed or absent, **the states step 6 is about — the `AuditEvents` table renamed, dropped or
  never migrated, or a login that cannot SELECT from it** — **and every way the KEY RING refuses to
  build**, which is ten guards in the constructor: a blank or under-32-character `Audit:ChainKey`;
  a `Audit:RetiredChainKeys:N` entry that is null, blank, or under 32 characters; a `LastSequence`
  below 1, at the top of the range, or equal to the entry before it; the same key listed twice or
  equal to the current one; and `Audit:FoundingChainKey` missing once anything is retired or naming
  material the ring does not hold. *(This list claimed to be every way and omitted two: the
  `Audit:ChainKey` floor, and the null entry — which is the one a caller reaches by passing a null
  directly, since a JSON `null` binds to a non-null entry with an empty key and is caught by the
  blank guard instead.)*

  All of those answer 3 in all three verbs. The per-entry ones name the offending
  `Audit:RetiredChainKeys:N` by its CONFIGURATION index — which is not its position after the
  boundary sort, so it is the one to edit. The three that are not per-entry — a short or missing
  `Audit:ChainKey`, and either founding-key refusal — have no index to give and name the setting
  instead.

  ⚠️ **"Validated at startup" IS true of the ring in the API now — by a different mechanism than
  that phrase suggests.** The two audit KEYS are options validation and do stop the verifier before
  it reads. The RING's rules live in `AuditChain`'s constructor and still do: nothing was moved into
  a validator, deliberately, because two copies of a structural rule drift and one root then holds a
  rule the other does not. What changed is that the API resolves the chain once during startup, so
  the constructor fires at the deploy rather than on the first request that opens the database. In
  the VERIFIER it still fires per verb, which is the same guarantee for a tool with no startup to
  speak of. The measured transcript is in the bullet on the verifier's three verbs, further up this
  page. *(This pointed at
  **RETIRING A KEY TAKES THREE VALUES**, which holds a command to run and no output at all.)*
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
