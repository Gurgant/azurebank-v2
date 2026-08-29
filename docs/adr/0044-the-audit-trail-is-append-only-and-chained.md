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
  unsolved problem for this design, not a solved one**, and B3 inherits it.
- **PSD2 Art. 21's five years is confirmed**, and narrower than a general audit rule: it binds Member
  States to require *payment institutions* to keep records *"for the purpose of this Title"*.
- **AMLD Art. 40 is superseded by Regulation (EU) 2024/1624 (AMLR) Art. 77 from 10 July 2027.** The
  period stays five years, and the clock explicitly also starts *"on the date of refusal to enter
  into a business relationship"* — which is directly about the refusal rows this design commits
  separately. A system being designed in 2026 should target Art. 77.
- **AMLR Art. 77(2) blesses the pointer design of D5 outright**: a reference may be retained instead
  of a copy *"provided that … the obliged entities can provide immediately to competent authorities
  the information and that the information cannot be modified or altered."* That is exactly the
  audit row pointing at an immutable ledger row — with a condition attached, which is that the
  pointed-at row must stay immediately producible and unalterable. `EnforceTransactionImmutability`
  is what makes that true today.
- **NIST SP 800-53 AU-11 still names no number** — *"[Assignment: organization-defined time period]"*
  — so the original parenthetical was correct and stands.
- **And the EDPS says the unbounded chain is itself the exposure**: where security monitoring
  produces logs, *"the purpose and the retention period need to be well defined"*. "Designed never to
  be purged" is not a retention policy; it is the absence of one.

What survives unchanged: this table holds pseudonymous ids and no personal data in `Detail`, which is
why the problem above is a records-management one rather than a live GDPR breach. What does not
survive is the claim that retention was settled.

## What is wired, and what is not

**Thirteen events write a row today.** Seven are administrative: `AccountDeleted`,
`AccountNumberRevealed`, `AzureTagRenamed`, `PinEnrolled`, `RefreshTokenUnknown`,
`RefreshTokenReuse` and `RefreshTokenReuseRevokeFailed`. For those the log line is kept alongside the
row — two destinations, two jobs.

**Four are money movements, added by B1** (2026-08-20): `MoneyDeposited`, `MoneyWithdrawn`,
`MoneyTransferred` and `MoneyTransferredInternally`. Until then this table recorded a renamed handle
and not one movement of money, which is the single thing a bank is audited for.

**Two are money REFUSALS, added 2026-08-29**: `MoneyWithdrawalRefused` and `MoneyTransferRefused`.
Five sites raise them — a locked PIN, a wrong PIN and insufficient funds on the withdrawal path, and
an absent step-up authorisation at both transfer kinds. Until then a bank recorded the withdrawal
that succeeded and not the one refused because somebody was guessing a PIN, and `PinService` throws
its lockout at two places while auditing at neither.

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
