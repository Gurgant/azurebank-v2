# The exported anchor copy

`anchors.sample.jsonl` is what `AzureBank.AuditVerifier export <path>` writes: the anchor chain,
one record per line, copied out of the database so that a later run has something to disagree with.

This is a **sample**. It was produced by the real command against a throwaway in-memory database, so
that a reader can see the shape without running anything. It is not this project's audit trail, and
there is no real audit trail to be one — nothing here is deployed.

## Why the file exists at all

The audit chain cannot see rows deleted from its END. A surviving prefix links and hashes perfectly,
so verification reports intact, and nothing in the chain records how many rows there should have
been. The anchor record was added to give that a witness — it stores how far a verification walk
reached and how many rows it held — but on its own it does not close the gap either: the same person
who truncates `AuditEvents` can delete the anchors covering past the cut, and then **both** chains
verify, because each links backwards only. `ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit`
asserts exactly that, on SQL Server, using the two DELETE statements an attacker would really use.

A copy that is no longer in the database is the thing that cannot be deleted along with it.

## What this does NOT buy, first

**A file this machine wrote to this machine's disk has been seen by nobody.** The property an anchor
buys is scoped narrowly in `docs/deferred/anchoring-the-audit-trail.md`, and the scope is the whole
point: *for every anchor a third party has **seen**, the operator loses the ability to tell a
different story about the rows that anchor covers.* Seen, not stored safely. Whoever can truncate the
table can delete the export in the same breath, so the write is the beginning of the control and not
the end of it.

**And it is a demonstration rather than a control.** The same document rules that out in advance: a
control that depends on somebody choosing to run it does not constrain that person, and nothing in
this deployment runs unattended. `export` is a verb an operator types. That is honest as a
demonstration and would be dishonest as a guarantee, which is why this paragraph is above the
interesting one.

**Nothing here is timestamped by anybody else.** ADR-0044 records what would change that — an
RFC 3161 token, deferred rather than rejected — and records that it is not built.

## What it does buy

One record per line, appended in counter order, never rewritten. So `diff` between two exports is
the comparison, and it needs no code:

```
export → (time passes) → export to a second path → diff
```

A new anchor is one **added** line. A history that has been rewritten downward is a **removed** one.
Under version control the same property holds for free: a commit that appends is additions only, and
a commit that revises the past cannot look like one.

That is why the format is JSON Lines and not a JSON array. An array would rewrite the previous last
line on every append — one comma — and every line after the first would show as changed, drowning
the only signal the file carries.

## What a reader can check with no key at all

Two of the fields on every line are meant to be checkable by somebody holding none of this project's
secrets:

- **`previousAnchorPayloadHash`** on each line equals **`payloadHash`** on the line before it. That
  is the chain, and it is plain SHA-256 over values you can see.
  `ExportedSampleTests.The_sample_is_a_chain_a_reader_can_follow_without_any_key` asserts it
  against this very file.
- **`anchoredValue`** is an unkeyed digest of the state the record claims. It is unkeyed on purpose:
  it has to survive a key rotation and be checkable by somebody holding no secret of ours.

**`mac` is not one of those.** It is keyed under `Audit:AnchorKey`, and it is present here only
because the sample was generated with a key this file prints in full under **The sample's
provenance**, and a key that is written down is not a secret at all. A real export carries real authentication codes and belongs wherever
real secrets belong.

## What publishing one of these reveals

Not the content of any audited event — no line carries an actor, a subject, an amount or a
description. An anchor record holds a counter, a scheme version, two key identities, a kind, three
coverage numbers and three hashes.

**The coverage numbers are not a digest, and they are not nothing.** `lowestCoveredSequence`,
`coveredThroughSequence` and `coveredRowCount` say how many audited events existed at each anchoring.
Published on a schedule, the difference between consecutive counts is how busy the bank was in that
interval, and where it was quiet. That is traffic analysis rather than disclosure, and on a system
with no users it is a non-issue — but it is a decision taken rather than a property assumed, and
anybody copying this design onto a system with real users should take it again.

## The sample's provenance

- **Produced by** `ExportCommand.RunAsync` — the same code path the verb runs, not a hand-written
  illustration of it.
- **Store**: EF Core InMemory, discarded after generation.
- **`Audit:ChainKey`**: `azurebank-sample-chain-key-published-in-this-repo`
- **`Audit:AnchorKey`**: `azurebank-sample-anchor-key-published-in-this-repo`
- **Shape**: one gap marker from an empty table, then three anchors over four events each.

Both keys are printed above deliberately. A published HMAC is an offline oracle for guessing the key
behind it, and ADR-0044's argument that publishing a key identity "is not a widening" is scoped to an
attacker who already holds the table — which a reader on GitHub does not. Publishing the key removes
the question rather than answering it: there is nothing to guess.

🔒 **Do not regenerate this file.** It stopped being only an illustration when the anchor payload
gained a second payload version: these records were written under the older one, they are the only
records in the tree under it, and `ExportedSampleLadderTests` reads them to prove this build still
authenticates a scheme it no longer writes. If the documentation needs a fresher illustration, add
a second file and leave this one alone.

**Regenerating does not slip past anything, and the danger is not silence.** Measured by rewriting
the version field of all four records and running the test: it goes RED at the assertion requiring
a record under the older version — *"this fixture exists to exercise the OLDER payload version; if
every record here is current, the sample was regenerated and the ladder is unguarded again"* — while
the record-count assertion PASSES in that same run, because regenerating the same rounds does not
change the count. So the hazard is the next step, not that one: somebody meets a red they did not
expect, reads it as a stale fixture, and clears it by deleting the assertion. That is the move that
actually unguards the ladder, and nothing in the code can stop it.

Two tests read this exact file. `ExportedSampleTests` checks its shape, its chain and its bytes as
properties it must have on its own terms, rather than against a stored copy of itself.
`ExportedSampleLadderTests` checks the version ladder and, separately, pins the record count: the
first fails on ANY regeneration, the second only when the number of rounds changes too.

*(This paragraph used to open "to regenerate it, run `export` … and replace the file", and named
`ExportedSampleTests` as "the guard that keeps it honest". Both were true until the ladder gained a
second rung. A document that instructs is more dangerous than one that merely describes, because it
is followed.)*
