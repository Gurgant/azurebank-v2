# ADR-0052: A notice names the audit row it belongs to

**Status:** Accepted · **Date:** 2026-09-09 · **Amended:** 2026-09-11 (the nulled pointer, under
D3's limit)

## Context

`notify` and the relay report `NO AUDIT ROW` when the evidence a notice belongs to cannot be found.
Before this decision the check was an existence query on `(ActorUserId, Event)`: has this user ever
done this kind of thing, not is THIS notice backed. While every kind happened once per account the
two were the same question. ADR-0047 makes `PinChanged` repeatable: a user with five changes has
five notices and five rows, and deleting the evidence of the third reports nothing, because the
first still answers. ADR-0047 records that as a limit and names the fix taken here: a per-notice
reference. It costs one nullable column, one index and one migration, and one interface gains a
return value.

## Decision

**D1 — The reference goes on the notice: a nullable `AuditEventId` on `SubscriberNotices`**,
because that table is deliberately unchained (ADR-0045 D7), so a column there touches no hash and
no walk. `AuditEvents` is HMAC-chained over an explicit field list in `AuditChain.ComputeRowHash`:
a column there is either outside the hash, an unprotected field on the one table whose purpose is
tamper-evidence, or inside it, which needs a new `CurrentPayloadVersion` and a renderer arm that
stays forever.

**D2 — A plain column, still no foreign key to `AuditEvents`**, because a notice whose evidence
has gone missing must be FOUND rather than refused, and a foreign key would refuse the very write
or delete that made it missing. That decision is the `AddSubscriberNotices` migration's, and it
stands: naming a row is not constraining it. The table's one foreign key is the cascading one to
`AspNetUsers`, by which a notice is erased with its owner and never on its own.

**D3 — The named row must also be this notice's: the exact check compares `(ActorUserId, Event)`
against the row the id names**, because `Id == evidence` alone would accept any surviving audit
row: whoever can write `SubscriberNotices` could delete a notice's evidence and re-point it at a
row that is still there. The chain cannot cover this one: nothing in `AuditEvents` is touched by
that edit, so every `RowHash` still verifies. It costs two predicates on a primary-key lookup. The
two failures print as one finding, "gone, or is not this notice's", and the repudiation runbook
(`docs/runbooks/pin-enrolment-repudiated.md`) carries the query that tells them apart.

**D4 — The chain covers the row; D3 covers the pointer.** `AuditEvent.Id` is element three of the
hashed payload, so giving an audit row another identity, to make some other notice's pointer land
on it, breaks that row's `RowHash` and the walk reports it. A referential constraint would check
only that a row exists, and cover neither.

**D5 — `IAuditService.Record` returns the id it minted**, because the writer assigns it before
`SaveChanges` (`Guid.CreateVersion7()` in `AuditService.Build`), so the notice carries it in the
same unit of work, with no second round trip and no second save. `Sequence` and `RowHash` are not
available then: `AuditChain` assigns both inside the save. Every other call site ignores the
return; the two that owe a notice use it.

**D6 — The column is nullable, the old existence question is the fallback, and the line says which
question was asked**, because every notice written before the migration names no row, and asking
those the exact question would report the whole backlog as missing its evidence on the first run.
"The `PinChanged` row it names is gone" is worth acting on; "no `PinChanged` row exists for that
user at all — this notice names no row, so that is the weaker question" may only mean the notice
predates the column (`ANoticeThatNamesNoRow_StillGetsTheWeakerQuestion_WhichIsTheBacklogsShape`).

## Rejected

- Rejected: the reference on `AuditEvents`, because it is unprotected outside the hash, and a
  payload version kept forever inside it (D1).
- Rejected: a foreign key to `AuditEvents`, because it would refuse the write or delete that made
  the evidence missing, and would check only that a row exists (D2, D4).
- Rejected: naming "gone" and "not this notice's" as two findings, because it needs a third state
  threaded through the result for a distinction one `SELECT` in the runbook resolves.
- Rejected: reading the audit row back off the `ChangeTracker` at the notice's call site, because
  it couples the notice writer to the audit writer having added to the same context and not saved
  yet: true today, and wrong silently if it ever stops being true.
- Rejected: an integrity binding on the pointer, a MAC over the pair, because it needs a new
  validated secret and closes one door in a room with several open (see the limits below).

## Consequences

- The finding is raised on rows that raised nothing before, and it cannot change an exit code or a
  sweep summary: `NO AUDIT ROW` is a console line and a `Warning`, `notify`'s exit is decided by
  `owed` alone, and the relay's arm does not touch `NoticeSweepSummary`.
- An unmigrated database fails every enrolment and every PIN change, not only the notice: the
  insert names a column the table does not have, in the same transaction, so the PIN is not set.
- Not covered (D3's limit): D3 reaches across kinds and not within one. A change notice re-pointed
  at an enrolment's row is caught; one re-pointed at another `PinChanged` row of the same user is
  not. `ARePointToANOTHERRowOfTHESAMEKind_IsNotCaught_AndThisPinsHowFarD3Reaches` pins zero
  findings, and goes red the day an integrity binding arrives.
- Not covered (D3's limit, the nulled pointer): a nulled `AuditEventId` is asked the old question,
  because a null is how the run knows a notice written before the migration. For a repeatable kind
  another row of the same user then answers
  (`ANULLEDPointer_IsAskedTheOldQuestion_SoAnotherRowOfItsKindAnswersForIt`). A binding over the
  pair would not close it alone: nulling pointer and MAC together leaves the backlog's shape, so it
  also needs every pre-migration notice bound, or the fallback retired.
- Not covered: a cheaper suppression. `SubscriberNotices` is unchained, and setting `DeliveredAt`
  and `DeliveryReceipt` takes a row out of every claim path, so the check never runs on it. What
  makes tampering evident is the chain, and the chain is on `AuditEvents` by decision.

## Revisit when

- A notice belongs to more than one audit row: one nullable column stops being the right shape.
- Audit rows are deleted as policy: expired must be told from removed, which only the chain can.
- `SubscriberNotices` gains a hash chain: the pointer becomes tamper-evident with no new secret.
- A transport really sends: "delivered without its evidence" may deserve to block, reversing D2.

## Verified by

- `NotifyCommandTests`: `ANoticeWhoseOwnAuditRowIsGone_IsFOUND_WhileTheUsersOtherRowsSurvive` and
  `ANoticeRePointedAtAnotherRow_IsStillFound_BecauseTheNamedRowMustBeThisNotices`.
- `SubscriberNoticePersistenceTests`: the enrolment and the change each name their row.

## Related

ADR-0045, ADR-0047, ADR-0048.
