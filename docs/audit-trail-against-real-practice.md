# The audit trail against real practice

What this project's audit trail does, set against what a real deployment would have, with the gap
named in each direction. It is a **comparison**, which is why it is here and not in
[`docs/adr/`](adr/README.md): there is nothing being decided on this page. Decisions live in the
ADRs. Sharp edges live in [`engineering-traps.md`](engineering-traps.md). The argument for anchoring
the trail outside the system, and why this deployment cannot, lives in
[`docs/deferred/anchoring-the-audit-trail.md`](deferred/anchoring-the-audit-trail.md) — **that page
owns it and this one points at it**, because the same argument in two files becomes two arguments
the first time one of them is edited.

Written 2026-08-28 against `main`. Where this page says something was **measured**, it was run on
this machine and the output is quoted. Where it cites a document or a test, it cites it **by name**
— never by line number, because a line number is a reference an unrelated edit can silently break,
which this repository has already had happen to it.

---

## The short answer

**The shape is mainstream, not improvised.** Two layers: a per-record keyed hash covering the record
and its predecessor, and a periodic checkpoint over `(range, count, tail hash)` chained to the
checkpoint before it. **That is structurally AWS CloudTrail's design for log-file integrity
validation, not a field-for-field copy of it**, and the difference is worth keeping straight:
CloudTrail's digest carries the log FILES delivered in a time window with their hashes, plus
`previousDigestS3Object` / `previousDigestHashValue` / `previousDigestSignature`, and it SIGNS with
`SHA256withRSA`. This checkpoint carries a SEQUENCE range with a row count, and authenticates with
an HMAC. Same two layers, same reason for the second one — CloudTrail's own documentation says the
chaining exists so tools "detect if a digest file has been deleted"[^digest] — and different
primitives at every level below. The signing difference is itself one of the divergences named later
on this page: it is why nobody but the operator can check this chain.

The same architecture appears inside SQL Server's ledger, which hashes each block over the previous
block's root hash.[^ledger] Two independent products reaching the same two-layer shape is the point:
the design is the mainstream answer to this problem, not an invention that needs defending.

[^digest]: [CloudTrail digest file structure](https://docs.aws.amazon.com/awscloudtrail/latest/userguide/cloudtrail-log-file-validation-digest-file-structure.html)
[^ledger]: [SQL Server ledger overview](https://learn.microsoft.com/en-us/sql/relational-databases/security/ledger/ledger-overview?view=sql-server-ver17)

**The honesty is standard too.** Microsoft's own page says ledger "can't prevent such attacks but
guarantees that any tampering will be detected when the ledger data is verified". ADR-0044 reaches
the same place in its own words — "the honest claim is narrow: this chain detects tampering by
someone who holds the database but not the key" — and the verifier prints that limit above the green
line rather than leaving it to be inferred.

**Almost every divergence below is one premise expressed many times:** nothing here runs unattended,
and one principal owns everything. Both are true of this deployment and both are labelled where they
occur rather than only in a plan.

**Two things a reader of a banking piece will look for and not find.** Absence carries no
information here — a missing anchor is indistinguishable from a quiet fortnight, and the two real
systems examined both solve that with a cadence rather than with linkage. And nothing outside the
chain constrains writes to the table: no engine `APPEND_ONLY`, no `DENY UPDATE/DELETE`, no WORM, so
**detection is the only layer there is**. Neither is a defect in what is merged. Both are things
worth knowing before somebody asks.

---

## What a real deployment has, and what is here

Ranked by how much a reader would miss it. "Built" means it is in `main` and a test or a transcript
holds it in place; "named" means the document says the true thing and nothing was built.

| # | What a real deployment has | Here today | Gap |
|---|---|---|---|
| 1 | A copy of the checkpoint the operator cannot revise | **Half built** — `export` writes the anchor chain to a file and refuses to overwrite one | The file is on the machine that would be attacked; nobody else has seen it |
| 2 | A cadence, so that absence is evidence | **Named, plus a number** — the UNCOVERED WINDOW is printed under every verdict | Nothing runs unattended, so there is no promise to break |
| 3 | Third-party time (RFC 3161) | **Named only** | No code; the blocker is the pinned trust root, not the money |
| 4 | Storage- or engine-level immutability | **Named only** | ⚠️ Measured below: the engine *refuses to express* the least-privilege version against the principal in use |
| 5 | Scheduled verification with alerting | **Named only** | The engine's own scheduler does not exist on this class of machine either |
| 6 | Separation of duties | **Named only** | One human, one Windows account |
| 7 | Inclusion proofs for handing over a subset of rows | **Not named anywhere in the ADR** | A tail anchor yields none, and the ADR does not say so |
| 8 | A retention and erasure policy | **Open by the ADR's own correction** | Unsolved, and the ADR says it is |

---

## The two that moved, and the two that are worse than they look

### 1 — A copy can leave the machine now, and that is genuinely half of it

`AzureBank.AuditVerifier export <path>` writes the anchor chain as JSON Lines and **refuses to
overwrite an existing file**, because overwriting the earlier copy with the current state is exactly
the move the copy exists to make visible. One record per line means `diff` between two exports is
the comparison and needs no tooling. `docs/audit/anchors.sample.jsonl` is committed so a reader can
see the shape without running anything.

**What it does not buy is the word "cannot".** A file this machine wrote to this machine's disk has
been seen by nobody, and whoever can truncate the table can delete the file too. The honest claim is
*seen, not stored safely* — and this one is not yet seen either. The deferred page argues that
distinction properly.

⚠️ **But they are two operations, and that is worth stating rather than blurring.** Truncating
`AuditEvents` is a statement against the database; removing the export is a second act against the
filesystem. An earlier version of this paragraph said the truncation deleted the file "in the same
breath", which reads as one move and is not. The distinction is the same one the uncovered window
rests on: **the careless version of the attack leaves the copy sitting there**, and somebody who did
not think about a file on disk has left the thing that disagrees with them. It buys no guarantee —
one extra command removes it — but "no guarantee" and "no obstacle" are different claims, and this
page had been making the wrong one.

### 2 — The gap is a number, and a number is not a schedule

Every verdict is followed by an UNCOVERED WINDOW block: how far `AuditEvents` runs past the deepest
sequence any anchor claims. It is computed from local data alone and it does not pretend anything
runs. **And it prints its own two limits rather than leaving them to be found.** The number HEALS:
sequences are reissued after a truncation, so writing enough new rows brings the tail back past the
claim and the window reads zero again. And it is blind to the thorough version of the attack —
delete the covering anchors along with the rows and the claim drops with the tail, which
`ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit` asserts on purpose. A
**negative** window is the one to stop for, and it is reported separately: it means the anchors
claim coverage through a sequence that no longer exists.

**The cadence half cannot be simulated and is not attempted.** With no promised interval, a missing
anchor is not weak evidence — it is none. CloudTrail mints an empty digest and RFC 6962 §3.5 signs
the same tree with a fresh timestamp precisely to avoid that, and
[`anchoring-the-audit-trail.md`](deferred/anchoring-the-audit-trail.md) sets out both with the
quotes and the consequence. It is not restated here.

### 4 — ⚠️ The least-privilege demonstration is not merely unbuilt; the engine refuses it

The obvious cheap demonstration is a grant: show that the application's principal may `INSERT` into
`AuditEvents` but is refused `UPDATE` and `DELETE`. Measured on this deployment, that demonstration
cannot be written as stated.

The API connects with `Trusted_Connection=True`, which resolves to the developer's own Windows
account, mapped to `dbo`, in both `db_owner` and `sysadmin`:

```
principal = <operator-account> | db user = dbo | db_owner = 1 | sysadmin = 1
may SELECT / INSERT / UPDATE / DELETE / ALTER  on dbo.AuditEvents
```

Attempting the `DENY` against a scratch table produced two refusals at once:

```
Cannot grant, deny, or revoke permissions to sa, dbo, entity owner,
information_schema, sys, or yourself.
DELETE SUCCEEDED despite DENY -- rows left: 0
```

So the permission cannot even be recorded against this principal, and the delete goes through
regardless. **A demonstration written anyway would be a demonstration of nothing** — it would need a
second, least-privileged login created first, at which point it constrains the application and still
not the operator, who remains the same person.

This matters more than a missing test. `AuditWritePermissionSqlServerTests` already does `CREATE
USER` / `DENY` / `EXECUTE AS`, so the machinery exists and the reason this is not built is not
effort. The runbook's own line — that truncation "is also the easiest thing to do by accident while
trying to clear a stuck table at three in the morning" — describes precisely the accident a `DENY
DELETE` on the API principal would bounce, and precisely the one it cannot bounce here.

### 7 — ⚠️ The ADR does not say that a tail anchor gives no inclusion proof

An anchor over `(range, count, tail hash)` lets you check that a set of rows is intact. It does not
let you prove to somebody else that **one particular row** is in the set without handing over the
range. That is what an inclusion proof is for, and it is what a regulator asking for a subset of a
customer's history would want.

ADR-0044 does not contain the words. Searched on 2026-08-28: **zero occurrences.** The risk is not
that the design is wrong — a tail anchor was chosen deliberately — but that a later evidence-pack
piece of work reads the ADR, finds truncation addressed and inclusion unmentioned, and inherits it
as closed. **Naming an open problem is cheaper than discovering it has been assumed shut.**

---

## Proportionality

**For a real bank: under-built, and specifically on the operational half.** The cryptography is the
right family and is adequate. What a supervisor or an external auditor would look for and not find
is items 1–5 and 8 — an external copy, a cadence, third-party time, storage immutability, alerting,
and a retention policy. The honest scope of the control is the one the verifier prints: tampering by
somebody holding the database but not the key, up to the last number a human wrote down.

**For a portfolio: right, and at the edge of over-built.** NIST SP 800-53 AU-9(3) — "Implement
cryptographic mechanisms to protect the integrity of audit information and audit tools" — is
allocated to the **High baseline**, and to no Low or Moderate one
([AU-9(3)](https://csf.tools/reference/nist-sp-800-53/r5/au/au-9/au-9-3/); the same page also
allocates it to OT High in SP 800-82r3, which is a different framework and not the comparison being
made here). A piece that implements a High-baseline control and then documents where it stops is not
under-built by any reading.

**The thing carrying the value is not the HMAC.** It is the gradient of honesty around it: a
withdrawn argument left visible in the ADR instead of tidied away, tests that assert the
uncomfortable direction on purpose — `TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit` and
`ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit` both exist to stop a claim
growing back — and a verifier that prints its own limit above the green line. The risk with this
audience is the opposite of over-building: a reader who sees "hand-rolled HMAC chain", stops there,
and never reaches the part where it is CloudTrail's shape.

---

## What this page does not establish

- **Whether a least-privilege split would hold.** What is measured above is that the current
  principal is `sysadmin` and that `DENY` against it is refused and ineffective. Whether a separate
  login would behave as intended was **not** tested, because no such login exists.
- **PCI DSS text.** The requirement about change-detection on audit logs was read through secondary
  sources only; the standard itself is behind the PCI SSC document library.
- **Whether SQL Server ledger is available on every hosting option** this project might use — no
  statement was found either way.
- **The scheduled-verification claim is about tooling, not about impossibility.** Microsoft
  "recommends scheduling the ledger verification regularly", and names the mechanism per platform:
  *"Scheduling database verification in Azure SQL Database can be done with Elastic Jobs or Azure
  Automation. For scheduling the database verification in Azure SQL Managed Instance and SQL Server,
  you can use SQL Server Agent."* The point is that a machine started by hand has nowhere to put a
  schedule, not that scheduling is hard.
- **Nothing here was inferred from the mock.** Every behavioural claim on this page was read from
  the source in `main` or measured against a running SQL Server, and the measured ones are quoted.
