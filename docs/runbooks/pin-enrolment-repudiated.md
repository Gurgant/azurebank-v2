# A subscriber says "this was not me" about a PIN notice

An account holder is owed a notice when a transfer PIN is set for the first time (ADR-0045) and
when it is changed (ADR-0047). Nothing sends it: the API's relay, the Function or the `notify`
verb of the operator tool renders it to a file in a pickup directory (ADR-0048, ADR-0051), and a
person passes that file on. The notice tells the holder to contact the address or number it
prints, quoting a reference. This page is what that contact does when the reference arrives,
and it ends where the remedy stops.

**Read the kind first.** The two kinds mean different things, and the remedy is not equally
partial.

- **`PinEnrolled`**: whoever set the PIN proved the account password (ADR-0040). Removing the PIN
  takes back the credential they made, not the one they used. No password reset exists, so the
  account is not safe again until one does: say that to the subscriber.
- **`PinChanged`**: whoever changed it proved only the PIN that was already there, not the
  password. Removing the PIN forces a new enrolment, which costs the password, so against this
  attacker the remedy holds once the open sessions have ended (section 4). If the subscriber
  also says they never set the first PIN, look for a `PinEnrolled` notice as well.

**Running the SQL.** Every statement here runs against the API's database. Pass `-I` to `sqlcmd`:
without it the UPDATEs of sections 2 and 3 fail with Msg 1934 (`QUOTED_IDENTIFIER`), because
`AspNetUsers` and `RefreshTokens` carry filtered indexes
([`engineering-traps.md`](../engineering-traps.md)).

## 1. Find the notice and the event it belongs to

The reference is the notice id as the notice prints it: 32 hex digits, no hyphens. Paste it
exactly as printed. The statement puts in the hyphens a `uniqueidentifier` needs, and stops with
its error message on anything that is not exactly 32 hex digits (a file name that still ends in
`.eml`, a Message-ID in its angle brackets, a digit dropped or doubled):

```sql
DECLARE @ref varchar(100) = '<reference, exactly as printed>';
SET @ref = TRIM(@ref);
IF LEN(@ref) <> 32 OR @ref LIKE '%[^0-9A-Fa-f]%'
    THROW 50000, 'The reference must be exactly the 32 hex digits the notice prints.', 1;
SELECT n.Id, n.UserId, n.Event, n.OccurredAt, n.DeliveredAt, n.DeliveryReceipt, n.AuditEventId
FROM SubscriberNotices n
WHERE n.Id = CONVERT(uniqueidentifier,
    STUFF(STUFF(STUFF(STUFF(@ref, 9, 0, '-'), 14, 0, '-'), 19, 0, '-'), 24, 0, '-'));
```

`Event` is the kind. `OccurredAt` is when the PIN was set or changed (UTC), `DeliveredAt` when a
runner rendered the notice and marked the row. A null `DeliveredAt` usually means that nobody
rendered it, and that a subscriber quoting this reference got it from somewhere else: a finding
of its own. But a runner can render the file and stop before the mark (ADR-0048 D3). Look in the
pickup directory for `<reference>.eml` first: if it is there the subscriber may be holding it,
and the row is what to fix: mark it by hand, setting `DeliveredAt` and `DeliveryReceipt` (the
file name) in one statement, because the database refuses one without the other; or move the
file out for the next sweep to render again.

**`AuditEventId` is not null.** That is the audit row, and it must also be this notice's:

```sql
SELECT e.Id, e.Sequence, e.OccurredAt, e.Outcome, e.Detail, e.ActorUserId, e.Event
FROM AuditEvents e
WHERE e.Id = '<AuditEventId from above>';
```

`notify` prints two failures as one finding, `NO AUDIT ROW`: for a change, "the `PinChanged` row
it names is gone, or is not this notice's". They do not call for the same act:

- **Zero rows:** the trail lost the row. Run `verify`: the question has become whether the record
  is intact, not whether this was the subscriber.
- **A row whose `ActorUserId` or `Event` is not the notice's:** the trail is fine and the notice
  was re-pointed, so somebody wrote `SubscriberNotices`. `verify` comes back clean here, and
  that is no reassurance.

**`AuditEventId` is null.** The notice names no row, so the older, weaker question is all there
is, and `notify` words its finding "no `PinChanged` row exists for that user at all":

```sql
SELECT e.Sequence, e.OccurredAt, e.Outcome, e.Detail
FROM AuditEvents e
WHERE e.ActorUserId = '<UserId from above>' AND e.Event = '<Event from above>';
```

Keep both terms, and take `Event` from the notice: by the actor alone, an enrolment's row answers
for a change. Never match a notice to a row by time: the two are written in one transaction but
read two clocks. `PinEnrolled` has one row per enrolment, more than one only where section 2 was
run and the subscriber enrolled again. `PinChanged` has one row per change, and several is no
fault: replacing the PIN repeatedly is what an attacker holding a PIN does. So one surviving row
answers for every change notice that names none: count the rows against the notices. `Detail`
says what was proved, `{"passwordProved":true}` or `{"currentPinProved":true}`.

A null is not proof that the notice is old. Nulling `AuditEventId` is a write to
`SubscriberNotices`, like a re-point, and every notice written after the
`AddSubscriberNoticeAuditEventId` migration names its row: on a notice that recent, a null is
itself the finding (ADR-0052, D3's limit).

**A re-point onto another row of the same user and the same kind** passes every check above, and
`notify` prints nothing for it (ADR-0052, D3's limit). It leaves two notices naming one audit
row, which nothing legitimate does:

```sql
SELECT AuditEventId, COUNT(*) AS Notices
FROM SubscriberNotices
WHERE AuditEventId IS NOT NULL
GROUP BY AuditEventId
HAVING COUNT(*) > 1;
```

Zero rows is the healthy answer. A row names an audit event that more than one notice claims:
look it up by that id (the second query) and read both notices. This is a sweep of the whole
table, not a check of one notice, so it belongs in a periodic pass too; the filtered index
`IX_SubscriberNotices_AuditEventId` exists for it. It cannot see a re-point onto an audit row
that no notice names, so when a repeated `PinChanged` is disputed, still count the rows against
the notices. `evidence` helps in none of this: it reads by a movement's `TXN-…` number, and a PIN
event has none.

## 2. Remove the PIN the subscriber repudiates

No endpoint does this. It is one statement, run by hand:

```sql
UPDATE AspNetUsers
SET PinHash = NULL, PinAccessFailedCount = 0, PinLockoutEnd = NULL
WHERE Id = '<UserId>';
```

The account is back where it was before the enrolment: no PIN, so no transfer, withdrawal,
account closure or full-number reveal until one is enrolled again, which costs the password and
writes its own notice. The statement is not audited, because nothing in the API performed it:
write down who ran it and when, beside the reference, somewhere the database cannot revise.

## 3. Cut the sessions

Each BFF session holds one refresh token, its grant, and the user's session stamp as it was at
sign-in. `POST /api/auth/logout` revokes every live grant of a user and raises the stamp in one
transaction. By hand it is the same two statements, in one transaction:

```sql
SET XACT_ABORT ON;
BEGIN TRANSACTION;

UPDATE RefreshTokens
SET RevokedAt = SYSUTCDATETIME(), RevokedReason = N'SignOutEverywhere'
WHERE UserId = '<UserId>' AND RevokedAt IS NULL AND ExpiresAt > SYSUTCDATETIME();

UPDATE AspNetUsers
SET SessionStamp = SessionStamp + 1, ConcurrencyStamp = CONVERT(nvarchar(36), NEWID())
WHERE Id = '<UserId>';

COMMIT TRANSACTION;
```

Write the reason: `SignOutEverywhere` is what the API writes for the same act, and a row revoked
for no reason reads as a legacy one (ADR-0057 §4.1). The second statement must report 1 row: 0
means the id matched no user. Keep its new `ConcurrencyStamp`: a PIN change that loaded the user
before the commit writes the whole row back through Identity, which checks only that column, and
would otherwise put the old stamp back.

The BFF reads every signed-in user's stamp every 15 s and refuses each of the user's sessions at
its first request after that read: within about 20 s of the commit for up to 1,000 signed-in
users, and 5 s more for each further 1,000 (ADR-0057 §5.3). While the BFF cannot read the stamp,
a session ends at its next renewal instead, after up to 7.5 minutes of use. An access token
already issued lives its 15 minutes (`Jwt:ExpirationMinutes`), but once its session has ended
only code inside the API's own replica can present it (ADR-0057 §5.1).

## 4. Where this stops

**`PinEnrolled`: the password.** Sections 2 and 3 take back the PIN and the sessions, not the
password the enrolment proved, and that attacker can sign in again. No password reset exists:
tell the subscriber that the account cannot be made safe from here, and write in the record that
the remedy was partial.

**`PinChanged`: the session still open.** A session is all this attacker holds after section 2,
and section 2 revokes no token. Until the session ends (section 3) it still reads the account,
its transactions and `/api/auth/me`, can deposit, and can reveal the full number if it was
elevated before section 2 was run; a transfer authorisation already minted under the old PIN can
be spent inside its two-minute window. Plan the clock on the 15 minutes of an access token, the
shorter cases being a bonus: treat the account as contained only after they have passed, and
write down when section 2 was run. After that the remedy is complete for this attacker, unless
the subscriber also repudiates the original enrolment or reuses the lost PIN elsewhere. Say in
the record which of the three it is.

**The other address.** If the subscriber says the email on the account is not theirs, nothing here
can change it: no endpoint exists, and every future notice goes to that address. That is a
second finding for the same record.

After the notices in it have been dealt with, delete the pickup directory: it holds addresses in
clear.
