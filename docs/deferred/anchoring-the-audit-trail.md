# Anchoring the audit trail, and why this deployment cannot

The audit trail is append-only and hash-chained: every row's HMAC covers the previous row's hash, so
altering a row or removing one from the middle breaks the walk at a nameable sequence. ADR-0044 sets
out the design and, deliberately, its limit.

**The limit is the end of the table.** Delete the newest rows and the surviving prefix links
perfectly, every hash matches, and verification reports intact. Nothing in the chain records how
many rows there should have been. Truncation needs no key at all — only write access — which makes
it the cheapest attack on this table, and the only one the chain cannot see. Delete every row and
the verifier does say something else — `NOTHING TO VERIFY`, on the stated grounds that an empty
chain links perfectly and a table truncated to nothing reports exactly what a fresh one does — but
that separates zero from non-zero and nothing more. It is a floor, not a defence.

This document is about the control that would close it, and why it is not here.

## What would close it

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

An anchor is only as good as its freshness. It bounds truncation to one anchoring interval — rows
written since the last anchor are exactly as undetectable as they are today — so the control is the
schedule, not the cryptography.

This runs on one machine, started by hand. There is no service up between sessions, so there is
nothing for a periodic anchoring job to live in, and "anchor when I remember to" is not a schedule.
A control that depends on somebody choosing to run it does not constrain that person, which is the
one thing this control is for.

### The second reason: I own both sides

The chain's threat model is somebody who holds the database but not the key. An anchor stored in
that same database is deleted in the same breath as the rows it protects — a suffix removed from
both chains verifies perfectly, because each links backwards only.

So the token has to live somewhere I cannot quietly revise. On a single-machine deployment the
application's database login, the box, and the operator running any export are all me. The property
that actually survives is narrower than it first appears:

> For every anchor a third party has **seen**, the operator loses the ability to tell a different
> story about the past.

Seen, not stored safely. Which means the control does not begin at the timestamp — it begins at
publication, and publication needs a schedule, which brings us back to the first reason.

## What would make it worth building

Deploying to Azure changes both reasons at once, which is why they are recorded together.

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
of each, and neither exists yet.

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
  size of the window in which truncation stays invisible, so it is the guarantee.
- **Whether the anchor record is authenticated.** A digest over a counter, two sequences, a count
  and two hashes is recomputable by anyone holding the table — including a plausible run of "the TSA
  was unreachable" gap markers. Without a MAC under a key the database does not hold, the design
  manufactures its own alibi.
- **Where the trust root comes from.** Pinning the TSA's root and its policy OID out of band is what
  turns a signature into a trust decision, and it has to sit somewhere the database under
  verification cannot reach. It is one-way for the same reason the payload is: once tokens exist
  under one root, dropping that root retires the anchors that depend on it. A verifier shipping
  green against an unpinned root has been proving nothing, and nothing about it looks wrong.
- **What the payload is allowed to be, forever after the first token.** This one is already true
  without any anchor, which is the part worth saying first. `ComputeRowHash` hardcodes the version
  prefix `v2` and reads a single `Audit:ChainKey`, and `VerifyAsync` recomputes **every** row with
  whatever those two are **now**. So a key rotation or a version bump today makes the verifier
  reject rows that were written correctly, with `HashMismatch` — the same verdict a tampered row
  gets. The message hedges usefully on one of the two: it says the row was either altered or checked
  under a different key. It says nothing about a version bump — and the operator-runnable verifier
  then makes that worse rather than leaving it unexplained. A bump breaks at the first row it
  recomputes, so on a table starting at sequence 1 it lands in the branch that prints *"Breaking at
  sequence 1 usually means the wrong `Audit:ChainKey`, not tampering ... Confirm the key before
  escalating."* Sound advice for the case it was written for; here it sends the operator to confirm
  a key that is fine. Historical rows stay verifiable only if the verifier keeps the old versions
  and keys and picks the right one per row, and nothing in a row says which.

  Anchoring does not create that; it makes it permanent. Before the first token a re-hash is a
  migration. After it, the anchored value is fixed in a signed token nobody can re-issue, so the
  bump leaves every earlier anchor unsatisfiable with no way back. **Whatever needs to be in the
  hashed payload has to be there first** — and whatever ships that should carry a test that verifies
  a row written under an OLD scheme, because this is a regression that arrives silently otherwise.

## What is true today

Verification says so out loud rather than leaving it to be inferred. An intact verdict ends with:

```
This proves no row was altered by anyone who does not hold Audit:ChainKey,
and that none was removed from the MIDDLE. It does NOT prove none was removed
from the END -- truncation needs no key and leaves every surviving row linking
correctly. Compare the count against your own.
```

And a test asserts the uncomfortable direction on purpose.
`TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit` deletes the last row, asserts the chain still
reports intact, and exists so that the claim cannot quietly grow back.

I had written that anchoring would turn that test red, and that this is how the documents resting on
it would be corrected at the same time rather than a year later. That is not true, and why it is
false is worth more than quietly deleting it. The test asserts on what `AuditChain.VerifyAsync`
returns. An anchor check is a different layer — it needs a token store and a pinned trust root,
neither of which belongs behind that interface — so it lands in the operator-runnable verifier, and
this test goes on passing beside it. The tripwire I described is a comment, not a mechanism.

The first correction I wrote for that was wrong in the other direction: it said the test would have
to be retired. It does not, and retiring it would throw away the thing worth keeping. Its assertion
stays true after anchoring, because anchoring does not touch the walk — and it stays useful for the
same reason, because green is the correct answer at that layer. The chain alone cannot see a
truncated tail, which is the whole reason an anchor is wanted; this is the test that would object if
somebody later taught `VerifyAsync` to consult anchors from behind that interface.

What changes is its scope, not its existence. The name says truncation "is not detected", which is a
claim about the system, and after anchoring the system does detect it — one layer up, within one
interval. So the test gets rescoped to the chain, a second test at the verifier layer asserts what
the anchor catches, and the documents resting on it — ADR-0044, the runbook, and this page — move
from "this cannot be detected" to "the chain cannot detect it, and here is what does." None of that
announces itself, which is the part worth writing down.

Until then, the count is the only witness, and the operator's own records are what it has to be
compared against.
