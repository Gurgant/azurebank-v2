# Anchoring the audit trail, and why it is not built

The audit trail is append-only and hash-chained: each row's HMAC covers the previous row's hash
(ADR-0044). For anybody who holds none of the keys in the verification ring, altering a row, or
removing one from the middle, breaks the walk at a sequence the verifier names.

**The limit is the end of the table.** Delete the newest rows and the rows that are left link
perfectly, every hash matches, and verification reports the chain intact. Nothing in the chain
records how many rows there should be. Truncation needs no key, only write access: it is the
cheapest attack on this table, and the one the chain misses against the attacker it is built for,
somebody who holds the database and none of the keys that cover the rows they want to change. Delete
every row and the verifier answers `NOTHING TO VERIFY`, which tells an empty table from one with
rows and nothing more.

The holder of an epoch's key (ADR-0044 D7) can also rewrite a row in that epoch and recompute the
hashes, unseen only while every row above the rewrite falls in an epoch they hold: the link between
two rows compares stored hashes with no key, so the first row above their reach stops linking.

Two controls would close these, at different layers: SQL Server's ledger at the write, and a
timestamp from a third party at the anchor. Neither replaces the other and both are deferred, not
rejected (ADR-0044 D2). This page is about the second.

## What would close it

**The ledger is not the whole answer.** SQL Server's ledger refuses an `UPDATE` or a `DELETE` of a
committed row in the engine, which constrains somebody who holds a connection. It does not constrain
whoever administers the server, who may drop the whole database. Only time issued by somebody else
does.

**The anchor** is a digest of the chain's tail, fixed at a point in time by a third party. An RFC
3161 timestamp token does that: a Time-Stamping Authority (TSA) is handed a hash and returns a
signed statement that the value existed at that instant. Anchor the chain's state periodically, and
a later, shorter history stops matching an earlier token. The tail hash commits to every row beneath
it, so one anchor covers the whole prefix and no Merkle tree is needed for truncation. A tree buys
inclusion proofs, which is a different problem.

## Why it is not implemented

### The decisive reason: no schedule runs it

An anchor is as fresh as the last copy that somebody else holds, not as the moment a token was
issued. At best it bounds truncation to one publication interval: rows written since that copy are
as undetectable as they are without it. The control is the schedule, not the cryptography.

`anchor` and `export` are verbs of the operator tool, and a person runs them. Nothing runs them on a
schedule: the only scheduled job of the Azure deployment is the demo pool's, and the deployment's
policy refuses any other (ADR-0064, decision 4). A control that depends on somebody choosing to run
it does not constrain that person, which is the one thing this control is for.

**With no promised cadence, a missing anchor is not weak evidence: it is none.** If anchors are due
on a schedule, a gap in the series is a finding: something was deleted, or something stopped. If
they appear when somebody remembers, a gap looks the same as a quiet fortnight. Chaining shows that
an anchor that exists was not tampered with. Only a promise about when anchors arrive shows that an
absent one should have been there.

Two published systems make the cadence the mechanism, and both sign something over an interval in
which nothing happened. AWS CloudTrail delivers a signed, chained digest file for every hour, also
for an hour with no activity
([digest file structure](https://docs.aws.amazon.com/awscloudtrail/latest/userguide/cloudtrail-log-file-validation-digest-file-structure.html)).
A log under RFC 6962 that received no submission during its Maximum Merge Delay signs the same tree
hash again with a fresh timestamp ([§3.5](https://www.rfc-editor.org/rfc/rfc6962)).

### The second reason: one principal owns both sides

An anchor stored in the database it anchors is deleted together with the rows it protects: a suffix
removed from both chains verifies perfectly, because each links backwards only. So the token has to
live where the deployment's own administrator cannot quietly revise it. On a development machine the
database login, the machine and whoever runs an export are one account. On Azure the app, the
migration and the deployment are three managed identities (ADR-0061), under one account that holds
the Owner role on the subscription and is the database's Microsoft Entra administrator. The property
that survives is narrow:

> For every anchor a third party has **seen**, the operator loses the ability to tell a different
> story about the rows that anchor covers.

Seen, not stored safely. The control begins at publication and not at the timestamp, and publication
needs a schedule: the first reason again.

## What would make it worth building

A process that runs when nobody is there, fetches a timestamp on a cadence and publishes each anchor
where the deployment's administrator cannot revise it. A place to run a job on a schedule supplies
the first part only. The rest:

- **A job, and its cost.** A Container Apps job or a timer-triggered Function is metered for each
  execution, and storage and the TSA may cost beside it. It is not priced.
- **A copy that one principal cannot revise.** Three identities under one principal that holds Owner
  or User Access Administrator are one principal: it can grant itself any of them again. The
  constraint is the boundary: the scope of each role assignment, who may assign roles at that scope,
  and a destination that is a public append-only repository or immutable storage with a **locked**
  retention policy, because an unlocked one is changed by the same principal.
- **A decision on what publication reveals.** The digest reveals nothing about any row. The sequence
  bounds and the row count show how many events were audited between two anchors
  ([`docs/audit/`](../audit/README.md)). Publishing the digest alone avoids it.
- **A home for the token.** The CMS `TimeStampToken` is the only object that carries the TSA's
  signature, its `genTime` and the imprint that binds both to the anchored bytes. A record published
  without it is a claim, not a timestamp, and a token kept in a table beside the rows it protects
  goes with them. So the exported copy is the anchor, and the local table is a cache of it.
- **A pinned trust root.** `Rfc3161TimestampToken.VerifySignatureForHash` resolves the signer
  certificate, matches it against the token's `ESSCertID`, requires the timestamping EKU
  `1.3.6.1.5.5.7.3.8`, checks `genTime` against the certificate's validity and verifies the CMS
  signature with `verifySignatureOnly: true`. It builds no chain, so there is no trust anchor and no
  revocation lookup, and every check passes for a certificate an attacker minted. The trust decision
  is the caller's: chain the certificate to a root, with its policy OID, pinned out of band, beside
  the secrets and never in the database being verified. This is read from the API's contract and
  source and not demonstrated: nothing here issues or verifies a token.

### What has to be settled before the first token

The last three are one-way doors.

- **The interval**, derived from how fast money moves and not from the clock. It is the floor of the
  window of invisible truncation, not its size: the window runs back to the last anchor a third
  party has seen, so a run that leaves a gap marker in place of a token, or a copy published late,
  widens it past one interval.
- **The trust root.** Once tokens exist under one root, dropping that root retires the anchors that
  depend on it.
- **Whether the anchor record is authenticated. Built.** Each record carries an HMAC under
  `Audit:AnchorKey`, a key the row chain does not use, over twelve elements that include its kind
  and the hash of the record before it (`AuditAnchorChain.RenderPayload`). Somebody who holds only
  the database can delete a record from the middle, which the anchor walk reports, but cannot mint
  one and cannot turn an anchor into a gap marker. It does not constrain whoever holds the key. It
  had to come first: records published without a MAC can be derived again at any later date, so a
  MAC added afterwards dates nothing.
- **What is hashed, for good. Built.** Each row records the payload version that rendered it and,
  from `v3`, an identity of the key that signed it, both inside the hashed payload, and a row the
  walk cannot render, or one that names a key the verifier lacks, is a break (ADR-0044 D2). What a
  TSA would sign is the anchored value, an unkeyed digest over eight elements
  (`ComputeAnchoredValue`). Before the first token a change of either is a migration. After it the
  anchored value is fixed in a signed token that nobody can issue again, so whatever has to be in
  the hashed payload has to be there first, with a test that verifies a row written under an older
  scheme.

## What is true today

An intact verdict from `verify` ends with:

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

`TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit` deletes the last row and asserts that the
chain still reports intact, so that the claim cannot quietly grow back.

**Anchor records exist, and they are local.** The tool's `anchor` verb walks the chain and records
its tail in `AuditAnchors`, a second chain under `Audit:AnchorKey` (ADR-0044 D2). No third party
timestamps them.

**A copy can leave the machine.** `AzureBank.AuditVerifier export <path>` writes the anchor chain to
a file outside the database, one JSON record per line, and refuses to overwrite an existing file,
because overwriting the earlier copy with the current state is the move the copy exists to make
visible. It copies stored columns and derives nothing. `diff` between two exports is the comparison.
A sample, with its notes, is in [`docs/audit/`](../audit/README.md).

**The gap is a number.** Every `verify` verdict is followed by an UNCOVERED WINDOW block: how far
`AuditEvents` runs past the deepest sequence any anchor claims to cover, computed from local data
alone. A NEGATIVE window is reported apart and is the one to stop for: the anchors claim coverage
through a sequence that no longer exists. With that reading the tool prints the number's two limits.
It heals: sequences are issued again after a truncation, so enough new rows bring the tail back past
the claim and the window reads zero
(`WritingOverATruncationHEALSTheWindow_WhichIsWhyItIsNotADetector`). And it is blind to the thorough
version: delete the covering anchors with the rows and the claim drops with the tail
(`ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit`).
[The runbook](../runbooks/audit-chain-unavailable.md) says how to read each.

**None of this is the control.** The window measures distance, not time, and the anchors it measures
against sit in the database they anchor. A file this machine wrote to its own disk has been seen by
nobody, and whoever can truncate the table can delete the file too: two acts, one against the
database and one against the filesystem, so a careless truncation leaves the copy behind. `export`
is a verb somebody types. The schedule, the third party and the timestamp are all missing. Until
they exist, the only witness outside the machine is the count that whoever runs the tool keeps for
themselves: the one the verdict asks for.

**When anchoring is built, the tail test stays.** It asserts what `AuditChain.VerifyAsync` returns,
and an anchor check is another layer, in the verifier, so the test keeps passing and nothing turns
red to say that the documents are due. A second test at the verifier layer then asserts what the
anchor catches, and ADR-0044, the runbook and this page change from "this cannot be detected" to
"the chain cannot detect it, and this does".
