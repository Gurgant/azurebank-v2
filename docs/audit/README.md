# The exported anchor copy

`anchors.sample.jsonl` is what the `export` verb of the audit tool writes: the anchor chain, one
record per line, copied out of the database so that a later run has something to disagree with. It
is a **sample**, produced by the real command against a throwaway in-memory database, and not the
audit trail of any deployment. **Do not regenerate it**: a test depends on these very records
([the sample's provenance](#the-samples-provenance)).

To copy a real chain, from the repository root:
`dotnet run --project backend/tools/AzureBank.AuditVerifier -- export <path>`. The verb refuses to
overwrite an existing file. The tool reads the connection string and the two audit keys from the
environment, as [the runbook](../runbooks/audit-chain-unavailable.md) shows.

## What the copy buys, and what it does not

The audit chain cannot see rows deleted from its end, and whoever truncates `AuditEvents` can delete
the anchors that cover past the cut: both chains then verify, because each links backwards only
(`ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit`). A copy that is no
longer in the database cannot be deleted along with it. It holds one record per line, appended in
counter order and never rewritten, so `diff` between two exports is the comparison and needs no
code:

```
export → (time passes) → export to a second path → diff
```

A new anchor is one **added** line, and a history rewritten downward is a **removed** one. Under
version control the same holds: a commit that appends is additions only. That is why the format is
JSON Lines and not a JSON array, which would rewrite the previous last line on every append.

**It is a demonstration and not a control.** A file this machine wrote to its own disk has been seen
by nobody, and whoever can truncate the table can delete the export too. `export` is a verb an
operator types, nothing runs it unattended, and nothing here is timestamped by anybody else.
[`anchoring-the-audit-trail.md`](../deferred/anchoring-the-audit-trail.md) has the argument.

## What a reader can check with no key at all

- **`previousAnchorPayloadHash`** on each line equals **`payloadHash`** on the line before it. That
  is the chain, and it is plain SHA-256 over values in the file.
  `ExportedSampleTests.The_sample_is_a_chain_a_reader_can_follow_without_any_key` asserts it against
  this file.
- **`anchoredValue`** is an unkeyed digest of the state the record claims, so that it survives a key
  rotation and can be checked by somebody who holds no secret.

**`mac` is not one of those.** It is keyed under `Audit:AnchorKey`, and it can be checked here only
because the sample's keys are printed below. A real export carries real authentication codes and
belongs wherever real secrets belong.

## What publishing one of these reveals

Not the content of any audited event: no line carries an actor, a subject, an amount or a
description. A record holds a counter, a payload version, a kind, two key identities, three coverage
numbers, four hashes, a MAC and a creation time. The coverage numbers (`lowestCoveredSequence`,
`coveredThroughSequence`, `coveredRowCount`) say how many audited events existed at each anchoring,
so published on a schedule they show how busy the system was in each interval. That is traffic
analysis and not disclosure, and on a system with real users it is a decision to take.

## The sample's provenance

- **Produced by** `ExportCommand.RunAsync`, the code path the verb runs, against EF Core InMemory.
- **`Audit:ChainKey`**: `azurebank-sample-chain-key-published-in-this-repo`
- **`Audit:AnchorKey`**: `azurebank-sample-anchor-key-published-in-this-repo`
- **Shape**: one gap marker from an empty table, then three anchors, after four, eight and twelve
  events.

Both keys are printed on purpose: a published HMAC is an offline oracle for guessing the key behind
it, and publishing the key leaves nothing to guess.

**Do not regenerate this file.** Its four records were written under the older payload version,
`a1`. They are the only records in the tree under it, and `ExportedSampleLadderTests` reads them to
prove that this build still authenticates a scheme it no longer writes. A regenerated file turns
that test red: restore the file, and never clear the red by deleting the assertion. For a fresher
illustration, add a second file. `ExportedSampleTests` reads the file too, and checks its shape, its
chain and its bytes.
