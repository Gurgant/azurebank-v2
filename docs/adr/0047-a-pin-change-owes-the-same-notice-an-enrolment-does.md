# ADR-0047: A PIN change owes the same notice an enrolment does

**Status:** Accepted · **Date:** 2026-09-04 · **Amended:** 2026-09-09 and 2026-09-10 (ADR-0052) ·
**Reverses:** ADR-0045, D8, which declines to notify a PIN change

## Context

Under ADR-0045 one event owes a notice: when a transfer PIN is first bound to an account, the API
writes a `SubscriberNotices` row inside the enrolment's own save. Its D8 lists a PIN change first
among the events that owe none. An enrolment costs the account password; a change costs only the
current PIN (ADR-0040). A change is therefore the one event that someone who watched a PIN being
entered, and never learned the password, has to produce, and without this decision it leaves the
account holder no notice, the audit trail no row and the log one unnamed line. This record adds a
second notice kind and its audit row; it changes no schema, endpoint, status code or client.

## Decision

- **D1: A PIN change writes its own notice, with its own event name**, `SecurityEvents.PinChanged`,
  a second value in the `Event` column `SubscriberNotice` already carries. It needs no migration,
  because the column is `nvarchar(40)` with no allowed-values constraint, the pending query is
  kind-blind and the `UserId` index is not unique.
- **D2: The change writes the audit row its notice is joined to, in the same save**, because
  `notify` prints `NO AUDIT ROW backs notice …` for a notice it cannot match to its evidence, and
  because an added `AuditEvent` is what opens the owned chain transaction: the change rolls back in
  both directions as the enrolment does (ADR-0045, D1). Its detail is `{"currentPinProved":true}`,
  because `{"passwordProved":true}` would be false here.
- **D3: No `SecurityEvent` log line**, because the row is evidence to keep and not an alert to wake
  someone for, as for the money movements of ADR-0044. Logged sites and rows written stay two
  inventories that move independently.
- **D4: No suppression rule: N changes owe N notices**, because each change costs the current PIN
  and a wrong PIN is counted by the lockout that guards every PIN use (ADR-0010), so repeats cannot
  be ground into a flood and are what someone holding a PIN produces. A refused change owes none:
  no `currentPin` or a password in its place is 422 `PIN_REQUIRED`, a wrong one 401 `INVALID_PIN`.
- **D5: Different words, and a stronger remedy.** The change notice says that the previous PIN was
  proved and that the password was not used and has not changed, because "for the first time" and
  "your account password was proved" are both false for a change. Its remedy is to have the PIN
  removed, because setting a new one costs the password, which a PIN-only attacker does not have.

## Rejected

- Rejected: `PinEnrolled` for both events, because the two would be indistinguishable in the
  evidence join, the console line, the runbooks' SQL and, since the renderer selects on that
  string, the message: a notice saying "for the first time" about a replacement is worse than none.
- Rejected: collapsing repeats at render time, one notice per user per window, because it hides
  the signal that matters most, puts a policy in a rendering step that has no state to make it
  with, and would have to survive the relay, which claims the same pending set.
- Rejected: waiting for a relay that delivers, because recording the obligation is worth doing
  without delivery (ADR-0045) and the row is what a relay finds.

## Consequences

- The account holder is owed a notice at either credential event, in words that tell them apart;
  the audit trail carries a row for a credential replacement; `notify` renders both kinds in one
  run and names each. Nothing here sends: delivery is ADR-0048's and stops at a pickup directory.
- The count of `_audit.Record` sites and ADR-0044's inventory of audited events are held by a test.
- A row whose `Event` has no renderer arm stays owed and is named on the console, because a row
  wrongly marked delivered is a notice nobody receives.
- A repeatable kind weakens one check, and ADR-0052 restores it. `notify` asked whether an audit
  row of that kind exists for the user, so one surviving row answered for every change notice.
  A notice now carries the id of its audit row, written in the same save, and the check asks about
  that row. The reference is a plain column and no foreign key: the `AddSubscriberNotices`
  migration adds none on purpose, so that a notice whose evidence is missing is found, not refused.
- Not covered: a notice written before ADR-0052's migration names no row and keeps the weaker
  question, by `(ActorUserId, Event)`.

## Revisit when

- A relay sends: each repeat then has a cost per message, and D4 is measured again.
- A PIN reset or revocation flow exists: D5's remedy names it, where it now asks for a person.
- A change-email endpoint exists (ADR-0045, D2): the old address is owed the notice too.

## Verified by

- `SubscriberNoticePersistenceTests` (D1, D4, a refused change) and `SubscriberNoticeSqlServerTests`
  (D2: `WhenTheChangeAuditRowCannotBeWritten_NeitherTheNewPinNorItsNoticeSurvives`).
- `SecurityEventConstantTests` (the site count) and `NotifyCommandTests` (both kinds, the join).

## Related

ADR-0010, ADR-0040, ADR-0044, ADR-0045, ADR-0048, ADR-0052.
