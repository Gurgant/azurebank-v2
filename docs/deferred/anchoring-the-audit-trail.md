# Anchoring the audit trail, and why this deployment cannot

The audit trail is append-only and hash-chained: every row's HMAC covers the previous row's hash, so
for anybody who holds none of the keys in the verification ring, altering a row or removing one from
the middle breaks the walk at a nameable sequence. ADR-0044 sets out the design and, deliberately,
its limit.

*(This said "anybody who does not hold `Audit:ChainKey`", which the key ring made false for the
ALTERING half. A row inside a retired key's epoch is checked under THAT key, so its holder — who
does not hold `Audit:ChainKey` — can alter the row and recompute the hash, and only the LINK to the
row above stops them. It stops them exactly until the rows above are also inside epochs they hold,
which is the case when the retired key's boundary is the top of the table: the rotation is recorded
and nothing has been written under the new key yet. The same empty range the triage table in the
runbook calls the window an attacker needs.)*

**The limit is the end of the table.** Delete the newest rows and the surviving prefix links
perfectly, every hash matches, and verification reports intact. Nothing in the chain records how
many rows there should have been. Truncation needs no key at all — only write access — which makes
it the cheapest attack on this table, and the only one the chain misses against the attacker it is
built for: somebody who holds the database but not the key that answers for the rows they want to
change. *(Singular "the key" until 2026-08-30. Since the key ring the verifier holds several, each
answering for its own EPOCH, so the attacker the chain is built for is one who holds none of the
keys covering the stretch they are after — narrower than "the key", and the narrowing is what the
ring cost.)* Somebody holding that key rewrites a row and recomputes the chain, which is equally
invisible — ADR-0044 records that as the other gap. Two
controls close them and they close different layers: SQL Server's ledger at the WRITE, an external
timestamp at the ANCHOR. This document is about the second. Delete every row and the verifier does
say something else — `NOTHING TO VERIFY`, on the stated grounds that an empty chain links perfectly
and a table truncated to nothing reports exactly what a fresh one does — but that separates zero
from non-zero and nothing more. It is a floor, not a defence.

This document is about the control that would close it, and why it is not here.

## What would close it

**The other half first, so this one is not read as the whole answer.** SQL Server's ledger refuses
an UPDATE or a DELETE on a committed row at the engine, and leaves undroppable residue if the table
is dropped. ADR-0044 records why it is deferred too, and what it does and does not buy. It closes a
different layer than anything below: engine enforcement constrains somebody holding a connection. It
does not constrain me, because dropping the whole database is permitted and I own the machine. Only
time issued by somebody else does that, and the rest of this document is about the cost of getting
it.

A digest of the chain's tail, fixed at a point in time by somebody other than me. RFC 3161 timestamp
tokens do exactly that: hand a Time-Stamping Authority a hash, get back a signed statement that this
value existed at this instant. Anchor `(lowest sequence, highest sequence, row count, tail hash)`
periodically, and a later, shorter history stops matching an earlier token.

"Signed by somebody other than me" carries more weight in that sentence than it looks. A signature
is worth exactly what the verifier can establish about the key behind it, and establishing that
turns out to be most of the work rather than a detail of it.

The tail hash transitively commits to every row beneath it, so one anchor covers the whole prefix.
No Merkle tree is needed **for truncation** — a tree buys inclusion proofs, which is a different
problem and belongs with the PSD2 Art. 72 evidence work.

## Why it is not implemented

### The decisive reason: nothing here runs unattended

An anchor is only as good as its freshness, and freshness runs from the last copy somebody else
holds rather than from the moment a token was issued. At best it bounds truncation to one
publication interval — rows written since that copy are exactly as undetectable as they are today —
so the control is the schedule, not the cryptography.

This runs on one machine, started by hand. There is no service up between sessions, so there is
nothing for a periodic anchoring job to live in, and "anchor when I remember to" is not a schedule.
A control that depends on somebody choosing to run it does not constrain that person, which is the
one thing this control is for.

**And the consequence is stronger than "less fresh": with no promised cadence, a missing anchor is
not weak evidence. It is none.** This is the half of the argument that is easy to state backwards,
so it is worth being exact about. If anchors are supposed to appear on a schedule, a gap in the
series is itself a finding — something was deleted, or something stopped. If they appear when
somebody remembers, a gap is indistinguishable from a quiet fortnight, and no amount of linkage
recovers the difference. Chaining tells you an anchor that EXISTS was not tampered with. Only a
promise about when they arrive tells you an anchor that is ABSENT should have been there.

Both real systems examined for this make the cadence the mechanism rather than the linkage, and both
do it by signing something over an interval in which nothing happened.

- **CloudTrail mints an empty digest.** *"CloudTrail will deliver a digest file even when there has
  been no API activity in your account during the one hour period that the digest file represents.
  This can be useful when you need to assert that no log files were delivered during the hour
  reported by the digest file."* The empty digest is a normal digest whose `logFiles` array is `[]`
  and whose `newestEventTime` and `oldestEventTime` are `null`; it is signed and chained like any
  other ([digest file structure](https://docs.aws.amazon.com/awscloudtrail/latest/userguide/cloudtrail-log-file-validation-digest-file-structure.html)).
- **RFC 6962 signs the same tree again with a new timestamp.** *"Each log MUST produce on demand a
  Signed Tree Head that is no older than the Maximum Merge Delay"*, and *"In the unlikely event that
  it receives no new submissions during an MMD period, the log SHALL sign the same Merkle Tree Hash
  with a fresh timestamp"* ([RFC 6962 §3.5](https://www.rfc-editor.org/rfc/rfc6962)). The tree hash
  is unchanged and the signature is new, which is the entire point: the freshness, not the content,
  is what is being attested.

_Both quotes read from the sources on 2026-08-28, not from a summary of them._

**So a cadence is not a nicer version of what is here — it is a different claim.** Anchoring on
demand can say "these rows were covered as of this anchor". Only anchoring on a promise can say
"nothing has been removed since, because you would be looking at a gap." That second sentence is the
one an auditor is actually asking for, and this deployment cannot make it for the reason above:
there is nothing running to make the promise to.

### The second reason: I own both sides

The chain's threat model is somebody who holds the database but not the key whose epoch covers the
rows they want — see the correction at the head of this document; it was written when there was one
key. An anchor stored in that same database is deleted in the same breath as the rows it protects
— a suffix removed from
both chains verifies perfectly, because each links backwards only.

So the token has to live somewhere I cannot quietly revise. On a single-machine deployment the
application's database login, the box, and the operator running any export are all me. The property
that actually survives is narrower than it first appears:

> For every anchor a third party has **seen**, the operator loses the ability to tell a different
> story about the rows that anchor covers.

Seen, not stored safely. Which means the control does not begin at the timestamp — it begins at
publication, and publication needs a schedule, which brings us back to the first reason.

## What would make it worth building

**The trigger is not Azure. It is anything that runs when nobody is here.** A €4/month VPS, AWS, GCP
or a scheduled GitHub Actions workflow all satisfy that trigger, and naming one vendor would make
this read as a decision about a vendor rather than about a missing property. What is missing is a
process alive between sessions that can fetch a timestamp on a cadence and publish it — and whatever
supplies that changes both reasons below at once, which is why they are recorded together.

⚠️ **Satisfying the trigger reconsiders this on its merits; it does not supply the control** — the
same distinction ADR-0024 draws about its own re-open trigger, where the arrival of a contended
resource reopens the decision rather than settling it. None of those platforms gives the anchoring
job, the pinned trust root, or a published copy this deployment cannot revise. They give it
somewhere to run, which is the one thing missing today and the reason the rest of this section is
worth pricing rather than assuming.

Azure is worked through here rather than the others because it is the deployment this project would
actually reach for, and because pricing an example is what keeps "it would be cheap" from staying an
assumption.

**Something would be running.** A Container Apps job or a timer-triggered Function on a schedule
gives the anchoring job somewhere to live. It is not free just because the application is already
deployed: a Container Apps job in the Consumption plan meters vCPU-seconds and GiB-seconds for the
duration of each execution, and a Function on the Consumption plan meters executions and GB-seconds
against a monthly free grant. Hourly, a job that runs for a second or two is small enough that the
grant probably swallows it — but "probably" is the honest word until somebody prices the plan that
is actually chosen, and there may be storage and TSA costs beside it.

**The identities would separate — but separation of identity is not separation of authority, and
that distinction is the whole thing.** The application's managed identity, the anchoring job's
identity and whatever holds the published copy stop being one Windows account. That is necessary and
it is not sufficient: anyone holding Owner or User Access Administrator at the subscription or
resource group can re-grant themselves any of it, so three identities under one privileged principal
are one principal wearing three hats.

Making it a constraint rather than a diagram means naming the boundary: the scope each role
assignment is made at, who is allowed to make role assignments at that scope, and — for the
published copy — immutable storage with a **locked** retention policy, because an unlocked one is
changed by the same principal. That is the work, and it is why this is filed under deferred rather
than sketched as a to-do.

**Publication gets a destination.** Append each anchor's payload and digest to a public, append-only
repository, or to immutable storage with a locked retention policy. What it buys is that a copy
exists where I cannot revise it, held by somebody whose clock I do not control.

**"Nothing sensitive is published" is the tempting sentence here, and it is too strong.** The digest
reveals nothing about the content of any row. The sequence bounds and the row count are not a
digest, and they do reveal activity metadata: published hourly, the difference between consecutive
counts is the number of audited events in that hour, which tracks how busy the bank was and when it
was quiet. That is traffic analysis rather than disclosure, and on this system it would be a
non-issue — but it is a decision to take rather than a property to assume, and publishing the digest
alone is the cheap way to take it.

Two payloads are involved, and the difference between them matters enough to name. The **anchored
value** is what a TSA signs: a digest over the chain's state, `(lowest sequence, highest sequence,
row count, tail hash)`. The **anchor record** is what gets stored and published, and it carries the
same four fields plus its own counter and the previous record's hash — so the anchors form a second
chain, over the first. Writing and verification would have to agree on one canonical byte rendering
of each, ~~and neither exists yet~~ *(corrected 2026-08-27: both exist —
`AuditAnchorChain.RenderPayload` and `ComputeAnchoredValue`. The export verb deliberately adds no
third one: it copies stored columns and derives nothing, because a third rendering would be a third
place for writing and verification to disagree about bytes.)*

**And the token needs a home, which is what these paragraphs kept skipping.** Publishing the record
without the token publishes a claim rather than a timestamp: the CMS `TimeStampToken` is the only
object carrying the TSA's signature, its `genTime`, and the imprint binding both to my bytes. Keep
it in a table beside the rows it protects and it goes in the same breath as them — the argument that
already ruled out storing the anchor there. So the exported copy is the anchor and the local table
is a cache of it, which is the reverse of how it is tempting to build.

**Verifying that token is also more than checking a signature, and the platform makes the wrong
thing the easy thing.** `Rfc3161TimestampToken.VerifySignatureForHash` resolves the signer
certificate, matches it against the token's `ESSCertID`, requires the critical timestamping EKU
`1.3.6.1.5.5.7.3.8`, checks `genTime` against that certificate's validity window, and verifies the
CMS signature with `verifySignatureOnly: true` — the flag that skips building a chain. No chain
means no trust anchor and no revocation lookup. Every one of those checks passes for a certificate
the attacker minted himself: he chooses the certificate, embeds its own `ESSCertID` in the token he
signs, and picks a validity window around a `genTime` he also picks. A verifier that stops where the
API stops prints a green line over a token he wrote. The API is not being misused — it answers the
only question it can answer, and that question is not "did somebody else attest this". The trust
decision belongs to the caller, and it is made by chaining that certificate to a root pinned out of
band: beside the secrets, never in the database being verified.

That paragraph is read from the API contract and the runtime source rather than demonstrated.
Nothing here issues or verifies a token, so it constrains code that does not exist, and I would want
it re-measured against a real token before anybody leaned on it.

Four things have to be settled before the first token, and three of them are one-way doors:

- **The interval**, derived from how fast money actually moves rather than from the clock. It is the
  floor on how wide the window of invisible truncation gets, not its size: the window runs back to
  the last anchor a third party has seen, so a run that leaves a gap marker instead of a token, or a
  copy published late, widens it past one interval. The schedule is the guarantee.
- **Whether the anchor record is authenticated. ✅ Settled, and built.** A digest over a counter,
  two sequences, a count and two hashes is recomputable by anyone holding the table — including a
  plausible run of gap markers. So the record carries an HMAC under `Audit:AnchorKey`, a sixth
  secret the row chain does not use, covering the marker kind as well: a database-only attacker can
  delete an interior record (loud) but not mint them (quiet), and cannot flip a real record into
  a marker to
  collapse the operator's provable bound. ⚠️ It does not constrain the operator, who holds the key.
  It is one-way because the alibi is the thing a MAC has to rule out: records already published
  without one can be re-derived by the operator at any later date, so MACing them afterwards dates
  nothing.
- **Where the trust root comes from.** Pinning the TSA's root and its policy OID out of band is what
  turns a signature into a trust decision, and it has to sit somewhere the database under
  verification cannot reach. It is one-way for the same reason the payload is: once tokens exist
  under one root, dropping that root retires the anchors that depend on it. A verifier shipping
  green against an unpinned root has been proving nothing, and nothing about it looks wrong.
- **What the payload is allowed to be, forever after the first token. ✅ This one has since been
  closed, and it was closed BEFORE the anchor rather than after — which is the whole point of
  listing it here.** As written, `ComputeRowHash` joined the literal `v2` and read a single
  `Audit:ChainKey`, and `VerifyAsync` recomputed **every** row with whatever those two were at the
  time it ran. So a key rotation or a version bump rejected rows that had been written correctly,
  with `HashMismatch` — the same verdict a tampered row gets — and the operator-runnable verifier
  made it worse rather than leaving it unexplained: a bump breaks at the first row recomputed, so on
  a table starting at sequence 1 it landed in the branch printing *"Breaking at sequence 1 usually
  means the wrong `Audit:ChainKey`, not tampering ... Confirm the key before escalating."* Sound
  advice for the case it was written for, and in that one it sent the operator to confirm a key that
  was fine.

  Every row now records the payload rendering that wrote it, and every row written from here on also
  records a non-secret identity of the key that signed it, both inside the hashed payload. Rows
  written before that migration record no identity — there was none to record, and inventing one
  would have put an unfalsifiable claim outside their hashed payload — so they verify under the
  founding key. The walk recomputes each row under the scheme it declares. A row this verifier
  cannot render, or that names a key it does not hold, is reported as UNCHECKED rather than as
  wrong, and that is a break rather than a note. The sentence that used to close this bullet — *"and
  nothing in a row says which"* — is false now, which is the good kind of stale.

  Anchoring does not create that; it makes it permanent. Before the first token a re-hash is a
  migration. After it, the anchored value is fixed in a signed token nobody can re-issue, so the
  bump leaves every earlier anchor unsatisfiable with no way back. **Whatever needs to be in the
  hashed payload has to be there first** — and whatever ships that should carry a test that verifies
  a row written under an OLD scheme, because this is a regression that arrives silently otherwise.

## What is true today

Verification says so out loud rather than leaving it to be inferred. An intact verdict ends with:

```
This proves no row was altered by anyone who does not hold the key whose
EPOCH that row falls in -- Audit:ChainKey for everything since the last
retirement, and each retired key for its own stretch and no other -- and
that none was removed from the MIDDLE. Retiring a key narrows what a
verification ACCEPTS from it, never what it can write: inside its own epoch
it still rewrites and recomputes as freely as it ever did.
It does NOT prove none was removed from the END -- truncation needs no key and
leaves every surviving row linking correctly.
Compare the count against your own.
```

And a test asserts the uncomfortable direction on purpose.
`TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit` deletes the last row, asserts the chain still
reports intact, and exists so that the claim cannot quietly grow back.

**A copy can now leave the machine, which is the half of this that was buildable here.**
`AzureBank.AuditVerifier export <path>` writes the anchor chain to a file outside the database, one
JSON record per line, and refuses to overwrite an existing one — because overwriting the earlier copy
with the current state is precisely the move the copy exists to make visible. One record per line
means `diff` between two exports is the comparison and needs no code, and a copy under version
control shows an appended anchor as an addition and a revised history as a deletion beside it.
`docs/audit/anchors.sample.jsonl` is one, committed so a reader can see the shape, with
`docs/audit/README.md` beside it.

**And the gap is now a number rather than a shrug.** Every `verify` verdict is followed by an
UNCOVERED WINDOW block: how far `AuditEvents` runs past the deepest sequence any anchor claims to
cover. It is computed from local data alone and it does not pretend anything runs — it turns "I do
not know how much sits outside every anchor" into a count.

**It prints its own two limits rather than leaving them to be found.** It HEALS: sequences are
reissued after a truncation, so writing enough new rows brings the tail back past the claim and the
window reads zero again. And it is blind to the thorough version — delete the covering anchors along
with the rows and the claim drops with the tail. A NEGATIVE window is reported separately and is the
one to stop for, because it means the anchors claim coverage through a sequence that no longer
exists. The runbook names the readings an operator is most likely to get wrong.

⚠️ **This does not make the cadence argument above any weaker.** The window measures distance, not
time, and the anchors it measures against sit in the database they anchor. It catches the truncation
carried out by somebody who did not know `AuditAnchors` was there. Removing the covering anchors
along with the rows is consistent and silent, which
`AuditAnchorSqlServerTests.ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit`
asserts on purpose. The window raises the cost of the attack; the schedule and the third party are
what would close it, and both are still missing.

⚠️ **It changes nothing above.** Writing a file is not the same as being seen, and this document's
own scoping is the reason: the property is bounded to anchors a third party has SEEN, and a file this
machine wrote to this machine's disk has been seen by nobody, and whoever can truncate the table can
delete the file too. ⚠️ *(Corrected 2026-08-29: this said the truncation deletes the file "in the
same breath", which reads as one move. They are two operations — one against the database, one
against the filesystem — and `docs/audit/README.md` already had it right, in the same week: "whoever
CAN truncate the table CAN delete the export". Three pages, two wordings, and the correct one was
already in the repository.)*

It is also a verb somebody types, which the decisive reason above already
disqualifies from being a control. So the export is the artefact and the demonstration; the schedule,
the third party and the timestamp are all still missing, and the four things that have to be settled
before the first token are still four.

It is tempting to call that a tripwire — to say anchoring will turn it red, and that this is how the
documents resting on it get corrected at the same time rather than a year later. It will not. The
test asserts on what `AuditChain.VerifyAsync` returns, and an anchor check is a different layer: it
needs a token store and a pinned trust root, neither of which belongs behind that interface, so it
lands in the operator-runnable verifier and this test goes on passing beside it. A tripwire made of
a comment is not a tripwire.

The opposite conclusion is just as wrong and easier to reach from there — that the test will have to
be retired. It will not, and retiring it would throw away the thing worth keeping. Its assertion
stays true after anchoring, because anchoring does not touch the walk, and it stays useful for the
same reason, because green is the correct answer at that layer. The chain alone cannot see a
truncated tail, which is the whole reason an anchor is wanted; this is the test that would object if
somebody later taught `VerifyAsync` to consult anchors from behind that interface.

What changes is its scope, not its existence. The name says truncation "is not detected", which is a
claim about the system, and after anchoring the system does detect it — one layer up, for the rows a
trusted anchor covers, and not for rows written since the last one. So the test gets rescoped to the
chain, a second test at the verifier layer asserts what the anchor catches, and the documents
resting on it — ADR-0044, the runbook, and this page — move from "this cannot be detected" to "the
chain cannot detect it, and here is what does." None of that announces itself, which is the part
worth writing down.

~~Until then, the count is the only witness, and the operator's own records are what it has to be
compared against.~~

*(Corrected 2026-08-28. `docs/runbooks/audit-chain-unavailable.md` was corrected in the same week to
say there are now three witnesses and only the last is a person, so this closing left the two pages
disagreeing.)* **Until then, the operator's own records are the only witness OUTSIDE this machine**,
and that distinction is the whole of it. `AuditAnchors` and the uncovered window are witnesses, and
they are witnesses the same person can revise — which is why this document keeps arguing for a copy
somebody else holds. What they buy is that the careless version of the attack now leaves arithmetic
that does not add up. What they cannot buy is testimony from anybody but you.
