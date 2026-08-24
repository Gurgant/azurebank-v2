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
       HAS_PERMS_BY_NAME('dbo.AuditEvents', 'OBJECT', 'SELECT') AS can_select;   -- 0 reads as "unreadable" above
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

## If the verifier reports CHAIN BROKEN

**A break has two families of cause and they need opposite responses, so classify before you act.**
Either somebody with write access changed or removed an audit row, or something entirely ordinary
produced the same verdict: an `Audit:ChainKey` that is not the one those rows were written with, or
a deletion of the OLDEST rows from outside the application — an archival job, a manual cleanup, a
restore of a partial backup. Both are measured below and neither involves an attacker, but they are
not equally likely. A wrong key happens on any deploy or rotation that exports the wrong value, and
it is the first thing to rule out. The other is not routine here: this application never deletes an
audit row, and ADR-0044 D5 leaves retention unsolved — so it is benign only if you can name the job
or the restore that did it.

**Classify first — it costs one command, and the tool already prints the two things that decide
it:** the KIND of break, and `Rows verified before the break`. The verifier only ever reads, so
running it again destroys no evidence and moves nothing: three rows read with the right key, then a
wrong one, then the right key again reported `CHAIN INTACT`, `CHAIN BROKEN`, `CHAIN INTACT`, and the
row count never moved.

- `does not match its own hash` with **`Rows verified before the break: 0`** — suspect the key
  before an attacker, and settle it by re-running with the key this deployment actually keeps. A
  wrong key is well-formed, so nothing rejects it, and it mismatches on the first row it reads —
  which is always sequence **1**, for the reason in the note under *After recovery*. Measured: three
  unaltered rows read with a valid but wrong key reported `CHAIN BROKEN at sequence 1`.
- the same verdict at **any sequence above 1** — NOT the key. A row numbered above 1 that records no
  predecessor got there by a write, which is the cheapest way to hide a deleted prefix. Measured:
  removing the oldest row and clearing the survivor's `PreviousHash` produces exactly this, with the
  CORRECT key. Treat it as the real thing.
- `does not match its own hash` with a **non-zero** count — an alteration at that row, *unless more
  than one key has written this table*. Nothing in the hashed payload records which key wrote a row,
  so if the secret was rotated, or two hosts or deployment slots run with different values, the walk
  verifies every row written under the key it holds and mismatches at the first row written under
  the other. Rule that out before you escalate; it looks exactly like tampering at one row.
- `expected to follow ... A row was deleted, reordered, or inserted` with **count 0** — the first
  row read names a predecessor that is not there, so the OLDEST rows are gone. An archival job and
  an attacker produce identical output here, so compare `Sequences read:` against the low end of the
  last run that came back INTACT. If it has moved up, ask whoever runs those jobs before you open an
  incident; if it has not moved, rows ahead of it went without the numbering changing, and that is
  not housekeeping. Measured: deleting the single lowest row from an intact chain of three gives
  `CHAIN BROKEN at sequence 2 ... expected to follow '(start of chain)'`, and a WRONG key against
  the same table prints the identical line — the link is checked before the hash, so on a chain with
  its head gone the key never enters into it. With a NON-ZERO count a row is missing or out of place
  in the MIDDLE, which removing the oldest rows cannot do — that one is the real thing.
- `could not be read at all` — a stored value contradicts the schema, which is itself a
  modification. Neither a wrong key nor a deletion can cause this.

**From the moment it is none of those, the table is evidence: do not repair it and do not write to
it.** A repair destroys the only record of what was done to it, and the chain cannot be "fixed" —
recomputing hashes requires the key, which is exactly what an attacker who reached the database is
assumed not to have.

**Capture, in this order, before anyone touches the database.** The verifier's full output including
the exit code; the sequence it names and the rows on either side of it
(`SELECT * FROM AuditEvents WHERE Sequence BETWEEN <n>-2 AND <n>+2`); the total row count; and the
SQL Server default trace or audit for recent writes to `AuditEvents` if it is enabled.

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

- **Verify the chain. Run this FROM THE REPOSITORY ROOT** — the `--project` path is relative to it,
  and from anywhere else `dotnet run` fails to find the project and exits **1**, which this document
  defines four paragraphs below as CHAIN BROKEN. That failure happens before the tool starts, so
  nothing inside it can translate the code. Confirm with `pwd` if you are not sure.

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
  read -rsp 'Connection string: ' ConnectionStrings__DefaultConnection && export ConnectionStrings__DefaultConnection && echo
  dotnet run --project backend/tools/AzureBank.AuditVerifier -- verify
  ```

  PowerShell, since it is the history file this paragraph cites. **`-AsPlainText` on
  `ConvertFrom-SecureString` is PowerShell 7 or later**; on Windows PowerShell 5.1 it does not exist
  and both assignments end up EMPTY, which the verifier then reports as a missing key. Check with
  `$PSVersionTable.PSVersion` first.

  ```powershell
  # PowerShell 7+
  $env:Audit__ChainKey = (Read-Host 'Audit:ChainKey' -AsSecureString | ConvertFrom-SecureString -AsPlainText)
  $env:ConnectionStrings__DefaultConnection = (Read-Host 'Connection string' -AsSecureString |
  ConvertFrom-SecureString -AsPlainText)
  dotnet run --project backend/tools/AzureBank.AuditVerifier -- verify
  ```

  ```powershell
  # Windows PowerShell 5.1
  $key = Read-Host 'Audit:ChainKey' -AsSecureString
  $env:Audit__ChainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
      [Runtime.InteropServices.Marshal]::SecureStringToBSTR($key))
  $cs = Read-Host 'Connection string' -AsSecureString
  $env:ConnectionStrings__DefaultConnection = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
      [Runtime.InteropServices.Marshal]::SecureStringToBSTR($cs))
  dotnet run --project backend/tools/AzureBank.AuditVerifier -- verify
  ```

  It prints the verdict WITH the row count and the sequence range, because "intact" on its own is
  not an answer — a chain of zero rows links perfectly, and a table truncated to nothing reports
  exactly what a fresh one does. Compare the count against what you expected: a chain that verifies
  40 rows where yesterday it had 40,000 is intact and catastrophic, and only the numbers say so.

  Exit codes, for scripting it: **0** intact, **1** broken, **2** nothing to verify, **3** no verdict
  (the store could not be read), **4** the command line was wrong, **5** interrupted. Only 0, 1 and
  2 are statements about the CHAIN.

  **`3` is the one to wire an alert on separately from `1`.** It covers everything that stopped the
  walk before it could reach a verdict: a MISSING or too-short `Audit:ChainKey`, an unreachable
  server, a connection string that is malformed or absent, **and the states step 6 is about — the
  `AuditEvents` table renamed, dropped or never migrated, or a login that cannot SELECT from it.**
  That last group is why the tool's own closing advice ("check the connection string and the key")
  is a starting point and not the list: read the exception line above it, which names the cause.

  **A WRONG key is not among them, and this runbook previously said it was.** A well-formed key that
  is simply not the one the chain was written with passes every check the tool can make, so the walk
  runs and the hashes mismatch: that exits **1**, the same as a tamper. There is no way around it —
  the two are indistinguishable to any check — which is exactly why the next paragraph exists.

  **`5` is not an incident.** Somebody stopped the walk — Ctrl+C, a killed job, a shutdown. Part of
  the chain was checked and the rest was not, so there is no verdict, but nothing is wrong with the
  store or the key and the triage list above does not apply. It is separate from `3` for that
  reason: an alert wired to `3` would otherwise hand whoever answers it a list of environment
  failures to chase after a colleague pressed Ctrl+C. `e2fsck` keeps the same distinction (32
  "canceled by user request" against 8 "operational error"), as does AIDE (25 for SIGINT against 18
  and 24). Re-run it to get an answer.

  **`4` exists because the framework collided with this vocabulary.** System.CommandLine reports
  every parse failure as exit 1, so running the tool with no arguments at all — the likeliest
  mistake there is — used to report a tampered audit trail. Measured, and now translated.

  **If it reports a HASH MISMATCH before any row verifies, suspect the key before you suspect an
  attacker — and read the sequence as well as the count.** A wrong key mismatches on the first row
  it reads, and that row is sequence **1**. It cannot be anything else: the walk checks the LINK
  before the hash, so the only row that can reach the hash check first is one recording no
  predecessor, and `AuditChain.Link` writes that only into an empty table, where the row it writes
  is sequence 1. `Rows verified before the break: 0` says the key is worth checking, and `Sequences
  read: 1 to ...` on the next line confirms it.

  **The same verdict above sequence 1 rules the key OUT** — a row numbered above 1 recording no
  predecessor got there by a write.

  **A withdrawn argument, left visible.** This note used to say the tell was the count alone, "not
  at sequence 1", because a purged chain begins at 5,001 and the hint would stop firing on the
  oldest tables. That reasoning is unreachable, and the tool was gated on it. On a chain whose head
  is gone the first row read records a predecessor that is missing, so the walk reports a LINK break
  and never reaches the hash check at all: measured, a wrong key against a decapitated chain prints
  output identical to the correct key. What the loosened gate did produce was the dangerous
  direction — an attacker who removed the oldest rows and cleared the survivor's `PreviousHash` was
  met with "usually means the wrong Audit:ChainKey ... Confirm the key before escalating".

  The row hash is an HMAC over `Audit:ChainKey`; a wrong key is well-formed, so nothing rejects it,
  and it mismatches on the first row it reads. It is also the ONLY break a wrong key can produce — it
  cannot make a row unreadable and it cannot change what a row records as its predecessor, so on
  those two the tool deliberately stays silent about the key.

  What it still cannot tell you is whether rows were removed from the END. That needs an anchor
  outside the system and is tracked separately — see ADR-0044.
- The refused movements were refused, not lost: no money moved, and no audit row claims it did.
  Customers can simply retry.
- If the cause was contention rather than an outage, the number worth capturing is how long the tail
  was held, and by what. That is the input to deciding whether the chain's single global tail needs
  to stop being single — a question ADR-0044 leaves open.
