# Runbook — the audit chain is unavailable

**Symptom:** deposits, withdrawals and transfers are failing. Reads still work. Sign-in still works.

**Why this runbook exists:** ADR-0044 D1 makes an audit row atomic with the money movement it
describes, so an audit store that cannot be written stops the bank moving money. That is deliberate —
a movement that was never recorded is one that cannot afterwards be accounted for to the customer
whose money moved. Every source that endorses fail-closed pairs it with a runbook. This is ours.

---

## Confirm it in one call

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://<host>/health/ready
```

`503` with `audit-chain` reporting **unhealthy** means the audit store is unreachable from this
instance. `200` means it is readable, and the cause is one of the narrower ones below.

## What you will see in the log

```
SecurityEvent AuditChainUnavailable: the audit chain tail could not be read within 5s,
so 1 pending audit row(s) and the action they describe are refused
```

That line is logged and writes **no audit row**, which is not an oversight. Every other refusal in
this system reaches `RecordRefusalAsync`, which opens its own connection so a rollback cannot erase
it — but that writes to `AuditEvents` and therefore takes the very lock that just failed. **For a
chain failure the chain cannot be where you report it.** The log line and the health check are the
whole signal.

---

## Triage, in order

**1. Is the database reachable at all?** If `/health/ready` also reports `database` unhealthy, this
is not an audit problem — it is the database, and the audit chain is simply the first thing to
notice. Treat it as a database outage.

**2. Is the table locked rather than unreachable?** The health check reads with `READUNCOMMITTED`, so
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
delayed a deposit on an unrelated account, by an unrelated user, by 2,820 ms.

**3. Is a long-running transaction holding it?**

```sql
SELECT session_id, transaction_id, database_transaction_begin_time
FROM sys.dm_tran_database_transactions dt
JOIN sys.dm_tran_session_transactions st ON st.transaction_id = dt.transaction_id
ORDER BY database_transaction_begin_time;
```

The oldest open transaction is the usual culprit. Killing it releases the tail and the queue drains.

**4. Is the table itself intact?** A missing table, a broken index on `IX_AuditEvents_Sequence`, or a
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

**Do not delete rows to "unstick" the table.** The chain is hash-linked; removing a row breaks the
verification of every row after it, permanently.

---

## After recovery

- Run the chain verification and check that it reads a non-zero count as well as reporting intact —
  a verification that read nothing also says "intact", which is why the count is asserted everywhere
  in the suite.
- The refused movements were refused, not lost: no money moved, and no audit row claims it did.
  Customers can simply retry.
- If the cause was contention rather than an outage, the number worth capturing is how long the tail
  was held, and by what. That is the input to deciding whether the chain's single global tail needs
  to stop being single — a question ADR-0044 leaves open.
