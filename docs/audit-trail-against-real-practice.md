# The audit trail against real practice

What this project's audit trail does, set against what a real deployment would have, with the gap
named in each direction. It is a comparison: nothing is decided on this page. The decisions are in
ADR-0044 ([`docs/adr/`](adr/README.md)), and the argument for anchoring the trail outside the system
is in [`deferred/anchoring-the-audit-trail.md`](deferred/anchoring-the-audit-trail.md).

## The short answer

**The shape is mainstream.** Two layers: a keyed hash per record, over the record and its
predecessor, and a checkpoint over (range, count, tail hash), chained to the checkpoint
before it. That is the structure of AWS CloudTrail's log-file integrity validation, where the
digests are chained so that a deleted digest is detected
([digest file structure](https://docs.aws.amazon.com/awscloudtrail/latest/userguide/cloudtrail-log-file-validation-digest-file-structure.html)),
and of SQL Server's ledger, where each block is hashed over the root hash of the block before it
([ledger overview](https://learn.microsoft.com/en-us/sql/relational-databases/security/ledger/ledger-overview?view=sql-server-ver17)).
It is not a copy of either: CloudTrail's digest lists the log files of a time window and is signed
with `SHA256withRSA`, and this checkpoint carries a sequence range with a row count under an HMAC.
That difference is a real divergence: only a holder of the key can check an HMAC, so nobody outside
can check that a record or a checkpoint is authentic. Without the key, a reader of an exported copy
of the checkpoints can still follow them: each one names the hash of the one before it and carries
an unkeyed digest of the state it claims
([what a reader can check with no key](audit/README.md#what-a-reader-can-check-with-no-key-at-all)).

**The stated limit is standard too.** Microsoft's page says of the ledger that it cannot prevent
such attacks, and that tampering is detected when the ledger data is verified. ADR-0044 states this
chain's claim as narrowly: it detects tampering by someone who holds the database but not the key
whose epoch the row falls in, except at the end of the table. The verifier prints that limit with
every intact verdict.

**Almost every divergence below comes from two premises:** nothing verifies or anchors the trail on
a schedule, and one principal administers everything.

**Two things a reader of a banking piece looks for and does not find.** Absence carries no
information: a missing anchor looks the same as a quiet fortnight, and CloudTrail and RFC 6962 both
solve that with a cadence on top of linkage. And nothing outside the chain constrains writes to the
table: no engine `APPEND_ONLY`, no `DENY UPDATE` or `DENY DELETE`, no WORM storage, so **detection
is the only layer there is**.

## What a real deployment has, and what is here

"Built" means that it is in `main` and a test holds it. "Named" means that the documents say the
true thing and nothing is built.

| # | A real deployment has | Here | Gap |
|---|---|---|---|
| 1 | A copy of the checkpoint that whoever runs the system cannot revise | **Half built**: `export` writes the anchor chain to a file and refuses to overwrite one | The file is on the machine that would be attacked, and nobody else has seen it |
| 2 | A cadence, so that absence is evidence | **Named, plus a number**: the UNCOVERED WINDOW under a `verify` verdict, not computed when either chain is broken | Nothing anchors on a schedule, and the number is blind to anchors deleted with the rows (`ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit`) |
| 3 | Third-party time (RFC 3161) | **Named only** | No code; the hard part is the pinned trust root |
| 4 | Immutability in the storage or the engine | **Named only** | The app's database user may update and delete audit rows |
| 5 | Scheduled verification with alerting | **Named only**: `verify` runs when a person runs it | No schedule verifies the chain |
| 6 | Separation of duties | **Named only** | One account administers the database and everything around it |
| 7 | Inclusion proofs, to hand over part of the rows | **Named only**: the evidence pack prints the rows and the verdict and claims no proof | A tail anchor yields none |
| 8 | A retention and erasure policy | **Retention decided, erasure open** (ADR-0044 D6) | The table is never purged, and erasure is not discharged |
| 9 | A notice to the account holder over a channel the session does not control | **Half built**: the row rides the enrolment (ADR-0045) or the PIN change (ADR-0047), and a runner renders it to a message file (ADR-0048) | The last hop is a directory; one unvalidated address; no relay; no PIN reset behind the contact |

Rows 1 to 3 are argued in [the deferred page](deferred/anchoring-the-audit-trail.md): what `export`
and the uncovered window buy, and why neither is the control. Row 9 is in
[`deferred/relaying-the-enrolment-notice.md`](deferred/relaying-the-enrolment-notice.md).

### Row 4: least privilege on the table is not built, and `dbo` cannot be denied

The cheap demonstration would be a grant: the application's principal may `INSERT` into
`AuditEvents` and is refused `UPDATE` and `DELETE`.

**On a development machine** the API connects with `Trusted_Connection=True`, as the Windows account
it runs under. The connection string selects that account and grants it no role. Measured on one
such machine: the account is mapped to `dbo` and is in `db_owner` and `sysadmin`; against a scratch
table the engine refuses to record a `DENY` for that principal, and the delete goes through.

```
Cannot grant, deny, or revoke permissions to sa, dbo, entity owner,
information_schema, sys, or yourself.
DELETE SUCCEEDED despite DENY -- rows left: 0
```

**On Azure** the app signs in as a database user in `db_datareader` and `db_datawriter` (ADR-0061,
`infra/sql-principals.sql`). That user is not `dbo`, may update and delete audit rows, and no `DENY`
is recorded against it. `AuditWritePermissionSqlServerTests` already creates a user, denies it
`INSERT` and runs as it, so the means for the demonstration exist. A `DENY` on the app's user would
constrain the application and not whoever administers the database: it would refuse the accident the
runbook warns against, deleting rows to clear a stuck table, only when that is done as the app's
user.

### Row 7: a tail anchor gives no inclusion proof

An anchor over (range, count, tail hash) lets its holder check that a set of rows is intact. It does
not let them prove to somebody else that one particular row is in the set without handing over the
range, which is what a regulator who asks for part of a customer's history would want. ADR-0044
lists it under what is not covered, and the evidence pack (`evidence <transactionNumber>`) prints
the rows and the chain's verdict without claiming the proof.

## Proportionality

**For a real bank: under-built, on the operational half.** The cryptography is the right family and
is adequate. What a supervisor or an external auditor would look for and not find is in rows 1 to 5
and in row 8. The scope of the control is the one the verifier prints: tampering by somebody who
holds the database but not the key whose epoch the row falls in, up to the last count a person wrote
down.

**For a portfolio: right, and at the edge of over-built.** NIST SP 800-53 AU-9(3), cryptographic
mechanisms that protect the integrity of audit information and audit tools, is allocated to the High
baseline and to no Low or Moderate one
([AU-9(3)](https://csf.tools/reference/nist-sp-800-53/r5/au/au-9/au-9-3/)).

**What carries the value is not the HMAC.** It is the limits stated around it: a verifier that
prints its own limit with every intact verdict, and tests that assert the uncomfortable direction on
purpose, so that a claim cannot grow back: `TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit` and
`ConsistentSuffixRemovalFromBOTHChains_IsNotDetected_AndThisPinsTheLimit`.

## What this page does not establish

- **Whether a least-privilege split would hold.** What is measured is that a `DENY` against `dbo` is
  refused and has no effect. A `DENY` of `UPDATE` and `DELETE` on `AuditEvents` against another user
  was not tested.
- **PCI DSS text.** The requirement on change detection for audit logs was read through secondary
  sources only: the standard is behind the PCI SSC document library.
- **Whether SQL Server's ledger is available on every hosting option.** No statement was found
  either way.
- **That scheduled verification is hard.** It is a matter of tooling: for ledger verification
  Microsoft names Elastic Jobs or Azure Automation on Azure SQL Database, and SQL Server Agent on
  Managed Instance and SQL Server.
