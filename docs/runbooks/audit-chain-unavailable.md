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
unreachable store — long past the point where anything asking had given up. If `/health/ready` ever
does hang, that bound has been removed, and the endpoint is no longer evidence of anything.

**The body is the point, and it is why the `curl` above does not discard it.** It names WHICH check
failed — exactly the distinction step 1 asks you to make. Note what that observation shows: the
database was perfectly healthy while the audit store was not, so a bare `503` would have sent you
hunting a database outage that was not happening.

`503` with `audit-chain` reporting **unhealthy** means the audit store is **unreadable** from this
instance. Unreadable, not unreachable: the probe fails the same way for a database that is down, an
`AuditEvents` table that was never migrated, a disabled or corrupt index, and credentials that can no
longer read it. Which of those it is: step 3 if it is a permission, step 6 if it is the table.
`200` means the probe read the table, and the cause is one of the narrower ones below.

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
no timeout at all. Treat it as "the probe did not finish", then work down the steps below: a
permission problem does not time out, so the causes worth chasing are an unreachable store, a locked
table, or a stuck transaction.

**3. Does it say `readable but NOT writable`?** Then this is a PERMISSION problem, not an outage,
and it has nothing in common with the rest of this runbook. The store answers every read and refuses
every audit row, so D1 refuses every money movement while the database looks perfectly healthy. The
probe asks `HAS_PERMS_BY_NAME` on `AuditEvents`, which honours role membership and `DENY`:

```sql
SELECT HAS_PERMS_BY_NAME('dbo.AuditEvents', 'OBJECT', 'INSERT') AS can_insert,   -- as the API's login
       HAS_PERMS_BY_NAME('dbo.AuditEvents', 'OBJECT', 'SELECT') AS can_select;   -- 0 here reads as 'unreadable' above
SELECT dp.name, dp.type_desc, p.permission_name, p.state_desc
FROM sys.database_permissions p
JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
WHERE p.major_id = OBJECT_ID('AuditEvents');
```

A `DENY` beats any `GRANT`, including one inherited from `db_datawriter` — that is the usual cause,
and it is invisible from the grant side alone. **Do not fix this by granting the API more than
INSERT on `AuditEvents`**: the chain is append-only by design, and a login holding UPDATE or DELETE
there is a larger problem than the outage you are ending.

**4. Is the table locked rather than unreachable?** The health check reads with `READUNCOMMITTED`, so
it stays healthy while the tail is merely locked by a slow writer. That is the case where readiness
says `200` and money movements still fail.

```sql
SELECT r.session_id, r.blocking_session_id, r.wait_type, r.wait_time, t.text
FROM sys.dm_exec_requests r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE r.blocking_session_id <> 0;
```

Look for sessions blocked on `AuditEvents`. The chain reads its tail with `UPDLOCK, HOLDLOCK`, so
**one** stuck transaction blocks every money movement in the system — measured: a three-second stall
delayed a deposit on an unrelated account, by an unrelated user, by 3,073–3,089 ms across three runs.
The unrelated movement waits out essentially the entire hold, so the number to find below is how long
the blocker has held it, not how many sessions are queued.

**5. Is a long-running transaction holding it?**

```sql
SELECT st.session_id, dt.transaction_id, dt.database_transaction_begin_time
FROM sys.dm_tran_database_transactions dt
JOIN sys.dm_tran_session_transactions st ON st.transaction_id = dt.transaction_id
ORDER BY dt.database_transaction_begin_time;
```

The oldest open transaction is the usual culprit. **`KILL` does not release the tail when you press
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

---

## What NOT to do

**Do not disable the audit chain to restore service.** The bank would then move money it cannot
account for, which is the exact state D1 exists to prevent — and unlike an outage, it leaves no trace
that it happened. An outage is recoverable; unaudited movements are not reconstructible.

**Do not raise `Audit:TailTimeoutSeconds` to push the failures away.** The bound is what turns a
thirty-second queue into a fast refusal; raising it makes every movement wait longer for the same
eventual failure, and holds a connection while it does. If the queue is legitimate rather than stuck
— a genuine burst — the fix is capacity, not patience.

**Do not delete rows to "unstick" the table.** Two different things happen, and the one that sounds
safer is the dangerous one.

Deleting an **interior** row is caught: the row after it records a predecessor that is no longer
there, and `VerifyAsync` reports the break at that sequence number. Deleting from the **end** is not
caught at all. The surviving prefix links perfectly and hashes perfectly, because verification only
ever looks backwards and has nothing to compare the end of the table against.

Measured, not argued — `AuditChainTests.TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit` writes
three rows, deletes the last one, and the chain still reports itself intact; the only trace is that
the count fell from three to two, which is evidence to somebody who wrote the old count down
elsewhere and to nobody who did not.

So truncation is the cheapest attack on this table — it needs **no key at all**, only write access —
and it is also the easiest thing to do by accident while trying to clear a stuck table at three in
the morning. ADR-0044 states the same limit and the honest claim it leaves: this chain detects
tampering by someone holding the database but not the key, **except at the end of the table**. Until
the head is anchored outside the system, the only witness to how many rows there should be is
somebody who wrote the number down.

---

## After recovery

- **Verify the chain.** Two environment variables and one command; the key and the connection
  string are passed through the environment rather than as arguments, so neither lands in your
  shell history.

  ```bash
  export ConnectionStrings__DefaultConnection="..."
  export Audit__ChainKey="..."
  dotnet run --project backend/tools/AzureBank.AuditVerifier -- verify
  ```

  It prints the verdict WITH the row count and the sequence range, because "intact" on its own is
  not an answer — a chain of zero rows links perfectly, and a table truncated to nothing reports
  exactly what a fresh one does. Compare the count against what you expected: a chain that verifies
  40 rows where yesterday it had 40,000 is intact and catastrophic, and only the numbers say so.

  Exit codes, for scripting it: **0** intact, **1** broken, **2** nothing to verify, **3** the tool
  itself could not start. The last two exist so an automated check cannot mistake a typo in an
  environment variable, or an empty table, for a clean bill of health.

  **If it reports a break at sequence 1, suspect the key before you suspect an attacker.** The row
  hash is an HMAC over `Audit:ChainKey`; a wrong key is well-formed, so nothing rejects it, and it
  mismatches from the very first row. A real tamper breaks where it happened, somewhere inside the
  table. The tool says this too, at the moment it matters.

  What it still cannot tell you is whether rows were removed from the END. That needs an anchor
  outside the system and is tracked separately — see ADR-0044.
- The refused movements were refused, not lost: no money moved, and no audit row claims it did.
  Customers can simply retry.
- If the cause was contention rather than an outage, the number worth capturing is how long the tail
  was held, and by what. That is the input to deciding whether the chain's single global tail needs
  to stop being single — a question ADR-0044 leaves open.
