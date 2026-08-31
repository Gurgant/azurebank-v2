# ADR-0044: Security events go to an append-only, hash-chained table

**Status:** Accepted · **Date:** 2026-08-19 · Gives the event vocabulary in
`AzureBank.Shared/Constants/SecurityEvents.cs` a durable destination — it had none, and no ADR of its
own either. Prerequisite for B1 (recording the acting user on every transaction) and B3 (the PSD2
Art. 72 evidence pack). Supersedes nothing.

## Context

Seventeen security events are logged, and that is all that happens to them. A log is a stream: it
rotates, it is writable by whoever can reach the host, and nothing about it is evidence. PCI DSS v4
10.3.2 asks that audit logs be protected from modification; NIST SP 800-53 AU-9 asks the same;
PSD2 Art. 72 requires an institution to be able to reconstruct what happened. None of that is
satisfied by `ILogger`.

The gap was found while doing something else — checking whether an account closure was audited at
all — and the answer was that there was no audit store to be audited into.

## Decision

A single `AuditEvents` table, written in the same transaction as the action it describes, with each
row hash-chained to the one before it.

### D1 — a failed audit write fails the business action

`IAuditService.Record` only calls `Add`. The row rides the caller's existing `SaveChangesAsync`, so
either both land or neither does. This is the contract `IdempotencyService.MarkExecutedPending`
already established in this codebase, so no new transaction machinery was introduced.

The cost is stated plainly: if the audit table is unwritable, audited operations stop. That is the
intended trade — better a blocked bank than an emptied one. Ratified by Vlad.

Refusals are the exception and need the opposite treatment: the refused operation's own rollback
would take the record of the refusal with it. `RecordRefusalAsync` therefore writes on its own scope
and commits immediately. The two names are deliberately hard to confuse, and each **throws** on the
outcomes that belong to the other: `Record` on `Refused` and `MitigationFailed`, `RecordRefusalAsync`
on `Succeeded` and `RetryCollision`. The second guard was missing until the first review round —
which meant a success could be committed out-of-band, D1 inverted.

Measured, not assumed —
`AuditChainSqlServerTests.WhenTheAuditRowCannotBeWritten_TheBusinessChangeIsRolledBackToo`: an audit
row too long for its column is refused by SQL Server, and the account rename that shared the save is
gone when re-read from a fresh connection.

**D1 HAS EXACTLY ONE EXCEPTION, and it is the refresh-token reuse path.** Everywhere else "no
evidence, no action" is the right trade. There, the action is CONTAINMENT of a token already proven
stolen, and refusing to contain it because the logging failed hands the attacker the family. The
first wiring awaited the refusal write *before* the try that guards `RevokeAllForUserAsync`, so an
unwritable audit table meant the revoke never ran, no `MitigationFailed` row was written either, and
the caller got the 500 that the comment inside that very catch calls out as inviting a retry of the
stolen token. Containment now runs first and the row is written after it. The row can still fail
loudly — the 5xx just no longer buys the attacker anything, because the family is already dead.
Pinned by `WhenTheReuseAuditRowCannotBeWritten_TheStolenFamilyIsStillRevoked`, which was verified to
go red when the order is put back.

### D1 re-examined against industry practice, 2026-08-20 — and kept

D1 was ratified before anyone had checked whether it is what real systems do. It has now been
checked, and the honest answer is **no standard requires it**:

- **NIST SP 800-53 AU-5** mandates *alerting* defined personnel plus organisation-defined actions.
  Its discussion offers shutting down as one of three examples, beside overwriting the oldest records
  and simply stopping audit generation.
- **AU-5(4) "Shutdown on Failure" is selected in no SP 800-53B baseline** — not Low, not Moderate,
  not High. It also permits degraded mode instead of shutdown, and waives itself entirely where an
  alternate logging path exists.
- **PCI DSS v4** requires failures be detected, alerted and responded to. Never that a payment be
  refused. No financial-services supervisory source requiring refusal could be found at all.

Two things nonetheless support the choice. The argument usually raised against it does not survive:
**a transactional outbox does not buy back availability**, because AWS's own guidance is fail-closed
on the write — *"if the outbox table update fails, the entire transaction is rolled back"*. The outbox
decouples delivery, not durability. And fail-closed auditing is deployed, not theoretical:
**HashiCorp Vault** refuses API requests it cannot audit, and **SQL Server Audit** ships
`FAIL_OPERATION` framed as *"use this option when maintaining a complete audit is more important than
full access to the Database Engine."*

**Vlad reaffirmed D1 after reading the above, and the reason he gave is not a compliance one.** It is
accountability: a movement that was never recorded is one the bank cannot afterwards account for to
the customer whose money moved — least of all a malicious one. No regulator compels that stance; it
is a claim about what this bank owes the person on the other side of the transaction, and it is made
deliberately in the knowledge that the standards stop short of it.

**What that obligates.** Every source that endorses fail-closed pairs it with things this system does
not yet have — an alternate path and a recovery runbook — and Vault's documented failure mode is the
one to fear: not a clean write error but a **hang**. A firewalled sink froze every Vault operation
even with a healthy second audit device configured. Our chain awaits SQL Server while holding
`UPDLOCK, HOLDLOCK` on the tail, so a hung connection stops every money movement with nothing thrown
and nothing alerting. Bounding that wait, emitting a refusal on the out-of-band path that can still
work when the enlisted one cannot, and writing the runbook, is the next change — not because D1 is in
doubt, but because a decision this deliberate deserves to fail loudly rather than silently.

**UPDATED 2026-08-20 — the three obligations above are discharged, and two of them resolved
differently than that paragraph expected.** The paragraph stands as written because it is the record
of what was decided and why; this is what happened when it was carried out. (An earlier edit replaced
it outright, which is the wrong thing to do to an ADR: it left the file describing an obligation as
though it had never been outstanding, and it discarded the Vault hang — the failure mode that
motivated the bound in the first place.)

**The cost was measured first, and it is larger than "the movement fails".** The chain's tail is
read under `UPDLOCK, HOLDLOCK`, so the lock is global to `AuditEvents` and every audited save queues
on it.
Stalling one tail read for three seconds delayed a deposit **on a different account, by a different
user**, by **3,073–3,089 ms across three runs** — `AuditChainContentionSqlServerTests` measures
exactly this. The unrelated deposit waits essentially the WHOLE hold, not part of it. (An earlier
figure of 2,820 ms appeared here; it understated the wait, because that version of the test gave the
first request a fixed 500 ms head start and so started measuring 500 ms into the stall.) A merely SLOW
audit store degrades the entire bank, not just the movement that touched it. Until now the only bound
was the global 30-second `CommandTimeout`, which covers the whole statement rather than the wait.

**So the wait is now bounded on that one statement** — `Audit:TailTimeoutSeconds`, five seconds by
default. D1 is untouched: a movement that cannot take the lock is still refused and still moves no
money. What changes is that it is refused in about a second instead of holding a connection and the
rest of the money path behind it for half a minute. Measured with the bound at one second and the
tail stalled for eight: **500 in 1,122 ms**. A command timeout rather than `SET LOCK_TIMEOUT`,
because the latter is SESSION-scoped and would ride a pooled connection into unrelated statements.

**The "alternate path" turned out not to exist, and that is the interesting finding.** The plan was
to report the refusal through `RecordRefusalAsync`, which opens its own connection — the shape
AU-5(4) calls an alternate logging path. It is not one here: it writes to `AuditEvents` and therefore
takes the very lock that just failed. **For a chain failure the chain cannot be where you report it.**
`SecurityEvents.AuditChainUnavailable` is therefore the one event in this vocabulary that is log-only
by NECESSITY rather than by choice, and `AuditChain` logs it before letting the exception go.

**And readiness now says it out loud.** `AuditChainHealthCheck` probes the store with
`READUNCOMMITTED` — deliberately lock-free, because a readiness probe that took the tail lock would
contend with the money path forever in order to report on contention. It therefore detects the case
that matters most and is least visible (store unreachable, table gone, database down) and NOT a tail
that is merely locked; that one surfaces through the log event above. Two instruments, two questions,
neither pretending to answer the other's.

**It asks THREE questions, not one, and this paragraph described only the first until 2026-08-24.**
Reading is the first; the probe also tests `DATABASEPROPERTYEX(DB_NAME(), 'Updateability')` and
`HAS_PERMS_BY_NAME(..., 'INSERT')`, and reports the read-only database and the refused INSERT as
SEPARATE verdicts because their fixes have nothing in common — a message about `GRANT`s would send
an operator hunting permissions on a database that is simply not `READ_WRITE`. Updateability is
tested BEFORE the permission question, so on a read-only database the `GRANT` question is never
asked at all. The write axes exist because **D1 refuses on the WRITE**: a probe that certified only
readability would be certifying the wrong capability. A fourth Unhealthy shape is not the probe's at
all — the framework's "A timeout occurred while running check.", which is how the cooperative
readiness budget surfaces to an operator.

**Why this paragraph was stale, which is the part worth keeping.** The write axes were added in a
later round of the same PR, after this text was written, and the squash hides that it went stale
rather than being born wrong. The runbook was corrected then and this was not, so for **27 hours** —
`1c1f941` on 2026-08-23 to the correction on 2026-08-24 — the document read under pressure was right
and the decision record was not. (An earlier draft of this sentence said "for a month". Measured
with `git log -S`: the runbook did not exist before `1c1f941`, and this whole file is five days old,
so a month was impossible under any reading — an unmeasured number, in the change whose own subject
is unmeasured numbers.) **When a probe's question changes, grep every document that describes it.**

The recovery procedure is `docs/runbooks/audit-chain-unavailable.md`, including the two things not to
do under pressure: disable the chain, or raise the bound to push the failures away.

**What remains open.** Two things, named rather than left to be discovered.

The tail is a single global row, so the chain serialises every money movement in the system by
construction. Nothing here changes that — it bounds the damage rather than removing the choke point.
Whether the chain should be partitioned is a real question this ADR does not answer.

**And there is still no way for an operator to VERIFY the chain.** `AuditChain.VerifyAsync` exists
and the suite calls it, but nothing exposes it — no endpoint, no CLI, no job. So the runbook can tell
an operator that movements are being recorded again, and cannot tell them the hashes still link. For
a design whose whole claim is tamper-evidence, that is the gap worth closing next: the property is
only as good as someone's ability to check it outside a test run.

**CLOSED 2026-08-23 by `tools/AzureBank.AuditVerifier`.** A console tool rather than an endpoint,
and the reason is the API's own shape: every controller carries a bare `[Authorize]`, so there is no
role to hang an operator-only route on and an endpoint would let any authenticated customer trigger
a full walk of the audit table. A tool makes the authorisation the honest one for a forensic act —
reach the database, and hold the chain key. Exposing it also forced a fix in `VerifyAsync` itself,
which read the whole table with `ToListAsync`: measured at 20,006 rows that was 12 MB of managed heap
and linear, so millions of rows would have been gigabytes for a walk that needs one row at a time.
It streams now. **The truncation gap above is NOT closed by this** — the verifier reports the count
and the range precisely because it cannot answer that question itself.

### D2 — the chain now, the SQL Server ledger later

Each row carries an HMAC-SHA256 over its own fields **and its predecessor's hash**. Keyed rather than
a bare digest, for the reason `StepUpOptions.BindingKey` gives: every field of an audit row is
enumerable, so an unkeyed hash could be recomputed by anyone holding the table. `Sequence` — the
column verification orders by — is inside the payload, which is why the marker read `v2`.

**Amended 2026-08-26: the marker is no longer a literal, and the version prefix's job has changed.**
Every row now stores the payload rendering that wrote it, and every row written from here on also
stores a non-secret identity of the key that signed it, both inside the hashed payload. Rows written
before the migration store no identity, because none was ever recorded, and they verify under the
founding key. Verification recomputes each row under the scheme it declares. Before this, the prefix
existed to invalidate EVERY previously computed value when a field was added; now it says WHICH
scheme wrote THIS row, so adding a field invalidates nothing already written. That inversion is what
makes a payload change or a key rotation survivable at all — until now either one made the verifier
reject correctly written rows and report them the way it reports tampering. It lands before any
external anchor exists, because the first token would freeze the rendering and the key permanently.

**A NULL key identity means no identity was recorded, not some particular key.** Such rows are
verified under the FOUNDING key — today that is `Audit:ChainKey`, because it is the only one there
has ever been. The word is deliberate: whatever adds a second key must add a ring entry for the
founding key rather than silently re-point history at whatever is current. The migration writes no
identity onto those rows, because none was ever recorded and inventing one would put an
unfalsifiable claim outside their hashed payload.

**A row the verifier cannot render, or that names a key it does not hold, is reported as UNCHECKED
and it is a BREAK.** Not a configuration note — treating it as one would hand an attacker a muzzle,
since overwriting a tampered row's key identity would soften the verdict from tampering to
housekeeping. Both values are inside the row's own hashed payload, so a stored value that is not
what the schema says it must be is itself a modification.

**What this does NOT change:** the sentence above about an attacker holding both the database and
the application's secrets. Publishing a 64-bit identity derived from the key gives a database-only
attacker an offline oracle for confirming a guessed `Audit:ChainKey` — and they already have that
oracle in every row's `RowHash`, so it is not a widening.

**A CORRECTION, because the first version of this section was too strong.** It said removing a row
"breaks every link after it". That is true of an INTERIOR row and false of the TAIL. Deleting the
last row, or the last thousand, leaves every surviving row hashing correctly and linking correctly,
because `VerifyAsync` only ever looks backwards and has nothing to compare the end of the chain
against. Truncation is therefore undetected — and, unlike the rewrite case below, it needs **no key
at all**, only write access to the table. Confirmed by walking the loop, and stated here rather than
left for a reader to discover.

**Now measured as well as reasoned (2026-08-22).** The test
`AuditChainTests.TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit` writes three rows, removes
the last, and asserts the chain still reports itself INTACT with the count down to two. It asserts
the uncomfortable direction deliberately. It exists because the runbook had gone on repeating the
too-strong claim this very section had already withdrawn.

**Corrected 2026-08-25: that test is not a tripwire, and this paragraph used to say it was.** It read
"if the head is ever anchored the test goes red, and the documents resting on this limit get
corrected with it", which is wrong twice. The word is TAIL — `head` is sequence 1, the row a
truncation spares, and this repository has a rule against exactly that slip. And the test asserts on
what `AuditChain.VerifyAsync` returns, while an anchor check needs a token store and a pinned trust
root, so it belongs in the operator-runnable verifier. The tail can be anchored with this test still
green.

**It is not retired then, either**, which is the easier mistake to make from here. The assertion
stays true because anchoring does not touch the walk, and it stays useful because green is the right
answer at that layer: the chain alone cannot see a truncated tail, which is the reason an anchor is
wanted at all. It gets RESCOPED instead — the name claims truncation "is not detected", a statement
about the system, and after anchoring the system detects it one layer up **for the rows a trusted
anchor already covers**. Not for the rest, and that qualifier is the guarantee rather than a
footnote to it: truncation of rows written since the last anchor stays exactly as invisible as it is
today, which is what makes the interval the thing being promised rather than a scheduling detail.
"Trusted" carries its own weight — an anchor checked against a root nobody pinned is not evidence of
anything. A second test at the verifier layer asserts what the anchor catches. This ADR,
`docs/runbooks/audit-chain-unavailable.md` and `docs/deferred/anchoring-the-audit-trail.md` are the
checklist, and nothing announces the day.

What this also does not defend against: an attacker holding both the database and the application's
secrets can rewrite a row and recompute the chain. ~~Both gaps close the same way — a digest anchored
outside the system, which is SQL Server's ledger, deferred rather than rejected.~~ *(corrected
2026-08-27: there are TWO controls and they close different layers — immediately below.)* Until then
the honest claim is narrow: **this chain detects tampering by someone who holds the database but not
the key, except at the end of the table.**

*(⚠️ Amended by D7, 2026-08-30: read "**not the key whose EPOCH that row falls in**". Once a key is
retired the ring holds more than one, and a holder of a RETIRED key can rewrite the rows inside that
key's epoch and recompute them — bounded ABOVE by its `LastSequence` and BELOW by a `FirstSequence`
derived from the previous retirement, so one compromised key reaches neither the rows later keys
wrote nor the rows earlier ones did. The singular reads as a stronger claim than the system makes,
and the places that quoted it have been amended with it. This note first said "unbounded below",
which was true for the hour between the two commits that gave the epoch its two ends.)*

**⚠️ CORRECTED 2026-08-27. The struck sentence named one control where there are two, and the
correction sits against it rather than in a section further down, which is the whole of the rule
`docs/adr/README.md` now states.** SQL Server's ledger and an RFC 3161 timestamp are COMPLEMENTARY
rather than alternatives, because they close different layers.

The ledger closes the WRITE. Once a row is committed, `APPEND_ONLY` refuses an UPDATE or a DELETE at
the engine, and does it uncatchably — a `BEGIN TRY … BEGIN CATCH` around the attempt never reaches
the CATCH block, because the batch simply dies — while `DROP TABLE` leaves the rows readable in
residue that cannot itself be dropped, altered or renamed (measured: **Msg 37427**). That is the one
property no application code can buy, and it needs nothing outside this machine.

An RFC 3161 timestamp closes the ANCHOR. It is time issued by somebody else, which is the only thing
that constrains the party holding every key — and that party is the operator, not the attacker the
chain is built for.

**Neither substitutes for the other.** Enforcement without outside time still leaves whoever owns the
machine free to drop the whole database and start again: dropping a DATABASE containing ledger tables
is permitted, it is dropping or truncating the TABLE that is refused. Outside time without
enforcement anchors a table that any connection can still rewrite in place, since an anchor certifies
only what was there when it fired. Both are deferred rather than rejected, and
`docs/deferred/anchoring-the-audit-trail.md` argues the second at length.

**Why one sentence could plausibly name either, which is the part worth keeping rather than merely
fixing.** The ledger has two halves with opposite requirements. Its DIGEST is an anchor and needs
exactly what a timestamp token needs — a copy held somewhere the operator cannot revise, refreshed on
a schedule. Its ENFORCEMENT needs neither. The struck sentence was written while the digest still
looked like a local answer to the anchor problem; measuring that is what removed it, since automatic
upload rejects a local emulator (12136) and what survives — `sp_generate_database_ledger_digest`, one
JSON row, no destination — has nowhere of its own to go. The claim outlived its premise, and naming
one control where there are two is what made it read as settled.

**An anchor record now exists, and it does NOT close that end.** Running the verifier's `anchor`
mode walks the chain once and records what it found — how far it reached, how many rows it held, and
the tail's hash — chained to the record before it and authenticated under `Audit:AnchorKey`, a sixth
secret the row chain does not use. ⚠️ **It detects no truncation.** Truncate the rows above some
sequence, delete every record covering past it, and both chains verify perfectly, because each links
backwards only; `ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit` asserts
exactly that. *(Qualified 2026-08-28, and the qualifier is narrow: `verify` now prints the UNCOVERED
WINDOW — how far past the deepest anchored sequence the table runs — so a truncation that left the
anchor records BEHIND comes out as a negative window and is named. Deleting the covering records
too, which is the sentence above, still produces perfect silence. What the window adds is that the
lazy version of the attack stopped being free.)* What the record buys is narrower and real: DELETING
an INTERIOR record is loud, because the counter gaps and the links stop meeting, while MINTING one
needs the key. *(Qualified 2026-08-28: "loud" means INTERIOR only. A SUFFIX removal leaves the
survivors at 1..n with every link met, and nothing in the walk asks how tall the chain ought to be —
which is exactly why the attack described above it is a suffix removal in BOTH tables. Measured both
ways by `DeletingAnchorsIsLoudONLYINTHEINTERIOR_ANDASUFFIXISSILENT`. The unqualified version was
repeated in nine places across this repository before anybody checked it.)* **The evidence is the
pair `(anchor number, covered-through sequence)` an operator wrote down somewhere this machine
cannot reach** — the counter alone can be regrown by re-running the command, and the sequence cannot
be regrown downward.

**And it no longer rests on somebody copying two numbers correctly.** `export <path>` writes every
anchor record to a file outside the database, one JSON object per line, and refuses to overwrite an
existing copy — overwriting the earlier one with the current state is the exact move a copy exists to
make visible, so this verb is never able to do it. The format is the comparison: a later export
differs from an earlier one by whole lines, so `diff` compares two of them and `git diff` compares
published ones. `docs/audit/anchors.sample.jsonl` is a committed sample, and `docs/audit/README.md`
says what it does and does not show.

⚠️ **The export does not close the end of the table either, and the reason is the same one.** A file
this machine wrote to this machine's disk has been seen by nobody, and the property an anchor buys is
scoped to what a third party HAS seen — so the write is where the control begins rather than where it
finishes, and the operator still supplies the elsewhere. It is a verb somebody types, which makes it
a demonstration rather than a constraint. **It adds no exit code, and this record deliberately does
not restate them.** An earlier draft of this paragraph listed five of the seven — leaving out 4 for a
missing path and 5 for an interruption — which was a fifth copy of a list that already lives in four
places and is already stale in one of them: `docs/runbooks/audit-chain-unavailable.md` enumerates
them *"for scripting it"* and stops at 5, so exit 6 appears nowhere an operator scripts from. D5's
own reasoning applies to the record as much as to the tool: the answer to a list that has outgrown
its own description is fewer copies, not a more complete one. The list is in
`AzureBank.AuditVerifier`'s `Program.cs` header, beside the constants it describes.

**And it does not constrain the operator**, who holds the key and can write honest-looking records
over a truncated table. An external timestamp — the ANCHOR half of the pair corrected above, not a
second answer to the same question — is what would change that, and it is not built.
**An anchor certifies EXISTENCE AT A TIME, never AUTHENTICITY:** it can only strengthen the standing
of whatever was in the table when it fired, including a forged row appended before it.

**Why that end is still open, written down rather than left to be inferred:**
`docs/deferred/anchoring-the-audit-trail.md`. The short version is that an anchor is only as good as
its freshness, freshness needs something running unattended, and nothing runs unattended here — so
the control would be a demonstration rather than a constraint. That document also records the four
things that have to be settled before the first token is ever issued, three of which are one-way
doors.

**A withdrawn argument, left visible.** The first version of this decision claimed the ledger was
impractical because its digests require an Azure storage destination. That is false, and measuring it
is what showed so: `sp_generate_database_ledger_digest` takes no destination at all — automatic
upload is what needs Azure. The ledger is deferred because `APPEND_ONLY` is a one-way door on a
schema still moving (measured: `DROP TABLE` leaves undroppable residue, Msg 37427; only nullable
columns may be added, Msg 37387), not because it cannot be done here. A local emulator is genuinely
out — Azurite is rejected with error 12136 over both http and https, measured directly rather than
inferred, after two research agents contradicted each other on the point.


### D3 — the chain is applied in the `SaveChanges` funnel

Not in the writer, and not in a `SaveChangesInterceptor`. The writer cannot hold a lock across the
insert; an interceptor is silently absent under test, because `CustomWebApplicationFactory` rebuilds
the `DbContext` registration and would drop it — every audit row would then be written with an empty
hash, and the guarantee would exist only where nobody looks.

`Sequence` is assigned by the chain rather than by an IDENTITY column. An IDENTITY exists only on a
relational provider, and roughly 585 of this project's tests run on EF InMemory, where `Sequence`
would stay 0 and the chain would have no order to be verified against. Ordering by `Id` instead was
tried first and was wrong: `Guid` ordering in .NET is not creation order even for a UUIDv7, and SQL
Server collates `uniqueidentifier` on a different byte order again.

### D4 — the eight BFF events stay log-only, and this says so

`RateLimitExceeded`, `CrossSiteRequestBlocked`, `StepUpRequired`, `StepUpWithoutSession`,
`RawRefreshBlocked`, `RawAuthEntryBlocked`, `SessionRequired` and `RefreshRejected` are raised in
the BFF, which has no database. The list is the count: naming them is what makes this checkable, and
`SecurityEventConstantTests.TheEventInventoryThisAdrStatesIsStillTheOneInTheSource` fails when a
new one appears without this section moving with it — which is how `RawAuthEntryBlocked` came to
be missing from it for one commit. Giving it one
means a second writer against the audit table, which is a larger decision than this one. They are
listed here so the next reader knows they were considered rather than missed.

### D5 — no personal data in the table

Actor and subject are stored as ids. `Detail` is JSON, capped at 1024 characters rather than MAX
precisely so nobody puts a stack trace — and the PII inside it — into a table designed never to be
purged. The AzureTag rename event records **neither** the old handle nor the new one, only that a
rename happened.

**CORRECTED 2026-08-20 after reading the primary texts.** The paragraph that stood here said: *"every
regime binding this system states a minimum retention — PCI DSS v4 10.5.1 twelve months, AMLD Art. 40
and PSD2 Art. 21 five years — and a minimum cannot be violated by keeping more. Erasure is answered
upstream: there is nothing personal in the table to erase."* The numbers were right and the reasoning
was wrong, in three separate ways. It is left quoted rather than deleted, because a record of what a
decision used to rest on is the point of this file.

- **PCI DSS v4.0.1 10.5.1 is twelve months, but the sentence has a second half** that was dropped:
  *"with at least the most recent three months immediately available for analysis"*. And more
  importantly, PCI DSS binds entities that *"store, process, or transmit cardholder data"* — this
  system never touches a PAN, so citing 10.5.1 as a governing rule here is probably a category
  error. It is kept as a design reference, not as an obligation.
- **AMLD Art. 40's five years is real, but the clock does not start at the event** — it runs *"five
  years after the end of the business relationship … or after the date of an occasional
  transaction"*. For a live customer that is open-ended. It also governs customer due-diligence
  documents and transaction records, not a security audit trail, so applying it to `AuditEvents` is a
  stretch even with the right number.
- **And it carries a DELETION duty, which is the part that breaks the old reasoning outright**:
  *"Upon expiry of the retention periods … obliged entities delete personal data"*. "A minimum cannot
  be violated by keeping more" is therefore false where a maximum also exists. An append-only chain
  cannot delete a row without breaking the HMAC linkage of every row after it — so **retention is an
  unsolved problem for this design, not a solved one**, and B3 inherits it. ⚠️ **NARROWED, not
    withdrawn, on 2026-08-29 by D6 below.** What is now answered is the records-management half: there
    is a stated period and a stated position — this table is never purged — together with the measured
    reason nothing here can purge it safely. What is NOT answered is the duty itself: D6's first
    version claimed erasure was solved upstream and three review findings took that apart, so the
    section now records that erasure is undischarged and names the three redesigns that would close
    it. The arrival of a policy is not the arrival of a solution, and D6 says so in its own words.
- **PSD2 Art. 21's five years is confirmed**, and narrower than a general audit rule: it binds Member
  States to require *payment institutions* to keep records *"for the purpose of this Title"*.
- **AMLD Art. 40 is superseded by Regulation (EU) 2024/1624 (AMLR) Art. 77 from 10 July 2027.** The
  period stays five years, and the clock explicitly also starts *"on the date of refusal to enter
  into a business relationship"* — which is directly about the refusal rows this design commits
  separately. A system being designed in 2026 should target Art. 77.
- **AMLR Art. 77(2) describes the shape of the pointer design**: a reference may be retained instead
  of a copy *"provided that … the obliged entities can provide immediately to competent authorities
  the information and that the information cannot be modified or altered."* That is what an audit row
  pointing at an immutable ledger row looks like. ⚠️ **This bullet said "blesses … outright" and D6
  below takes that apart** — 77(2) is a conditional derogation for obliged entities, this system is
  not one, and `EnforceTransactionImmutability` holds the second condition only against code that
  goes through the change tracker. Read as design corroboration, which is what it is; read as
  endorsement, it would put two conflicting legal positions in one file.
- **NIST SP 800-53 AU-11 still names no number** — *"[Assignment: organization-defined time period]"*
  — so the original parenthetical was correct and stands.
- **And the EDPS says the unbounded chain is itself the exposure**: where security monitoring
  produces logs, *"the purpose and the retention period need to be well defined"*. "Designed never to
  be purged" is not a retention policy; it is the absence of one.

What survives unchanged: this table holds pseudonymous ids and no personal data in `Detail`, which is
why the problem above is a records-management one rather than a live GDPR breach. What does not
survive is the claim that retention was settled.

### D6 — retention is a policy, and this table is never purged

The section above establishes that retention is unsolved and stops there. Stopping there is itself
the problem the EDPS names: *"the purpose and the retention period need to be well defined"*.
"Designed never to be purged" describes a behaviour, not a policy — a policy has to say for how long,
what is deleted, and where. This is that decision.

**First, the question every obligation turns on: is this an obliged entity? No.** AMLD Art. 40 and
AMLR Art. 77 bind *obliged entities*, a term the AMLR defines and this system does not meet under
any reading — the definitional article itself was NOT read, so it is not cited here, which is the
same discipline the two rejected citations above earned; PSD2 Art. 21 binds *payment
institutions*, which are authorised undertakings; PCI DSS binds entities that *"store, process, or
transmit cardholder data"* and this system never touches a PAN. **None of them binds this system
today**, and writing otherwise would be the same fabricated-citation failure the section above was
corrected for. What is true is narrower and worth more: a system of this shape designed in 2026
should target AMLR Art. 77, because designing to no rule at all is how the absence of a period
becomes the exposure.

**The period, stated so it exists: five years, designed for ten.** Five is AMLR Art. 77 and PSD2
Art. 21; ten is the headroom Member States may add under AMLD Art. 40, which several use. The clock
runs from the end of the business relationship, not from the event — so for a live customer it is
open-ended by construction, which is a property of the rule and not a hole in this design. Twelve
months queryable and three immediately available is PCI DSS 10.5.1's shape, kept as a **design
reference rather than an obligation**, for the reason given above.

**The decision: `AuditEvents` is never purged.** ⚠️ **The first version of this section went on to
say that erasure is therefore solved upstream, and that was wrong in three separate ways — all three
raised in review on `21c81df` and all three corrected here.** The corrected position is narrower and
is the honest one: nothing in this design can purge this table safely, and the upstream answer covers
less than half of what it appeared to.

1. **The duty is on personal data, and D5 keeps DIRECT identifiers out of this table** — no name, no
   handle, no `Detail` carrying an amount or a balance. ⚠️ **Narrowed from "keeps personal data out",
   which contradicted the point below in the same list.** Pseudonymous ids are personal data while
   anybody can link them, so what D5 removes is the part that needs no linking; it does not put this
   table outside the duty.
2. ⚠️ **Pseudonymous is not anonymous, and deleting one table does not make it so.** An identifier
   stays personal data while anybody could reasonably link it back, which is a question about every
   copy that exists — backups, and the rest of this schema — not only about the row just deleted.
   Deleting a `User` row removes *one* mapping. It does not establish that none remains, and this
   ADR should not claim a conclusion that would need an inventory nobody has taken.
3. ⚠️ **And the row the money pointers point AT is refused deletion.**
   `EnforceTransactionImmutability` rejects `EntityState.Deleted` on `Transaction` in as many words —
   *"Transactions are immutable. Financial records cannot be modified or deleted."* So for the four
   money events, whose `SubjectId` names a ledger row, the target outlives any retention period, and
   with it the path from that row to an account and its owner. **The erasure-upstream answer applies
   to pointers whose target is deletable, and the majority of this table's pointers are not.** That is
   the same immutability the paragraph above cites as a STRENGTH, seen from the other side.

   ⚠️ **The guard is APPLICATION-LEVEL, and saying so makes the problem worse rather than smaller.**
   It runs in `SaveChanges` against the change tracker, `AuditEvent.SubjectId` carries an index and no
   foreign key, and no trigger defends the referenced row — the same stated limit this file already
   records for the anchor guard: *"this defends against our own future code, never against the
   adversary."* So a lawful deletion at expiry could only be performed the way an attacker would
   perform an unlawful one: raw SQL, around the guard, leaving a pointer that resolves to nothing.
   **The compliant act and the attack are the same act.** That is the collision in its sharpest form,
   and it is the reason this section ends by naming redesigns rather than procedures.
4. ⚠️ **AMLR Art. 77(2) is a conditional derogation, not a licence, and invoking it here contradicted
   the paragraph two above.** It permits a reference instead of a copy only where the entity is within
   scope, the information stays immediately retrievable and unalterable, and internal procedures
   document the retrieval. This system is **not** an obliged entity — stated above — so citing 77(2)
   as validation borrows authority from a rule that does not reach it. It is kept as **design
   corroboration**: the shape it describes is the shape built here, which is worth knowing and is not
   a legal basis.

**Why not a soft delete, which is the obvious alternative and was asked directly.** It would keep the
chain intact, which is its whole attraction, and it fails twice.

- **A flag is not erasure.** The data is still present, still readable, still processed. It would fix
  the chain problem without touching the only problem the deletion is for.
- **And the column has nowhere to live on this table.** Put `DeletedAt` inside the hashed payload and
  writing it modifies a committed row — the one thing the chain exists to make impossible. Put it
  outside and the chain cannot see it flipped, so the deletion is unauditable by the very system whose
  job is to audit. This ADR already refused that shape once, for the anchor's timestamp: *"a nullable
  slot somebody later populates is a legitimate-looking UPDATE path on an append-only table."*

**The collision, measured rather than argued.** A retention rule asks for a PREFIX deletion — the
oldest rows, from sequence 1 upward. `AuditChain.VerifyAsync` starts its walk expecting no
predecessor, so the lowest surviving row's `PreviousHash` does not match and the verdict is
`LinkBroken`, worded *"expected to follow '(start of chain)'"* — **the same verdict a tamper gets.**
That is deliberate: a chain able to tell an authorised removal from an unauthorised one would have to
trust whoever declared it authorised.
`AuditChainTests.DeletingTheOLDESTRows_IsLOUD_WhichIsWhyRetentionCannotPurgeThisTable` pins it, and
it exists because the cheap way to break this policy is not malice — it is somebody reading
"retention: five years" in a year's time and writing a job that deletes old audit rows, believing it
safe because the rows are old.

⚠️ **AND THE LOUDNESS DEPENDS ON A SURVIVOR, WHICH IS THE OPPOSITE OF THE COMFORTABLE ASSUMPTION.**
A retention job on a table where everything has expired deletes EVERY row — and then no survivor is
left to point at a missing predecessor, so `VerifyAsync` reports intact with zero rows verified,
which is exactly what it reports for an installation nobody has written to yet. Measured, by
`PurgingTheWHOLETable_IsSILENT_WhichIsTheOtherHalfOfWhyRetentionCannotUseIt`. **The partial purge is
indistinguishable from tampering and the total one is indistinguishable from a fresh start**, so the
difference between the two failures is not a safety margin — it is which mistake happens to be made.
The operator tool is one layer better and only one: it refuses to render an empty table green,
exiting `NothingToVerify`. That separates zero from non-zero, never "purged" from "new".

**What this does not solve, stated so nobody inherits it as settled.**

- **Erasure is not discharged, and for the money events it cannot be.** The first draft of this list
  said only that nothing enforces the produce-then-delete ordering. The correction above is stronger:
  on `Transaction` the deletion is not merely unordered, it is REFUSED, so the pointer's target
  outlives any retention period by design. Where the target IS deletable — a `User` row — the ordering
  is still unenforced, and an early deletion would break the very condition Art. 77(2) attaches.
- **Three things would close it, and each is a redesign rather than a setting.** Segmenting the chain
  into periods that can be dropped whole; per-subject encryption so that destroying a key erases the
  data without touching a row; or making ledger rows deletable, which trades this table's problem for
  a worse one two tables over. None is proposed here. Naming them is what stops the next reader
  concluding that the problem has no shape.
- **There is no purge job**, and there is nothing to run one — the same premise that defers anchoring
  defers this. The policy is a rule a person follows, which is weaker than a rule a system enforces,
  and saying so is the point.
- **The honest summary, so the arrival of a policy is not read as the arrival of a solution:** this
  section states a period and states that the table is never purged. It does NOT discharge a deletion
  duty, and if this system ever came within one, the work above is where that would start.

### D7 — a key ring, so a rotation stops destroying the history it protects

Rotating `Audit:ChainKey` used to cost a deployment its entire past. Every row written since key
identity existed records the identity of the key that wrote it — the older ones record none, and
this section returns to them under `Audit:FoundingChainKey`. A verifier holding a different key
matched none of the ones that do, and the walk broke at the lowest row with a verdict that reads
exactly like tampering. The one operational hygiene
measure a keyed design most obviously wants was the one it could not survive.

**The rows are not rewritten, and that was ratified before this code existed.** Re-hashing history to
the new key would invalidate every anchor ever issued, and — in the database — it is the same
operation the anchor exists to detect. So the fix is read-side only: `Audit:RetiredChainKeys` holds
the retired material, and verification picks the key the row names.

**⚠️ The ring SELECTS by `KeyId`; it never TRIES keys in turn**, and the difference is the entire
safety of rotating. The tail-anchor decision named the hazard in one sentence before there was
anything to name it about: a trial-keyring verifier accepts a row a **retired** key could have minted
at any sequence, so every rotation would widen the forgery surface instead of narrowing it. Selection
works because `KeyId` sits *inside* the hashed payload — a row cannot lie about which key to check it
with, because changing the claim changes the hash the check recomputes.
`AuditChainTests.ARowThatLIESAboutItsKeyIsCaught_WhichIsWhyTheRingSELECTSRatherThanTRIES` pins it,
and replacing the lookup with a loop reddens that test and nothing else.

**A retired key reads and never writes.** Writing always takes `Audit:ChainKey`. Possessing an old
key is the exact circumstance a rotation assumes has happened, so a ring whose members could also
write would hand the attacker the ability to append rows that verify.

⚠️ **AND THAT IS NOT ENOUGH ON ITS OWN — THE FIRST VERSION OF THIS RING WAS A REGRESSION.** Refusing
to write through a retired key stops OUR code from doing it. It does nothing about somebody who
holds the retired key and a database connection: they compute an honest hash under it, label the row
honestly, insert by raw SQL, and a ring that accepts any member key at any sequence verifies it.
**Measured before the fix: such a row, appended after the rotation, verified clean.** Before the ring
there was no such row at all — a retired key verified nothing, so its holder could forge nothing. An
unbounded ring therefore hands an old key a power it did not have, which is the opposite of what
rotating is for, and it is what the tail-anchor decision meant by "the forgery surface grows with
every rotation".

**So each retired key carries the sequence it stopped at**, and answers only at or below it. A row
above the boundary is refused even when its hash is correct, because a correct hash under a key that
had no business writing by then is what minting looks like.
`ARetiredKeyCannotMintAROWATTHETAIL_BecauseTheRingBoundsItToItsEpoch` pins it, and it was written as
a REPRODUCTION first: it failed against the unbounded ring before it passed against the bounded one.

**What the boundaries do not buy.** Inside its own epoch the retired key is as powerful as it ever
was — whoever holds it can rewrite that stretch and recompute it, exactly as the current key's holder
can rewrite the present. What they buy is that the damage stays THERE: the recorded boundaries
partition the sequence space, so a compromised retired key reaches neither the rows later keys wrote
nor the rows earlier ones did. Rotation confines a key to one epoch; it cannot make that epoch
unrewritable, which is the whole of what rotation can achieve without an anchor from outside.
*(This paragraph said the boundary "bounds a retired key's FUTURE, not its past" until the epoch
gained a start — the singular "boundary" is the tell, and the section added below never came back to
correct the section above it.)*

**⚠️ The FOUNDING key is named, never assumed — and the first draft of this ring assumed it.** Rows
older than the key-identity column carry a null `KeyId`, which means *no identity was recorded* and
never *the current key*. This ADR chose that word in advance: "whatever adds a second key must add a
ring entry for the FOUNDING key rather than silently re-point history at whatever is current." The
first version of the ring verified those rows under whatever `Audit:ChainKey` held today, which is
the forbidden thing said in code. `Audit:FoundingChainKey` now names it, is required as soon as
anything is retired — and not before, because until then there has only ever been one key — and must
designate material the ring already holds, so each key lives in exactly one place.

**⚠️ THE RING MADE AN INTACT VERDICT CLAIM LESS, AND EVERY SENTENCE THAT SAYS OTHERWISE IS NOW
WRONG.** Before it, `verify` ended with *"no row was altered by anyone who does not hold
Audit:ChainKey"*. After it, the honest statement is *a key in the RING* — because a retired key still
recomputes every row inside its own epoch, which is **What the boundaries do not buy** stated from
the operator's side. Rounding that back up in the one sentence somebody reads to conclude the
bank was not attacked is the worst place in the system to overclaim, so the tool says the weaker
thing.

The same staleness reached three more sentences, and only one of them was raised in review. A hash
mismatch on a row that names its key said the row *"records the key identity that the configured
Audit:ChainKey derives"* — false after a rotation, when the ring selected a RETIRED key, and it sends
an operator to compare two ids that are not supposed to match. Its sibling arm, one `if` away, still
offered *"a different Audit:ChainKey"* as the alternative for a row that records no identity, which
is checked under `Audit:FoundingChainKey`. And the verifier tool carried its own copy of the first
one — the copy an operator actually reads during an incident. **A verdict that names the wrong knob
is a wrong verdict even when its conclusion is right**, and the lesson repeats: the review named an
instance, the defect was a class, and grepping for the class is what found the other three.

**⚠️ AND THE BOUNDARY WAS OPTIONAL UNTIL REVIEW, BECAUSE THE FORGER PICKS THE PAYLOAD VERSION.**
Bounding retired keys by `KeyId` bounds every key a row can NAME. A `v2` row names none — that
version records no key identity — so it is checked under `Audit:FoundingChainKey` without naming it,
and the keyed bound never ran. **Measured: a row minted with the retired founding key, at the tail,
above its boundary, labelled `v2` — verified clean, `IsIntact = True`.** Before the ring the same row
needed the CURRENT key, so this was the ring handing an old key a power it did not have: the exact
regression **⚠️ THE BOUNDARY IS NOT BOOKKEEPING** in `RetiredChainKey` says the boundary exists to
prevent, reached by choosing a different payload version. **A boundary that one payload version can
walk around is not a boundary.**

The founding key now carries the epoch of the ring entry it designates — it inherits it rather than
configuring it twice, because a designation that could disagree with the key it names would be a
second place for the same fact to live. It is pinned by
`AV2RowMintedWithTheRETIREDFoundingKey_IsRefused_BecauseTheEpochBoundsItToo`, which verifies the
forgery under a raised boundary FIRST: the epoch is checked before the hash, so
a fixture whose forged hash was merely wrong would be refused by accident and prove nothing.

**⚠️ AN EPOCH HAS TWO ENDS, AND THE FIRST TWO VERSIONS OF THIS RING GAVE IT ONE.** Every boundary
check read `row.Sequence > last`. An upper bound stops a retired key minting ABOVE its retirement;
nothing stopped it answering for every sequence BELOW it, including the stretches older keys wrote.
**Measured:** with one key retired at 2 and a second at 4, the holder of the second re-authored
sequences 1 through 4 — relabelling the first key's two rows as its own — and the walk returned
`IsIntact = True`, four rows verified. So compromising the NEWEST retired key handed over the whole
history rather than one epoch, and each further rotation made the prize larger instead of smaller.
That is the second time on this branch that a half-bounded ring inverted the reason to rotate; the
first was the missing upper bound itself.

**The lower bound is DERIVED, never configured.** The recorded boundaries already partition the
sequence space: a key that stopped at N was preceded by one that stopped at N', so its epoch is
`(N', N]`, the first key's starts at 1, and the current key's starts one past the last retirement.
Asking an operator for the start as well would be a second place to state one fact — the objection
`DeriveKeyId` records against configured ids — and the two would drift with nothing detecting it.
Two retired keys claiming the SAME boundary are refused, because the rows beneath it would belong to
whichever sorted first and sort order is not something a configuration states.

**A rotation with no writes between it and the next one is the SAME configuration, not a different
one**, and this paragraph claimed otherwise: that such a key "gets an empty epoch and correctly
answers for no rows". Measured while auditing — 512 boundary triples, **zero** produce an empty
epoch, because sorting makes equality the only reachable collision and the refusal above is what
meets it. Refusing is right rather than merely what the code does: a key that wrote nothing has no
row naming its id, so a ring entry for it answers for nothing and its only effect is to make the
boundary beneath it ambiguous. The honest configuration leaves that key OUT of the ring.
`TheNEWESTRetiredKeyCannotREAUTHORWhatOlderKeysWrote_BecauseAnEpochHasTwoEnds` pins it, control
first — an honest three-epoch table must still verify, or the bound would be refusing history.

**A consequence worth stating: the key identity inside the payload is now the SECOND line, not the
first.** Epochs partition `[1, ∞)`, so a row belongs to exactly one key's epoch and naming any other
key puts it outside a range before its hash is recomputed. `ARowThatLIESAboutItsKey…` used to fail as
`HashMismatch` and now fails as `UnknownScheme`, earlier and with a better message. Both defences
still hold; only the order changed, and the test records the old expectation so that finding the new
verdict does not read as a regression.

**Every key in the ring is held to the same strength floor**, which it was not until review. Both
composition roots require `Audit:ChainKey` to be at least 32 characters; a retired key was checked
only for being non-blank, so a three-character one would have been accepted — and would then have
been the only thing standing behind every row in its epoch, the stretch of the trail nobody can
rewrite to repair. A key weak enough to guess makes its epoch forgeable by anyone holding the
database, which is precisely the attacker D2 is written against. The floor lives in the ring
construction rather than in each root's options validation, for the reason the ring itself lives
there: a structural rule enforced in one root is a rule the other does not have.

**What this does not do.**

- **A `v2` row cannot be rotated at all.** It records no key identity, so there is nothing to select
  on, and trying keys is the one thing the ring must not do. Those rows verify under the founding key
  and no other. This is not a gap in the ring; it is why the key-identity column was required *before*
  the first anchor rather than alongside rotation.
- **An anchor records ONE key id for a walk that may have used several, and this change does not
  fix it.** `AuditAnchor.VerifiedUnderChainKeyId` is written from the current key unconditionally, so
  after a rotation it names the key the RUN held rather than the keys the walk actually applied — and
  `TailRowHash` is an HMAC under whichever key the tail row named. So the field answers which key
  the RUN held and nothing more: an anchor taken after a rotation but before the first write under
  the new key records the current id beside a tail a RETIRED key authenticated. *(This bullet first
  said the field "still answers the question it was added for, because the tail is written under the
  current key" — true whenever anything has been written since the rotation, false in exactly the
  window a rotation opens.)* Left as it is deliberately: the anchor half of rotation is deferred
  below, and changing the anchor schema for a field nothing reads yet would be schema churn ahead of
  the decision that should shape it. Stated here because an omission decided is different from one
  forgotten, and only one of the two is safe to find later.
- **Nothing forces the anchor a rotation should trigger.** The same decision ratified that "a rotation
  must force an immediate on-demand anchor, with the key-epoch boundary carried in the anchor". This
  change does not implement that, because nothing here runs unattended to notice a rotation happened —
  the same premise that defers anchoring itself. It is an operator step, and an operator step is
  weaker than a mechanism; saying so is the point.
- **The ring is a read-side convenience, and it adds one control that did not exist before it.** It
  makes an honest rotation survivable, and it puts a FLOOR under the current key. At write time the
  current key's holder is still unconstrained — `row.Sequence = ++sequence` and the hash is taken
  under `Audit:ChainKey` whatever the row says. At VERIFICATION they are not: `Audit:ChainKey` is
  itself a ring member, its epoch starts at one past the last retirement, and a row naming it below
  that is refused before its hash is recomputed. With one retirement recorded at 100, a row naming
  the current key verifies at 101 and above and at no sequence in [1, 100]. On `main` all 100 of
  them verified. *(This bullet said "it constrains nobody", then "it constrains the CURRENT key's
  holder not at all" with a parenthetical naming only the refusal ABOVE a retired key's epoch. That
  parenthetical was written when an epoch had one end. The lower end, added later on this branch,
  constrains the current key too, and no bullet here said so.)*
- **What the boundary constrains is a power the ring itself introduced, not one the world had.**
  Before the ring, a rotation left every old row refused under any key the deployment held —
  `WithoutRetiringTheOldKey_RotationStrandsTheHistory…` pins that — so an old key's holder could
  produce nothing that verified. For the RETIRED keys the boundary is exactly that: it stops the
  widening running past the retirement point, and below the boundary the ring still hands an old key
  more than it had, never less. A self-limit on a new capability.

  ⚠️ **BUT "this change only WIDENS what verification accepts" IS FALSE, AND IT WAS THE SECURITY
  ARGUMENT OF THIS SECTION.** Measured against `main`: at `fc1ce40` the only gate on a `v3` row was
  `row.KeyId != _keyId` — identity, with sequence nowhere in it — so a row honestly naming the
  current key verified at ANY sequence. At HEAD the current key carries a `FirstSequence` of one past
  the last retirement, and a row below it is refused before the hash. Concretely: `Audit:ChainKey`
  = K_new with K_old retired at 100, and a `v3` row at sequence 50 naming K_new with a hash correct
  under K_new. On `main` that row verified; here it does not. That is a NARROWING, on the current
  key, over the whole range [1, 100] — and it is a new control, not a self-limit. It is the right
  control: below the last retirement the current key had not started writing, so naming it there is
  as wrong as naming a retired key above its own boundary. What was wrong was calling it nothing.
  *(With nothing retired the ring holds one key whose epoch starts at 1, and HEAD accepts everything
  `main` accepted. The narrowing exists only once a rotation has been recorded.)*
- **And it is defeasible by whoever holds the configuration.** The refusal happens at VERIFICATION,
  never at write time, and the verdict says so itself — *"Raising LastSequence turns this verdict
  green either way"*. A read-side setting an operator can turn green is not the same kind of object
  as a hash; the anchor is still the thing that would change the picture.

## What is wired, and what is not

**Thirteen events write a row today.** Seven are administrative: `AccountDeleted`,
`AccountNumberRevealed`, `AzureTagRenamed`, `PinEnrolled`, `RefreshTokenUnknown`,
`RefreshTokenReuse` and `RefreshTokenReuseRevokeFailed`. For those the log line is kept alongside the
row — two destinations, two jobs.

**Four are money movements, added by B1** (2026-08-20): `MoneyDeposited`, `MoneyWithdrawn`,
`MoneyTransferred` and `MoneyTransferredInternally`. Until then this table recorded a renamed handle
and not one movement of money, which is the single thing a bank is audited for.

**Two are money REFUSALS, added 2026-08-29**: `MoneyWithdrawalRefused` and `MoneyTransferRefused`.
Four sites raise them — a locked PIN and a wrong PIN on the withdrawal path, and an absent step-up
authorisation at both transfer kinds. Until then a bank recorded the withdrawal that succeeded and
not the one refused because somebody was guessing a PIN, and `PinService` throws its lockout at two
places while auditing at neither. **This is the change the paragraph further down anticipated**, and
it wires only what that paragraph named as a security signal.

⚠️ **The `Detail` rule INVERTS on these two, and D5 is why it inverts rather than lapsing.** The four
successes below carry a null `Detail` because the facts live on the ledger row `SubjectId` reaches. A
refusal commits no ledger row, so a pointer-shaped row would point at nothing and "a withdrawal was
refused" without a reason is indistinguishable from noise. So these carry a `Detail` — **the
`ErrorCodes` constant and nothing else**, the same string the caller already received over HTTP. Not
the amount, not the balance, not the counterparty. The reason is the part that is otherwise
unrecoverable; the amount is the part D5 forbids, and D5 does not stop applying because the movement
failed.

**Their subject is the ACCOUNT, not a transaction that does not exist** — and on a transfer it is the
account the money would have LEFT, which is the same rule the successes follow. The account is
already in the system, so naming it adds no new personal data and keeps the row retrievable.

Three things about the four movements are decisions rather than details.

**They emit NO `SecurityEvent` log line, and that is the first event class to do so.** The
administrative seven are worth waking an operator for; a deposit is not, and the money paths already
carry their own operational log lines. So a movement gets durable evidence without flooding the
alert stream. It also means the two inventories — logged sites, and rows written — moved
independently for the first time, which is why
`SecurityEventConstantTests.TheEventInventoryThisAdrStatesIsStillTheOneInTheSource` now counts
`_audit.Record` and `_audit.RecordRefusalAsync` sites as well as log templates. A guard that only
counted templates would have let this very paragraph go stale in silence, exactly as D4 did.

**`Detail` is null on all four.** The amount, the counterparty, the description and the account are
already on the ledger row that `SubjectId` reaches, and copying them here would break D5 — an amount
tied to an actor id is financial data about an identifiable person, in a table designed never to be
purged. The audit row answers *who did what to which movement*; the ledger row answers *what moved*.

**A transfer writes ONE row, and its subject is the OUTGOING ledger row.** Two ledger rows are the
bookkeeping of a single act, and the outgoing leg is the act itself — money leaving, under the
authorisation the actor minted and which is consumed against that very row.

For an EXTERNAL transfer there is a second, sharper reason, and it is worth stating separately
because it does NOT apply to the internal case: the incoming leg lands on the payee's account, whose
`Account.UserId` is provably not the actor. The payee is resolved by handle with no ownership check,
and the self-transfer guard plus the unique `AzureTag` index make the two ids differ by construction.
Subjecting the row to that leg would name someone who authorised nothing.

An INTERNAL transfer moves between two accounts the actor already owns — `InternalTransferAsync`
ownership-checks BOTH against the same user — so there the incoming leg's owner *is* the actor and
nothing would be misattributed. The subject is still the outgoing leg, for the first reason alone.
Saying so is not pedantry: an earlier draft of this paragraph gave the external reason for both, and
a reader checking it against `InternalTransferAsync` would have found the ADR asserting something
the code contradicts.

**And one thing the retry loop forced.** A money row must be written INSIDE the concurrency-retry
loop, because the transaction id it takes as its subject is minted inside that loop. That left a
failed attempt's row tracked as Added while the next attempt added another —
`ConcurrencyRetry.ResetToStoreAsync` detached only `Transaction`, and its own comment records that
the `IdempotencyRecord` is deliberately left attached, so nothing had ever been asked this question.
Measured with an injected collision: one deposit, **two** rows. It now detaches `AuditEvent` too,
because a row written about an attempt must die with the attempt — the opposite of the idempotency
flip, which is about the request and survives every attempt. An audit trail that overcounts
movements is worse than one that misses them: it manufactures evidence of transfers that never
happened. Pinned by `ADepositThatRetries_WritesExactlyOneAuditRow`.

⚠️ **SUPERSEDED IN PART on 2026-08-29, and the two halves went opposite ways.** The paragraph below
is kept whole, because its reasoning is what decided the split and not only its conclusion.

WHAT CHANGED: the security signals it names — a transfer presented without a step-up, a wrong PIN —
ARE now wired, which is the "own change with its own tests" its last sentence asked for, plus the PIN
LOCKOUT it did not anticipate because that branch throws instead of returning.

WHAT DID NOT CHANGE: insufficient funds, self-transfer and same-account stay out, for exactly the
reason given below. The first draft of that change wired insufficient funds as well, and this
paragraph is what caught it — a decision recorded with its reasoning was still doing its job eight
days later, against the person who wrote it.

**Money REFUSALS are not wired here**, and the reason is measured rather than assumed. There are 19
throw sites across `TransactionService` and `TransferService`, and most are business validation —
insufficient funds, a self-transfer, a same-account transfer. Those are routine user outcomes, and a
row per attempt is the same unbounded write into a never-purged table that keeps registration
refusals out. The ones that ARE security signals — a transfer presented without a step-up
authorisation, a wrong PIN at the mint — belong with the step-up path, where ADR-0010's lockout
already lives, and that is its own change with its own tests.

The remaining ten logged events are deliberately log-only, with reasons that were measured rather
than assumed:

- **Registration refusals** (`DuplicateRegistration`, `RegistrationRejected`) — `/api/auth/register`
  is unauthenticated, and the API carries **no rate limiter of its own** (checked: zero
  `RequireRateLimiting` in `AzureBank.Api`; the limit lives in the BFF). An audit row per attempt is
  therefore an unauthenticated, unbounded write into the one table that is never pruned. ~~Revisit
  together with #231, which puts these endpoints behind the BFF.~~ *(that revisit has happened —
  see below; the instruction is struck rather than deleted so the original entry stays whole)*

  **Revisited 2026-08-19, as this ADR said to.** #231 has landed: the proxied `/api/auth/register` is
  now answered 404, so the only route to the API's registration is the BFF's own
  `/bff/auth/register`, which carries the `auth` rate-limiter policy. The unbounded half of the
  argument is therefore weaker than when it was written. It is NOT gone — a rate limit is per IP, and
  the events stay log-only for now — but the reason has changed and is recorded rather than left
  standing on a premise that no longer holds. Wiring them is a change with its own tests; it belongs
  to B1/B3, not to a footnote here.
- **Retry collisions** (`TransactionNumberCollision` ×2, `AccountNumberCollision`) — these are health
  signals about a random-id generator, not acts by a principal, and they are raised inside a `catch`
  that `continue`s a retry loop. An enlisted row would die with the attempt that failed; a
  self-committing one would write one row per attempt. Both wrong, and the log is right.

**The first half of the next sentence stopped being true on 2026-08-23** and is left standing
because an ADR is a record: `tools/AzureBank.AuditVerifier` reads this table, and `VerifyAsync` is
now called from production code rather than only from tests. The second half still holds — nothing
verifies the chain on a schedule.

Two further gaps, named rather than left implicit: **nothing reads this table yet** — there is no
endpoint, no access control and no operator view, which is B3's work — and **`VerifyAsync` is called
only by tests**, so nothing verifies the chain on a schedule.

## Consequences

- Audited operations now depend on the audit table being writable. That is D1 working, not a defect.
- Saves that carry an audit row open an explicit transaction. Saves that do not are untouched.
- Two secrets exist where one did: `Audit:ChainKey` joins `StepUp:BindingKey`, `Idempotency:HashKey`
  and `Security:PinPepper`. All four are `ValidateOnStart`, and none is ever stored in the database.

## Six defects this work found, none of them visible to a green suite

Recorded here because each was live in a version whose whole suite was green, and each needed a
different oracle: two needed real SQL Server, one needed the running API, two came from a review that
read the code, and one from an adversarial sweep of the write path. The pattern is the point — "779
tests pass" said nothing about any of them.

**The hash did not survive a round trip.** `OccurredAt` was hashed with `ToString("O")`, which emits
a trailing `Z` when `DateTime.Kind` is `Utc` and omits it when the kind is `Unspecified`.
`datetime2` stores no kind, so a row written from `DateTime.UtcNow` hashed one way and, read back
from the database, hashed another: every audit row would have failed verification in production,
always. Confirmed by recomputing a stored row's HMAC outside .NET —

```
payload with "2026-08-19T10:39:24.7403300Z" -> 6a7028…143903  == the stored RowHash
payload with "2026-08-19T10:39:24.7403300"  -> a98d42…c3dbd9  != the stored RowHash
```

`Ticks` is hashed instead: a plain integer, exact through `datetime2(7)`, with nothing kind-dependent
to lose.

**The lock had no transaction to be held by.** `UPDLOCK, HOLDLOCK` on the tail read is meaningless
outside a transaction, and EF opens its implicit one *inside* `SaveChanges` — after the chain had
already run and released it. Twenty-four concurrent writers: `Cannot insert duplicate key row in
object 'dbo.AuditEvents' with unique index 'IX_AuditEvents_Sequence'. The duplicate key value is
(2)`. The unique index on `Sequence` is why this was loud instead of a silent fork, which is an
argument for keeping it. After the fix: 24 rows, sequences 1..24, no fork, no deadlock.

**And the transaction was refused by production's own configuration.** `EnableRetryOnFailure` is on
in `ServiceCollectionExtensions`, and EF refuses a user-initiated transaction under a retrying
strategy. So every audited request on the real API answered **500** —

```
POST https://localhost:7215/api/auth/refresh  ->  500
System.InvalidOperationException: The configured execution strategy
'SqlServerRetryingExecutionStrategy' does not support user-initiated transactions.
```

— while all 766 tests passed, because `CustomWebApplicationFactory` leaves the retrying strategy
**off** unless a test opts into it. The default SQL test path is therefore not the production path.
The fix is the idiom `AuthService.RegisterAsync` already uses,
`Database.CreateExecutionStrategy()`; `AnAuditedSave_WorksUnderTheRetryingStrategyProductionActuallyUses`
now opts in, so one test finally runs the production configuration.

Verified afterwards on the running API rather than only in tests: the same call answers **401**, and
four rows sit in `AzureBankDev` — three `RefreshTokenUnknown` refusals written on their own
connections, then an `AzureTagRenamed` success that rode a business transaction, each linked to the
hash before it, `Detail` empty on all four (D5).

### The three the review round added

**`PinEnrolled` was never written at all.** `Record` only calls `Add`; the caller's save persists it.
The call sat AFTER `UserManager.UpdateAsync`, which had already saved, and nothing saved again — so
the row was discarded when the scope disposed. Measured on the running API: `POST /api/auth/pin`
answered **200**, the security log line was emitted once, and `AuditEvents` held **zero** rows. The
unit test was green throughout, because it held a `Mock<IAuditService>` and asserted that `Record` was
CALLED. That is the lesson worth keeping: *the writer was invoked* and *the evidence exists* are
different claims, and only the second one is the feature. `AuditTrailPersistenceTests` now reads the
table for every wired success path.

**`AzureTagRenamed` rode a second save.** The rename committed, then the row was added and saved
separately, so the two could part company — D1 broken on the path this ADR is the reason for. Every
row-exists assertion still passed, because the row WAS written; only an injected failure separates
the shapes, which is what `WhenTheAuditRowCannotBeWritten_TheHandleRenameIsRolledBackToo` does.

Fixing it moved a second thing: with the audit row now sharing that save, `IX_AuditEvents_Sequence`
can raise the same 2601/2627 a duplicate handle does, so `UserService`'s number-only match would have
reported a hash-chain collision to the caller as "that handle is already taken". The narrowing moved
to `ConcurrencyRetry.IsAzureTagCollision`, matching by INDEX NAME like its three siblings.

### And one the sweep added, which no bot and no test had seen

**The reuse path was made fail-open by its own audit row** — see D1's exception above. Found by
fanning out over the audit write path with instructions to classify every call site and then refute
each finding, rather than by re-reading the diff.

## References

- PCI DSS v4.0 §10.2.2, §10.3.2, §10.5.1
- NIST SP 800-53 Rev. 5 AU-3, AU-9, AU-11
- PSD2 (EU 2015/2366) Art. 21, Art. 72; AMLD (EU 2015/849) Art. 40
- `backend/src/AzureBank.Shared/Constants/SecurityEvents.cs` — the event names this table stores
- [ADR-0009](0009-idempotency-monetary-operations.md) — the enlisting-writer contract D1 copies
- [ADR-0008](0008-step-up-authentication.md) — the PIN events among them
- `azurebank-work/plans/audit-trail/` — the measurements behind every number above
