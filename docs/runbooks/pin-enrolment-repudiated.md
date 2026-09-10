# A subscriber says "this was not me" about a PIN notice

Two kinds of notice are owed to an account holder: one when a transfer PIN is set for the first
time (ADR-0045) and one when an existing PIN is changed (ADR-0047). Nothing in the system SENDS
either. Three things can render an owed notice to a file in a pickup directory, and they are not
chosen the same way. Two are HOSTED and `Notices:Runner` names which — the API's relay (ADR-0048) or
the Function (ADR-0051); the host the flag does not name renders nothing however live it is, and the
host it names renders nothing unless it is running. The third is the operator's `notify`, which that
flag does not gate at all: it renders whenever a person runs it, `Notices:Runner=None` included, and
what keeps it off a live host's rows is the lease it takes (ADR-0048 D5). Getting the file to the
holder is an operator's step whichever wrote it. Once a notice has been passed on, it tells the holder to
contact the address or number the operator put behind `--contact`, quoting a
reference. This page is what that contact does with the reference. It is short because the remedy
is short — and it ends with the sentence that says where the remedy stops.

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

The reference is the notice id as the notice prints it: 32 hex digits, no hyphens.
`SubscriberNotices.Id` is a `uniqueidentifier`, and SQL Server does not convert that form — pasted
bare into a comparison it fails with `Msg 8169, Conversion failed` (measured 2026-09-04). The
statement below inserts the hyphens itself, so paste the reference exactly as printed. It also
checks the paste before looking anything up: anything that is not exactly 32 hex digits — a
filename with `.eml` still attached, a Message-ID with its angle brackets, a digit dropped or
doubled — stops with the error message below instead of matching. That check is there because a
`char(32)` variable would silently keep the first 32 characters of a longer paste and look up
whatever they happened to spell. Run against the API's database:

    DECLARE @ref varchar(100) = '<reference, exactly as printed>';
    SET @ref = TRIM(@ref);
    IF LEN(@ref) <> 32 OR @ref LIKE '%[^0-9A-Fa-f]%'
        THROW 50000, 'The reference must be exactly the 32 hex digits the notice prints.', 1;
    SELECT n.Id, n.UserId, n.Event, n.OccurredAt, n.DeliveredAt, n.DeliveryReceipt, n.AuditEventId
    FROM SubscriberNotices n
    WHERE n.Id = CONVERT(uniqueidentifier,
        STUFF(STUFF(STUFF(STUFF(@ref, 9, 0, '-'), 14, 0, '-'), 19, 0, '-'), 24, 0, '-'));

`Event` is which of the two kinds this is, and it decides how the paragraphs above and §4 read.
`OccurredAt` is when the PIN was set or changed (UTC). `DeliveredAt` is when a runner — the relay
or `notify` — rendered it and marked the row. NULL has two readings. Usually nobody has rendered it,
and a subscriber quoting this reference got it from somewhere else — its own finding. But a runner
can render the file and stop before the mark (ADR-0048 D3): then the file sits in the pickup
directory, a person may already have moved it on, and the row still reads owed. Look in the
directory for `<reference>.eml` before concluding anything; if it is there, the subscriber may well
be holding it, and the row is the one to fix — mark it by hand, or move the file out and let the
next sweep re-render it.

The audit row is NAMED by the notice since ADR-0052. ⚠️ **The pair `(actor, event)` is used in
BOTH arms and does a different job in each** — it is the LOOKUP KEY when the notice names no row,
and a CHECK on the row it does name, which is what makes a re-pointed notice findable. It is never
joined by time, because the two rows are written in one transaction but read two clocks.

**`AuditEventId` is not null.** That is the row, and it must also be this notice's:

    SELECT e.Id, e.Sequence, e.OccurredAt, e.Outcome, e.Detail, e.ActorUserId, e.Event
    FROM AuditEvents e
    WHERE e.Id = '<AuditEventId from above>';

⚠️ **Zero rows and a mismatch are different things, and `notify` prints them as one finding** —
*"gone, or is not this notice's"* — because it cannot tell you which without this query. Zero rows
means the audit row is gone: the TRAIL lost it, so run `verify`. A row whose `ActorUserId` or
`Event` does not match the notice's means the row is fine and the NOTICE was re-pointed: somebody
wrote the `SubscriberNotices` table, and the trail is not where to look.

**`AuditEventId` is null.** The notice predates ADR-0052 and names nothing, so the older question is
all there is:

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

⚠️ **`NO AUDIT ROW` is not "the query came back empty".** A notice that names a row can have that
row sitting THERE and mismatched, which is what the annotation on the query above is for. The finding
is one line over both outcomes and they do not call for the same act. These are how you tell:

- **A NAMED row that is gone.** The trail lost it: run `verify`, because the question has become
  "is the record intact", not "was this the subscriber".
- **A NAMED row that is THERE and whose `ActorUserId` or `Event` does not match the notice.** The
  trail is fine and the notice was re-pointed — somebody wrote `SubscriberNotices`. ⚠️ `verify` will
  come back CLEAN here, and a session that ran it first reads that as reassurance; it is the tell.
- **No row at all, for a notice that names none.** The older question. Those rows carry no
  `AuditEventId`, so the pair `(ActorUserId, Event)` is the whole of what can be looked up —
  ⚠️ **both terms, never the actor alone.** The query above uses both, and dropping `Event` hands
  an operator an ENROLMENT's row as a change's evidence, which is the one mistake this page's own
  warning about `PinEnrolled` already tells you not to make.

⚠️ **And one re-point none of the three can show you — but this query can.** If the named row was
swapped for ANOTHER row of the same user and the same kind, every check above passes and no finding
appears at all: the pair the verb compares is satisfied by any `PinChanged` row that user has.
ADR-0052 records why that is pinned rather than closed. **It is not invisible to YOU, though.** The
swap has to leave the notice pointing somewhere, and the row it now points at already belongs to
another notice — so two notices name one audit row, and nothing legitimate does that:

    SELECT AuditEventId, COUNT(*) AS Notices
    FROM SubscriberNotices
    WHERE AuditEventId IS NOT NULL
    GROUP BY AuditEventId
    HAVING COUNT(*) > 1;

**Zero rows is the healthy answer**, and it is the answer on a clean database — measured against
`AzureBankDev` on 2026-09-10, which returned the header and no rows. Run it that way once yourself
so you know what clean looks like. (The filtered index it scans was checked at the same time:
`sys.indexes` reports `IX_SubscriberNotices_AuditEventId` with `filter_definition = ([AuditEventId]
IS NOT NULL)` and `has_filter = 1`.) Any row it returns names an audit event that more than one
notice claims as its evidence: take the `AuditEventId` back to the query in §1 and read both
notices. ⚠️ **This is a SWEEP, not a per-notice check** — it is the one question here you ask of the
whole table rather than of the notice in front of you, so it belongs in a periodic pass as much as
in an investigation. The filtered index on `AuditEventId` exists for exactly this scan and for
nothing else; `SubscriberNoticeConfiguration` says so where somebody wondering about the index will
read it.

⚠️ **What it cannot see**: a re-point onto a row that NO other notice names — an audit row belonging
to no notice at all. Those exist (an `AuditEvents` row is written for events that owe no notice), so
a zero here narrows the question, it does not close it. **When a repeated `PinChanged` is what is
under dispute, still count the rows against the notices by hand** — on the `(ActorUserId, Event)`
pair, exactly as you would for a notice that names none.

Do NOT reach for `evidence` in any of these, the same-kind re-point included: it reads by a
transfer's `TXN-…` number and a PIN event has none.

**Since ADR-0052 the finding is exact, and the line tells you which question it asked.** A notice
written from then on names the audit row it belongs to, so `NO AUDIT ROW` is about THAT row rather
than about the user's history — the line reads **"the `PinChanged` row it names is gone, or is not
this notice's"**. ⚠️ It does not say which of the two states above it found: the FIRST TWO bullets
are how you tell, and they do not call for the same act.

⚠️ **A notice written BEFORE that migration names no row and gets the older, weaker question**, and
the line says so: **"no `PinChanged` row exists for that user at all"**. A change can happen many
times, so for those rows one surviving audit row still answers for all of them and a missing one
still raises nothing. **When you see the weaker wording, count the rows against the notices
yourself** — the query above is how, and it takes BOTH `ActorUserId` and `Event`. ⚠️ **Counting by
actor alone is the mistake this page warns about twice already**: it lets a `PinEnrolled` row answer
for a `PinChanged` notice, which is the one comparison that must never pass.

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

Access tokens already issued live until they expire — 15 minutes (`Jwt:ExpirationMinutes`) — and the
BFF's own session has its own windows (30 minutes idle, 60 absolute; 10 and 20 in Development).
Whether that matters depends on which kind you are treating. For a `PinEnrolled` repudiation it does
not: that attacker proved the password and can sign in again, so the window is not the point. For a
`PinChanged` repudiation it IS the point — a session is the only thing that attacker still holds
after step 2 — and §4 says what it reaches until it expires.

## 4. Where this stops

**The password — for a `PinEnrolled` repudiation.** Whoever enrolled the PIN proved the account
password. Steps 2 and 3 take back the PIN and the sessions; they do not take back the password, and
there is no reset flow to hand the subscriber. Until one exists, the honest instruction to the
subscriber is that the account cannot be made safe from here, and the honest note in the record is
that the remedy was partial.

**For a `PinChanged` repudiation steps 2 and 3 go further, but not immediately.** That attacker
proved a PIN and not the password, so clearing `PinHash` takes back the credential they used and
setting a new one costs the password they never had. What it does NOT take back is the access token
they are already holding. Clearing the PIN revokes no token, and step 3 revokes REFRESH tokens only,
so until that access token dies the session still reads the account, its transactions and
`/api/auth/me`, can deposit, and can reveal the full number if the session was elevated before you
acted. A transfer needs an authorisation that was minted with the OLD PIN, so one already minted and
unspent can still be presented inside its own two-minute window.

How long that is depends on what the attacker holds. A bearer token presented to the API directly
lives its full 15 minutes — and a direct caller is who the change path is reachable by, since the
client turns away anyone who already has a PIN. A BFF session ends sooner: inside the token's last
60 seconds the BFF tries to refresh it, the API refuses the refresh token step 3 revoked, and the
BFF drops the session there; `/bff/auth/me` may still answer from what it cached, but nothing more
reaches the API. **Plan the clock on the 15 minutes** — the shorter case is a bonus, not the bound —
treat the account as contained only after it has passed, and note the time you ran step 2 so the
record shows when it closed.

After it, the remedy is complete for this attacker unless the subscriber ALSO repudiates the
original enrolment, or the PIN they lost is one they reuse elsewhere. Say which of the three
situations the record is in rather than reusing the paragraph above.

**The other address.** If the subscriber says the email on the account is not theirs, nothing here
can change it — no endpoint exists — and every future notice goes to the same address. That is a
second finding, and it belongs in the same record.

Delete the pickup directory after the notices in it have been dealt with. It holds addresses in
clear.
