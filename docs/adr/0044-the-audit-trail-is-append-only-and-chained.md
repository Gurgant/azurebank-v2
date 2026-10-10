# ADR-0044: Security events go to an append-only, hash-chained table

**Status:** Accepted · **Date:** 2026-08-19 · **Amended:** 2026-08-29 to 2026-09-02 (D6, D7, D8),
2026-09-04 to 2026-10-03 (ADR-0047, ADR-0049, ADR-0050, ADR-0056, ADR-0057, ADR-0058, ADR-0062)

## Context

Seventeen security events are logged by the API, counted by log site, and eight by the BFF. Before
this decision that was all: a log rotates, whoever can reach the host can write to it, and nothing
about it is evidence. PCI DSS v4 10.3.2 and NIST SP 800-53 AU-9 ask that audit logs be protected
from modification, and PSD2 Art. 72 that an institution can reconstruct what happened.

## Decision

- **D1 — a failed audit write fails the business action.** `IAuditService.Record` only calls `Add`
  and the row rides the caller's `SaveChangesAsync`, so both land or neither: better a blocked bank
  than an emptied one, because a movement never recorded cannot be accounted for to the customer
  whose money moved (`WhenTheAuditRowCannotBeWritten_TheBusinessChangeIsRolledBackToo`). A refusal
  uses `RecordRefusalAsync`, on its own scope and committed at once, because the rollback of what
  was refused would take its record along; each method throws on the other's outcomes. D1 has no
  exception: the tripwire of ADR-0057 revokes nothing, so its unwritable row fails the request
  loudly (`WhenTheTripwiresAuditRowCannotBeWritten_TheAnswerIs500_AndTheOtherSessionStaysActive`).
- **D2 — the chain now, the SQL Server ledger later.** Each `AuditEvents` row carries an HMAC-SHA256
  over its fields, `Sequence` included, and the previous row's hash: keyed, because every field is
  enumerable and anyone holding the table could recompute a bare digest. Its payload also holds the
  version that rendered it and, from `v3`, an identity of its key (not a widening: every `RowHash`
  is the same oracle for a guessed key), so a new field or a rotated key invalidates nothing already
  written; a row that cannot be rendered, or names a key the verifier lacks, is a break. The
  operator tool `AzureBank.AuditVerifier` walks the chain (`verify`) and records its tail as an
  anchor, in a second chain under `Audit:AnchorKey` (`anchor`). The ledger would refuse a rewrite
  and an RFC 3161 timestamp would date the tail from outside: neither replaces the other and both
  are deferred, not rejected, because `APPEND_ONLY` is a one-way door on a moving schema and an
  anchor is only as fresh as its unattended job (`docs/deferred/anchoring-the-audit-trail.md`).
- **D3 — the chain is applied in the `SaveChanges` funnel.** `AzureBankDbContext` reads the tail
  under `UPDLOCK, HOLDLOCK`, assigns `Sequence` and the hashes and inserts in one transaction,
  because the writer cannot hold a lock across the insert and a `SaveChangesInterceptor` would be
  dropped in silence under test, where `CustomWebApplicationFactory` rebuilds the registration.
- **D4 — the eight BFF events stay log-only.** `RateLimitExceeded`, `CrossSiteRequestBlocked`,
  `StepUpRequired`, `StepUpWithoutSession`, `RawRefreshBlocked`, `RawAuthEntryBlocked`,
  `SessionRequired` and `RefreshRejected` are raised in the BFF, which has no database, and giving
  it one means a second writer against `AuditEvents`: a larger decision than this one.
- **D5 — no personal data in the table.** Actor and subject are ids, and `Detail` is JSON capped at
  1024 characters, so that no stack trace, with the personal data inside it, reaches a table that is
  never purged. A movement's row carries no amount, counterparty or account, because the ledger row
  its `SubjectId` reaches holds them and an amount beside an actor id is financial data about an
  identifiable person: a deposit's `Detail` is null, the others name only the authorisation they
  consumed (`AuditDetails`), and a refusal's is the `ErrorCodes` constant and nothing else.
- **D6 — retention is a policy, and this table is never purged.** The period is five years, designed
  for ten, after the business relationship ends (AMLR, Regulation (EU) 2024/1624, Art. 77; PSD2
  Art. 21; AMLD Art. 40 for ten); none binds a system that is no obliged entity or payment
  institution. No purge is safe: an empty table reads as new, one without its oldest rows as
  tampered (`DeletingTheOLDESTRows_IsLOUD_WhichIsWhyRetentionCannotPurgeThisTable`). Erasure is not
  discharged: three redesigns would do it (a chain of droppable periods, per-subject encryption,
  making ledger rows deletable), none proposed; only a demo copy's ledger is deleted (ADR-0062).
- **D7 — a key ring, so a rotation stops destroying the history it protects.**
  `Audit:RetiredChainKeys` holds each retired key with the `LastSequence` it stopped at, and
  verification selects the key a row names: trying keys in turn would accept a row a retired key
  mints at any sequence (`ARowThatLIESAboutItsKeyIsCaught_WhichIsWhyTheRingSELECTSRatherThanTRIES`).
  A retired key only reads, and only inside its epoch, which starts after the previous retirement
  (`TheNEWESTRetiredKeyCannotREAUTHORWhatOlderKeysWrote_BecauseAnEpochHasTwoEnds`). The founding key
  is named, never assumed: "whatever adds a second key must add a ring entry for the FOUNDING key
  rather than silently re-point history at whatever is current", so `Audit:FoundingChainKey` names
  the entry that answers for rows with no identity. `AuditKeyRingStartupCheck` builds the ring at
  startup; an anchor names its tail's key. `Audit:AnchorKey` has no ring: a rotation ends its chain.
- **D8 — the evidence pack reads by transaction, and says which half the chain vouches for.** The
  verifier's `evidence <transactionNumber>` assembles what PSD2 Art. 72 asks for one movement: its
  ledger row, the consumed `StepUpAuthorization`, its audit rows and the chain verdict. The success
  row names that authorisation under its hash, and the pack checks the unchained table against it
  (`ATransferWhoseAuthorisationRowIsGone_IsBOUNDAUTHORISATIONMISSING_AndTheChainStaysIntact`).
- **What is wired, and what is not.** Fourteen events write a row, at nine `_audit.Record(` and
  seven `_audit.RecordRefusalAsync(` sites. Seven are administrative: `AccountDeleted`,
  `AccountNumberRevealed`, `AzureTagRenamed`, `PinEnrolled`, `RefreshTokenUnknown`,
  `RefreshTokenReuse` (the tripwire) and `PinChanged`, which alone writes no log line (ADR-0047).
  Four are money movements (B1 in the code), `MoneyDeposited`, `MoneyWithdrawn`, `MoneyTransferred`
  and `MoneyTransferredInternally`: one row each, a transfer's on its outgoing ledger row, dropped
  with a failed attempt (`ADepositThatRetries_WritesExactlyOneAuditRow`), and no log line: evidence
  to keep, not an alert. Three are refusals, `MoneyWithdrawalRefused`, `MoneyTransferRefused` and
  `AccountDeletionRefused` (ADR-0049): no step-up authorisation, or a wrong or locked PIN at the
  four mints (ADR-0056). Registration refusals and business validation (insufficient funds, the
  daily limit) stay log-only, because a row the caller can repeat at will is an unbounded write on
  the tail lock (`AWithdrawalRefusedForFunds_WritesNoRow_AndThatIsTheDecision`); retry collisions
  because they are health signals, not acts of a principal; `RefreshRenewalRateHigh` because a
  renewal writes nothing (ADR-0057). `AuditChainUnavailable` is log-only by necessity: its row would
  need the lock that just failed.

## Rejected

- Rejected: a transactional outbox to keep availability, because its own write is fail-closed too.
- Rejected: `SET LOCK_TIMEOUT` for the tail bound, because a pooled session would carry it on.
- Rejected: an endpoint instead of the `AzureBank.AuditVerifier` tool, because no role guards one.
- Rejected: a soft delete for retention, because a flag is not erasure and the chain cannot see it.
- Rejected: re-hashing history on a rotation, because that is the operation an anchor detects.
- Rejected: an options validator for the ring's rules, because a rule kept in two places drifts.

## Consequences

- Audited operations stop when the table is unwritable. No standard requires that: NIST SP 800-53
  AU-5 asks for an alert, and AU-5(4), shutdown on failure, is in no SP 800-53B baseline.
- Every audited save queues on one tail lock, bounded by `Audit:TailTimeoutSeconds` (5): a save that
  cannot take it moves no money and answers 503 (`AuditChainContentionSqlServerTests`, ADR-0058).
- Readiness (`AuditChainHealthCheck`) names an unreadable store, a read-only database or a refused
  `INSERT` (`AuditWritePermissionSqlServerTests`): see `docs/runbooks/audit-chain-unavailable.md`.
- `Audit:ChainKey` and `Audit:AnchorKey` are secrets of 32+ characters, apart from every other key.
- Not covered (D2): the end of the table. The honest claim is narrow: this chain detects tampering
  by someone who holds the database but not the key whose epoch that row falls in, except at the end
  of the table (`TruncatingTheTAIL_IsNotDetected_AndThisPinsTheLimit`), anchor records or not.
- Not covered (D6): erasure. Pseudonymous ids are personal data while anyone can link them, and
  `EnforceTransactionImmutability` refuses to delete the ledger row a money event names, so a lawful
  deletion needs raw SQL around the guard: the compliant act and the attack are the same act.
- Not covered: an authorisation's row is unchained, a successful mint writes no row, an intact
  verdict is no inclusion proof, no schedule verifies the chain, and partitioning the chain is open.

## Verified by

- `AuditChainTests`, `AuditChainSqlServerTests`, `AuditTrailPersistenceTests` (reads the table).
- `TheEventInventoryThisAdrStatesIsStillTheOneInTheSource` (counts), `AuditProseGuardTests` (names).

## Related

ADR-0009, ADR-0042, ADR-0045, ADR-0047, ADR-0049, ADR-0050, ADR-0056, ADR-0057, ADR-0058, ADR-0062.
