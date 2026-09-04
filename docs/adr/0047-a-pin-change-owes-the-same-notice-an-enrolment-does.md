# ADR-0047: A PIN change owes the same notice an enrolment does

**Status:** Accepted · **Date:** 2026-09-04 · Reverses ADR-0045's D8, which declined to notify a PIN
change, and fires the trigger that ADR listed for itself. Adds a
second `SubscriberNotice` kind and the audit row it is joined to; changes no schema, no endpoint, no
status code and no client.

## Context

ADR-0045 built one notice: when a transfer PIN is bound to an account for the first time, the API
writes a `SubscriberNotices` row inside the enrolment's own save, and the operator tool's `notify`
verb renders the pending rows into a pickup directory. Its D8 named what did **not** owe a notice,
and a PIN CHANGE was first on that list, for four reasons.

Two of the four were true when written and are still true: the wording of the two standards, and
that a change costs the current PIN. The third — that the change path wrote no audit row — was also
true, and is the condition D2 below ends. The fourth, that a notice here would need a suppression
rule that does not exist, is denied by D4 below. None of them was a reason to stay silent, which is
what measuring showed.

Measured 2026-09-04 through the BFF (`:5000`) in front of the API (`:7215`), Development, against a
throwaway store, **before any of this ADR was implemented**:

```
POST /bff/auth/register                                   -> 201
POST /bff/auth/set-pin  {pin, password}      (enrolment)  -> 200
POST /bff/auth/set-pin  {pin, currentPin}    (change)     -> 200
POST /bff/auth/verify-pin {new pin}          (control)    -> 200

SubscriberNotices : 1 row   (PinEnrolled)
AuditEvents       : 1 row   (PinEnrolled, Succeeded)
```

The control matters: the new PIN verified, so the credential really had been replaced. A PIN could
be changed leaving the account holder no notice, the audit trail no row, and the operator's log
nothing but an unnamed `"User {UserId} set their PIN"` line. The only durable trace of the old PIN's
replacement was the hash that had overwritten it.

That is the asymmetry this decision closes. An enrolment costs the account **password**. A change
costs only the **current PIN** (ADR-0040). So the change is precisely the event an attacker who has
watched a PIN entered — over a shoulder, on a shared screen — and who has never learned the
password, leaves behind. The one event a PIN-only attacker must produce was the one event the system
said nothing about.

## Decision

**D1 — A PIN change writes its own notice, with its own event name.** `SecurityEvents.PinChanged`,
a second value in the `Event` column `SubscriberNotice` already carries. No migration: the column is
`nvarchar(40)` with no allowed-values constraint, the pending query is kind-blind, and the
`UserId` index is not unique, so a second row of a second kind was already legal.

**D2 — And the audit row it is joined to.** Not symmetry, and not a nice-to-have. `notify` matches a
notice to its evidence by `(ActorUserId, Event)` and prints `NO AUDIT ROW backs notice …` when it
cannot, so a change notice without an audit row of the same name would raise that finding on every
run — a permanent false alarm in the runbook's most alarming line. The audit row is also what puts
this save under the OWNED chain transaction, which opens only when an `AuditEvent` is Added; the
change therefore rides the same locking path the enrolment does, and the both-directions rollback
ADR-0045 D1 proved for the enrolment is proved for the change by two SQL-gated tests rather than
inherited by assertion. Remove the `_audit.Record` and the first of them fails on `fault.Fired`,
which is the assertion that says why. Its detail is `{"currentPinProved":true}`, because
`{"passwordProved":true}` would be false here.

**D3 — No `SecurityEvent` log line.** The row is evidence to keep, not an alert to wake someone for.
This follows the money-movement precedent ADR-0044 records: a durable row with no operator alert. It
also leaves the two inventories — logged sites and rows written — moving independently, which is the
state that ADR's guard already expects.

**D4 — No suppression rule, and the repeatable surface is the signal.** D8 named the missing
suppression rule as the blocker. Measured on the running stack: three changes in a row produce three
notices, and three refused attempts produce none.

```
change x3                          -> PinChanged x3, PinEnrolled x1
no currentPin                      -> 422 PIN_REQUIRED     notices unchanged
wrong currentPin                   -> 401 INVALID_PIN      notices unchanged
password on an account with a PIN  -> 422 PIN_REQUIRED     notices unchanged
```

A change cannot be ground into a flood, because each one costs the current PIN and a wrong PIN is
counted by the same lockout that guards every other PIN use (ADR-0010). N changes producing N
notices is therefore information rather than noise: an owner who receives four notices in a minute
is being told something true and urgent. Collapsing them at render time was considered and declined
below.

**D5 — Different words, and a stronger remedy.** The change notice is not the enrolment's text with
a verb swapped. "For the first time" and "your account password was proved in the same request" are
both false for a change, and the second would tell a reader whose PIN was watched that their
password had been used. The change notice says what was proved — the previous PIN — and that the
password was not used and has not changed. Its remedy is also stronger and says so: removing the PIN
forces re-enrolment, which costs the password, and a PIN-only attacker does not have it.

## Alternatives declined

**Reuse `PinEnrolled` for both.** The cheapest edit and the worst. The two kinds would be
indistinguishable in the evidence join, in the operator's console line, in both runbooks' SQL, and —
because the renderer selects on that string — in the message the owner reads. A notice telling
someone a PIN was set "for the first time" when it was replaced is worse than no notice.

**Collapse repeats at render time** (one notice per user per window). It hides the signal that
matters most: repeated changes are what an attacker in possession of a PIN actually does. It also
puts a policy decision inside a rendering step that has no state to make it with, and it would have
to survive the relay, which claims the same pending set.

**Wait for the relay.** The relay is ratified and unbuilt _(as of this ADR's morning; built the
same evening as ADR-0048)_; see `docs/deferred/`. Recording the
obligation is what ADR-0045 decided is worth doing without delivery; a change is owed
that same obligation now, and the row is what a later relay will find.

## Consequences

The account holder is told when either credential event happens, in words that distinguish them. The
audit trail carries a row for a credential replacement it did not carry before. `notify` renders
both kinds in one run and names each. Nothing is delivered — that clause is still open, and this ADR
does not narrow it.

Two counts move with this decision and are enforced by a test rather than by care: the
`_audit.Record` site count, and ADR-0044's "What is wired" inventory, which now reads fourteen
events, eight of them administrative.

An unrenderable kind is still a finding, not a silent success: a row whose `Event` has no renderer
arm stays owed and is named on the console. That branch had no test before this change and has one
now: once a relay exists, a row wrongly marked delivered is a notice nobody will ever receive.

**And one detective control gets weaker, which is the cost of a repeatable kind.** `notify` reports
`NO AUDIT ROW` by asking whether a row of that kind EXISTS for the user, not by matching each notice
to its own. While every kind happened once per account those were the same question. A change can
happen many times, so where several change notices are owed, one surviving audit row answers for all
of them and a missing one raises nothing. Making the check exact needs a per-notice reference on the
row — which ADR-0045 deliberately did not add, so that a notice whose evidence has gone missing is
found rather than refused. That is a schema decision and it is not taken here: the limit is pinned
by a test that fails if anyone closes it without moving this paragraph, named in the repudiation
runbook so an operator counts the rows themselves, and carried in the backlog.

## What would change this

- **A relay that delivers.** Then "notified" means something, the repeatable surface has a cost per
  message, and D4's no-suppression decision is worth re-measuring rather than re-asserting.
- **A PIN reset or revocation flow.** D5's remedy sentence currently asks the reader to contact a
  human; when the flow exists the notice should name it.
- **An exact evidence join.** A per-notice reference from the notice to its audit row would restore
  the `NO AUDIT ROW` finding to what it was when every kind was singular. It is a schema change and
  it reopens ADR-0045's no-foreign-key decision, so it is a decision of its own rather than a fix.
- **A change-email endpoint.** ADR-0045 D2's trigger, unchanged here: the old address would then
  have to be notified too, and this notice's "addressed to the email held on your account" line
  becomes the weaker of two claims.
