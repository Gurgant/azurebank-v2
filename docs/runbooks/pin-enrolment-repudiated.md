# A subscriber says "this was not me" about a PIN notice

Two notices reach an account holder: one when a transfer PIN is set for the first time (ADR-0045)
and one when an existing PIN is changed (ADR-0047). Both tell the holder to contact the address or
number the operator put behind `--contact`, quoting a reference. This page is what that contact does
with the reference. It is short because the remedy is short — and it ends with the sentence that
says where the remedy stops.

**Read this first, and read the kind first.** The two kinds mean different things and the remedy is
not equally partial.

- **`PinEnrolled`** — whoever set the PIN proved the account PASSWORD (T8). Nulling the PIN removes
  the credential they minted; it does not remove the credential they used. No password reset exists
  in this system, so the account is not safe again until one does. Say that to the subscriber rather
  than implying otherwise.
- **`PinChanged`** — whoever changed it proved only the PIN THAT WAS ALREADY THERE (ADR-0040). The
  password was not used. Nulling the PIN forces re-enrolment, which costs the password — so against
  this attacker the remedy is not partial in the same way, and the subscriber can be told so. If the
  subscriber also says they never set the first PIN, look for a `PinEnrolled` notice as well and
  treat that one by the rule above.

## 1. Find the notice and the event it belongs to

The reference is the notice id, 32 hex digits. Run against the API's database:

    SELECT n.Id, n.UserId, n.Event, n.OccurredAt, n.DeliveredAt, n.DeliveryReceipt
    FROM SubscriberNotices n
    WHERE n.Id = '<reference>';

`Event` is which of the two kinds this is, and it decides how the paragraphs above and §4 read.
`OccurredAt` is when the PIN was set or changed (UTC). `DeliveredAt` is when `notify` rendered it;
if
it is NULL the subscriber cannot be holding a rendered notice for this reference, and the reference
came from somewhere else — treat that as its own finding.

The audit row is joined by (actor, event), never by time — the two are written in one transaction
but read two clocks:

    SELECT e.Sequence, e.OccurredAt, e.Outcome, e.Detail
    FROM AuditEvents e
    WHERE e.ActorUserId = '<UserId from above>' AND e.Event = '<Event from above>';

Use the notice's own `Event` in that WHERE clause. Filtering on `PinEnrolled` for a change reference
returns zero rows, which reads exactly like the finding below and is not one.

For `PinEnrolled`, one row per enrolment — normally exactly one, and more only where §2 below has
been run and the subscriber has since re-enrolled; the trail keeps both. For
`PinChanged` there is one row per change, and several is not a fault — it is the account holder's
PIN being replaced repeatedly, which is what an attacker holding a PIN does. `Detail` names what was
proved: `{"passwordProved":true}` for an enrolment, `{"currentPinProved":true}` for a change.

Zero rows with a notice present is the `NO AUDIT ROW` finding `notify` prints; run `verify` before
going further — the question has become "is the record intact", not "was this the subscriber". Do
NOT reach for `evidence`: it reads by a transfer's `TXN-…` number and a PIN event has none. The
query above, by `ActorUserId`, is the whole of what can be looked up here.

One limit to know before trusting that finding for a change. `notify` asks only whether a row of
that kind EXISTS for the user, and a change can happen many times — so where several `PinChanged`
notices are owed, one surviving audit row answers for all of them and a missing one raises nothing.
Count the rows against the notices yourself when the reference is a change (ADR-0047).

## 2. Remove the PIN the subscriber repudiates

There is no endpoint for this; it is one statement, run by hand, and it is the whole of what an
operator can do:

    UPDATE AspNetUsers
    SET PinHash = NULL, PinAccessFailedCount = 0, PinLockoutEnd = NULL
    WHERE Id = '<UserId>';

The account is back where it was before the enrolment: no PIN, so no transfer, withdrawal or
full-number reveal is possible until one is enrolled again — which costs the password (T8). The
subscriber re-enrols when they choose, and that enrolment writes its own notice.

This UPDATE is not audited. Nothing in the API performed it, so no audit row can claim it; write
down who ran it and when, beside the reference, somewhere the database cannot revise.

## 3. Cut the sessions

Refresh tokens are the API's long-lived sessions. `RefreshTokenService.RevokeAllForUserAsync` does
this in code, and nothing exposes it to an operator, so by hand:

    UPDATE RefreshTokens
    SET RevokedAt = SYSUTCDATETIME()
    WHERE UserId = '<UserId>' AND RevokedAt IS NULL;

Access tokens already issued live until they expire; the BFF's own session cache has its own
lifetime. Both are short. Neither is the point.

## 4. Where this stops

**The password — for a `PinEnrolled` repudiation.** Whoever enrolled the PIN proved the account
password. Steps 2 and 3 take back the PIN and the sessions; they do not take back the password, and
there is no reset flow to hand the subscriber. Until one exists, the honest instruction to the
subscriber is that the account cannot be made safe from here, and the honest note in the record is
that the remedy was partial.

**For a `PinChanged` repudiation the same steps go further.** That attacker proved a PIN and not the
password, so removing the PIN takes back everything they held: setting a new one costs the password
they never had. The remedy is only partial here if the subscriber ALSO repudiates the original
enrolment, or if the PIN they lost is one they reuse elsewhere. Say which of the two situations the
record is in rather than reusing the paragraph above.

**The other address.** If the subscriber says the email on the account is not theirs, nothing here
can change it — no endpoint exists — and every future notice goes to the same address. That is a
second finding, and it belongs in the same record.

Delete the pickup directory after the notices in it have been dealt with. It holds addresses in
clear.
