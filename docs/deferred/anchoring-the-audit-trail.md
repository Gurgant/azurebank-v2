# Anchoring the audit trail, and why this deployment cannot

The audit trail is append-only and hash-chained: every row's HMAC covers the previous row's hash, so
altering a row or removing one from the middle breaks the walk at a nameable sequence. ADR-0044 sets
out the design and, deliberately, its limit.

**The limit is the end of the table.** Delete the newest rows and the surviving prefix links
perfectly, every hash matches, and verification reports intact. Nothing in the chain records how many
rows there should have been. Truncation needs no key at all — only write access — which makes it the
cheapest attack on this table, and the only one the chain cannot see.

This document is about the control that would close it, and why it is not here.

## What would close it

A digest of the chain's tail, fixed at a point in time by somebody other than me. RFC 3161 timestamp
tokens do exactly that: hand a Time-Stamping Authority a hash, get back a signed statement that this
value existed at this instant. Anchor `(lowest sequence, highest sequence, row count, tail hash)`
periodically, and a later, shorter history stops matching an earlier token.

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

The chain's threat model is somebody who holds the database but not the key. An anchor stored in that
same database is deleted in the same breath as the rows it protects — a suffix removed from both
chains verifies perfectly, because each links backwards only.

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

Three things would have to be decided at that point, and two of them are one-way doors:

- **The interval**, derived from how fast money actually moves rather than from the clock. It is the
  size of the window in which truncation stays invisible, so it is the guarantee.
- **Whether the anchor record is authenticated.** A digest over a counter, two sequences, a count and
  two hashes is recomputable by anyone holding the table — including a plausible run of "the TSA was
  unreachable" gap markers. Without a MAC under a key the database does not hold, the design
  manufactures its own alibi.
- **What the payload is allowed to be, forever after the first token.** This one is already true
  without any anchor, which is the part worth saying first. `ComputeRowHash` hardcodes the version
  prefix `v2` and reads a single `Audit:ChainKey`, and `VerifyAsync` recomputes **every** row with
  whatever those two are **now**. So a key rotation or a version bump today makes the verifier
  reject rows that were written correctly, with `HashMismatch` — the same verdict a tampered row
  gets. The message hedges usefully on one of the two: it says the row was either altered or checked
  under a different key. It says nothing about a version bump, so that case reads as tampering with
  no hint otherwise. Historical rows stay verifiable only if the verifier keeps the old versions and
  keys and picks the right one per row, and nothing in a row says which.

  Anchoring does not create that; it makes it permanent. Before the first token a re-hash is a
  migration. After it, the anchored value is fixed in a signed token nobody can re-issue, so the
  bump leaves every earlier anchor unsatisfiable with no way back. **Whatever needs to be in the
  hashed payload has to be there first** — and whatever ships that should carry a test that verifies
  a row written under an OLD scheme, because this is a regression that arrives silently otherwise.

## What is true today

Verification says so out loud rather than leaving it to be inferred. An intact verdict prints:

```
This proves no row was altered by anyone who does not hold Audit:ChainKey,
and that none was removed from the MIDDLE. It does NOT prove none was removed
from the END -- truncation needs no key and leaves every surviving row linking
correctly. Compare the count against your own.
```

And a test asserts the uncomfortable direction on purpose. `TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit`
deletes the last row, asserts the chain still reports intact, and exists so that the claim cannot
quietly grow back. If this is ever anchored, that test goes red — which is how the documentation
resting on it gets corrected at the same time, rather than a year later.

Until then, the count is the only witness, and the operator's own records are what it has to be
compared against.
