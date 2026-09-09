# ADR-0052: A notice names the audit row it belongs to

**Status:** Accepted · **Date:** 2026-09-09 · Takes the decision
[ADR-0047](0047-a-pin-change-owes-the-same-notice-an-enrolment-does.md) recorded as a limit and
listed under "What would change this": *"A per-notice reference from the notice to its audit row
would restore the `NO AUDIT ROW` finding to what it was when every kind was singular."* One nullable
column, one index, one migration. No endpoint, no client, no new secret, and one interface gains a
return value.

## Context

`notify` and the relay both report `NO AUDIT ROW` when the evidence a notice belongs to cannot be
found. Until this ADR the check was an EXISTENCE query:

```csharp
_context.AuditEvents.AnyAsync(e => e.ActorUserId == notice.UserId && e.Event == notice.Event)
```

*Has this user ever done this kind of thing*, not *is THIS notice backed*. While every kind happened
once per account those were the same question. [ADR-0047](0047-a-pin-change-owes-the-same-notice-an-enrolment-does.md)
made `PinChanged` repeatable, and from that moment they were not: a user with five changes has five
notices and five rows, and deleting the evidence for the third reports nothing, because the first
still answers the query. The detective control that was meant to notice a removed audit row stopped
noticing removals.

ADR-0047 recorded that rather than claiming it away, and named the fix: a per-notice reference. It
also said the fix reopened a decision, and there the record was wrong in a way worth stating.

**The "no foreign key" decision is not in ADR-0045.** That document contains the word "foreign" zero
times. The decision is in the `AddSubscriberNotices` migration's own remarks: *"there is deliberately
no foreign key to AuditEvents: the audit row and the notice are written in the same save and joined
by (ActorUserId, Event) when the notice is rendered, so that a notice whose evidence has gone missing
is FOUND rather than refused."* ADR-0047 cited ADR-0045 for it twice. Both citations are corrected
there, and ADR-0045 now carries a note saying where the decision actually lives, because that is
where a reader looking for it arrives.

## Decision

**D1 — The reference goes on the NOTICE, and the direction is not a coin flip.** `SubscriberNotices`
is deliberately unchained (ADR-0045 D7), so a column there touches no hash and no walk. `AuditEvents`
is HMAC-chained over an explicit field list in `AuditChain.ComputeRowHash`: a column there would be
either OUTSIDE the hash — an unprotected field on the one table whose entire purpose is
tamper-evidence — or inside it, which needs a new `CurrentPayloadVersion` and a renderer arm that,
in that file's own words, *"is added forever: rows written under it can never be re-hashed once an
anchor certifies them"*. A nullable `AuditEventId` on the notice costs none of that.

**D2 — A plain column, still no foreign key, and the migration's sentence survives intact.** The
reason for having no constraint was never laxity: a notice whose evidence has gone missing must be
FOUND rather than refused, and a foreign key would refuse the very write or delete that made it
missing. Naming a row is not constraining it. `SubscriberNotices` is not constraint-free — it has a
cascading foreign key to `AspNetUsers`, which is how a notice is erased, with its owner and never on
its own. It is constraint-free **towards `AuditEvents`**, and remains so.

**D3 — Tamper-evidence comes free, and an FK would not have added it.** `AuditEvent.Id` is element
three of the hashed payload, so re-pointing a notice by editing the audit row's identity breaks that
row's `RowHash` and the walk reports it. A referential constraint checks that a row EXISTS; the chain
already checks that it has not been altered.

**D4 — `IAuditService.Record` returns the id it minted.** The value exists before `SaveChanges`
because the writer assigns it — `Id = Guid.CreateVersion7()` in `AuditService.Build` — so the notice
can carry it in the same unit of work, with no second round trip and no second save. ⚠️ `Sequence`
and `RowHash` are NOT available: both are assigned inside the `SaveChanges` funnel by `AuditChain`,
which is why this returns the id and could not have returned either of them. Every existing call site
ignores the return and compiles unchanged; the two that owe a notice use it.

The alternative was reading the row back off the `ChangeTracker` at the notice's call site. It needs
no interface change and it was declined: it would couple the notice writer to the fact that the audit
writer adds to this same context and has not saved yet — true today, invisible if it ever stopped
being true, and wrong silently rather than loudly.

**D5 — Nullable, with the old question as the fallback, and the line says which it asked.** Every
notice written before the migration names no row. Asking them the exact question would report the
entire existing backlog as missing its evidence on the first run — a detective control turned into
noise on the day it shipped. So: exact when the reference is present, the old existence question when
it is not.

The two are different findings and the operator is told which: **"the `PinChanged` row it names is
gone"** against **"no `PinChanged` row exists for that user at all — this notice names no row, so
that is the weaker question"**. The first is worth acting on. The second may simply mean the notice
predates this ADR, and sending an operator to look for a row that was never referenced is how a
control loses its credibility.

## Consequences

The finding is now raised on rows that raised nothing before, which is the point — and it **cannot**
change an exit code or a sweep summary. `NO AUDIT ROW` is a console line and a `Warning`; `notify`'s
exit is decided by `owed` alone, and the relay's arm does not touch `NoticeSweepSummary`. Every
summary-equality assertion in the suite was safe and stayed green.

⚠️ **The forcing function ADR-0047 relied on did not fire, and that is the most useful thing this
ADR records.** `ARepeatableKind_MakesTheMissingAuditRowFindingWeaker_AndThisPinsHowMuch` existed to
go RED when the join became exact, so that whoever fixed it had to move ADR-0047's paragraph. It
stayed GREEN: the notices it builds name no row, so they take the fallback this ADR introduced, and
the test now describes that path correctly. ADR-0047 was moved deliberately instead. **A test guards
the shape it builds, not the claim its name announces** — and a limit pinned by a test that
constructs only the old shape will survive the fix that closes it.

The fix has its own test, `ANoticeWhoseOwnAuditRowIsGone_IsFOUND_WhileTheUsersOtherRowsSurvive`: two
change notices each naming their own row, one row deleted, exactly one finding. Falsified — reverting
the query to the existence check reddens it and nothing else.

⚠️ **An unmigrated database now fails every enrolment and every PIN change**, not just the notice:
the insert names a column the table does not have and it is part of the same transaction, so the PIN
is not set either. The migration's remarks say so where somebody applying it will read them.

## What would change this

- **A second reference on the same row.** If a notice ever belonged to more than one audit row — a
  change that also rotated something, say — one nullable column stops being the right shape and the
  question of a join table opens.
- **Deleting audit rows as policy.** Today a missing row is an anomaly and the finding is the point.
  A retention policy that removed rows on purpose would make this finding fire routinely, and the
  control would need to distinguish *expired* from *removed* — which the chain can answer and this
  column cannot.
- **A sending transport.** The finding is currently read by a person running `notify` or reading the
  relay's log. Once a notice is actually sent, "delivered without its evidence" becomes a thing that
  happened to a subscriber rather than a line in a console, and may deserve to block rather than
  report — which would reverse D2 and is exactly the decision D2 declines to pre-empt.
